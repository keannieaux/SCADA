using SCADA.Core.Alarms;
using SCADA.Expressions;

namespace SCADA.Alarms;

/// <summary>Состояние одной уставки внутри правила: гистерезис и фронты
/// привязаны к уставке физически, но наружу правило показывает одно
/// агрегированное состояние (docs/M5-plan.md §7.1).</summary>
internal sealed class LimitState
{
    public required ThresholdLimit Limit { get; init; }
    public bool Active { get; set; }
}

/// <summary>
/// Рантайм-состояние одного правила. Одна авария на правило: при нескольких
/// сработавших уставках показывается старшая по severity, рост severity при
/// активной аварии — эскалация (новое событие, re-alert). State machine —
/// docs/M5-plan.md §7.1.
/// </summary>
internal sealed class RuleRuntime
{
    public required AlarmRule Rule { get; init; }
    public Expression? Condition { get; init; }
    public required int[] TagIndices { get; init; }

    /// <summary>MinDuration с учётом проектного дефолта (§2.11).</summary>
    public required int MinDurationMs { get; init; }

    /// <summary>Уставки с их состояниями. Только для Threshold-правил.</summary>
    public LimitState[] Limits { get; init; } = [];

    public AlarmState State { get; set; } = AlarmState.Normal;

    /// <summary>Агрегированное условие: истинно, пока сработала хотя бы одна
    /// уставка (или истинно выражение). Отдельно от State: условие может быть
    /// истинным, пока фронт ждёт MinDuration, и наоборот (RtnUnack).</summary>
    public bool ConditionActive { get; set; }

    /// <summary>Когда условие стало истинным. Отметка фронта и база MinDuration.</summary>
    public long? ConditionTrueSinceUtcMs { get; set; }

    /// <summary>Старшая сработавшая уставка на текущий момент. null —
    /// для Expression-правил и в неактивном состоянии.</summary>
    public ThresholdLimit? ActiveLimit { get; set; }

    public long ActivatedAtUtcMs { get; set; }
    public string? AcknowledgedBy { get; set; }

    public AlarmSeverity Severity => ActiveLimit?.Severity ?? Rule.Severity;
    public string Area => Rule.Area;

    /// <summary>Ожидает подтверждения по MinDuration: условие истинно,
    /// но событие Active ещё не выстрелило.</summary>
    public bool HasPendingFront =>
        ConditionActive && State is AlarmState.Normal or AlarmState.RtnUnack;
}
