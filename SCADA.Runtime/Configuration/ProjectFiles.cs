using SCADA.Core.Alarms;
using SCADA.Core.Channels;
using SCADA.Core.Devices;
using SCADA.Core.Tags;
using SCADA.Core.Users;

namespace SCADA.Runtime.Configuration;

// project.json: {"formatVersion": 1, "name": "MyProject", "version": "1.0",
//                "startScheme": "main"}
public class ProjectFile
{
    public int FormatVersion { get; set; }
    public required string Name { get; set; }
    public string Version { get; set; } = "1.0";

    /// <summary>Стартовый экран (имя схемы из schemes/). Не задан —
    /// UI открывает первый по алфавиту.</summary>
    public string? StartScheme { get; set; }
}

// devices.json: {"formatVersion": 1, "channels": [...], "devices": [...]}
public class DevicesFile
{
    public int FormatVersion { get; set; }
    public IReadOnlyList<ChannelDefinition> Channels { get; set; } = [];
    public IReadOnlyList<DeviceDefinition> Devices { get; set; } = [];
}

// tags.json: {"formatVersion": 1, "tags": [...]}
public class TagsFile
{
    public int FormatVersion { get; set; }
    public IReadOnlyList<TagDefinition> Tags { get; set; } = [];
}

// alarms.json: {"formatVersion": 1, "templates": {...}, "sound": {...},
//               "defaults": {...}, "rules": [...]}
// Файл опционален (docs/M5-plan.md §2.2): отсутствие = проект без аварий.
public class AlarmsFile
{
    public int FormatVersion { get; set; }
    public Dictionary<string, string> Templates { get; set; } = new();
    public SoundConfiguration Sound { get; set; } = new();
    public AlarmDefaults Defaults { get; set; } = new();
    public IReadOnlyList<AlarmRule> Rules { get; set; } = [];
}

// roles.json: {"formatVersion": 1, "roles": [...], "minPasswordLength": 4,
//              "sessionTimeoutMinutes": 10}
// Файл опционален (docs/users-plan.md §6): отсутствие = проект без
// разграничения, рантайм работает в AuthMode.Local. Пользователей в файле
// нет — users.json живёт в папке проекта и загрузчиком не читается (§3).
public class RolesFile
{
    public int FormatVersion { get; set; }
    public IReadOnlyList<RoleDefinition> Roles { get; set; } = [];
    public int MinPasswordLength { get; set; } = 4;
    public int SessionTimeoutMinutes { get; set; } = 10;
}
