namespace SCADA.Core.Schemes;

/// <summary>
/// События (концепт §5.1). У элементов — указательные (v1 реализует только
/// Click, остальные заложены в формат — это байт-тип перед списком действий).
/// У схемы/шаблона — жизненный цикл экрана: Opened/Closed (попап при входе,
/// запись «оператор на экране», старт/стоп звука). Один enum на оба уровня,
/// номера стабильны.
/// </summary>
public enum SchemeEventKind : byte
{
    Click = 0,
    MouseDown = 1,
    MouseUp = 2,
    DoubleClick = 3,
    PointerEnter = 4,
    PointerLeave = 5,

    // уровень схемы/шаблона
    Opened = 6,
    Closed = 7,
}

/// <summary>Событие → цепочка действий (последовательно, с условиями).</summary>
public sealed class SchemeEvent
{
    public required SchemeEventKind Kind { get; init; }
    public required IReadOnlyList<SchemeAction> Actions { get; init; }
}
