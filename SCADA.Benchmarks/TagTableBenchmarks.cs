using BenchmarkDotNet.Attributes;
using SCADA.Core.Tags;
using SCADA.Runtime.TagTable;

namespace SCADA.Benchmarks;

[MemoryDiagnoser] // добавляет в отчёт колонку аллокаций — нам критично
public class TagTableBenchmarks
{
    [Params(10_000, 20_000)] // прогонит каждый бенчмарк на обоих размерах
    public int Capacity { get; set; }

    private TagTable _table = null!;
    private TagValue _value;
    private TagId[] _buffer = null!;

    [GlobalSetup] // выполняется один раз, в замер не входит
    public void Setup()
    {
        _table = new TagTable(Capacity);
        _value = new TagValue(42.5, 638000000000, Quality.Good);
        _buffer = new TagId[Capacity];
    }

    [Benchmark(Baseline = true)] // точка отсчёта, остальные сравнятся с ней
    public void Write() => _table.Write(new TagId(0), _value);

    [Benchmark]
    public TagValue Read() => _table.Read(new TagId(0));

    [Benchmark]
    public int ScanChanged() => _table.GetChangedSince(0, _buffer);
}
