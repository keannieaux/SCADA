using SCADA.Core.Tags;

namespace SCADA.Runtime.Runtime;

/// <summary>
/// Ряд сырых значений одного тега — результат запроса истории через
/// <see cref="IRuntimeClient"/> (docs/archive-format.md §13.2).
/// </summary>
/// <remarks>
/// Массив, а не буфер вызывающего: через границу процесса управлять чужой
/// памятью нельзя, и remote-реализация всё равно материализует ответ.
/// </remarks>
public sealed record HistorySeries(
    TagId Id,
    TagValue[] Points,
    LoggingMode Mode,
    bool Downsampled)
{
    public static HistorySeries Empty(TagId id) =>
        new(id, [], LoggingMode.Periodic, false);
}

/// <summary>
/// Ряд агрегатов одного тега за интервалы одинаковой длины.
/// </summary>
public sealed record BucketSeries(
    TagId Id,
    ArchiveBucket[] Buckets,
    LoggingMode Mode,
    bool Downsampled)
{
    public static BucketSeries Empty(TagId id) =>
        new(id, [], LoggingMode.Periodic, false);
}
