namespace SCADA.Expressions.Compiler.Tests;

public class BinderTests
{
    // тестовый каталог: словарь имя → индекс
    private sealed class FakeCatalog : ITagCatalog
    {
        private readonly Dictionary<string, int> _tags = new(StringComparer.OrdinalIgnoreCase);
        public FakeCatalog Add(string name, int index) { _tags[name] = index; return this; }
        public bool TryGetIndex(string name, out int index) => _tags.TryGetValue(name, out index);
    }

    private static ITagCatalog Catalog() => new FakeCatalog()
        .Add("Boiler1.Temp", 5)
        .Add("Pump1.Running", 7)
        .Add("Tank1.Level", 9);

    private static BoundExpression Bind(string text, ITagCatalog catalog)
        => Binder.Bind(Parser.Parse(text), catalog);

    [Fact]
    public void Bind_TagRef_ResolvesToIndex()
    {
        var bound = Bind("Boiler1.Temp + 1", Catalog());

        var add = Assert.IsType<BoundBinary>(bound.Root);
        var tag = Assert.IsType<BoundTagValue>(add.Left);
        Assert.Equal(5, tag.TagIndex);
    }

    [Fact]
    public void Bind_UnknownTag_ThrowsWithPosition()
    {
        var ex = Assert.Throws<ExpressionCompileException>(
            () => Bind("Boiler1.Tmp > 80", Catalog()));

        var error = Assert.Single(ex.Errors);
        Assert.Contains("Boiler1.Tmp", error.Message);
        Assert.Equal(0, error.Position);
    }

    [Fact]
    public void Bind_UnknownFunction_Throws()
    {
        var ex = Assert.Throws<ExpressionCompileException>(
            () => Bind("Foo(Boiler1.Temp)", Catalog()));

        Assert.Contains("Foo", ex.Errors[0].Message);
    }

    [Fact]
    public void Bind_WrongArgCount_Throws()
    {
        var ex = Assert.Throws<ExpressionCompileException>(
            () => Bind("Clamp(Boiler1.Temp, 0)", Catalog()));

        Assert.Contains("3", ex.Errors[0].Message);
    }

    [Fact]
    public void Bind_TagRefArg_EmitsTagIndex_NotValue()
    {
        // IsGood(Boiler1.Temp): аргумент — ссылка на тег, эмитится индекс
        var bound = Bind("IsGood(Boiler1.Temp)", Catalog());

        var call = Assert.IsType<BoundCall>(bound.Root);
        Assert.Equal(BuiltinFunctions.IsGood, call.Function.Id);
        var tagIndex = Assert.IsType<BoundTagIndex>(call.Args[0]);
        Assert.Equal(5, tagIndex.TagIndex);
    }

    [Fact]
    public void Bind_TagRefArg_WithNumber_Throws()
    {
        // IsGood(5) — ссылка на тег обязана быть именем, не числом
        Assert.Throws<ExpressionCompileException>(() => Bind("IsGood(5)", Catalog()));
    }

    [Fact]
    public void Bind_CollectsTagIndices_WithoutDuplicates()
    {
        var bound = Bind("Boiler1.Temp > 80 && Pump1.Running || Boiler1.Temp < 10", Catalog());

        Assert.Equal([5, 7], bound.TagIndices.Order().ToArray());
    }

    [Fact]
    public void Bind_CollectsAllErrors_NotJustFirst()
    {
        // два несуществующих тега — обе ошибки в одном исключении
        var ex = Assert.Throws<ExpressionCompileException>(
            () => Bind("No1 + No2", Catalog()));

        Assert.Equal(2, ex.Errors.Count);
    }
}
