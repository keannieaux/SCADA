using Microsoft.Extensions.Hosting;
using SCADA.Alarms;
using SCADA.Core.Tags;

namespace SCADA.Runtime.Alarms;

/// <summary>
/// Конвейер сигнализации (docs/M5-plan.md §7.2): отслеживает эпохи TagTable,
/// пересчитывает затронутые правила, пишет события в журнал, рассылает
/// изменения подписчикам, периодически запускает retention-чистку.
/// </summary>
public sealed class AlarmPipeline : BackgroundService
{
    private readonly ITagTable _tagTable;
    private readonly IAlarmEngine _engine;
    private readonly IEventJournal _journal;
    private readonly AlarmChangeBroadcaster _broadcaster;
    private readonly AlarmPipelineOptions _options;
    private readonly JournalOptions _journalOptions;
    private readonly Action<string>? _onWarning;

    public AlarmPipeline(
        ITagTable tagTable,
        IAlarmEngine engine,
        IEventJournal journal,
        AlarmChangeBroadcaster broadcaster,
        AlarmPipelineOptions options,
        JournalOptions journalOptions,
        Action<string>? onWarning = null)
    {
        _tagTable = tagTable;
        _engine = engine;
        _journal = journal;
        _broadcaster = broadcaster;
        _options = options;
        _journalOptions = journalOptions;
        _onWarning = onWarning;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        long epoch = _tagTable.CurrentEpoch;
        var buffer = new TagId[4096];
        long nextRetentionMs = NowUtcMs()
            + (long)_options.RetentionCheckIntervalMinutes * 60_000;
        bool firstRun = true;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.TickIntervalMs, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            long now = NowUtcMs();
            var events = new List<AlarmEvent>();

            // первый проход: свести восстановленное из журнала состояние
            // с фактическими значениями тегов (§7.3)
            if (firstRun)
            {
                events.AddRange(_engine.EvaluateAll(now));
                firstRun = false;
            }

            int changed = _tagTable.GetChangedSince(epoch, buffer);
            epoch = _tagTable.CurrentEpoch;
            if (changed == buffer.Length)
            {
                // буфер переполнен — часть изменений потеряна, пересчитываем всё
                events.AddRange(_engine.EvaluateAll(now));
            }
            else
            {
                for (int i = 0; i < changed; i++)
                    events.AddRange(_engine.EvaluateTag(buffer[i], now));
            }

            events.AddRange(_engine.Tick(now));

            Publish(events);

            if (now >= nextRetentionMs)
            {
                long cutoff = now - (long)_journalOptions.RetentionDays * 86_400_000;
                int deleted = _journal.DeleteOlderThan(cutoff);
                if (deleted > 0)
                    _onWarning?.Invoke($"[журнал] retention: удалено {deleted} событий");
                nextRetentionMs = now + (long)_options.RetentionCheckIntervalMinutes * 60_000;
            }
        }
    }

    private void Publish(List<AlarmEvent> events)
    {
        if (events.Count == 0)
            return;

        _journal.Append(events);

        foreach (var ev in events)
        {
            var view = _engine.GetAlarm(ev.RuleName);
            if (view is null)
                continue;

            var kind = ev.Type switch
            {
                AlarmEventType.Active => AlarmChangeKind.Activated,
                AlarmEventType.Normal => AlarmChangeKind.Normalized,
                AlarmEventType.Escalated => AlarmChangeKind.Activated, // та же строка баннера, новый severity
                _ => AlarmChangeKind.Acknowledged
            };
            _broadcaster.Publish(new AlarmChange(kind, view));
        }
    }

    private static long NowUtcMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
