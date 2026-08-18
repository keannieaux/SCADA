using Avalonia;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;
using System.Diagnostics;
using SCADA.Core.Schemes;

namespace SCADA.Graphics;

public readonly record struct SchemeElementVisual(
    Rect Bounds,
    SKColor Fill,
    bool QualityBad,
    ElementKind Kind,
    double RotationDegrees,
    bool HasFillLevel,
    double FillLevel,
    string Text,
    string? SymbolPath);

internal sealed class SchemeDrawOperation(Rect bounds, IReadOnlyList<SchemeElementVisual> items, double panX, double panY, double zoom) : ICustomDrawOperation

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
            canvas.Save();
            canvas.Translate((float)panX, (float)panY);
            canvas.Scale((float)zoom);
            var sw=Stopwatch.StartNew();
            foreach (var item in items)
            {
                var rect=new SKRect(
                    (float)item.Bounds.X, (float)item.Bounds.Y,
                    (float)(item.Bounds.X+item.Bounds.Width), (float)(item.Bounds.Y+item.Bounds.Height));

                canvas.Save();
                canvas.RotateDegrees((float)item.RotationDegrees, rect.MidX, rect.MidY);

                if (item.HasFillLevel)
                {
                    var outlineColor=ThemeColors.Resolve("BorderColor", new SKColor(0x3A,0x3F,0x44));
                    using var outline=new SKPaint{Color=outlineColor, IsAntialias=true, Style=SKPaintStyle.Stroke, StrokeWidth=2};
                    canvas.DrawRect(rect,outline);

                    float fillHeight=rect.Height*(float)item.FillLevel;
                    var fillRect=new SKRect(rect.Left, rect.Bottom-fillHeight,rect.Right,rect.Bottom);
                    using var fillPaint=new SKPaint{Color=item.Fill,IsAntialias=true};
                    canvas.DrawRect(fillRect,fillPaint);
                }
                else if(item.Kind==ElementKind.Symbol && item.SymbolPath is {} path)
                {
                    var picture=SymbolCache.Load(path);
                    var sourceRect=picture.CullRect;
                    float scaleX=sourceRect.Width>0 ? rect.Width/sourceRect.Width : 1;
                    float scaleY=sourceRect.Height>0 ? rect.Height/sourceRect.Height : 1;

                    canvas.Save();
                    canvas.Translate(rect.Left, rect.Top);
                    canvas.Scale(scaleX,scaleY);
                    canvas.DrawPicture(picture);
                    canvas.Restore();
                }
                else
                {
                    using var paint=new SKPaint{Color=item.Fill, IsAntialias=true};

                    if(item.Kind==ElementKind.Ellipse)
                        canvas.DrawOval(rect,paint);
                    else
                        canvas.DrawRect(rect,paint);
                }

                if (item.QualityBad)
                {
                    var badgeColor=ThemeColors.Resolve("WarnColor",new SKColor(0xE8,0xA3,0x3D));
                    using var badge=new SKPaint{Color=badgeColor,IsAntialias=true};
                    canvas.DrawCircle(rect.Right-6,rect.Top+6,4,badge);
                }

                if (!string.IsNullOrEmpty(item.Text))
                {
                    using var font=new SKFont(SKTypeface.FromFamilyName("Cascadia Code"),14);
                    using var textPaint=new SKPaint
                    {
                        Color=ThemeColors.Resolve("TextColor", new SKColor(0xE7,0xE9,0xEA)),
                        IsAntialias=true
                    };
                    canvas.DrawText(item.Text, rect.MidX, rect.MidY + font.Size / 3, SKTextAlign.Center, font, textPaint);
                }

                canvas.Restore();
            }
            canvas.Restore();
        }
    }

    public bool HitTest(Point p) => Bounds.Contains(p);
    public bool Equals(ICustomDrawOperation? other) => false;
    public void Dispose() { }
}
