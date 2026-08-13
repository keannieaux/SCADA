using FluentModbus;
using SCADA.Core.Devices;
using SCADA.Core.Tags;
using SCADA.Drivers.Abstractions;

namespace SCADA.Drivers.Modbus;

/// <summary>
/// Драйвер Modbus TCP. При подключении парсит адреса тегов и группирует
/// их в блоки запросов (§7.3); в цикле опроса выполняет блоки и раскладывает
/// ответы по результатам. Значения СЫРЫЕ — масштаб применяет движок.
/// Ошибки связи выбрасываются наружу: движок пометит теги Quality.Bad
/// и продолжит работу; переподключение — задача следующего шага.
/// </summary>
public sealed class ModbusTcpDriver : IDeviceDriver
{
    private ModbusTcpClient _client = null!;
    private ModbusSettings _settings = null!;
    private ModbusAddress[] _addresses = []; // по индексу тега
    private RequestBlock[] _blocks = [];

    public string ProtocolName => "modbus-tcp";

    public Task ConnectAsync(DeviceDefinition device, IReadOnlyList<TagDefinition> tags, CancellationToken ct)
    {
        _settings = ModbusSettings.Parse(device.Configuration);

        _addresses = tags.Select(t => ModbusAddress.Parse(t.Address)).ToArray();
        var mappings = tags
            .Select((t, i) => new ModbusTagMapping(i, _addresses[i]))
            .ToArray();
        _blocks = RequestGrouper.Group(mappings, _settings.MaxGap).ToArray();

        _client = new ModbusTcpClient
        {
            ReadTimeout = _settings.TimeoutMs,
            WriteTimeout = _settings.TimeoutMs
        };
        _client.Connect($"{_settings.Host}:{_settings.Port}");

        return Task.CompletedTask;
    }

    public async ValueTask<bool> PollAsync(Memory<TagValue> results, CancellationToken ct)
    {
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        foreach (var block in _blocks)
        {
            switch (block.Table)
            {
                case ModbusTable.HoldingRegister:
                    var holding = await _client.ReadHoldingRegistersAsync(
                        _settings.UnitId, (ushort)block.Start, (ushort)block.Count, ct);
                    FillRegisters(block, holding.Span, results.Span, timestamp);
                    break;

                case ModbusTable.InputRegister:
                    var input = await _client.ReadInputRegistersAsync(
                        _settings.UnitId, (ushort)block.Start, (ushort)block.Count, ct);
                    FillRegisters(block, input.Span, results.Span, timestamp);
                    break;

                case ModbusTable.Coil:
                    var coils = await _client.ReadCoilsAsync(
                        _settings.UnitId, (ushort)block.Start, (ushort)block.Count, ct);
                    FillBits(block, coils.Span, results.Span, timestamp);
                    break;

                case ModbusTable.DiscreteInput:
                    var inputs = await _client.ReadDiscreteInputsAsync(
                        _settings.UnitId, (ushort)block.Start, (ushort)block.Count, ct);
                    FillBits(block, inputs.Span, results.Span, timestamp);
                    break;
            }
        }

        return true;
    }

    private void FillRegisters(RequestBlock block, ReadOnlySpan<byte> data,
        Span<TagValue> results, long timestamp)
    {
        foreach (var item in block.Items)
        {
            var type = _addresses[item.ResultIndex].DataType;
            double value = RegisterDecoder.Decode(data, item.OffsetWithinBlock * 2, type);
            results[item.ResultIndex] = new TagValue(value, timestamp, Quality.Good);
        }
    }

    private static void FillBits(RequestBlock block, ReadOnlySpan<byte> data,
        Span<TagValue> results, long timestamp)
    {
        // биты в ответе УПАКОВАНЫ: бит N = (data[N / 8] >> (N % 8)) & 1
        foreach (var item in block.Items)
        {
            int bit = item.OffsetWithinBlock;
            double value = (data[bit / 8] >> (bit % 8)) & 1;
            results[item.ResultIndex] = new TagValue(value, timestamp, Quality.Good);
        }
    }

    public Task DisconnectAsync()
    {
        _client?.Dispose();
        return Task.CompletedTask;
    }
}
