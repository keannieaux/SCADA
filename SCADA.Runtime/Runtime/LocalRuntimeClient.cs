using SCADA.Alarms;
using SCADA.Core.Schemes;
using SCADA.Core.Tags;
using SCADA.Core.Users;
using SCADA.Package.Sections;
using SCADA.Runtime.Alarms;
using SCADA.Runtime.Audit;
using SCADA.Runtime.Historian;
using SCADA.Runtime.Polling;
using SCADA.Runtime.Schemes;
using SCADA.Runtime.TagTable;
using SCADA.Runtime.Users;

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
    private readonly SchemeCatalog? _schemeCatalog;
    private readonly IAccessControl? _access;
    private readonly IAuditJournal? _audit;
    private readonly SessionTagRouter? _sessionTags;

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
    /// <param name="schemeCatalog">
    /// null только в unit-тестах клиента: методы схем отдают пустые результаты,
    /// GetScheme/GetAsset — KeyNotFoundException.
    /// </param>
    /// <param name="access">
    /// null = проверок прав нет, поведение прежнее (unit-тесты, сборки без
    /// подсистемы пользователей). Подключён — операторская запись требует
    /// Operate, квитирование AckAlarms (docs/users-plan.md §5).
    /// </param>
    /// <param name="audit">
    /// журнал для отказов: попытка без права — тоже событие (§7). Успешные
    /// записи аудирует движок опроса, он же знает старые значения.
    /// </param>
    /// <param name="sessionTags">
    /// маршрутизатор сессионных тегов, если они есть в проекте: их запись
    /// исполняется локально, минуя движок опроса и аудит
    /// (docs/session-tags-concept.md §2.2). Обычно это тот же объект, что
    /// передан в <paramref name="tagTable"/>.
    /// </param>
    public LocalRuntimeClient(
        ITagTable tagTable,
        IHistorian? historian = null,
        HistoryQueryLimits? limits = null,
        IAlarmEngine? alarmEngine = null,
        IEventJournal? eventJournal = null,
        AlarmChangeBroadcaster? alarmBroadcaster = null,
        PollingEngine? pollingEngine = null,
        SchemeCatalog? schemeCatalog = null,
        IAccessControl? access = null,
        IAuditJournal? audit = null,
        SessionTagRouter? sessionTags = null)
    {
        _tagTable = tagTable;
        _historian = historian;
        _limits = limits ?? new HistoryQueryLimits();
        _alarmEngine = alarmEngine;
        _eventJournal = eventJournal;
        _alarmBroadcaster = alarmBroadcaster;
        _pollingEngine = pollingEngine;
        _schemeCatalog = schemeCatalog;
        _access = access;
        _audit = audit;
        _sessionTags = sessionTags;
    }

    public TagValue Read(TagId id) => _tagTable.Read(id);

    public void Read(ReadOnlySpan<TagId> ids, Span<TagValue> results)
    {
        for (int i = 0; i < ids.Length; i++)
            results[i] = _tagTable.Read(ids[i]);
    }

    public StringTagValue ReadString(TagId id) => _tagTable.ReadString(id);

    public void ReadStrings(ReadOnlySpan<TagId> ids, Span<StringTagValue> results)
    {
        for (int i = 0; i < ids.Length; i++)
            results[i] = _tagTable.ReadString(ids[i]);
    }

    public long CurrentEpoch => _tagTable.CurrentEpoch;

    public int GetChangedSince(long epoch, Span<TagId> destination)
        => _tagTable.GetChangedSince(epoch, destination);

    public void WriteLocal(TagId id, double value)
        => _tagTable.Write(id, new TagValue(value, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Quality.Good));

    public void WriteLocalString(TagId id, string text)
        => _tagTable.WriteString(id, new StringTagValue(text,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Quality.Good));

    public async ValueTask<IReadOnlyList<TagWriteResult>> WriteTagsAsync(
        IReadOnlyList<TagWriteItem> items, string requestedBy, CancellationToken ct = default)
    {
        // сессионные теги пишутся локально: без сети, без права Operate
        // и без аудита — это состояние интерфейса, а не команда объекту
        // (docs/session-tags-concept.md §2.2). Смешанный пакет делится:
        // сессионная часть исполняется здесь, остальная — обычным путём
        if (_sessionTags is not null && items.Any(i => _sessionTags.IsSessionTag(i.TagId)))
            return await WriteMixedAsync(items, requestedBy, ct);

        if (_access is not null)
        {
            if (!_access.HasPermission(SystemPermissions.Operate))
            {
                // отказ — тоже событие аудита (§7): «пытались, не дали»
                AuditDenied("tag-write", SystemPermissions.Operate,
                    items.Select(i => (TagName(i.TagId), (double?)i.Value)));
                return items.Select(_ => new TagWriteResult(
                    TagWriteStatus.Denied,
                    $"недостаточно прав (требуется: {SystemPermissions.Operate})")).ToArray();
            }

            // в аудит идёт логин сессии, а не строка от вызывающего:
            // клиент может прислать что угодно, ядро знает, кто вошёл
            requestedBy = _access.CurrentLogin;
            _access.NoteActivity();
        }

        // без движка опроса записывать некуда — внятный отказ, а не падение
        if (_pollingEngine is null)
            return items.Select(_ => new TagWriteResult(
                TagWriteStatus.Failed, "движок опроса не подключён")).ToArray();
        return await _pollingEngine.WriteTagsAsync(items, requestedBy, ct);
    }

    /// <summary>
    /// Пакет с сессионными тегами: сессионные исполняются локально, остальные
    /// уходят прежним путём (право `Operate`, движок опроса, аудит). Порядок
    /// результатов соответствует входному списку — контракт batch-native
    /// не меняется.
    /// </summary>
    private async ValueTask<IReadOnlyList<TagWriteResult>> WriteMixedAsync(
        IReadOnlyList<TagWriteItem> items, string requestedBy, CancellationToken ct)
    {
        var results = new TagWriteResult[items.Count];
        var forEngine = new List<TagWriteItem>();
        var engineIndices = new List<int>();

        for (int i = 0; i < items.Count; i++)
        {
            if (_sessionTags!.IsSessionTag(items[i].TagId))
            {
                results[i] = _sessionTags.WriteFromOperator(items[i].TagId, items[i].Value);
                continue;
            }
            forEngine.Add(items[i]);
            engineIndices.Add(i);
        }

        if (forEngine.Count > 0)
        {
            var engineResults = await WriteTagsAsync(forEngine, requestedBy, ct);
            for (int i = 0; i < engineIndices.Count; i++)
                results[engineIndices[i]] = engineResults[i];
        }

        return results;
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
        // право проверяется до состояния подсистемы: отказ фиксируется
        // одинаково, есть в проекте аварии или нет
        if (_access is not null)
        {
            // список перечисляется дважды (аудит отказа и квитирование) —
            // материализуем, вызывающий мог передать ленивый запрос
            var names = ruleNames as IReadOnlyList<string> ?? ruleNames.ToArray();
            if (!_access.HasPermission(SystemPermissions.AckAlarms))
            {
                AuditDenied("alarm-ack", SystemPermissions.AckAlarms,
                    names.Select(name => (name, (double?)null)));
                return ValueTask.CompletedTask;
            }

            acknowledgedBy = _access.CurrentLogin;
            _access.NoteActivity();
            ruleNames = names;
        }

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

    // --- схемы (M6) ---

    public IReadOnlyList<SchemeInfo> GetSchemes()
        => _schemeCatalog?.Schemes ?? [];

    public Scheme GetScheme(string name)
        => _schemeCatalog?.GetScheme(name)
            ?? throw new KeyNotFoundException($"Схема '{name}' не найдена");

    public IReadOnlyList<SchemeTemplate> GetTemplates()
        => _schemeCatalog?.Templates ?? [];

    public CodePool GetCodePool()
        => _schemeCatalog?.CodePool ?? new CodePool([], []);

    public bool TryGetTagId(string name, out TagId id)
    {
        if (_schemeCatalog is not null && _schemeCatalog.TagsByName.TryGetValue(name, out id))
            return true;
        id = default;
        return false;
    }

    public IReadOnlyList<string> GetAssets()
        => _schemeCatalog?.Assets ?? [];

    public byte[] GetAsset(string path)
        => _schemeCatalog?.GetAsset(path)
            ?? throw new KeyNotFoundException($"Ассет '{path}' не найден");

    /// <summary>Запись отказа в аудит: одна строка на каждую цель попытки,
    /// связанные общим BatchId — как у обычной пакетной записи (§7).</summary>
    private void AuditDenied(string action, string permission,
        IEnumerable<(string Target, double? NewValue)> targets)
    {
        if (_audit is null)
            return;

        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string batchId = Guid.NewGuid().ToString("N");
        string user = _access?.CurrentLogin ?? string.Empty;

        _audit.Append(targets.Select(t => new AuditEntry(
            TimestampUtcMs: now,
            User: user,
            Action: action,
            Target: t.Target,
            OldValue: null,
            NewValue: t.NewValue,
            Result: nameof(TagWriteStatus.Denied),
            Detail: $"недостаточно прав (требуется: {permission})",
            BatchId: batchId)).ToArray());
    }

    /// <summary>Имя тега для аудита. Обратный поиск по каталогу — путь
    /// отказа, он редкий; на успешной записи имена берёт движок опроса.</summary>
    private string TagName(TagId id)
    {
        if (_schemeCatalog is not null)
            foreach (var (name, tagId) in _schemeCatalog.TagsByName)
                if (tagId == id)
                    return name;
        return id.ToString();
    }

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
