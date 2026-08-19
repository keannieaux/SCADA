using System.Diagnostics;
using Avalonia.Markup.Xaml.MarkupExtensions;
using SCADA.Core.Schemes;
using SCADA.Core.Tags;
using SCADA.Expressions.Compiler;

namespace SCADA.Graphics;

public static class SchemeLoader
{
    public static IReadOnlyList<CompiledSchemeElement> Compile(Scheme scheme, ITagCatalog catalog)
    {
        var result=new List<CompiledSchemeElement>(scheme.Elements.Count);

        foreach(var element in scheme.Elements)
            result.Add(CompileElement(element, catalog));

        return result;
    }

    private static CompiledSchemeElement CompileElement(SchemeElement element, ITagCatalog catalog)
    {
        var bindings=new List<CompiledBinding>(element.Bindings.Count);
        var allTagIndices=new HashSet<int>();
        bool hasFillBinding=false;
        bool hasVolatile=false;

        foreach(var binding in element.Bindings)
        {
            var expression=ExpressionCompiler.Compile(binding.Expression, catalog);
            var def =ElementSchemas.Find(element.Kind, binding.PropertyId)
                ?? throw new InvalidOperationException(
                    $"свойство {binding.PropertyId} не найдено у вида {element.Kind} (элемент '{element.Name}')");

            bindings.Add(new CompiledBinding(binding.PropertyId, def.Type, expression, binding.Mapping, binding.Stops, binding.Volatile));

            foreach(int index in expression.TagIndices)
                allTagIndices.Add(index);

            if(binding.PropertyId==SchemeProperty.FillLevel)
                hasFillBinding=true;
            if(binding.Volatile)
                hasVolatile=true;

        }

        var onClick=element.Events
            .FirstOrDefault(e=>e.Kind==SchemeEventKind.Click)?.Actions
            .Select(a=>CompileAction(a,catalog))
            .Where(a=>a is not null)
            .Select(a=>a!)
            .ToList();

        return new CompiledSchemeElement(
            Source: element,
            Bindings: bindings,
            AllTagIndices: allTagIndices.ToArray(),
            HasFillBinding: hasFillBinding,
            HasVolatileBindings: hasVolatile,
            OnClick: onClick);
    }

    private static CompiledSchemeAction? CompileAction(SchemeAction action, ITagCatalog catalog)
    {
        CompiledExpression? condition= action.Condition is { } text
            ? ExpressionCompiler.Compile(text, catalog)
            : null;

        return action switch
        {
            WriteTagAction w when catalog.TryGetIndex(w.Tag.Name, out int idx)=>
                new CompiledWriteTagAction(new TagId(idx),w.Value,action.Confirmation, condition),
            ToggleTagAction t when catalog.TryGetIndex(t.Tag.Name, out int idx)=>
                new CompiledToggleTagAction(new TagId(idx), action.Confirmation, condition),
            OpenSchemeAction o=>
                new CompiledOpenSchemeAction(o.SchemeName, action.Confirmation, condition),
            ShowDialogAction d=>
                new CompiledShowDialogAction(d.Message, action.Confirmation, condition),
            _=>null,
        };
    }
}
