namespace SCADA.Runtime.Audit;

/// <summary>Запись журнала аудита (ТЗ §13): кто, когда, что менял, результат.
/// Элементы одного пакета (рецепт) связаны общим BatchId.</summary>
public sealed record AuditEntry(
    long TimestampUtcMs,
    string User,
    string Action,       // "tag-write"
    string Target,       // имя тега
    double? OldValue,
    double? NewValue,
    string Result,       // TagWriteStatus
    string? Detail = null,
    string? BatchId = null);

/// <summary>
/// Журнал аудита (ТЗ §13). Пишутся и успехи, и отказы: попытка записи —
/// тоже событие. Ошибки записи журнала не роняют службу (ТЗ §8.9).
/// </summary>
public interface IAuditJournal
{
    void Append(IReadOnlyList<AuditEntry> entries);
}
