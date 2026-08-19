using SCADA.Core.Alarms;
using SCADA.Expressions;

namespace SCADA.Alarms;

/// <summary>
/// Правило, подготовленное к исполнению: для Expression-правил — готовое
/// скомпилированное выражение, для всех — индексы тегов, от которых правило
/// зависит. Кто компилирует (PackageBuilder на сборке проекта) — забота
/// конвейера, не движка (§5.4: рантайм без компилятора).
/// </summary>
public sealed class PreparedAlarmRule
{
    public required AlarmRule Rule { get; init; }

    /// <summary>Скомпилированное условие. Только для Type=Expression.</summary>
    public Expression? Condition { get; init; }

    /// <summary>Индексы тегов (в TagTable), от которых зависит правило.
    /// Для Threshold — единственный тег, для Expression — все участвующие.</summary>
    public required int[] TagIndices { get; init; }
}
