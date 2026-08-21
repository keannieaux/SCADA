using System.Reflection;
using SCADA.Core.Schemes;
using SCADA.Graphics;

namespace SCADA.Architecture.Tests;

/// <summary>
/// `ElementSchemas` объявлен единственным источником истины по id свойств
/// (концепт §3.2, id стабильны и пишутся в пакет). `SCADA.Graphics.SchemeProperty`
/// держит их копию — иначе код рендера был бы усыпан магическими числами.
/// Копия и оригинал обязаны совпадать: если id разъедутся, графика молча
/// привяжется не к тому свойству, и это не поймает ни сборка, ни рантайм.
/// </summary>
public class SchemePropertyIdsTests
{
    public static TheoryData<string, int> GraphicsConstants()
    {
        var data = new TheoryData<string, int>();
        foreach (var field in typeof(SchemeProperty)
                     .GetFields(BindingFlags.Public | BindingFlags.Static)
                     .Where(f => f.IsLiteral && f.FieldType == typeof(int)))
        {
            data.Add(field.Name, (int)field.GetRawConstantValue()!);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(GraphicsConstants))]
    public void GraphicsConstant_MatchesCoreRegistry(string name, int id)
    {
        var def = AllCoreProperties().FirstOrDefault(d => d.Id == id);

        Assert.True(def is not null,
            $"SchemeProperty.{name} = {id}: такого id нет в ElementSchemas.");
        Assert.True(def!.Name == name,
            $"SchemeProperty.{name} = {id}, а в ElementSchemas под этим id — '{def.Name}'.");
    }

    /// <summary>Union по видам корректен только если один id везде значит одно
    /// и то же: базовые свойства входят в несколько видов одним дескриптором.</summary>
    [Fact]
    public void CoreRegistry_UsesEachIdForExactlyOneName()
    {
        var collisions = AllCoreProperties()
            .GroupBy(d => d.Id)
            .Where(g => g.Select(d => d.Name).Distinct().Count() > 1)
            .Select(g => $"id {g.Key}: {string.Join(", ", g.Select(d => d.Name).Distinct())}")
            .ToList();

        Assert.True(collisions.Count == 0,
            "один id под разными именами: " + string.Join("; ", collisions));
    }

    private static IEnumerable<PropertyDef> AllCoreProperties()
        => ElementSchemas.Kinds
            .SelectMany(ElementSchemas.For)
            .Concat(ElementSchemas.SchemeProperties);
}
