using SCADA.Core.Schemes;
using SCADA.Core.Tags;
using SCADA.Expressions;
using SCADA.Expressions.Compiler;
using SCADA.Package.Sections;
using SCADA.Runtime.Runtime;
using SkiaSharp;

namespace SCADA.Graphics;

public static class SchemeLoader
{
    /// <summary>Схема из пакета: выражения уже скомпилированы при сборке,
    /// привязка ссылается на пул индексом (B2, концепт §11).</summary>
    public static IReadOnlyList<CompiledSchemeElement> Load(Scheme scheme, IRuntimeClient client)
    {
        var pool=client.GetCodePool();
        var result=new List<CompiledSchemeElement>(scheme.Elements.Count);

        foreach(var element in InZOrder(scheme.Elements))
            result.Add(LoadElement(element, pool, client));

        return result;
    }

    /// <summary>Схема, собранная в коде: выражения компилируются на лету.
    /// Нужен SyntheticSchemeGenerator и headless-стенду SchemeRenderingBenchmarks.</summary>
    public static IReadOnlyList<CompiledSchemeElement> Compile(Scheme scheme, ITagCatalog catalog)
    {
        var result=new List<CompiledSchemeElement>(scheme.Elements.Count);

        foreach(var element in InZOrder(scheme.Elements))
            result.Add(CompileElement(element, catalog));

        return result;
    }

    // порядок наложения задаёт ZOrder; при равных — порядок в файле,
    // OrderBy устойчив, поэтому исходную последовательность не перемешает
    private static IEnumerable<SchemeElement> InZOrder(IReadOnlyList<SchemeElement> elements)
        => elements.OrderBy(e => e.ZOrder);

    private static CompiledSchemeElement LoadElement(SchemeElement element, CodePool pool,
        IRuntimeClient client)
    {
        var bindings=new List<CompiledBinding>(element.Bindings.Count);
        var allTagIndices=new HashSet<int>();
        bool hasFillBinding=false;
        bool hasVolatile=false;

        foreach(var binding in element.Bindings)
        {
            var def=ElementSchemas.Find(element.Kind, binding.PropertyId)
                ?? throw new InvalidOperationException(
                    $"свойство {binding.PropertyId} не найдено у вида {element.Kind} (элемент '{element.Name}')");

            if(binding.CompiledExpressionIndex is { } index)
            {
                bindings.Add(new CompiledBinding(binding.PropertyId, def.Type,
                    pool.ToExpression(index), binding.Mapping, binding.Stops, binding.Volatile));
            }
            // прямая строковая привязка (концепт §4.6): выражение — это ровно имя
            // строкового тега, сборщик его не компилирует, а кладёт один индекс.
            // Строки в ВМ не участвуют, читаются напрямую
            else if(binding.CompiledTagIndices is [int stringTag])
            {
                bindings.Add(new CompiledBinding(binding.PropertyId, def.Type, null,
                    binding.Mapping, binding.Stops, binding.Volatile, new TagId(stringTag)));
            }
            else
            {
                throw new InvalidOperationException(
                    $"привязка свойства {binding.PropertyId} элемента '{element.Name}' " +
                    "не скомпилирована: пакет собран неверно");
            }


            foreach(int tagIndex in binding.CompiledTagIndices ?? [])
                allTagIndices.Add(tagIndex);

            if(binding.PropertyId==SchemeProperty.FillLevel)
                hasFillBinding=true;
            if(binding.Volatile)
                hasVolatile=true;
        }

        return Build(element, bindings, allTagIndices, hasFillBinding, hasVolatile,
            LoadSymbol(element, client),
            element.Events

                .FirstOrDefault(e=>e.Kind==SchemeEventKind.Click)?.Actions
                .Select(a=>LoadAction(a, pool, client))
                .Where(a=>a is not null)
                .Select(a=>a!)
                .ToList());
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
            var def=ElementSchemas.Find(element.Kind, binding.PropertyId)
                ?? throw new InvalidOperationException(
                    $"свойство {binding.PropertyId} не найдено у вида {element.Kind} (элемент '{element.Name}')");

            bindings.Add(new CompiledBinding(binding.PropertyId, def.Type,
                expression.ToExpression(), binding.Mapping, binding.Stops, binding.Volatile));

            foreach(int index in expression.TagIndices)
                allTagIndices.Add(index);

            if(binding.PropertyId==SchemeProperty.FillLevel)
                hasFillBinding=true;
            if(binding.Volatile)
                hasVolatile=true;
        }

        return Build(element, bindings, allTagIndices, hasFillBinding, hasVolatile,
            null,
            element.Events

                .FirstOrDefault(e=>e.Kind==SchemeEventKind.Click)?.Actions
                .Select(a=>CompileAction(a, catalog))
                .Where(a=>a is not null)
                .Select(a=>a!)
                .ToList());
    }

    private static CompiledSchemeElement Build(SchemeElement element,
        List<CompiledBinding> bindings, HashSet<int> allTagIndices,
        bool hasFillBinding, bool hasVolatile, SKPicture? symbol, List<CompiledSchemeAction>? onClick)
        => new(
            Source: element,
            Bindings: bindings,
            AllTagIndices: allTagIndices.ToArray(),
            HasFillBinding: hasFillBinding,
            HasVolatileBindings: hasVolatile,
            Symbol: symbol,
            OnClick: onClick);

    private static SKPicture? LoadSymbol(SchemeElement element, IRuntimeClient client)
    {
        if(element.Kind!=ElementKind.Symbol)
            return null;

        string? name=element.Properties
            .FirstOrDefault(p=>p.PropertyId==SchemeProperty.SymbolName).Value.Text;

        return string.IsNullOrEmpty(name) ? null : SymbolCache.Load($"symbols/{name}", client);
    }

    private static CompiledSchemeAction? LoadAction(SchemeAction action, CodePool pool,
        IRuntimeClient client)
    {
        Expression? condition=action.CompiledConditionIndex is { } index
            ? pool.ToExpression(index)
            : null;

        return action switch
        {
            WriteTagAction w when client.TryGetTagId(w.Tag.Name, out var id)=>
                new CompiledWriteTagAction(id, w.Value, action.Confirmation, condition),
            ToggleTagAction t when client.TryGetTagId(t.Tag.Name, out var id)=>
                new CompiledToggleTagAction(id, action.Confirmation, condition),
            OpenSchemeAction o=>
                new CompiledOpenSchemeAction(o.SchemeName, action.Confirmation, condition),
            ShowDialogAction d=>
                new CompiledShowDialogAction(d.Message, action.Confirmation, condition),
            _=>null,
        };
    }

    private static CompiledSchemeAction? CompileAction(SchemeAction action, ITagCatalog catalog)
    {
        Expression? condition=action.Condition is { } text
            ? ExpressionCompiler.Compile(text, catalog).ToExpression()
            : null;

        return action switch
        {
            WriteTagAction w when catalog.TryGetIndex(w.Tag.Name, out int idx)=>
                new CompiledWriteTagAction(new TagId(idx), w.Value, action.Confirmation, condition),
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
