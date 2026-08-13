using SCADA.Core.Devices;

namespace SCADA.Core.Tags;

public class TagDefinition
{
    public required TagId Id { get; set; }
    public required string Name { get; set; }
    public string Description { get; set; } = "";
    public required TagDataType DataType { get; set; }

    public required DeviceId DeviceId { get; set; }

    public string Address { get; set; } = "";

    public double ScaleFactor { get; set; } = 1.0;
    public double ScaleOffset { get; set; } = 0.0;

    public double? MinValue { get; set; }
    public double? MaxValue { get; set; }
    public double? Deadband { get; set; }

    public string Units { get; set; } = "";
    public bool IsWritable { get; set; }

    public double? InitValue { get; set; }
    public bool IsPersistent { get; set; }

    /// <summary>Происхождение тега: из исходного проекта или сгенерирован системой.</summary>
    public TagOrigin Origin { get; set; } = TagOrigin.Process;

     public TagLoggingConfiguration? Logging { get; set; }

}
