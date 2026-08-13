using System.Text.Json.Serialization;
using SCADA.Core.Tags;

namespace SCADA.Runtime.Configuration;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    Converters =[typeof(TagIdJsonConverter),
                 typeof(DeviceIdJsonConverter),
                 typeof(ChannelIdJsonConverter),
                 typeof(JsonStringEnumConverter<TagDataType>)])]

[JsonSerializable(typeof(TagDefinition))]
[JsonSerializable(typeof(ProjectFile))]
[JsonSerializable(typeof(DevicesFile))]
[JsonSerializable(typeof(TagsFile))]
public partial class ProjectJsonContext: JsonSerializerContext
{

}
