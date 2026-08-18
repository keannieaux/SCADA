using SCADA.Core.Alarms;
using SCADA.Core.Devices;
using SCADA.Core.Tags;

namespace SCADA.Alarms.Tests;

/// <summary>
/// Восстановление состояния движка из журнала при рестарте (§7.3):
/// RestoreRecovered принимает состояния без записи событий, EvaluateAll
/// сводит их с фактическими значениями тегов.
/// </summary>
public class AlarmEngineRecoveryTests
{
    private const long T0 = 1_000_000;

    private sealed class StubTagTable : ITagTable
    {
        private readonly Dictionary<int, TagValue> _values = new();
        public void Set(int index, double value, Quality quality = Quality.Good)
            => _values[index] = new TagValue(value, 0, quality);
        public TagValue Read(TagId id)
            => _values.TryGetValue(id.Value, out var v) ? v : new TagValue(0, 0, Quality.Bad);
        public void Write(TagId id, TagValue value) => _values[id.Value] = value;
        public long CurrentEpoch => 0;
        public int GetChangedSince(long epoch, Span<TagId> destination) => 0;
    }

    private readonly StubTagTable _tags = new();
    private readonly List<TagDefinition> _tagDefs = new()
    {
        new TagDefinition { Id = new TagId(0), Name = "Boiler1.Temp",
            DataType = TagDataType.Analog, DeviceId = new DeviceId(0) }
    };

    private AlarmEngine EngineWithHiRule(out AlarmRule rule)
    {
        rule = new AlarmRule
        {
            Name = "R1",
            Type = AlarmType.Threshold,
            TagName = "Boiler1.Temp",
            Limits = [new ThresholdLimit { Kind = ThresholdKind.Hi, Value = 80 }],
            Description = "Температура котла"
        };
        return new AlarmEngine(new AlarmConfiguration { Rules = [rule] },
            [new PreparedAlarmRule { Rule = rule, TagIndices = [0] }], _tags, _tagDefs);
    }

    [Fact]
    public void RestoreRecovered_ActiveAlarm_AppearsInGetActive()
    {
        var engine = EngineWithHiRule(out _);
        _tags.Set(0, 85);

        engine.RestoreRecovered(new[]
        {
            new RecoveredAlarmState("R1", ThresholdKind.Hi, AlarmState.ActiveUnack, T0, null)
        });

        var active = Assert.Single(engine.GetActive(new AlarmFilter()));
        Assert.Equal(AlarmState.ActiveUnack, active.State);
        Assert.Equal(T0, active.ActivatedAtUtcMs);
        Assert.True(engine.IsActive("R1"));
    }

    [Fact]
    public void EvaluateAll_ValueReturnedToNormalDuringDowntime_FiresNormal()
    {
        var engine = EngineWithHiRule(out _);
        _tags.Set(0, 50); // за простой значение ушло в норму

        engine.RestoreRecovered(new[]
        {
            new RecoveredAlarmState("R1", ThresholdKind.Hi, AlarmState.ActiveUnack, T0, null)
        });
        var events = engine.EvaluateAll(T0 + 5000);

        var ev = Assert.Single(events);
        Assert.Equal(AlarmEventType.Normal, ev.Type);
        // RequiresAck по умолчанию true → авария ждёт квитирования
        Assert.Equal(AlarmState.RtnUnack,
            Assert.Single(engine.GetActive(new AlarmFilter())).State);
    }

    [Fact]
    public void EvaluateAll_ValueStillActive_KeepsStateWithoutNewEvents()
    {
        var engine = EngineWithHiRule(out _);
        _tags.Set(0, 85); // условие по-прежнему истинно

        engine.RestoreRecovered(new[]
        {
            new RecoveredAlarmState("R1", ThresholdKind.Hi, AlarmState.ActiveAck, T0, "op@ARM1")
        });
        var events = engine.EvaluateAll(T0 + 5000);

        Assert.Empty(events); // повторный Active не пишется — фронт уже был
        var active = Assert.Single(engine.GetActive(new AlarmFilter()));
        Assert.Equal(AlarmState.ActiveAck, active.State);
        Assert.Equal("op@ARM1", active.AcknowledgedBy);
    }

    [Fact]
    public void RestoreRecovered_RuleRemovedFromConfig_IsIgnored()
    {
        var engine = EngineWithHiRule(out _);

        engine.RestoreRecovered(new[]
        {
            new RecoveredAlarmState("DELETED_RULE", null, AlarmState.ActiveUnack, T0, null)
        });

        Assert.Empty(engine.GetActive(new AlarmFilter()));
    }
}
