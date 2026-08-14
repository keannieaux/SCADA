using SCADA.Core.Tags;
namespace SCADA.Historian;

/// <summary>
/// Шов хранилища архива (docs/archive-format.md §13.3).
/// Все операции ниже IHistorian работают с streamId, а не TagId.
/// </summary>
public interface IArchiveStore
{
    /// <summary>Регистрирует поток: без регистрации Write не принимает данные.</summary>
    void RegisterStream(int streamId, ArchiveStreamConfig config);

    /// <summary>Записать отсчёты в поток. Может попасть в несколько файлов месяцев.</summary>
    void Write(int streamId, ReadOnlySpan<ArchivePoint> points);

    /// <summary>Сырые точки диапазона [fromMs, toMs]. Возвращает число записанных.</summary>
    ValueTask<int> ReadRawAsync(int streamId, long fromMs, long toMs,
                                Memory<ArchivePoint> destination, CancellationToken ct);

    /// <summary>Агрегаты по бакетам bucketMs длиной. Возвращает число заполненных бакетов.</summary>
    ValueTask<int> ReadBucketsAsync(int streamId, long fromMs, long toMs,
                                    long bucketMs, Memory<ArchiveBucket> destination,
                                    CancellationToken ct);

    /// <summary>
    /// Последняя точка потока не позже atMs, либо null если таких нет.
    /// Якорь левого края тренда (§13.1). Реализация обязана искать назад по
    /// всем имеющимся данным, а не в фиксированном окне: у тега в режиме
    /// OnChange последнее изменение может быть многодневной давности, и это
    /// штатный случай, а не отсутствие данных.
    /// </summary>
    ValueTask<ArchivePoint?> ReadAtAsync(int streamId, long atMs, CancellationToken ct);

    /// <summary>Принудительно закрыть все открытые блоки и сбросить их на диск.</summary>
    ValueTask FlushAsync(CancellationToken ct = default);

    StoreCapabilities Capabilities { get; }
}
