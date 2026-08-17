using SCADA.Core.Tags;

namespace SCADA.Historian.Tests;

/// <summary>
/// Контракт чтения стора (docs/archive-format.md §13.1, §13.3): якорь левого
/// края тренда и агрегация по бакетам.
/// </summary>
public class ArchiveReadContractTests : IDisposable
{
    private const long BaseTime = 1_700_000_000_000L;

    private readonly string _root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public ArchiveReadContractTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* временный каталог */ }
    }

    private FileArchiveStore CreateStore(TagDataType dataType = TagDataType.Analog,
        LoggingMode mode = LoggingMode.Periodic, double scale = 1.0)
    {
        var store = new FileArchiveStore(_root);
        store.RegisterStream(1, new ArchiveStreamConfig(dataType, mode, scale, 0.0));
        return store;
    }

    [Fact]
    public async Task ReadAt_ReturnsLastPointNotAfterInstant_NotFirst()
    {
        var store = CreateStore();
        store.Write(1,
        [
            new ArchivePoint(BaseTime, 10.0, Quality.Good),
            new ArchivePoint(BaseTime + 1000, 20.0, Quality.Good),
            new ArchivePoint(BaseTime + 2000, 30.0, Quality.Good),
            new ArchivePoint(BaseTime + 5000, 50.0, Quality.Good)
        ]);
        await store.FlushAsync();

        var anchor = await store.ReadAtAsync(1, BaseTime + 3000, CancellationToken.None);

        Assert.NotNull(anchor);
        Assert.Equal(30.0, anchor!.Value.Value, precision: 10);
    }

    [Fact]
    public async Task ReadAt_SearchesBackAcrossMonths()
    {
        // OnChange-тег: последнее изменение случилось два месяца назад.
        // Запрос «что было вчера» обязан вернуть его, а не пустоту, иначе
        // левый край тренда нарисуется на пустом месте (§13.1).
        var store = CreateStore(TagDataType.Discrete, LoggingMode.OnChange);

        long june = new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        long august = new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        store.Write(1, [new ArchivePoint(june, 1.0, Quality.Good)]);
        await store.FlushAsync();

        var anchor = await store.ReadAtAsync(1, august, CancellationToken.None);

        Assert.NotNull(anchor);
        Assert.Equal(1.0, anchor!.Value.Value, precision: 10);
        Assert.Equal(june, anchor.Value.TimestampUtcMs);
    }

    [Fact]
    public async Task ReadAt_SeesOpenBlock()
    {
        var store = CreateStore();
        store.Write(1, [new ArchivePoint(BaseTime, 42.0, Quality.Good)]);

        // Без FlushAsync: точка ещё в открытом блоке, но читаться обязана.
        var anchor = await store.ReadAtAsync(1, BaseTime + 1000, CancellationToken.None);

        Assert.NotNull(anchor);
        Assert.Equal(42.0, anchor!.Value.Value, precision: 10);
    }

    [Fact]
    public async Task ReadAt_NoDataBefore_ReturnsNull()
    {
        var store = CreateStore();
        store.Write(1, [new ArchivePoint(BaseTime + 10_000, 1.0, Quality.Good)]);
        await store.FlushAsync();

        Assert.Null(await store.ReadAtAsync(1, BaseTime, CancellationToken.None));
    }

    [Fact]
    public async Task ReadBuckets_HeaderFastPath_MatchesPointByPoint()
    {
        // Бакет крупнее блока — агрегат берётся из заголовков; бакет мельче
        // блока — из разжатых точек. Оба пути обязаны дать одно и то же,
        // иначе тренд менялся бы от масштаба (§13.3, правило 2).
        var store = CreateStore(scale: 0.01);

        var points = new ArchivePoint[3000];
        for (int i = 0; i < points.Length; i++)
            points[i] = new ArchivePoint(BaseTime + i * 1000L, (7000 + i) * 0.01, Quality.Good);

        store.Write(1, points);
        await store.FlushAsync();

        long from = BaseTime;
        long to = BaseTime + points.Length * 1000L;

        var coarse = new ArchiveBucket[2];
        await store.ReadBucketsAsync(1, from, to, (to - from) / 2, coarse, CancellationToken.None);

        var fine = new ArchiveBucket[600];
        await store.ReadBucketsAsync(1, from, to, (to - from) / 600, fine, CancellationToken.None);

        double coarseMin = coarse.Where(b => b.Count > 0).Min(b => b.Min);
        double coarseMax = coarse.Where(b => b.Count > 0).Max(b => b.Max);
        double fineMin = fine.Where(b => b.Count > 0).Min(b => b.Min);
        double fineMax = fine.Where(b => b.Count > 0).Max(b => b.Max);

        Assert.Equal(fineMin, coarseMin, precision: 8);
        Assert.Equal(fineMax, coarseMax, precision: 8);
        Assert.Equal(points.Length, coarse.Sum(b => b.Count));
        Assert.Equal(points.Length, fine.Sum(b => b.Count));
    }

    [Fact]
    public async Task ReadBuckets_EmptyBucket_HasZeroCountAndNaN()
    {
        var store = CreateStore();
        store.Write(1,
        [
            new ArchivePoint(BaseTime, 5.0, Quality.Good),
            new ArchivePoint(BaseTime + 9000, 7.0, Quality.Good)
        ]);
        await store.FlushAsync();

        var buckets = new ArchiveBucket[10];
        await store.ReadBucketsAsync(1, BaseTime, BaseTime + 10_000, 1000, buckets, CancellationToken.None);

        // Пропуск обязан отличаться от «значение равно нулю» (§13.1).
        Assert.Equal(0, buckets[5].Count);
        Assert.True(double.IsNaN(buckets[5].Min));
        Assert.Equal(1, buckets[0].Count);
        Assert.Equal(5.0, buckets[0].Min, precision: 10);
    }

    [Fact]
    public async Task ReadBuckets_BadQuality_ExcludedFromAggregate()
    {
        var store = CreateStore();
        store.Write(1,
        [
            new ArchivePoint(BaseTime, 10.0, Quality.Good),
            new ArchivePoint(BaseTime + 100, 999.0, Quality.Bad),
            new ArchivePoint(BaseTime + 200, 20.0, Quality.Good)
        ]);
        await store.FlushAsync();

        var buckets = new ArchiveBucket[1];
        await store.ReadBucketsAsync(1, BaseTime, BaseTime + 1000, 1000, buckets, CancellationToken.None);

        // Значение при Bad — последнее известное, а не измерение (§6.2):
        // в min/max/среднее оно попадать не должно.
        Assert.Equal(3, buckets[0].Count);
        Assert.Equal(2, buckets[0].GoodCount);
        Assert.Equal(10.0, buckets[0].Min, precision: 10);
        Assert.Equal(20.0, buckets[0].Max, precision: 10);
        Assert.Equal(15.0, buckets[0].Avg, precision: 10);
    }

    [Fact]
    public async Task ReadRaw_SeesOpenBlockAfterClosedOnes()
    {
        var store = CreateStore();

        store.Write(1, [new ArchivePoint(BaseTime, 1.0, Quality.Good)]);
        await store.FlushAsync();
        store.Write(1, [new ArchivePoint(BaseTime + 1000, 2.0, Quality.Good)]);

        var buffer = new ArchivePoint[10];
        int count = await store.ReadRawAsync(1, 0, long.MaxValue, buffer, CancellationToken.None);

        Assert.Equal(2, count);
        Assert.Equal(1.0, buffer[0].Value, precision: 10);
        Assert.Equal(2.0, buffer[1].Value, precision: 10);
    }
}
