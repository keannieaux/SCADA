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

            TagId? qualityTag=element.QualityTagName is { } name && catalog.TryGetIndex(name, out int index)
                ? new TagId(index)
                :null;

            result.Add(new CompiledSchemeElement(element, value, qualityTag));
        }

        return result;
    }
}
