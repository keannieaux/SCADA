using SCADA.Core.Tags;
using SCADA.Expressions;

namespace SCADA.Graphics;

public abstract record CompiledSchemeAction(string? Confirmation, Expression? Condition);
public sealed record CompiledWriteTagAction(TagId TagId, double Value, string? Confirmation, Expression? Condition)
    : CompiledSchemeAction(Confirmation, Condition);
public sealed record CompiledToggleTagAction(TagId TagId, string? Confirmation, Expression? Condition)
    :CompiledSchemeAction(Confirmation, Condition);
public sealed record CompiledOpenSchemeAction(string SchemeName, string? Confirmation, Expression? Condition)
    :CompiledSchemeAction(Confirmation, Condition);
public sealed record CompiledShowDialogAction(string Message, string? Confirmation, Expression? Condition)
    :CompiledSchemeAction(Confirmation, Condition);
