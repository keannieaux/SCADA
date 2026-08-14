using SCADA.Core.Tags;

namespace SCADA.Runtime.Historian;

/// <summary>
/// Результат чтения истории (docs/archive-format.md §13.1).
/// Содержит число возвращённых точек/бакетов, режим логирования потока
/// и признак того, что ответ был прорежен до агрегатов.
/// </summary>
public readonly record struct HistoryResult(
    int Count,
    LoggingMode Mode,
    bool Downsampled);
