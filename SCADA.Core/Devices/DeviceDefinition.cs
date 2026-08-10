
using SCADA.Core.Channels;

namespace SCADA.Core.Devices;

public class DeviceDefinition
{
    public required DeviceId Id { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = "";
    public required string DriverName { get; init; }
    public required ChannelId ChannelId { get; init; }
    public string Configuration { get; init; } = "";
}
