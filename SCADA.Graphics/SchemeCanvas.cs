using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using SCADA.Core.Tags;
using SCADA.Expressions;
using SkiaSharp;

namespace SCADA.Graphics;

public sealed class SchemeCanvas : Control
{
    private readonly IReadOnlyList<CompiledSchemeElement> _elements;
    private readonly ITagTable _tagTable;
    private long _lastEpoch=-1;

    public SchemeCanvas(IReadOnlyList<CompiledSchemeElement> elements, ITagTable tagTable)
    {
        _elements=elements;
        _tagTable=tagTable;

        var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(200)};
        timer.Tick+=(_,_)=>Tick();
        timer.Start();
    }

    private void Tick()
    {
        long epoch=_tagTable.CurrentEpoch;
        if(epoch==_lastEpoch)
            return;

        _lastEpoch=epoch;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var evalContext=new EvaluationContext{Tags=_tagTable};
        var visuals=new SchemeElementVisual[_elements.Count];

        for(int i = 0; i < _elements.Count; i++)
        {
            var element=_elements[i];
            var bounds=new Rect(element.Source.X, element.Source.Y, element.Source.Width,element.Source.Height);
            var fill=EvaluateFill(element,evalContext);
            bool qualityBad=element.QualityTag is { } tagId && _tagTable.Read(tagId).Quality != Quality.Good;

            visuals[i]=new SchemeElementVisual(bounds,fill,qualityBad);
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
