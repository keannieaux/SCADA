namespace SCADA.Core.Tags;

/// <summary>
/// Происхождение тега. Process — описан инженером в исходном проекте;
/// Diagnostics — сгенерирован системой при загрузке (диагностика каналов, §7.4);
/// Alarm — сгенерирован из правил сигнализации (@Alarm.*/@AlarmGroup.*/
/// @AlarmSystem.*, концепт §10). Системные в исходные файлы никогда не
/// сохраняются.
/// </summary>
public enum TagOrigin : byte
{
    Process,
    Diagnostics,
    Alarm
}
