using SCADA.Core.Tags;

namespace SCADA.Runtime.Historian;

/// <summary>
/// Определяет режим логирования тега из его конфигурации.
/// </summary>
public static class LoggingModeHelper
{
    public static LoggingMode Infer(TagLoggingConfiguration? config)
    {
        if (config is null)
            return LoggingMode.Periodic;
        if (config.LogOnChange)
            return LoggingMode.OnChange;
        if (config.Schedule.Count > 0)
            return LoggingMode.Schedule;
        return LoggingMode.Periodic;
    }
}
