using SCADA.Core.Devices;
using SCADA.Core.Tags;
using SCADA.Historian;
using SCADA.Runtime.Historian;
using Xunit.Abstractions;
using TagTableImpl = SCADA.Runtime.TagTable.TagTable;

namespace SCADA.Runtime.Tests;

/// <summary>
/// Калькулятор объёма архива (ТЗ §4.3) и критерий приёмки M4 (ТЗ §17):
/// фактический рост сравнивается с предсказанием калькулятора, а не с
/// угаданной константой. Такой критерий проверяем при любом исходе и заодно
/// проверяет сам калькулятор.
/// </summary>
public class ArchiveVolumeCalculatorTests : IDisposable
{
    /// <summary>Допуск критерия приёмки M4.</summary>
    private const double TolerancePercent = 25;

    private const long DayStart = 1_700_000_000_000L;

    private readonly ITestOutputHelper _out;
    private readonly string _root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    private FileArchiveStore? _store;

    public ArchiveVolumeCalculatorTests(ITestOutputHelper output)
    {
        _out = output;
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        _store?.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* временный каталог */ }
    }

    [Fact]
    public void NonArchivedTags_AreNotCounted()
    {
        var config = CreateConfig(analogCount: 3, archived: false);

        var estimate = ArchiveVolumeCalculator.Estimate(config, retentionDays: 400);

        Assert.Equal(0, estimate.ArchivedTags);
        Assert.Equal(0, estimate.BytesPerDay);
    }

    [Fact]
    public void PeriodicInterval_DrivesPointCount()
    {
        var oneSecond = CreateConfig(analogCount: 1, intervalSeconds: 1);
        var tenSeconds = CreateConfig(analogCount: 1, intervalSeconds: 10);

        var fast = ArchiveVolumeCalculator.Estimate(oneSecond, 400);
        var slow = ArchiveVolumeCalculator.Estimate(tenSeconds, 400);

        Assert.Equal(86_400, fast.TopConsumers[0].PointsPerDay, precision: 0);
        Assert.Equal(8_640, slow.TopConsumers[0].PointsPerDay, precision: 0);
        Assert.True(fast.BytesPerDay > slow.BytesPerDay);
    }

    [Fact]
    public void SlowTag_BlockOverheadDominates()
    {
        // Тег раз в 5 минут: 288 отсчётов в сутки при 24 блоках. Заголовки
        // блоков стоят дороже самих данных, и наивная модель «отсчёты × байты»
        // занизила бы объём на порядок.
        var config = CreateConfig(analogCount: 1, intervalSeconds: 300);

        var estimate = ArchiveVolumeCalculator.Estimate(config, 400);
        var tag = estimate.TopConsumers[0];

        double payloadBytes = tag.PointsPerDay * 0.55;
        Assert.True(tag.BytesPerDay > payloadBytes * 5,
            $"{tag.BytesPerDay:F0} байт/сут при полезной нагрузке {payloadBytes:F0} — " +
            "накладные расходы блоков не учтены");
    }

    [Fact]
    public void Report_StatesDiskRequirementWithMargin()
    {
        var config = CreateConfig(analogCount: 100, intervalSeconds: 1);

        string report = ArchiveVolumeCalculator.Format(
            ArchiveVolumeCalculator.Estimate(config, 400));

        _out.WriteLine(report);

        Assert.Contains("логируемых тегов", report);
        Assert.Contains("трёхкратным запасом", report);
        Assert.Contains("Наибольший вклад", report);
    }

    /// <summary>
    /// Критерий приёмки M4 (ТЗ §17, пункт 2): суточный прогон, фактический рост
    /// в пределах 25 % от предсказания калькулятора.
    /// </summary>
    [Fact]
    public async Task DailyRun_ActualGrowthMatchesEstimate()
    {
        var config = CreateConfig(analogCount: 8, intervalSeconds: 1, discreteCount: 4);
        var estimate = ArchiveVolumeCalculator.Estimate(config, retentionDays: 1);

        long actualBytes = await SimulateDayAsync(config);
        double predicted = estimate.BytesPerDay;
        double deviation = Math.Abs(actualBytes - predicted) * 100.0 / predicted;

        _out.WriteLine($"предсказано: {predicted / 1024:F1} КБ/сут");
        _out.WriteLine($"фактически:  {actualBytes / 1024.0:F1} КБ/сут");
        _out.WriteLine($"расхождение: {deviation:F1} %");

        Assert.True(deviation <= TolerancePercent,
            $"Расхождение {deviation:F1} % превышает допуск {TolerancePercent} %. " +
            "Либо константы калькулятора разошлись с кодеками, либо сломалось " +
            "накопление блоков.");
    }

    /// <summary>
    /// Прогоняет через конвейер сутки данных с синтетическими метками времени.
    /// Реальные сутки ждать незачем: конвейер оперирует метками из TagTable,
    /// а не системными часами.
    /// </summary>
    private async Task<long> SimulateDayAsync(ProjectConfiguration config)
    {
        var table = new TagTableImpl(config.Tags.Count);
        var registry = new ArchiveStreamRegistry(_root);

        _store = new FileArchiveStore(_root, durable: false);
        var pipeline = new ArchivePipeline(table, _store, registry, config,
            new ArchivePipelineOptions { DefaultInterval = TimeSpan.FromSeconds(1) });

        var random = new Random(20260814);
        var units = new long[config.Tags.Count];
        for (int i = 0; i < units.Length; i++)
            units[i] = 7000 + i * 13;

        // Сутки по секунде: 86 400 тиков конвейера.
        for (int second = 0; second < 86_400; second++)
        {
            long timestamp = DayStart + second * 1000L;

            foreach (var tag in config.Tags)
            {
                int index = tag.Id.Value;

                if (tag.DataType == TagDataType.Discrete)
                {
                    // ~100 переключений в сутки — умолчание калькулятора.
                    if (random.Next(864) == 0)
                        units[index] = units[index] == 0 ? 1 : 0;

                    table.Write(tag.Id, new TagValue(units[index], timestamp, Quality.Good));
                    continue;
                }

                // Стабильный аналоговый сигнал: 20 % отсчётов меняются на
                // младший разряд АЦП — форма установившегося режима.
                if (random.Next(100) < 20)
                    units[index] += random.Next(-1, 2);

                table.Write(tag.Id, new TagValue(units[index] * 0.01, timestamp, Quality.Good));
            }

            pipeline.ProcessTick();
            pipeline.FlushPending();
        }

        await _store.FlushAsync();

        return Directory.GetFiles(_root, "*.dat", SearchOption.AllDirectories)
            .Sum(file => new FileInfo(file).Length);
    }

    private static ProjectConfiguration CreateConfig(
        int analogCount, double intervalSeconds = 1, int discreteCount = 0, bool archived = true)
    {
        var tags = new List<TagDefinition>();

        for (int i = 0; i < analogCount; i++)
        {
            tags.Add(new TagDefinition
            {
                Id = new TagId(tags.Count),
                Name = $"Analog{i}",
                DataType = TagDataType.Analog,
                DeviceId = new DeviceId(0),
                IsArchived = archived,
                ScaleFactor = 0.01,
                ScaleOffset = 0.0,
                Logging = new TagLoggingConfiguration
                {
                    Interval = TimeSpan.FromSeconds(intervalSeconds)
                }
            });
        }

        for (int i = 0; i < discreteCount; i++)
        {
            tags.Add(new TagDefinition
            {
                Id = new TagId(tags.Count),
                Name = $"Discrete{i}",
                DataType = TagDataType.Discrete,
                DeviceId = new DeviceId(0),
                IsArchived = archived,
                Logging = new TagLoggingConfiguration { LogOnChange = true }
            });
        }

        return new ProjectConfiguration { Name = "VolumeTest", Tags = tags };
    }
}
