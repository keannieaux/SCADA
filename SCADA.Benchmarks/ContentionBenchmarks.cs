using System.Collections.Concurrent;
using BenchmarkDotNet.Attributes;
using SCADA.Core.Tags;
using SCADA.Runtime.TagTable;

namespace SCADA.Benchmarks;

[MemoryDiagnoser]
public class ContentionBenchmarks
{
    [Params(4, 8)] // прогоним с 4 и 8 писателями
    public int Writers { get; set; }

    private const int TagCount = 10_000;
    private const int TotalWrites = 2000000; // суммарно на всех писателей за прогон

    private TagTable _table = null!;
    private ConcurrentQueue<int> _bus = null!;
    private TagValue _value;

    [GlobalSetup]
    public void Setup()
    {
        _table = new TagTable(TagCount);
        _bus = new ConcurrentQueue<int>();
        _value = new TagValue(1.0, 1, Quality.Good);
    }

    [IterationCleanup] // между итерациями чистим очередь, иначе она растёт бесконечно
    public void Cleanup()
    {
        while (_bus.TryDequeue(out _)) { }
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = TotalWrites)]
    public void TagTable_Write()
    {
        int perWriter = TotalWrites / Writers;
        int tagsPerWriter = TagCount / Writers;

        Parallel.For(0, Writers, w =>
        {
            int firstTag = w * tagsPerWriter;
            for (int i = 0; i < perWriter; i++)
                _table.Write(new TagId(firstTag + i % tagsPerWriter), _value);
        });
    }

    [Benchmark(OperationsPerInvoke = TotalWrites)]
    public void Bus_Publish()
    {
        int perWriter = TotalWrites / Writers;
        int tagsPerWriter = TagCount / Writers;

        Parallel.For(0, Writers, w =>
        {
            int firstTag = w * tagsPerWriter;
            for (int i = 0; i < perWriter; i++)
                _bus.Enqueue(firstTag + i % tagsPerWriter);
        });
    }
}
