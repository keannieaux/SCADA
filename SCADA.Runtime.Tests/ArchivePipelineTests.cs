using SCADA.Core.Devices;
using SCADA.Core.Tags;
using SCADA.Historian;
using SCADA.Runtime.Historian;
using TagTableImpl = SCADA.Runtime.TagTable.TagTable;

namespace SCADA.Runtime.Tests;

public class ArchivePipelineTests : IDisposable
{
    private readonly string _storeRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public ArchivePipelineTests() => Directory.CreateDirectory(_storeRoot);

    public void Dispose()
    {
        try { Directory.Delete(_storeRoot, recursive: true); } catch { }
    }

    [Fact]
    public async Task Periodic_ArchivedTag_WritesAtConfiguredInterval()
    {
        var table = new TagTableImpl(1);
        var config = CreateConfig(new TagLoggingConfiguration
        {
            Interval = TimeSpan.FromSeconds(1)
        });
        var store = new FileArchiveStore(_storeRoot);
        var registry = new ArchiveStreamRegistry(_storeRoot);
        var pipeline = new ArchivePipeline(table, store, registry, config, new ArchivePipelineOptions
        {
            TickInterval = TimeSpan.FromMilliseconds(10),
            DefaultInterval = TimeSpan.FromSeconds(1)
        });

        long baseTime = 1_700_000_000_000L;
        table.Write(new TagId(0), new TagValue(10.0, baseTime, Quality.Good));

        pipeline.ProcessTick();
        pipeline.FlushPending();

        // 500 мс — ещё рано
        long t1 = baseTime + 500;
        table.Write(new TagId(0), new TagValue(11.0, t1, Quality.Good));
        pipeline.ProcessTick();
        pipeline.FlushPending();

        // 1 с после первой — время записи
        long t2 = baseTime + 1000;
        table.Write(new TagId(0), new TagValue(12.0, t2, Quality.Good));
        pipeline.ProcessTick();
        pipeline.FlushPending();

        int streamId = registry.Resolve("T0", TagDataType.Analog);
        var buffer = new ArchivePoint[10];
        int count = await store.ReadRawAsync(streamId, 0, long.MaxValue, buffer, CancellationToken.None);

        Assert.Equal(2, count);
        Assert.Equal(10.0, buffer[0].Value, precision: 10);
        Assert.Equal(baseTime, buffer[0].TimestampUtcMs);
        Assert.Equal(12.0, buffer[1].Value, precision: 10);
        Assert.Equal(t2, buffer[1].TimestampUtcMs);
    }

    [Fact]
    public async Task OnChange_ArchivedTag_WritesOnlyWhenValueChanges()
    {
        var table = new TagTableImpl(1);
        var config = CreateConfig(new TagLoggingConfiguration { LogOnChange = true });
        var store = new FileArchiveStore(_storeRoot);
        var registry = new ArchiveStreamRegistry(_storeRoot);
        var pipeline = new ArchivePipeline(table, store, registry, config);

        long baseTime = 1_700_000_000_000L;
        table.Write(new TagId(0), new TagValue(10.0, baseTime, Quality.Good));
        pipeline.ProcessTick();
        pipeline.FlushPending();

        // то же значение — не пишем
        long t1 = baseTime + 100;
        table.Write(new TagId(0), new TagValue(10.0, t1, Quality.Good));
        pipeline.ProcessTick();
        pipeline.FlushPending();

        // изменилось — пишем
        long t2 = baseTime + 200;
        table.Write(new TagId(0), new TagValue(20.0, t2, Quality.Good));
        pipeline.ProcessTick();
        pipeline.FlushPending();

        int streamId = registry.Resolve("T0", TagDataType.Analog);
        var buffer = new ArchivePoint[10];
        int count = await store.ReadRawAsync(streamId, 0, long.MaxValue, buffer, CancellationToken.None);

        Assert.Equal(2, count);
        Assert.Equal(10.0, buffer[0].Value, precision: 10);
        Assert.Equal(20.0, buffer[1].Value, precision: 10);
    }

    [Fact]
    public async Task QualityTransition_WritesOneBadPointThenSkipsUntilRecovery()
    {
        var table = new TagTableImpl(1);
        var config = CreateConfig(new TagLoggingConfiguration
        {
            Interval = TimeSpan.FromSeconds(1)
        });
        var store = new FileArchiveStore(_storeRoot);
        var registry = new ArchiveStreamRegistry(_storeRoot);
        var pipeline = new ArchivePipeline(table, store, registry, config);

        long baseTime = 1_700_000_000_000L;
        table.Write(new TagId(0), new TagValue(10.0, baseTime, Quality.Good));
        pipeline.ProcessTick();
        pipeline.FlushPending();

        // переход Good → Bad, одна точка
        long t1 = baseTime + 500;
        table.Write(new TagId(0), new TagValue(10.0, t1, Quality.Bad));
        pipeline.ProcessTick();
        pipeline.FlushPending();

        // Bad дальше — пропускаем
        long t2 = baseTime + 600;
        table.Write(new TagId(0), new TagValue(10.0, t2, Quality.Bad));
        pipeline.ProcessTick();
        pipeline.FlushPending();

        // восстановление Good — пишем
        long t3 = baseTime + 1500;
        table.Write(new TagId(0), new TagValue(11.0, t3, Quality.Good));
        pipeline.ProcessTick();
        pipeline.FlushPending();

        int streamId = registry.Resolve("T0", TagDataType.Analog);
        var buffer = new ArchivePoint[10];
        int count = await store.ReadRawAsync(streamId, 0, long.MaxValue, buffer, CancellationToken.None);

        Assert.Equal(3, count);
        Assert.Equal(Quality.Good, buffer[0].Quality);
        Assert.Equal(Quality.Bad, buffer[1].Quality);
        Assert.Equal(Quality.Good, buffer[2].Quality);
    }

    [Fact]
    public async Task NonMonotonicTimestamp_IsDropped_AndCounterIncrements()
    {
        var table = new TagTableImpl(1);
        var config = CreateConfig(new TagLoggingConfiguration { LogOnChange = true });
        var store = new FileArchiveStore(_storeRoot);
        var registry = new ArchiveStreamRegistry(_storeRoot);
        var pipeline = new ArchivePipeline(table, store, registry, config);

        long baseTime = 1_700_000_000_000L;
        table.Write(new TagId(0), new TagValue(10.0, baseTime, Quality.Good));
        pipeline.ProcessTick();
        pipeline.FlushPending();

        long earlier = baseTime - 1000;
        table.Write(new TagId(0), new TagValue(20.0, earlier, Quality.Good));
        pipeline.ProcessTick();
        pipeline.FlushPending();

        Assert.Equal(1, pipeline.DroppedNonMonotonicCount);

        int streamId = registry.Resolve("T0", TagDataType.Analog);
        var buffer = new ArchivePoint[10];
        int count = await store.ReadRawAsync(streamId, 0, long.MaxValue, buffer, CancellationToken.None);

        Assert.Equal(1, count);
        Assert.Equal(10.0, buffer[0].Value, precision: 10);
    }

    private static ProjectConfiguration CreateConfig(TagLoggingConfiguration logging)
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
                    IsArchived = true,
                    ScaleFactor = 1.0,
                    ScaleOffset = 0.0,
                    Logging = logging
                }
            ]
        };
    }
}
