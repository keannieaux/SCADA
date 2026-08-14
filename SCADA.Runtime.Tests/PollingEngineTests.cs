using SCADA.Core.Channels;
using SCADA.Core.Devices;
using SCADA.Core.Tags;
using SCADA.Drivers.Abstractions;
using SCADA.Runtime.Configuration;
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

    // Управляемый из теста драйвер: «связь» рвётся и восстанавливается флагом.
    private sealed class FlakyGate
    {
        public volatile bool IsUp;
    }

    private sealed class FlakyDriver(FlakyGate gate) : IDeviceDriver
    {
        public string ProtocolName => "flaky";

        public Task ConnectAsync(DeviceDefinition device, IReadOnlyList<TagDefinition> tags, CancellationToken ct)
            => gate.IsUp ? Task.CompletedTask : throw new IOException("нет связи");

        public ValueTask<bool> PollAsync(Memory<TagValue> results, CancellationToken ct)
        {
            if (!gate.IsUp)
                throw new IOException("обрыв связи");
            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            for (int i = 0; i < results.Length; i++)
                results.Span[i] = new TagValue(7.5, timestamp, Quality.Good);
            return ValueTask.FromResult(true);
        }

        public Task DisconnectAsync() => Task.CompletedTask;
    }

    private static ProjectConfiguration CreateFlakyConfig() => new()
    {
        Name = "Test",
        Channels = [new ChannelDefinition { Id = new ChannelId(0), Name = "C", ChannelType = "none" }],
        Devices =
        [
            // оба устройства на ОДНОМ канале — проверяем, что сбой одного не влияет на другое
            new DeviceDefinition { Id = new DeviceId(0), Name = "Flaky", DriverName = "flaky", ChannelId = new ChannelId(0) },
            new DeviceDefinition { Id = new DeviceId(1), Name = "SimPLC", DriverName = "simulator", ChannelId = new ChannelId(0) }
        ],
        Tags =
        [
            new TagDefinition { Id = new TagId(0), Name = "FlakyTag", DataType = TagDataType.Analog, DeviceId = new DeviceId(0), Address = "x" },
            new TagDefinition { Id = new TagId(1), Name = "SimTag", DataType = TagDataType.Analog, DeviceId = new DeviceId(1), Address = "const:1" }
        ]
    };

    [Fact]
    public async Task Reconnect_AfterOutage_RestoresGoodQuality_AndKeepsLastValue()
    {
        var gate = new FlakyGate { IsUp = true };
        DriverFactory.Register("flaky", () => new FlakyDriver(gate));

        var table = new TagTable.TagTable(capacity: 10);
        var engine = new PollingEngine(CreateFlakyConfig(), table, TimeSpan.FromMilliseconds(20));
        await engine.StartAsync();
        try
        {
            await WaitForAsync(() => table.Read(new TagId(0)).Quality == Quality.Good, timeoutMs: 2000);

            gate.IsUp = false; // обрыв
            await WaitForAsync(() => table.Read(new TagId(0)).Quality == Quality.Bad, timeoutMs: 2000);
            var duringOutage = table.Read(new TagId(0));
            Assert.Equal(7.5, duringOutage.Value); // значение не зануляется (§4.2)

            // соседнее устройство на том же канале продолжает опрашиваться
            await WaitForAsync(() => table.Read(new TagId(1)).Quality == Quality.Good, timeoutMs: 2000);

            gate.IsUp = true; // восстановление — первая попытка reconnect через ~1с
            await WaitForAsync(() => table.Read(new TagId(0)).Quality == Quality.Good, timeoutMs: 5000);
            Assert.Equal(Quality.Good, table.Read(new TagId(0)).Quality);
        }
        finally
        {
            await engine.StopAsync();
        }
    }

    [Fact]
    public async Task ConnectFailure_AtStartup_DoesNotKillChannel_AndRecovers()
    {
        var gate = new FlakyGate { IsUp = false }; // устройство недоступно с самого старта
        DriverFactory.Register("flaky", () => new FlakyDriver(gate));

        var table = new TagTable.TagTable(capacity: 10);

        // Быстрый backoff: с боевым (1с → 30с) к моменту восстановления связи
        // накапливается 3–4 отказа, и следующая попытка отстоит на 8–16 секунд.
        // Тест либо ждал бы полминуты, либо падал под нагрузкой — что и делал.
        var backoff = new ReconnectBackoff
        {
            BaseDelay = TimeSpan.FromMilliseconds(20),
            MaxDelay = TimeSpan.FromMilliseconds(100)
        };

        var engine = new PollingEngine(CreateFlakyConfig(), table,
            TimeSpan.FromMilliseconds(20), backoff);
        await engine.StartAsync();
        try
        {
            // ждём, пока движок действительно попытается подключиться и пометит тег Bad
            await WaitForAsync(() => table.Read(new TagId(0)).Quality == Quality.Bad, timeoutMs: 5000);
            // соседнее устройство на том же канале должно быть живо
            await WaitForAsync(() => table.Read(new TagId(1)).Quality == Quality.Good, timeoutMs: 5000);

            gate.IsUp = true;
            await WaitForAsync(() => table.Read(new TagId(0)).Quality == Quality.Good, timeoutMs: 5000);
            Assert.Equal(Quality.Good, table.Read(new TagId(0)).Quality);  // подключился сам
        }
        finally
        {
            await engine.StopAsync();
        }
    }

    [Fact]
    public async Task ChannelDiagnostics_FlushesMetricsToSystemTags()
    {
        var gate = new FlakyGate { IsUp = true };
        DriverFactory.Register("flaky", () => new FlakyDriver(gate));

        var config = CreateFlakyConfig();
        DiagnosticsGenerator.AppendDiagnostics(config);
        // после AppendDiagnostics: id 2=@C.Connected, 4=@C.RequestsOk,
        // 5=@C.RequestsFailed, 6=@C.ReconnectCount (порядок метрик генератора)

        var table = new TagTable.TagTable(capacity: config.Tags.Count);
        var engine = new PollingEngine(config, table, TimeSpan.FromMilliseconds(20));
        await engine.StartAsync();
        try
        {
            await WaitForAsync(() => table.Read(new TagId(2)).Value == 1.0, timeoutMs: 3000);
            Assert.True(table.Read(new TagId(4)).Value > 0);     // RequestsOk

            gate.IsUp = false; // обрыв
            await WaitForAsync(() => table.Read(new TagId(2)).Value == 0.0, timeoutMs: 3000);
            Assert.True(table.Read(new TagId(5)).Value > 0);     // RequestsFailed

            gate.IsUp = true; // восстановление: backoff ~1с + flush
            await WaitForAsync(() => table.Read(new TagId(2)).Value == 1.0, timeoutMs: 5000);
            Assert.True(table.Read(new TagId(6)).Value >= 1);    // ReconnectCount
        }
        finally
        {
            await engine.StopAsync();
        }
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs, int intervalMs = 50)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow > deadline)
                throw new TimeoutException("Условие не выполнилось в отведённый таймаут");

            await Task.Delay(intervalMs);
        }
    }
}
