using SCADA.Core.Tags;

namespace SCADA.Historian.Tests;

/// <summary>
/// Приёмка по объёму (docs/M4-review-01.md, задача 1). Проверяется не число
/// прочитанных точек, а **байты на точку**: именно этот показатель отличает
/// работающее накопление блоков от записи блока на каждую точку, и именно его
/// не видели тесты, пропустившие расхождение в 174 раза.
/// </summary>
public class ArchiveVolumeTests : IDisposable
{
    private const int Points = 4096;
    private const long BaseTime = 1_700_000_000_000L;

    private readonly List<string> _roots = [];

    public void Dispose()
    {
        foreach (string root in _roots)
        {
            try { Directory.Delete(root, recursive: true); } catch { /* временный каталог */ }
        }
    }

    [Fact]
    public async Task PointByPointWrite_CostsSameAsBatch()
    {
        var data = CreateAnalogSeries();

        long byPoint = await MeasureAsync(data, batched: false);
        long batched = await MeasureAsync(data, batched: true);

        // Конвейер отдаёт точки по одной каждый тик. Если открытый блок живёт
        // в сторе, гранулярность вызова Write на объём не влияет вовсе.
        double divergence = Math.Abs(byPoint - batched) * 100.0 / batched;
        Assert.True(divergence <= 5.0,
            $"Запись по одной точке дала {byPoint} байт против {batched} при пакетной — " +
            $"расхождение {divergence:F1} %. Блок накапливается не в сторе.");
    }

    [Fact]
    public async Task AnalogSeries_FitsVolumeBudget()
    {
        var data = CreateAnalogSeries();
        long size = await MeasureAsync(data, batched: false);
        double bytesPerPoint = (double)size / Points;

        // Расчётная оценка спеки §19 — около 0,5 байта на отсчёт, из неё
        // получены 24 ГБ в год в ТЗ §8.5. Порог с запасом: рост выше него
        // означает, что сломался кодек либо накопление блоков.
        Assert.True(bytesPerPoint < 1.5,
            $"{bytesPerPoint:F2} байта на отсчёт при ожидаемых ~0,5 (спека §19).");
    }

    [Fact]
    public async Task DiscreteOnChange_IsCheap()
    {
        // Дискрет при OnChange пишется только на переключениях: 50 в сутки
        // против 86 400 отсчётов при Periodic (ТЗ §8.3).
        var data = new ArchivePoint[200];
        for (int i = 0; i < data.Length; i++)
            data[i] = new ArchivePoint(BaseTime + i * 60_000L, i % 2, Quality.Good);

        long size = await MeasureAsync(data,
            new ArchiveStreamConfig(TagDataType.Discrete, LoggingMode.OnChange, 1.0, 0.0),
            batched: false);

        Assert.True((double)size / data.Length < 3.0,
            $"{(double)size / data.Length:F2} байта на переключение дискрета.");
    }

    /// <summary>
    /// Стабильный аналоговый тег на решётке 0,01 с дрожанием в младший разряд
    /// АЦП: 80 % отсчётов без изменения — типичная форма технологического
    /// параметра в установившемся режиме (спека §19).
    /// </summary>
    private static ArchivePoint[] CreateAnalogSeries()
    {
        var data = new ArchivePoint[Points];
        var random = new Random(1);
        long units = 7531;

        for (int i = 0; i < Points; i++)
        {
            if (random.Next(100) < 20)
                units += random.Next(-1, 2);

            data[i] = new ArchivePoint(BaseTime + i * 1000L, units * 0.01, Quality.Good);
        }

        return data;
    }

    private Task<long> MeasureAsync(ArchivePoint[] data, bool batched)
        => MeasureAsync(data,
            new ArchiveStreamConfig(TagDataType.Analog, LoggingMode.Periodic, 0.01, 0.0),
            batched);

    private async Task<long> MeasureAsync(ArchivePoint[] data, ArchiveStreamConfig config, bool batched)
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _roots.Add(root);

        var store = new FileArchiveStore(root);
        store.RegisterStream(1, config);

        if (batched)
        {
            store.Write(1, data);
        }
        else
        {
            foreach (var point in data)
                store.Write(1, new[] { point });
        }

        await store.FlushAsync();

        return Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Sum(file => new FileInfo(file).Length);
    }
}
