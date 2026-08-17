using SCADA.Core.Tags;

namespace SCADA.Historian;

/// <summary>
/// Хранилище архива в памяти (docs/archive-format.md §13.3).
/// </summary>
/// <remarks>
/// Две роли. Первая: заглушка для разработки трендов до появления файлов
/// (ТЗ §16.4) — в отличие от пустышки, отдаёт настоящие данные. Вторая, более
/// важная: вторая реализация шва. Абстракция с единственной реализацией через
/// год оказывается формой этой реализации, и добавление третьей превращается
/// в археологию; conformance-набор прогоняется на обеих и не даёт шву
/// выродиться.
///
/// Данные не сжимаются и не ограничены по объёму: назначение — тесты и
/// разработка, а не работа на объекте.
/// </remarks>
public sealed class InMemoryArchiveStore : IArchiveStore
{
    private readonly Dictionary<int, ArchiveStreamConfig> _streams = [];
    private readonly Dictionary<int, List<ArchivePoint>> _points = [];
    private readonly Lock _sync = new();

    public StoreCapabilities Capabilities { get; } = StoreCapabilities.RawRead;

    public void RegisterStream(int streamId, ArchiveStreamConfig config)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(streamId, 1);

        lock (_sync)
        {
            _streams[streamId] = config;
            _points.TryAdd(streamId, []);
        }
    }

    public void Write(int streamId, ReadOnlySpan<ArchivePoint> points)
    {
        if (points.Length == 0)
            return;

        lock (_sync)
        {
            var stream = GetStream(streamId);

            foreach (var point in points)
            {
                // Тот же инвариант, что у файлового стора (§6.3): метки строго
                // возрастают. Нарушение — ошибка вызывающего, а не данных.
                if (stream.Count > 0 && point.TimestampUtcMs <= stream[^1].TimestampUtcMs)
                {
                    throw new InvalidDataException(
                        $"Метки времени потока {streamId} должны строго возрастать: " +
                        $"{point.TimestampUtcMs} <= {stream[^1].TimestampUtcMs}.");
                }

                stream.Add(point);
            }
        }
    }

    public ValueTask<int> ReadRawAsync(int streamId, long fromMs, long toMs,
        Memory<ArchivePoint> destination, CancellationToken ct)
    {
        lock (_sync)
        {
            var stream = GetStream(streamId);
            var span = destination.Span;
            int written = 0;

            foreach (var point in stream)
            {
                if (point.TimestampUtcMs < fromMs || point.TimestampUtcMs > toMs)
                    continue;

                if (written >= span.Length)
                    break;

                span[written++] = point;
            }

            return new ValueTask<int>(written);
        }
    }

    public ValueTask<int> ReadBucketsAsync(int streamId, long fromMs, long toMs,
        long bucketMs, Memory<ArchiveBucket> destination, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bucketMs);

        lock (_sync)
        {
            var stream = GetStream(streamId);
            var span = destination.Span;

            for (int i = 0; i < span.Length; i++)
            {
                long start = fromMs + i * bucketMs;
                span[i] = new ArchiveBucket(start, start + bucketMs,
                    double.NaN, double.NaN, double.NaN, 0, 0);
            }

            int lastFilled = -1;
            foreach (var point in stream)
            {
                if (point.TimestampUtcMs < fromMs || point.TimestampUtcMs > toMs)
                    continue;

                int index = (int)((point.TimestampUtcMs - fromMs) / bucketMs);
                if (index < 0 || index >= span.Length)
                    continue;

                lastFilled = Math.Max(lastFilled, index);
                span[index] = Merge(span[index], point);
            }

            return new ValueTask<int>(lastFilled + 1);
        }
    }

    public ValueTask<ArchivePoint?> ReadAtAsync(int streamId, long atMs, CancellationToken ct)
    {
        lock (_sync)
        {
            var stream = GetStream(streamId);

            for (int i = stream.Count - 1; i >= 0; i--)
            {
                if (stream[i].TimestampUtcMs <= atMs)
                    return new ValueTask<ArchivePoint?>(stream[i]);
            }

            return new ValueTask<ArchivePoint?>((ArchivePoint?)null);
        }
    }

    /// <summary>Данные и так в памяти: сбрасывать нечего, но контракт обязан работать.</summary>
    public ValueTask FlushAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    /// <summary>
    /// Правила сложения повторяют файловый стор: Count считает все отсчёты,
    /// агрегаты — только достоверные (§6.2, §8.5).
    /// </summary>
    private static ArchiveBucket Merge(ArchiveBucket bucket, ArchivePoint point)
    {
        int count = bucket.Count + 1;

        if (point.Quality != Quality.Good)
            return bucket with { Count = count };

        int goodCount = bucket.GoodCount + 1;

        if (bucket.GoodCount == 0)
        {
            return new ArchiveBucket(bucket.StartMs, bucket.EndMs,
                point.Value, point.Value, point.Value, count, goodCount);
        }

        double avg = (bucket.Avg * bucket.GoodCount + point.Value) / goodCount;
        return new ArchiveBucket(bucket.StartMs, bucket.EndMs,
            Math.Min(bucket.Min, point.Value),
            Math.Max(bucket.Max, point.Value),
            avg, count, goodCount);
    }

    private List<ArchivePoint> GetStream(int streamId)
    {
        if (!_points.TryGetValue(streamId, out var stream))
            throw new InvalidOperationException($"Поток {streamId} не зарегистрирован в архиве");

        return stream;
    }
}
