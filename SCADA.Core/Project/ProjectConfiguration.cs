using SCADA.Core.Channels;
using SCADA.Core.Devices;
using SCADA.Core.Tags;

public class ProjectConfiguration
{
    public required string Name { get; init; }
    public string Version { get; init; } = "1.0";

    public IReadOnlyList<TagDefinition> Tags { get; init; } = Array.Empty<TagDefinition>();
    public IReadOnlyList<DeviceDefinition> Devices { get; init; } = Array.Empty<DeviceDefinition>();
    public IReadOnlyList<ChannelDefinition> Channels { get; init; } = Array.Empty<ChannelDefinition>();
}
