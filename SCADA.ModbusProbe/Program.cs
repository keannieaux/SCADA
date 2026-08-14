using SCADA.Core.Channels;
using SCADA.Core.Devices;
using SCADA.Core.Tags;
using SCADA.Drivers.Modbus;
using SCADA.Runtime.Polling;
using SCADA.Runtime.TagTable;

// Ручной пробник Modbus TCP: подключается к устройству, опрашивает теги
// и печатает в консоль изменения (имя, адрес, значение, качество).
// Запуск: dotnet run --project SCADA.ModbusProbe

// ── настройка под устройство ────────────────────────────────────────────
// slave id задаётся параметром unit (по умолчанию 0 — стандарт для прямого TCP):
// "127.0.0.1:502;unit=1;timeout=1000"
const string connection = "127.0.0.1:502;unit=1;timeout=1000";

// по 2 тега каждого типа данных + битовые таблицы.
// Смещения — ПРИМЕР: поправь под карту регистров своего устройства.
(string Name, string Address)[] probeTags =
[
    ("HR_U16_A", "hr:0:u16"),   ("HR_U16_B", "hr:1:u16"),
    ("HR_I16_A", "hr:2:i16"),   ("HR_I16_B", "hr:3:i16"),
    ("HR_U32_A", "hr:4:u32"),   ("HR_U32_B", "hr:6:u32"),
    ("HR_I32_A", "hr:8:i32"),   ("HR_I32_B", "hr:10:i32"),
    ("HR_F32_A", "hr:12:f32"),  ("HR_F32_B", "hr:14:f32"),
    ("COIL_A",   "coil:0"),     ("COIL_B",   "coil:1"),
    ("DI_A",     "di:0"),       ("DI_B",     "di:1"),
];

const int pollPeriodMs = 500;
// ─────────────────────────────────────────────────────────────────────────

DriverFactory.Register("modbus-tcp", () => new ModbusTcpDriver());

var config = new ProjectConfiguration
{
    Name = "ModbusProbe",
    Channels = [new ChannelDefinition { Id = new ChannelId(0), Name = "Probe", ChannelType = "modbus-tcp" }],
    Devices =
    [
        new DeviceDefinition
        {
            Id = new DeviceId(0), Name = "Device", DriverName = "modbus-tcp",
            ChannelId = new ChannelId(0), Configuration = connection
        }
    ],
    Tags = probeTags.Select((t, i) => new TagDefinition
    {
        Id = new TagId(i), Name = t.Name,
        DataType = t.Address.StartsWith("coil") || t.Address.StartsWith("di")
            ? TagDataType.Discrete : TagDataType.Analog,
        DeviceId = new DeviceId(0), Address = t.Address
    }).ToArray()
};

var table = new TagTable(config.Tags.Count);
var engine = new PollingEngine(config, table, TimeSpan.FromMilliseconds(pollPeriodMs));

// причина ошибок (таймаут, отказ соединения, Modbus-исключение) — сразу в консоль
engine.OnDeviceError = (device, ex) =>
    Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  ОШИБКА {device.Name}: {ex.GetType().Name}: {ex.Message}");

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };

Console.WriteLine($"Опрос {connection}, тегов: {config.Tags.Count}, период {pollPeriodMs} мс. Ctrl+C — выход.");
await engine.StartAsync(shutdown.Token);

// читаем изменения по эпохам — тот же механизм, что использует UI (ТЗ §9.2)
long epoch = 0;
var changed = new TagId[config.Tags.Count];
try
{
    while (!shutdown.IsCancellationRequested)
    {
        await Task.Delay(200, shutdown.Token);

        int count = table.GetChangedSince(epoch, changed);
        epoch = table.CurrentEpoch;

        for (int i = 0; i < Math.Min(count, changed.Length); i++)
        {
            var tag = config.Tags[changed[i].Value];
            var value = table.Read(tag.Id);
            var time = DateTimeOffset.FromUnixTimeMilliseconds(value.TimeStampUtc).ToLocalTime();
            Console.WriteLine($"{time:HH:mm:ss.fff}  {tag.Name,-10} {tag.Address,-12} = {value.Value,12}  [{value.Quality}]");
        }
    }
}
catch (OperationCanceledException) { }
finally
{
    await engine.StopAsync();
}
