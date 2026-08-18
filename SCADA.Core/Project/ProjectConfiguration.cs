using SCADA.Core.Alarms;
using SCADA.Core.Channels;
using SCADA.Core.Devices;
using SCADA.Core.Schemes;
using SCADA.Core.Tags;

public class ProjectConfiguration
{
    public required string Name { get; set; }
    public string Version { get; set; } = "1.0";

    public IReadOnlyList<TagDefinition> Tags { get; set; } = Array.Empty<TagDefinition>();
    public IReadOnlyList<DeviceDefinition> Devices { get; set; } = Array.Empty<DeviceDefinition>();
    public IReadOnlyList<ChannelDefinition> Channels { get; set; } = Array.Empty<ChannelDefinition>();

    /// <summary>Правила сигнализации (M5). Пустая конфигурация = проект без аварий.</summary>
    public AlarmConfiguration Alarms { get; set; } = new();

    /// <summary>Схемы (экраны) из schemes/*.scheme (концепт §3, §6).
    /// Пустой список = проект без визуализации.</summary>
    public List<Scheme> Schemes { get; set; } = [];

    /// <summary>Шаблоны из templates/*.scheme — параметризованные фрагменты
    /// схем (концепт §7).</summary>
    public List<SchemeTemplate> Templates { get; set; } = [];
}
