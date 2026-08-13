namespace SCADA.Expressions;

public sealed class Expression
{
    public required byte[] Code{get; init;}
    public required double[] Constants{get; init;}

}
