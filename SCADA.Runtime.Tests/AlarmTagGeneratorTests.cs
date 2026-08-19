using SCADA.Core.Alarms;
using SCADA.Core.Tags;
using SCADA.Runtime.Configuration;

namespace SCADA.Runtime.Tests;

/// <summary>
/// Генератор системных тегов сигнализации (концепт §10): состав имён,
/// детерминизм Id, Origin=Alarm, корень без правил, валидация имён правил.
/// </summary>
public class AlarmTagGeneratorTests
{
    private static AlarmRule Rule(string name) => new()
    {
        Name = name,
        Type = AlarmType.Expression,
        Condition = "T0 > 0"
    };

    private static ProjectConfiguration CreateConfig(params AlarmRule[] rules) => new()
    {
        Name = "Test",
        Tags =
        [
            new TagDefinition { Id = new TagId(0), Name = "T0",
                DataType = TagDataType.Analog, DeviceId = new SCADA.Core.Devices.DeviceId(0) }
        ],
        Alarms = new AlarmConfiguration { Rules = rules }
    };

    [Fact]
    public void Rules_ProduceRuleGroupAndSystemTags()
    {
        var config = CreateConfig(
            Rule("Цех2.Секция5.Насос7.Перегрев"),
            Rule("Цех2.Секция6.Насос2.Перегрев"));

        AlarmTagGenerator.AppendAlarmTags(config);

        var names = config.Tags.Select(t => t.Name).ToArray();

        // по 3 на правило
        Assert.Contains("@Alarm.Цех2.Секция5.Насос7.Перегрев.Active", names);
        Assert.Contains("@Alarm.Цех2.Секция5.Насос7.Перегрев.Unacked", names);
        Assert.Contains("@Alarm.Цех2.Секция5.Насос7.Перегрев.Severity", names);

        // по 4 на каждый префикс: Цех2, Цех2.Секция5, Цех2.Секция5.Насос7, ...
        Assert.Contains("@AlarmGroup.Цех2.AnyActive", names);
        Assert.Contains("@AlarmGroup.Цех2.Секция5.Count", names);
        Assert.Contains("@AlarmGroup.Цех2.Секция5.Насос7.MaxSeverity", names);
        Assert.Contains("@AlarmGroup.Цех2.Секция6.AnyUnacked", names);

        // корень: 4 агрегата + диагностика подсистемы
        Assert.Contains("@AlarmSystem.AnyActive", names);
        Assert.Contains("@AlarmSystem.AnyUnacked", names);
        Assert.Contains("@AlarmSystem.MaxSeverity", names);
        Assert.Contains("@AlarmSystem.Count", names);
        Assert.Contains("@AlarmSystem.JournalSizeMb", names);

        // 1 процессный + 2*3 правила + 5 групп*4 + 5 корня
        Assert.Equal(1 + 6 + 5 * 4 + 5, config.Tags.Count);

        Assert.All(config.Tags.Skip(1), t => Assert.Equal(TagOrigin.Alarm, t.Origin));
        Assert.All(config.Tags.Skip(1), t => Assert.False(t.IsWritable));
        Assert.All(config.Tags.Skip(1), t => Assert.False(t.IsArchived));

        var device = Assert.Single(config.Devices);
        Assert.Equal("@Alarms", device.Name);
        Assert.Equal("internal", device.DriverName);
    }

    [Fact]
    public void NoRules_SystemTagsStillGenerated()
    {
        // баннер привязывается к @AlarmSystem.* — компилируется в любом проекте
        var config = CreateConfig();

        AlarmTagGenerator.AppendAlarmTags(config);

        var names = config.Tags.Select(t => t.Name).ToArray();
        Assert.Equal(1 + AlarmTags.SystemMetrics.Length, config.Tags.Count);
        Assert.Contains("@AlarmSystem.AnyUnacked", names);
    }

    [Fact]
    public void RuleWithoutDots_ProducesNoGroupTags()
    {
        var config = CreateConfig(Rule("Одиночная"));

        AlarmTagGenerator.AppendAlarmTags(config);

        var names = config.Tags.Select(t => t.Name).ToArray();
        Assert.DoesNotContain(names, n => n.StartsWith("@AlarmGroup."));
        Assert.Contains("@Alarm.Одиночная.Active", names);
    }

    [Fact]
    public void Deterministic_SameProject_SameIds()
    {
        var first = CreateConfig(Rule("Цех2.Насос7.Перегрев"), Rule("Цех2.Насос2.Перегрев"));
        var second = CreateConfig(Rule("Цех2.Насос7.Перегрев"), Rule("Цех2.Насос2.Перегрев"));

        AlarmTagGenerator.AppendAlarmTags(first);
        AlarmTagGenerator.AppendAlarmTags(second);

        Assert.Equal(
            first.Tags.Select(t => (t.Id, t.Name)).ToArray(),
            second.Tags.Select(t => (t.Id, t.Name)).ToArray());
        // Id продолжают плотный ряд после тегов проекта
        Assert.Equal(first.Tags.Count, first.Tags.Max(t => t.Id.Value) + 1);
    }

    [Theory]
    [InlineData("Насос 7")]            // пробел — не лексируется в выражении
    [InlineData("Насос7..Перегрев")]   // пустой сегмент
    [InlineData("Насос7.")]            // пустой хвост
    [InlineData("@Насос7")]            // системный префикс зарезервирован
    [InlineData("7Насос.Перегрев")]    // сегмент с цифры — не идентификатор
    public void InvalidRuleName_RejectedByValidator(string ruleName)
    {
        var config = CreateConfig(Rule(ruleName));

        var errors = ProjectValidator.Validate(config);

        Assert.Contains(errors, e => e.Contains("имя недопустимо"));
    }

    [Fact]
    public void ValidDottedRuleName_PassesValidation()
    {
        var config = CreateConfig(Rule("Цех2.Секция5.Насос7.Перегрев"));

        var errors = ProjectValidator.Validate(config);

        Assert.DoesNotContain(errors, e => e.Contains("имя недопустимо"));
    }
}
