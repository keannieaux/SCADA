using SCADA.Core.Alarms;
using SCADA.Core.Tags;

namespace SCADA.Alarms;

/// <summary>Идентификатор события = первичный ключ строки журнала.</summary>
public readonly record struct AlarmId(long Value);

public enum AlarmEventType : byte
{
    /// <summary>Вход в аварию (фронт false→true). Снабжается снимками тегов.</summary>
    Active = 0,
    /// <summary>Возврат в норму (фронт true→false).</summary>
    Normal = 1,
    /// <summary>Квитирование оператором.</summary>
    Acknowledged = 2,
    /// <summary>Эскалация: при активной аварии сработала уставка с более
    /// высоким severity. Та же авария, не новая строка в баннере. Если авария
    /// была квитирована — снова становится неквитированной (re-alert).</summary>
    Escalated = 3
}

/// <summary>
/// Событие журнала аварий (docs/M5-plan.md §4.2). Сообщение — готовый текст:
/// история отображается без шаблонов и не теряет данные при переименовании тегов.
/// Timestamp — время фронта по часам сервера (UTC), не timestamp тега.
/// </summary>
public record AlarmEvent(
    AlarmId Id,
    long TimestampUtcMs,
    string RuleName,
    ThresholdKind? Limit,           // null для Expression-правил
    AlarmEventType Type,
    string Message,
    AlarmSeverity Severity,
    string Area,
    IReadOnlyList<AlarmTagSnapshot> TagSnapshots,  // для Type = Active/Escalated (§2.11)
    string? AcknowledgedBy = null,
    string? AckComment = null,
    long? AcknowledgedAtUtcMs = null);

/// <summary>Снимок участвующего тега на момент срабатывания.</summary>
public record AlarmTagSnapshot(
    TagId TagId,
    string TagName,
    double? Value,
    Quality Quality);
