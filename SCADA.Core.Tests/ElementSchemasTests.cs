using SCADA.Core.Schemes;

namespace SCADA.Core.Tests;

/// <summary>Целостность реестра схем свойств: id стабильны и уникальны,
/// умолчания соответствуют типу — иначе бинарные секции разъедутся с кодом.</summary>
public class ElementSchemasTests
{
    [Fact]
    public void EveryElementKind_IsRegistered()
    {
        foreach (ElementKind kind in Enum.GetValues<ElementKind>())
            Assert.NotNull(ElementSchemas.For(kind));
    }

    [Fact]
    public void PropertyIds_AreGloballyUnique()
    {
        // дескрипторы общих свойств (Base/Shape) входят в схемы нескольких
        // видов — это один и тот же def. Коллизия — это один id у РАЗНЫХ def.
        var collisions = ElementSchemas.Kinds
            .SelectMany(ElementSchemas.For)
            .GroupBy(d => d.Id)
            .Where(g => g.Select(d => d.Name).Distinct().Count() > 1)
            .ToList();

        Assert.Empty(collisions);
    }

    [Fact]
    public void Defaults_MatchDeclaredType()
    {
        foreach (var def in ElementSchemas.Kinds.SelectMany(ElementSchemas.For))
            Assert.Equal(def.Type, def.Default.Type);
    }

    [Fact]
    public void ChoiceProperties_HaveChoices()
    {
        foreach (var def in ElementSchemas.Kinds.SelectMany(ElementSchemas.For))
            if (def.Type == PropertyType.Choice)
                Assert.NotNull(def.Choices);
    }

    [Fact]
    public void ValidateBinding_RejectsUnknownAndNonAnimatable()
    {
        Assert.NotNull(ElementSchemas.ValidateBinding(ElementKind.Rectangle, 999));

        // TextFormat существует, но неанимируем
        Assert.NotNull(ElementSchemas.ValidateBinding(ElementKind.Rectangle, 8));

        Assert.Null(ElementSchemas.ValidateBinding(ElementKind.Rectangle, 10)); // FillColor
    }
}
