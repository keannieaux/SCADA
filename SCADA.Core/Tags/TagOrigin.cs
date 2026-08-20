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
    Alarm,

    /// <summary>Сгенерирован подсистемой сессий: @User.*, @Right.*, @Station.*
    /// (docs/session-tags-concept.md §3). Значения таких тегов персональны
    /// для АРМа — область <see cref="TagScope.Session"/>.</summary>
    Session = 3
}
