using SCADA.Core.Channels;
using SCADA.Core.Devices;
using SCADA.Core.Tags;

namespace SCADA.Runtime.Configuration;

// project.json: {"formatVersion": 1, "name": "MyProject", "version": "1.0"}
public class ProjectFile
{
    public int FormatVersion { get; init; }
    public required string Name { get; init; }
    public string Version { get; init; } = "1.0";
}

// devices.json: {"formatVersion": 1, "channels": [...], "devices": [...]}
public class DevicesFile
{
    public int FormatVersion { get; init; }
    public IReadOnlyList<ChannelDefinition> Channels { get; init; } = [];
    public IReadOnlyList<DeviceDefinition> Devices { get; init; } = [];
}

// tags.json: {"formatVersion": 1, "tags": [...]}
public class TagsFile
{
    public int FormatVersion { get; init; }
    public IReadOnlyList<TagDefinition> Tags { get; init; } = [];
}
