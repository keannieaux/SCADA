using System.Text.Json;
using System.Text.Json.Serialization;
using SCADA.Core.Devices;

namespace SCADA.Runtime.Configuration;

public sealed class DeviceIdJsonConverter : JsonConverter<DeviceId>
{
    public override DeviceId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new(reader.GetInt32());

    public override void Write(Utf8JsonWriter writer, DeviceId value, JsonSerializerOptions options)
        => writer.WriteNumberValue(value.Value);
}
