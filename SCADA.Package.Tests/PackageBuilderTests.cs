using SCADA.Core.Tags;
using SCADA.Expressions;
using SCADA.Expressions.Compiler;
using SCADA.Package.Builder;
using SCADA.Runtime.TagTable;

namespace SCADA.Package.Tests;

// полный цикл: каталог с JSON → .scadapkg → ProjectConfiguration обратно
public class PackageBuilderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    private string ProjectDir => Path.Combine(_dir, "project");
    private string PackagePath => Path.Combine(_dir, "project.scadapkg");

    public PackageBuilderTests()
    {
        Directory.CreateDirectory(ProjectDir);
        File.WriteAllText(Path.Combine(ProjectDir, "project.json"),
            """{"formatVersion": 1, "name": "BoilerRoom", "version": "3.1"}""");
        File.WriteAllText(Path.Combine(ProjectDir, "devices.json"), """
            {
              "formatVersion": 1,
              "channels": [{"id": 0, "name": "Line1", "channelType": "modbus-tcp", "configuration": "192.168.0.10:502"}],
              "devices": [{"id": 0, "name": "PLC1", "driverName": "simulator", "channelId": 0}]
            }
            """);
        File.WriteAllText(Path.Combine(ProjectDir, "tags.json"), """
            {
              "formatVersion": 1,
              "tags": [
                {"id": 0, "name": "Boiler1.Temp", "dataType": "analog", "deviceId": 0,
                 "address": "sin:10", "minValue": 0, "maxValue": 150, "units": "°C"},
                {"id": 1, "name": "Pump1.Running", "dataType": "discrete", "deviceId": 0, "address": "square:5"}
              ]
            }
            """);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class ProjectCatalog : ITagCatalog
    {
        public bool TryGetIndex(string name, out int index)
        {
            switch (name)
            {
                case "Boiler1.Temp": index = 0; return true;
                case "Pump1.Running": index = 1; return true;
                default: index = -1; return false;
            }
        }
    }

    [Fact]
    public void FullCycle_JsonToPackageToConfig()
    {
        PackageBuilder.Build(ProjectDir, PackagePath);

        var config = PackageProjectLoader.Load(PackagePath);

        Assert.Equal("BoilerRoom", config.Name);
        Assert.Equal("3.1", config.Version);

        // пакет несёт и сгенерированную диагностику канала: 7 тегов на канал
        Assert.Equal(2 + 7, config.Tags.Count);
        var temp = config.Tags[0];
        Assert.Equal("Boiler1.Temp", temp.Name);
        Assert.Equal(TagDataType.Analog, temp.DataType);
        Assert.Equal("sin:10", temp.Address);
        Assert.Equal(150, temp.MaxValue);
        Assert.Equal("°C", temp.Units);
        Assert.Equal(TagOrigin.Process, temp.Origin);

        // диагностический тег прошёл сквозь пакет с сохранением Origin
        var connected = config.Tags[2];
        Assert.Equal("@Line1.Connected", connected.Name);
        Assert.Equal(TagOrigin.Diagnostics, connected.Origin);

        var channel = Assert.Single(config.Channels);
        Assert.Equal("192.168.0.10:502", channel.Configuration);
        Assert.Equal(2, config.Devices.Count); // PLC1 + диагностическое "@Line1"
        Assert.Equal("simulator", config.Devices[0].DriverName);
    }

    [Fact]
    public void FullCycle_Expressions_DeduplicatedAndExecutable()
    {
        var catalog = new ProjectCatalog();
        var expressions = new[]
        {
            ExpressionCompiler.Compile("Boiler1.Temp > 80", catalog),
            ExpressionCompiler.Compile("Boiler1.Temp > 80", catalog), // дубликат
            ExpressionCompiler.Compile("Pump1.Running == 1", catalog)
        };

        PackageBuilder.Build(ProjectDir, PackagePath, expressions);

        using var reader = PackageReader.Open(PackagePath);
        var pool = PackageProjectLoader.LoadCodePool(reader);

        // два уникальных выражения из трёх
        Assert.Equal(2, pool.Expressions.Length);
        // константы слиты в общий пул: 80 и 1, без дубликатов
        Assert.Equal(2, pool.Constants.Length);

        // загруженное выражение исполняется ВМ и даёт правильный результат
        var table = new TagTable(capacity: 2);
        table.Write(new TagId(0), new TagValue(100.0, 1000, Quality.Good));
        var context = new EvaluationContext { Tags = table };

        double result = ExpressionVM.Evaluate(pool.ToExpression(0), context);
        Assert.Equal(1.0, result);

        // список тегов для пересчёта по эпохам сохранился
        Assert.Equal([0], pool.Expressions[0].TagIndices);
    }
}
