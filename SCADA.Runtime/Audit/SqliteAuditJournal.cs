using Microsoft.Data.Sqlite;

namespace SCADA.Runtime.Audit;

/// <summary>
/// SQLite-реализация журнала аудита (ТЗ §13). Таблица Audit в том же
/// events.db, что и журнал аварий (WAL допускает несколько соединений).
/// Своя ошибка записи не роняет службу: сообщение в onError, запись
/// теряется, запись в устройство при этом уже состоялась — операция
/// не откатывается из-за журнала.
/// </summary>
public sealed class SqliteAuditJournal : IAuditJournal, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly Action<string>? _onError;

    public SqliteAuditJournal(string path, Action<string>? onError = null)
    {
        _onError = onError;
        // пулинг не нужен, как у журнала аварий: одно соединение на весь срок
        _connection = new SqliteConnection($"Data Source={path};Pooling=false");
        _connection.Open();
        InitializeSchema();
    }

    private void InitializeSchema()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS Audit (
                Id INTEGER PRIMARY KEY,
                TimestampUtcMs INTEGER NOT NULL,
                UserName TEXT NOT NULL,
                Action TEXT NOT NULL,
                Target TEXT NOT NULL,
                OldValue REAL,
                NewValue REAL,
                Result TEXT NOT NULL,
                Detail TEXT,
                BatchId TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_audit_time ON Audit(TimestampUtcMs);
            CREATE INDEX IF NOT EXISTS idx_audit_target ON Audit(Target, TimestampUtcMs);
            """;
        cmd.ExecuteNonQuery();
    }

    public void Append(IReadOnlyList<AuditEntry> entries)
    {
        if (entries.Count == 0)
            return;

        try
        {
            using var transaction = _connection.BeginTransaction();
            foreach (var entry in entries)
            {
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = """
                    INSERT INTO Audit
                        (TimestampUtcMs, UserName, Action, Target,
                         OldValue, NewValue, Result, Detail, BatchId)
                    VALUES
                        ($ts, $user, $action, $target, $old, $new, $result, $detail, $batch);
                    """;
                cmd.Parameters.AddWithValue("$ts", entry.TimestampUtcMs);
                cmd.Parameters.AddWithValue("$user", entry.User);
                cmd.Parameters.AddWithValue("$action", entry.Action);
                cmd.Parameters.AddWithValue("$target", entry.Target);
                cmd.Parameters.AddWithValue("$old",
                    entry.OldValue is { } o ? o : DBNull.Value);
                cmd.Parameters.AddWithValue("$new",
                    entry.NewValue is { } n ? n : DBNull.Value);
                cmd.Parameters.AddWithValue("$result", entry.Result);
                cmd.Parameters.AddWithValue("$detail",
                    entry.Detail is not null ? entry.Detail : DBNull.Value);
                cmd.Parameters.AddWithValue("$batch",
                    entry.BatchId is not null ? entry.BatchId : DBNull.Value);
                cmd.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        catch (SqliteException ex)
        {
            _onError?.Invoke($"[аудит] ошибка записи, {entries.Count} записей потеряно: {ex.Message}");
        }
    }

    public void Dispose() => _connection.Dispose();
}
