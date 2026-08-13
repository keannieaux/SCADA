using SCADA.Core.Channels;
using SCADA.Core.Devices;
using SCADA.Core.Tags;
using SCADA.Runtime.Historian;
using SCADA.Runtime.TagTable;
using Xunit.Abstractions;

namespace SCADA.Runtime.Tests;

// Замер footprint'а на 20 000 тегов для бюджета памяти §4.1 (≤ 500 МБ на АРМ).
// Не тест, а измерение — смотри вывод.
public class MemoryFootprintMeasurement
{
    private readonly ITestOutputHelper _output;

    public MemoryFootprintMeasurement(ITestOutputHelper output) => _output = output;

    private static double Mb(long bytes) => bytes / 1024.0 / 1024.0;

    [Fact]
    public void Footprint_20000_Tags()
    {
        const int tagCount = 20_000;

        long baseline = GC.GetTotalMemory(forceFullCollection: true);

        // 1. оперативная база
        var table = new TagTable.TagTable(tagCount);
        long afterTable = GC.GetTotalMemory(forceFullCollection: true);

        // 2. конфигурация: 20 000 описаний тегов + устройство + канал
        var config = new ProjectConfiguration
        {
            Name = "Footprint",
            Channels = [new ChannelDefinition { Id = new ChannelId(0), Name = "Ch0", ChannelType = "none" }],
            Devices = [new DeviceDefinition { Id = new DeviceId(0), Name = "PLC0", DriverName = "simulator", ChannelId = new ChannelId(0) }],
            Tags = Enumerable.Range(0, tagCount).Select(i => new TagDefinition
            {
                Id = new TagId(i), Name = $"Tag{i}",
                DataType = TagDataType.Analog, DeviceId = new DeviceId(0),
                Address = "sin:10", Units = "°C", Description = "Технологический параметр"
            }).ToArray()
        };
        long afterConfig = GC.GetTotalMemory(forceFullCollection: true);

        // 3. историк-заглушка, полностью заполненный: 20 000 тегов × 3600 точек
        var historian = new InMemoryHistorian(tagCount, capacityPerTag: 3600);
        for (int i = 0; i < tagCount; i++)
            for (int j = 0; j < 3600; j++)
                historian.Append(new TagId(i), new TagValue(j, j * 1000L, Quality.Good));
        long afterHistorian = GC.GetTotalMemory(forceFullCollection: true);

        _output.WriteLine($"TagTable:                  {Mb(afterTable - baseline):F1} МБ");
        _output.WriteLine($"Конфигурация (20k тегов):  {Mb(afterConfig - afterTable):F1} МБ");
        _output.WriteLine($"Историк (20k × 3600):      {Mb(afterHistorian - afterConfig):F1} МБ");
        _output.WriteLine($"ИТОГО:                     {Mb(afterHistorian - baseline):F1} МБ  (бюджет 500 МБ)");

        GC.KeepAlive(table);
        GC.KeepAlive(config);
        GC.KeepAlive(historian);
    }
}
