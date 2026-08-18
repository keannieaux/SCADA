using SCADA.Core.Tags;
using SCADA.Expressions.Compiler;

namespace SCADA.Graphics;

public sealed record CompiledSchemeElement(
    SchemeElement Source,
    CompiledExpression? Value,
    CompiledExpression? Visible,
    CompiledExpression? BlinkWhen,
    CompiledExpression? Rotation,
    CompiledExpression? FillLevel,
    CompiledExpression? Text,
    CompiledExpression? PositionX,
    CompiledExpression? PositionY,
    TagId? QualityTag,
    IReadOnlyList<CompiledSchemeAction>? OnClick);

