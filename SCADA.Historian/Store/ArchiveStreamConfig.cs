using SCADA.Core.Tags;

namespace SCADA.Historian;

/// <summary>
/// Конфигурация архивного потока: то, что не меняется от блока к блоку.
/// </summary>
public readonly record struct ArchiveStreamConfig(
    TagDataType DataType,
    LoggingMode Mode,
    double Scale,
    double Offset);
