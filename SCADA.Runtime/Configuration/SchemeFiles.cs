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
public abstract class SchemeActionDto
{
    /// <summary>Условие «выполнять, если…» — выражение, концепт §5.2.</summary>
    public string? Condition { get; set; }

    /// <summary>Модификатор Confirm: текст вопроса перед выполнением.</summary>
    public string? Confirm { get; set; }
}

public sealed class WriteTagActionDto : SchemeActionDto
{
    /// <summary>Тег: абсолютное имя или параметрическая ссылка "{Prefix}.X".</summary>
    public string? Tag { get; set; }
    public double Value { get; set; }
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
