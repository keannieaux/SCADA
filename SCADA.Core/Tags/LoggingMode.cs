namespace SCADA.Core.Tags;

/// <summary>
/// Режим логирования тега в архив (docs/archive-format.md §6, §8.4).
/// Определяет семантику пропуска отсчётов: Periodic — разрыв, OnChange — удержание.
/// </summary>
public enum LoggingMode : byte
{
    Periodic = 0,
    OnChange = 1,
    Schedule = 2
}
