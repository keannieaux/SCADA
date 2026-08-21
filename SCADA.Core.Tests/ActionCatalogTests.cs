using SCADA.Core.Schemes;

namespace SCADA.Core.Tests;

/// <summary>
/// Целостность каталога действий (docs/scheme-controls-plan.md, C1).
/// Главная защита: каждый наследник SchemeAction обязан быть в каталоге —
/// иначе действие однажды тихо пропадёт при чтении секции (писатель бросит,
/// читатель пропустит).
/// </summary>
public class ActionCatalogTests
{
    [Fact]
    public void EveryConcreteAction_IsRegistered()
    {
        var concrete = typeof(SchemeAction).Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false } && t.IsSubclassOf(typeof(SchemeAction)))
            .ToArray();

        Assert.NotEmpty(concrete); // рефлексия не «съела» сборку
        foreach (var type in concrete)
            Assert.True(ActionCatalog.Find(type) is not null,
                $"Действие {type.Name} не зарегистрировано в ActionCatalog");
    }

    [Fact]
    public void TypeCodes_AreUnique()
    {
        var duplicates = ActionCatalog.Actions.GroupBy(d => d.TypeCode)
            .Where(g => g.Count() > 1).ToArray();

        Assert.Empty(duplicates);
    }

    [Fact]
    public void JsonNames_AreUnique()
    {
        var duplicates = ActionCatalog.Actions.GroupBy(d => d.JsonName)
            .Where(g => g.Count() > 1).ToArray();

        Assert.Empty(duplicates);
    }

    [Fact]
    public void DisplayNames_AreFilled()
    {
        foreach (var def in ActionCatalog.Actions)
        {
            Assert.False(string.IsNullOrWhiteSpace(def.DisplayName),
                $"У действия {def.ClrType.Name} пустое отображаемое имя");
            foreach (var param in def.Params)
                Assert.False(string.IsNullOrWhiteSpace(param.DisplayName),
                    $"У параметра {param.Name} действия {def.ClrType.Name} пустое имя");
        }
    }

    [Fact]
    public void TypeCodeFor_RegisteredAction_ReturnsCode()
    {
        Assert.Equal((byte)0, ActionCatalog.TypeCodeFor(
            new WriteTagAction(new SchemeTagRef("T1", false), 1.0)));
        Assert.Equal((byte)4, ActionCatalog.TypeCodeFor(new ClosePopupAction()));
    }

    [Fact]
    public void TypeCodeFor_UnregisteredAction_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ActionCatalog.TypeCodeFor(new CustomAction()));
    }

    [Fact]
    public void IsKnown_CoversExactlyRegisteredCodes()
    {
        // конкретные коды не зашиваем: каталог растёт, а проверяем мы то,
        // что IsKnown отвечает ровно по нему — иначе тест ломается на каждом
        // новом действии, ничего при этом не поймав
        foreach (var def in ActionCatalog.Actions)
            Assert.True(ActionCatalog.IsKnown(def.TypeCode));

        byte free = (byte)(ActionCatalog.Actions.Max(d => d.TypeCode) + 1);
        Assert.False(ActionCatalog.IsKnown(free));
        Assert.False(ActionCatalog.IsKnown(255));
    }

    [Fact]
    public void TagRefsOf_ExtractsTagParams()
    {
        var write = new WriteTagAction(new SchemeTagRef("Pump5.Run", false), 1.0);

        var refs = ActionCatalog.TagRefsOf(write).ToArray();

        Assert.Single(refs);
        Assert.Equal("Pump5.Run", refs[0].Name);

        // действие без TagRef-параметров — пусто, без исключений
        Assert.Empty(ActionCatalog.TagRefsOf(new ClosePopupAction()));
        Assert.Empty(ActionCatalog.TagRefsOf(
            new ShowDialogAction("текст")));
    }

    // Наследник вне каталога — имитация забывчивости разработчика
    private sealed record CustomAction : SchemeAction;
}
