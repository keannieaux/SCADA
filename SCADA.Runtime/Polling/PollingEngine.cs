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
/// Переподключение — ответственность движка (единая политика §4.2):
/// драйвер одноразовый, после ошибки связи выбрасывается и пересоздаётся
/// с экспоненциальной задержкой 1с → 30с.
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

    /// <summary>
    /// Диагностический hook: вызывается при ошибке опроса/подключения устройства.
    /// Движок ошибки не глотает молча — но и не логирует сам (логирование — M4+);
    /// подписчик решает, что с ними делать (пробник печатает в консоль).
    /// </summary>
    public Action<DeviceDefinition, Exception>? OnDeviceError { get; set; }

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
        // состояние на устройство: теги, переиспользуемый буфер, драйвер и backoff
        var states = devices.Select(d =>
        {
            var tags = _config.Tags.Where(t => t.DeviceId == d.Id).ToArray();
            return new DevicePollState(d, tags, new TagValue[tags.Length]);
        }).ToArray();

        // диагностика канала (§7.4); null, если конфиг без сгенерированных тегов
        var channel = _config.Channels.FirstOrDefault(c => c.Id == devices[0].ChannelId);
        var diagnostics = channel is null ? null : ChannelDiagnostics.Create(_config, channel);

        try
        {
            foreach (var state in states)
                if (await TryConnectAsync(state, ct) == ConnectOutcome.Reconnected)
                    diagnostics?.OnReconnect();

            using var timer = new PeriodicTimer(_pollPeriod);
            while (await timer.WaitForNextTickAsync(ct))
            {
                foreach (var state in states)
                {
                    // устройство отключено — ждём своего времени и пробуем снова (§4.2)
                    if (state.Driver is null)
                    {
                        if (DateTimeOffset.UtcNow >= state.NextConnectAt)
                            if (await TryConnectAsync(state, ct) == ConnectOutcome.Reconnected)
                                diagnostics?.OnReconnect();
                        continue;
                    }

                    try
                    {
                        long started = System.Diagnostics.Stopwatch.GetTimestamp();
                        await PollDeviceAsync(state.Driver, state.Tags, state.Buffer, ct);

                        // внутренние устройства не опрашиваются по сети — в статистику канала не идут
                        if (diagnostics is not null && state.Device.DriverName != "internal")
                            diagnostics.OnPollSuccess(
                                System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        // связь потеряна — теги Bad, драйвер выбрасываем, планируем reconnect
                        OnDeviceError?.Invoke(state.Device, ex);
                        if (state.Device.DriverName != "internal")
                            diagnostics?.OnPollFailure();
                        MarkTagsBad(state.Tags);
                        await DropDriverAsync(state);
                    }
                }

                // сброс диагностики в таблицу — раз в секунду, не чаще
                if (diagnostics is not null)
                {
                    long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    if (diagnostics.IsFlushDue(now))
                        diagnostics.Flush(_tagTable, AllConnected(states), now);
                }
            }
        }
        finally
        {
            foreach (var state in states)
                await DropDriverAsync(state, scheduleReconnect: false);
        }
    }

    // канал считается подключённым, когда подключены все его сетевые устройства
    private static bool AllConnected(DevicePollState[] states)
        => states.Where(s => s.Device.DriverName != "internal").All(s => s.Driver is not null);

    private enum ConnectOutcome { Connected, Reconnected, Failed }

    /// <summary>
    /// Попытка подключения устройства. Драйвер одноразовый: при ошибке
    /// выбрасывается, следующая попытка создаст новый экземпляр.
    /// </summary>
    private async Task<ConnectOutcome> TryConnectAsync(DevicePollState state, CancellationToken ct)
    {
        IDeviceDriver? driver = null;
        try
        {
            driver = DriverFactory.Create(state.Device);
            await driver.ConnectAsync(state.Device, state.Tags, ct);
            state.Driver = driver;
            bool wasReconnect = state.ConsecutiveFailures > 0;
            state.ConsecutiveFailures = 0;
            return wasReconnect ? ConnectOutcome.Reconnected : ConnectOutcome.Connected;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            OnDeviceError?.Invoke(state.Device, ex);
            MarkTagsBad(state.Tags);
            ScheduleReconnect(state);
            if (driver is not null)
            {
                try { await driver.DisconnectAsync(); }
                catch (Exception) { /* драйвер не подключился — игнорируем ошибки dispose */ }
            }
            return ConnectOutcome.Failed;
        }
    }

    private async Task DropDriverAsync(DevicePollState state, bool scheduleReconnect = true)
    {
        var driver = state.Driver;
        if (driver is null)
            return;

        state.Driver = null;
        if (scheduleReconnect)
            ScheduleReconnect(state);

        try { await driver.DisconnectAsync(); }
        catch (Exception) { /* соединение уже мёртво — игнорируем ошибки dispose */ }
    }

    private static void ScheduleReconnect(DevicePollState state)
    {
        state.ConsecutiveFailures++;
        state.NextConnectAt = DateTimeOffset.UtcNow + BackoffDelay(state.ConsecutiveFailures);
    }

    // экспоненциальная задержка по ТЗ §4.2: 1с → 2 → 4 → 8 → 16 → 30с (потолок)
    private static TimeSpan BackoffDelay(int consecutiveFailures)
        => TimeSpan.FromSeconds(Math.Min(30, 1 << Math.Min(consecutiveFailures - 1, 5)));

    /// <summary>Состояние опроса одного устройства внутри канала.</summary>
    private sealed class DevicePollState(DeviceDefinition device, TagDefinition[] tags, TagValue[] buffer)
    {
        public DeviceDefinition Device { get; } = device;
        public TagDefinition[] Tags { get; } = tags;
        public TagValue[] Buffer { get; } = buffer;
        public IDeviceDriver? Driver { get; set; }
        public int ConsecutiveFailures { get; set; }
        public DateTimeOffset NextConnectAt { get; set; }
    }

    private async ValueTask PollDeviceAsync(
        IDeviceDriver driver, TagDefinition[] tags, TagValue[] buffer, CancellationToken ct)
    {
        // исключение = потеря связи: летит в RunChannelAsync, который
        // пометит теги Bad и запланирует переподключение
        bool hasFreshValues = await driver.PollAsync(buffer, ct);

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

    /// <summary>
    /// Пометить теги устройства недостоверными. Значение сохраняется последнее
    /// известное (ТЗ §4.2 — не зануляем), метка времени обновляется: по ней
    /// видно, когда качество ухудшилось.
    /// </summary>
    private void MarkTagsBad(TagDefinition[] tags)
    {
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var tag in tags)
        {
            var current = _tagTable.Read(tag.Id);
            _tagTable.Write(tag.Id, new TagValue(current.Value, timestamp, Quality.Bad));
        }
    }
}
