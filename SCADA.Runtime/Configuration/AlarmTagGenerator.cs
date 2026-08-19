using SCADA.Core.Alarms;
using SCADA.Core.Channels;
using SCADA.Core.Devices;
using SCADA.Core.Tags;

namespace SCADA.Runtime.Configuration;

/// <summary>
/// Генератор системных тегов сигнализации (концепт §10) — по образцу
/// DiagnosticsGenerator. Из имён правил строит:
/// - по 3 тега на правило (@Alarm.&lt;имя&gt;.*),
/// - по 4 тега на каждый префикс dotted-имён (@AlarmGroup.&lt;путь&gt;.*),
/// - корневой набор @AlarmSystem.* — всегда, даже без правил: привязки
///   глобального баннера должны компилироваться в любом проекте.
///
/// Инварианты — те же, что у DiagnosticsGenerator: детерминизм (один проект —
/// одни Id), плотный ряд TagId, Origin=Alarm, имена с '@' (валидатор запрещает
/// их в исходниках, ProjectWriter не сохраняет). Порядок генерации — правила
/// в порядке alarms.json, группы по первому появлению, корень последним;
/// состав метрик append-only (AlarmTags).
/// </summary>
public static class AlarmTagGenerator
{
    /// <summary>Псевдоканал подсистемы сигнализации (см. ArchiveChannelId).</summary>
    public static readonly ChannelId AlarmChannelId = new(-2);

    public const string DeviceName = "@Alarms";

    /// <summary>Добавить системные теги аварий в загруженную конфигурацию.
    /// Вызывается строго после DiagnosticsGenerator.AppendDiagnostics —
    /// новая подсистема не сдвигает уже назначенные Id.</summary>
    public static void AppendAlarmTags(ProjectConfiguration config)
    {
        var devices = config.Devices.ToList();
        var tags = config.Tags.ToList();
        int nextDeviceId = devices.Count == 0 ? 0 : devices.Max(d => d.Id.Value) + 1;
        int nextTagId = tags.Count == 0 ? 0 : tags.Max(t => t.Id.Value) + 1;

        var device = new DeviceDefinition
        {
            Id = new DeviceId(nextDeviceId),
            Name = DeviceName,
            Description = "Состояние и диагностика сигнализации",
            DriverName = "internal", // значения публикует движок аварий
            ChannelId = AlarmChannelId
        };
        devices.Add(device);

        void AddTag(string name, TagDataType dataType) =>
            tags.Add(new TagDefinition
            {
                Id = new TagId(nextTagId++),
                Name = name,
                DataType = dataType,
                DeviceId = device.Id,
                Origin = TagOrigin.Alarm,
                InitValue = 0
            });

        foreach (var rule in config.Alarms.Rules)
            foreach (var (suffix, dataType) in AlarmTags.RuleMetrics)
                AddTag(AlarmTags.RuleTag(rule.Name, suffix), dataType);

        // группы — префиксы имён правил, по первому появлению (детерминизм)
        var groupPaths = new List<string>();
        var seen = new HashSet<string>();
        foreach (var rule in config.Alarms.Rules)
            foreach (string path in AlarmTags.GroupPaths(rule.Name))
                if (seen.Add(path))
                    groupPaths.Add(path);

        foreach (string path in groupPaths)
            foreach (var (suffix, dataType) in AlarmTags.GroupMetrics)
                AddTag(AlarmTags.GroupTag(path, suffix), dataType);

        foreach (var (suffix, dataType) in AlarmTags.SystemMetrics)
            AddTag(AlarmTags.SystemTag(suffix), dataType);

        config.Devices = devices;
        config.Tags = tags;
    }
}
