using System.Collections.Generic;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using SCADA.Core.Devices;
using SCADA.Core.Tags;
using SCADA.Historian;
using SCADA.Runtime.Configuration;
using SCADA.Runtime.Historian;

namespace SCADA.Benchmarks;

/// <summary>
/// Приёмочный бенчмарк М4 (ТЗ §17, критерий №1): тренд 10 тегов.
/// Замер идёт через публичный фасад <see cref="IHistorian.ReadBucketsAsync"/>
/// с фиксированным числом бакетов (ширина экрана АРМа), то есть по тому же
/// пути, которым пойдёт реальный тренд.
/// </summary>
[MemoryDiagnoser]
public class TrendBenchmarks
{
    private const int TagCount = 10;
    private const int BucketCount = 2000;
    private const long BaseTime = 1_704_067_200_000L; // 2024-01-01 00:00 UTC

    // Реалистичный сигнал: 1 Гц, 20 % изменений, дрожание в младший разряд
    // решётки 0.01. Такой же сигнал используется в ArchiveBenchmarks.
    private static readonly int[] DaysInMonth2024 = [31, 29, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31];
    private static readonly int SecondsInYear = DaysInMonth2024.Sum() * 24 * 60 * 60;
    private static readonly long YearMs = SecondsInYear * 1000L;
    private static readonly long EightHoursMs = 8 * 60 * 60 * 1000L;

    private ProjectConfiguration _config = null!;
    private RuntimeHistorian _historian = null!;
    private FileArchiveStore _store = null!;
    private ArchiveStreamRegistry _registry = null!;
    private InMemoryHistorian _ring = null!;
    private string _root = null!;
    private ArchiveBucket[] _buckets = null!;

    [GlobalSetup]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        _registry = new ArchiveStreamRegistry(_root);
        _store = new FileArchiveStore(_root, durable: false);

        _config = CreateConfig();
        _ring = new InMemoryHistorian(TagCount);

        // Разрешаем потоки и регистрируем их в сторе: RuntimeHistorian сам
        // регистрирует только в реестре, а для прямой записи нужен стор.
        foreach (var tag in _config.Tags)
        {
            int streamId = _registry.Resolve(tag.Name, tag.DataType);
            var mode = LoggingModeHelper.Infer(tag.Logging);
            _store.RegisterStream(streamId, new ArchiveStreamConfig(
                tag.DataType, mode, tag.ScaleFactor, 0.0));
        }

        WriteYearOfData();
        _store.FlushAsync().GetAwaiter().GetResult();

        _historian = new RuntimeHistorian(_ring, _store, _registry, _config);

        // Фиксированное число бакетов — ширина тренда на экране АРМа.
        // Для года бакет получается ~4,3 ч, и срабатывает заголовочный путь;
        // для 8 часов бакет ~14 с, идёт разбор небольшого числа блоков.
        _buckets = new ArchiveBucket[BucketCount];
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _store.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* временный каталог */ }
    }

    [Benchmark(Description = "Тренд 10 тегов за 8 ч через IHistorian")]
    public async Task<long> ReadTrend10Tags8Hours()
    {
        long total = 0;
        long to = BaseTime + YearMs;
        long from = to - EightHoursMs;

        for (int tag = 0; tag < TagCount; tag++)
        {
            var result = await _historian.ReadBucketsAsync(
                new TagId(tag), from, to, _buckets, default);
            total += result.Count;
        }

        return total;
    }

    [Benchmark(Description = "Тренд 10 тегов за год через IHistorian")]
    public async Task<long> ReadTrend10TagsYear()
    {
        long total = 0;
        long from = BaseTime;
        long to = BaseTime + YearMs;

        for (int tag = 0; tag < TagCount; tag++)
        {
            var result = await _historian.ReadBucketsAsync(
                new TagId(tag), from, to, _buckets, default);
            total += result.Count;
        }

        return total;
    }

    private void WriteYearOfData()
    {
        var random = new Random(20260817);
        long units = 7531;
        var points = new List<ArchivePoint>(2_600_000);

        for (int tagIndex = 0; tagIndex < TagCount; tagIndex++)
        {
            var tag = _config.Tags[tagIndex];
            int streamId = _registry.Resolve(tag.Name, tag.DataType);
            long monthStart = BaseTime;

            for (int month = 0; month < DaysInMonth2024.Length; month++)
            {
                points.Clear();
                int secondsInMonth = DaysInMonth2024[month] * 24 * 60 * 60;

                for (int s = 0; s < secondsInMonth; s++)
                {
                    if (random.Next(100) < 20)
                        units += random.Next(-1, 2);

                    points.Add(new ArchivePoint(
                        monthStart + s * 1000L,
                        units * 0.01,
                        Quality.Good));
                }

                _store.Write(streamId, CollectionsMarshal.AsSpan(points));
                monthStart += secondsInMonth * 1000L;
            }
        }
    }

    private static ProjectConfiguration CreateConfig()
    {
        var tags = new List<TagDefinition>(TagCount);
        for (int i = 0; i < TagCount; i++)
        {
            tags.Add(new TagDefinition
            {
                Id = new TagId(i),
                Name = $"TrendTag{i}",
                DataType = TagDataType.Analog,
                DeviceId = new DeviceId(0),
                IsArchived = true,
                ScaleFactor = 0.01,
                Logging = new TagLoggingConfiguration { Interval = TimeSpan.FromSeconds(1) }
            });
        }

        return new ProjectConfiguration { Name = "TrendBench", Tags = tags };
    }
}
