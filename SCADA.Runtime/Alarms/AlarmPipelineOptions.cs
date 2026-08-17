using SCADA.Core.Tags;

namespace SCADA.Runtime.Alarms;

/// <summary>Настройки конвейера сигнализации, биндятся из Runtime:Alarms.</summary>
public sealed class AlarmPipelineOptions
{
    /// <summary>Период обхода изменений TagTable и проверки отложенных фронтов.</summary>
    public int TickIntervalMs { get; set; } = 100;

    /// <summary>Как часто запускается retention-чистка журнала.</summary>
    public int RetentionCheckIntervalMinutes { get; set; } = 60;

    /// <summary>Сколько последних событий журнала читается для восстановления
    /// активных аварий при старте (§7.3).</summary>
    public int RecoveryReadLimit { get; set; } = 100_000;
}
