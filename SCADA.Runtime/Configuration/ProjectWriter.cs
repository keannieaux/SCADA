using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace SCADA.Runtime.Configuration;

/// <summary>
/// Сохранение конфигурации проекта в исходной форме (ТЗ §14.1).
/// Симметричен ProjectLoader: тот же формат, тот же контекст сериализации.
/// Перед записью конфигурация проверяется ProjectValidator'ом —
/// битый конфиг на диск не попадает.
/// </summary>
public static class ProjectWriter
{
    public static void Save(ProjectConfiguration config, string projectDirectory)
    {
        // сохраняется только исходная форма: системная диагностика (§7.4)
        // генерируется при каждой загрузке и в файлы не пишется
        var sourceConfig = new ProjectConfiguration
        {
            Name = config.Name,
            Version = config.Version,
            Channels = config.Channels,
            Devices = config.Devices.Where(d => !DiagnosticsGenerator.IsSystemDevice(d)).ToArray(),
            Tags = config.Tags.Where(t => !DiagnosticsGenerator.IsSystemTag(t)).ToArray()
        };

        // последняя линия обороны: даже если вызвали в обход редактора,
        // невалидная конфигурация не будет записана
        var errors = ProjectValidator.Validate(sourceConfig);
        if (errors.Count > 0)
            throw new ProjectConfigurationException(errors);

        Directory.CreateDirectory(projectDirectory);

        WriteFile(projectDirectory, "project.json",
            new ProjectFile
            {
                FormatVersion = ProjectLoader.CurrentFormatVersion,
                Name = sourceConfig.Name,
                Version = sourceConfig.Version,
                StartScheme = sourceConfig.StartScheme
            },
            ProjectJsonContext.Default.ProjectFile);

        WriteFile(projectDirectory, "devices.json",
            new DevicesFile
            {
                FormatVersion = ProjectLoader.CurrentFormatVersion,
                Channels = sourceConfig.Channels,
                Devices = sourceConfig.Devices
            },
            ProjectJsonContext.Default.DevicesFile);

        WriteFile(projectDirectory, "tags.json",
            new TagsFile
            {
                FormatVersion = ProjectLoader.CurrentFormatVersion,
                Tags = sourceConfig.Tags
            },
            ProjectJsonContext.Default.TagsFile);
    }

    // Атомарная запись: сначала temp-файл, потом замена.
    // При сбое посередине на диске останется либо старый файл целиком,
    // либо новый целиком — но не половина нового.
    private static void WriteFile<T>(string directory, string fileName, T value, JsonTypeInfo<T> typeInfo)
    {
        var json = JsonSerializer.Serialize(value, typeInfo);
        var path = Path.Combine(directory, fileName);
        var tempPath = path + ".tmp";

        File.WriteAllText(tempPath, json);
        File.Move(tempPath, path, overwrite: true);
    }
}
