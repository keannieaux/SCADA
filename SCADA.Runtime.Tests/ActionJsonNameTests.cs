using System.Text.Json.Serialization;
using SCADA.Core.Schemes;
using SCADA.Runtime.Configuration;

namespace SCADA.Runtime.Tests;

/// <summary>
/// Согласованность JSON-дискриминаторов исходников схем (SchemeFiles.cs)
/// с каталогом действий (C1): DTO и каталог — два представления одного
/// списка, и они не должны разъехаться. Проверяется по атрибутам
/// JsonDerivedType на базовом SchemeActionDto.
/// </summary>
public class ActionJsonNameTests
{
    private static Dictionary<Type, string> Discriminators()
        => typeof(SchemeActionDto)
            .GetCustomAttributes(typeof(JsonDerivedTypeAttribute), inherit: false)
            .Cast<JsonDerivedTypeAttribute>()
            .ToDictionary(a => a.DerivedType, a => (string)a.TypeDiscriminator!);

    [Fact]
    public void EveryDtoDiscriminator_ExistsInCatalog()
    {
        foreach (var (dtoType, discriminator) in Discriminators())
            Assert.True(ActionCatalog.FindByJsonName(discriminator) is not null,
                $"DTO {dtoType.Name}: дискриминатор \"{discriminator}\" " +
                "не найден в ActionCatalog");
    }

    [Fact]
    public void EveryCatalogEntry_HasDto()
    {
        var dtoNames = Discriminators().Values.ToHashSet(StringComparer.Ordinal);

        foreach (var def in ActionCatalog.Actions)
            Assert.True(dtoNames.Contains(def.JsonName),
                $"Действие {def.ClrType.Name}: в каталоге JsonName \"{def.JsonName}\", " +
                "но DTO с таким дискриминатором нет в SchemeFiles.cs");
    }

    [Fact]
    public void DtoNaming_MatchesActionNaming()
    {
        // "WriteTagActionDto" ↔ дискриминатор "WriteTag" ↔ CLR "WriteTagAction":
        // SchemeFileLoader.MapAction режет суффикс "Dto" для сообщений об ошибках,
        // а каталог ссылается на CLR-тип — имена обязаны совпадать
        foreach (var (dtoType, discriminator) in Discriminators())
        {
            var def = ActionCatalog.FindByJsonName(discriminator)!;
            Assert.Equal(def.ClrType.Name + "Dto", dtoType.Name);
        }
    }
}
