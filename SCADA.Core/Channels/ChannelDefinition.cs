namespace SCADA.Core.Channels;

public class ChannelDefinition
{
    public required ChannelId Id { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = "";
    public required string ChannelType { get; init; }
    public string Configuration { get; init; } = "";
}
