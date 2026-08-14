using Avalonia;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;

namespace SCADA.Graphics;

public readonly record struct SchemeElementVisual(Rect Bounds, SKColor Fill, bool QualityBad);

internal sealed class SchemeDrawOperation(Rect bounds, IReadOnlyList<SchemeElementVisual> items) : ICustomDrawOperation
{
    public Rect Bounds { get; } = bounds;

    public void Render(ImmediateDrawingContext context)
    {
        var lease=context.TryGetFeature<ISkiaSharpApiLeaseFeature>()?.Lease();
        if (lease is null)
            return;

        using (lease)
        {
            var canvas=lease.SkCanvas;

            foreach (var item in items)
            {
                var rect=new SKRect(
                    (float)item.Bounds.X, (float)item.Bounds.Y,
                    (float)(item.Bounds.X+item.Bounds.Width), (float)(item.Bounds.Y+item.Bounds.Height));

                using var paint=new SKPaint {Color=item.Fill, IsAntialias=true};
                canvas.DrawRect(rect,paint);

                if (item.QualityBad)
                {
                    var badgeColor=ThemeColors.Resolve("WarnColor", new SKColor(0xE8, 0xA3, 0x3D));
                    using var badge=new SKPaint{Color=badgeColor,IsAntialias=true};
                    canvas.DrawCircle(rect.Right-6,rect.Top+6,4,badge);
                }
            }
        }
    }

    public bool HitTest(Point p) => Bounds.Contains(p);
    public bool Equals(ICustomDrawOperation? other) => false;
    public void Dispose() { }
}
