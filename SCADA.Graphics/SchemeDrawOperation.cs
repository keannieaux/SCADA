using Avalonia;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;
using System.Diagnostics;
using SCADA.Core.Schemes;
using System.Collections.Concurrent;

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
    SKPicture? Symbol);

public sealed class SchemeDrawOperation(Rect bounds, List<SchemeElementVisual> items, double panX, double panY, double zoom, ConcurrentStack<List<SchemeElementVisual>> pool, SKPicture? staticPicture) : ICustomDrawOperation

{
    public Rect Bounds { get; } = bounds;
    private static readonly SKTypeface s_typeface=SKTypeface.FromFamilyName("Cascadia Code");
    private static readonly SKFont s_font=new(s_typeface,14);
    private static readonly SKPaint s_outlinePaint=new(){Color=ThemeColors.Resolve("BorderColor", new SKColor(0x3A,0x3F,0x44)), IsAntialias=true, Style=SKPaintStyle.Stroke, StrokeWidth=2};
    private static readonly SKPaint s_badgePaint=new(){Color=ThemeColors.Resolve("WarnColor", new SKColor(0xE8,0xA3,0x3D)), IsAntialias=true};
    private static readonly SKPaint s_textPaint=new(){Color=ThemeColors.Resolve("TextColor", new SKColor(0xE7,0xE9,0xEA)), IsAntialias=true};
    private static readonly Dictionary<SKColor, SKPaint> s_fillPaints=new();

    private static SKPaint GetFillPaint(SKColor color)
    {
        if(!s_fillPaints.TryGetValue(color,out var paint))
        {
            paint=new SKPaint{Color=color,IsAntialias=true};
            s_fillPaints[color]=paint;
        }
        return paint;
    }
    public void Render(ImmediateDrawingContext context)
    {
        var lease=context.TryGetFeature<ISkiaSharpApiLeaseFeature>()?.Lease();
        if (lease is null)
            return;

        using (lease)
        {
            var sw=Stopwatch.StartNew();
            DrawItems(lease.SkCanvas, items, panX, panY, zoom, staticPicture);
            Debug.WriteLine($"Draw: {sw.Elapsed.TotalMilliseconds:F2} мс, отрисовано {items.Count} элементов");
        }
    }

    // public static: само рисование доступно headless-замерам (бенчмарки на
    // SKSurface без Avalonia); Render — тонкая обёртка над ним
    public static void DrawItems(SKCanvas canvas, IReadOnlyList<SchemeElementVisual> items,
        double panX, double panY, double zoom, SKPicture? staticPicture=null)
    {
        canvas.Save();
        canvas.Translate((float)panX, (float)panY);
        canvas.Scale((float)zoom);
        if(staticPicture is not null)
            canvas.DrawPicture(staticPicture);
        foreach (var item in items)
        {
            var rect=new SKRect(
                (float)item.Bounds.X, (float)item.Bounds.Y,
                (float)(item.Bounds.X+item.Bounds.Width), (float)(item.Bounds.Y+item.Bounds.Height));

            canvas.Save();
            canvas.RotateDegrees((float)item.RotationDegrees, rect.MidX, rect.MidY);

            if (item.HasFillLevel)
            {
                canvas.DrawRect(rect, s_outlinePaint);

                float fillHeight=rect.Height*(float)item.FillLevel;
                var fillRect=new SKRect(rect.Left, rect.Bottom-fillHeight,rect.Right,rect.Bottom);
                canvas.DrawRect(fillRect,GetFillPaint(item.Fill));
            }
            else if(item.Kind==ElementKind.Symbol && item.Symbol is {} picture)
            {
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
                var paint=GetFillPaint(item.Fill);
                if(item.Kind==ElementKind.Ellipse)
                    canvas.DrawOval(rect,paint);
                else
                    canvas.DrawRect(rect,paint);
            }

            if (item.QualityBad)
            {
                canvas.DrawCircle(rect.Right-6,rect.Top+6,4,s_badgePaint);
            }

            if(!string.IsNullOrEmpty(item.Text))
                canvas.DrawText(item.Text, rect.MidX, rect.MidY+s_font.Size/3, SKTextAlign.Center,s_font,s_textPaint);

            canvas.Restore();
        }
        canvas.Restore();
    }

    public bool HitTest(Point p) => Bounds.Contains(p);
    public bool Equals(ICustomDrawOperation? other) => false;
    public void Dispose()=>pool.Push(items);
}
