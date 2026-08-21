using System.Text.Json.Serialization;
using SCADA.Core.Schemes;

namespace SCADA.Runtime.Configuration;

// schemes/<имя>.scheme и templates/<имя>.scheme (JSON, UTF-8) — концепт §3, §7.
// DTO зеркалят модель SCADA.Core/Schemes, но нарочно «рыхлые»: все поля
// опциональны, значения свойств и стопов — строки, интерпретируемые по
// PropertyType из ElementSchemas. Строгость обеспечивает SchemeFileLoader:
// неизвестное свойство или битое значение — ошибка загрузки, а не пропуск
// (исходник строгий, в отличие от бинарных секций пакета, §11.2).

// { "id": "guid?", "name": "строка?", "properties": [...], "events": [...],
//   "parameters": [...] (только templates), "elements": [...] }
public class SchemeFile
{
    public Guid? Id { get; set; }      // необязателен — генерируется
    public string? Name { get; set; }  // необязателен — имя файла

    /// <summary>Свойства уровня схемы (фон, проектный размер — §11): id из
    /// ElementSchemas.SchemeProperties.</summary>
    public List<ElementPropertyDto> Properties { get; set; } = [];

    /// <summary>События уровня экрана (Opened/Closed — §5.1).</summary>
    public List<SchemeEventDto> Events { get; set; } = [];

    /// <summary>Право на открытие экрана (docs/users-plan.md §5). Только для
    /// schemes/*.scheme; в шаблоне — ошибка загрузки: попап открывается
    /// действием, право ставится на действие.</summary>
    public string? RequiredRight { get; set; }

    /// <summary>Только для templates/*.scheme; в схеме — ошибка загрузки.</summary>
    public List<TemplateParameterDto> Parameters { get; set; } = [];
    public List<SchemeElementDto> Elements { get; set; } = [];
}

public class TemplateParameterDto
{
    public string? Name { get; set; }
    public TemplateParameterType Type { get; set; } = TemplateParameterType.String;
    public string? Default { get; set; }
}

public class SchemeElementDto
{
    public Guid? Id { get; set; }
    public string? Name { get; set; }
    public ElementKind? Kind { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public int ZOrder { get; set; }
    public Guid? ParentId { get; set; }

    /// <summary>Только для Kind = Control (концепт §8).</summary>
    public string? ControlType { get; set; }

    /// <summary>Только для Kind = Instance (концепт §7).</summary>
    public string? TemplateName { get; set; }
    public Dictionary<string, string>? TemplateParameters { get; set; }

    public List<ElementPropertyDto> Properties { get; set; } = [];
    public List<ElementBindingDto> Bindings { get; set; } = [];
    public List<SchemeEventDto> Events { get; set; } = [];

    /// <summary>Право на элемент (docs/users-plan.md §5); без него элемент
    /// недоступен в состоянии <see cref="DeniedState"/>.</summary>
    public string? RequiredRight { get; set; }

    /// <summary>Вид отказа: Disabled (умолчание) или Hidden.</summary>
    public DeniedState DeniedState { get; set; } = DeniedState.Disabled;
}

// { "id": 10, "value": "#FF33383D" } — значение строкой, тип из дескриптора.
public class ElementPropertyDto
{
    public int Id { get; set; }
    public string? Value { get; set; }
}

public class ElementBindingDto
{
    public int Property { get; set; }
    public string? Expression { get; set; }
    public StopMapping Mapping { get; set; } = StopMapping.Direct;
    public bool Volatile { get; set; }
    public List<StopDto>? Stops { get; set; }
}

public class StopDto
{
    public double Input { get; set; }
    public string? Output { get; set; }
}

public class SchemeEventDto
{
    public SchemeEventKind? Kind { get; set; }
    public List<SchemeActionDto> Actions { get; set; } = [];
}

// Действия — полиморфные по дискриминатору "type" (концепт §5.3).
// Неизвестный type — ошибка JSON (исходник строгий).
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(WriteTagActionDto), "WriteTag")]
[JsonDerivedType(typeof(ToggleTagActionDto), "ToggleTag")]
[JsonDerivedType(typeof(OpenSchemeActionDto), "OpenScheme")]
[JsonDerivedType(typeof(OpenPopupActionDto), "OpenPopup")]
[JsonDerivedType(typeof(ClosePopupActionDto), "ClosePopup")]
[JsonDerivedType(typeof(BackActionDto), "Back")]
[JsonDerivedType(typeof(ShowDialogActionDto), "ShowDialog")]
[JsonDerivedType(typeof(SetPropertyActionDto), "SetProperty")]
public abstract class SchemeActionDto
{
    /// <summary>Условие «выполнять, если…» — выражение, концепт §5.2.</summary>
    public string? Condition { get; set; }

    /// <summary>Модификатор Confirm: текст вопроса перед выполнением.</summary>
    public string? Confirm { get; set; }

    /// <summary>Право на выполнение действия (docs/users-plan.md §5).
    /// Проверяется вместе с правом элемента: оба.</summary>
    public string? RequiredRight { get; set; }

    /// <summary>Что показать при отказе: Notify (умолчание) или Silent.</summary>
    public DeniedFeedback DeniedFeedback { get; set; } = DeniedFeedback.Notify;
}

public sealed class WriteTagActionDto : SchemeActionDto
{
    /// <summary>Тег: абсолютное имя или параметрическая ссылка "{Prefix}.X".</summary>
    public string? Tag { get; set; }

    /// <summary>Значение-константа. Nullable, чтобы отличать «не задано»
    /// от нуля: заданы и value, и valueExpression — ошибка загрузки (C2).</summary>
    public double? Value { get; set; }

    /// <summary>Значение-выражение (C2): "Тег + 1", "Уставка * 0.9".
    /// Задано — вычисляется в момент выполнения, Value игнорируется.</summary>
    public string? ValueExpression { get; set; }
}

public sealed class ToggleTagActionDto : SchemeActionDto
{
    public string? Tag { get; set; }
}

public sealed class OpenSchemeActionDto : SchemeActionDto
{
    public string? SchemeName { get; set; }
    public Dictionary<string, string>? Parameters { get; set; }
}

public sealed class OpenPopupActionDto : SchemeActionDto
{
    public string? TemplateName { get; set; }
    public Dictionary<string, string>? Parameters { get; set; }
}

public sealed class ClosePopupActionDto : SchemeActionDto;

public sealed class BackActionDto : SchemeActionDto;

public sealed class ShowDialogActionDto : SchemeActionDto
{
    public string? Message { get; set; }
}

// { "type": "SetProperty", "element": "Панель", "property": 5, "value": "false" }
public sealed class SetPropertyActionDto : SchemeActionDto
{
    /// <summary>Имя элемента-цели в своей схеме (внутри шаблона — в шаблоне).</summary>
    public string? Element { get; set; }

    /// <summary>id свойства из ElementSchemas — как у привязок и свойств.</summary>
    public int Property { get; set; }

    /// <summary>Значение строкой, тип берётся из дескриптора свойства —
    /// та же форма, что у properties и стопов.</summary>
    public string? Value { get; set; }

    /// <summary>Значение-выражение (C5): задано — value должно отсутствовать.
    /// Допустимо только для числовых по существу свойств.</summary>
    public string? ValueExpression { get; set; }
}
