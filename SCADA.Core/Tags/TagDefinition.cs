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

    /// <summary>Писать тег в архив (M4). По умолчанию не пишется —
    /// логируются 10–30 % тегов (ТЗ §1.1, §8.3).</summary>
    public bool IsArchived { get; set; }

    /// <summary>
    /// Квантование значения до N значащих разрядов перед записью в архив
    /// (docs/archive-format.md §7.3). С потерями — только по явному указанию.
    /// null — без квантования.
    /// </summary>
    public int? Precision { get; set; }

    public string Units { get; set; } = "";
    public bool IsWritable { get; set; }

    /// <summary>Любая запись в этот тег требует подтверждения в UI (M7).
    /// Страховка опасной точки: наследуется всеми элементами схемы,
    /// элемент может переопределить своим Confirmation (Auto|Always|Never).</summary>
    public bool RequiresWriteConfirmation { get; set; }

    public double? InitValue { get; set; }
    public bool IsPersistent { get; set; }

    /// <summary>Происхождение тега: из исходного проекта или сгенерирован системой.</summary>
    public TagOrigin Origin { get; set; } = TagOrigin.Process;

     public TagLoggingConfiguration? Logging { get; set; }

}
