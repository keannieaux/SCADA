using SCADA.Core.Devices;
using SCADA.Core.Tags;
using SCADA.Historian;
using SCADA.Runtime.Historian;
using TagTableImpl = SCADA.Runtime.TagTable.TagTable;

namespace SCADA.Runtime.Tests;

/// <summary>
/// Сквозная проверка архива: TagTable → конвейер → файловый стор → фасад.
/// Модульные тесты проверяют узлы по отдельности; здесь проверяется, что
/// собранная цепочка действительно пишет и отдаёт данные — без этого
/// «веха завершается работающим приложением» (ТЗ §15.5) ничем не подтверждена.
/// </summary>
public class ArchiveEndToEndTests : IDisposable
{
    private const long BaseTime = 1_700_000_000_000L;

    private readonly string _root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    private FileArchiveStore? _store;

    public ArchiveEndToEndTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        _store?.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* временный каталог */ }
    }

    private (TagTableImpl Table, ArchivePipeline Pipeline, RuntimeHistorian Historian) Build()
    {
        var config = CreateConfig();
        var table = new TagTableImpl(config.Tags.Count);
        var registry = new ArchiveStreamRegistry(_root);

        _store = new FileArchiveStore(_root);
        var pipeline = new ArchivePipeline(table, _store, registry, config,
            new ArchivePipelineOptions { DefaultInterval = TimeSpan.FromSeconds(1) });

        var ring = new InMemoryHistorian(config.Tags.Count);
        var historian = new RuntimeHistorian(ring, _store, registry, config);

        return (table, pipeline, historian);
    }

    [Fact]
    public async Task AnalogTag_WrittenByPipeline_ReadableThroughFacade()
    {
        var (table, pipeline, historian) = Build();

        // Десять секунд процесса при интервале логирования 1 с.
        for (int i = 0; i < 10; i++)
        {
            table.Write(new TagId(0), new TagValue(70.0 + i * 0.5, BaseTime + i * 1000L, Quality.Good));
            pipeline.ProcessTick();
            pipeline.FlushPending();
        }

        var buffer = new TagValue[32];
        var result = await historian.ReadRawAsync(new TagId(0), BaseTime, BaseTime + 20_000, buffer);

        Assert.Equal(10, result.Count);
        Assert.Equal(LoggingMode.Periodic, result.Mode);
        Assert.Equal(70.0, buffer[0].Value, precision: 8);
        Assert.Equal(74.5, buffer[9].Value, precision: 8);
    }

    [Fact]
    public async Task NonArchivedTag_ReturnsNothingFromArchive()
    {
        var (table, pipeline, historian) = Build();

        table.Write(new TagId(2), new TagValue(85.0, BaseTime, Quality.Good));
        pipeline.ProcessTick();
        pipeline.FlushPending();

        // Тег без IsArchived в архив не попадает и потока не имеет.
        var buffer = new TagValue[8];
        var result = await historian.ReadRawAsync(new TagId(2), BaseTime, BaseTime + 10_000, buffer);

        Assert.Equal(0, result.Count);
    }

    [Fact]
    public async Task DiscreteTag_OnChange_KeepsTransitionsOnly()
    {
        var (table, pipeline, historian) = Build();

        double[] states = [0, 0, 1, 1, 1, 0];
        for (int i = 0; i < states.Length; i++)
        {
            table.Write(new TagId(1), new TagValue(states[i], BaseTime + i * 1000L, Quality.Good));
            pipeline.ProcessTick();
            pipeline.FlushPending();
        }

        var buffer = new TagValue[16];
        var result = await historian.ReadRawAsync(new TagId(1), BaseTime, BaseTime + 10_000, buffer);

        // Первый отсчёт плюс два переключения — повторы не пишутся.
        Assert.Equal(3, result.Count);
        Assert.Equal(LoggingMode.OnChange, result.Mode);
        Assert.Equal(0.0, buffer[0].Value, precision: 8);
        Assert.Equal(1.0, buffer[1].Value, precision: 8);
        Assert.Equal(0.0, buffer[2].Value, precision: 8);
    }

    [Fact]
    public async Task Anchor_ReturnsLastKnownValueBeforeWindow()
    {
        var (table, pipeline, historian) = Build();

        table.Write(new TagId(1), new TagValue(1.0, BaseTime, Quality.Good));
        pipeline.ProcessTick();
        pipeline.FlushPending();

        // Через сутки дискрет не менялся: запрос «что было» обязан вернуть
        // последнее известное, иначе левый край тренда будет пустым (§13.1).
        var anchor = await historian.ReadAtAsync(new TagId(1), BaseTime + 86_400_000L);

        Assert.NotNull(anchor);
        Assert.Equal(1.0, anchor!.Value.Value, precision: 8);
    }

    [Fact]
    public async Task Buckets_AggregateAnalogSeries()
    {
        var (table, pipeline, historian) = Build();

        for (int i = 0; i < 60; i++)
        {
            table.Write(new TagId(0), new TagValue(70.0 + i, BaseTime + i * 1000L, Quality.Good));
            pipeline.ProcessTick();
            pipeline.FlushPending();
        }

        var buckets = new ArchiveBucket[6];
        var result = await historian.ReadBucketsAsync(new TagId(0), BaseTime, BaseTime + 60_000, buckets);

        Assert.True(result.Count > 0);
        Assert.Equal(60, buckets.Sum(b => b.Count));
        Assert.Equal(70.0, buckets.Where(b => b.Count > 0).Min(b => b.Min), precision: 8);
        Assert.Equal(129.0, buckets.Where(b => b.Count > 0).Max(b => b.Max), precision: 8);
    }

    [Fact]
    public async Task BadQuality_BreaksSeriesWithSingleMarker()
    {
        var (table, pipeline, historian) = Build();

        table.Write(new TagId(0), new TagValue(70.0, BaseTime, Quality.Good));
        pipeline.ProcessTick();
        pipeline.FlushPending();

        // Обрыв связи: движок держит последнее значение с качеством Bad
        // и обновляет метку на каждой попытке переподключения.
        for (int i = 1; i <= 5; i++)
        {
            table.Write(new TagId(0), new TagValue(70.0, BaseTime + i * 1000L, Quality.Bad));
            pipeline.ProcessTick();
            pipeline.FlushPending();
        }

        table.Write(new TagId(0), new TagValue(71.0, BaseTime + 6000, Quality.Good));
        pipeline.ProcessTick();
        pipeline.FlushPending();

        var buffer = new TagValue[16];
        var result = await historian.ReadRawAsync(new TagId(0), BaseTime, BaseTime + 10_000, buffer);

        // Достоверное, одна отметка перехода, достоверное после восстановления —
        // пять копий устаревшего значения в архив не попадают (§6.2).
        Assert.Equal(3, result.Count);
        Assert.Equal(Quality.Good, buffer[0].Quality);
        Assert.Equal(Quality.Bad, buffer[1].Quality);
        Assert.Equal(Quality.Good, buffer[2].Quality);
    }

    private static ProjectConfiguration CreateConfig() => new()
    {
        Name = "EndToEnd",
        Tags =
        [
            new TagDefinition
            {
                Id = new TagId(0), Name = "Boiler1.Temp", DataType = TagDataType.Analog,
                DeviceId = new DeviceId(0), IsArchived = true,
                ScaleFactor = 0.5, ScaleOffset = 0.0,
                Logging = new TagLoggingConfiguration { Interval = TimeSpan.FromSeconds(1) }
            },
            new TagDefinition
            {
                Id = new TagId(1), Name = "Pump1.Running", DataType = TagDataType.Discrete,
                DeviceId = new DeviceId(0), IsArchived = true,
                Logging = new TagLoggingConfiguration { LogOnChange = true }
            },
            new TagDefinition
            {
                Id = new TagId(2), Name = "Settings.Setpoint", DataType = TagDataType.Analog,
                DeviceId = new DeviceId(0), IsArchived = false
            }
        ]
    };
}
