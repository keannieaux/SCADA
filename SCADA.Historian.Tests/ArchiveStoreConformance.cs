using SCADA.Core.Tags;

namespace SCADA.Historian.Tests;

/// <summary>
/// Conformance-набор шва <see cref="IArchiveStore"/> (docs/archive-format.md
/// §13.3, правило 4). Один набор правил, прогоняемый против каждой реализации.
/// </summary>
/// <remarks>
/// Смысл не в покрытии, а в том, чтобы контракт был записан отдельно от
/// механики. Пока реализация одна, любое её случайное поведение незаметно
/// становится частью контракта, и автор следующей реализации обнаруживает это
/// на отладке через год. Здесь проверяется только то, на что вправе
/// рассчитывать вызывающий, и ничего про формат файлов или сжатие.
/// </remarks>
public abstract class ArchiveStoreConformance : IDisposable
{
    protected const long BaseTime = 1_700_000_000_000L;

    protected static readonly ArchiveStreamConfig AnalogConfig =
        new(TagDataType.Analog, LoggingMode.Periodic, 0.01, 0.0);

    protected static readonly ArchiveStreamConfig DiscreteConfig =
        new(TagDataType.Discrete, LoggingMode.OnChange, 1.0, 0.0);

    /// <summary>Единственное, что различается между реализациями.</summary>
    protected abstract IArchiveStore CreateStore();

    public abstract void Dispose();

    private IArchiveStore NewStore(params (int StreamId, ArchiveStreamConfig Config)[] streams)
    {
        var store = CreateStore();
        foreach (var (streamId, config) in streams)
            store.RegisterStream(streamId, config);

        return store;
    }

    private IArchiveStore NewAnalogStore(int streamId = 1)
        => NewStore((streamId, AnalogConfig));

    private static ArchivePoint[] Series(int count, long stepMs = 1000, long startMs = BaseTime)
    {
        var points = new ArchivePoint[count];
        for (int i = 0; i < count; i++)
            points[i] = new ArchivePoint(startMs + i * stepMs, (7000 + i) * 0.01, Quality.Good);

        return points;
    }

    // --- регистрация ---

    [Fact]
    public async Task UnregisteredStream_IsRejectedOnWrite()
    {
        var store = CreateStore();

        Assert.Throws<InvalidOperationException>(
            () => store.Write(42, [new ArchivePoint(BaseTime, 1.0, Quality.Good)]));

        await Task.CompletedTask;
    }

