namespace SCADA.Core.Schemes;

/// <summary>Значение свойства экземпляра: id + значение. Хранятся только
/// отличающиеся от умолчания (разреженность, концепт §3.2).</summary>
public readonly record struct ElementProperty(int PropertyId, PropertyValue Value);

/// <summary>
/// Элемент схемы (концепт §3.1). Чистые данные без Avalonia/Skia — как
/// AlarmRule: SCADA.Core ни на что не ссылается (DependencyRulesTests),
/// сериализовать может Package.Builder, рисовать — Graphics.
/// </summary>
public sealed class SchemeElement
{
    public required Guid Id { get; init; }
    public string Name { get; init; } = "";
    public required ElementKind Kind { get; init; }

    // Геометрия — фиксированные поля: мировые координаты схемы (§3.3).
    public required double X { get; init; }
    public required double Y { get; init; }
    public required double Width { get; init; }
    public required double Height { get; init; }
    public int ZOrder { get; init; }

    /// <summary>Иерархия/группы (§3.3): трансформации накапливаются от корня.
    /// null — элемент верхнего уровня.</summary>
    public Guid? ParentId { get; init; }

    /// <summary>Разреженные значения свойств (только ≠ умолчанию схемы вида).</summary>
    public IReadOnlyList<ElementProperty> Properties { get; init; } = [];

    /// <summary>Динамизация: выражение → свойство (§4).</summary>
    public IReadOnlyList<ElementBinding> Bindings { get; init; } = [];

    /// <summary>События → цепочки действий (§5).</summary>
    public IReadOnlyList<SchemeEvent> Events { get; init; } = [];

    /// <summary>Только для Kind = Control: тип hosted-контрола
    /// ("trend", "alarmview", …) — концепт §8.</summary>
    public string? ControlType { get; init; }

    /// <summary>Только для Kind = Instance: имя шаблона и параметры (§7).</summary>
    public string? TemplateName { get; init; }
    public IReadOnlyDictionary<string, string>? TemplateParameters { get; init; }
}
