using SCADA.Core.Tags;
using SCADA.Expressions.Compiler;

namespace SCADA.Graphics;

public abstract record CompiledSchemeAction(string? Confirmation, CompiledExpression? Condition);
public sealed record CompiledWriteTagAction(TagId TagId, double Value, string? Confirmation, CompiledExpression? Condition)
    : CompiledSchemeAction(Confirmation, Condition);
public sealed record CompiledToggleTagAction(TagId TagId, string? Confirmation, CompiledExpression? Condition)
    :CompiledSchemeAction(Confirmation, Condition);
public sealed record CompiledOpenSchemeAction(string SchemeName, string? Confirmation, CompiledExpression? Condition)
    :CompiledSchemeAction(Confirmation, Condition);
public sealed record CompiledShowDialogAction(string Message, string? Confirmation, CompiledExpression? Condition)
    :CompiledSchemeAction(Confirmation, Condition);
