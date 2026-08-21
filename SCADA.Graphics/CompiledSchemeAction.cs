using SCADA.Core.Tags;
using SCADA.Expressions;

namespace SCADA.Graphics;

public abstract record CompiledSchemeAction(string? Confirmation, Expression? Condition);

/// <summary>Запись в тег. ValueExpression (C2) задано — значение вычисляется
/// в момент выполнения, позиционный Value игнорируется ("Тег + 1" и т.п.).</summary>
public sealed record CompiledWriteTagAction(TagId TagId, double Value,
    Expression? ValueExpression, string? Confirmation, Expression? Condition)
    : CompiledSchemeAction(Confirmation, Condition);

public sealed record CompiledToggleTagAction(TagId TagId, string? Confirmation, Expression? Condition)
    : CompiledSchemeAction(Confirmation, Condition);

public sealed record CompiledOpenSchemeAction(string SchemeName,
    IReadOnlyList<ResolvedActionParameter>? Parameters, string? Confirmation, Expression? Condition)
    : CompiledSchemeAction(Confirmation, Condition);

public sealed record CompiledOpenPopupAction(string TemplateName,
    IReadOnlyList<ResolvedActionParameter>? Parameters, string? Confirmation, Expression? Condition)
    : CompiledSchemeAction(Confirmation, Condition);

public sealed record CompiledShowDialogAction(string Message, string? Confirmation, Expression? Condition)
    : CompiledSchemeAction(Confirmation, Condition);

/// <summary>Назад по стеку истории переходов (ActionCatalog, код 5).</summary>
public sealed record CompiledBackAction(string? Confirmation, Expression? Condition)
    : CompiledSchemeAction(Confirmation, Condition);
