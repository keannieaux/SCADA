using BenchmarkDotNet.Attributes;
using SCADA.Core.Channels;
using SCADA.Core.Devices;
using SCADA.Core.Tags;
using SCADA.Drivers.Simulator;
using SCADA.Runtime.TagTable;

namespace SCADA.Benchmarks;

// Приёмочный бенчмарк M1 (ТЗ §4.1, §17): полный контур на 20 000 тегов.
// CPU-бюджет 25% на целевом железе выводится из времени цикла:
// при опросе 10 раз/с загрузка ≈ PollCycle × 10.
[MemoryDiagnoser]
public class AcceptanceBenchmarks
{
    [Params(20_000)]
    public int TagCount { get; set; }

    private TagTable _table = null!;
    private SimulatorDriver _driver = null!;
    private TagValue[] _pollBuffer = null!;
    private TagId[] _scanBuffer = null!;

    [GlobalSetup]
    public void Setup()
    {
        var device = new DeviceDefinition
        {
            Id = new DeviceId(0), Name = "SimPLC",
            DriverName = "simulator", ChannelId = new ChannelId(0)
        };
        var tags = Enumerable.Range(0, TagCount)
            .Select(i => new TagDefinition
            {
                Id = new TagId(i), Name = $"Tag{i}",
                DataType = TagDataType.Analog,
                DeviceId = new DeviceId(0), Address = "sin:10"
            })
            .ToArray();

        _driver = new SimulatorDriver();
        _driver.ConnectAsync(device, tags, CancellationToken.None).GetAwaiter().GetResult();

        _table = new TagTable(TagCount);
        _pollBuffer = new TagValue[TagCount];
        _scanBuffer = new TagId[TagCount];
    }

    // один полный цикл опроса: драйвер заполняет буфер, всё пишется в таблицу
    [Benchmark]
    public void PollCycle()
    {
        _driver.PollAsync(_pollBuffer, CancellationToken.None).GetAwaiter().GetResult();
        for (int i = 0; i < TagCount; i++)
            _table.Write(new TagId(i), _pollBuffer[i]);
    }

    // худший случай скана изменений: все 20 000 тегов изменились
    [Benchmark]
    public int EpochScan()
        => _table.GetChangedSince(0, _scanBuffer);

    // чтение «кадра мнемосхемы»: 500 динамических элементов (§4.1)
    [Benchmark]
    public double ReadFrame500()
    {
        double sink = 0;
        for (int i = 0; i < 500; i++)
            sink += _table.Read(new TagId(i)).Value;
        return sink;
    }
}