    [Fact]
    public async Task UnregisteredStream_IsRejectedOnRead()
    {
        var store = CreateStore();
        var buffer = new ArchivePoint[4];

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.ReadRawAsync(42, 0, long.MaxValue, buffer, CancellationToken.None));
    }

    // --- запись и чтение ---

    [Fact]
    public async Task WrittenPoints_AreReadBackInOrder()
    {
        var store = NewAnalogStore();
        var written = Series(50);
        store.Write(1, written);
        await store.FlushAsync();

        var buffer = new ArchivePoint[100];
        int count = await store.ReadRawAsync(1, 0, long.MaxValue, buffer, CancellationToken.None);

        Assert.Equal(written.Length, count);
        for (int i = 0; i < count; i++)
        {
            Assert.Equal(written[i].TimestampUtcMs, buffer[i].TimestampUtcMs);
            Assert.Equal(written[i].Value, buffer[i].Value, precision: 8);
        }
    }

    [Fact]
    public async Task FreshData_IsVisibleBeforeFlush()
    {
        // Данные, ещё не дошедшие до постоянного хранилища, обязаны читаться:
        // иначе только что записанное пропадало бы из трендов на время
        // накопления блока — до часа (§13.3).
        var store = NewAnalogStore();
        store.Write(1, Series(3));

        var buffer = new ArchivePoint[10];
        int count = await store.ReadRawAsync(1, 0, long.MaxValue, buffer, CancellationToken.None);

        Assert.Equal(3, count);
    }

    [Fact]
    public async Task Flush_IsIdempotent()
    {
        var store = NewAnalogStore();
        store.Write(1, Series(5));

        await store.FlushAsync();
        await store.FlushAsync();

        var buffer = new ArchivePoint[10];
        Assert.Equal(5, await store.ReadRawAsync(1, 0, long.MaxValue, buffer, CancellationToken.None));
    }

    [Fact]
    public async Task WriteEmpty_IsNoOp()
    {
        var store = NewAnalogStore();
        store.Write(1, []);
        await store.FlushAsync();

        var buffer = new ArchivePoint[4];
        Assert.Equal(0, await store.ReadRawAsync(1, 0, long.MaxValue, buffer, CancellationToken.None));
    }

    [Fact]
    public async Task Streams_AreIsolated()
    {
        var store = NewStore((1, AnalogConfig), (2, AnalogConfig));
        store.Write(1, Series(5));
        store.Write(2, Series(3, startMs: BaseTime + 500));
        await store.FlushAsync();

        var buffer = new ArchivePoint[10];
        Assert.Equal(5, await store.ReadRawAsync(1, 0, long.MaxValue, buffer, CancellationToken.None));
        Assert.Equal(3, await store.ReadRawAsync(2, 0, long.MaxValue, buffer, CancellationToken.None));
    }

    // --- границы диапазона ---

    [Fact]
    public async Task Range_IsInclusiveOnBothEnds()
    {
        var store = NewAnalogStore();
        var points = Series(5);
        store.Write(1, points);
        await store.FlushAsync();

        var buffer = new ArchivePoint[10];
        int count = await store.ReadRawAsync(1,
            points[1].TimestampUtcMs, points[3].TimestampUtcMs, buffer, CancellationToken.None);

        Assert.Equal(3, count);
        Assert.Equal(points[1].TimestampUtcMs, buffer[0].TimestampUtcMs);
        Assert.Equal(points[3].TimestampUtcMs, buffer[2].TimestampUtcMs);
    }

    [Fact]
    public async Task RangeWithoutData_ReturnsZero()
    {
        var store = NewAnalogStore();
        store.Write(1, Series(5));
        await store.FlushAsync();

        var buffer = new ArchivePoint[10];
        int count = await store.ReadRawAsync(1,
            BaseTime + 1_000_000, BaseTime + 2_000_000, buffer, CancellationToken.None);

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task SmallDestination_IsNotOverflowed()
    {
        var store = NewAnalogStore();
        store.Write(1, Series(50));
        await store.FlushAsync();

        var buffer = new ArchivePoint[7];
        int count = await store.ReadRawAsync(1, 0, long.MaxValue, buffer, CancellationToken.None);

        // Буфер задаёт вызывающий, и переполнить его реализация не вправе.
        Assert.Equal(buffer.Length, count);
    }

    // --- качество ---

    [Fact]
    public async Task Quality_SurvivesRoundTrip()
    {
        var store = NewAnalogStore();
        store.Write(1,
        [
            new ArchivePoint(BaseTime, 70.0, Quality.Good),
            new ArchivePoint(BaseTime + 1000, 70.0, Quality.Bad),
            new ArchivePoint(BaseTime + 2000, 70.0, Quality.Uncertain)
        ]);
        await store.FlushAsync();

        var buffer = new ArchivePoint[10];
        await store.ReadRawAsync(1, 0, long.MaxValue, buffer, CancellationToken.None);

        Assert.Equal(Quality.Good, buffer[0].Quality);
        Assert.Equal(Quality.Bad, buffer[1].Quality);
        Assert.Equal(Quality.Uncertain, buffer[2].Quality);
    }

    // --- якорь ---

    [Fact]
    public async Task ReadAt_ReturnsLastPointNotAfterInstant()
    {
        var store = NewAnalogStore();
        var points = Series(5);
        store.Write(1, points);
        await store.FlushAsync();

        var anchor = await store.ReadAtAsync(1, points[2].TimestampUtcMs + 500, CancellationToken.None);

        Assert.NotNull(anchor);
        Assert.Equal(points[2].TimestampUtcMs, anchor!.Value.TimestampUtcMs);
    }

    [Fact]
    public async Task ReadAt_ExactTimestamp_ReturnsThatPoint()
    {
        var store = NewAnalogStore();
        var points = Series(5);
        store.Write(1, points);
        await store.FlushAsync();

        var anchor = await store.ReadAtAsync(1, points[3].TimestampUtcMs, CancellationToken.None);

        Assert.Equal(points[3].TimestampUtcMs, anchor!.Value.TimestampUtcMs);
    }

    [Fact]
    public async Task ReadAt_BeforeAnyData_ReturnsNull()
    {
        var store = NewAnalogStore();
        store.Write(1, Series(5));
        await store.FlushAsync();

        Assert.Null(await store.ReadAtAsync(1, BaseTime - 10_000, CancellationToken.None));
    }

    [Fact]
    public async Task ReadAt_LooksArbitrarilyFarBack()
    {
        // У тега в режиме OnChange последнее изменение бывает многодневной
        // давности — это штатный случай, а не отсутствие данных. Окном по
        // времени реализация ограничиваться не вправе (§13.1).
        var store = NewStore((1, DiscreteConfig));
        store.Write(1, [new ArchivePoint(BaseTime, 1.0, Quality.Good)]);
        await store.FlushAsync();

        var anchor = await store.ReadAtAsync(1, BaseTime + 90L * 86_400_000, CancellationToken.None);

        Assert.NotNull(anchor);
        Assert.Equal(BaseTime, anchor!.Value.TimestampUtcMs);
    }

    [Fact]
    public async Task ReadAt_SeesFreshDataBeforeFlush()
    {
        var store = NewAnalogStore();
        store.Write(1, [new ArchivePoint(BaseTime, 42.0, Quality.Good)]);

        var anchor = await store.ReadAtAsync(1, BaseTime + 1000, CancellationToken.None);

        Assert.NotNull(anchor);
        Assert.Equal(42.0, anchor!.Value.Value, precision: 8);
    }

    // --- бакеты ---

    [Fact]
    public async Task Buckets_LayoutIsContiguousAndStartsAtFrom()
    {
        var store = NewAnalogStore();
        store.Write(1, Series(10));
        await store.FlushAsync();

        var buckets = new ArchiveBucket[5];
        await store.ReadBucketsAsync(1, BaseTime, BaseTime + 10_000, 2000, buckets, CancellationToken.None);

        Assert.Equal(BaseTime, buckets[0].StartMs);
        for (int i = 0; i < buckets.Length; i++)
        {
            Assert.Equal(BaseTime + i * 2000, buckets[i].StartMs);
            Assert.Equal(buckets[i].StartMs + 2000, buckets[i].EndMs);
        }
    }

    [Fact]
    public async Task Buckets_AreNeverCoarserThanRequested()
    {
        // Вызывающий рассчитывает разметку по StartMs/EndMs: вернуть бакеты
        // крупнее запрошенных значит сломать ему шкалу (§13.3, правило 2).
        var store = NewAnalogStore();
        store.Write(1, Series(100));
        await store.FlushAsync();

        var buckets = new ArchiveBucket[20];
        await store.ReadBucketsAsync(1, BaseTime, BaseTime + 100_000, 5000, buckets, CancellationToken.None);

        Assert.All(buckets, b => Assert.Equal(5000, b.EndMs - b.StartMs));
    }

    [Fact]
    public async Task EmptyBucket_HasZeroCount()
    {
        var store = NewAnalogStore();
        store.Write(1,
        [
            new ArchivePoint(BaseTime, 5.0, Quality.Good),
            new ArchivePoint(BaseTime + 9000, 7.0, Quality.Good)
        ]);
        await store.FlushAsync();

        var buckets = new ArchiveBucket[10];
        await store.ReadBucketsAsync(1, BaseTime, BaseTime + 10_000, 1000, buckets, CancellationToken.None);

        // Пропуск обязан отличаться от «значение равно нулю» (§13.1).
        Assert.True(buckets[5].IsEmpty);
        Assert.Equal(0, buckets[5].Count);
        Assert.False(buckets[0].IsEmpty);
    }

    [Fact]
    public async Task Buckets_CountAllPoints_ButAggregateOnlyGood()
    {
        var store = NewAnalogStore();
        store.Write(1,
        [
            new ArchivePoint(BaseTime, 10.0, Quality.Good),
            new ArchivePoint(BaseTime + 100, 999.0, Quality.Bad),
            new ArchivePoint(BaseTime + 200, 20.0, Quality.Good)
        ]);
        await store.FlushAsync();

        var buckets = new ArchiveBucket[1];
        await store.ReadBucketsAsync(1, BaseTime, BaseTime + 1000, 1000, buckets, CancellationToken.None);

        // Значение при Bad — последнее известное, а не измерение (§6.2).
        Assert.Equal(3, buckets[0].Count);
        Assert.Equal(2, buckets[0].GoodCount);
        Assert.Equal(10.0, buckets[0].Min, precision: 8);
        Assert.Equal(20.0, buckets[0].Max, precision: 8);
        Assert.Equal(15.0, buckets[0].Avg, precision: 8);
    }

    [Fact]
    public async Task Buckets_MatchRawPoints()
    {
        // Агрегат обязан согласовываться с сырыми данными: расхождение здесь
        // означало бы, что тренд меняется от масштаба просмотра.
        var store = NewAnalogStore();
        var points = Series(200);
        store.Write(1, points);
        await store.FlushAsync();

        long from = BaseTime;
        long to = BaseTime + 200_000;

        var buckets = new ArchiveBucket[8];
        await store.ReadBucketsAsync(1, from, to, (to - from) / 8, buckets, CancellationToken.None);

        var raw = new ArchivePoint[300];
        int rawCount = await store.ReadRawAsync(1, from, to, raw, CancellationToken.None);

        Assert.Equal(rawCount, buckets.Sum(b => b.Count));
        Assert.Equal(raw.Take(rawCount).Min(p => p.Value),
            buckets.Where(b => !b.IsEmpty).Min(b => b.Min), precision: 6);
        Assert.Equal(raw.Take(rawCount).Max(p => p.Value),
            buckets.Where(b => !b.IsEmpty).Max(b => b.Max), precision: 6);
    }

    [Fact]
    public async Task Buckets_SeeFreshDataBeforeFlush()
    {
        var store = NewAnalogStore();
        store.Write(1, Series(5));

        var buckets = new ArchiveBucket[5];
        await store.ReadBucketsAsync(1, BaseTime, BaseTime + 5000, 1000, buckets, CancellationToken.None);

        Assert.Equal(5, buckets.Sum(b => b.Count));
    }

    // --- дискретные ---

    [Fact]
    public async Task DiscreteStream_RoundTrips()
    {
        var store = NewStore((1, DiscreteConfig));
        store.Write(1,
        [
            new ArchivePoint(BaseTime, 0.0, Quality.Good),
            new ArchivePoint(BaseTime + 1000, 1.0, Quality.Good),
            new ArchivePoint(BaseTime + 2000, 0.0, Quality.Good)
        ]);
        await store.FlushAsync();

        var buffer = new ArchivePoint[10];
        int count = await store.ReadRawAsync(1, 0, long.MaxValue, buffer, CancellationToken.None);

        Assert.Equal(3, count);
        Assert.Equal(0.0, buffer[0].Value, precision: 8);
        Assert.Equal(1.0, buffer[1].Value, precision: 8);
        Assert.Equal(0.0, buffer[2].Value, precision: 8);
    }

    // --- инварианты записи ---

    [Fact]
    public async Task NonMonotonicWrite_IsRejected()
    {
        // Монотонность — инвариант формата (§6.3), обеспечивать её обязан
        // конвейер. Стор нарушение не терпит: молча записанная неверная
        // последовательность даёт нечитаемый диапазон.
        var store = NewAnalogStore();
        store.Write(1, [new ArchivePoint(BaseTime + 5000, 1.0, Quality.Good)]);

        Assert.ThrowsAny<Exception>(() =>
        {
            store.Write(1, [new ArchivePoint(BaseTime, 2.0, Quality.Good)]);
            store.FlushAsync().AsTask().GetAwaiter().GetResult();
        });

        await Task.CompletedTask;
    }
}

/// <summary>Файловое хранилище: боевая реализация.</summary>
public sealed class FileArchiveStoreConformance : ArchiveStoreConformance
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _stores = [];

    protected override IArchiveStore CreateStore()
    {
        // Каждый экземпляр в своём подкаталоге: захват каталога исключительный.
        string root = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        var store = new FileArchiveStore(root, durable: false);
        _stores.Add(store);
        return store;
    }

    public override void Dispose()
    {
        foreach (var store in _stores)
            store.Dispose();

        try { Directory.Delete(_root, recursive: true); } catch { /* временный каталог */ }
    }
}

/// <summary>Хранилище в памяти: заглушка разработки и вторая реализация шва.</summary>
public sealed class InMemoryArchiveStoreConformance : ArchiveStoreConformance
{
    protected override IArchiveStore CreateStore() => new InMemoryArchiveStore();

    public override void Dispose() { }
}
