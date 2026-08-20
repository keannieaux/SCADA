using SCADA.Core.Schemes;
using SCADA.Core.Tags;
using SCADA.Expressions;

namespace SCADA.Graphics;

public sealed record CompiledBinding(
    int PropertyId,
    PropertyType Type,
    Expression Expression,
    StopMapping Mapping,
    IReadOnlyList<Stop>? Stops,
    bool Volatile);

public sealed record CompiledSchemeElement(
    SchemeElement Source,
    IReadOnlyList<CompiledBinding> Bindings,
    int[] AllTagIndices,
    bool HasFillBinding,
    bool HasVolatileBindings,
    IReadOnlyList<CompiledSchemeAction>? OnClick);
