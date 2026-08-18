using SCADA.Core.Tags;
using SCADA.Expressions.Compiler;

namespace SCADA.Graphics;

public static class SchemeLoader
{
    public static IReadOnlyList<CompiledSchemeElement> Compile(Scheme scheme, ITagCatalog catalog)
    {
        var result=new List<CompiledSchemeElement>(scheme.Elements.Count);

        foreach (var element in scheme.Elements)
        {
            CompiledExpression? value = element.ValueExpression is { } text
                ? ExpressionCompiler.Compile(text, catalog)
                :null;

            CompiledExpression? visible=element.VisibleExpression is { } visibleText
                ? ExpressionCompiler.Compile(visibleText,catalog)
                :null;

            CompiledExpression? blinkWhen=element.BlinkWhenExpression is { } blinkText
                ? ExpressionCompiler.Compile(blinkText,catalog)
                :null;

            CompiledExpression? rotation=element.RotationExpression is {} rotationText
                ? ExpressionCompiler.Compile(rotationText, catalog)
                :null;

            CompiledExpression? fillLevel=element.FillLevelExpression is { } fillLevelText
                ? ExpressionCompiler.Compile(fillLevelText,catalog)
                :null;

            CompiledExpression? displayText=element.TextExpression is {} textSource
                ? ExpressionCompiler.Compile(textSource,catalog)
                :null;

            CompiledExpression? positionX=element.PositionXExpression is {} positionXText
                ? ExpressionCompiler.Compile(positionXText,catalog)
                : null;

            CompiledExpression? positionY=element.PositionYExpression is {} positionYText
                ? ExpressionCompiler.Compile(positionYText,catalog)
                : null;

            TagId? qualityTag=element.QualityTagName is { } name && catalog.TryGetIndex(name, out int index)
                ? new TagId(index)
                :null;
            IReadOnlyList<CompiledSchemeAction>? onClick = element.OnClick
                ?.Select(a => CompileAction(a, catalog))
                .Where(a => a is not null)
                .Select(a => a!)
                .ToList();

            result.Add(new CompiledSchemeElement(
                Source: element,
                Value: value,
                Visible: visible,
                BlinkWhen: blinkWhen,
                Rotation: rotation,
                FillLevel: fillLevel,
                Text: displayText,
                PositionX: positionX,
                PositionY: positionY,
                QualityTag: qualityTag,
                OnClick: onClick));
        }

        return result;
    }

    private static CompiledSchemeAction? CompileAction(SchemeAction action, ITagCatalog catalog) => action switch
    {
        WriteTagAction w when catalog.TryGetIndex(w.TagName, out int idx) => new CompiledWriteTagAction(new TagId(idx), w.Value),
        ToggleTagAction t when catalog.TryGetIndex(t.TagName, out int idx) => new CompiledToggleTagAction(new TagId(idx)),
        OpenSchemeAction o => new CompiledOpenSchemeAction(o.SchemeName),
        ShowDialogAction d => new CompiledShowDialogAction(d.Message),
        ConfirmAction c => new CompiledConfirmAction(c.Message),
        _ => null
    };

}
