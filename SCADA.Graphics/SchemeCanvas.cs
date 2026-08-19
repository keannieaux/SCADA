using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using SCADA.Core.Schemes;
using SCADA.Core.Tags;
using SCADA.Expressions;
using SCADA.Runtime.Runtime;
using SkiaSharp;
using System.Diagnostics;
using Avalonia.Input;
using System.Collections.Concurrent;

namespace SCADA.Graphics;

public sealed class SchemeCanvas : Control
{
    private const string RequestedBy = "os-user@station"; // заглушка до M7-авторизации в UI

    private readonly SchemeElementRuntime[] _runtime;
    private readonly IRuntimeClient _runtimeClient;
    private readonly TagId[] _changedBuffer;
    private readonly bool[] _changedSet;
    private bool _blinkPhase;
    private double _panX;
    private double _panY;
    private double _zoom=1;
    private bool _isDragging;
    private Point _dragStart;
    private double _dragStartPanX;
    private double _dragStartPanY;
    private Point _pointerDownPosition;
    private bool _pointerMoved;

    private long _lastEpoch=-1;
    private readonly ConcurrentStack<List<SchemeElementVisual>> _visualsPool=new();
    private readonly bool _anyVolatile;

    public SchemeCanvas(IReadOnlyList<CompiledSchemeElement> elements, IRuntimeClient runtimeClient, int tagCount)
    {
        _runtime=elements.Select(e=>new SchemeElementRuntime(e)).ToArray();
        _anyVolatile=elements.Any(e=>e.HasVolatileBindings);
        _runtimeClient=runtimeClient;
        _changedBuffer=new TagId[tagCount];
        _changedSet=new bool[tagCount];

        RecomputeAll();
    }

    public void StartLive()
    {
        var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(200)};
        timer.Tick+=(_,_)=>Tick();
        timer.Start();

