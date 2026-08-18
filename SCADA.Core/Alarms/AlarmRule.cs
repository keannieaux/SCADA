namespace SCADA.Core.Alarms;

/// <summary>
/// Правило сигнализации (docs/M5-plan.md §4.1). Исходная форма — alarms.json,
/// компилируемая — alarms.bin в пакете. Поля Compiled* заполняются только
/// при сборке пакета, в исходной форме пустые.
/// </summary>
public class AlarmRule
{
    /// <summary>Уникальное имя правила.</summary>
    public required string Name { get; set; }

    /// <summary>"Температура масла превышена" — подставляется в шаблон сообщения.</summary>
    public string Description { get; set; } = "";

    public AlarmType Type { get; set; }

    /// <summary>Дефолт для Expression-правил и fallback для уставок без своего severity.</summary>
    public AlarmSeverity Severity { get; set; } = AlarmSeverity.Warning;

    /// <summary>Логическая зона/объект: фильтрация журнала и группировка. На логику не влияет.</summary>
    public string Area { get; set; } = "";

    /// <summary>Требуется ли квитирование. false — возврат в норму сразу закрывает аварию.</summary>
    public bool RequiresAck { get; set; } = true;

    /// <summary>Переопределение глобального шаблона сообщения из AlarmConfiguration.Templates.</summary>
    public string? MessageTemplate { get; set; }

    /// <summary>Анти-дребезг по времени: условие должно удерживаться не меньше
    /// этого срока, иначе фронт игнорируется. 0 — без задержки. Не задано в
    /// правиле — действует AlarmDefaults.MinDurationMs.</summary>
    public int? MinDurationMs { get; set; }

    // --- Threshold ---

    public string? TagName { get; set; }

    /// <summary>Уставки с индивидуальным severity. Только для Type=Threshold.</summary>
    public IReadOnlyList<ThresholdLimit>? Limits { get; set; }

    /// <summary>Гистерезис — свойство условия, а не измерения (ТЗ M5).</summary>
    public double Hysteresis { get; set; }

    // --- Expression ---

    /// <summary>Исходный текст выражения. Только для Type=Expression.</summary>
    public string? Condition { get; set; }

    // --- заполняются при сборке пакета ---

    public int[]? CompiledTagIndices { get; set; }
    public int? CompiledExpressionIndex { get; set; }
}
