namespace SCADA.Historian;

/// <summary>
/// Итог прохода ротации (docs/archive-format.md §15).
/// Возвращается вызывающему, а не пишется в лог изнутри: хранилище не знает
/// про журналирование, а факт удаления данных обязан быть виден снаружи —
/// тихая потеря недопустима.
/// </summary>
public readonly record struct RetentionReport(
    int DeletedFiles,
    long FreedBytes,
    int SkippedByFloor,
    int MonthsRemoved,
    long OldestRemainingUtcMs)
{
    public static RetentionReport Empty => new(0, 0, 0, 0, 0);

    public bool AnythingDeleted => DeletedFiles > 0;

    /// <summary>
    /// Сколько потоков не удалось ужать из-за пола MinRetentionDays.
    /// Ненулевое значение при нехватке места означает, что освобождать больше
    /// нечего и запись придётся остановить (ТЗ §8.9).
    /// </summary>
    public bool HitFloor => SkippedByFloor > 0;
}
