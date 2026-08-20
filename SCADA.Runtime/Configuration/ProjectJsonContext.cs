using System.Text.Json.Serialization;
using SCADA.Core.Alarms;
using SCADA.Core.Schemes;
using SCADA.Core.Tags;
using SCADA.Core.Users;

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
                 typeof(JsonStringEnumConverter<AlarmSeverity>),
                 typeof(JsonStringEnumConverter<ElementKind>),
                 typeof(JsonStringEnumConverter<StopMapping>),
                 typeof(JsonStringEnumConverter<SchemeEventKind>),
                 typeof(JsonStringEnumConverter<TemplateParameterType>)])]

[JsonSerializable(typeof(TagDefinition))]
[JsonSerializable(typeof(ProjectFile))]
[JsonSerializable(typeof(DevicesFile))]
[JsonSerializable(typeof(TagsFile))]
[JsonSerializable(typeof(AlarmsFile))]
[JsonSerializable(typeof(RolesFile))]
[JsonSerializable(typeof(SchemeFile))]
[JsonSerializable(typeof(Dictionary<string, double>))]
[JsonSerializable(typeof(List<UserDefinition>))]
public partial class ProjectJsonContext: JsonSerializerContext
{

}
