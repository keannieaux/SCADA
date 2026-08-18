using System.Threading.Channels;
using SCADA.Core.Devices;
using SCADA.Core.Tags;
using SCADA.Drivers.Abstractions;
using SCADA.Runtime.Audit;

namespace SCADA.Runtime.Polling;

/// <summary>
/// Запись в теги (M7). Контракт batch-native: одиночная запись — пакет из
/// одного элемента, одного пути кода. Пакет валидируется целиком ДО
/// исполнения (частично применённый рецепт хуже отказа), режется по каналам,
/// каждый подпакет едет в очередь своего канала и исполняется циклом опроса —
/// сокет принадлежит циклу, конкурентного доступа к драйверу нет.
/// Internal-теги минуют очередь: пишутся напрямую в TagTable, персистентные —
/// ещё и на диск. Каждая попытка пишется в аудит (ТЗ §13).
/// </summary>
public sealed partial class PollingEngine
{
    /// <summary>Диагностика записи: ошибки персистентности и т.п. —
    /// не останавливают исполнение, но молчать о них нельзя.</summary>
    public Action<string>? OnWriteWarning { get; set; }

    /// <summary>
    /// Записать пакет значений (инженерные единицы). Результат — поэлементный,
    /// в порядке входного списка. Один таймаут на пакет (writeTimeoutMs).
    /// </summary>
    public async Task<IReadOnlyList<TagWriteResult>> WriteTagsAsync(
        IReadOnlyList<TagWriteItem> items, string requestedBy, CancellationToken ct = default)
    {
        if (items.Count == 0)
            return Array.Empty<TagWriteResult>();

        var results = new TagWriteResult?[items.Count];
        string batchId = Guid.NewGuid().ToString("N");
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var tagNames = new string[items.Count];
        var oldValues = new double?[items.Count];

        // валидация конфигурации: весь пакет отклоняется до исполнения
        bool hasInvalid = false;
        for (int i = 0; i < items.Count; i++)
        {
            if (!_tagById.TryGetValue(items[i].TagId, out var tag))
            {
                results[i] = new TagWriteResult(TagWriteStatus.NotWritable, "тег не найден");
                hasInvalid = true;
                continue;
            }
            tagNames[i] = tag.Name;
            oldValues[i] = _tagTable.Read(tag.Id).Value;
            if (!tag.IsWritable)
            {
                results[i] = new TagWriteResult(TagWriteStatus.NotWritable,
                    "тег не доступен для записи");
                hasInvalid = true;
            }
            else if (tag.ScaleFactor == 0)
            {
                results[i] = new TagWriteResult(TagWriteStatus.ValidationFailed,
                    "нулевой масштаб: обратное преобразование невозможно");
                hasInvalid = true;
            }
        }

        if (hasInvalid)
        {
            for (int i = 0; i < items.Count; i++)
                results[i] ??= new TagWriteResult(TagWriteStatus.ValidationFailed,
                    "пакет отклонён: ошибки валидации других элементов");
            AuditWrite(batchId, requestedBy, items, tagNames, oldValues, results);
            return FinalizeResults(results);
        }

        // маршрутизация: internal — напрямую в таблицу, сетевые — по каналам
        var pendingByPipe = new Dictionary<int, List<PendingWriteEntry>>();
        for (int i = 0; i < items.Count; i++)
        {
            var tag = _tagById[items[i].TagId];
            var device = _deviceById[tag.DeviceId];

            if (device.DriverName == "internal")
            {
                // internal-тег: источник правды — сама TagTable
                _tagTable.Write(tag.Id, new TagValue(items[i].Value, now, Quality.Good));
                if (tag.IsPersistent)
                    SavePersistent(tag.Name, items[i].Value);
                results[i] = TagWriteResult.Success;
                continue;
            }

            // обратное масштабирование — симметрично чтению (PollDeviceAsync)
            double raw = (items[i].Value - tag.ScaleOffset) / tag.ScaleFactor;
            int pipeIndex = _channelByDevice[device.Id];
            if (!pendingByPipe.TryGetValue(pipeIndex, out var list))
                pendingByPipe[pipeIndex] = list = [];
            list.Add(new PendingWriteEntry(device.Id, new DriverWriteItem(tag, raw), i));
        }

        if (pendingByPipe.Count > 0)
        {
            if (_writePipes is null)
            {
                foreach (var entries in pendingByPipe.Values)
                    foreach (var entry in entries)
                        results[entry.TargetIndex] = new TagWriteResult(
                            TagWriteStatus.Failed, "движок не запущен");
            }
            else
            {
                await DispatchNetworkWritesAsync(pendingByPipe, results, ct);
            }
        }

        AuditWrite(batchId, requestedBy, items, tagNames, oldValues, results);
        return FinalizeResults(results);
    }

    /// <summary>Удобная обёртка одиночной записи — для кнопок UI.</summary>
    public async Task<TagWriteResult> WriteTagAsync(
        TagId tag, double value, string requestedBy, CancellationToken ct = default)
        => (await WriteTagsAsync([new TagWriteItem(tag, value)], requestedBy, ct))[0];

