using SCADA.Core.Tags;
using SCADA.Runtime.Runtime;
using TagTableImpl = SCADA.Runtime.TagTable.TagTable;

namespace SCADA.Runtime.Tests;

/// <summary>
/// Контракт UI ↔ ядро для строковых тегов (концепт §4.6): клиент рантайма
/// пишет и читает строки, пакетное чтение работает, до первой записи текст
/// пуст с качеством Uncertain.
/// </summary>
public class LocalRuntimeClientStringTests
{
    [Fact]
    public void WriteLocalString_ReadString_RoundTrips()
    {
        var table = new TagTableImpl(capacity: 4);
        var client = new LocalRuntimeClient(table);

        client.WriteLocalString(new TagId(1), "Насос работает");

        StringTagValue result = client.ReadString(new TagId(1));
        Assert.Equal("Насос работает", result.Text);
        Assert.Equal(Quality.Good, result.Quality);
        Assert.True(result.TimeStampUtc > 0);
    }

    [Fact]
    public void ReadString_BeforeAnyWrite_ReturnsUncertainEmpty()
    {
        var table = new TagTableImpl(capacity: 4);
        var client = new LocalRuntimeClient(table);

        StringTagValue result = client.ReadString(new TagId(2));

        Assert.Equal("", result.Text);
        Assert.Equal(Quality.Uncertain, result.Quality);
    }

    [Fact]
    public void ReadStrings_ReadsBatchInOrder()
    {
        var table = new TagTableImpl(capacity: 8);
        var client = new LocalRuntimeClient(table);
        client.WriteLocalString(new TagId(1), "один");
        client.WriteLocalString(new TagId(5), "пять");

        TagId[] ids = [new TagId(1), new TagId(3), new TagId(5)];
        var results = new StringTagValue[3];
        client.ReadStrings(ids, results);

        Assert.Equal("один", results[0].Text);
        Assert.Equal("", results[1].Text); // нетронутый — пуст
        Assert.Equal("пять", results[2].Text);
    }
}
