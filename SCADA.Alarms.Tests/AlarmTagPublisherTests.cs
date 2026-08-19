using SCADA.Core.Alarms;
using SCADA.Core.Devices;
using SCADA.Core.Tags;

namespace SCADA.Alarms.Tests;

/// <summary>
/// Публикация состояния аварий системными тегами (концепт §10): правило,
/// многоуровневые группы по dotted-имени, корень @AlarmSystem, квитирование,
/// эскалация, заморозка по качеству (M5-план §2.10), запись только изменений.
/// </summary>
public class AlarmTagPublisherTests
{
    private const long T0 = 1_000_000;

    private sealed class CountingTagTable : ITagTable
    {
        private readonly Dictionary<int, TagValue> _values = new();

        public int Writes { get; private set; }

        public void Set(int index, double value, Quality quality = Quality.Good)
            => _values[index] = new TagValue(value, 0, quality);

        public TagValue Read(TagId id)
            => _values.TryGetValue(id.Value, out var v) ? v : new TagValue(0, 0, Quality.Bad);

        public void Write(TagId id, TagValue value)
        {
            _values[id.Value] = value;
            Writes++;
        }

        public long CurrentEpoch => 0;
        public int GetChangedSince(long epoch, Span<TagId> destination) => 0;
        public void WriteString(TagId id, StringTagValue value) { }
        public StringTagValue ReadString(TagId id) => StringTagValue.Empty;
    }

    // автораздача TagId по имени — имитация каталога сгенерированных тегов
    private sealed class FakeCatalog
    {
        private readonly Dictionary<string, TagId> _ids = new();
        private int _next = 100;

        public TagId? Resolve(string name)
        {
            if (!_ids.TryGetValue(name, out var id))
                _ids[name] = id = new TagId(_next++);
            return id;
        }

        public TagId Id(string name) =>
            _ids.TryGetValue(name, out var id) ? id : throw new KeyNotFoundException(name);
    }

    private readonly CountingTagTable _tags = new();
    private readonly FakeCatalog _catalog = new();

    private readonly List<TagDefinition> _tagDefs =
    [
        new TagDefinition { Id = new TagId(0), Name = "Boiler1.Temp",
            DataType = TagDataType.Analog, DeviceId = new DeviceId(0) }
    ];

    private AlarmEngine EngineWithPublisher(params AlarmRule[] rules)
    {
        var publisher = new AlarmTagPublisher(_tags, rules.Select(r => r.Name),
            _catalog.Resolve);
        return new AlarmEngine(new AlarmConfiguration { Rules = rules },
            rules.Select(Prep).ToArray(), _tags, _tagDefs, publisher);
    }

    private static PreparedAlarmRule Prep(AlarmRule rule) => new()
    {
        Rule = rule,
        TagIndices = [0]
    };

    private static AlarmRule Threshold(string name, params ThresholdLimit[] limits) => new()
    {
        Name = name,
        Type = AlarmType.Threshold,
        TagName = "Boiler1.Temp",
        Limits = limits,
        Hysteresis = 2,
        RequiresAck = true,
        Description = name
    };

    private static ThresholdLimit Hi(double value, AlarmSeverity? severity = null)
        => new() { Kind = ThresholdKind.Hi, Value = value, Severity = severity };

    private static ThresholdLimit HiHi(double value, AlarmSeverity? severity = null)
        => new() { Kind = ThresholdKind.HiHi, Value = value, Severity = severity };

    private TagValue Tag(string name) => _tags.Read(_catalog.Id(name));

    private static string Rule(string ruleName, string suffix)
        => Core.Alarms.AlarmTags.RuleTag(ruleName, suffix);
    private static string Group(string path, string suffix)
        => Core.Alarms.AlarmTags.GroupTag(path, suffix);
    private static string System(string suffix)
        => Core.Alarms.AlarmTags.SystemTag(suffix);

    [Fact]
    public void Activation_PublishesRuleGroupAndRootTags()
    {
        var rule = Threshold("Цех2.Секция5.Насос7.Перегрев", Hi(80, AlarmSeverity.Warning));
        var engine = EngineWithPublisher(rule);

        _tags.Set(0, 90);
        engine.EvaluateTag(new TagId(0), T0);

        Assert.Equal(1, Tag(Rule(rule.Name, "Active")).Value);
        Assert.Equal(1, Tag(Rule(rule.Name, "Unacked")).Value);
        Assert.Equal((double)AlarmSeverity.Warning, Tag(Rule(rule.Name, "Severity")).Value);

        foreach (string path in new[] { "Цех2", "Цех2.Секция5", "Цех2.Секция5.Насос7" })
        {
            Assert.Equal(1, Tag(Group(path, "AnyActive")).Value);
            Assert.Equal(1, Tag(Group(path, "AnyUnacked")).Value);
            Assert.Equal((double)AlarmSeverity.Warning, Tag(Group(path, "MaxSeverity")).Value);
            Assert.Equal(1, Tag(Group(path, "Count")).Value);
        }

        Assert.Equal(1, Tag(System("AnyActive")).Value);
        Assert.Equal(1, Tag(System("AnyUnacked")).Value);
        Assert.Equal(1, Tag(System("Count")).Value);
    }

