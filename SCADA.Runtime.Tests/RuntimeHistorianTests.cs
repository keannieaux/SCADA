using SCADA.Core.Devices;
using SCADA.Core.Tags;
using SCADA.Historian;
using SCADA.Runtime.Historian;
using TagTableImpl = SCADA.Runtime.TagTable.TagTable;

namespace SCADA.Runtime.Tests;

public class RuntimeHistorianTests : IDisposable
{
    private readonly string _storeRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public RuntimeHistorianTests() => Directory.CreateDirectory(_storeRoot);

    public void Dispose()
    {
        try { Directory.Delete(_storeRoot, recursive: true); } catch { }
    }

    [Fact]
    public async Task ReadRawAsync_ReturnsArchivedPointFromStore()
    {
        var table = new TagTableImpl(1);
        var config = CreateConfig(isArchived: true);
        var store = new FileArchiveStore(_storeRoot);
        var registry = new ArchiveStreamRegistry(_storeRoot);
        var pipeline = new ArchivePipeline(table, store, registry, config);
        var ring = new InMemoryHistorian(1, 36000, table);
        var historian = new RuntimeHistorian(ring, store, registry, config);

        await ring.StartAsync();

        long t0 = 1_700_000_000_000L;
        table.Write(new TagId(0), new TagValue(42.0, t0, Quality.Good));

        pipeline.ProcessTick();
        pipeline.FlushPending();

        await Task.Delay(100);
        await ring.StopAsync();

        var buffer = new TagValue[10];
        var result = await historian.ReadRawAsync(new TagId(0), 0, long.MaxValue, buffer);

        Assert.Equal(1, result.Count);
        Assert.Equal(LoggingMode.Periodic, result.Mode);
        Assert.Equal(42.0, buffer[0].Value, precision: 10);
        Assert.Equal(t0, buffer[0].TimeStampUtc);
        Assert.Equal(Quality.Good, buffer[0].Quality);
    }

    [Fact]
    public async Task ReadAtAsync_ReturnsValueAtOrBeforeTimestamp()
    {
        var table = new TagTableImpl(1);
        var config = CreateConfig(isArchived: true);
        var store = new FileArchiveStore(_storeRoot);
        var registry = new ArchiveStreamRegistry(_storeRoot);
        var pipeline = new ArchivePipeline(table, store, registry, config);
        var ring = new InMemoryHistorian(1, 36000, table);
        var historian = new RuntimeHistorian(ring, store, registry, config);

        await ring.StartAsync();

        long t0 = 1_700_000_000_000L;
        table.Write(new TagId(0), new TagValue(10.0, t0, Quality.Good));
        pipeline.ProcessTick();
        pipeline.FlushPending();

        table.Write(new TagId(0), new TagValue(20.0, t0 + 2000, Quality.Good));
        pipeline.ProcessTick();
        pipeline.FlushPending();

        await Task.Delay(100);
        await ring.StopAsync();

        var at = await historian.ReadAtAsync(new TagId(0), t0 + 1000);

        Assert.NotNull(at);
        Assert.Equal(10.0, at.Value.Value, precision: 10);
        Assert.Equal(t0, at.Value.TimeStampUtc);
    }

    [Fact]
    public void ReadRecent_ReturnsLatestValuesFromRing()
    {
        var table = new TagTableImpl(1);
        var config = CreateConfig(isArchived: false);
        var ring = new InMemoryHistorian(1, capacityPerTag: 5);
        var registry = new ArchiveStreamRegistry(_storeRoot);
        var historian = new RuntimeHistorian(ring, new EmptyArchiveStore(), registry, config, capacityPerTag: 5);

        long t0 = 1_700_000_000_000L;
        for (int i = 0; i < 10; i++)
            ring.Append(new TagId(0), new TagValue(i, t0 + i * 1000, Quality.Good));

        Span<TagValue> buffer = new TagValue[3];
        int count = historian.ReadRecent(new TagId(0), buffer);

        Assert.Equal(3, count);
        Assert.Equal(7.0, buffer[0].Value, precision: 10);
        Assert.Equal(9.0, buffer[2].Value, precision: 10);
    }

    [Fact]
    public async Task NonArchivedTag_ReadRawAsync_ReturnsZero()
    {
        var table = new TagTableImpl(1);
        var config = CreateConfig(isArchived: false);
        var store = new FileArchiveStore(_storeRoot);
        var registry = new ArchiveStreamRegistry(_storeRoot);
        var ring = new InMemoryHistorian(1, 36000, table);
        var historian = new RuntimeHistorian(ring, store, registry, config);

        long t0 = 1_700_000_000_000L;
        table.Write(new TagId(0), new TagValue(99.0, t0, Quality.Good));
        await Task.Delay(50);

        var buffer = new TagValue[10];
        var result = await historian.ReadRawAsync(new TagId(0), 0, long.MaxValue, buffer);

        Assert.Equal(0, result.Count);
    }

    private static ProjectConfiguration CreateConfig(bool isArchived)
    {
        return new ProjectConfiguration
        {
            Name = "Test",
            Tags =
            [
                new TagDefinition
                {
                    Id = new TagId(0),
                    Name = "T0",
                    DataType = TagDataType.Analog,
                    DeviceId = new DeviceId(0),
                    IsArchived = isArchived,
                    ScaleFactor = 1.0,
                    ScaleOffset = 0.0,
                    Logging = new TagLoggingConfiguration { Interval = TimeSpan.FromSeconds(1) }
                }
            ]
        };
    }
}

/// <summary>
/// Заглушка IArchiveStore, которая всегда возвращает 0.
/// </summary>
public sealed class EmptyArchiveStore : IArchiveStore
{
    public StoreCapabilities Capabilities => StoreCapabilities.RawRead;
    public void RegisterStream(int streamId, ArchiveStreamConfig config) { }
    public void Write(int streamId, ReadOnlySpan<ArchivePoint> points) { }
    public ValueTask FlushAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask<int> ReadRawAsync(int streamId, long fromMs, long toMs, Memory<ArchivePoint> destination, CancellationToken ct)
        => new ValueTask<int>(0);
    public ValueTask<int> ReadBucketsAsync(int streamId, long fromMs, long toMs, long bucketMs, Memory<ArchiveBucket> buckets, CancellationToken ct)
        => new ValueTask<int>(0);
    public ValueTask<ArchivePoint?> ReadAtAsync(int streamId, long atMs, CancellationToken ct)
        => new ValueTask<ArchivePoint?>((ArchivePoint?)null);
}
