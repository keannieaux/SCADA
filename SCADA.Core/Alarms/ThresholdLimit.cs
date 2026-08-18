namespace SCADA.Core.Alarms;

/// <summary>
/// Одна уставка порогового правила. Каждая уставка — самостоятельное условие
/// со своим состоянием: движок ведёт state machine по ключу (RuleName, Kind).
/// </summary>
public class ThresholdLimit
{
    public required ThresholdKind Kind { get; set; }
    public required double Value { get; set; }

    /// <summary>Severity сработавшей уставки (per-limit, §2.5).
    /// null — дефолт правила <see cref="AlarmRule.Severity"/>.</summary>
    public AlarmSeverity? Severity { get; set; }
}
