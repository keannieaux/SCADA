namespace SCADA.Core.Channels;

public class ChannelDefinition
{
    public required ChannelId Id { get; set; }
    public required string Name { get; set; }
    public string Description { get; set; } = "";
    public required string ChannelType { get; set; }
    public string Configuration { get; set; } = "";
}
