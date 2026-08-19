namespace SCADA.Core.Schemes;

/// <summary>
/// Схема (экран) — концепт §6. В пакете schemes/<имя>.bin, несколько штук;
/// рантайм перечисляет через манифест по префиксу.
/// </summary>
public sealed class Scheme
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }

    /// <summary>Свойства уровня схемы (фон, размер, стартовый зум) —
    /// разреженные, схема дескрипторов ElementSchemas.SchemeProperties.</summary>
    public IReadOnlyList<ElementProperty> Properties { get; init; } = [];

    /// <summary>События уровня экрана (Opened/Closed, §5.1) — тот же формат
    /// цепочек действий, что у элементов.</summary>
    public IReadOnlyList<SchemeEvent> Events { get; init; } = [];

    public required IReadOnlyList<SchemeElement> Elements { get; init; }
}

/// <summary>Тип параметра шаблона. v1 — строка (префикс тегов); число и
/// перечисление резервируются, формат параметра это допускает.</summary>
public enum TemplateParameterType : byte
{
    String = 0,
    Number = 1,
    Choice = 2,
}

public sealed record TemplateParameter(
    string Name, TemplateParameterType Type, string? DefaultValue);

/// <summary>
/// Шаблон — параметризованный фрагмент схемы (концепт §7): панель агрегата
/// для попапа, символ-сборка для экземпляров на схеме. В пакете
/// templates/<имя>.bin — тот же формат секции, что у схем, плюс параметры.
/// Привязки внутри используют параметрические ссылки (SchemeTagRef).
/// </summary>
public sealed class SchemeTemplate
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public IReadOnlyList<TemplateParameter> Parameters { get; init; } = [];

    /// <summary>Свойства уровня шаблона — та же схема, что у Scheme.</summary>
    public IReadOnlyList<ElementProperty> Properties { get; init; } = [];

    /// <summary>События уровня шаблона (актуально для попапов, §5.1).</summary>
    public IReadOnlyList<SchemeEvent> Events { get; init; } = [];

    public required IReadOnlyList<SchemeElement> Elements { get; init; }
}
