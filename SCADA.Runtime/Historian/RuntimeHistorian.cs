using SCADA.Core.Tags;
using SCADA.Historian;

namespace SCADA.Runtime.Historian;

/// <summary>
/// Фасад чтения истории: кольцо последнего часа + файловый архив.
/// Реализация IHistorian по контракту §13 archive-format.md.
/// </summary>
public sealed class RuntimeHistorian : IHistorian
{
    private readonly InMemoryHistorian _ring;
    private readonly IArchiveStore _store;
    private readonly IArchiveStreamRegistry _streamRegistry;
    private readonly long _ringCoverageMs;

    /// <summary>
    /// Полная связка: кольцо + архивное хранилище.
    /// </summary>
    public RuntimeHistorian(
        InMemoryHistorian ring,
        IArchiveStore store,
        IArchiveStreamRegistry streamRegistry,
        ProjectConfiguration config,
        int capacityPerTag = 36000)
    {
        _ring = ring;
        _store = store;
        _streamRegistry = streamRegistry;
        _ringCoverageMs = capacityPerTag * 100L; // приёмка: 100 мс между точками
        Initialize(config);
    }

    public ValueTask<HistoryResult> ReadRawAsync(
        TagId id, long fromMs, long toMs,
        Memory<TagValue> destination, CancellationToken ct = default)
    {
        if (destination.Length == 0)
            return new ValueTask<HistoryResult>(new HistoryResult(0, LoggingMode.Periodic, false));

        if (!TryGetStreamInfo(id, out var info))
            return new ValueTask<HistoryResult>(new HistoryResult(0, LoggingMode.Periodic, false));

        if (!info.IsArchived)
        {
            // неархивируемые теги в архиве не живут — только кольцо
            int count = _ring.Read(id, fromMs, toMs, destination.Span);
            return new ValueTask<HistoryResult>(new HistoryResult(count, info.Mode, false));
        }

        return ReadRawFromStoreAsync(info.StreamId, info.Mode, fromMs, toMs, destination, ct);
    }

    private async ValueTask<HistoryResult> ReadRawFromStoreAsync(
        int streamId, LoggingMode mode, long fromMs, long toMs,
        Memory<TagValue> destination, CancellationToken ct)
    {
        var archiveBuffer = new ArchivePoint[destination.Length];
        int read = await _store.ReadRawAsync(streamId, fromMs, toMs, archiveBuffer, ct);

        var destSpan = destination.Span;
        int written = 0;
        for (int i = 0; i < read; i++)
        {
            var p = archiveBuffer[i];
            destSpan[written++] = new TagValue(p.Value, p.TimestampUtcMs, p.Quality);
        }

        return new HistoryResult(written, mode, false);
    }

    public async ValueTask<HistoryResult> ReadBucketsAsync(
        TagId id, long fromMs, long toMs,
        Memory<ArchiveBucket> buckets, CancellationToken ct = default)
    {
        if (buckets.Length == 0)
            return new HistoryResult(0, LoggingMode.Periodic, false);

        if (!TryGetStreamInfo(id, out var info) || !info.IsArchived)
            return new HistoryResult(0, LoggingMode.Periodic, false);

        long bucketMs = (toMs - fromMs + buckets.Length - 1) / buckets.Length;
        if (bucketMs <= 0)
            bucketMs = 1;

        int filled = await _store.ReadBucketsAsync(info.StreamId, fromMs, toMs,
            bucketMs, buckets, ct);

        bool downsampled = filled > 0 && buckets.Length < (toMs - fromMs) / 1000;
        return new HistoryResult(filled, info.Mode, downsampled);
    }

    public ValueTask<TagValue?> ReadAtAsync(
        TagId id, long atMs, CancellationToken ct = default)
    {
        if (!TryGetStreamInfo(id, out var info) || !info.IsArchived)
            return new ValueTask<TagValue?>((TagValue?)null);

        // Поиск назад ведёт стор: только он знает раскладку файлов и может
        // уйти на нужную глубину. Окном по времени тут ограничиваться нельзя —
        // у OnChange-тега последнее изменение бывает многодневной давности,
        // и это штатный случай, а не отсутствие данных (§13.1).
        return ReadAtFromStoreAsync(info.StreamId, atMs, ct);
    }

    private async ValueTask<TagValue?> ReadAtFromStoreAsync(int streamId, long atMs, CancellationToken ct)
    {
        ArchivePoint? point = await _store.ReadAtAsync(streamId, atMs, ct);
        if (!point.HasValue)
            return null;

        var p = point.Value;
        return new TagValue(p.Value, p.TimestampUtcMs, p.Quality);
    }

    public int ReadRecent(TagId id, Span<TagValue> destination)
    {
        return _ring.ReadRecent(id, destination);
    }

    private readonly record struct StreamInfo(int StreamId, LoggingMode Mode, bool IsArchived);

    private readonly Dictionary<TagId, StreamInfo> _infos = new();

    private void Initialize(ProjectConfiguration config)
    {
        foreach (var tag in config.Tags)
        {
            var mode = LoggingModeHelper.Infer(tag.Logging);

            // Поток заводится только под архивируемый тег. Иначе реестр —
            // список того, что лежит в архиве, — врал бы о своём содержимом,
            // а идентификаторы расходовались бы на теги, данных по которым
            // не будет никогда. Неархивируемый тег живёт только в кольце.
            int streamId = tag.IsArchived
                ? _streamRegistry.Resolve(tag.Name, tag.DataType)
                : 0;

            _infos[tag.Id] = new StreamInfo(streamId, mode, tag.IsArchived);
        }
    }

    private bool TryGetStreamInfo(TagId id, out StreamInfo info)
    {
        return _infos.TryGetValue(id, out info);
    }
}
