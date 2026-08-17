using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using SCADA.Core.Alarms;
using SCADA.Core.Tags;

namespace SCADA.Alarms;

/// <summary>
/// SQLite-реализация журнала (docs/M5-plan.md §8). Файл events.db в папке
/// проекта (ТЗ §14.6), WAL, incremental vacuum. Снимки тегов — JSON.
/// Ошибки записи не роняют службу: сообщение уходит в onError, событие
/// теряется, опрос и HMI продолжают работу (ТЗ §8.9).
/// </summary>
public sealed class SqliteEventJournal : IEventJournal, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly Action<string>? _onError;

    public SqliteEventJournal(string path, Action<string>? onError = null)
    {
        _onError = onError;
        // пулинг не нужен: журнал держит одно соединение на весь срок службы,
        // а пул не отдавал бы файловый дескриптор до ClearAllPools
        _connection = new SqliteConnection($"Data Source={path};Pooling=false");
        _connection.Open();
        InitializeSchema();
    }

    private void InitializeSchema()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA auto_vacuum = INCREMENTAL;
            CREATE TABLE IF NOT EXISTS AlarmEvents (
                Id INTEGER PRIMARY KEY,
                TimestampUtcMs INTEGER NOT NULL,
                RuleName TEXT NOT NULL,
                LimitKind INTEGER,
                EventType INTEGER NOT NULL,
                Severity INTEGER NOT NULL,
                Area TEXT,
                Message TEXT NOT NULL,
                TagSnapshots TEXT,
                AcknowledgedBy TEXT,
                AckComment TEXT,
                AcknowledgedAtUtcMs INTEGER
            );
            CREATE INDEX IF NOT EXISTS idx_alarms_time ON AlarmEvents(TimestampUtcMs);
            CREATE INDEX IF NOT EXISTS idx_alarms_rule ON AlarmEvents(RuleName, TimestampUtcMs);
            CREATE INDEX IF NOT EXISTS idx_alarms_severity ON AlarmEvents(Severity, TimestampUtcMs);
            CREATE INDEX IF NOT EXISTS idx_alarms_area ON AlarmEvents(Area, TimestampUtcMs);
            """;
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<AlarmId> Append(IReadOnlyList<AlarmEvent> events)
    {
        if (events.Count == 0)
            return Array.Empty<AlarmId>();

        try
        {
            using var transaction = _connection.BeginTransaction();
            var ids = new List<AlarmId>(events.Count);
            foreach (var ev in events)
            {
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = """
                    INSERT INTO AlarmEvents
                        (TimestampUtcMs, RuleName, LimitKind, EventType, Severity, Area,
                         Message, TagSnapshots, AcknowledgedBy, AckComment, AcknowledgedAtUtcMs)
                    VALUES
                        ($ts, $rule, $limit, $type, $severity, $area,
                         $message, $snapshots, $ackBy, $ackComment, $ackAt);
                    SELECT last_insert_rowid();
                    """;
                cmd.Parameters.AddWithValue("$ts", ev.TimestampUtcMs);
                cmd.Parameters.AddWithValue("$rule", ev.RuleName);
                cmd.Parameters.AddWithValue("$limit",
                    ev.Limit is { } l ? (int)l : DBNull.Value);
                cmd.Parameters.AddWithValue("$type", (int)ev.Type);
                cmd.Parameters.AddWithValue("$severity", (int)ev.Severity);
                cmd.Parameters.AddWithValue("$area", ev.Area);
                cmd.Parameters.AddWithValue("$message", ev.Message);
                cmd.Parameters.AddWithValue("$snapshots",
                    ev.TagSnapshots.Count > 0
                        ? JsonSerializer.Serialize(ev.TagSnapshots, JournalJsonContext.Default.IReadOnlyListAlarmTagSnapshot)
                        : DBNull.Value);
                cmd.Parameters.AddWithValue("$ackBy",
                    ev.AcknowledgedBy is not null ? ev.AcknowledgedBy : DBNull.Value);
                cmd.Parameters.AddWithValue("$ackComment",
                    ev.AckComment is not null ? ev.AckComment : DBNull.Value);
                cmd.Parameters.AddWithValue("$ackAt",
                    ev.AcknowledgedAtUtcMs is { } at ? at : DBNull.Value);
                ids.Add(new AlarmId((long)cmd.ExecuteScalar()!));
            }
            transaction.Commit();
            return ids;
        }
        catch (SqliteException ex)
        {
            _onError?.Invoke($"[журнал] ошибка записи, {events.Count} событий потеряно: {ex.Message}");
            return Array.Empty<AlarmId>();
        }
    }

    public IReadOnlyList<AlarmEvent> Query(AlarmHistoryQuery query)
    {
        var sql = new System.Text.StringBuilder(
            "SELECT * FROM AlarmEvents WHERE TimestampUtcMs >= $from AND TimestampUtcMs <= $to");
        using var cmd = _connection.CreateCommand();
        cmd.Parameters.AddWithValue("$from", query.FromUtcMs);
        cmd.Parameters.AddWithValue("$to", query.ToUtcMs);

        if (query.Severity is { } severity)
        {
            sql.Append(" AND Severity = $severity");
            cmd.Parameters.AddWithValue("$severity", (int)severity);
        }
        if (query.Area is { } area)
        {
            sql.Append(" AND Area = $area");
            cmd.Parameters.AddWithValue("$area", area);
        }
        if (query.RuleName is { } rule)
        {
            sql.Append(" AND RuleName = $rule");
            cmd.Parameters.AddWithValue("$rule", rule);
        }
        sql.Append(" ORDER BY TimestampUtcMs DESC, Id DESC LIMIT $limit");
        cmd.Parameters.AddWithValue("$limit", query.Limit);

        cmd.CommandText = sql.ToString();
        return ReadEvents(cmd);
    }

    public IReadOnlyList<AlarmEvent> ReadRecentDesc(int limit)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            "SELECT * FROM AlarmEvents ORDER BY TimestampUtcMs DESC, Id DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", limit);
        return ReadEvents(cmd);
    }

    public int DeleteOlderThan(long cutoffUtcMs)
    {
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM AlarmEvents WHERE TimestampUtcMs < $cutoff";
            cmd.Parameters.AddWithValue("$cutoff", cutoffUtcMs);
            int deleted = cmd.ExecuteNonQuery();
            if (deleted > 0)
            {
                using var vacuum = _connection.CreateCommand();
                vacuum.CommandText = "PRAGMA incremental_vacuum";
                vacuum.ExecuteNonQuery();
            }
            return deleted;
        }
        catch (SqliteException ex)
        {
            _onError?.Invoke($"[журнал] ошибка retention-очистки: {ex.Message}");
            return 0;
        }
    }

    private List<AlarmEvent> ReadEvents(SqliteCommand cmd)
    {
        var result = new List<AlarmEvent>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            // порядок колонок — как в CREATE TABLE (InitializeSchema)
            string? snapshotsJson = reader.IsDBNull(8) ? null : reader.GetString(8);
            result.Add(new AlarmEvent(
                Id: new AlarmId(reader.GetInt64(0)),
                TimestampUtcMs: reader.GetInt64(1),
                RuleName: reader.GetString(2),
                Limit: reader.IsDBNull(3) ? null : (ThresholdKind)reader.GetInt32(3),
                Type: (AlarmEventType)reader.GetInt32(4),
                Severity: (AlarmSeverity)reader.GetInt32(5),
                Area: reader.IsDBNull(6) ? "" : reader.GetString(6),
                Message: reader.GetString(7),
                TagSnapshots: snapshotsJson is null
                    ? Array.Empty<AlarmTagSnapshot>()
                    : JsonSerializer.Deserialize(snapshotsJson,
                        JournalJsonContext.Default.IReadOnlyListAlarmTagSnapshot)
                      ?? Array.Empty<AlarmTagSnapshot>(),
                AcknowledgedBy: reader.IsDBNull(9) ? null : reader.GetString(9),
                AckComment: reader.IsDBNull(10) ? null : reader.GetString(10),
                AcknowledgedAtUtcMs: reader.IsDBNull(11) ? null : reader.GetInt64(11)));
        }
        return result;
    }

    public void Dispose() => _connection.Dispose();
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(IReadOnlyList<AlarmTagSnapshot>))]
[JsonSerializable(typeof(AlarmTagSnapshot))]
[JsonSerializable(typeof(TagId))]
[JsonSerializable(typeof(Quality))]
internal partial class JournalJsonContext : JsonSerializerContext
{
}
