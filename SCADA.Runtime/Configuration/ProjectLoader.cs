using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

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

        // если файлы не прочитались — дальше проверять нечего
        if (projectFile is null || devicesFile is null || tagsFile is null)
            throw new ProjectConfigurationException(errors);

        CheckFormatVersion(projectFile.FormatVersion, "project.json", errors);
        CheckFormatVersion(devicesFile.FormatVersion, "devices.json", errors);
        CheckFormatVersion(tagsFile.FormatVersion, "tags.json", errors);

        var config = new ProjectConfiguration
        {
            Name = projectFile.Name,
            Version = projectFile.Version,
            Tags = tagsFile.Tags,
            Devices = devicesFile.Devices,
            Channels = devicesFile.Channels
        };

        // правила целостности живут в ProjectValidator — те же, что использует редактор
        errors.AddRange(ProjectValidator.Validate(config));

        if (errors.Count > 0)
            throw new ProjectConfigurationException(errors);

        // диагностические теги каналов (§7.4) добавляются после валидации
        // исходной формы; PackageBuilder идёт через этот же Load, поэтому
        // Id диагностики совпадают между пакетом и рантаймом
        DiagnosticsGenerator.AppendDiagnostics(config);

        return config;
    }

    private static T? LoadFile<T>(string directory, string fileName,
        JsonTypeInfo<T> typeInfo, List<string> errors)
    {
        var path = Path.Combine(directory, fileName);
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

    private static void CheckFormatVersion(int version, string fileName, List<string> errors)
    {
        if (version != CurrentFormatVersion)
            errors.Add($"{fileName}: неподдерживаемая версия формата {version} (поддерживается {CurrentFormatVersion})");
    }
}
