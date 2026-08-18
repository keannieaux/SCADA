using SCADA.Core.Alarms;
using SCADA.Core.Devices;
using SCADA.Core.Tags;
using SCADA.Expressions;

namespace SCADA.Alarms.Tests;

/// <summary>
/// State machine движка (docs/M5-plan.md §7.1): все переходы, гистерезис,
/// MinDuration, заморозка при качестве ≠ Good (§2.10), per-limit severity.
/// </summary>
public class AlarmEngineTests
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
            DataType = TagDataType.Analog, DeviceId = new DeviceId(0), Units = "°C" },
        new TagDefinition { Id = new TagId(1), Name = "Pump1.Running",
            DataType = TagDataType.Discrete, DeviceId = new DeviceId(0) }
    };

    private static AlarmConfiguration Config(params AlarmRule[] rules) => new()
    {
        Rules = rules,
        Templates = new Dictionary<string, string>
        {
            ["thresholdActive"] = "{Severity}: {Description}. {Tag0.Value} {Tag0.Unit} >= {Limit.Value} ({Limit.Kind})",
            ["thresholdNormal"] = "{Description} вернулось в норму",
            ["expressionActive"] = "{Severity}: {Description}. Значения: {TagValues}"
        }
    };

    private static AlarmRule ThresholdRule(string name = "R1", bool requiresAck = true,
        int? minDurationMs = null, params ThresholdLimit[] limits) => new()
    {
        Name = name,
        Type = AlarmType.Threshold,
        TagName = "Boiler1.Temp",
        Limits = limits,
        Hysteresis = 2,
        RequiresAck = requiresAck,
        MinDurationMs = minDurationMs,
        Area = "Котельная",
        Description = "Температура котла"
    };

    private AlarmEngine Engine(AlarmConfiguration config, params PreparedAlarmRule[] rules)
        => new(config, rules, _tags, _tagDefs);

    private PreparedAlarmRule Prep(AlarmRule rule) => new()
    {
        Rule = rule,
        TagIndices = [0]
    };

    private static ThresholdLimit Hi(double value, AlarmSeverity? severity = null)
        => new() { Kind = ThresholdKind.Hi, Value = value, Severity = severity };

    // --- фронты и state machine ---

    [Fact]
    public void Threshold_CrossingLimit_FiresActiveWithSnapshot()
    {
        var rule = ThresholdRule(limits: Hi(80, AlarmSeverity.High));
        var engine = Engine(Config(rule), Prep(rule));
        _tags.Set(0, 85);

        var events = engine.EvaluateTag(new TagId(0), T0);

        var ev = Assert.Single(events);
        Assert.Equal(AlarmEventType.Active, ev.Type);
        Assert.Equal("R1", ev.RuleName);
        Assert.Equal(ThresholdKind.Hi, ev.Limit);
        Assert.Equal(AlarmSeverity.High, ev.Severity); // per-limit severity
        Assert.Equal(T0, ev.TimestampUtcMs);

        var snapshot = Assert.Single(ev.TagSnapshots);
        Assert.Equal("Boiler1.Temp", snapshot.TagName);
        Assert.Equal(85, snapshot.Value);
        Assert.Equal(Quality.Good, snapshot.Quality);

        var active = Assert.Single(engine.GetActive(new AlarmFilter()));
        Assert.Equal(AlarmState.ActiveUnack, active.State);
        Assert.True(engine.IsActive("R1"));
    }

    [Fact]
    public void Threshold_Hysteresis_PreventsChatter()
    {
        var rule = ThresholdRule(limits: Hi(80));
        var engine = Engine(Config(rule), Prep(rule));

        _tags.Set(0, 85);
        engine.EvaluateTag(new TagId(0), T0);

        // внутри полосы гистерезиса (80 - 2) — авария держится
        _tags.Set(0, 79);
        Assert.Empty(engine.EvaluateTag(new TagId(0), T0 + 100));

        // ниже полосы — возврат в норму
        _tags.Set(0, 77);
        var events = engine.EvaluateTag(new TagId(0), T0 + 200);
        var ev = Assert.Single(events);
        Assert.Equal(AlarmEventType.Normal, ev.Type);
        Assert.Empty(ev.TagSnapshots); // снимки только для Active (§2.11)

        // не квитировано — перешло в RtnUnack и висит в списке активных
        var active = Assert.Single(engine.GetActive(new AlarmFilter()));
        Assert.Equal(AlarmState.RtnUnack, active.State);
        Assert.False(engine.IsActive("R1")); // условие ложно — для мнемосхем не активна
    }

    [Fact]
    public void Acknowledge_ActiveUnack_GoesToActiveAck_ThenNormalCloses()
    {
        var rule = ThresholdRule(limits: Hi(80));
        var engine = Engine(Config(rule), Prep(rule));
        _tags.Set(0, 85);
        engine.EvaluateTag(new TagId(0), T0);

        var ack = engine.Acknowledge("R1", "op@ARM1", "принято", T0 + 100);

        Assert.NotNull(ack);
        Assert.Equal(AlarmEventType.Acknowledged, ack.Type);
        Assert.Equal("op@ARM1", ack.AcknowledgedBy);
        Assert.Equal("принято", ack.AckComment);

        var active = Assert.Single(engine.GetActive(new AlarmFilter()));
        Assert.Equal(AlarmState.ActiveAck, active.State);
        Assert.Equal("op@ARM1", active.AcknowledgedBy);

        // возврат в норму из квитированного состояния закрывает аварию
        _tags.Set(0, 50);
        engine.EvaluateTag(new TagId(0), T0 + 200);
        Assert.Empty(engine.GetActive(new AlarmFilter()));
    }

    [Fact]
    public void Acknowledge_RtnUnack_ClosesAlarm()
    {
        var rule = ThresholdRule(limits: Hi(80));
        var engine = Engine(Config(rule), Prep(rule));
        _tags.Set(0, 85);
        engine.EvaluateTag(new TagId(0), T0);
        _tags.Set(0, 50);
        engine.EvaluateTag(new TagId(0), T0 + 100); // → RtnUnack

        var ack = engine.Acknowledge("R1", "op@ARM1", null, T0 + 200);

        Assert.NotNull(ack);
        Assert.Empty(engine.GetActive(new AlarmFilter()));
    }

    [Fact]
    public void Acknowledge_WhenNothingToAck_ReturnsNull()
    {
        var rule = ThresholdRule(limits: Hi(80));
        var engine = Engine(Config(rule), Prep(rule));

        Assert.Null(engine.Acknowledge("R1", "op@ARM1", null, T0));
        Assert.Null(engine.Acknowledge("NO_SUCH_RULE", "op@ARM1", null, T0));
    }

    [Fact]
    public void RequiresAckFalse_NormalClosesImmediately()
    {
        var rule = ThresholdRule(requiresAck: false, limits: Hi(80));
        var engine = Engine(Config(rule), Prep(rule));
        _tags.Set(0, 85);
        engine.EvaluateTag(new TagId(0), T0);

        var active = Assert.Single(engine.GetActive(new AlarmFilter()));
        Assert.Equal(AlarmState.ActiveAck, active.State); // квитирование не требуется

        _tags.Set(0, 50);
        engine.EvaluateTag(new TagId(0), T0 + 100);
        Assert.Empty(engine.GetActive(new AlarmFilter()));
    }

    [Fact]
    public void RetimeFromRtnUnack_NewFront_FiresNewActiveEvent()
    {
        var rule = ThresholdRule(limits: Hi(80));
        var engine = Engine(Config(rule), Prep(rule));
        _tags.Set(0, 85);
        engine.EvaluateTag(new TagId(0), T0);
        _tags.Set(0, 50);
        engine.EvaluateTag(new TagId(0), T0 + 100); // RtnUnack

        // повторное срабатывание из RtnUnack — новый фронт и новое событие
        _tags.Set(0, 90);
        var events = engine.EvaluateTag(new TagId(0), T0 + 200);

        var ev = Assert.Single(events);
        Assert.Equal(AlarmEventType.Active, ev.Type);
        Assert.Single(engine.GetActive(new AlarmFilter { UnacknowledgedOnly = true }));
    }

    // --- MinDuration ---

    [Fact]
    public void MinDuration_DelaysFront_UntilConditionHeld()
    {
        var rule = ThresholdRule(minDurationMs: 1000, limits: Hi(80));
        var engine = Engine(Config(rule), Prep(rule));
        _tags.Set(0, 85);

        Assert.Empty(engine.EvaluateTag(new TagId(0), T0)); // фронт отложен
        Assert.Empty(engine.Tick(T0 + 500));                // рано

        var events = engine.Tick(T0 + 1000);
        var ev = Assert.Single(events);
        Assert.Equal(AlarmEventType.Active, ev.Type);
        Assert.Equal(T0, ev.TimestampUtcMs); // время фронта, а не подтверждения
    }

    [Fact]
    public void MinDuration_ConditionDroppedEarly_CancelsSilently()
    {
        var rule = ThresholdRule(minDurationMs: 1000, limits: Hi(80));
        var engine = Engine(Config(rule), Prep(rule));
        _tags.Set(0, 85);
        engine.EvaluateTag(new TagId(0), T0);

        _tags.Set(0, 50); // вернулось раньше срока — ни Active, ни Normal
        Assert.Empty(engine.EvaluateTag(new TagId(0), T0 + 500));
        Assert.Empty(engine.Tick(T0 + 2000));
        Assert.Empty(engine.GetActive(new AlarmFilter()));
    }

    // --- качество ---

    [Fact]
    public void BadQuality_FreezesRule_NoEventsNoChanges()
    {
        var rule = ThresholdRule(limits: Hi(80));
        var engine = Engine(Config(rule), Prep(rule));

        // пересечение на плохом качестве — не срабатывает
        _tags.Set(0, 85, Quality.Bad);
        Assert.Empty(engine.EvaluateTag(new TagId(0), T0));
        Assert.Empty(engine.GetActive(new AlarmFilter()));

        // качество восстановилось — правило пересчитывается и срабатывает
        _tags.Set(0, 85, Quality.Good);
        Assert.Single(engine.EvaluateTag(new TagId(0), T0 + 100));
    }

    [Fact]
    public void BadQuality_ActiveAlarm_DoesNotReturnToNormal()
    {
        var rule = ThresholdRule(limits: Hi(80));
        var engine = Engine(Config(rule), Prep(rule));
        _tags.Set(0, 85);
        engine.EvaluateTag(new TagId(0), T0);

        // обрыв связи: значение "в норме", но качество Bad — норму не фиксируем
        _tags.Set(0, 50, Quality.Bad);
        Assert.Empty(engine.EvaluateTag(new TagId(0), T0 + 100));
        Assert.Single(engine.GetActive(new AlarmFilter()));
    }

    // --- эскалация (одна авария на правило, §7.1) ---

    private AlarmEngine TwoLimitEngine(out AlarmRule rule)
    {
        rule = ThresholdRule(limits: [
            new ThresholdLimit { Kind = ThresholdKind.HiHi, Value = 95, Severity = AlarmSeverity.Critical },
            Hi(80, AlarmSeverity.High)
        ]);
        return Engine(Config(rule), Prep(rule));
    }

    [Fact]
    public void Escalation_HigherSeverity_SameAlarmSingleRow()
    {
        var engine = TwoLimitEngine(out _);

        _tags.Set(0, 85); // только Hi
        var ev1 = Assert.Single(engine.EvaluateTag(new TagId(0), T0));
        Assert.Equal(AlarmEventType.Active, ev1.Type);
        Assert.Equal(ThresholdKind.Hi, ev1.Limit);
        Assert.Equal(AlarmSeverity.High, ev1.Severity);
        Assert.Single(engine.GetActive(new AlarmFilter()));

        _tags.Set(0, 96); // пересекло и HiHi — та же авария эскалирует
        var ev2 = Assert.Single(engine.EvaluateTag(new TagId(0), T0 + 100));
        Assert.Equal(AlarmEventType.Escalated, ev2.Type);
        Assert.Equal(ThresholdKind.HiHi, ev2.Limit);
        Assert.Equal(AlarmSeverity.Critical, ev2.Severity);
        Assert.NotEmpty(ev2.TagSnapshots); // эскалация — как Active, со снимками

        // в баннере по-прежнему одна строка, но с новым severity
        var active = Assert.Single(engine.GetActive(new AlarmFilter()));
        Assert.Equal(AlarmSeverity.Critical, active.Severity);
        Assert.Equal(ThresholdKind.HiHi, active.Limit);
    }

    [Fact]
    public void Escalation_AfterAck_ReAlertsToUnack()
    {
        var engine = TwoLimitEngine(out _);
        _tags.Set(0, 85);
        engine.EvaluateTag(new TagId(0), T0);
        engine.Acknowledge("R1", "op@ARM1", null, T0 + 50);
        Assert.Equal(AlarmState.ActiveAck,
            Assert.Single(engine.GetActive(new AlarmFilter())).State);

        // эскалация после квитирования — re-alert
        _tags.Set(0, 96);
        Assert.Single(engine.EvaluateTag(new TagId(0), T0 + 100));
        var active = Assert.Single(engine.GetActive(new AlarmFilter()));
        Assert.Equal(AlarmState.ActiveUnack, active.State);
        Assert.Null(active.AcknowledgedBy);
    }

    [Fact]
    public void Deescalation_IsSilent()
    {
        var engine = TwoLimitEngine(out _);
        _tags.Set(0, 96);
        engine.EvaluateTag(new TagId(0), T0); // сразу HiHi

        _tags.Set(0, 85); // HiHi ушло, Hi держится — деэскалация без событий
        Assert.Empty(engine.EvaluateTag(new TagId(0), T0 + 100));

        var active = Assert.Single(engine.GetActive(new AlarmFilter()));
        Assert.Equal(AlarmSeverity.High, active.Severity);
        Assert.Equal(ThresholdKind.Hi, active.Limit);
    }

    [Fact]
    public void Normal_OnlyWhenLastLimitClears()
    {
        var engine = TwoLimitEngine(out _);
        _tags.Set(0, 96);
        engine.EvaluateTag(new TagId(0), T0);

        _tags.Set(0, 85); // HiHi отпустило, Hi ещё активен — авария жива
        engine.EvaluateTag(new TagId(0), T0 + 100);
        Assert.Single(engine.GetActive(new AlarmFilter()));

        _tags.Set(0, 50); // ушла последняя уставка — возврат в норму
        var ev = Assert.Single(engine.EvaluateTag(new TagId(0), T0 + 200));
        Assert.Equal(AlarmEventType.Normal, ev.Type);
    }

    // --- Expression ---

    // байткод "tag0 > 5": LoadTag 0, LoadConst 0, Greater, Return
    private static Expression TagGreaterThan(int tagIndex, double constant)
    {
        var code = new List<byte> { (byte)OpCode.LoadTag };
        code.AddRange(BitConverter.GetBytes(tagIndex));
        code.Add((byte)OpCode.LoadConst);
        code.AddRange(BitConverter.GetBytes(0));
        code.Add((byte)OpCode.Greater);
        code.Add((byte)OpCode.Return);
        return new Expression { Code = code.ToArray(), Constants = [constant] };
    }

    [Fact]
    public void ExpressionRule_MultiTag_SnapshotsAllParticipants()
    {
        var rule = new AlarmRule
        {
            Name = "TEMP_HIGH",
            Type = AlarmType.Expression,
            Condition = "Boiler1.Temp > 90",
            Severity = AlarmSeverity.Critical,
            Description = "Высокая температура"
        };
        var engine = Engine(Config(rule), new PreparedAlarmRule
        {
            Rule = rule,
            Condition = TagGreaterThan(0, 90),
            TagIndices = [0]
        });

        _tags.Set(0, 91);
        var events = engine.EvaluateTag(new TagId(0), T0);

        var ev = Assert.Single(events);
        Assert.Null(ev.Limit); // у Expression-правил уставки нет
        Assert.Contains("TEMP_HIGH".Replace("TEMP_HIGH", "Высокая температура"), ev.Message);
        Assert.Single(ev.TagSnapshots);
    }

    // --- фильтры и сообщения ---

    [Fact]
    public void GetActive_FiltersByAreaAndUnacknowledged()
    {
        var rule1 = ThresholdRule("R1", limits: Hi(80));
        var rule2 = new AlarmRule
        {
            Name = "R2", Type = AlarmType.Threshold, TagName = "Pump1.Running",
            Limits = [Hi(0.5)], Area = "Насосная", Description = "Насос"
        };
        var config = Config(rule1, rule2);
        var engine = Engine(config, Prep(rule1), new PreparedAlarmRule
        {
            Rule = rule2, TagIndices = [1]
        });

        _tags.Set(0, 85);
        _tags.Set(1, 1);
        engine.EvaluateTag(new TagId(0), T0);
        engine.EvaluateTag(new TagId(1), T0);

        Assert.Equal(2, engine.GetActive(new AlarmFilter()).Count);
        Assert.Single(engine.GetActive(new AlarmFilter { Area = "Насосная" }));

        engine.Acknowledge("R1", "op@ARM1", null, T0 + 100);
        var unack = engine.GetActive(new AlarmFilter { UnacknowledgedOnly = true });
        Assert.Single(unack);
        Assert.Equal("R2", unack[0].RuleName);
    }

    [Fact]
    public void ActiveEvent_MessageRenderedFromTemplate()
    {
        var rule = ThresholdRule(limits: Hi(80, AlarmSeverity.High));
        var engine = Engine(Config(rule), Prep(rule));
        _tags.Set(0, 85);

        var ev = Assert.Single(engine.EvaluateTag(new TagId(0), T0));

        Assert.Equal("High: Температура котла. 85 °C >= 80 (Hi)", ev.Message);
    }

    [Fact]
    public void RuleMessageTemplate_OverridesGlobal()
    {
        var rule = ThresholdRule(limits: Hi(80));
        rule.MessageTemplate = "АВАРИЯ: {Rule}";
        var engine = Engine(Config(rule), Prep(rule));
        _tags.Set(0, 85);

        var ev = Assert.Single(engine.EvaluateTag(new TagId(0), T0));

        Assert.Equal("АВАРИЯ: R1", ev.Message);
    }
}
