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

            bindings.Add(new CompiledBinding(binding.PropertyId, def.Type, expression.ToExpression(), binding.Mapping, binding.Stops, binding.Volatile));

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
            // C2: значение-выражение компилируется как условие, из текста —
            // пакетный CompiledValueIndex использует пул code.bin, до которого
            // у загрузчика графики доступа нет (единый путь через пул — задел)
            WriteTagAction w when catalog.TryGetIndex(w.Tag.Name, out int idx)=>
                new CompiledWriteTagAction(new TagId(idx), w.Value,
                    w.ValueExpression is { } valueText
                        ? ExpressionCompiler.Compile(valueText, catalog)
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

    /// <summary>C2: разрешение значений составных параметров навигации.
    /// Из пакета приходит готовая классификация (CompiledParameters): вид и
    /// TagId строкового тега заполнены сборкой — доверяем ей. Без неё
    /// (исходные схемы вне сборки, демо) классифицируем по скобкам:
    /// ссылку на строковый тег без сборки не отличить от константы
    /// (ITagCatalog типов не знает) — это удел пакетного пути.</summary>
    private static IReadOnlyList<ResolvedActionParameter>? ResolveParameters(
        IReadOnlyDictionary<string, string>? source,
        List<CompiledActionParameter>? compiled,
        ITagCatalog catalog)
    {
        if (compiled is { Count: > 0 })
            return compiled.Select(p => p.Kind == ActionParamValueKind.StringTagRef
                    ? new ResolvedActionParameter(p.Name, p.Kind)
                        { Text = p.SourceValue, StringTagId = new TagId(p.TagId) }
                    : ResolveSource(p.Name, p.SourceValue, catalog))
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
        var placeholders = new List<CompiledExpression>();
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
            placeholders.Add(ExpressionCompiler.Compile(expression, catalog));
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
