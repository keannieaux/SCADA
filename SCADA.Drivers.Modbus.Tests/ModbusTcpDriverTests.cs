using System.Net;
using FluentModbus;
using SCADA.Core.Channels;
using SCADA.Core.Devices;
using SCADA.Core.Tags;

namespace SCADA.Drivers.Modbus.Tests;

// интеграционный тест: виртуальный ПЛК (ModbusTcpServer) в процессе
public class ModbusTcpDriverTests : IDisposable
{
    private readonly ModbusTcpServer _server;

    public ModbusTcpDriverTests()
    {
        _server = new ModbusTcpServer(); // асинхронный режим: отвечает сам
        _server.Start(new IPEndPoint(IPAddress.Loopback, 5020));

        lock (_server.Lock)
        {
            var holding = _server.GetHoldingRegisters();
            holding.SetBigEndian<short>(100, 735);        // u16: 735
            holding.SetBigEndian<short>(105, -5);         // i16: -5
            holding.SetBigEndian<float>(110, 12.5f);      // f32: 12.5 (регистры 110-111)

            _server.GetCoils().Set(address: 3, value: true); // буфер упакованный: Set, не индекс
        }
    }

    public void Dispose() => _server.Stop();

    private static DeviceDefinition Device => new()
    {
        Id = new DeviceId(0),
        Name = "PLC",
        DriverName = "modbus-tcp",
        ChannelId = new ChannelId(0),
        Configuration = "127.0.0.1:5020;timeout=1000" // unit по умолчанию 0 — стандарт для прямого TCP
    };

    private static TagDefinition Tag(int id, string address) => new()
    {
        Id = new TagId(id), Name = $"T{id}", DataType = TagDataType.Analog,
        DeviceId = new DeviceId(0), Address = address
    };

    [Fact]
    public async Task Poll_ReadsRegistersAndBits_DecodesAndDistributes()
    {
        var tags = new[]
        {
            Tag(0, "hr:100"),        // 735
            Tag(1, "hr:105:i16"),    // -5
            Tag(2, "hr:110:f32"),    // 12.5
            Tag(3, "coil:3")         // 1
        };

        var driver = new ModbusTcpDriver();
        await driver.ConnectAsync(Device, tags, CancellationToken.None);

        var results = new TagValue[tags.Length];
        bool hasData = await driver.PollAsync(results, CancellationToken.None);

        Assert.True(hasData);
        Assert.Equal(735.0, results[0].Value);
        Assert.Equal(-5.0, results[1].Value);
        Assert.Equal(12.5, results[2].Value, precision: 4);
        Assert.Equal(1.0, results[3].Value);
        Assert.All(results, r => Assert.Equal(Quality.Good, r.Quality));

        await driver.DisconnectAsync();
    }

    [Fact]
    public async Task Poll_ServerDown_Throws_SoEngineMarksBad()
    {
        var driver = new ModbusTcpDriver();
        await driver.ConnectAsync(Device, [Tag(0, "hr:100")], CancellationToken.None);

        _server.Stop(); // обрыв связи

        await Assert.ThrowsAnyAsync<Exception>(
            () => driver.PollAsync(new TagValue[1], CancellationToken.None).AsTask());
    }
}
