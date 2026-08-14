using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using SCADA.Core.Tags;
using SCADA.Expressions;
using SkiaSharp;
using System.Diagnostics;

namespace SCADA.Graphics;

public sealed class SchemeCanvas : Control
{
    private readonly SchemeElementRuntime[] _runtime;
    private readonly ITagTable _tagTable;
    private readonly TagId[] _changedBuffer;
    private readonly bool[] _changedSet;

    private long _lastEpoch=-1;

    public SchemeCanvas(IReadOnlyList<CompiledSchemeElement> elements, ITagTable tagTable, int tagCount)
    {
        _runtime=elements.Select(e=>new SchemeElementRuntime(e)).ToArray();
        _tagTable=tagTable;
        _changedBuffer=new TagId[tagCount];
        _changedSet=new bool[tagCount];

        RecomputeAll();

        var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(200)};
        timer.Tick+=(_,_)=>Tick();
        timer.Start();
    }

    private void Tick()
{
    long epoch=_tagTable.CurrentEpoch;
    if(epoch==_lastEpoch)
        return;

    int count=_tagTable.GetChangedSince(_lastEpoch,_changedBuffer);
    _lastEpoch=epoch;
    int effectiveCount=Math.Min(count,_changedBuffer.Length);

    for(int i=0;i<effectiveCount;i++)
        _changedSet[_changedBuffer[i].Value]=true;

    var evalContext=new EvaluationContext{Tags=_tagTable};
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
        var evalContext=new EvaluationContext{Tags=_tagTable};
        foreach(var element in _runtime)
            Recompute(element,evalContext);
    }

    private static bool IsDirty(CompiledSchemeElement element, bool[] changedSet)
    {
        if(element.Value is { } compiled)
            foreach (int index in compiled.TagIndices)
                if (changedSet[index])
                    return true;

        return element.QualityTag is { } tagId && changedSet[tagId.Value];
    }

    private static void Recompute(SchemeElementRuntime element, EvaluationContext context)
    {
        element.Fill=EvaluateFill(element.Compiled, context);
        element.QualityBad=element.Compiled.QualityTag is { } tagId
            && context.Tags.Read(tagId).Quality != Quality.Good;
    }

    public override void Render(DrawingContext context)
    {
        var visuals=new SchemeElementVisual[_runtime.Length];

        for(int i = 0; i < _runtime.Length; i++)
        {
            var source=_runtime[i].Compiled.Source;
            var bounds=new Rect(source.X, source.Y, source.Width,source.Height);
            visuals[i]=new SchemeElementVisual(bounds,_runtime[i].Fill,_runtime[i].QualityBad,source.Kind);
        }

        context.Custom(new SchemeDrawOperation(Bounds,visuals));
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
