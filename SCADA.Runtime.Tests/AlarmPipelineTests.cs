using SCADA.Alarms;
using SCADA.Core.Alarms;
using SCADA.Core.Devices;
using SCADA.Core.Tags;
using SCADA.Runtime.Alarms;
using SCADA.Runtime.Runtime;
using TagTableImpl = SCADA.Runtime.TagTable.TagTable;

namespace SCADA.Runtime.Tests;

/// <summary>
/// Конвейер сигнализации поверх реальной TagTable (docs/M5-plan.md §7.2):
/// пересчёт по эпохам, запись в журнал, рассылка изменений, retention.
/// </summary>
public class AlarmPipelineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    private readonly List<IDisposable> _journals = new();

    public AlarmPipelineTests() => Directory.CreateDirectory(_dir);
    public void Dispose()
    {
        foreach (var journal in _journals)
            journal.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private readonly TagTableImpl _tagTable = new(1);
    private readonly List<TagDefinition> _tagDefs = new()
    {
        new TagDefinition { Id = new TagId(0), Name = "Boiler1.Temp",
            DataType = TagDataType.Analog, DeviceId = new DeviceId(0), Units = "°C" }
    };

    private static AlarmRule Rule() => new()
    {
        Name = "R1",
        Type = AlarmType.Threshold,
        TagName = "Boiler1.Temp",
        Limits = [new ThresholdLimit { Kind = ThresholdKind.Hi, Value = 80 }],
        Area = "Котельная",
        Description = "Температура котла"
    };

    private (AlarmEngine engine, SqliteEventJournal journal, AlarmChangeBroadcaster broadcaster)
        CreateEngine()
    {
        var rule = Rule();
        var config = new AlarmConfiguration { Rules = [rule] };
        var engine = new AlarmEngine(config,
            [new PreparedAlarmRule { Rule = rule, TagIndices = [0] }], _tagTable, _tagDefs);
        var journal = new SqliteEventJournal(Path.Combine(_dir, $"{Guid.NewGuid()}.db"));
        _journals.Add(journal);
        return (engine, journal, new AlarmChangeBroadcaster());
    }

    private AlarmPipeline Pipeline(AlarmEngine engine, SqliteEventJournal journal,
        AlarmChangeBroadcaster broadcaster, AlarmPipelineOptions? options = null,
        JournalOptions? journalOptions = null) =>
        new(_tagTable, engine, journal, broadcaster,
            options ?? new AlarmPipelineOptions { TickIntervalMs = 20 },
            journalOptions ?? new JournalOptions());

    private void SetTag(double value, Quality quality = Quality.Good) =>
        _tagTable.Write(new TagId(0),
            new TagValue(value, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), quality));

    // таймаут с запасом: при параллельном прогоне всех тестовых сборок
    // планировщик и ThreadPool заметно отстают от TickIntervalMs
    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 10_000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        while (!condition())
        {
            if (cts.IsCancellationRequested)
                throw new TimeoutException("условие не наступило за отведённое время");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task TagCrossingLimit_EventInJournalAndBroadcast()
    {
        var (engine, journal, broadcaster) = CreateEngine();
        using var pipeline = Pipeline(engine, journal, broadcaster);
        await pipeline.StartAsync(CancellationToken.None);

        var received = new List<AlarmChange>();
        await using var enumerator = broadcaster.Subscribe(CancellationToken.None).GetAsyncEnumerator();
        var readNext = enumerator.MoveNextAsync().AsTask();

        SetTag(85);
        await WaitForAsync(() =>
            journal.Query(new AlarmHistoryQuery(0, long.MaxValue)).Count > 0);

        var ev = journal.Query(new AlarmHistoryQuery(0, long.MaxValue)).Single();
        Assert.Equal(AlarmEventType.Active, ev.Type);
        Assert.Equal("R1", ev.RuleName);
        Assert.True(ev.Id.Value > 0); // первичный ключ присвоен журналом

        Assert.True(await readNext);
        Assert.Equal(AlarmChangeKind.Activated, enumerator.Current.Kind);
        Assert.Equal(AlarmState.ActiveUnack, enumerator.Current.Alarm.State);

        await pipeline.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task FirstRun_EvaluateAll_CatchesAlreadyCrossedValue()
    {
        SetTag(85); // значение зашло за уставку ДО старта конвейера
        var (engine, journal, broadcaster) = CreateEngine();
        using var pipeline = Pipeline(engine, journal, broadcaster);

        await pipeline.StartAsync(CancellationToken.None);
        await WaitForAsync(() => engine.GetActive(new AlarmFilter()).Count > 0);
        await pipeline.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ChangedMoreThanBuffer_StillEvaluates()
    {
        // Регрессия на контракт GetChangedSince: возвращается ПОЛНОЕ число
        // изменившихся тегов, а буфер конвейера — 4096. Раньше переполнение
        // ловилось сравнением на равенство, и при 5000 изменений цикл уходил
        // за границу массива: поток конвейера падал, аварии переставали
        // считаться совсем. Момент как раз тот, когда это нужнее всего —
        // первый опрос, восстановление связи, запись рецепта.
        var table = new TagTableImpl(6000);
        var rule = Rule();
        var engine = new AlarmEngine(new AlarmConfiguration { Rules = [rule] },
            [new PreparedAlarmRule { Rule = rule, TagIndices = [0] }], table, _tagDefs);
        var journal = new SqliteEventJournal(Path.Combine(_dir, $"{Guid.NewGuid()}.db"));
        _journals.Add(journal);

        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        table.Write(new TagId(0), new TagValue(85, now, Quality.Good)); // за уставкой

        using var pipeline = new AlarmPipeline(table, engine, journal,
            new AlarmChangeBroadcaster(),
            new AlarmPipelineOptions { TickIntervalMs = 20 }, new JournalOptions());

        await pipeline.StartAsync(CancellationToken.None);

        // первый тик: авария поднялась обычным путём (EvaluateAll на старте)
        await WaitForAsync(() =>
            journal.Query(new AlarmHistoryQuery(0, long.MaxValue)).Count > 0);

        // а теперь массовое изменение НА ХОДУ: 5001 тег за один тик при
        // буфере 4096 — переполнение, конвейер обязан пересчитать всё
        // и увидеть возврат в норму
        now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        table.Write(new TagId(0), new TagValue(10, now, Quality.Good));
        for (int i = 1; i <= 5000; i++)
            table.Write(new TagId(i), new TagValue(i, now, Quality.Good));

        await WaitForAsync(() =>
            journal.Query(new AlarmHistoryQuery(0, long.MaxValue)).Count > 1);
        await pipeline.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Retention_OldEventsDeletedOnSchedule()
    {
        var (engine, journal, broadcaster) = CreateEngine();
        long old = DateTimeOffset.UtcNow.AddDays(-400).ToUnixTimeMilliseconds();
        journal.Append(new[]
        {
            new AlarmEvent(default, old, "R1", ThresholdKind.Hi, AlarmEventType.Active,
                "старое", AlarmSeverity.High, "Котельная", Array.Empty<AlarmTagSnapshot>())
        });

        using var pipeline = Pipeline(engine, journal, broadcaster,
            options: new AlarmPipelineOptions
            {
                TickIntervalMs = 20,
                RetentionCheckIntervalMinutes = 0 // чистка на каждом тике
            });

        await pipeline.StartAsync(CancellationToken.None);
        await WaitForAsync(() =>
            journal.Query(new AlarmHistoryQuery(0, long.MaxValue)).Count == 0);
        await pipeline.StopAsync(CancellationToken.None);
    }
}

/// <summary>
/// Клиентский API аварий (docs/M5-plan.md §9): баннер, история, квитирование,
/// подписка. In-process реализация контракта IRuntimeClient.
/// </summary>
public class LocalRuntimeClientAlarmTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    private readonly List<IDisposable> _journals = new();

    public LocalRuntimeClientAlarmTests() => Directory.CreateDirectory(_dir);
    public void Dispose()
    {
        foreach (var journal in _journals)
            journal.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private readonly TagTableImpl _tagTable = new(1);
    private readonly List<TagDefinition> _tagDefs = new()
    {
        new TagDefinition { Id = new TagId(0), Name = "Boiler1.Temp",
            DataType = TagDataType.Analog, DeviceId = new DeviceId(0) }
    };

    private (LocalRuntimeClient client, AlarmEngine engine, SqliteEventJournal journal,
        AlarmChangeBroadcaster broadcaster) CreateClient()
    {
        var rule = new AlarmRule
        {
            Name = "R1", Type = AlarmType.Threshold, TagName = "Boiler1.Temp",
            Limits = [new ThresholdLimit { Kind = ThresholdKind.Hi, Value = 80 }],
            Description = "Температура котла"
        };
        var engine = new AlarmEngine(new AlarmConfiguration { Rules = [rule] },
            [new PreparedAlarmRule { Rule = rule, TagIndices = [0] }], _tagTable, _tagDefs);
        var journal = new SqliteEventJournal(Path.Combine(_dir, $"{Guid.NewGuid()}.db"));
        _journals.Add(journal);
        var broadcaster = new AlarmChangeBroadcaster();
        var client = new LocalRuntimeClient(_tagTable, null, null, engine, journal, broadcaster);
        return (client, engine, journal, broadcaster);
    }

    private static readonly AlarmFilter All = new();

    [Fact]
    public async Task AlarmsDisabled_EmptyResultsNotFailures()
    {
        var client = new LocalRuntimeClient(_tagTable);

        Assert.Empty(await client.GetActiveAlarmsAsync(All));
        Assert.Empty(await client.GetAlarmHistoryAsync(new AlarmHistoryQuery(0, long.MaxValue)));
        await client.AcknowledgeAlarmsAsync(
            ["R1"], "op@ARM1"); // не падает
    }

    [Fact]
    public async Task Acknowledge_WritesJournalEventAndBroadcasts()
    {
        var (client, engine, journal, broadcaster) = CreateClient();
        _tagTable.Write(new TagId(0), new TagValue(85, 0, Quality.Good));
        engine.EvaluateTag(new TagId(0), 1000);

        var active = await client.GetActiveAlarmsAsync(All);
        var alarm = Assert.Single(active);
        Assert.Equal(AlarmState.ActiveUnack, alarm.State);

        var received = broadcaster.Subscribe(CancellationToken.None).GetAsyncEnumerator();
        var readNext = received.MoveNextAsync().AsTask();

        await client.AcknowledgeAlarmsAsync(
            ["R1"], "op@ARM1", "принято");

        // событие в журнале с пользователем и комментарием
        var ev = Assert.Single(journal.Query(new AlarmHistoryQuery(0, long.MaxValue)));
        Assert.Equal(AlarmEventType.Acknowledged, ev.Type);
        Assert.Equal("op@ARM1", ev.AcknowledgedBy);
        Assert.Equal("принято", ev.AckComment);

        // рассылка и новое состояние
        Assert.True(await readNext);
        Assert.Equal(AlarmChangeKind.Acknowledged, received.Current.Kind);
        Assert.Equal(AlarmState.ActiveAck,
            (await client.GetActiveAlarmsAsync(All)).Single().State);
    }

    [Fact]
    public async Task Acknowledge_GroupList_AcksAll()
    {
        var (client, engine, journal, _) = CreateClient();
        _tagTable.Write(new TagId(0), new TagValue(85, 0, Quality.Good));
        engine.EvaluateTag(new TagId(0), 1000);

        await client.AcknowledgeAlarmsAsync(
            ["R1", "NO_SUCH"], // несуществующее молча пропускается
            "op@ARM1");

        Assert.Single(journal.Query(new AlarmHistoryQuery(0, long.MaxValue)));
        Assert.Equal(AlarmState.ActiveAck,
            (await client.GetActiveAlarmsAsync(All)).Single().State);
    }

    [Fact]
    public async Task History_ComesFromJournal()
    {
        var (client, engine, journal, _) = CreateClient();
        _tagTable.Write(new TagId(0), new TagValue(85, 0, Quality.Good));
        journal.Append(engine.EvaluateTag(new TagId(0), 1000));

        var history = await client.GetAlarmHistoryAsync(new AlarmHistoryQuery(0, long.MaxValue));

        var ev = Assert.Single(history);
        Assert.Equal("R1", ev.RuleName);
        Assert.True(ev.Id.Value > 0);
    }
}
