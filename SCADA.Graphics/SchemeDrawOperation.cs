using Avalonia;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;
using System.Diagnostics;

namespace SCADA.Graphics;

public readonly record struct SchemeElementVisual(Rect Bounds, SKColor Fill, bool QualityBad, ShapeKind Kind);

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

            var sw=Stopwatch.StartNew();
            foreach (var item in items)
            {
                var rect=new SKRect(
                    (float)item.Bounds.X, (float)item.Bounds.Y,
                    (float)(item.Bounds.X+item.Bounds.Width), (float)(item.Bounds.Y+item.Bounds.Height));

                using var paint=new SKPaint {Color=item.Fill, IsAntialias=true};

                if (item.Kind==ShapeKind.Ellipse)
                    canvas.DrawOval(rect,paint);
                else
                    canvas.DrawRect(rect,paint);

                if (item.QualityBad)
                {
                    var badgeColor=ThemeColors.Resolve("WarnColor", new SKColor(0xE8, 0xA3, 0x3D));
                    using var badge=new SKPaint{Color=badgeColor,IsAntialias=true};
                    canvas.DrawCircle(rect.Right-6,rect.Top+6,4,badge);
                }
            }
            sw.Stop();
            Debug.WriteLine($"Draw: {sw.Elapsed.TotalMilliseconds:F2} мс, {items.Count} элементов");
        }
    }

    public bool HitTest(Point p) => Bounds.Contains(p);
    public bool Equals(ICustomDrawOperation? other) => false;
    public void Dispose() { }
}
