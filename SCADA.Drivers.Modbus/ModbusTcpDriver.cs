using System.Net;
using FluentModbus;
using SCADA.Core.Devices;
using SCADA.Core.Tags;
using SCADA.Drivers.Abstractions;

namespace SCADA.Drivers.Modbus;

/// <summary>
/// Драйвер Modbus TCP. При подключении парсит адреса тегов и группирует
/// их в блоки запросов (§7.3); в цикле опроса выполняет блоки и раскладывает
/// ответы по результатам. Значения СЫРЫЕ — масштаб применяет движок.
/// Ошибки связи выбрасываются наружу: движок пометит теги Quality.Bad,
/// выбросит этот экземпляр и переподключится с backoff (§4.2) — драйвер
/// одноразовый, переподключение не его ответственность.
/// </summary>
public sealed class ModbusTcpDriver : IWritableDeviceDriver
{
    private ModbusTcpClient _client = null!;
    private ModbusSettings _settings = null!;
    private ModbusAddress[] _addresses = []; // по индексу тега
    private RequestBlock[] _blocks = [];
    private Dictionary<TagId, ModbusAddress> _addressByTag = []; // для записи

    public string ProtocolName => "modbus-tcp";

    public async Task ConnectAsync(DeviceDefinition device, IReadOnlyList<TagDefinition> tags, CancellationToken ct)
    {
        _settings = ModbusSettings.Parse(device.Configuration);

        _addresses = tags.Select(t => ModbusAddress.Parse(t.Address)).ToArray();
        _addressByTag = tags.Select((t, i) => (t.Id, _addresses[i]))
            .ToDictionary(x => x.Id, x => x.Item2);
        var mappings = tags
            .Select((t, i) => new ModbusTagMapping(i, _addresses[i]))
            .ToArray();
        _blocks = RequestGrouper.Group(mappings, _settings.MaxGap, _settings.MaxRegisters).ToArray();

        // DNS-резолв асинхронный; сам Connect у FluentModbus только синхронный —
        // выносим в фоновый поток, чтобы не блокировать цикл канала
        var addresses = await Dns.GetHostAddressesAsync(_settings.Host, ct);
        var endpoint = new IPEndPoint(addresses[0], _settings.Port);

        _client = new ModbusTcpClient
        {
            ReadTimeout = _settings.TimeoutMs,
            WriteTimeout = _settings.TimeoutMs
        };
        await Task.Run(() => _client.Connect(endpoint), ct);
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

    /// <summary>
    /// Запись (M7). Двухфазная: сначала кодируются ВСЕ элементы — ошибка
    /// кодирования отклоняет весь пакет, устройство не трогается; потом
    /// передача. В FC16 сливаются только строго соседние регистры (в отличие
    /// от чтения, MaxGap на записи недопустим: дыры в диапазоне уехали бы
    /// в устройство мусором). Отклонение блока устройством (exception
    /// response) помечает только его теги, остальные блоки доезжают.
    /// Потеря связи — исключение наружу, как на опросе.
    /// </summary>
    public async Task<TagWriteResult[]> WriteAsync(
        IReadOnlyList<DriverWriteItem> items, CancellationToken ct)
    {
        var results = new TagWriteResult[items.Count];
        var encoded = new (ModbusAddress Address, byte[] Bytes)?[items.Count];

        // фаза 1: адреса + кодирование
        bool hasInvalid = false;
        for (int i = 0; i < items.Count; i++)
        {
            var tag = items[i].Tag;
            if (!_addressByTag.TryGetValue(tag.Id, out var address))
            {
                results[i] = new(TagWriteStatus.Failed, "тег не принадлежит устройству");
                hasInvalid = true;
                continue;
            }
            if (address.Table is ModbusTable.DiscreteInput or ModbusTable.InputRegister)
            {
                results[i] = new(TagWriteStatus.NotWritable,
                    $"таблица {address.Table} только для чтения");
                hasInvalid = true;
                continue;
            }
            try
            {
                byte[] bytes = address.Table == ModbusTable.Coil
                    ? [] // койл — бит, кодируется при передаче
                    : RegisterEncoder.Encode(items[i].RawValue, address.DataType);
                encoded[i] = (address, bytes);
            }
            catch (ArgumentOutOfRangeException ex)
            {
                results[i] = new(TagWriteStatus.ValidationFailed, ex.Message);
                hasInvalid = true;
            }
        }

        // весь пакет отклоняется до передачи — частично применённый
        // рецепт хуже отказа
        if (hasInvalid)
        {
            for (int i = 0; i < items.Count; i++)
                if (results[i].Status == default)
                    results[i] = new(TagWriteStatus.ValidationFailed,
                        "пакет отклонён: ошибки валидации других элементов");
            return results;
        }

        // фаза 2: группировка в блоки (таблица → смещение) и передача
        foreach (var block in BuildWriteBlocks(items, encoded))
        {
            try
            {
                await ExecuteBlockAsync(block, items, encoded, ct);
                foreach (int index in block.ItemIndices)
                    results[index] = TagWriteResult.Success;
            }
            catch (ModbusException ex)
            {
                // устройство отвергло блок — остальные блоки продолжаем
                foreach (int index in block.ItemIndices)
                    results[index] = new(TagWriteStatus.RejectedByDevice, ex.Message);
            }
        }

        return results;
    }

    /// <summary>Блок записи: койл по одному (FC05), регистры — строго
    /// непрерывные серии (FC06/FC16), в пределах MaxRegisters.</summary>
    private List<WriteBlock> BuildWriteBlocks(
        IReadOnlyList<DriverWriteItem> items,
        (ModbusAddress Address, byte[] Bytes)?[] encoded)
    {
        var blocks = new List<WriteBlock>();
        WriteBlock? current = null;

        foreach (int i in Enumerable.Range(0, items.Count)
                     .OrderBy(i => encoded[i]!.Value.Address.Table)
                     .ThenBy(i => encoded[i]!.Value.Address.Offset))
        {
            var (address, _) = encoded[i]!.Value;

            bool continues = current is not null
                && current.Table == ModbusTable.HoldingRegister
                && address.Table == ModbusTable.HoldingRegister
                && address.Offset == current.NextOffset
                && current.RegisterCount + address.RegisterCount <= _settings.MaxRegisters;

            if (!continues)
            {
                current = new WriteBlock(address.Table, address.Offset);
                blocks.Add(current);
            }
            current!.ItemIndices.Add(i); // current гарантированно не null
            current.RegisterCount += address.RegisterCount;
        }
        return blocks;
    }

    private async Task ExecuteBlockAsync(WriteBlock block,
        IReadOnlyList<DriverWriteItem> items,
        (ModbusAddress Address, byte[] Bytes)?[] encoded, CancellationToken ct)
    {
        if (block.Table == ModbusTable.Coil)
        {
            // койлы — по одному (FC05); команды дискретные, пакетов не бывает
            int i = block.ItemIndices[0];
            await _client.WriteSingleCoilAsync(_settings.UnitId, block.StartOffset,
                items[i].RawValue >= 0.5, ct);
            return;
        }

        byte[] data = block.ItemIndices
            .SelectMany(i => encoded[i]!.Value.Bytes)
            .ToArray();

        if (block.RegisterCount == 1)
            await _client.WriteSingleRegisterAsync(_settings.UnitId,
                (ushort)block.StartOffset, data, ct);
        else
            await _client.WriteMultipleRegistersAsync(_settings.UnitId,
                (ushort)block.StartOffset, data, ct);
    }

    private sealed class WriteBlock(ModbusTable table, int startOffset)
    {
        public ModbusTable Table { get; } = table;
        public int StartOffset { get; } = startOffset;
        public List<int> ItemIndices { get; } = [];
        public int RegisterCount { get; set; }
        public int NextOffset => StartOffset + RegisterCount;
    }
}
