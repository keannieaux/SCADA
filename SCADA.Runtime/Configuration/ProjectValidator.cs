using SCADA.Core.Tags;

namespace SCADA.Runtime.Configuration;

/// <summary>
/// Правила целостности конфигурации проекта. Единственное место,
/// где эти правила живут: загрузчик вызывает их после чтения файлов,
/// редактор — для проверки перед сохранением.
/// Новая сущность конфигурации (alarms, users, archive-groups) =
/// новый приватный метод + одна строка в Validate.
/// Validate работает с ИСХОДНОЙ формой проекта: сгенерированной системной
/// диагностики (§7.4) в ней быть не должно — это проверяется.
/// </summary>
public static class ProjectValidator
{
    /// <summary>
    /// Проверяет конфигурацию и возвращает список всех найденных ошибок.
    /// Пустой список = конфигурация корректна.
    /// </summary>
    public static IReadOnlyList<string> Validate(ProjectConfiguration config)
    {
        var errors = new List<string>();
        ValidateReferences(config, errors);
        ValidateTagIds(config.Tags, errors);
        ValidateNoSystemEntities(config, errors);
        return errors;
    }

    private static void ValidateReferences(ProjectConfiguration config, List<string> errors)
    {
        var deviceIds = config.Devices.Select(d => d.Id).ToHashSet();
        var channelIds = config.Channels.Select(c => c.Id).ToHashSet();

        foreach (var tag in config.Tags)
            if (!deviceIds.Contains(tag.DeviceId))
                errors.Add($"Тег '{tag.Name}' (id={tag.Id.Value}): устройство id={tag.DeviceId.Value} не найдено");

        foreach (var device in config.Devices)
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

    // системные сущности генерируются при загрузке (§7.4) — в исходной форме
    // их быть не должно: иначе возможны коллизии с тем, что создаст генератор
    private static void ValidateNoSystemEntities(ProjectConfiguration config, List<string> errors)
    {
        foreach (var device in config.Devices)
            if (DiagnosticsGenerator.IsSystemDevice(device))
                errors.Add($"Устройство '{device.Name}': префикс '{DiagnosticsGenerator.SystemPrefix}' зарезервирован за системной диагностикой");

        foreach (var tag in config.Tags)
        {
            if (tag.Name.StartsWith(DiagnosticsGenerator.SystemPrefix))
                errors.Add($"Тег '{tag.Name}' (id={tag.Id.Value}): префикс '{DiagnosticsGenerator.SystemPrefix}' зарезервирован за системной диагностикой");
            if (tag.Origin != TagOrigin.Process)
                errors.Add($"Тег '{tag.Name}' (id={tag.Id.Value}): Origin={tag.Origin} недопустим в исходном проекте — системные теги генерируются при загрузке");
        }
    }
}
