using SCADA.Core.Channels;
using SCADA.Core.Devices;
using SCADA.Core.Tags;
using SCADA.Runtime.Configuration;

namespace SCADA.Runtime.Tests;

public class DiagnosticsGeneratorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

    public DiagnosticsGeneratorTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static ProjectConfiguration CreateConfig() => new()
    {
        Name = "Test",
        Channels =
        [
            new ChannelDefinition { Id = new ChannelId(0), Name = "Line1", ChannelType = "modbus-tcp" },
            new ChannelDefinition { Id = new ChannelId(1), Name = "Line2", ChannelType = "modbus-tcp" }
        ],
        Devices =
        [
            new DeviceDefinition { Id = new DeviceId(0), Name = "PLC1", DriverName = "simulator", ChannelId = new ChannelId(0) },
            new DeviceDefinition { Id = new DeviceId(1), Name = "PLC2", DriverName = "simulator", ChannelId = new ChannelId(1) }
        ],
        Tags =
        [
            new TagDefinition { Id = new TagId(0), Name = "T0", DataType = TagDataType.Analog, DeviceId = new DeviceId(0), Address = "const:1" },
            new TagDefinition { Id = new TagId(1), Name = "T1", DataType = TagDataType.Analog, DeviceId = new DeviceId(1), Address = "const:2" }
        ]
    };

    [Fact]
    public void AppendDiagnostics_AddsDeviceAndMetricsPerChannel()
    {
        var config = CreateConfig();
        DiagnosticsGenerator.AppendDiagnostics(config);

        Assert.Equal(4, config.Devices.Count);      // 2 исходных + 2 диагностических
        Assert.Equal(2 + 2 * 7, config.Tags.Count); // по 7 метрик на канал

        var diagDevice = config.Devices[2];
        Assert.Equal("@Line1", diagDevice.Name);
        Assert.Equal("internal", diagDevice.DriverName);
        Assert.Equal(new ChannelId(0), diagDevice.ChannelId);

        var metricNames = config.Tags.Skip(2).Take(7).Select(t => t.Name).ToArray();
        Assert.Equal(
            ["@Line1.Connected", "@Line1.LastOkTime", "@Line1.RequestsOk", "@Line1.RequestsFailed",
             "@Line1.ReconnectCount", "@Line1.ResponseTimeAvg", "@Line1.ResponseTimeMax"],
            metricNames);

        Assert.All(config.Tags.Skip(2), t => Assert.Equal(TagOrigin.Diagnostics, t.Origin));
        Assert.All(config.Tags.Take(2), t => Assert.Equal(TagOrigin.Process, t.Origin));
    }

    [Fact]
    public void AppendDiagnostics_ContinuesDenseTagIds()
    {
        var config = CreateConfig();
        DiagnosticsGenerator.AppendDiagnostics(config);

        // инвариант TagTable: Id покрывают 0..n-1 без дыр, исходные Id не сдвинулись
        var ids = config.Tags.Select(t => t.Id.Value).Order().ToArray();
        for (int i = 0; i < ids.Length; i++)
            Assert.Equal(i, ids[i]);
    }

    [Fact]
    public void AppendDiagnostics_IsDeterministic()
    {
        // пакет и рантайм должны получить одни и те же Id — иначе сломается связывание
        var first = CreateConfig();
        var second = CreateConfig();
        DiagnosticsGenerator.AppendDiagnostics(first);
        DiagnosticsGenerator.AppendDiagnostics(second);

        Assert.Equal(
            first.Tags.Select(t => (t.Id, t.Name)),
            second.Tags.Select(t => (t.Id, t.Name)));
        Assert.Equal(
            first.Devices.Select(d => (d.Id, d.Name)),
            second.Devices.Select(d => (d.Id, d.Name)));
    }

    [Fact]
    public void Validate_RejectsSystemEntitiesInSourceConfig()
    {
        var config = CreateConfig();
        config.Tags = config.Tags.Concat(
        [
            new TagDefinition { Id = new TagId(2), Name = "@Line1.Connected", DataType = TagDataType.Discrete, DeviceId = new DeviceId(0) }
        ]).ToArray();

        var errors = ProjectValidator.Validate(config);

        Assert.Contains(errors, e => e.Contains("'@'"));
    }

    [Fact]
    public void Validate_RejectsNonProcessOriginInSourceConfig()
    {
        var config = CreateConfig();
        config.Tags[0].Origin = TagOrigin.Diagnostics;

        var errors = ProjectValidator.Validate(config);

        Assert.Contains(errors, e => e.Contains("Origin"));
    }

    [Fact]
    public void Save_LoadedConfig_DoesNotPersistDiagnostics()
    {
        ProjectWriter.Save(CreateConfig(), _dir);
        var loaded = ProjectLoader.Load(_dir); // здесь диагностика уже сгенерирована

        ProjectWriter.Save(loaded, _dir);      // сохраняем загруженное — диагностика не должна попасть в файлы

        var tagsJson = File.ReadAllText(Path.Combine(_dir, "tags.json"));
        var devicesJson = File.ReadAllText(Path.Combine(_dir, "devices.json"));
        Assert.DoesNotContain("@", tagsJson);
        Assert.DoesNotContain("@", devicesJson);
        Assert.DoesNotContain("Diagnostics", tagsJson);

        // и повторная загрузка даёт ту же конфигурацию, что и первая
        var reloaded = ProjectLoader.Load(_dir);
        Assert.Equal(loaded.Tags.Select(t => (t.Id, t.Name)), reloaded.Tags.Select(t => (t.Id, t.Name)));
    }
}
