using System.Globalization;
using System.Text;
using SCADA.Core.Alarms;
using SCADA.Core.Tags;

namespace SCADA.Alarms;

/// <summary>
/// Рендер готового текста сообщения по шаблону (docs/M5-plan.md §2.4).
/// В журнал уходит уже готовая строка — история не зависит от шаблонов
/// и переживает переименование тегов. Плейсхолдеры: {Severity}, {Rule},
/// {Description}, {Area}, {Tag0.Value}, {Tag0.Unit}, {Limit.Value},
/// {Limit.Kind}, {TagValues}.
/// </summary>
internal static class MessageRenderer
{
    // ключи глобальных шаблонов в AlarmConfiguration.Templates
    public static string TemplateKey(AlarmType type, AlarmEventType eventType)
        => (type, eventType) switch
        {
            (AlarmType.Threshold, AlarmEventType.Active) => "thresholdActive",
            (AlarmType.Threshold, AlarmEventType.Normal) => "thresholdNormal",
            (AlarmType.Threshold, AlarmEventType.Escalated) => "thresholdEscalated",
            (AlarmType.Expression, AlarmEventType.Active) => "expressionActive",
            (AlarmType.Expression, AlarmEventType.Normal) => "expressionNormal",
            _ => "acknowledged"
        };

    public static string Render(string template, RuleRuntime rt, AlarmEventType eventType,
        IReadOnlyList<AlarmTagSnapshot> snapshots, IReadOnlyList<TagDefinition> tagDefinitions)
    {
        var result = new StringBuilder(template)
            .Replace("{Severity}", rt.Severity.ToString())
            .Replace("{Rule}", rt.Rule.Name)
            .Replace("{Description}", rt.Rule.Description)
            .Replace("{Area}", rt.Rule.Area);

        if (rt.ActiveLimit is { } limit)
        {
            result.Replace("{Limit.Value}", limit.Value.ToString(CultureInfo.InvariantCulture))
                  .Replace("{Limit.Kind}", limit.Kind.ToString());
        }

        if (snapshots.Count > 0)
        {
            var first = snapshots[0];
            result.Replace("{Tag0.Value}", FormatValue(first.Value))
                  .Replace("{Tag0.Unit}", Unit(first.TagId, tagDefinitions));

            result.Replace("{TagValues}", string.Join(", ",
                snapshots.Select(s => $"{s.TagName}={FormatValue(s.Value)}")));
        }

        return result.ToString();
    }

    private static string FormatValue(double? value)
        => value?.ToString(CultureInfo.InvariantCulture) ?? "—";

    private static string Unit(TagId tagId, IReadOnlyList<TagDefinition> tagDefinitions)
        => tagId.Value < tagDefinitions.Count ? tagDefinitions[tagId.Value].Units : "";
}
