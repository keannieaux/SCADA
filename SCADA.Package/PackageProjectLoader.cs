using SCADA.Package.Sections;

namespace SCADA.Package;

/// <summary>
/// Загрузка конфигурации проекта из .scadapkg — вход исполнительной поставки.
/// Симметричен PackageBuilder (инженерная поставка).
/// </summary>
public static class PackageProjectLoader
{
    public static ProjectConfiguration Load(string packagePath)
    {
        using var reader = PackageReader.Open(packagePath);
        return Load(reader);
    }

    public static ProjectConfiguration Load(PackageReader reader)
    {
        var tags = TagsSectionReader.Read(reader.ReadEntry("tags.bin"));
        var (channels, devices) = DevicesSectionReader.Read(reader.ReadEntry("devices.bin"));

        return new ProjectConfiguration
        {
            Name = reader.Manifest.ProjectName,
            Version = reader.Manifest.ProjectVersion,
            Tags = tags,
            Devices = devices,
            Channels = channels
        };
    }

    /// <summary>Пул байткода проекта (выражения схем, условия и т.д.).</summary>
    public static CodePool LoadCodePool(PackageReader reader)
        => CodeSectionReader.Read(reader.ReadEntry("code.bin"));
}
