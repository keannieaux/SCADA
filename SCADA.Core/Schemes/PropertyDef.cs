namespace SCADA.Core.Schemes;

/// <summary>
/// Дескриптор свойства вида элемента (концепт §3.2): id стабилен и пишется в
/// пакет, поэтому id никогда не переиспользуются — новое свойство = новый id.
/// Разреженное хранение: экземпляр хранит только значения ≠ умолчанию.
/// Панель свойств редактора генерируется из этих дескрипторов.
/// </summary>
public sealed record PropertyDef(
    int Id,
    string Name,
    PropertyType Type,
    PropertyValue Default,
    bool Animatable,
    string Category)
{
    /// <summary>Для Choice: допустимые варианты (индекс → подпись).</summary>
    public IReadOnlyList<string>? Choices { get; init; }
}