    private async Task DispatchNetworkWritesAsync(
        Dictionary<int, List<PendingWriteEntry>> pendingByPipe,
        TagWriteResult?[] results, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_writeTimeout);

        var completions = new List<Task>();
        foreach (var (pipeIndex, entries) in pendingByPipe)
        {
            var write = new PendingWrite(entries, results);
            try
            {
                await _writePipes![pipeIndex].Writer.WriteAsync(write, timeoutCts.Token);
                completions.Add(write.Completion.Task);
            }
            catch (OperationCanceledException)
            {
                write.CompleteAll(new TagWriteResult(TagWriteStatus.Timeout,
                    "очередь записи канала переполнена"));
            }
        }

        try
        {
            await Task.WhenAll(completions).WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            // таймаут на пакет: неисполненное помечаем, исполненное остаётся как есть
            foreach (var entries in pendingByPipe.Values)
                foreach (var entry in entries)
                    results[entry.TargetIndex] ??= new TagWriteResult(
                        TagWriteStatus.Timeout, "истёк таймаут исполнения записи");
        }
    }

    /// <summary>Исполнение подпакета в цикле канала. Исключение драйвера =
    /// потеря связи: тот же путь, что ошибка опроса (теги Bad, reconnect).</summary>
    private async Task ExecuteWriteAsync(
        PendingWrite write, DevicePollState[] states, CancellationToken ct)
    {
        foreach (var group in write.Entries.GroupBy(e => e.Device))
        {
            var state = states.First(s => s.Device.Id == group.Key);

            TagWriteResult[] deviceResults;
            if (state.Driver is null)
            {
                deviceResults = group.Select(_ => new TagWriteResult(
                    TagWriteStatus.DeviceOffline, "устройство отключено")).ToArray();
            }
            else if (state.Driver is not IWritableDeviceDriver writable)
            {
                deviceResults = group.Select(_ => new TagWriteResult(
                    TagWriteStatus.WriteNotSupported,
                    $"драйвер '{state.Device.DriverName}' не поддерживает запись")).ToArray();
            }
            else
            {
                try
                {
                    deviceResults = await writable.WriteAsync(
                        group.Select(e => e.Item).ToArray(), ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    OnDeviceError?.Invoke(state.Device, ex);
                    MarkTagsBad(state.Tags);
                    await DropDriverAsync(state);
                    deviceResults = group.Select(_ => new TagWriteResult(
                        TagWriteStatus.Failed, ex.Message)).ToArray();
                }
            }

            foreach (var (entry, result) in group.Zip(deviceResults))
                write.Results[entry.TargetIndex] = result;
        }
        write.Completion.TrySetResult();
    }

    private void SavePersistent(string tagName, double value)
    {
        try
        {
            _persistence?.Save(tagName, value);
        }
        catch (Exception ex)
        {
            // значение в таблице уже записано; потеря персистентности — предупреждение
            OnWriteWarning?.Invoke(
                $"[запись] персистентное значение '{tagName}' не сохранено на диск: {ex.Message}");
        }
    }

    private void AuditWrite(string batchId, string user, IReadOnlyList<TagWriteItem> items,
        string[] tagNames, double?[] oldValues, TagWriteResult?[] results)
    {
        if (_audit is null)
            return;

        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var entries = new List<AuditEntry>(items.Count);
        for (int i = 0; i < items.Count; i++)
        {
            var result = results[i] ?? new TagWriteResult(TagWriteStatus.Failed, "не исполнено");
            entries.Add(new AuditEntry(
                TimestampUtcMs: now,
                User: user,
                Action: "tag-write",
                Target: tagNames[i] ?? items[i].TagId.ToString(),
                OldValue: oldValues[i],
                NewValue: items[i].Value,
                Result: result.Status.ToString(),
                Detail: result.Error,
                BatchId: batchId));
        }
        _audit.Append(entries);
    }

    private static IReadOnlyList<TagWriteResult> FinalizeResults(TagWriteResult?[] results)
        => results.Select(r => r ?? new TagWriteResult(TagWriteStatus.Failed, "не исполнено"))
            .ToArray();

    /// <summary>Элемент очереди записи канала: подпакет одного канала.
    /// Results — общий массив всей команды, заполняется по TargetIndex.</summary>
    private sealed class PendingWrite(List<PendingWriteEntry> entries, TagWriteResult?[] results)
    {
        public List<PendingWriteEntry> Entries { get; } = entries;
        public TagWriteResult?[] Results { get; } = results;
        public TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void CompleteAll(TagWriteResult result)
        {
            foreach (var entry in Entries)
                Results[entry.TargetIndex] ??= result;
            Completion.TrySetResult();
        }
    }

    private readonly record struct PendingWriteEntry(
        DeviceId Device, DriverWriteItem Item, int TargetIndex);
}
