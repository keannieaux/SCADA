using System.Text.Json;
using System.Text.Json.Serialization;
using SCADA.Core.Tags;

namespace SCADA.Runtime.Configuration;

public sealed class TagIdJsonConverter : JsonConverter<TagId>
{
    public override TagId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new(reader.GetInt32());

    public override void Write(Utf8JsonWriter writer, TagId value, JsonSerializerOptions options)
        => writer.WriteNumberValue(value.Value);
}
