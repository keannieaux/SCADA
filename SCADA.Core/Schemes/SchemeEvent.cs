namespace SCADA.Core.Schemes;

/// <summary>
/// События элемента (концепт §5.1). v1 реализует только Click, остальные
/// заложены в формат — это байт-тип перед списком действий.
/// </summary>
public enum SchemeEventKind : byte
{
    Click = 0,
    MouseDown = 1,
    MouseUp = 2,
    DoubleClick = 3,
    PointerEnter = 4,
    PointerLeave = 5,
}

/// <summary>Событие → цепочка действий (последовательно, с условиями).</summary>
public sealed class SchemeEvent
{
    public required SchemeEventKind Kind { get; init; }
    public required IReadOnlyList<SchemeAction> Actions { get; init; }
}
