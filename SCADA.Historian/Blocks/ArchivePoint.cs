using SCADA.Core.Tags;

namespace SCADA.Historian;

/// <summary>
/// Одна точка архивного потока: метка времени, значение, качество.
/// Это единица, с которой работают кодеки и блоки.
/// </summary>
public readonly record struct ArchivePoint(
    long TimestampUtcMs,
    double Value,
    Quality Quality);
