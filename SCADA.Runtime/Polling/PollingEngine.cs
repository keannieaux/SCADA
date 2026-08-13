using SCADA.Core.Devices;
using SCADA.Core.Tags;
using SCADA.Drivers.Abstractions;
using SCADA.Runtime.TagTable;

namespace SCADA.Runtime.Polling;

/// <summary>
/// Движок опроса: создаёт драйверы устройств и периодически
/// переносит значения из них в TagTable.
/// Один канал связи = один Task (ТЗ §7.3): устройства внутри канала
/// опрашиваются последовательно, каналы — параллельно.
/// </summary>
public sealed class PollingEngine
{
    private readonly ProjectConfiguration _config;
    private readonly ITagTable _tagTable;
    private readonly TimeSpan _pollPeriod;

    private CancellationTokenSource? _cts;
    private Task[]? _channelTasks;

    public PollingEngine(ProjectConfiguration config, ITagTable tagTable, TimeSpan? pollPeriod = null)
    {
        _config = config;
        _tagTable = tagTable;
        _pollPeriod = pollPeriod ?? TimeSpan.FromMilliseconds(100);
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        if (_cts is not null)
            throw new InvalidOperationException("Движок уже запущен");

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // внутренние теги никто не опрашивает — записываем им начальные значения
        WriteInitialValues();

        _channelTasks = _config.Devices
            .GroupBy(d => d.ChannelId)
            .Select(g => RunChannelAsync(g.ToArray(), _cts.Token))
            .ToArray();

        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_cts is null)
            return;

        await _cts.CancelAsync();
        // циклы завершаются отменой таймера — это штатный путь, не ошибка
        try { await Task.WhenAll(_channelTasks!); }
        catch (OperationCanceledException) { }

        _cts.Dispose();
        _cts = null;
        _channelTasks = null;
    }

    private void WriteInitialValues()
    {
        var internalDeviceIds = _config.Devices
            .Where(d => d.DriverName == "internal")
            .Select(d => d.Id)
            .ToHashSet();

        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var tag in _config.Tags)
            if (tag.InitValue.HasValue && internalDeviceIds.Contains(tag.DeviceId))
                _tagTable.Write(tag.Id, new TagValue(tag.InitValue.Value, timestamp, Quality.Good));
    }

    private async Task RunChannelAsync(DeviceDefinition[] devices, CancellationToken ct)
    {
        // подготовка: драйвер, его теги и переиспользуемый буфер на каждое устройство
        var drivers = new IDeviceDriver[devices.Length];
        var deviceTags = new TagDefinition[devices.Length][];
        var buffers = new TagValue[devices.Length][];

        try
        {
            for (int i = 0; i < devices.Length; i++)
            {
                drivers[i] = DriverFactory.Create(devices[i]);
                deviceTags[i] = _config.Tags.Where(t => t.DeviceId == devices[i].Id).ToArray();
                buffers[i] = new TagValue[deviceTags[i].Length];
                await drivers[i].ConnectAsync(devices[i], deviceTags[i], ct);
            }

            using var timer = new PeriodicTimer(_pollPeriod);
            while (await timer.WaitForNextTickAsync(ct))
            {
                for (int i = 0; i < devices.Length; i++)
                    await PollDeviceAsync(drivers[i], deviceTags[i], buffers[i], ct);
            }
        }
        finally
        {
            foreach (var driver in drivers)
                if (driver is not null)
                    await driver.DisconnectAsync();
        }
    }

    private async ValueTask PollDeviceAsync(
        IDeviceDriver driver, TagDefinition[] tags, TagValue[] buffer, CancellationToken ct)
    {
        bool hasFreshValues;
        try
        {
            hasFreshValues = await driver.PollAsync(buffer, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // связь с устройством потеряна — теги помечаются Bad, движок продолжает работать
            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            for (int i = 0; i < tags.Length; i++)
                _tagTable.Write(tags[i].Id, new TagValue(0, timestamp, Quality.Bad));
            return;
        }

        // драйвер сообщил, что новых данных нет — таблицу не трогаем
        if (!hasFreshValues)
            return;

        // Инвариант: в TagTable значения всегда в ИНЖЕНЕРНЫХ единицах.
        // Драйверы отдают сырые значения, масштаб применяется здесь —
        // один раз для всех протоколов (дефолт factor=1/offset=0 — тождественно).
        for (int i = 0; i < tags.Length; i++)
        {
            var raw = buffer[i];
            double scaled = raw.Value * tags[i].ScaleFactor + tags[i].ScaleOffset;
            _tagTable.Write(tags[i].Id, new TagValue(scaled, raw.TimeStampUtc, raw.Quality));
        }
    }
}
