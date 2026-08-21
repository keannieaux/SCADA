using System.Globalization;
using System.Text.Json;
using SCADA.Core.Schemes;

namespace SCADA.Runtime.Configuration;

/// <summary>
/// Загрузка исходников схем и шаблонов: schemes/*.scheme и templates/*.scheme
/// (JSON, UTF-8 — концепт §3, §7). Каталоги опциональны: нет каталога — нет
/// схем. Исходник строгий: битый JSON, неизвестный id свойства, значение не
/// по типу, привязка к неанимируемому свойству — ошибка в общий список
/// ошибок ProjectLoader (ProjectConfigurationException).
/// </summary>
public static class SchemeFileLoader
{
    public const string SchemesDirectory = "schemes";
    public const string TemplatesDirectory = "templates";
    public const string Extension = ".scheme";

    public static (List<Scheme> Schemes, List<SchemeTemplate> Templates) Load(
        string projectDirectory, List<string> errors)
    {
        var schemes = new List<Scheme>();
        var templates = new List<SchemeTemplate>();
        LoadDirectory(projectDirectory, SchemesDirectory, isTemplate: false, schemes, templates, errors);
        LoadDirectory(projectDirectory, TemplatesDirectory, isTemplate: true, schemes, templates, errors);
        return (schemes, templates);
    }

    private static void LoadDirectory(string projectDirectory, string directoryName,
        bool isTemplate, List<Scheme> schemes, List<SchemeTemplate> templates,
        List<string> errors)
    {
        string directory = Path.Combine(projectDirectory, directoryName);
        if (!Directory.Exists(directory))
            return;

        // порядок детерминирован — пакет воспроизводим
        foreach (string path in Directory.EnumerateFiles(directory, "*" + Extension).Order())
        {
            string source = $"{directoryName}/{Path.GetFileName(path)}";

            SchemeFile? file;
            try
            {
                file = JsonSerializer.Deserialize(File.ReadAllText(path),
                    ProjectJsonContext.Default.SchemeFile);
            }
            catch (JsonException ex)
            {
                errors.Add($"{source}: ошибка JSON: {ex.Message}");
                continue;
            }

            if (file is null)
            {
                errors.Add($"{source}: пустой файл");
                continue;
            }

            var elements = new List<SchemeElement>(file.Elements.Count);
            for (int i = 0; i < file.Elements.Count; i++)
            {
                var element = MapElement(file.Elements[i], i, source, errors);
                if (element is not null)
                    elements.Add(element);
            }

            var properties = MapSchemeProperties(file, source, errors);
            var events = MapEvents(file.Events, source, "схема", schemeLevel: true, errors);

            if (isTemplate)
            {
                if (file.RequiredRight is not null)
                    errors.Add($"{source}: requiredRight допустим только у схем " +
                        "(попап открывается действием — право ставится на действие)");
                templates.Add(new SchemeTemplate
                {
                    Id = file.Id ?? Guid.NewGuid(),
                    Name = file.Name ?? Path.GetFileNameWithoutExtension(path),
                    Properties = properties,
                    Events = events,
                    Parameters = MapParameters(file, source, errors),
                    Elements = elements
                });
            }
            else
            {
                if (file.Parameters.Count > 0)
                    errors.Add($"{source}: parameters допустимы только в шаблонах (templates/)");
                schemes.Add(new Scheme
                {
                    Id = file.Id ?? Guid.NewGuid(),
                    Name = file.Name ?? Path.GetFileNameWithoutExtension(path),
                    RequiredRight = file.RequiredRight,
                    Properties = properties,
                    Events = events,
                    Elements = elements
                });
            }
        }
    }

    private static List<ElementProperty> MapSchemeProperties(SchemeFile file,
        string source, List<string> errors)
    {
        var properties = new List<ElementProperty>(file.Properties.Count);
        foreach (var propertyDto in file.Properties)
        {
            var def = ElementSchemas.FindSchemeProperty(propertyDto.Id);
            if (def is null)
            {
                errors.Add($"{source}: у схемы нет свойства с id {propertyDto.Id}");
                continue;
            }
            var value = ParseValue(def, propertyDto.Value, source, "свойства схемы", errors);
            if (value is not null)
                properties.Add(new ElementProperty(propertyDto.Id, value.Value));
        }
        return properties;
    }

