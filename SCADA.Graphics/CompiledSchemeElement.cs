using SCADA.Core.Schemes;
using SCADA.Core.Tags;
using SCADA.Expressions.Compiler;

namespace SCADA.Graphics;

public sealed record CompiledBinding(
    int PropertyId,
    PropertyType Type,
    CompiledExpression Expression,
    StopMapping Mapping,
    IReadOnlyList<Stop>? Stops);

public sealed record CompiledSchemeElement(
    SchemeElement Source,
    IReadOnlyList<CompiledBinding> Bindings,
    int[] AllTagIndices,
    bool HasFillBinding,
    IReadOnlyList<CompiledSchemeAction>? OnClick);