    [Fact]
    public void Acknowledge_ClearsUnackedEverywhere()
    {
        var rule = Threshold("Цех2.Насос7.Перегрев", Hi(80));
        var engine = EngineWithPublisher(rule);

        _tags.Set(0, 90);
        engine.EvaluateTag(new TagId(0), T0);
        engine.Acknowledge(rule.Name, "op", null, T0 + 1000);

        Assert.Equal(1, Tag(Rule(rule.Name, "Active")).Value);  // активна, но квитирована
        Assert.Equal(0, Tag(Rule(rule.Name, "Unacked")).Value);
        Assert.Equal(0, Tag(Group("Цех2", "AnyUnacked")).Value);
        Assert.Equal(1, Tag(Group("Цех2", "AnyActive")).Value);
        Assert.Equal(0, Tag(System("AnyUnacked")).Value);
    }

    [Fact]
    public void MultiLevelGroups_AggregateAcrossBranches()
    {
        var ruleA = Threshold("Цех2.Секция5.Насос7.Перегрев", Hi(80));
        var ruleB = Threshold("Цех2.Секция6.Насос2.Перегрев", Hi(80));
        var engine = EngineWithPublisher(ruleA, ruleB);

        _tags.Set(0, 90);
        engine.EvaluateAll(T0);

        Assert.Equal(2, Tag(Group("Цех2", "Count")).Value);
        Assert.Equal(1, Tag(Group("Цех2.Секция5", "Count")).Value);
        Assert.Equal(1, Tag(Group("Цех2.Секция6", "Count")).Value);
        Assert.Equal(2, Tag(System("Count")).Value);
    }

    [Fact]
    public void RuleWithoutDots_UpdatesOnlyRoot()
    {
        var rule = Threshold("Одиночная", Hi(80));
        var engine = EngineWithPublisher(rule);

        _tags.Set(0, 90);
        engine.EvaluateTag(new TagId(0), T0);

        Assert.Equal(1, Tag(Rule(rule.Name, "Active")).Value);
        Assert.Equal(1, Tag(System("Count")).Value);
    }

    [Fact]
    public void NoStateChange_NoTagWrites()
    {
        var rule = Threshold("Цех2.Насос7.Перегрев", Hi(80));
        var engine = EngineWithPublisher(rule);

        _tags.Set(0, 90);
        engine.EvaluateTag(new TagId(0), T0);
        int writesAfterActivation = _tags.Writes;
        Assert.True(writesAfterActivation > 0);

        // те же значения, ещё пересчёт — ни одной записи: лишняя эпоха это
        // лишний пересчёт схем
        engine.EvaluateTag(new TagId(0), T0 + 100);
        engine.EvaluateAll(T0 + 200);
        Assert.Equal(writesAfterActivation, _tags.Writes);
    }

    [Fact]
    public void BadQuality_FreezesState_MarksRuleTagsUncertain()
    {
        var rule = Threshold("Цех2.Насос7.Перегрев", Hi(80));
        var engine = EngineWithPublisher(rule);

        _tags.Set(0, 90);
        engine.EvaluateTag(new TagId(0), T0);

        // связь оборвалась: состояние заморожено (§2.10), теги — Uncertain
        _tags.Set(0, 95, Quality.Bad);
        engine.EvaluateTag(new TagId(0), T0 + 100);

        var active = Tag(Rule(rule.Name, "Active"));
        Assert.Equal(1, active.Value); // замороженное значение сохраняется
        Assert.Equal(Quality.Uncertain, active.Quality);

        // связь восстановилась — качество возвращается
        _tags.Set(0, 95, Quality.Good);
        engine.EvaluateTag(new TagId(0), T0 + 200);
        Assert.Equal(Quality.Good, Tag(Rule(rule.Name, "Active")).Quality);
    }

    [Fact]
    public void Escalation_RaisesSeverityAndReAlerts()
    {
        var rule = Threshold("Цех2.Насос7.Перегрев",
            Hi(80, AlarmSeverity.Warning), HiHi(90, AlarmSeverity.Critical));
        var engine = EngineWithPublisher(rule);

        _tags.Set(0, 85);
        engine.EvaluateTag(new TagId(0), T0);
        engine.Acknowledge(rule.Name, "op", null, T0 + 500);
        Assert.Equal(0, Tag(Rule(rule.Name, "Unacked")).Value);

        _tags.Set(0, 95);
        engine.EvaluateTag(new TagId(0), T0 + 1000);

        Assert.Equal((double)AlarmSeverity.Critical, Tag(Rule(rule.Name, "Severity")).Value);
        Assert.Equal(1, Tag(Rule(rule.Name, "Unacked")).Value); // re-alert
        Assert.Equal((double)AlarmSeverity.Critical, Tag(Group("Цех2", "MaxSeverity")).Value);
    }

    [Fact]
    public void RestoreRecovered_PublishesRestoredStateImmediately()
    {
        var rule = Threshold("Цех2.Насос7.Перегрев", Hi(80, AlarmSeverity.High));
        var engine = EngineWithPublisher(rule);

        engine.RestoreRecovered(
        [
            new RecoveredAlarmState(rule.Name, null, AlarmState.ActiveUnack, T0, null)
        ]);

        Assert.Equal(1, Tag(Rule(rule.Name, "Active")).Value);
        Assert.Equal(1, Tag(Rule(rule.Name, "Unacked")).Value);
        Assert.Equal(1, Tag(System("Count")).Value);
    }
}
