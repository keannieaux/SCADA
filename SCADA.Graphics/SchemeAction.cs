namespace SCADA.Graphics;

public abstract record SchemeAction;
public sealed record WriteTagAction(string TagName, double Value) : SchemeAction;
public sealed record ToggleTagAction(string TagName) : SchemeAction;
public sealed record OpenSchemeAction(string SchemeName) : SchemeAction;
public sealed record ShowDialogAction(string Message) : SchemeAction;
public sealed record ConfirmAction(string Message) : SchemeAction;
