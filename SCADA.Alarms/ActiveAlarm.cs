using SCADA.Core.Alarms;

namespace SCADA.Alarms;

/// <summary>Состояние state machine аварии (docs/M5-plan.md §7.1).</summary>
public enum AlarmState : byte
{
    Normal = 0,
    ActiveUnack = 1,
    ActiveAck = 2,
    /// <summary>Вернулась в норму, но не квитирована.</summary>
    RtnUnack = 3
}

/// <summary>Активная (или ожидающая квитирования) авария для баннера.</summary>
public record ActiveAlarm(
    string RuleName,
    ThresholdKind? Limit,
    AlarmState State,
    AlarmSeverity Severity,
    string Area,
    string Message,
    long ActivatedAtUtcMs,
    string? AcknowledgedBy);

public record AlarmFilter(
    AlarmSeverity? MinSeverity = null,
    string? Area = null,
    bool? UnacknowledgedOnly = null);

public record AlarmHistoryQuery(
    long FromUtcMs,
    long ToUtcMs,
    AlarmSeverity? Severity = null,
    string? Area = null,
    string? RuleName = null,
    int Limit = 1000);

public enum AlarmChangeKind : byte
{
    Activated = 0,
    Normalized = 1,
    Acknowledged = 2
}

/// <summary>Элемент подписки UI на изменения аварий.</summary>
public record AlarmChange(
    AlarmChangeKind Kind,
    ActiveAlarm Alarm);
