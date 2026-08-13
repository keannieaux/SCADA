using SCADA.Core.Channels;
using SCADA.Core.Devices;
using SCADA.Core.Tags;
using SCADA.Runtime.Polling;
using SCADA.Runtime.TagTable;

namespace SCADA.Runtime.Tests;

public class PollingEngineTests
{
    // конфиг в коде, без JSON: один simulator-канал с двумя тегами
    // и один internal-канал с тегом уставки
    private static ProjectConfiguration CreateConfig() => new()
    {
        Name = "Test",
        Channels =
        [
            new ChannelDefinition { Id = new ChannelId(0), Name = "SimChannel", ChannelType = "none" },
            new ChannelDefinition { Id = new ChannelId(1), Name = "LocalChannel", ChannelType = "none" }
        ],
        Devices =
        [
            new DeviceDefinition { Id = new DeviceId(0), Name = "SimPLC", DriverName = "simulator", ChannelId = new ChannelId(0) },
            new DeviceDefinition { Id = new DeviceId(1), Name = "Local", DriverName = "internal", ChannelId = new ChannelId(1) }
        ],
        Tags =
        [
            new TagDefinition { Id = new TagId(0), Name = "Setpoint", DataType = TagDataType.Analog, DeviceId = new DeviceId(0), Address = "const:7.5" },
            new TagDefinition { Id = new TagId(1), Name = "Temperature", DataType = TagDataType.Analog, DeviceId = new DeviceId(0), Address = "sin:10" },
            new TagDefinition { Id = new TagId(2), Name = "LocalMode", DataType = TagDataType.Analog, DeviceId = new DeviceId(1), InitValue = 42 }
        ]
    };

    private static async Task<PollingEngine> RunFor(ProjectConfiguration config, TagTable.TagTable table, int milliseconds)
    {
        var engine = new PollingEngine(config, table, TimeSpan.FromMilliseconds(20));
        await engine.StartAsync();
        await Task.Delay(milliseconds);
        await engine.StopAsync();
        return engine;
    }

    [Fact]
    public async Task PollsSimulatorDevice_WritesValuesToTagTable()
    {
        var table = new TagTable.TagTable(capacity: 10);

        await RunFor(CreateConfig(), table, milliseconds: 150);

        // const:7.5 — детерминированное значение, проверяем точно
        TagValue setpoint = table.Read(new TagId(0));
        Assert.Equal(7.5, setpoint.Value);
        Assert.Equal(Quality.Good, setpoint.Quality);
        Assert.True(setpoint.TimeStampUtc > 0);

        // sin:10 — проверяем только, что живое и в диапазоне
        TagValue temperature = table.Read(new TagId(1));
        Assert.Equal(Quality.Good, temperature.Quality);
        Assert.InRange(temperature.Value, -1.0, 1.0);
    }

    [Fact]
    public async Task InternalTag_GetsInitValueOnStart()
    {
        var table = new TagTable.TagTable(capacity: 10);

        await RunFor(CreateConfig(), table, milliseconds: 50);

        TagValue localMode = table.Read(new TagId(2));
        Assert.Equal(42, localMode.Value);
        Assert.Equal(Quality.Good, localMode.Quality);
    }

    [Fact]
    public async Task StopAsync_FreezesUpdates()
    {
        var table = new TagTable.TagTable(capacity: 10);

        await RunFor(CreateConfig(), table, milliseconds: 150);

        long epochAfterStop = table.CurrentEpoch;
        await Task.Delay(100);

        Assert.Equal(epochAfterStop, table.CurrentEpoch);
    }

    [Fact]
    public async Task Engine_UpdatesEpochWhileRunning()
    {
        var table = new TagTable.TagTable(capacity: 10);
        var engine = new PollingEngine(CreateConfig(), table, TimeSpan.FromMilliseconds(20));

        await engine.StartAsync();
        long epochAtStart = table.CurrentEpoch;
        await Task.Delay(150);
        await engine.StopAsync();

        Assert.True(table.CurrentEpoch > epochAtStart);
    }

    [Fact]
    public async Task Poll_AppliesScaleFromTagDefinition()
    {
        // const:7.5 с ScaleFactor=2 и ScaleOffset=-5 → 7.5*2-5 = 10
        var config = CreateConfig();
        config.Tags[0].ScaleFactor = 2.0;
        config.Tags[0].ScaleOffset = -5.0;

        var table = new TagTable.TagTable(capacity: 10);
        await RunFor(config, table, milliseconds: 100);

        Assert.Equal(10.0, table.Read(new TagId(0)).Value);
    }
}
