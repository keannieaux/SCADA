using SCADA.Core.Tags;
using SCADA.Historian;

namespace SCADA.Historian.Tests;

public class FileArchiveStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public FileArchiveStoreTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task Write_And_ReadRaw_RoundTrip()
    {
        var store = new FileArchiveStore(_root);
        store.RegisterStream(1, new ArchiveStreamConfig(TagDataType.Analog, LoggingMode.Periodic, 0.1, 0.0));

        var points = new ArchivePoint[100];
        long baseTime = 1_700_000_000_000L;
        for (int i = 0; i < points.Length; i++)
            points[i] = new ArchivePoint(baseTime + i * 1000, i * 0.1, Quality.Good);

        store.Write(1, points);

        var buffer = new ArchivePoint[200];
        int count = await store.ReadRawAsync(1, baseTime, baseTime + 99_000, buffer, CancellationToken.None);

        Assert.Equal(100, count);
        for (int i = 0; i < count; i++)
        {
            Assert.Equal(points[i].TimestampUtcMs, buffer[i].TimestampUtcMs);
            Assert.Equal(points[i].Value, buffer[i].Value, precision: 10);
        }
    }

    [Fact]
    public async Task Write_CreatesFileWithHeader()
    {
        var store = new FileArchiveStore(_root);
        store.RegisterStream(5, new ArchiveStreamConfig(TagDataType.Analog, LoggingMode.Periodic, 1.0, 0.0));

        var points = new[]
        {
            new ArchivePoint(1_700_000_000_000L, 1.0, Quality.Good),
            new ArchivePoint(1_700_000_001_000L, 2.0, Quality.Good)
        };

        store.Write(5, points);
        await store.FlushAsync(CancellationToken.None);

        string monthDir = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000L).UtcDateTime.ToString("yyyy-MM");
        string path = Path.Combine(_root, monthDir, "000005.dat");
        Assert.True(File.Exists(path));

        byte[] file = File.ReadAllBytes(path);
        Assert.True(file.Length >= 16);
        Assert.Equal((byte)'S', file[0]);
        Assert.Equal((byte)'C', file[1]);
        Assert.Equal((byte)'A', file[2]);
        Assert.Equal((byte)'R', file[3]);
    }

    [Fact]
    public async Task Write_SplitsByMonth()
    {
        var store = new FileArchiveStore(_root);
        store.RegisterStream(2, new ArchiveStreamConfig(TagDataType.Analog, LoggingMode.Periodic, 1.0, 0.0));

        var june = new DateTime(2026, 6, 30, 23, 59, 0, DateTimeKind.Utc);
        var july = new DateTime(2026, 7, 1, 0, 1, 0, DateTimeKind.Utc);
        long juneMs = new DateTimeOffset(june).ToUnixTimeMilliseconds();
        long julyMs = new DateTimeOffset(july).ToUnixTimeMilliseconds();

        var points = new[]
        {
            new ArchivePoint(juneMs, 100.0, Quality.Good),
            new ArchivePoint(julyMs, 200.0, Quality.Good)
        };

        store.Write(2, points);
        await store.FlushAsync(CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(_root, "2026-06", "000002.dat")));
        Assert.True(File.Exists(Path.Combine(_root, "2026-07", "000002.dat")));
    }

    [Fact]
    public async Task Write_SplitsIntoMultipleBlocks_ByCount()
    {
        var store = new FileArchiveStore(_root, blockTimeout: TimeSpan.FromHours(1));
        store.RegisterStream(3, new ArchiveStreamConfig(TagDataType.Analog, LoggingMode.Periodic, 1.0, 0.0));

        // 8192 точек → должно разбиться минимум на 2 блока
        var points = new ArchivePoint[8192];
        long baseTime = 1_700_000_000_000L;
        for (int i = 0; i < points.Length; i++)
            points[i] = new ArchivePoint(baseTime + i * 1000, i, Quality.Good);

        store.Write(3, points);

        var buffer = new ArchivePoint[8192];
        int count = await store.ReadRawAsync(3, baseTime, baseTime + (points.Length - 1) * 1000L,
            buffer, CancellationToken.None);

        Assert.Equal(points.Length, count);
        for (int i = 0; i < count; i++)
            Assert.Equal(points[i].Value, buffer[i].Value, precision: 10);
    }

    [Fact]
    public async Task ReadBuckets_ReturnsAggregates()
    {
        var store = new FileArchiveStore(_root);
        store.RegisterStream(4, new ArchiveStreamConfig(TagDataType.Analog, LoggingMode.Periodic, 1.0, 0.0));

        long baseTime = 1_700_000_000_000L;
        var points = new ArchivePoint[20];
        for (int i = 0; i < points.Length; i++)
            points[i] = new ArchivePoint(baseTime + i * 1000, i, Quality.Good);

        store.Write(4, points);

        var buckets = new ArchiveBucket[4]; // по 5000 мс каждый
        int count = await store.ReadBucketsAsync(4, baseTime, baseTime + 20_000, 5000,
            buckets, CancellationToken.None);

        Assert.Equal(4, count);
        Assert.Equal(0, buckets[0].Min, precision: 5);
        Assert.Equal(4, buckets[0].Max, precision: 5);
        Assert.Equal(2, buckets[0].Avg, precision: 5);
        Assert.Equal(5, buckets[0].Count);

        Assert.Equal(15, buckets[3].Min, precision: 5);
        Assert.Equal(19, buckets[3].Max, precision: 5);
    }
}
