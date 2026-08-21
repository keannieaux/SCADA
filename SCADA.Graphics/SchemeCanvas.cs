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
using System.IO.Pipelines;
using Avalonia.Media.Immutable;


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
    private readonly SchemeElementRuntime[] _dynamic;
    private readonly SchemeElementRuntime[] _static;
    private SKPicture? _staticPicture;
    private readonly IImmutableBrush _background;


    public SchemeCanvas(Scheme scheme, IReadOnlyList<CompiledSchemeElement> elements,
        IRuntimeClient runtimeClient, int tagCount)
    {
        _background=new ImmutableSolidColorBrush(
            Color.FromUInt32(SchemeValue(scheme, SchemeProperty.Background).Color));
        _zoom=SchemeValue(scheme, SchemeProperty.StartZoom).Number;

        _runtime=elements.Select(e=>new SchemeElementRuntime(e)).ToArray();

        _anyVolatile=elements.Any(e=>e.HasVolatileBindings);
        int staticCount=0;
        while(staticCount<_runtime.Length && _runtime[staticCount].Compiled.Bindings.Count==0)
            staticCount++;

        _static=_runtime[..staticCount];
        _dynamic=_runtime[staticCount..];
        _runtimeClient=runtimeClient;
        _changedBuffer=new TagId[tagCount];
        _changedSet=new bool[tagCount];

        RecomputeAll();
    }

        // значение свойства уровня схемы: заданное в файле или умолчание реестра
    private static PropertyValue SchemeValue(Scheme scheme, int propertyId)
    {
        foreach(var property in scheme.Properties)
            if(property.PropertyId==propertyId)
                return property.Value;

        return ElementSchemas.FindSchemeProperty(propertyId)?.Default
            ?? throw new InvalidOperationException($"нет дескриптора свойства схемы {propertyId}");
    }


    public void StartLive()
    {
        // есть volatile-привязки (анимации от времени) — тик на частоте кадров
        // ТЗ §4.1 (30 FPS); статичная схема пересчитывается раз в 200 мс (§9.2)
        var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(_anyVolatile ? 33 : 200)};
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
            double rotation=runtime.Get(SchemeProperty.RotationDegrees).Number;

            if (HitTestShape(source.Kind, bounds, rotation, point))
            {

                var context=new EvaluationContext{Tags=_runtimeClient,NowUnixMs=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()};
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
            if (action.Condition is { } condition && ExpressionVM.Evaluate(condition, context)==0)
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

        var evalContext=new EvaluationContext{Tags=_runtimeClient, NowUnixMs=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()};
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
        var evalContext=new EvaluationContext{Tags=_runtimeClient, NowUnixMs=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()};
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

    // public: пересчёт одного элемента доступен headless-замерам (бенчмарки)
    public static void Recompute(SchemeElementRuntime element, EvaluationContext context)
    {
        var bindings=element.Compiled.Bindings;
        for(int i = 0; i < bindings.Count; i++)
        {
            var binding=bindings[i];
            double raw=ExpressionVM.Evaluate(binding.Expression, context);
            element.Set(binding.PropertyId,MapValue(binding,raw,element));
        }

        bool qualityBad=false;
        foreach(int index in element.Compiled.AllTagIndices)
            if(context.Tags.Read(new TagId(index)).Quality != Quality.Good)
            {
                qualityBad=true;
                break;
            }

        element.QualityBad=qualityBad;
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
        for(int i=0;i<stops.Count;i++)
            if (raw>=stops[i].Input)
                result=stops[i];
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
        if(element.TryGetCachedText(raw,out string cached))
            return cached;

        string format=element.Get(SchemeProperty.TextFormat).Text ?? "F1";
        string units=element.Get(SchemeProperty.Units).Text ?? "";
        string formatted=raw.ToString(format);
        string result=string.IsNullOrEmpty(units)?formatted:$"{formatted} {units}";

        element.CacheText(raw,result);
        return result;
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(_background, new Rect(Bounds.Size));

        _staticPicture ??= RecordStatic();

        if(!_visualsPool.TryPop(out var visuals))
            visuals=new List<SchemeElementVisual>(_dynamic.Length);
        visuals.Clear();
        BuildVisuals(_dynamic, _panX, _panY, _zoom, Bounds.Width, Bounds.Height,
            _blinkPhase, visuals);

        context.Custom(new SchemeDrawOperation(Bounds,visuals,_panX,_panY,_zoom,_visualsPool,_staticPicture));
    }

    private SKPicture RecordStatic()
    {
        var visuals=new List<SchemeElementVisual>(_static.Length);
        BuildVisuals(_static, 500_000,500_000,1,1_000_000,1_000_000,true,visuals);

        using var recorder=new SKPictureRecorder();
        var canvas=recorder.BeginRecording(new SKRect(-500_000,-500_000,500_000,500_000));
        SchemeDrawOperation.DrawItems(canvas,visuals,0,0,1);
        return recorder.EndRecording();
    }

    // public static: построение списка визуалов доступно headless-замерам;
    // viewport — видимый прямоугольник экрана в координатах контрола
    public static void BuildVisuals(IReadOnlyList<SchemeElementRuntime> runtime,
        double panX, double panY, double zoom, double viewportWidth, double viewportHeight,
        bool blinkPhase, List<SchemeElementVisual> visuals)
    {
        var visibleRect=new Rect(-panX/zoom, -panY/zoom, viewportWidth/zoom, viewportHeight/zoom);

        foreach (var element in runtime)
        {
            bool visible=element.Get(SchemeProperty.Visible).AsBool;
            bool showNow=visible && (!element.BlinkActive || blinkPhase);
            if(!showNow)
                continue;

            var source=element.Compiled.Source;
            double offsetX=element.Get(SchemeProperty.PositionOffsetX).Number;
            double offsetY=element.Get(SchemeProperty.PositionOffsetY).Number;
            double rotation=element.Get(SchemeProperty.RotationDegrees).Number;
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

            visuals.Add(new SchemeElementVisual(
                Bounds: bounds,
                Fill: ToSkColor(element.Get(SchemeProperty.FillColor)),
                QualityBad: element.QualityBad,
                Kind: source.Kind,
                RotationDegrees: rotation,
                HasFillLevel: element.Compiled.HasFillBinding,
                FillLevel: element.Get(SchemeProperty.FillLevel).Number,
                Text: element.Get(SchemeProperty.Text).Text ?? "",
                Symbol: element.Compiled.Symbol));
        }
    }

    private static SKColor ToSkColor(PropertyValue value)
    {
        uint argb=value.Color;
        return new SKColor((byte)(argb>>16), (byte)(argb>>8), (byte)argb, (byte)(argb>>24));
    }
}
