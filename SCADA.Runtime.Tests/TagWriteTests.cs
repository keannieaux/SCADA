using SCADA.Core.Channels;
using SCADA.Core.Devices;
using SCADA.Core.Tags;
using SCADA.Drivers.Abstractions;
using SCADA.Runtime.Audit;
using SCADA.Runtime.Polling;
using SCADA.Runtime.TagTable;

namespace SCADA.Runtime.Tests;

/// <summary>
/// Запись в теги (M7): маршалинг в цикл канала, валидация пакета до
/// исполнения, internal-теги напрямую, персистентность, аудит.
/// </summary>
public class TagWriteTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

    public TagWriteTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // конфиг: simulator-устройство с writable-тегом уставки и read-only тегом,
    // internal-устройство с персистентным тегом
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
            new TagDefinition { Id = new TagId(0), Name = "Setpoint", DataType = TagDataType.Analog,
                DeviceId = new DeviceId(0), Address = "const:7.5", IsWritable = true },
            new TagDefinition { Id = new TagId(1), Name = "Temperature", DataType = TagDataType.Analog,
                DeviceId = new DeviceId(0), Address = "sin:10" },
            new TagDefinition { Id = new TagId(2), Name = "LocalMode", DataType = TagDataType.Analog,
                DeviceId = new DeviceId(1), InitValue = 42, IsWritable = true, IsPersistent = true }
        ]
    };

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 10_000, int intervalMs = 20)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow > deadline)
                throw new TimeoutException("Условие не выполнилось в отведённый таймаут");
            await Task.Delay(intervalMs);
        }
    }

    private sealed class CollectingAuditJournal : IAuditJournal
    {
        public List<AuditEntry> Entries { get; } = [];
        public void Append(IReadOnlyList<AuditEntry> entries) => Entries.AddRange(entries);
    }

    [Fact]
    public async Task Write_SimulatorTag_ValueVisibleAfterPoll()
    {
        var table = new TagTable.TagTable(capacity: 10);
        var engine = new PollingEngine(CreateConfig(), table, TimeSpan.FromMilliseconds(20));
        await engine.StartAsync();
        try
        {
            var result = await engine.WriteTagAsync(new TagId(0), 99.0, "tester");

            Assert.Equal(TagWriteStatus.Ok, result.Status);
            // override держится, ближайший опрос приносит записанное значение
            await WaitForAsync(() => table.Read(new TagId(0)).Value == 99.0);
        }
        finally
        {
            await engine.StopAsync();
        }
    }

    [Fact]
    public async Task Write_NotWritableTag_Rejected()
    {
        var table = new TagTable.TagTable(capacity: 10);
        var engine = new PollingEngine(CreateConfig(), table, TimeSpan.FromMilliseconds(20));
        await engine.StartAsync();
        try
        {
            var result = await engine.WriteTagAsync(new TagId(1), 5.0, "tester");

            Assert.Equal(TagWriteStatus.NotWritable, result.Status);
        }
        finally
        {
            await engine.StopAsync();
        }
    }

    [Fact]
    public async Task Write_UnknownTag_Rejected()
    {
        var table = new TagTable.TagTable(capacity: 10);
        var engine = new PollingEngine(CreateConfig(), table, TimeSpan.FromMilliseconds(20));
        var result = await engine.WriteTagAsync(new TagId(999), 5.0, "tester");
        await engine.StopAsync();

        Assert.Equal(TagWriteStatus.NotWritable, result.Status);
    }

    [Fact]
    public async Task Batch_WithInvalidItem_RejectedEntirely()
    {
        var table = new TagTable.TagTable(capacity: 10);
        var engine = new PollingEngine(CreateConfig(), table, TimeSpan.FromMilliseconds(20));
        await engine.StartAsync();
        try
        {
            var results = await engine.WriteTagsAsync(
                [new TagWriteItem(new TagId(0), 99.0),   // валидный
                 new TagWriteItem(new TagId(1), 5.0)],   // не writable
                "tester");

            Assert.All(results, r => Assert.NotEqual(TagWriteStatus.Ok, r.Status));

            // валидный элемент НЕ исполнен: частично применённый пакет хуже отказа
            await Task.Delay(150);
            Assert.NotEqual(99.0, table.Read(new TagId(0)).Value);
        }
        finally
        {
            await engine.StopAsync();
        }
    }

    [Fact]
    public async Task Write_OfflineDevice_DeviceOffline()
    {
        DriverFactory.Register("offline", () => new OfflineDriver());
        var config = new ProjectConfiguration
        {
            Name = "Test",
            Channels = [new ChannelDefinition { Id = new ChannelId(0), Name = "C", ChannelType = "none" }],
            Devices = [new DeviceDefinition { Id = new DeviceId(0), Name = "Dead", DriverName = "offline", ChannelId = new ChannelId(0) }],
            Tags = [new TagDefinition { Id = new TagId(0), Name = "Cmd", DataType = TagDataType.Analog,
                DeviceId = new DeviceId(0), IsWritable = true }]
        };

        var table = new TagTable.TagTable(capacity: 10);
        var engine = new PollingEngine(config, table, TimeSpan.FromMilliseconds(20));
        await engine.StartAsync();
        try
        {
            var result = await engine.WriteTagAsync(new TagId(0), 1.0, "tester");

            // команда не ждёт переподключения — отказ сразу
            Assert.Equal(TagWriteStatus.DeviceOffline, result.Status);
        }
        finally
        {
            await engine.StopAsync();
        }
    }

    [Fact]
    public async Task Write_AppliesReverseScaling()
    {
        // ScaleFactor=2, Offset=-5: инженерные 10 → сырое 7.5 → опрос вернёт 10
        var config = CreateConfig();
        config.Tags[0].ScaleFactor = 2.0;
        config.Tags[0].ScaleOffset = -5.0;

        var table = new TagTable.TagTable(capacity: 10);
        var engine = new PollingEngine(config, table, TimeSpan.FromMilliseconds(20));
        await engine.StartAsync();
        try
        {
            var result = await engine.WriteTagAsync(new TagId(0), 10.0, "tester");

            Assert.Equal(TagWriteStatus.Ok, result.Status);
            await WaitForAsync(() => table.Read(new TagId(0)).Value == 10.0);
        }
        finally
        {
            await engine.StopAsync();
        }
    }

    [Fact]
    public async Task Write_InternalTag_DirectAndAudited()
    {
        var audit = new CollectingAuditJournal();
        var table = new TagTable.TagTable(capacity: 10);
        var engine = new PollingEngine(CreateConfig(), table, TimeSpan.FromMilliseconds(20),
            audit: audit);
        await engine.StartAsync();
        try
        {
            var result = await engine.WriteTagAsync(new TagId(2), 7.0, "tester@station1");

            Assert.Equal(TagWriteStatus.Ok, result.Status);
            Assert.Equal(7.0, table.Read(new TagId(2)).Value); // сразу, без ожидания опроса

            var entry = Assert.Single(audit.Entries);
            Assert.Equal("tag-write", entry.Action);
            Assert.Equal("LocalMode", entry.Target);
            Assert.Equal("tester@station1", entry.User);
            Assert.Equal(42.0, entry.OldValue); // InitValue — до записи
            Assert.Equal(7.0, entry.NewValue);
            Assert.Equal("Ok", entry.Result);
            Assert.False(string.IsNullOrEmpty(entry.BatchId));
        }
        finally
        {
            await engine.StopAsync();
        }
    }

    [Fact]
    public async Task Persistent_InternalTag_RestoresAfterRestart()
    {
        string storePath = Path.Combine(_dir, "persistent-tags.json");

        // первый «запуск»: оператор меняет уставку 42 → 5
        var table1 = new TagTable.TagTable(capacity: 10);
        var engine1 = new PollingEngine(CreateConfig(), table1,
            TimeSpan.FromMilliseconds(20), persistence: new PersistentTagStore(storePath));
        await engine1.StartAsync();
        var result = await engine1.WriteTagAsync(new TagId(2), 5.0, "tester");
        await engine1.StopAsync();
        Assert.Equal(TagWriteStatus.Ok, result.Status);

        // второй «запуск»: персистентное значение перекрывает InitValue
        var table2 = new TagTable.TagTable(capacity: 10);
        var engine2 = new PollingEngine(CreateConfig(), table2,
            TimeSpan.FromMilliseconds(20), persistence: new PersistentTagStore(storePath));
        await engine2.StartAsync();
        try
        {
            Assert.Equal(5.0, table2.Read(new TagId(2)).Value);
        }
        finally
        {
            await engine2.StopAsync();
        }
    }

    [Fact]
    public async Task Batch_SpansChannels_ResultsInOrder()
    {
        // тег 0 — simulator (канал 0), тег 2 — internal: один пакет, два пути
        var table = new TagTable.TagTable(capacity: 10);
        var engine = new PollingEngine(CreateConfig(), table, TimeSpan.FromMilliseconds(20));
        await engine.StartAsync();
        try
        {
            var results = await engine.WriteTagsAsync(
                [new TagWriteItem(new TagId(2), 3.0),   // internal
                 new TagWriteItem(new TagId(0), 55.0)], // сетевой
                "tester");

            Assert.Equal(TagWriteStatus.Ok, results[0].Status);
            Assert.Equal(TagWriteStatus.Ok, results[1].Status);
            Assert.Equal(3.0, table.Read(new TagId(2)).Value);
            await WaitForAsync(() => table.Read(new TagId(0)).Value == 55.0);
        }
        finally
        {
            await engine.StopAsync();
        }
    }

    // устройство, которое никогда не подключается
    private sealed class OfflineDriver : IDeviceDriver
    {
        public string ProtocolName => "offline";
        public Task ConnectAsync(DeviceDefinition device, IReadOnlyList<TagDefinition> tags, CancellationToken ct)
            => throw new IOException("нет связи");
        public ValueTask<bool> PollAsync(Memory<TagValue> results, CancellationToken ct)
            => throw new IOException("нет связи");
        public Task DisconnectAsync() => Task.CompletedTask;
    }
}
