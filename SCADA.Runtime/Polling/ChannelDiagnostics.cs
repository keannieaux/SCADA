using SCADA.Core.Channels;
using SCADA.Core.Tags;
using SCADA.Runtime.Configuration;

namespace SCADA.Runtime.Polling;

/// <summary>
/// Писатель диагностических тегов канала (ТЗ §7.4). События (успех/ошибка
/// запроса, переподключение) накапливаются в цикле опроса, а в TagTable
/// сбрасываются раз в секунду: операторским данным не нужна частота опроса,
/// а лишние записи раздувают эпохи изменений.
/// Возвращает null из Create, если в конфигурации нет сгенерированной
/// диагностики (например, ручные конфиги в тестах) — движок работает без неё.
/// </summary>
internal sealed class ChannelDiagnostics
{
    private const long FlushIntervalMs = 1000;

    private readonly TagId _connected;
    private readonly TagId _lastOkTime;
    private readonly TagId _requestsOk;
    private readonly TagId _requestsFailed;
    private readonly TagId _reconnectCount;
    private readonly TagId _responseTimeAvg;
    private readonly TagId _responseTimeMax;

    private long _ok;
    private long _failed;
    private long _reconnects;
    private double _responseSum;
    private double _responseMax;
    private long _lastOkUnixMs;
    private long _lastFlushUnixMs;

    private ChannelDiagnostics(IReadOnlyDictionary<string, TagId> metricIds)
    {
        _connected = metricIds[DiagnosticsGenerator.ConnectedSuffix];
        _lastOkTime = metricIds[DiagnosticsGenerator.LastOkTimeSuffix];
        _requestsOk = metricIds[DiagnosticsGenerator.RequestsOkSuffix];
        _requestsFailed = metricIds[DiagnosticsGenerator.RequestsFailedSuffix];
        _reconnectCount = metricIds[DiagnosticsGenerator.ReconnectCountSuffix];
        _responseTimeAvg = metricIds[DiagnosticsGenerator.ResponseTimeAvgSuffix];
        _responseTimeMax = metricIds[DiagnosticsGenerator.ResponseTimeMaxSuffix];
    }

    public static ChannelDiagnostics? Create(ProjectConfiguration config, ChannelDefinition channel)
    {
        var deviceName = DiagnosticsGenerator.DeviceName(channel.Name);
        var device = config.Devices.FirstOrDefault(
            d => d.ChannelId == channel.Id && d.Name == deviceName);
        if (device is null)
            return null; // диагностика не сгенерирована — канал работает без неё

        // словарь допустим: это разовая инициализация канала, не горячий путь
        string prefix = deviceName + ".";
        var metricIds = new Dictionary<string, TagId>();
        foreach (var tag in config.Tags)
            if (tag.DeviceId == device.Id)
                metricIds[tag.Name[prefix.Length..]] = tag.Id;

        return new ChannelDiagnostics(metricIds);
    }

    public void OnPollSuccess(double elapsedMs, long unixMs)
    {
        _ok++;
        _responseSum += elapsedMs;
        if (elapsedMs > _responseMax)
            _responseMax = elapsedMs;
        _lastOkUnixMs = unixMs;
    }

    public void OnPollFailure() => _failed++;

    public void OnReconnect() => _reconnects++;

    public bool IsFlushDue(long unixMs) => unixMs - _lastFlushUnixMs >= FlushIntervalMs;

    public void Flush(ITagTable table, bool connected, long unixMs)
    {
        _lastFlushUnixMs = unixMs;
        Write(table, _connected, connected ? 1 : 0, unixMs);
        Write(table, _lastOkTime, _lastOkUnixMs, unixMs);
        Write(table, _requestsOk, _ok, unixMs);
        Write(table, _requestsFailed, _failed, unixMs);
        Write(table, _reconnectCount, _reconnects, unixMs);
        Write(table, _responseTimeAvg, _ok > 0 ? _responseSum / _ok : 0, unixMs);
        Write(table, _responseTimeMax, _responseMax, unixMs);
    }

    private static void Write(ITagTable table, TagId id, double value, long unixMs)
        => table.Write(id, new TagValue(value, unixMs, Quality.Good));
}
