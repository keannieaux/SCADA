using SCADA.Alarms;
using SCADA.Core.Tags;
using SCADA.Runtime.Alarms;
using SCADA.Runtime.Historian;
using SCADA.Runtime.Polling;
using SCADA.Runtime.TagTable;

namespace SCADA.Runtime.Runtime;

/// <summary>
/// Реализация контракта UI ↔ ядро внутри одного процесса: текущие значения
/// берутся из TagTable, история — из фасада <see cref="IHistorian"/>.
/// Remote-реализация повторит те же формы запросов через gRPC.
/// </summary>
public sealed class LocalRuntimeClient : IRuntimeClient
{
    private readonly ITagTable _tagTable;
    private readonly IHistorian? _historian;
    private readonly HistoryQueryLimits _limits;
    private readonly IAlarmEngine? _alarmEngine;
    private readonly IEventJournal? _eventJournal;
    private readonly AlarmChangeBroadcaster? _alarmBroadcaster;
    private readonly PollingEngine? _pollingEngine;

    /// <param name="historian">
    /// null, если архив выключен: тогда запросы истории отдают пустые ряды,
    /// а не падают. Схемы и текущие значения работают без архива.
    /// </param>
    /// <param name="alarmEngine">
    /// null, если сигнализация не настроена: методы аварий отдают пустые
    /// результаты. Тот же принцип, что у <paramref name="historian"/>.
    /// </param>
    /// <param name="pollingEngine">
    /// null, если записи в устройства нет (чтение-only сценарии): WriteTagsAsync
    /// отдаёт отказ, а не падает.
    /// </param>
    public LocalRuntimeClient(
        ITagTable tagTable,
        IHistorian? historian = null,
        HistoryQueryLimits? limits = null,
        IAlarmEngine? alarmEngine = null,
        IEventJournal? eventJournal = null,
        AlarmChangeBroadcaster? alarmBroadcaster = null,
        PollingEngine? pollingEngine = null)
    {
        _tagTable = tagTable;
        _historian = historian;
        _limits = limits ?? new HistoryQueryLimits();
        _alarmEngine = alarmEngine;
        _eventJournal = eventJournal;
        _alarmBroadcaster = alarmBroadcaster;
        _pollingEngine = pollingEngine;
    }

    public TagValue Read(TagId id) => _tagTable.Read(id);

    public void Read(ReadOnlySpan<TagId> ids, Span<TagValue> results)
    {
        for (int i = 0; i < ids.Length; i++)
            results[i] = _tagTable.Read(ids[i]);
    }

    public long CurrentEpoch => _tagTable.CurrentEpoch;

    public int GetChangedSince(long epoch, Span<TagId> destination)
        => _tagTable.GetChangedSince(epoch, destination);

    public void WriteLocal(TagId id, double value)
        => _tagTable.Write(id, new TagValue(value, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Quality.Good));

    public async ValueTask<IReadOnlyList<TagWriteResult>> WriteTagsAsync(
        IReadOnlyList<TagWriteItem> items, string requestedBy, CancellationToken ct = default)
    {
        // без движка опроса записывать некуда — внятный отказ, а не падение
        if (_pollingEngine is null)
            return items.Select(_ => new TagWriteResult(
                TagWriteStatus.Failed, "движок опроса не подключён")).ToArray();
        return await _pollingEngine.WriteTagsAsync(items, requestedBy, ct);
    }

    public async ValueTask<TagWriteResult> WriteTagAsync(
        TagId tag, double value, string requestedBy, CancellationToken ct = default)
        => (await WriteTagsAsync([new TagWriteItem(tag, value)], requestedBy, ct))[0];

    public async ValueTask<HistorySeries[]> ReadHistoryAsync(
        IReadOnlyList<TagId> ids, long fromMs, long toMs,
        int maxPointsPerTag, CancellationToken ct = default)
    {
        _limits.EnsureStreamCount(ids.Count);

        if (_historian is null)
            return [.. ids.Select(HistorySeries.Empty)];

        int cap = Math.Min(maxPointsPerTag, _limits.MaxPointsPerQuery);
        if (cap <= 0)
            return [.. ids.Select(HistorySeries.Empty)];

        using var cts = CreateTimeout(ct);
        var result = new HistorySeries[ids.Count];

        for (int i = 0; i < ids.Count; i++)
        {
            var buffer = new TagValue[cap];
            var read = await _historian.ReadRawAsync(ids[i], fromMs, toMs, buffer, cts.Token);

            // Буфер заполнен целиком — значит точек в диапазоне было не меньше
            // предела, и часть осталась за кадром. Отдавать обрезанный ряд как
            // полный нельзя: тренд молча нарисовал бы половину диапазона.
            if (read.Count >= cap)
            {
                result[i] = await DownsampleAsync(ids[i], fromMs, toMs, cap, cts.Token);
                continue;
            }

            result[i] = new HistorySeries(ids[i], buffer[..read.Count], read.Mode, false);
        }

        return result;
    }

