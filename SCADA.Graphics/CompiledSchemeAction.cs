using SCADA.Core.Tags;

namespace SCADA.Graphics;

public abstract record CompiledSchemeAction;
public sealed record CompiledWriteTagAction(TagId TagId, double Value) : CompiledSchemeAction;
public sealed record CompiledToggleTagAction(TagId TagId) : CompiledSchemeAction;
public sealed record CompiledOpenSchemeAction(string SchemeName) : CompiledSchemeAction;
public sealed record CompiledShowDialogAction(string Message) : CompiledSchemeAction;
public sealed record CompiledConfirmAction(string Message) : CompiledSchemeAction;

