using SCADA.Alarms;
using SCADA.Core.Alarms;
using SCADA.Runtime.Alarms;
using SCADA.Runtime.Historian;

namespace SCADA.Runtime.Runtime;

/// <summary>
/// Параметры запуска <see cref="RuntimeHost"/>. Заполняются вызывающим
/// (сервер биндит их из appsettings), сам хост конфигурацию процесса не читает.
/// </summary>
public sealed record RuntimeHostOptions
{
    /// <summary>Каталог проекта (dev-режим) или путь к .scadapkg (боевая поставка, ТЗ §14.2).</summary>
    public required string ProjectPath { get; init; }

    /// <summary>Период опроса устройств.</summary>
    public TimeSpan PollPeriod { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Настройки журнала аварий.</summary>
    public JournalOptions Journal { get; init; } = new();

    /// <summary>Настройки конвейера сигнализации.</summary>
    public AlarmPipelineOptions Alarms { get; init; } = new();

    /// <summary>Настройки архива.</summary>
    public ArchiveOptions Archive { get; init; } = new();

    /// <summary>Пределы запросов истории (§14.1).</summary>
    public HistoryQueryLimits HistoryLimits { get; init; } = new();

    /// <summary>
    /// dev-режим: компиляция expression-правил на месте (компилятор у
    /// вызывающего, ТЗ §5.4 — боевая поставка без компилятора).
    /// null → в dev-режиме expression-правила пропускаются с предупреждением;
    /// в пакетном режиме используется code.bin и это свойство игнорируется.
    /// </summary>
    public Func<AlarmRule, PreparedAlarmRule?>? ExpressionFactory { get; init; }
}
