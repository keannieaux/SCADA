namespace SCADA.Core.Alarms;

/// <summary>
/// Приоритет аварии (docs/M5-plan.md §2.5). Порядок значений — порядковый:
/// баннер сортирует по убыванию, звук выбирается по максимальному severity
/// среди активных неквитированных.
/// </summary>
public enum AlarmSeverity : byte
{
    /// <summary>К сведению, действий не требует.</summary>
    Info = 0,
    /// <summary>Отклонение, следить.</summary>
    Warning = 1,
    /// <summary>Требуется вмешательство в ближайшее время.</summary>
    High = 2,
    /// <summary>Требуется немедленное вмешательство.</summary>
    Critical = 3
}
