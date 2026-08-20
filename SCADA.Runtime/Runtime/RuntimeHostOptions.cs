using SCADA.Alarms;
using SCADA.Core.Users;
using SCADA.Runtime.Alarms;
using SCADA.Runtime.Historian;

namespace SCADA.Runtime.Runtime;

/// <summary>
/// Параметры запуска <see cref="RuntimeHost"/>. Заполняются вызывающим
/// (сервер биндит их из appsettings), сам хост конфигурацию процесса не читает.
/// </summary>
public sealed record RuntimeHostOptions
{
    /// <summary>Путь к собранному пакету .scadapkg (боевая поставка, ТЗ §14.2).
    /// Рантайм работает только с пакетом (A5.9): исходники JSON читают
    /// редактор и сборщик проекта.</summary>
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

    /// <summary>Режим аутентификации (docs/users-plan.md §6). Умолчание
    /// Local — автологин: запуск проекта без ролей и разработка экранов
    /// не должны упираться в окно входа.</summary>
    public AuthMode AuthMode { get; init; } = AuthMode.Local;
}
