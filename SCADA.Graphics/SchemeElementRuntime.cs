using SkiaSharp;

namespace SCADA.Graphics;

internal sealed class SchemeElementRuntime(CompiledSchemeElement compiled)
{
    public CompiledSchemeElement Compiled {get;}=compiled;
    public SKColor Fill {get;set;}=SKColors.Transparent;
    public bool QualityBad {get;set;}
    public bool Visible {get;set;}=true;
    public bool BlinkActive{get;set;}
    public double RotationDegrees {get;set;}
    public double FillLevel{get;set;}
    public string DisplayText{get;set;}="";
    public double OffsetX{get;set;}
    public double OffsetY{get;set;}
}
