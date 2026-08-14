using SCADA.Core.Tags;
using SCADA.Historian;

namespace SCADA.Runtime.Historian;

/// <summary>
/// Фасад чтения истории (docs/archive-format.md §13.1).
/// Записи в интерфейсе нет намеренно: конвейер архива тянет данные
/// из TagTable сам, публичного способа дописать в обход правил нет.
/// </summary>
public interface IHistorian
{
    /// <summary>Сырые точки диапазона. Свежие — из кольца, остальные с диска.</summary>
    ValueTask<HistoryResult> ReadRawAsync(
        TagId id, long fromMs, long toMs,
        Memory<TagValue> destination, CancellationToken ct = default);

    /// <summary>Агрегаты. Запросил N бакетов — дал не больше N.</summary>
    ValueTask<HistoryResult> ReadBucketsAsync(
        TagId id, long fromMs, long toMs,
        Memory<ArchiveBucket> buckets, CancellationToken ct = default);

    /// <summary>Последнее значение не позже atMs. Левый край тренда.</summary>
    ValueTask<TagValue?> ReadAtAsync(
        TagId id, long atMs, CancellationToken ct = default);

    /// <summary>Последние N из кольца: без диска, без await.</summary>
    int ReadRecent(TagId id, Span<TagValue> destination);
}
