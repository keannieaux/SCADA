namespace SCADA.Core.Alarms;

/// <summary>
/// Конфигурация сигнализации проекта (docs/M5-plan.md §4.3).
/// Исходная форма — alarms.json; отсутствие файла = проект без аварий.
/// </summary>
public class AlarmConfiguration
{
    public IReadOnlyList<AlarmRule> Rules { get; set; } = [];

    /// <summary>Глобальные шаблоны сообщений по ключам
    /// (thresholdActive, thresholdNormal, expressionActive, ...).</summary>
    public IReadOnlyDictionary<string, string> Templates { get; set; }
        = new Dictionary<string, string>();

    public SoundConfiguration Sound { get; set; } = new();

    public AlarmDefaults Defaults { get; set; } = new();
}

/// <summary>Настройки звука (§2.8). Звук — функция UI-слоя, движок о нём не знает.</summary>
public class SoundConfiguration
{
    public bool Enabled { get; set; } = true;

    /// <summary>severity → звуковой файл проекта. Отсутствие уровня = встроенный
    /// звук UI. Файлы упаковываются в .scadapkg при сборке.</summary>
    public Dictionary<AlarmSeverity, string> Files { get; set; } = new();
}

/// <summary>Проектные дефолты правил; правило может переопределить.</summary>
public class AlarmDefaults
{
    /// <summary>Анти-дребезг по времени по умолчанию. 0 — без задержки.</summary>
    public int MinDurationMs { get; set; }
}
