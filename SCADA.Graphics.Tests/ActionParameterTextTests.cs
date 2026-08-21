using SCADA.Core.Schemes;
using SCADA.Core.Tags;
using SCADA.Expressions;
using SCADA.Expressions.Compiler;

namespace SCADA.Graphics.Tests;

/// <summary>
/// C2 (docs/scheme-controls-plan.md): разрешение значений составных
/// параметров действий при загрузке схемы (SchemeLoader) и сборка строки
/// в момент выполнения (ActionParameterText); значение-выражение WriteTag.
/// </summary>
public class ActionParameterTextTests
{
    private sealed class FakeCatalog : ITagCatalog
    {
        private readonly Dictionary<string, int> _tags = new()
        {
            ["Boiler1.Temp"] = 0,
            ["Pump1.Running"] = 1
        };

        public bool TryGetIndex(string name, out int index)
            => _tags.TryGetValue(name, out index);
    }

    private sealed class FakeTagReader : ITagValueReader
    {
        private readonly Dictionary<int, double> _values;
        public FakeTagReader(Dictionary<int, double> values) => _values = values;

        public TagValue Read(TagId id)
            => new(_values.GetValueOrDefault(id.Value), 0, Quality.Good);
    }

    private static Scheme SchemeWithActions(params SchemeAction[] actions)
        => new()
        {
            Id = Guid.NewGuid(),
            Name = "test",
            Elements =
            [
                new SchemeElement
                {
                    Id = Guid.NewGuid(),
                    Kind = ElementKind.Rectangle,
                    X = 0, Y = 0, Width = 10, Height = 10,
                    Events = [new SchemeEvent { Kind = SchemeEventKind.Click, Actions = [..actions] }]
                }
            ]
        };

    private static CompiledSchemeAction CompileSingle(Scheme scheme)
    {
        var elements = SchemeLoader.Compile(scheme, new FakeCatalog());
        return Assert.Single(elements[0].OnClick!);
    }

    private static EvaluationContext Context(params (int Id, double Value)[] values)
        => new()
        {
            Tags = new FakeTagReader(values.ToDictionary(v => v.Id, v => v.Value)),
            NowUnixMs = 0
        };

    [Fact]
    public void ConstantParameter_ResolvesAsIs()
    {
        var scheme = SchemeWithActions(new OpenPopupAction("pump",
            new Dictionary<string, string> { ["Prefix"] = "Pump5" }));

        var action = Assert.IsType<CompiledOpenPopupAction>(CompileSingle(scheme));

        var parameter = Assert.Single(action.Parameters!);
        Assert.Equal(ActionParamValueKind.Constant, parameter.Kind);
        Assert.Equal("Pump5", ActionParameterText.Resolve(parameter, Context()));
    }

    [Fact]
    public void TemplateParameter_PlaceholdersEvaluatedAtRuntime()
    {
        var scheme = SchemeWithActions(new OpenPopupAction("pump",
            new Dictionary<string, string> { ["Prefix"] = "Насосная{Boiler1.Temp}.Pump{Pump1.Running}" }));

        var action = Assert.IsType<CompiledOpenPopupAction>(CompileSingle(scheme));

        var parameter = Assert.Single(action.Parameters!);
        Assert.Equal(ActionParamValueKind.Template, parameter.Kind);
        Assert.Equal("Насосная7.Pump1",
            ActionParameterText.Resolve(parameter, Context((0, 7), (1, 1))));
    }

    [Fact]
    public void StringTagRef_FromPackageCompiledForm_ReadsTagAtRuntime()
    {
        // пакетный путь: классификацию сделала сборка (ITagCatalog типов
        // не знает), загрузчик доверяет CompiledParameters
        var action = new OpenPopupAction("pump",
            new Dictionary<string, string> { ["Selected"] = "Session.SelectedPump" })
        {
            CompiledParameters =
            [
                new CompiledActionParameter
                {
                    Name = "Selected", SourceValue = "Session.SelectedPump",
                    Kind = ActionParamValueKind.StringTagRef, TagId = 42
                }
            ]
        };

        var compiled = Assert.IsType<CompiledOpenPopupAction>(
            CompileSingle(SchemeWithActions(action)));

        var parameter = Assert.Single(compiled.Parameters!);
        Assert.Equal(ActionParamValueKind.StringTagRef, parameter.Kind);
        Assert.Equal(new TagId(42), parameter.StringTagId);
        Assert.Equal("Pump5", ActionParameterText.Resolve(parameter, Context(),
            id => id.Value == 42 ? "Pump5" : ""));
    }

    [Fact]
    public void SourcePath_WithoutCompiledParameters_ClassifiesByBraces()
    {
        // исходники вне сборки (демо): со скобками — шаблон, без — константа
        var scheme = SchemeWithActions(new OpenSchemeAction("detail",
            new Dictionary<string, string>
            {
                ["Static"] = "Pump5",
                ["Dynamic"] = "Pump{Pump1.Running}"
            }));

        var action = Assert.IsType<CompiledOpenSchemeAction>(CompileSingle(scheme));

        Assert.Equal(ActionParamValueKind.Constant,
            action.Parameters!.Single(p => p.Name == "Static").Kind);
        Assert.Equal(ActionParamValueKind.Template,
            action.Parameters!.Single(p => p.Name == "Dynamic").Kind);
    }

    [Fact]
    public void UnbalancedTemplate_Throws()
    {
        var scheme = SchemeWithActions(new OpenPopupAction("pump",
            new Dictionary<string, string> { ["Prefix"] = "Pump{Boiler1.Temp" }));

        Assert.Throws<InvalidOperationException>(
            () => SchemeLoader.Compile(scheme, new FakeCatalog()));
    }

    [Fact]
    public void WriteTagValueExpression_Compiles_AndEvaluatesAtRuntime()
    {
        var scheme = SchemeWithActions(new WriteTagAction(SchemeTagRef.Absolute("Pump1.Running"), 0)
        {
            ValueExpression = "Pump1.Running + 1"
        });

        var action = Assert.IsType<CompiledWriteTagAction>(CompileSingle(scheme));

        Assert.NotNull(action.ValueExpression);
        double value = ExpressionVM.Evaluate(
            action.ValueExpression.Value, Context((1, 41)));
        Assert.Equal(42, value);
    }

    [Fact]
    public void WriteTagConstant_LeavesValueExpressionNull()
    {
        var scheme = SchemeWithActions(new WriteTagAction(SchemeTagRef.Absolute("Pump1.Running"), 5));

        var action = Assert.IsType<CompiledWriteTagAction>(CompileSingle(scheme));

        Assert.Null(action.ValueExpression);
        Assert.Equal(5, action.Value);
    }
}
