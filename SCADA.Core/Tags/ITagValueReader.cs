namespace SCADA.Core.Tags;

/// <summary>
/// Минимальный источник значений тегов для вычисления выражений.
/// ВМ выражений (EvaluationContext.Tags) вызывает только Read — поэтому
/// контракт один метод. Ему удовлетворяют и ITagTable (локальный рантайм),
/// и IRuntimeClient (в т.ч. будущий remote через gRPC, ТЗ §12): схемы и
/// панели работают через IRuntimeClient, не зная внутренностей движка.
/// </summary>
public interface ITagValueReader
{
    TagValue Read(TagId id);
}
