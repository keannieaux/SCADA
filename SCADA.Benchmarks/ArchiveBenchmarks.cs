using BenchmarkDotNet.Attributes;
using SCADA.Core.Devices;
using SCADA.Core.Tags;
using SCADA.Historian;
using SCADA.Runtime.Configuration;
using SCADA.Runtime.Historian;
using SCADA.Runtime.TagTable;

namespace SCADA.Benchmarks;

// Бенчмарки архива (ТЗ §15.4, критерий приёмки M4 №5).
// Проверяются два разных свойства:
//   - пропускная способность кодеков: сколько отсчётов в секунду сжимается
//     и разжимается; отсюда видно, укладывается ли суточный прогон и чтение
//     тренда в бюджеты §4.1;
//   - отсутствие аллокаций в установившемся режиме конвейера (§15.2).
[MemoryDiagnoser]
public class ArchiveBenchmarks
{
    private const long BaseTime = 1_700_000_000_000L;

    /// <summary>Целевой размер блока — то, чем оперирует стор (§8.6).</summary>
    [Params(4096)]
    public int BlockPoints { get; set; }

    private ArchivePoint[] _analogPoints = null!;
    private byte[] _analogBlock = null!;
    private byte[] _fileWithManyBlocks = null!;

    [GlobalSetup]
    public void Setup()
    {
        _analogPoints = CreateAnalogSeries(BlockPoints);

        _analogBlock = BlockBuilder.Build(_analogPoints, TagDataType.Analog,
            LoggingMode.Periodic, scale: 0.01, offset: 0.0);

        // Файл на сутки при частоте 1 Гц: примерно 24 блока.
        _fileWithManyBlocks = BuildFile(blockCount: 24);
    }

    // --- кодеки ---

    [Benchmark(Description = "Сборка блока 4096 отсчётов (сжатие)")]
    public byte[] BuildBlock()
        => BlockBuilder.Build(_analogPoints, TagDataType.Analog,
            LoggingMode.Periodic, scale: 0.01, offset: 0.0);

    [Benchmark(Description = "Разбор блока 4096 отсчётов (разжатие)")]
    public int ReadBlock()
        => BlockReader.Read(_analogBlock).Points.Length;

    // Заголовки — тот путь, которым читается широкий диапазон (§8.4).
    // Разница с полным разбором показывает, что даёт быстрый путь.
    [Benchmark(Description = "Проход по заголовкам суток без разжатия")]
    public long ScanHeaders()
    {
        long sum = 0;
        int pos = 16;

        while (pos < _fileWithManyBlocks.Length)
        {
            var span = _fileWithManyBlocks.AsSpan(pos);
            if (!BlockReader.TryReadHeader(span, out var header))
                break;

            sum += header.Count;
            pos += System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(span[2..]);
        }

        return sum;
    }

    [Benchmark(Description = "Полный разбор суток с разжатием")]
    public long DecodeWholeDay()
    {
        long sum = 0;
        int pos = 16;

        while (pos < _fileWithManyBlocks.Length)
        {
            int length = System.Buffers.Binary.BinaryPrimitives
                .ReadInt32LittleEndian(_fileWithManyBlocks.AsSpan(pos + 2));

            sum += BlockReader.Read(_fileWithManyBlocks.AsSpan(pos, length)).Points.Length;
            pos += length;
        }

        return sum;
    }

    private static ArchivePoint[] CreateAnalogSeries(int count)
    {
        var points = new ArchivePoint[count];
        var random = new Random(20260814);
        long units = 7531;

        for (int i = 0; i < count; i++)
        {
            if (random.Next(100) < 20)
                units += random.Next(-1, 2);

            points[i] = new ArchivePoint(BaseTime + i * 1000L, units * 0.01, Quality.Good);
        }

        return points;
    }

    private byte[] BuildFile(int blockCount)
    {
        using var stream = new MemoryStream();
        stream.Write(new byte[16]); // место под заголовок файла

        for (int block = 0; block < blockCount; block++)
        {
            var points = new ArchivePoint[_analogPoints.Length];
            long offset = block * (long)_analogPoints.Length * 1000L;

            for (int i = 0; i < points.Length; i++)
            {
                points[i] = _analogPoints[i] with
                {
                    TimestampUtcMs = _analogPoints[i].TimestampUtcMs + offset
                };
            }

            stream.Write(BlockBuilder.Build(points, TagDataType.Analog,
                LoggingMode.Periodic, scale: 0.01, offset: 0.0));
        }

        return stream.ToArray();
    }
}

// Отдельный класс: у конвейера свой набор параметров и своя установка,
// а смешивание в одном классе сделало бы прогон непрозрачным.
[MemoryDiagnoser]
public class ArchivePipelineBenchmarks
{
    private const long BaseTime = 1_700_000_000_000L;

    /// <summary>Логируемые теги: 10–30 % от 10 000 по ТЗ §1.1.</summary>
    [Params(2000)]
    public int ArchivedTags { get; set; }

    private TagTable _table = null!;
    private ArchivePipeline _pipeline = null!;
    private string _root = null!;
    private long _tick;

    [GlobalSetup]
    public void Setup()
    {
        var config = CreateConfig(ArchivedTags);
        _table = new TagTable(config.Tags.Count);
        _root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        var registry = new ArchiveStreamRegistry(_root);
        var store = new FileArchiveStore(_root, durable: false);

        _pipeline = new ArchivePipeline(_table, store, registry, config,
            new ArchivePipelineOptions { DefaultInterval = TimeSpan.FromSeconds(1) });

        // Прогреваем: первый тик пишет все теги, установившийся режим — не первый.
        FillTable(BaseTime);
        _pipeline.ProcessTick();
        _pipeline.FlushPending();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* временный каталог */ }
    }

    // Установившийся режим: тик конвейера при 100 мс между тиками и интервале
    // логирования 1 с — то есть большинство тиков не пишет ничего, только
    // обходит состояния. Именно этот путь обязан быть безаллокационным (§15.2).
    [Benchmark(Description = "Тик конвейера, ничего не пишется")]
    public void IdleTick()
    {
        _tick += 100;
        FillTable(BaseTime + _tick);
        _pipeline.ProcessTick();
    }

    [Benchmark(Description = "Тик конвейера с записью всех тегов")]
    public void WritingTick()
    {
        _tick += 1000;
        FillTable(BaseTime + _tick);
        _pipeline.ProcessTick();
        _pipeline.FlushPending();
    }

    private void FillTable(long timestampMs)
    {
        for (int i = 0; i < ArchivedTags; i++)
            _table.Write(new TagId(i), new TagValue(70.0 + i * 0.01, timestampMs, Quality.Good));
    }

    private static ProjectConfiguration CreateConfig(int count)
    {
        var tags = new List<TagDefinition>(count);
        for (int i = 0; i < count; i++)
        {
            tags.Add(new TagDefinition
            {
                Id = new TagId(i),
                Name = $"Tag{i}",
                DataType = TagDataType.Analog,
                DeviceId = new DeviceId(0),
                IsArchived = true,
                ScaleFactor = 0.01,
                Logging = new TagLoggingConfiguration { Interval = TimeSpan.FromSeconds(1) }
            });
        }

        return new ProjectConfiguration { Name = "Bench", Tags = tags };
    }
}
