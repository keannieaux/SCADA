using SCADA.Core.Channels;
using SCADA.Core.Devices;
using SCADA.Core.Tags;
using SCADA.Runtime.Configuration;

namespace SCADA.Runtime.Tests;

public class ProjectWriterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

    public ProjectWriterTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static ProjectConfiguration CreateValidConfig() => new()
    {
        Name = "RoundTrip",
        Version = "2.5",
        Channels =
        [
            new ChannelDefinition { Id = new ChannelId(0), Name = "Ch0", ChannelType = "modbus-tcp", Description = "Основной канал" }
        ],
        Devices =
        [
            new DeviceDefinition { Id = new DeviceId(0), Name = "SimPLC", DriverName = "simulator", ChannelId = new ChannelId(0) },
            new DeviceDefinition { Id = new DeviceId(1), Name = "Local", DriverName = "internal", ChannelId = new ChannelId(0) }
        ],
        Tags =
        [
            new TagDefinition
            {
                Id = new TagId(0), Name = "Temperature", DataType = TagDataType.Analog,
                DeviceId = new DeviceId(0), Address = "sin:10",
                MinValue = 0, MaxValue = 150, Units = "°C", ScaleFactor = 2.0
            },
            new TagDefinition
            {
                Id = new TagId(1), Name = "Pump", DataType = TagDataType.Discrete,
                DeviceId = new DeviceId(0), Address = "square:5"
            },
            new TagDefinition
            {
                Id = new TagId(2), Name = "Mode", DataType = TagDataType.Analog,
                DeviceId = new DeviceId(1), InitValue = 42, IsPersistent = true
            }
        ]
    };

    [Fact]
    public void Save_ThenLoad_RoundTrip_PreservesConfiguration()
    {
        var original = CreateValidConfig();

        ProjectWriter.Save(original, _dir);
        var loaded = ProjectLoader.Load(_dir);

        Assert.Equal(original.Name, loaded.Name);
        Assert.Equal(original.Version, loaded.Version);
        Assert.Equal(original.Tags.Count, loaded.Tags.Count);
        Assert.Equal(original.Devices.Count, loaded.Devices.Count);
        Assert.Equal(original.Channels.Count, loaded.Channels.Count);

        // точечная проверка полей, которые легко потерять при сериализации
        var temp = loaded.Tags[0];
        Assert.Equal("Temperature", temp.Name);
        Assert.Equal(TagDataType.Analog, temp.DataType);
        Assert.Equal("sin:10", temp.Address);
        Assert.Equal(150, temp.MaxValue);
        Assert.Equal(2.0, temp.ScaleFactor);
        Assert.Equal("°C", temp.Units);

        var mode = loaded.Tags[2];
        Assert.Equal(42, mode.InitValue);
        Assert.True(mode.IsPersistent);
        Assert.Equal(new DeviceId(1), mode.DeviceId);
    }

    [Fact]
    public void Save_InvalidConfig_ThrowsAndWritesNothing()
    {
        var config = CreateValidConfig();
        // ломаем: два тега с одинаковым Id
        config = new ProjectConfiguration
        {
            Name = config.Name,
            Channels = config.Channels,
            Devices = config.Devices,
            Tags = [config.Tags[0], config.Tags[0]]
        };

        Assert.Throws<ProjectConfigurationException>(() => ProjectWriter.Save(config, _dir));

        // невалидный конфиг не должен оставить файлов на диске
        Assert.False(File.Exists(Path.Combine(_dir, "project.json")));
        Assert.False(File.Exists(Path.Combine(_dir, "tags.json")));
        Assert.False(File.Exists(Path.Combine(_dir, "devices.json")));
    }

    [Fact]
    public void Validate_ValidConfig_ReturnsNoErrors()
    {
        var errors = ProjectValidator.Validate(CreateValidConfig());

        Assert.Empty(errors);
    }
}
