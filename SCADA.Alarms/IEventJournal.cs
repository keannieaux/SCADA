namespace SCADA.Alarms;

/// <summary>
/// Журнал событий/алармов (docs/M5-plan.md §2.1, §8). Абстракция хранилища:
/// реализацию можно заменить (как IArchiveStore для истории) без изменения ядра.
/// </summary>
public interface IEventJournal
{
    /// <summary>Записать события, присвоив им первичные ключи.
    /// Ошибка записи (в т.ч. переполнение диска) не должна ронять вызывающий
    /// код — ТЗ §8.9: журнал не влияет на опрос и HMI.</summary>
    IReadOnlyList<AlarmId> Append(IReadOnlyList<AlarmEvent> events);

    /// <summary>История с фильтрами по времени, severity, area, правилу.</summary>
    IReadOnlyList<AlarmEvent> Query(AlarmHistoryQuery query);

    /// <summary>Последние события в обратном порядке (новые первыми).
    /// Для восстановления состояния активных аварий при старте (§7.3).</summary>
    IReadOnlyList<AlarmEvent> ReadRecentDesc(int limit);

    /// <summary>Retention: удалить события старше отсечки. Возвращает число удалённых.</summary>
    int DeleteOlderThan(long cutoffUtcMs);
}
