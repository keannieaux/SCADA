using SCADA.Core.Alarms;
using SCADA.Core.Tags;

namespace SCADA.Alarms.Tests;

/// <summary>Журнал SQLite: запись, фильтры, retention (docs/M5-plan.md §8).</summary>
public class SqliteEventJournalTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    private readonly SqliteEventJournal _journal;

    public SqliteEventJournalTests()
    {
        Directory.CreateDirectory(_dir);
        _journal = new SqliteEventJournal(Path.Combine(_dir, "events.db"));
    }

    public void Dispose()
    {
        _journal.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private static AlarmEvent Event(long ts, string rule = "R1",
        AlarmEventType type = AlarmEventType.Active,
        AlarmSeverity severity = AlarmSeverity.High, string area = "Котельная",
        ThresholdKind? limit = null, string message = "msg",
        IReadOnlyList<AlarmTagSnapshot>? snapshots = null,
        string? ackBy = null, string? ackComment = null, long? ackAt = null) =>
        new(default, ts, rule, limit, type, message, severity, area,
            snapshots ?? Array.Empty<AlarmTagSnapshot>(), ackBy, ackComment, ackAt);

    [Fact]
    public void Append_AssignsIds_AndQueryReadsBackAllFields()
    {
        var snapshots = new AlarmTagSnapshot[]
        {
            new(new TagId(0), "Boiler1.Temp", 95.5, Quality.Good),
            new(new TagId(1), "Pump1.Running", null, Quality.Bad)
        };
        var ids = _journal.Append(new[]
        {
            Event(1000, limit: ThresholdKind.HiHi, snapshots: snapshots),
            Event(2000, type: AlarmEventType.Acknowledged,
                ackBy: "op@ARM1", ackComment: "принято", ackAt: 2000)
        });

        Assert.Equal(2, ids.Count);
        Assert.True(ids[1].Value > ids[0].Value);

        var read = _journal.Query(new AlarmHistoryQuery(0, long.MaxValue));
        Assert.Equal(2, read.Count);

        var active = read.Single(e => e.Type == AlarmEventType.Active);
        Assert.Equal(ids[0], active.Id);
        Assert.Equal(1000, active.TimestampUtcMs);
        Assert.Equal(ThresholdKind.HiHi, active.Limit);
        Assert.Equal(AlarmSeverity.High, active.Severity);
        Assert.Equal("Котельная", active.Area);
        Assert.Equal(2, active.TagSnapshots.Count);
        Assert.Equal("Boiler1.Temp", active.TagSnapshots[0].TagName);
        Assert.Equal(95.5, active.TagSnapshots[0].Value);
        Assert.Equal(Quality.Good, active.TagSnapshots[0].Quality);
        Assert.Null(active.TagSnapshots[1].Value);
        Assert.Equal(Quality.Bad, active.TagSnapshots[1].Quality);

        var ack = read.Single(e => e.Type == AlarmEventType.Acknowledged);
        Assert.Equal("op@ARM1", ack.AcknowledgedBy);
        Assert.Equal("принято", ack.AckComment);
        Assert.Equal(2000, ack.AcknowledgedAtUtcMs);
        Assert.Empty(ack.TagSnapshots); // снимки только для Active (§2.11)
    }

    [Fact]
    public void Query_FiltersByTimeSeverityAreaRule()
    {
        _journal.Append(new[]
        {
            Event(1000, severity: AlarmSeverity.Warning, area: "А"),
            Event(2000, severity: AlarmSeverity.Critical, area: "Б", rule: "R2"),
            Event(3000, severity: AlarmSeverity.Critical, area: "А")
        });

        Assert.Single(_journal.Query(new AlarmHistoryQuery(1500, 2500)));
        Assert.Single(_journal.Query(
            new AlarmHistoryQuery(0, long.MaxValue, Severity: AlarmSeverity.Warning)));
        Assert.Equal(2, _journal.Query(
            new AlarmHistoryQuery(0, long.MaxValue, Severity: AlarmSeverity.Critical)).Count);
        Assert.Single(_journal.Query(
            new AlarmHistoryQuery(0, long.MaxValue, Area: "Б")));
        Assert.Single(_journal.Query(
            new AlarmHistoryQuery(0, long.MaxValue, RuleName: "R2")));

        // порядок — новые первыми
        var all = _journal.Query(new AlarmHistoryQuery(0, long.MaxValue));
        Assert.Equal(3000, all[0].TimestampUtcMs);
    }

    [Fact]
    public void Query_RespectsLimit()
    {
        _journal.Append(Enumerable.Range(0, 10)
            .Select(i => Event(i * 1000L)).ToArray());

        var read = _journal.Query(new AlarmHistoryQuery(0, long.MaxValue, Limit: 3));

        Assert.Equal(3, read.Count);
        Assert.Equal(9000, read[0].TimestampUtcMs);
    }

    [Fact]
    public void DeleteOlderThan_RemovesOnlyOld()
    {
        _journal.Append(new[] { Event(1000), Event(2000), Event(3000) });

        int deleted = _journal.DeleteOlderThan(2500);

        Assert.Equal(2, deleted);
        var rest = _journal.Query(new AlarmHistoryQuery(0, long.MaxValue));
        Assert.Single(rest);
        Assert.Equal(3000, rest[0].TimestampUtcMs);
    }

    [Fact]
    public void ReadRecentDesc_ReturnsNewestFirst()
    {
        _journal.Append(new[] { Event(1000, rule: "R1"), Event(2000, rule: "R2") });

        var recent = _journal.ReadRecentDesc(10);

        Assert.Equal(2, recent.Count);
        Assert.Equal("R2", recent[0].RuleName);
    }

    // --- восстановление состояния из хвоста журнала (§7.3) ---

    [Fact]
    public void Recovery_ActiveWithoutNormal_IsActiveUnack()
    {
        var states = AlarmStateRecovery.Resolve(new[] { Event(1000) });

        var s = Assert.Single(states);
        Assert.Equal(AlarmState.ActiveUnack, s.State);
        Assert.Equal(1000, s.ActivatedAtUtcMs);
    }

    [Fact]
    public void Recovery_ActiveThenAck_IsActiveAck()
    {
        var states = AlarmStateRecovery.Resolve(new[]
        {
            Event(2000, type: AlarmEventType.Acknowledged, ackBy: "op@ARM1"),
            Event(1000)
        });

        var s = Assert.Single(states);
        Assert.Equal(AlarmState.ActiveAck, s.State);
        Assert.Equal("op@ARM1", s.AcknowledgedBy);
    }

    [Fact]
    public void Recovery_ActiveThenNormal_IsRtnUnack()
    {
        var states = AlarmStateRecovery.Resolve(new[]
        {
            Event(2000, type: AlarmEventType.Normal),
            Event(1000)
        });

        Assert.Equal(AlarmState.RtnUnack, Assert.Single(states).State);
    }

    [Fact]
    public void Recovery_FullCycle_IsClosed()
    {
        // Active → Normal → Acknowledged: авария закрыта, восстанавливать нечего
        Assert.Empty(AlarmStateRecovery.Resolve(new[]
        {
            Event(3000, type: AlarmEventType.Acknowledged, ackBy: "op@ARM1"),
            Event(2000, type: AlarmEventType.Normal),
            Event(1000)
        }));

        // Active → Ack → Normal: тоже закрыта
        Assert.Empty(AlarmStateRecovery.Resolve(new[]
        {
            Event(3000, type: AlarmEventType.Normal),
            Event(2000, type: AlarmEventType.Acknowledged, ackBy: "op@ARM1"),
            Event(1000)
        }));
    }

    [Fact]
    public void Recovery_NewCycleHidesOldHistory()
    {
        // R1 пережил полный цикл и сработал снова — восстанавливается по новому циклу
        var states = AlarmStateRecovery.Resolve(new[]
        {
            Event(4000), // новый цикл — активна
            Event(3000, type: AlarmEventType.Acknowledged),
            Event(2000, type: AlarmEventType.Normal),
            Event(1000)
        });

        var s = Assert.Single(states);
        Assert.Equal(AlarmState.ActiveUnack, s.State);
        Assert.Equal(4000, s.ActivatedAtUtcMs);
    }

    [Fact]
    public void Recovery_EscalationKeepsSameAlarm()
    {
        // Active(Hi) → Escalated(HiHi): одна авария, восстанавливается одна,
        // со старшей уставкой
        var states = AlarmStateRecovery.Resolve(new[]
        {
            Event(2000, type: AlarmEventType.Escalated, limit: ThresholdKind.HiHi),
            Event(1000, limit: ThresholdKind.Hi)
        });

        var s = Assert.Single(states);
        Assert.Equal(AlarmState.ActiveUnack, s.State);
        Assert.Equal(ThresholdKind.HiHi, s.Limit);
        Assert.Equal(1000, s.ActivatedAtUtcMs); // активация — от первого фронта
    }
}
