namespace SCADA.Graphics;

public sealed class SchemeElement
{
    public required Guid Id { get; init; }
    public int ZOrder{ get; init; }
    public ShapeKind Kind{get;init;}=ShapeKind.Rectangle;

    public required double X {get; init;}
    public required double Y{get;init;}
    public required double Width {get; init;}
    public required double Height{get;init;}

    public string? ValueExpression{get;init;}
    public double WarnThreshold{get;init;}=double.PositiveInfinity;
    public double CritThreshold{get;init;}=double.PositiveInfinity;

    public string? QualityTagName{get;init;}
}
