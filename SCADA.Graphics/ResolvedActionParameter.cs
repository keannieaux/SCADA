using SCADA.Core.Schemes;
using SCADA.Core.Tags;
using SCADA.Expressions;

namespace SCADA.Graphics;

/// <summary>
/// Значение составного параметра действия (OpenScheme/OpenPopup) после
/// загрузки схемы (docs/scheme-controls-plan.md, C2): константа, ссылка на
/// строковый тег или шаблон со скомпилированными числовыми плейсхолдерами.
/// Сборка строки — <see cref="ActionParameterText"/> в момент выполнения
/// действия (редкий путь — клик), ВМ остаётся числовой.
/// </summary>
public sealed record ResolvedActionParameter(string Name, ActionParamValueKind Kind)
{
    /// <summary>Constant: текст как есть ("Pump5"). Для Template — исходный
    /// шаблон, хранится для диагностики (сборка идёт по Literals/Placeholders).</summary>
    public string? Text { get; init; }

    /// <summary>StringTagRef: строковый тег, читается в момент выполнения —
    /// покрывает «открой окно насоса, выбранного в списке».</summary>
    public TagId? StringTagId { get; init; }

    /// <summary>Template: литералы между плейсхолдерами; Literals.Count
    /// == Placeholders.Count + 1. Плейсхолдеры — скомпилированные числовые
    /// выражения в порядке появления в шаблоне ("Pump{N}").</summary>
    public IReadOnlyList<string>? Literals { get; init; }
    public IReadOnlyList<Expression>? Placeholders { get; init; }
}
