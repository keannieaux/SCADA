using SCADA.Core.Tags;
using SCADA.Expressions;
using SCADA.Runtime.TagTable;

namespace SCADA.Expressions.Compiler.Tests;

// полный цикл: текст → компилятор → байткод → ВМ → значение
public class CompilerIntegrationTests
{
    private sealed class FakeCatalog : ITagCatalog
    {
        private readonly Dictionary<string, int> _tags = new(StringComparer.OrdinalIgnoreCase);
        public FakeCatalog Add(string name, int index) { _tags[name] = index; return this; }
        public bool TryGetIndex(string name, out int index) => _tags.TryGetValue(name, out index);
    }

    private static ITagCatalog Catalog() => new FakeCatalog()
        .Add("Boiler1.Temp", 0)
        .Add("Pump1.Running", 1)
        .Add("Tank1.Level", 2)
        .Add("Tank1.Volume", 3);

    private static TagTable Table()
    {
        var table = new TagTable(capacity: 4);
        table.Write(new TagId(0), new TagValue(100.0, 1000, Quality.Good));  // Temp
        table.Write(new TagId(1), new TagValue(1.0, 1000, Quality.Good));    // Running
        table.Write(new TagId(2), new TagValue(30.0, 1000, Quality.Good));   // Level
        table.Write(new TagId(3), new TagValue(120.0, 1000, Quality.Good));  // Volume
        return table;
    }

    private static double Evaluate(string text, ITagTable table)
    {
        var compiled = ExpressionCompiler.Compile(text, Catalog());
        return ExpressionVM.Evaluate(compiled.ToExpression(), new EvaluationContext { Tags = table });
    }

    [Fact]
    public void Arithmetic() =>
        Assert.Equal(5.0, Evaluate("2 + 3", Table()));

    [Fact]
    public void Precedence() =>
        Assert.Equal(14.0, Evaluate("2 + 3 * 4", Table()));

    [Fact]
    public void TagArithmetic() =>
        Assert.Equal(25.0, Evaluate("(Tank1.Level / Tank1.Volume) * 100", Table()));

    [Fact]
    public void ComparisonAndLogic() =>
        Assert.Equal(1.0, Evaluate("Boiler1.Temp > 80 && Pump1.Running", Table()));

    [Fact]
    public void Ternary() =>
        Assert.Equal(20.0, Evaluate("Boiler1.Temp > 200 ? 10 : 20", Table()));

    [Fact]
    public void BuiltinWithTagRefArg() =>
        Assert.Equal(1.0, Evaluate("IsGood(Boiler1.Temp)", Table()));

    [Fact]
    public void ValueOr_OnBadQuality_ReturnsDefault()
    {
        var table = Table();
        table.Write(new TagId(0), new TagValue(0, 2000, Quality.Bad)); // связь оборвалась

        Assert.Equal(-1.0, Evaluate("ValueOr(Boiler1.Temp, -1)", table));
    }

    [Fact]
    public void RealScadaExpression_BadQualityForcesFalse()
    {
        // IsGood(Temp) && Temp > 80: при обрыве связи — ложь, хотя значение «высокое»
        var table = Table();
        table.Write(new TagId(0), new TagValue(100.0, 2000, Quality.Bad));

        Assert.Equal(0.0, Evaluate("IsGood(Boiler1.Temp) && Boiler1.Temp > 80", table));
    }

    [Fact]
    public void NestedBuiltins() =>
        Assert.Equal(100.0, Evaluate("Clamp(Boiler1.Temp * 2, 0, 100)", Table()));

    [Fact]
    public void ConstantPool_Deduplicates()
    {
        var compiled = ExpressionCompiler.Compile("Boiler1.Temp + 80 + 80 + 80", Catalog());

        // 80 встречается трижды — один слот в пуле, других констант нет
        Assert.Single(compiled.Constants);
        Assert.Contains(80.0, compiled.Constants);
    }

    [Fact]
    public void TagIndices_CollectedForEpochRecalc()
    {
        var compiled = ExpressionCompiler.Compile("Boiler1.Temp > 80 && Pump1.Running", Catalog());

        Assert.Equal([0, 1], compiled.TagIndices.Order().ToArray());
    }

    [Fact]
    public void UnknownTag_IsCompileTimeError()
    {
        // несуществующий тег — ошибка сборки, а не отказ на объекте (§11.6)
        Assert.Throws<ExpressionCompileException>(
            () => ExpressionCompiler.Compile("Boiler1.Tmp > 80", Catalog()));
    }
}
