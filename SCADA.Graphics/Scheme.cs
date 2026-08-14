namespace SCADA.Graphics;

public sealed class Scheme
{
    public required Guid Id{get;init;}
    public required string Name{get;init;}
    public required IReadOnlyList<SchemeElement> Elements {get;init;}
}
