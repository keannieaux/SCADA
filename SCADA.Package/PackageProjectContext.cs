using System.Text.Json.Serialization;

namespace SCADA.Package;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true
)]
[JsonSerializable(typeof(PackageManifest))]
public partial class PackageJsonContext : JsonSerializerContext
{
}