    private static List<TemplateParameter> MapParameters(SchemeFile file, string source,
        List<string> errors)
    {
        var parameters = new List<TemplateParameter>(file.Parameters.Count);
        var names = new HashSet<string>();
        foreach (var dto in file.Parameters)
        {
            if (string.IsNullOrWhiteSpace(dto.Name))
            {
                errors.Add($"{source}: параметр шаблона без имени");
                continue;
            }
            if (!names.Add(dto.Name))
            {
                errors.Add($"{source}: дубликат параметра шаблона '{dto.Name}'");
                continue;
            }
            parameters.Add(new TemplateParameter(dto.Name, dto.Type, dto.Default));
        }
        return parameters;
    }

    private static SchemeElement? MapElement(SchemeElementDto dto, int index, string source,
        List<string> errors)
    {
        string label = string.IsNullOrEmpty(dto.Name) ? $"#{index}" : $"'{dto.Name}'";

        if (dto.Kind is not { } kind)
        {
            errors.Add($"{source}: элемент {label}: не задан kind");
            return null;
        }

        var properties = new List<ElementProperty>(dto.Properties.Count);
        foreach (var propertyDto in dto.Properties)
        {
            var def = ElementSchemas.Find(kind, propertyDto.Id);
            if (def is null)
            {
                errors.Add($"{source}: элемент {label}: у вида {kind} нет свойства с id {propertyDto.Id}");
                continue;
            }
            var value = ParseValue(def, propertyDto.Value, source, $"элемент {label}", errors);
            if (value is not null)
                properties.Add(new ElementProperty(propertyDto.Id, value.Value));
        }

        var bindings = new List<ElementBinding>(dto.Bindings.Count);
        foreach (var bindingDto in dto.Bindings)
        {
            var binding = MapBinding(bindingDto, kind, source, label, errors);
            if (binding is not null)
                bindings.Add(binding);
        }

        var events = MapEvents(dto.Events, source, $"элемент {label}",
            schemeLevel: false, errors);

        // обязательные поля служебных видов (§7, §8)
        if (kind == ElementKind.Instance && string.IsNullOrWhiteSpace(dto.TemplateName))
            errors.Add($"{source}: элемент {label}: для Instance не задан templateName");
        if (kind == ElementKind.Control && string.IsNullOrWhiteSpace(dto.ControlType))
            errors.Add($"{source}: элемент {label}: для Control не задан controlType");

        return new SchemeElement
        {
            Id = dto.Id ?? Guid.NewGuid(),
            Name = dto.Name ?? "",
            Kind = kind,
            X = dto.X,
            Y = dto.Y,
            Width = dto.Width,
            Height = dto.Height,
            ZOrder = dto.ZOrder,
            ParentId = dto.ParentId,
            ControlType = dto.ControlType,
            TemplateName = dto.TemplateName,
            TemplateParameters = dto.TemplateParameters,
            Properties = properties,
            Bindings = bindings,
            Events = events,
            RequiredRight = dto.RequiredRight,
            DeniedState = dto.DeniedState
        };
    }

    private static ElementBinding? MapBinding(ElementBindingDto dto, ElementKind kind,
        string source, string label, List<string> errors)
    {
        // привязка к несуществующему/неанимируемому свойству — ошибка (§3.2)
        string? bindingError = ElementSchemas.ValidateBinding(kind, dto.Property);
        if (bindingError is not null)
        {
            errors.Add($"{source}: элемент {label}: привязка: {bindingError}");
            return null;
        }
        if (string.IsNullOrWhiteSpace(dto.Expression))
        {
            errors.Add($"{source}: элемент {label}: привязка свойства {dto.Property} без выражения");
            return null;
        }

        var def = ElementSchemas.Find(kind, dto.Property)!;
        List<Stop>? stops = null;
        if (dto.Stops is not null)
        {
            stops = new List<Stop>(dto.Stops.Count);
            foreach (var stopDto in dto.Stops)
            {
                // выход стопа — значение целевого свойства, тип из дескриптора
                var output = ParseValue(def, stopDto.Output, source,
                    $"элемент {label}, стоп {stopDto.Input}", errors);
                if (output is null)
                    return null;
                stops.Add(new Stop(stopDto.Input, output.Value));
            }
        }

        return new ElementBinding
        {
            PropertyId = dto.Property,
            Expression = dto.Expression,
            Mapping = dto.Mapping,
            Volatile = dto.Volatile,
            Stops = stops
        };
    }

