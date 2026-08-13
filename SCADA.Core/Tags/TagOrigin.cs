namespace SCADA.Core.Tags;

/// <summary>
/// Происхождение тега. Process — описан инженером в исходном проекте;
/// Diagnostics — сгенерирован системой при загрузке (диагностика каналов, §7.4)
/// и в исходные файлы никогда не сохраняется.
/// </summary>
public enum TagOrigin : byte
{
    Process,
    Diagnostics
}
