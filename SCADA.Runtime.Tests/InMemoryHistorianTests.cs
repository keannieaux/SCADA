using SCADA.Core.Tags;
using SCADA.Runtime.Historian;

namespace SCADA.Runtime.Tests;

public class InMemoryHistorianTests
{
    private static TagValue Value(double v, long timestamp) => new(v, timestamp, Quality.Good);

    [Fact]
    public void Append_ThenRead_ReturnsWrittenValues()
    {
        var historian = new InMemoryHistorian(tagCapacity: 10);
        historian.Append(new TagId(0), Value(1.0, 1000));
        historian.Append(new TagId(0), Value(2.0, 2000));

        Span<TagValue> destination = new TagValue[10];
        int count = historian.Read(new TagId(0), 0, 5000, destination);

        Assert.Equal(2, count);
        Assert.Equal(1.0, destination[0].Value);
        Assert.Equal(2.0, destination[1].Value);
    }

    [Fact]
    public void RingOverflow_KeepsLatestValues_InChronologicalOrder()
    {
        var historian = new InMemoryHistorian(tagCapacity: 10, capacityPerTag: 3);
        for (int i = 0; i < 5; i++)
            historian.Append(new TagId(0), Value(i, 1000 * (i + 1)));

        Span<TagValue> destination = new TagValue[10];
        int count = historian.Read(new TagId(0), 0, long.MaxValue, destination);

        // из пяти записанных живы последние три: 2, 3, 4
        Assert.Equal(3, count);
        Assert.Equal(2.0, destination[0].Value);
        Assert.Equal(3.0, destination[1].Value);
        Assert.Equal(4.0, destination[2].Value);
    }

    [Fact]
    public void Read_FiltersByTimeRange()
    {
        var historian = new InMemoryHistorian(tagCapacity: 10);
        historian.Append(new TagId(0), Value(1.0, 1000));
        historian.Append(new TagId(0), Value(2.0, 2000));
        historian.Append(new TagId(0), Value(3.0, 3000));
        historian.Append(new TagId(0), Value(4.0, 4000));

        Span<TagValue> destination = new TagValue[10];
        int count = historian.Read(new TagId(0), 2000, 3000, destination);

        Assert.Equal(2, count);
        Assert.Equal(2.0, destination[0].Value);
        Assert.Equal(3.0, destination[1].Value);
    }

    [Fact]
    public void Read_DestinationSmallerThanData_ReturnsLatest()
    {
        var historian = new InMemoryHistorian(tagCapacity: 10);
        for (int i = 0; i < 10; i++)
            historian.Append(new TagId(0), Value(i, 1000 * (i + 1)));

        Span<TagValue> destination = new TagValue[4];
        int count = historian.Read(new TagId(0), 0, long.MaxValue, destination);

        // влезают только 4 — отдаём самые поздние
        Assert.Equal(4, count);
        Assert.Equal(6.0, destination[0].Value);
        Assert.Equal(9.0, destination[3].Value);
    }

    [Fact]
    public void Read_TagWithoutData_ReturnsZero()
    {
        var historian = new InMemoryHistorian(tagCapacity: 10);

        Span<TagValue> destination = new TagValue[10];
        int count = historian.Read(new TagId(5), 0, long.MaxValue, destination);

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Feeder_TagTableChanges_AppearInHistorian()
    {
        var table = new TagTable.TagTable(capacity: 10);
        var historian = new InMemoryHistorian(tagCapacity: 10,
            tagTable: table, feedPeriod: TimeSpan.FromMilliseconds(20));

        await historian.StartAsync();
        table.Write(new TagId(0), Value(11.5, 1000));
        table.Write(new TagId(1), Value(22.5, 2000));
        await Task.Delay(150);
        await historian.StopAsync();

        Span<TagValue> destination = new TagValue[10];

        int count0 = historian.Read(new TagId(0), 0, long.MaxValue, destination);
        Assert.True(count0 >= 1);
        Assert.Equal(11.5, destination[0].Value);

        int count1 = historian.Read(new TagId(1), 0, long.MaxValue, destination);
        Assert.True(count1 >= 1);
        Assert.Equal(22.5, destination[0].Value);
    }

    [Fact]
    public async Task StartAsync_WithoutTagTable_Throws()
    {
        var historian = new InMemoryHistorian(tagCapacity: 10);

        await Assert.ThrowsAsync<InvalidOperationException>(() => historian.StartAsync());
    }
}