    /// <summary>Общий маппинг событий — для элементов и для уровня схемы
    /// (§5.1). Указательные события на схеме и Opened/Closed на элементе —
    /// ошибка исходника: строгость на своём уровне.</summary>
    private static List<SchemeEvent> MapEvents(List<SchemeEventDto> dtos, string source,
        string context, bool schemeLevel, List<string> errors)
    {
        var events = new List<SchemeEvent>(dtos.Count);
        foreach (var eventDto in dtos)
        {
            if (eventDto.Kind is not { } eventKind)
            {
                errors.Add($"{source}: {context}: событие без kind");
                continue;
            }
            bool isLifecycle = eventKind is SchemeEventKind.Opened or SchemeEventKind.Closed;
            if (isLifecycle != schemeLevel)
            {
                errors.Add($"{source}: {context}: событие {eventKind} " +
                           (schemeLevel ? "не применимо на уровне схемы"
                                        : "применимо только на уровне схемы"));
                continue;
            }
            var actions = new List<SchemeAction>(eventDto.Actions.Count);
            foreach (var actionDto in eventDto.Actions)
            {
                var action = MapAction(actionDto, source, context, errors);
                if (action is not null)
                    actions.Add(action);
            }
            events.Add(new SchemeEvent { Kind = eventKind, Actions = actions });
        }
        return events;
    }

    private static SchemeAction? MapAction(SchemeActionDto dto, string source, string context,
        List<string> errors)
    {
        // "WriteTagActionDto" → "WriteTag"
        string typeName = dto.GetType().Name;
        string actionContext = $"{context}, действие {typeName[..^9]}";

        SchemeAction? action = dto switch
        {
            WriteTagActionDto a => ParseTagRef(a.Tag, source, actionContext, errors) is { } tag
                ? MapWriteTag(a, tag, source, actionContext, errors)
                : null,
            ToggleTagActionDto a => ParseTagRef(a.Tag, source, actionContext, errors) is { } tag
                ? new ToggleTagAction(tag) : null,
            OpenSchemeActionDto a => !string.IsNullOrWhiteSpace(a.SchemeName)
                ? new OpenSchemeAction(a.SchemeName, a.Parameters)
                : Missing(errors, source, actionContext, "schemeName"),
            OpenPopupActionDto a => !string.IsNullOrWhiteSpace(a.TemplateName)
                ? new OpenPopupAction(a.TemplateName, a.Parameters)
                : Missing(errors, source, actionContext, "templateName"),
            ClosePopupActionDto => new ClosePopupAction(),
            BackActionDto => new BackAction(),
            ShowDialogActionDto a => !string.IsNullOrWhiteSpace(a.Message)
                ? new ShowDialogAction(a.Message)
                : Missing(errors, source, actionContext, "message"),
            SetPropertyActionDto a => MapSetProperty(a, source, actionContext, errors),
            _ => null // иерархия закрыта атрибутами, недостижимо
        };

        if (action is not null)
            action = action with
            {
                Condition = dto.Condition,
                Confirmation = dto.Confirm,
                RequiredRight = dto.RequiredRight,
                DeniedFeedback = dto.DeniedFeedback
            };
        return action;
    }

    private static SchemeAction? Missing(List<string> errors, string source, string context,
        string field)
    {
        errors.Add($"{source}: {context}: не задано поле {field}");
        return null;
    }

    /// <summary>C2: value и valueExpression взаимоисключающие; одно из двух
    /// обязательно (иначе действие записывало бы молчаливый ноль).</summary>
    private static SchemeAction? MapWriteTag(WriteTagActionDto dto, SchemeTagRef tag,
        string source, string context, List<string> errors)
    {
        bool hasValue = dto.Value.HasValue;
        bool hasExpression = !string.IsNullOrWhiteSpace(dto.ValueExpression);

        if (hasValue && hasExpression)
        {
            errors.Add($"{source}: {context}: заданы и value, и valueExpression — оставьте одно");
            return null;
        }
        if (!hasValue && !hasExpression)
            return Missing(errors, source, context, "value или valueExpression");

        return new WriteTagAction(tag, dto.Value ?? 0) { ValueExpression = dto.ValueExpression };
    }

