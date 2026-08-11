using SCADA.Core.Devices;

namespace SCADA.Core.Tags;

public class TagDefinition
{
    public required TagId Id { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = "";
    public required TagDataType DataType { get; init; }

    public required DeviceId DeviceId { get; init; }

    public string Address { get; init; } = "";

    public double ScaleFactor { get; init; } = 1.0;
    public double ScaleOffset { get; init; } = 0.0;

    public double? MinValue { get; init; }
    public double? MaxValue { get; init; }
    public double? Deadband { get; init; }

    public string Units { get; init; } = "";
    public bool IsWritable { get; init; }

    public double? InitValue { get; init; }
    public bool IsPersistent { get; init; }

     public TagLoggingConfiguration? Logging { get; init; }

}
