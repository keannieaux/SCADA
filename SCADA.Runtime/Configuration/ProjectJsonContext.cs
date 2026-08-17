using System.Text.Json.Serialization;
using SCADA.Core.Alarms;
using SCADA.Core.Tags;

namespace SCADA.Runtime.Configuration;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    Converters =[typeof(TagIdJsonConverter),
                 typeof(DeviceIdJsonConverter),
                 typeof(ChannelIdJsonConverter),
                 typeof(JsonStringEnumConverter<TagDataType>),
                 typeof(JsonStringEnumConverter<TagOrigin>),
                 typeof(JsonStringEnumConverter<AlarmType>),
                 typeof(JsonStringEnumConverter<ThresholdKind>),
                 typeof(JsonStringEnumConverter<AlarmSeverity>)])]

[JsonSerializable(typeof(TagDefinition))]
[JsonSerializable(typeof(ProjectFile))]
[JsonSerializable(typeof(DevicesFile))]
[JsonSerializable(typeof(TagsFile))]
[JsonSerializable(typeof(AlarmsFile))]
public partial class ProjectJsonContext: JsonSerializerContext
{

}
