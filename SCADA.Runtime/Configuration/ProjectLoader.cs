using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using SCADA.Core.Tags;

namespace SCADA.Runtime.Configuration;

public static class ProjectLoader
{
    public const int CurrentFormatVersion = 1;

    public static ProjectConfiguration Load(string projectDirectory)
    {


        var errors = new List<string>();

        var projectFile = LoadFile(projectDirectory, "project.json",
            ProjectJsonContext.Default.ProjectFile, errors);
        var devicesFile = LoadFile(projectDirectory, "devices.json",
            ProjectJsonContext.Default.DevicesFile, errors);
        var tagsFile    = LoadFile(projectDirectory, "tags.json",
            ProjectJsonContext.Default.TagsFile, errors);

        if (projectFile is null || devicesFile is null || tagsFile is null)
            throw new ProjectConfigurationException(errors);

        CheckFormatVersion(projectFile.FormatVersion, "project.json", errors);
        CheckFormatVersion(devicesFile.FormatVersion, "devices.json", errors);
        CheckFormatVersion(tagsFile.FormatVersion, "tags.json", errors);

        ValidateReferences(tagsFile.Tags, devicesFile, errors);
        ValidateTagIds(tagsFile.Tags, errors);

        if (errors.Count > 0)
            throw new ProjectConfigurationException(errors);

        return new ProjectConfiguration
        {
            Name = projectFile.Name,
            Version = projectFile.Version,
            Tags = tagsFile.Tags,
            Devices = devicesFile.Devices,
            Channels = devicesFile.Channels
        };
    }
    private static T? LoadFile<T>(string directory, string fileName,
        JsonTypeInfo<T> typeInfo,List<string> errors)
    {
        var path = Path.Combine(directory,fileName);
        if (!File.Exists(path))
        {
            errors.Add($"Не найден файл {fileName}");
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(path), typeInfo);
        }
        catch (JsonException ex)
        {
            errors.Add($"{fileName}: ошибка JSON: {ex.Message}");
            return default;
        }
    }

    private static void CheckFormatVersion(int version, string fileName,List<string> errors)
    {
        if(version != CurrentFormatVersion)
            errors.Add($"{fileName}: неподдерживаемая версия формата {version} (поддерживается {CurrentFormatVersion})");
    }

        private static void ValidateReferences(
        IReadOnlyList<TagDefinition> tags, DevicesFile devicesFile, List<string> errors)
    {
        var deviceIds = devicesFile.Devices.Select(d => d.Id).ToHashSet();
        var channelIds = devicesFile.Channels.Select(c => c.Id).ToHashSet();

        foreach (var tag in tags)
            if (!deviceIds.Contains(tag.DeviceId))
                errors.Add($"Тег '{tag.Name}' (id={tag.Id.Value}): устройство id={tag.DeviceId.Value} не найдено");

        foreach (var device in devicesFile.Devices)
            if (!channelIds.Contains(device.ChannelId))
                errors.Add($"Устройство '{device.Name}': канал id={device.ChannelId.Value} не найден");
    }

    private static void ValidateTagIds(IReadOnlyList<TagDefinition> tags, List<string> errors)
    {
        foreach (var group in tags.GroupBy(t => t.Id).Where(g => g.Count() > 1))
            errors.Add($"Дубликат TagId {group.Key.Value}: {string.Join(", ", group.Select(t => t.Name))}");

        foreach (var group in tags.GroupBy(t => t.Name).Where(g => g.Count() > 1))
            errors.Add($"Дубликат имени тега '{group.Key}'");

        // Id должны быть 0..count-1 без дыр — TagTable индексируется напрямую
        var sorted = tags.Select(t => t.Id.Value).Order().ToArray();
        for (int i = 0; i < sorted.Length; i++)
            if (sorted[i] != i)
            {
                errors.Add($"TagId не покрывают диапазон [0, {sorted.Length}): ожидался id={i}, найден id={sorted[i]}");
                break; // одной такой ошибки достаточно
            }
    }
}


