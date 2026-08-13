using SCADA.Core.Channels;
using SCADA.Core.Devices;
using SCADA.Package.Builder.Sections;
using SCADA.Package.Sections;

namespace SCADA.Package.Tests;

public class DevicesSectionTests
{
    [Fact]
    public void RoundTrip_ChannelsAndDevices_AllFieldsPreserved()
    {
        var channels = new List<ChannelDefinition>
        {
            new()
            {
                Id = new ChannelId(0),
                Name = "Line1",
                Description = "Основная линия",
                ChannelType = "modbus-tcp",
                Configuration = "192.168.0.10:502"
            }
        };
        var devices = new List<DeviceDefinition>
        {
            new()
            {
                Id = new DeviceId(0),
                Name = "PLC1",
                Description = "Котельная",
                ChannelId = new ChannelId(0),
                DriverName = "modbus-tcp",
                Configuration = "unit=1"
            },
            new()
            {
                Id = new DeviceId(1),
                Name = "Local",
                ChannelId = new ChannelId(0),
                DriverName = "internal"
                // Description и Configuration — по умолчанию (пустые строки)
            }
        };

        var bytes = DevicesSectionWriter.Write(channels, devices);
        var (restoredChannels, restoredDevices) = DevicesSectionReader.Read(bytes);

        var channel = Assert.Single(restoredChannels);
        Assert.Equal(0, channel.Id.Value);
        Assert.Equal("Line1", channel.Name);
        Assert.Equal("Основная линия", channel.Description);
        Assert.Equal("modbus-tcp", channel.ChannelType);
        Assert.Equal("192.168.0.10:502", channel.Configuration);

        Assert.Equal(2, restoredDevices.Count);
        var plc = restoredDevices[0];
        Assert.Equal("PLC1", plc.Name);
        Assert.Equal("Котельная", plc.Description);
        Assert.Equal(0, plc.ChannelId.Value);
        Assert.Equal("modbus-tcp", plc.DriverName);
        Assert.Equal("unit=1", plc.Configuration);

        var local = restoredDevices[1];
        Assert.Equal("Local", local.Name);
        Assert.Equal("", local.Description);
        Assert.Equal("", local.Configuration);
    }

    [Fact]
    public void RoundTrip_EmptySection_Works()
    {
        var bytes = DevicesSectionWriter.Write([], []);
        var (channels, devices) = DevicesSectionReader.Read(bytes);

        Assert.Empty(channels);
        Assert.Empty(devices);
    }
}
