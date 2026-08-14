using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using SkiaSharp;

namespace SCADA.Graphics;

public static class ThemeColors
{
    public static SKColor Resolve(string resourceKey, SKColor fallback)
    {
        if(Application.Current is { } app
            && app.TryGetResource(resourceKey, ThemeVariant.Default, out var value)
            && value is Color color)
        {
            return new SKColor(color.R, color.G, color.B, color.A);
        }
        return fallback;
    }
}
