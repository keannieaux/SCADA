using SCADA.Core.Tags;
using SCADA.Expressions.Compiler;

namespace SCADA.Graphics;

public sealed record CompiledSchemeElement(SchemeElement Source, CompiledExpression? Value, TagId? QualityTag);
