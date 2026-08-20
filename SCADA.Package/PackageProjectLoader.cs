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
            StartScheme = reader.Manifest.StartScheme,
            Tags = tags,
            Devices = devices,
            Channels = channels,
            // M5: секция опциональна — проект без alarms.json собирается без неё
            Alarms = reader.HasEntry("alarms.bin")
                ? AlarmsSectionReader.Read(reader.ReadEntry("alarms.bin"))
                : new Core.Alarms.AlarmConfiguration(),
            // роли опциональны так же (docs/users-plan.md §4.1)
            Users = reader.HasEntry("roles.bin")
                ? RolesSectionReader.Read(reader.ReadEntry("roles.bin"))
                : new Core.Users.UsersConfiguration(),
            // схемы и шаблоны опциональны: перечисление через манифест
            // по префиксу (концепт §11.1), отсутствие — пустые списки
            Schemes = ReadEntries<Core.Schemes.Scheme>(reader, "schemes/",
                SchemeSectionReader.ReadScheme),
            Templates = ReadEntries<Core.Schemes.SchemeTemplate>(reader, "templates/",
                SchemeSectionReader.ReadTemplate)
        };
    }

    private static List<T> ReadEntries<T>(PackageReader reader, string prefix,
        Func<byte[], T> read)
        => reader.Manifest.Entries
            .Where(e => e.Name.StartsWith(prefix) && e.Name.EndsWith(".bin"))
            .Select(e => read(reader.ReadEntry(e.Name)))
            .ToList();

    /// <summary>Пул байткода проекта (выражения схем, условия и т.д.).</summary>
    public static CodePool LoadCodePool(PackageReader reader)
        => CodeSectionReader.Read(reader.ReadEntry("code.bin"));
}