    /// <summary>C5: «задать свойство элемента». Здесь разбирается только форма
    /// записи — значение по типу свойства и взаимоисключающие value/
    /// valueExpression. Всё, что требует знать проект целиком (элемент с таким
    /// именем есть, свойство есть у его вида и анимируемо, им не управляет
    /// привязка), проверяет сборка: загрузчик видит один файл.</summary>
    private static SchemeAction? MapSetProperty(SetPropertyActionDto dto, string source,
        string context, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(dto.Element))
            return Missing(errors, source, context, "element");

        // тип значения известен по одному id: id свойств глобально уникальны
        var def = ElementSchemas.FindAny(dto.Property);
        if (def is null)
        {
            errors.Add($"{source}: {context}: нет свойства с id {dto.Property}");
            return null;
        }

        bool hasValue = dto.Value is not null;
        bool hasExpression = !string.IsNullOrWhiteSpace(dto.ValueExpression);
        if (hasValue && hasExpression)
        {
            errors.Add($"{source}: {context}: заданы и value, и valueExpression — оставьте одно");
            return null;
        }
        if (!hasValue && !hasExpression)
            return Missing(errors, source, context, "value или valueExpression");

        PropertyValue? value = null;
        if (hasValue)
        {
            value = ParseValue(def, dto.Value, source, context, errors);
            if (value is null)
                return null;
        }

        return new SetPropertyAction(dto.Element, dto.Property, value)
        {
            ValueExpression = dto.ValueExpression
        };
    }

    /// <summary>"{Prefix}.X" → параметрическая ссылка (концепт §4.4, §7);
    /// иначе — абсолютное имя тега.</summary>
    private static SchemeTagRef? ParseTagRef(string? tag, string source, string context,
        List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            errors.Add($"{source}: {context}: не задан tag");
            return null;
        }
        if (!tag.StartsWith('{'))
            return SchemeTagRef.Absolute(tag);

        int close = tag.IndexOf('}');
        string suffix = close < 0 ? "" : tag[(close + 1)..];
        if (close <= 1 || (suffix.Length > 0 && !suffix.StartsWith('.')))
        {
            errors.Add($"{source}: {context}: некорректная параметрическая ссылка '{tag}' " +
                       "(ожидается \"{Параметр}.Суффикс\")");
            return null;
        }
        return SchemeTagRef.Parametric(tag[1..close], suffix);
    }

    /// <summary>Строковое значение из JSON → типизированное по дескриптору
    /// свойства: Number/Choice — число, Boolean — true/false,
    /// Color — "#AARRGGBB", String — как есть.</summary>
    private static PropertyValue? ParseValue(PropertyDef def, string? text, string source,
        string context, List<string> errors)
    {
        if (text is not null)
        {
            switch (def.Type)
            {
                case PropertyType.Number:
                    if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture,
                            out double number))
                        return PropertyValue.FromNumber(number);
                    break;
                case PropertyType.Boolean:
                    if (bool.TryParse(text, out bool boolean))
                        return PropertyValue.FromBool(boolean);
                    break;
                case PropertyType.Choice:
                    if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture,
                            out int choice))
                        return PropertyValue.FromChoice(choice);
                    break;
                case PropertyType.Color:
                    if (TryParseColor(text, out uint argb))
                        return PropertyValue.FromColor(argb);
                    break;
                case PropertyType.String:
                    return PropertyValue.FromString(text);
            }
        }

        errors.Add($"{source}: {context}: значение '{text}' не соответствует типу " +
                   $"{def.Type} свойства '{def.Name}'");
        return null;
    }

    private static bool TryParseColor(string text, out uint argb)
    {
        argb = 0;
        return text.Length == 9 && text[0] == '#' &&
               uint.TryParse(text.AsSpan(1), NumberStyles.HexNumber,
                   CultureInfo.InvariantCulture, out argb);
    }
}