    public async ValueTask<BucketSeries[]> ReadBucketsAsync(
        IReadOnlyList<TagId> ids, long fromMs, long toMs,
        int bucketCount, CancellationToken ct = default)
    {
        _limits.EnsureStreamCount(ids.Count);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bucketCount);

        if (_historian is null)
            return [.. ids.Select(BucketSeries.Empty)];

        using var cts = CreateTimeout(ct);
        var result = new BucketSeries[ids.Count];

        for (int i = 0; i < ids.Count; i++)
        {
            var buckets = new ArchiveBucket[bucketCount];
            var read = await _historian.ReadBucketsAsync(ids[i], fromMs, toMs, buckets, cts.Token);
            result[i] = new BucketSeries(ids[i], buckets, read.Mode, read.Downsampled);
        }

        return result;
    }

    public async ValueTask<TagValue?[]> ReadAtAsync(
        IReadOnlyList<TagId> ids, long atMs, CancellationToken ct = default)
    {
        _limits.EnsureStreamCount(ids.Count);

        var result = new TagValue?[ids.Count];
        if (_historian is null)
            return result;

        using var cts = CreateTimeout(ct);
        for (int i = 0; i < ids.Count; i++)
            result[i] = await _historian.ReadAtAsync(ids[i], atMs, cts.Token);

        return result;
    }

    public int ReadRecent(TagId id, Span<TagValue> destination)
        => _historian?.ReadRecent(id, destination) ?? 0;

    // --- сигнализация (M5) ---

    public ValueTask<IReadOnlyList<ActiveAlarm>> GetActiveAlarmsAsync(
        AlarmFilter filter, CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<ActiveAlarm>>(
            _alarmEngine?.GetActive(filter) ?? Array.Empty<ActiveAlarm>());

    public ValueTask<IReadOnlyList<AlarmEvent>> GetAlarmHistoryAsync(
        AlarmHistoryQuery query, CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<AlarmEvent>>(
            _eventJournal?.Query(query) ?? Array.Empty<AlarmEvent>());

    public ValueTask AcknowledgeAlarmsAsync(
        IEnumerable<string> ruleNames, string acknowledgedBy,
        string? comment = null, CancellationToken ct = default)
    {
        if (_alarmEngine is null)
            return ValueTask.CompletedTask;

        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var ruleName in ruleNames)
        {
            var ev = _alarmEngine.Acknowledge(ruleName, acknowledgedBy, comment, now);
            if (ev is null)
                continue;

            // квитирование идёт тем же путём, что события конвейера:
            // журнал + рассылка подписчикам
            _eventJournal?.Append(new[] { ev });
            var view = _alarmEngine.GetAlarm(ruleName);
            if (view is not null)
                _alarmBroadcaster?.Publish(new AlarmChange(AlarmChangeKind.Acknowledged, view));
        }
        return ValueTask.CompletedTask;
    }

    public IAsyncEnumerable<AlarmChange> SubscribeAlarmsAsync(CancellationToken ct = default)
        => _alarmBroadcaster?.Subscribe(ct) ?? EmptyAlarmChanges(ct);

    private static async IAsyncEnumerable<AlarmChange> EmptyAlarmChanges(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        yield break;
    }

    /// <summary>
    /// Диапазон не помещается в предел сырых точек — отдаём агрегаты по числу
    /// бакетов, равному пределу, и помечаем ряд как прореженный (§14.1).
    /// </summary>
    private async ValueTask<HistorySeries> DownsampleAsync(
        TagId id, long fromMs, long toMs, int bucketCount, CancellationToken ct)
    {
        var buckets = new ArchiveBucket[bucketCount];
        var read = await _historian!.ReadBucketsAsync(id, fromMs, toMs, buckets, ct);

        // Из бакета берём среднее достоверных: тренд рисует линию, а разброс
        // внутри интервала виден по Min/Max при запросе бакетами напрямую.
        var points = new List<TagValue>(read.Count);
        for (int i = 0; i < buckets.Length; i++)
        {
            if (buckets[i].IsEmpty)
                continue;

            var quality = buckets[i].HasGoodValues ? Quality.Good : Quality.Bad;
            double value = buckets[i].HasGoodValues ? buckets[i].Avg : double.NaN;
            points.Add(new TagValue(value, buckets[i].StartMs, quality));
        }

        return new HistorySeries(id, [.. points], read.Mode, Downsampled: true);
    }

    private CancellationTokenSource CreateTimeout(CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_limits.QueryTimeoutMs);
        return cts;
    }
}
