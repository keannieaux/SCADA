using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using SCADA.Core.Tags;
using SCADA.Expressions;
using SCADA.Runtime.Runtime;
using SkiaSharp;
using System.Diagnostics;
using Avalonia.Input;

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

    public SchemeCanvas(IReadOnlyList<CompiledSchemeElement> elements, IRuntimeClient runtimeClient, int tagCount)
    {
        _runtime=elements.Select(e=>new SchemeElementRuntime(e)).ToArray();
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
            var bounds=new Rect(source.X+runtime.OffsetX, source.Y+runtime.OffsetY, source.Width, source.Height);

            if (bounds.Contains(point))
            {
                await ExecuteActions(actions);
                return;
            }
        }
    }

    private async Task ExecuteActions(IReadOnlyList<CompiledSchemeAction> actions)
    {
        var owner=TopLevel.GetTopLevel(this) as Window;

        foreach (var action in actions)
        {
            switch (action)
            {
                case CompiledWriteTagAction write:
                {
                    var result=await _runtimeClient.WriteTagAsync(write.TagId, write.Value, RequestedBy);
                    if (result.Status!=TagWriteStatus.Ok)
                        Debug.WriteLine($"WriteTag({write.TagId.Value}): {result.Status} {result.Error}");
                    break;
                }

                case CompiledToggleTagAction toggle:
                {
                    double current=_runtimeClient.Read(toggle.TagId).Value;
                    var result=await _runtimeClient.WriteTagAsync(toggle.TagId, current==0 ? 1 : 0, RequestedBy);
                    if (result.Status!=TagWriteStatus.Ok)
                        Debug.WriteLine($"ToggleTag({toggle.TagId.Value}): {result.Status} {result.Error}");
                    break;
                }

                case CompiledShowDialogAction dialog when owner is not null:
                    await SchemeDialogs.ShowMessage(owner, dialog.Message);
                    break;

                case CompiledConfirmAction confirm when owner is not null:
                    bool confirmed=await SchemeDialogs.ShowConfirm(owner, confirm.Message);
                    if (!confirmed)
                        return;
                    break;

                case CompiledOpenSchemeAction openScheme:
                    Debug.WriteLine($"OpenScheme('{openScheme.SchemeName}') — переключение схем пока не реализовано");
                    break;
            }
        }
    }

    private void Tick()
    {
        long epoch=_runtimeClient.CurrentEpoch;
        if(epoch==_lastEpoch)
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
            if (!IsDirty(element.Compiled, _changedSet))
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
        if(element.Value is { } compiled)
            foreach (int index in compiled.TagIndices)
                if (changedSet[index])
                    return true;

        if(element.Visible is { } visible)
            foreach (int index in visible.TagIndices)
                if(changedSet[index])
                    return true;

        if(element.BlinkWhen is {} blink)
            foreach(int index in blink.TagIndices)
                if(changedSet[index])
                    return true;

        if(element.Rotation is { } rotation)
            foreach(int index in rotation.TagIndices)
                if(changedSet[index])
                    return true;

        if(element.FillLevel is {} fillLevel)
            foreach(int index in fillLevel.TagIndices)
                if(changedSet[index])
                    return true;

        if(element.Text is {} text)
            foreach(int index in text.TagIndices)
                if(changedSet[index])
                    return true;

        if(element.PositionX is {} positionX)
            foreach(int index in positionX.TagIndices)
                if(changedSet[index])
                    return true;

        if(element.PositionY is {} positionY)
            foreach(int index in positionY.TagIndices)
                if(changedSet[index])
                    return true;

        return element.QualityTag is { } tagId && changedSet[tagId.Value];
    }

    private static void Recompute(SchemeElementRuntime element, EvaluationContext context)
    {
        element.Fill=EvaluateFill(element.Compiled, context);
        element.QualityBad=element.Compiled.QualityTag is { } tagId
            && context.Tags.Read(tagId).Quality != Quality.Good;
        element.Visible=element.Compiled.Visible is not { } visible
            || ExpressionVM.Evaluate(visible.ToExpression(), context) !=0;
        element.BlinkActive=element.Compiled.BlinkWhen is { } blink
            && ExpressionVM.Evaluate(blink.ToExpression(), context)!=0;
        element.RotationDegrees=element.Compiled.Rotation is {} rotation
            ? ExpressionVM.Evaluate(rotation.ToExpression(), context)
            : 0;
        element.FillLevel=element.Compiled.FillLevel is {} fillLevel
            ? Math.Clamp(ExpressionVM.Evaluate(fillLevel.ToExpression(), context),0,1)
            :0;
        element.DisplayText=EvaluateText(element.Compiled,context);
        element.OffsetX=element.Compiled.PositionX is {} positionX
            ? ExpressionVM.Evaluate(positionX.ToExpression(),context)
            : 0;
        element.OffsetY=element.Compiled.PositionY is {} positionY
            ? ExpressionVM.Evaluate(positionY.ToExpression(), context)
            :0;
    }

    private static string EvaluateText(CompiledSchemeElement element, EvaluationContext context)
    {
        if(element.Text is not { } text)
            return "";

        double value=ExpressionVM.Evaluate(text.ToExpression(), context);
        string formatted=value.ToString(element.Source.TextFormat ?? "F1");
        return string.IsNullOrEmpty(element.Source.Units) ? formatted : $"{formatted} {element.Source.Units}";
    }

    public override void Render(DrawingContext context)
    {
        var visuals=new List<SchemeElementVisual>(_runtime.Length);

        foreach (var runtime in _runtime)
        {
            bool showNow=runtime.Visible && (!runtime.BlinkActive || _blinkPhase);
            if(!showNow)
                continue;

            var source=runtime.Compiled.Source;
            var bounds=new Rect(source.X+runtime.OffsetX,source.Y+runtime.OffsetY,source.Width,source.Height);
            visuals.Add(new SchemeElementVisual(
                Bounds: bounds,
                Fill: runtime.Fill,
                QualityBad: runtime.QualityBad,
                Kind: source.Kind,
                RotationDegrees: runtime.RotationDegrees,
                HasFillLevel: source.FillLevelExpression is not null,
                FillLevel: runtime.FillLevel,
                Text: runtime.DisplayText,
                SymbolPath: source.SymbolPath));
        }

        context.Custom(new SchemeDrawOperation(Bounds,visuals,_panX,_panY,_zoom));
    }

    private static SKColor EvaluateFill(CompiledSchemeElement element, EvaluationContext context)
    {
        var normal=ThemeColors.Resolve("Bg3Color", new SKColor(0x33,0x38,0x3D));
        if (element.Value is not { } compiled)
            return normal;

        double value=ExpressionVM.Evaluate(compiled.ToExpression(), context);

        if (value >= element.Source.CritThreshold)
            return ThemeColors.Resolve("CritColor", new SKColor(0xE5,0x48,0x4D));
        if(value>=element.Source.WarnThreshold)
            return ThemeColors.Resolve("WarnColor", new SKColor(0xE8,0xA3,0x3D));
        return normal;
    }
}
