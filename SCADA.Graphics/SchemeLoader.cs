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
            // C2: значение-выражение и плейсхолдеры параметров приходят
            // индексами пула — сборка их уже скомпилировала
            WriteTagAction w when client.TryGetTagId(w.Tag.Name, out var id)=>
                new CompiledWriteTagAction(id, w.Value,
                    w.CompiledValueIndex is { } valueIndex ? pool.ToExpression(valueIndex) : null,
                    action.Confirmation, condition),
            ToggleTagAction t when client.TryGetTagId(t.Tag.Name, out var id)=>
                new CompiledToggleTagAction(id, action.Confirmation, condition),
            OpenSchemeAction o=>
                new CompiledOpenSchemeAction(o.SchemeName,
                    LoadParameters(o.CompiledParameters, pool),
                    action.Confirmation, condition),
            OpenPopupAction o=>
                new CompiledOpenPopupAction(o.TemplateName,
                    LoadParameters(o.CompiledParameters, pool),
                    action.Confirmation, condition),
            ShowDialogAction d=>
                new CompiledShowDialogAction(d.Message, action.Confirmation, condition),
            _=>null,
        };
    }

    /// <summary>C2, пакетный путь: разбирать нечего — сборка положила в секцию
    /// готовую исполнительную форму (вид значения, тег, литералы), а выражения
    /// лежат в пуле. Парсер скобок здесь не нужен и не должен появляться:
    /// он есть только в сборке, поэтому правила разбора не могут разойтись.</summary>
    private static IReadOnlyList<ResolvedActionParameter>? LoadParameters(
        List<CompiledActionParameter>? compiled, CodePool pool)
    {
        if (compiled is not { Count: > 0 })
            return null;

        var result = new List<ResolvedActionParameter>(compiled.Count);
        foreach (var parameter in compiled)
        {
            switch (parameter.Kind)
            {
                case ActionParamValueKind.StringTagRef:
                    result.Add(new ResolvedActionParameter(parameter.Name, parameter.Kind)
                    {
                        StringTagId = new TagId(parameter.TagId)
                    });
                    break;

                case ActionParamValueKind.Template:
                    result.Add(new ResolvedActionParameter(parameter.Name, parameter.Kind)
                    {
                        Literals = parameter.Literals ?? [],
                        Placeholders = (parameter.ExpressionIndices ?? [])
                            .Select(pool.ToExpression)
                            .ToList()
                    });
                    break;

                default:
                    result.Add(new ResolvedActionParameter(parameter.Name,
                        ActionParamValueKind.Constant) { Text = parameter.Text });
                    break;
            }
        }
        return result;
    }

    private static CompiledSchemeAction? CompileAction(SchemeAction action, ITagCatalog catalog)
    {
        Expression? condition=action.Condition is { } text
            ? ExpressionCompiler.Compile(text, catalog).ToExpression()
            : null;

        return action switch
        {
            // C2: значение-выражение компилируется как условие, из текста —
            // пакетный CompiledValueIndex использует пул code.bin, до которого
            // у загрузчика графики доступа нет (единый путь через пул — задел)
            WriteTagAction w when catalog.TryGetIndex(w.Tag.Name, out int idx)=>
                new CompiledWriteTagAction(new TagId(idx), w.Value,
                    w.ValueExpression is { } valueText
                        ? ExpressionCompiler.Compile(valueText, catalog).ToExpression()
                        : null,
                    action.Confirmation, condition),
            ToggleTagAction t when catalog.TryGetIndex(t.Tag.Name, out int idx)=>
                new CompiledToggleTagAction(new TagId(idx), action.Confirmation, condition),
            OpenSchemeAction o=>
                new CompiledOpenSchemeAction(o.SchemeName,
                    ResolveParameters(o.Parameters, o.CompiledParameters, catalog),
                    action.Confirmation, condition),
            OpenPopupAction o=>
                new CompiledOpenPopupAction(o.TemplateName,
                    ResolveParameters(o.Parameters, o.CompiledParameters, catalog),
                    action.Confirmation, condition),
            ShowDialogAction d=>
                new CompiledShowDialogAction(d.Message, action.Confirmation, condition),
            _=>null,
        };
    }

    /// <summary>C2: разрешение значений составных параметров навигации
    /// на исходном пути (схема не из пакета: редактор, демо).
    ///
    /// Текст значений здесь берётся только из словаря Parameters — в
    /// исполнительной форме его нет (§11: в пакете байткод и индексы,
    /// не текст выражений). От готовой классификации, если она есть,
    /// нужен единственный факт: ссылка на строковый тег — её без сборки
    /// не отличить от константы, ITagCatalog типов не знает. Остальное
    /// разбирается по скобкам, потому что выражения тут компилируются
    /// из текста в любом случае.</summary>
    private static IReadOnlyList<ResolvedActionParameter>? ResolveParameters(
        IReadOnlyDictionary<string, string>? source,
        List<CompiledActionParameter>? compiled,
        ITagCatalog catalog)
    {
        if (compiled is { Count: > 0 })
            return compiled.Select(p => p.Kind == ActionParamValueKind.StringTagRef
                    ? new ResolvedActionParameter(p.Name, p.Kind)
                        { StringTagId = new TagId(p.TagId) }
                    : ResolveSource(p.Name,
                        source?.GetValueOrDefault(p.Name) ?? p.Text, catalog))
                .ToList();

        if (source is { Count: > 0 })
            return source.Select(kv => ResolveSource(kv.Key, kv.Value, catalog)).ToList();

        return null;
    }

    /// <summary>Константа или шаблон с плейсхолдерами "{выражение}".
    /// Правила разбора зеркалят сборку (ProjectBuildService.CompileParameterTemplate):
    /// несбалансированные/пустые скобки — ошибка, экранирования нет.</summary>
    private static ResolvedActionParameter ResolveSource(string name, string value,
        ITagCatalog catalog)
    {
        if (!value.Contains('{') && !value.Contains('}'))
            return new ResolvedActionParameter(name, ActionParamValueKind.Constant)
                { Text = value };

        var literals = new List<string>();
        var placeholders = new List<Expression>();
        int position = 0;
        while (position < value.Length)
        {
            int open = value.IndexOf('{', position);
            int close = value.IndexOf('}', position);
            if (open < 0 && close < 0)
                break;
            if (close >= 0 && (open < 0 || close < open))
                throw new InvalidOperationException(
                    $"Параметр действия '{name}': '}}' без открывающей '{{' в шаблоне '{value}'");
            literals.Add(value[position..open]);
            int end = value.IndexOf('}', open + 1);
            if (end < 0)
                throw new InvalidOperationException(
                    $"Параметр действия '{name}': незакрытый плейсхолдер в шаблоне '{value}'");
            string expression = value[(open + 1)..end].Trim();
            if (expression.Length == 0)
                throw new InvalidOperationException(
                    $"Параметр действия '{name}': пустой плейсхолдер в шаблоне '{value}'");
            placeholders.Add(ExpressionCompiler.Compile(expression, catalog).ToExpression());
            position = end + 1;
        }
        literals.Add(value[position..]);

        return new ResolvedActionParameter(name, ActionParamValueKind.Template)
        {
            Text = value,
            Literals = literals,
            Placeholders = placeholders
        };
    }
}
