
using SCADA.Core.Channels;

namespace SCADA.Core.Devices;

public class DeviceDefinition
{
    public required DeviceId Id { get; set; }
    public required string Name { get; set; }
    public string Description { get; set; } = "";
    public required string DriverName { get; set; }
    public required ChannelId ChannelId { get; set; }
    public string Configuration { get; set; } = "";
}
