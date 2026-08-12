using System.Text.Json;
using System.Text.Json.Serialization;
using SCADA.Core.Channels;

namespace SCADA.Runtime.Configuration;

public sealed class ChannelIdJsonConverter : JsonConverter<ChannelId>
{
    public override ChannelId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new(reader.GetInt32());

    public override void Write(Utf8JsonWriter writer, ChannelId value, JsonSerializerOptions options)
        => writer.WriteNumberValue(value.Value);
}
