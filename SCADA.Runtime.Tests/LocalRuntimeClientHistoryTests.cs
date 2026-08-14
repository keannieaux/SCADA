using SCADA.Core.Devices;
using SCADA.Core.Tags;
using SCADA.Historian;
using SCADA.Runtime.Historian;
using SCADA.Runtime.Runtime;
using TagTableImpl = SCADA.Runtime.TagTable.TagTable;

namespace SCADA.Runtime.Tests;

/// <summary>
/// Контракт UI ↔ ядро в части истории (docs/archive-format.md §13.2).
/// Против него параллельно пишется код трендов, поэтому проверяются не только
/// значения, но и то, что читатель получает вместе с ними: режим логирования
/// и признак прореживания.
/// </summary>
public class LocalRuntimeClientHistoryTests : IDisposable
{
    private const long BaseTime = 1_700_000_000_000L;

    private readonly string _root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    private FileArchiveStore? _store;

    public LocalRuntimeClientHistoryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        _store?.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* временный каталог */ }
    }

    private (LocalRuntimeClient Client, TagTableImpl Table, ArchivePipeline Pipeline) Build(
        HistoryQueryLimits? limits = null)
    {
        var config = CreateConfig();
        var table = new TagTableImpl(config.Tags.Count);
        var registry = new ArchiveStreamRegistry(_root);

        _store = new FileArchiveStore(_root);
        var pipeline = new ArchivePipeline(table, _store, registry, config,
            new ArchivePipelineOptions { DefaultInterval = TimeSpan.FromSeconds(1) });

        var ring = new InMemoryHistorian(config.Tags.Count);
        var historian = new RuntimeHistorian(ring, _store, registry, config);

        return (new LocalRuntimeClient(table, historian, limits), table, pipeline);
    }

    private static void Feed(TagTableImpl table, ArchivePipeline pipeline,
        TagId id, double value, long timestampMs, Quality quality = Quality.Good)
    {
        table.Write(id, new TagValue(value, timestampMs, quality));
        pipeline.ProcessTick();
        pipeline.FlushPending();
    }

    [Fact]
    public async Task ReadHistory_ReturnsSeriesPerTag_WithLoggingMode()
    {
        var (client, table, pipeline) = Build();

        for (int i = 0; i < 5; i++)
        {
            Feed(table, pipeline, new TagId(0), 70.0 + i, BaseTime + i * 1000L);
            Feed(table, pipeline, new TagId(1), i % 2, BaseTime + i * 1000L);
        }

        var series = await client.ReadHistoryAsync(
            [new TagId(0), new TagId(1)], BaseTime, BaseTime + 10_000, maxPointsPerTag: 100);

        Assert.Equal(2, series.Length);

        // Режим обязан приходить вместе с данными: при Periodic пропуск —
        // это разрыв линии, при OnChange — удержание значения (§6.1).
        Assert.Equal(LoggingMode.Periodic, series[0].Mode);
        Assert.Equal(LoggingMode.OnChange, series[1].Mode);
        Assert.Equal(5, series[0].Points.Length);
        Assert.False(series[0].Downsampled);
    }

    [Fact]
    public async Task ReadHistory_OverLimit_DownsamplesInsteadOfFailing()
    {
        var (client, table, pipeline) = Build();

        for (int i = 0; i < 40; i++)
            Feed(table, pipeline, new TagId(0), 70.0 + i, BaseTime + i * 1000L);

        var series = await client.ReadHistoryAsync(
            [new TagId(0)], BaseTime, BaseTime + 40_000, maxPointsPerTag: 10);

        // Оператор, запросивший диапазон, хочет увидеть диапазон, а не ошибку:
        // ответ прореживается и помечается (§14.1).
        Assert.True(series[0].Downsampled);
        Assert.True(series[0].Points.Length <= 10);
        Assert.NotEmpty(series[0].Points);
    }

    [Fact]
    public async Task ReadBuckets_ReturnsRequestedCount()
    {
        var (client, table, pipeline) = Build();

        for (int i = 0; i < 30; i++)
            Feed(table, pipeline, new TagId(0), 70.0 + i, BaseTime + i * 1000L);

        var series = await client.ReadBucketsAsync(
            [new TagId(0)], BaseTime, BaseTime + 30_000, bucketCount: 6);

        Assert.Equal(6, series[0].Buckets.Length);
        Assert.Equal(30, series[0].Buckets.Sum(b => b.Count));
        Assert.Equal(70.0, series[0].Buckets.Where(b => !b.IsEmpty).Min(b => b.Min), precision: 8);
    }

    [Fact]
    public async Task ReadAt_ReturnsAnchorPerTag_NullWhenNoData()
    {
        var (client, table, pipeline) = Build();

        Feed(table, pipeline, new TagId(1), 1.0, BaseTime);

        var anchors = await client.ReadAtAsync(
            [new TagId(1), new TagId(0)], BaseTime + 86_400_000L);

        Assert.NotNull(anchors[0]);
        Assert.Equal(1.0, anchors[0]!.Value.Value, precision: 8);
        Assert.Null(anchors[1]);
    }

    [Fact]
    public async Task TooManyTags_IsRefused()
    {
        var (client, _, _) = Build(new HistoryQueryLimits { MaxStreamsPerQuery = 1 });

        // Молча урезать список тегов нельзя: тренд нарисовался бы без части
        // кривых, и это хуже внятной ошибки.
        var error = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await client.ReadHistoryAsync(
                [new TagId(0), new TagId(1)], BaseTime, BaseTime + 1000, 100));

        Assert.Contains("при пределе 1", error.Message);
    }

    [Fact]
    public async Task WithoutArchive_ReturnsEmptySeriesInsteadOfThrowing()
    {
        var config = CreateConfig();
        var table = new TagTableImpl(config.Tags.Count);
        var client = new LocalRuntimeClient(table);

        var series = await client.ReadHistoryAsync([new TagId(0)], BaseTime, BaseTime + 1000, 100);
        var buckets = await client.ReadBucketsAsync([new TagId(0)], BaseTime, BaseTime + 1000, 4);
        var anchors = await client.ReadAtAsync([new TagId(0)], BaseTime);

        // Архив выключен — схемы и текущие значения обязаны работать.
        Assert.Empty(series[0].Points);
        Assert.Empty(buckets[0].Buckets);
        Assert.Null(anchors[0]);
        Assert.Equal(0, client.ReadRecent(new TagId(0), new TagValue[4]));
    }

    private static ProjectConfiguration CreateConfig() => new()
    {
        Name = "ClientHistory",
        Tags =
        [
            new TagDefinition
            {
                Id = new TagId(0), Name = "Boiler1.Temp", DataType = TagDataType.Analog,
                DeviceId = new DeviceId(0), IsArchived = true,
                Logging = new TagLoggingConfiguration { Interval = TimeSpan.FromSeconds(1) }
            },
            new TagDefinition
            {
                Id = new TagId(1), Name = "Pump1.Running", DataType = TagDataType.Discrete,
                DeviceId = new DeviceId(0), IsArchived = true,
                Logging = new TagLoggingConfiguration { LogOnChange = true }
            }
        ]
    };
}