        var blinkTimer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(500)};
        blinkTimer.Tick+=(_,_)=>BlinkTick();
        blinkTimer.Start();
    }

    private void BlinkTick()
    {
        _blinkPhase=!_blinkPhase;

        bool anyBlinking=false;
        foreach(var element in _runtime)
            if (element.BlinkActive)
            {
                anyBlinking=true;
                break;
            }

        if(anyBlinking)
            InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        var pointer=e.GetPosition(this);
        double oldZoom=_zoom;
        double newZoom=Math.Clamp(_zoom*(e.Delta.Y>0 ? 1.1 : 1/1.1), 0.1, 10);

        _panX=pointer.X-(pointer.X-_panX)*(newZoom/oldZoom);
        _panY=pointer.Y-(pointer.Y-_panY)*(newZoom/oldZoom);
        _zoom=newZoom;

        InvalidateVisual();
        e.Handled=true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _isDragging=true;
            _dragStart=e.GetPosition(this);
            _dragStartPanX=_panX;
            _dragStartPanY=_panY;
            _pointerDownPosition=_dragStart;
            _pointerMoved=false;

            e.Pointer.Capture(this);
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (!_isDragging)
            return;

        var current=e.GetPosition(this);

        double moveDx=current.X-_pointerDownPosition.X;
        double moveDy=current.Y-_pointerDownPosition.Y;
        if (moveDx*moveDx+moveDy*moveDy>16)
            _pointerMoved=true;

        _panX=_dragStartPanX+(current.X-_dragStart.X);
        _panY=_dragStartPanY+(current.Y-_dragStart.Y);
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (_isDragging)
        {
            _isDragging=false;
            e.Pointer.Capture(null);

            if (!_pointerMoved)
                _ = HandleClick(e.GetPosition(this));
        }
    }

    private async Task HandleClick(Point screenPoint)
    {
        double schemeX=(screenPoint.X-_panX)/_zoom;
        double schemeY=(screenPoint.Y-_panY)/_zoom;
        var point=new Point(schemeX, schemeY);

        for (int i=_runtime.Length-1; i>=0; i--)
        {
            var runtime=_runtime[i];
            if (runtime.Compiled.OnClick is not { Count: > 0 } actions)
                continue;

            var source=runtime.Compiled.Source;
            double offsetX=runtime.Get(SchemeProperty.PositionOffsetX).Number;
            double offsetY=runtime.Get(SchemeProperty.PositionOffsetY).Number;
            var bounds=new Rect(source.X+offsetX, source.Y+offsetY, source.Width, source.Height);
            double rotation=runtime.Get(SchemeProperty.Rotation).Number;

            if (HitTestShape(source.Kind, bounds, rotation, point))
            {

                var context=new EvaluationContext{Tags=_runtimeClient};
                await ExecuteActions(actions, context);
                return;
            }
        }
    }

    private static bool HitTestShape(ElementKind kind, Rect bounds, double rotationDegrees, Point point)
    {
        var rect=new SKRect((float)bounds.X, (float)bounds.Y, (float)(bounds.X+bounds.Width), (float)(bounds.Y+bounds.Height));

        using var builder=new SKPathBuilder();
        if (kind==ElementKind.Ellipse)
            builder.AddOval(rect);
        else
            builder.AddRect(rect);
        using var path=builder.Detach();

        if (rotationDegrees!=0)
            path.Transform(SKMatrix.CreateRotationDegrees((float)rotationDegrees, rect.MidX, rect.MidY));

        return path.Contains((float)point.X, (float)point.Y);
    }




    private async Task ExecuteActions(IReadOnlyList<CompiledSchemeAction> actions, EvaluationContext context)
    {
        var owner=TopLevel.GetTopLevel(this) as Window;

        foreach (var action in actions)
        {
            if (action.Condition is { } condition && ExpressionVM.Evaluate(condition.ToExpression(), context)==0)
                continue;

            if (action.Confirmation is { } message && owner is not null)
            {
                bool confirmed=await SchemeDialogs.ShowConfirm(owner, message);
                if (!confirmed)
                    return;
            }

            switch (action)
            {
                case CompiledWriteTagAction write:
                {
                    var result=await _runtimeClient.WriteTagAsync(write.TagId, write.Value, RequestedBy);
                    if (result.Status!=TagWriteStatus.Ok)
                        await ReportWriteError(owner, write.TagId, result);
                    break;
                }

                case CompiledToggleTagAction toggle:
                {
                    double current=_runtimeClient.Read(toggle.TagId).Value;
                    var result=await _runtimeClient.WriteTagAsync(toggle.TagId, current==0 ? 1 : 0, RequestedBy);
                    if (result.Status!=TagWriteStatus.Ok)
                        await ReportWriteError(owner, toggle.TagId, result);
                    break;
                }

                case CompiledShowDialogAction dialog when owner is not null:
                    await SchemeDialogs.ShowMessage(owner, dialog.Message);
                    break;

                case CompiledOpenSchemeAction openScheme:
                    Debug.WriteLine($"OpenScheme('{openScheme.SchemeName}') — переключение схем пока не реализовано");
                    break;
            }
        }
    }

        private static async Task ReportWriteError(Window? owner, TagId tagId, TagWriteResult result)
    {
        string message=$"Не удалось записать тег {tagId.Value}: {result.Status} {result.Error}";
        Debug.WriteLine(message);
        if (owner is not null)
            await SchemeDialogs.ShowMessage(owner, message);
    }

    private void Tick()
    {
        long epoch=_runtimeClient.CurrentEpoch;
        if(epoch==_lastEpoch && !_anyVolatile)
            return;

        int count=_runtimeClient.GetChangedSince(_lastEpoch,_changedBuffer);
        _lastEpoch=epoch;
        int effectiveCount=Math.Min(count,_changedBuffer.Length);

        for(int i=0;i<effectiveCount;i++)
            _changedSet[_changedBuffer[i].Value]=true;

        var evalContext=new EvaluationContext{Tags=_runtimeClient};
        int dirtyCount=0;

        var sw=Stopwatch.StartNew();
        foreach(var element in _runtime)
        {
            if (!element.Compiled.HasVolatileBindings && !IsDirty(element.Compiled, _changedSet))
                continue;

            Recompute(element,evalContext);
            dirtyCount++;
        }
        sw.Stop();
        Debug.WriteLine($"Tick: {sw.Elapsed.TotalMilliseconds:F2} мс, пересчитано {dirtyCount} из {_runtime.Length}");

        for(int i =0;i<effectiveCount;i++)
            _changedSet[_changedBuffer[i].Value]=false;

        if(dirtyCount>0)
            InvalidateVisual();
    }

    private void RecomputeAll()
    {
        var evalContext=new EvaluationContext{Tags=_runtimeClient};
        foreach(var element in _runtime)
            Recompute(element,evalContext);
    }

    private static bool IsDirty(CompiledSchemeElement element, bool[] changedSet)
    {
        foreach (int index in element.AllTagIndices)
            if (changedSet[index])
                return true;

        return false;
    }

    private static void Recompute(SchemeElementRuntime element, EvaluationContext context)
    {
        foreach (var binding in element.Compiled.Bindings)
        {
            double raw=ExpressionVM.Evaluate(binding.Expression.ToExpression(), context);
            element.Set(binding.PropertyId, MapValue(binding, raw, element));
        }

        element.QualityBad=element.Compiled.AllTagIndices
            .Any(index => context.Tags.Read(new TagId(index)).Quality != Quality.Good);
        element.BlinkActive=element.Get(SchemeProperty.Blink).AsBool;
    }

    private static PropertyValue MapValue(CompiledBinding binding, double raw, SchemeElementRuntime element)
    {
        if (binding.Mapping!=StopMapping.Direct && binding.Stops is { Count: > 0 } stops)
            return binding.Mapping==StopMapping.Interpolated
                ? Interpolate(stops, raw)
                : PickDiscrete(stops, raw);

        return binding.Type switch
        {
            PropertyType.Number => PropertyValue.FromNumber(raw),
            PropertyType.Boolean => PropertyValue.FromBool(raw!=0),
            PropertyType.Choice => PropertyValue.FromChoice((int)raw),
            PropertyType.Color => PropertyValue.FromColor((uint)raw),
            PropertyType.String => PropertyValue.FromString(FormatText(raw, element)),
            _ => PropertyValue.FromNumber(raw),
        };
    }

    private static PropertyValue PickDiscrete(IReadOnlyList<Stop> stops, double raw)
    {
        var result=stops[0];
        foreach (var stop in stops)
            if (raw>=stop.Input)
                result=stop;
        return result.Output;
    }

        private static PropertyValue Interpolate(IReadOnlyList<Stop> stops, double raw)
    {
        if (raw<=stops[0].Input)
            return stops[0].Output;
        if (raw>=stops[^1].Input)
            return stops[^1].Output;

        for (int i=0; i<stops.Count-1; i++)
        {
            var a=stops[i];
            var b=stops[i+1];
            if (raw>=a.Input && raw<=b.Input)
            {
                double t=(raw-a.Input)/(b.Input-a.Input);
                return a.Output.Type==PropertyType.Color
                    ? PropertyValue.FromColor(LerpColor(a.Output.Color, b.Output.Color, t))
                    : PropertyValue.FromNumber(a.Output.Number+(b.Output.Number-a.Output.Number)*t);
            }
        }

        return stops[^1].Output;
    }

    private static uint LerpColor(uint from, uint to, double t)
    {
        double LerpChannel(int shift)
        {
            double a=(byte)(from>>shift);
            double b=(byte)(to>>shift);
            return Math.Round(a+(b-a)*t);
        }

        return ((uint)LerpChannel(24)<<24) | ((uint)LerpChannel(16)<<16) | ((uint)LerpChannel(8)<<8) | (uint)LerpChannel(0);
    }

    private static string FormatText(double raw, SchemeElementRuntime element)
    {
        string format=element.Get(SchemeProperty.TextFormat).Text ?? "F1";
        string units=element.Get(SchemeProperty.Units).Text ?? "";
        string formatted=raw.ToString(format);
        return string.IsNullOrEmpty(units) ? formatted : $"{formatted} {units}";
    }

    public override void Render(DrawingContext context)
    {
        if(!_visualsPool.TryPop(out var visuals))
            visuals=new List<SchemeElementVisual>(_runtime.Length);
        visuals.Clear();
        var visibleRect=new Rect(-_panX/_zoom, -_panY/_zoom, Bounds.Width/_zoom, Bounds.Height/_zoom);

        foreach (var runtime in _runtime)
        {
            bool visible=runtime.Get(SchemeProperty.Visible).AsBool;
            bool showNow=visible && (!runtime.BlinkActive || _blinkPhase);
            if(!showNow)
                continue;

            var source=runtime.Compiled.Source;
            double offsetX=runtime.Get(SchemeProperty.PositionOffsetX).Number;
            double offsetY=runtime.Get(SchemeProperty.PositionOffsetY).Number;
            double rotation=runtime.Get(SchemeProperty.Rotation).Number;
            var bounds=new Rect(source.X+offsetX,source.Y+offsetY,source.Width,source.Height);
            if (rotation != 0)
            {
                double rad=rotation*Math.PI/180;
                double cos=Math.Abs(Math.Cos(rad)), sin=Math.Abs(Math.Sin(rad));
                double rw=bounds.Width*cos+bounds.Height*sin;
                double rh=bounds.Width*sin+bounds.Height*cos;
                bounds=new Rect(bounds.Center.X-rw/2, bounds.Center.Y-rh/2,rw,rh);
            }

            if(!bounds.Intersects(visibleRect))
                continue;

            string? symbolPath=source.Kind==ElementKind.Symbol
                ? ResolveSymbolPath(runtime.Get(SchemeProperty.SymbolName).Text)
                : null;

            visuals.Add(new SchemeElementVisual(
                Bounds: bounds,
                Fill: ToSkColor(runtime.Get(SchemeProperty.FillColor)),
                QualityBad: runtime.QualityBad,
                Kind: source.Kind,
                RotationDegrees: rotation,
                HasFillLevel: runtime.Compiled.HasFillBinding,
                FillLevel: runtime.Get(SchemeProperty.FillLevel).Number,
                Text: runtime.Get(SchemeProperty.Text).Text ?? "",
                SymbolPath: symbolPath));
        }

        context.Custom(new SchemeDrawOperation(Bounds,visuals,_panX,_panY,_zoom,_visualsPool));
    }

    private static string? ResolveSymbolPath(string? symbolName)
        => string.IsNullOrEmpty(symbolName)
            ? null
            : Path.Combine(AppContext.BaseDirectory, "Symbols", $"{symbolName}.svg");

    private static SKColor ToSkColor(PropertyValue value)
    {
        uint argb=value.Color;
        return new SKColor((byte)(argb>>16), (byte)(argb>>8), (byte)argb, (byte)(argb>>24));
    }
}
