using SCADA.Core.Schemes;
using SCADA.Core.Tags;
using SCADA.Expressions;
using SkiaSharp;

namespace SCADA.Graphics;

public sealed record CompiledBinding(
    int PropertyId,
    PropertyType Type,
    Expression? Expression,
    StopMapping Mapping,
    IReadOnlyList<Stop>? Stops,
    bool Volatile,
    TagId? StringTag=null);

public sealed record CompiledSchemeElement(
    SchemeElement Source,
    IReadOnlyList<CompiledBinding> Bindings,
    int[] AllTagIndices,
    bool HasFillBinding,
    bool HasVolatileBindings,
    SKPicture? Symbol,
    IReadOnlyList<CompiledSchemeAction>? OnClick);
