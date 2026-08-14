using SkiaSharp;

namespace SCADA.Graphics;

internal sealed class SchemeElementRuntime(CompiledSchemeElement compiled)
{
    public CompiledSchemeElement Compiled {get;}=compiled;
    public SKColor Fill {get;set;}=SKColors.Transparent;
    public bool QualityBad {get;set;}
}
