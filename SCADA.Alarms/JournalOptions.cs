namespace SCADA.Alarms;

/// <summary>
/// Настройки журнала аварий — биндятся из секции Runtime:Journal в appsettings
/// (docs/M5-plan.md §2.11), по образцу ArchiveOptions из Runtime:Archive.
/// </summary>
public sealed class JournalOptions
{
    /// <summary>Срок хранения событий, сутки.</summary>
    public int RetentionDays { get; set; } = 365;

    /// <summary>Нижний предел retention при принудительном сокращении
    /// DiskSpaceSupervisor'ом — дальше освобождать нечего (ТЗ §8.9).</summary>
    public int MinRetentionDays { get; set; } = 30;
}
