namespace SCADA.Historian;

/// <summary>
/// Возможности реализации IArchiveStore. Конвейер добавляет недостающую
/// агрегацию/ротацию, если стор их не поддерживает.
/// </summary>
[Flags]
public enum StoreCapabilities
{
    None = 0,
    RawRead = 1,
    NativeAggregation = 2,
    NativeRetention = 4
}
