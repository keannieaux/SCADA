using SCADA.Alarms;
using SCADA.Core.Alarms;
using SCADA.Core.Tags;

namespace SCADA.Runtime.Alarms;

/// <summary>
/// Подготовка правил к исполнению: имена тегов заменяются индексами TagTable
/// (раннее связывание, ТЗ §11.6). Expression-правила проходят через фабрику —
/// в dev-режиме это компилятор, в боевом пакете — чтение из code.bin;
/// рантайм про компилятор не знает (§5.4).
/// </summary>
public static class AlarmRulePreparer
{
    /// <param name="expressionFactory">
    /// Строит PreparedAlarmRule для Expression-правила (компиляция или чтение
    /// из пакета). null или null-результат — правило пропускается с предупреждением.
    /// </param>
    public static IReadOnlyList<PreparedAlarmRule> Prepare(
        AlarmConfiguration config,
        IReadOnlyList<TagDefinition> tags,
        Func<AlarmRule, PreparedAlarmRule?>? expressionFactory,
        Action<string>? onWarning = null)
    {
        var indexByName = new Dictionary<string, int>();
        foreach (var tag in tags)
            indexByName[tag.Name] = tag.Id.Value;

        var result = new List<PreparedAlarmRule>();
        foreach (var rule in config.Rules)
        {
            switch (rule.Type)
            {
                case AlarmType.Threshold:
                    if (!indexByName.TryGetValue(rule.TagName!, out int index))
                    {
                        onWarning?.Invoke($"[сигнализация] правило '{rule.Name}': тег '{rule.TagName}' не найден, правило пропущено");
                        break;
                    }
                    result.Add(new PreparedAlarmRule { Rule = rule, TagIndices = [index] });
                    break;

                case AlarmType.Expression:
                    if (expressionFactory is null)
                    {
                        onWarning?.Invoke($"[сигнализация] правило '{rule.Name}': expression-правила недоступны в этом режиме, правило пропущено");
                        break;
                    }
                    var prepared = expressionFactory(rule);
                    if (prepared is null)
                        onWarning?.Invoke($"[сигнализация] правило '{rule.Name}': условие не скомпилировано, правило пропущено");
                    else
                        result.Add(prepared);
                    break;
            }
        }
        return result;
    }
}
