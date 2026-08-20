using SCADA.Alarms;
using SCADA.Core.Alarms;
using SCADA.Core.Channels;
using SCADA.Core.Devices;
using SCADA.Core.Tags;
using SCADA.Core.Users;
using SCADA.Runtime.Alarms;
using SCADA.Runtime.Audit;
using SCADA.Runtime.Configuration;
using SCADA.Runtime.Polling;
using SCADA.Runtime.Runtime;
using SCADA.Runtime.TagTable;
using SCADA.Runtime.Users;
using TagTableImpl = SCADA.Runtime.TagTable.TagTable;

namespace SCADA.Runtime.Tests;

/// <summary>
/// Проверки прав в ядре (docs/users-plan.md §5, §7): операторская запись
/// требует Operate, квитирование — AckAlarms, отказ попадает в аудит,
/// а в журнал идёт логин сессии, а не строка от вызывающего.
/// </summary>
public class AccessControlTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    private readonly FakeTime _time = new(new DateTimeOffset(2026, 8, 20, 8, 0, 0, TimeSpan.Zero));
    private readonly TagTableImpl _tagTable = new(4);
    private readonly CollectingAuditJournal _audit = new();
    private readonly List<IDisposable> _journals = [];

    private static readonly UsersConfiguration Users = new()
    {
        IsConfigured = true,
        Roles =
        [
            new RoleDefinition { Name = "Наблюдатель", Permissions = ["View"] },
            new RoleDefinition { Name = "Оператор", Permissions = ["Operate"] },
            new RoleDefinition { Name = "Диспетчер", Permissions = ["AckAlarms"] }
        ],
        MinPasswordLength = 6,
        IdleTimeoutMinutes = 10
    };

    // internal-устройство: запись идёт прямо в таблицу, движок запускать не нужно
    private static readonly ProjectConfiguration Project = new()
    {
        Name = "Access",
        Channels = [new ChannelDefinition { Id = new ChannelId(0), Name = "Local", ChannelType = "none" }],
        Devices =
        [
            new DeviceDefinition { Id = new DeviceId(0), Name = "Local",
                DriverName = "internal", ChannelId = new ChannelId(0) }
        ],
        Tags =
        [
            new TagDefinition { Id = new TagId(0), Name = "Насос1.Задание",
                DataType = TagDataType.Analog, DeviceId = new DeviceId(0), IsWritable = true }
        ]
    };

    private static readonly AlarmRule Rule = new()
    {
        Name = "R1", Type = AlarmType.Threshold, TagName = "Насос1.Задание",
        Limits = [new ThresholdLimit { Kind = ThresholdKind.Hi, Value = 80 }],
        Description = "Превышение задания"
    };

    public AccessControlTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var journal in _journals)
            journal.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private (LocalRuntimeClient Client, SessionService Sessions, UserStore Store,
        AlarmEngine Alarms, SqliteEventJournal Events) Build(AuthMode mode = AuthMode.Full)
    {
        var store = new UserStore(_dir, Users);
        var sessions = new SessionService(store, Users, mode, _time);
        var access = new SessionAccessControl(sessions);

        var engine = new PollingEngine(Project, _tagTable, TimeSpan.FromMilliseconds(50),
            audit: _audit);
        var alarms = new AlarmEngine(new AlarmConfiguration { Rules = [Rule] },
            [new PreparedAlarmRule { Rule = Rule, TagIndices = [0] }], _tagTable, Project.Tags);
        var events = new SqliteEventJournal(Path.Combine(_dir, $"{Guid.NewGuid()}.db"));
        _journals.Add(events);

        var client = new LocalRuntimeClient(_tagTable, null, null, alarms, events,
            new AlarmChangeBroadcaster(), engine, null, access, _audit);
        return (client, sessions, store, alarms, events);
    }

    [Fact]
    public async Task Write_WithoutOperate_DeniedAndAudited()
    {
        var (client, sessions, store, _, _) = Build();
        store.AddUser("ivanov", "password1", ["Наблюдатель"]);
        sessions.Authenticate("ivanov", "password1");

        var result = await client.WriteTagAsync(new TagId(0), 42, "кто-угодно");

        Assert.Equal(TagWriteStatus.Denied, result.Status);
        Assert.Contains(SystemPermissions.Operate, result.Error);
        // значение не изменилось: отказ до исполнения
        Assert.NotEqual(42, _tagTable.Read(new TagId(0)).Value);

        var entry = Assert.Single(_audit.Entries);
        Assert.Equal("ivanov", entry.User); // логин сессии, а не аргумент вызова
        Assert.Equal("tag-write", entry.Action);
        Assert.Equal("Denied", entry.Result);
        Assert.Contains(SystemPermissions.Operate, entry.Detail);
    }

    [Fact]
    public async Task Write_WithOperate_ExecutedAndAuditedUnderSessionLogin()
    {
        var (client, sessions, store, _, _) = Build();
        store.AddUser("Petrov", "password1", ["Оператор"]);
        sessions.Authenticate("petrov", "password1");

        var result = await client.WriteTagAsync(new TagId(0), 42, "os-user@station");

        Assert.Equal(TagWriteStatus.Ok, result.Status);
        Assert.Equal(42, _tagTable.Read(new TagId(0)).Value);

        var entry = Assert.Single(_audit.Entries);
        Assert.Equal("Petrov", entry.User); // заглушка из аргумента заменена
        Assert.Equal("Ok", entry.Result);
    }

    [Fact]
    public async Task Write_WithoutSession_Denied()
    {
        var (client, _, _, _, _) = Build(); // AuthMode.Full, никто не вошёл

        var result = await client.WriteTagAsync(new TagId(0), 42, "кто-угодно");

        Assert.Equal(TagWriteStatus.Denied, result.Status);
        Assert.Equal(SessionAccessControl.NoSessionLogin, Assert.Single(_audit.Entries).User);
    }

    [Fact]
    public async Task Write_OnLockedSession_Denied_ThenAllowedAfterUnlock()
    {
        var (client, sessions, store, _, _) = Build();
        store.AddUser("ivanov", "password1", ["Оператор"]);
        sessions.Authenticate("ivanov", "password1");

        _time.Advance(TimeSpan.FromMinutes(11));
        sessions.Evaluate(); // автоблокировка по бездействию

        Assert.Equal(TagWriteStatus.Denied,
            (await client.WriteTagAsync(new TagId(0), 42, "ivanov")).Status);

        Assert.True(sessions.Unlock("password1"));
        Assert.Equal(TagWriteStatus.Ok,
            (await client.WriteTagAsync(new TagId(0), 42, "ivanov")).Status);
    }

    [Fact]
    public async Task Write_CountsAsActivity_PostponesLock()
    {
        var (client, sessions, store, _, _) = Build();
        store.AddUser("ivanov", "password1", ["Оператор"]);
        var session = sessions.Authenticate("ivanov", "password1")!;

        _time.Advance(TimeSpan.FromMinutes(9));
        await client.WriteTagAsync(new TagId(0), 42, "ivanov"); // работа идёт
        _time.Advance(TimeSpan.FromMinutes(9));
        sessions.Evaluate();

        Assert.False(session.IsLocked);
    }

    [Fact]
    public async Task Acknowledge_WithoutRight_NotAcknowledgedAndAudited()
    {
        var (client, sessions, store, alarms, events) = Build();
        store.AddUser("ivanov", "password1", ["Оператор"]); // Operate есть, AckAlarms нет
        sessions.Authenticate("ivanov", "password1");
        _tagTable.Write(new TagId(0), new TagValue(85, 0, Quality.Good));
        alarms.EvaluateTag(new TagId(0), 1000);

        await client.AcknowledgeAlarmsAsync(["R1"], "кто-угодно");

        Assert.Empty(events.Query(new AlarmHistoryQuery(0, long.MaxValue)));
        Assert.Equal(AlarmState.ActiveUnack,
            (await client.GetActiveAlarmsAsync(new AlarmFilter())).Single().State);

        var entry = Assert.Single(_audit.Entries);
        Assert.Equal("alarm-ack", entry.Action);
        Assert.Equal("R1", entry.Target);
        Assert.Contains(SystemPermissions.AckAlarms, entry.Detail);
    }

    [Fact]
    public async Task Acknowledge_WithRight_UsesSessionLogin()
    {
        var (client, sessions, store, alarms, events) = Build();
        store.AddUser("dispatcher", "password1", ["Диспетчер"]);
        sessions.Authenticate("dispatcher", "password1");
        _tagTable.Write(new TagId(0), new TagValue(85, 0, Quality.Good));
        alarms.EvaluateTag(new TagId(0), 1000);

        await client.AcknowledgeAlarmsAsync(["R1"], "os-user@station", "принято");

        var ev = Assert.Single(events.Query(new AlarmHistoryQuery(0, long.MaxValue)));
        Assert.Equal(AlarmEventType.Acknowledged, ev.Type);
        Assert.Equal("dispatcher", ev.AcknowledgedBy); // AcknowledgedBy получил смысл
        Assert.Empty(_audit.Entries); // отказов не было
    }

    [Fact]
    public async Task LocalMode_AllowsEverything_BehaviourUnchanged()
    {
        var (client, sessions, _, alarms, events) = Build(AuthMode.Local);
        _tagTable.Write(new TagId(0), new TagValue(85, 0, Quality.Good));
        alarms.EvaluateTag(new TagId(0), 1000);

        var write = await client.WriteTagAsync(new TagId(0), 42, "неважно");
        await client.AcknowledgeAlarmsAsync(["R1"], "неважно");

        Assert.Equal(TagWriteStatus.Ok, write.Status);
        Assert.Single(events.Query(new AlarmHistoryQuery(0, long.MaxValue)));
        // в аудите — логин локальной сессии (заглушка "os-user@station" ушла)
        Assert.Equal(sessions.Current!.Login, _audit.Entries[0].User);
    }

    [Fact]
    public async Task WithoutAccessControl_BehaviourUnchanged()
    {
        var engine = new PollingEngine(Project, _tagTable, TimeSpan.FromMilliseconds(50),
            audit: _audit);
        var client = new LocalRuntimeClient(_tagTable, pollingEngine: engine);

        var result = await client.WriteTagAsync(new TagId(0), 42, "os-user@station");

        Assert.Equal(TagWriteStatus.Ok, result.Status);
        Assert.Equal("os-user@station", Assert.Single(_audit.Entries).User);
    }

    private sealed class CollectingAuditJournal : IAuditJournal
    {
        public List<AuditEntry> Entries { get; } = [];
        public void Append(IReadOnlyList<AuditEntry> entries) => Entries.AddRange(entries);
    }
}
