using SCADA.Core.Channels;
using SCADA.Core.Devices;
using SCADA.Core.Schemes;
using SCADA.Core.Tags;
using SCADA.Core.Users;
using SCADA.Runtime.Configuration;
using SCADA.Runtime.TagTable;
using SCADA.Runtime.Users;
using TagTableImpl = SCADA.Runtime.TagTable.TagTable;

namespace SCADA.Runtime.Tests;

/// <summary>
/// Системные сессионные теги (docs/session-tags-concept.md §3): генерация
/// при загрузке проекта и заполнение по событиям сессии.
/// </summary>
public class SessionSystemTagTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    private readonly FakeTime _time = new(new DateTimeOffset(2026, 8, 20, 8, 0, 0, TimeSpan.Zero));

    public SessionSystemTagTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static readonly UsersConfiguration Users = new()
    {
        IsConfigured = true,
        Roles =
        [
            new RoleDefinition { Name = "Оператор", Permissions = ["Operate"] },
            new RoleDefinition { Name = "Технолог", Permissions = ["Уставки.Edit"] }
        ],
        MinPasswordLength = 6,
        IdleTimeoutMinutes = 10
    };

    private static ProjectConfiguration Project(bool withScheme = false) => new()
    {
        Name = "Session",
        Channels = [new ChannelDefinition { Id = new ChannelId(0), Name = "Ch", ChannelType = "none" }],
        Devices = [new DeviceDefinition { Id = new DeviceId(0), Name = "PLC", DriverName = "simulator", ChannelId = new ChannelId(0) }],
        Tags =
        [
            new TagDefinition { Id = new TagId(0), Name = "Насос1.Обороты",
                DataType = TagDataType.Analog, DeviceId = new DeviceId(0) }
        ],
        Users = Users,
        Schemes = withScheme
            ?
            [
                new Scheme
                {
                    Id = Guid.NewGuid(), Name = "Уставки",
                    RequiredRight = "Уставки.View",
                    Elements =
                    [
                        new SchemeElement
                        {
                            Id = Guid.NewGuid(), Kind = ElementKind.Rectangle,
                            X = 0, Y = 0, Width = 1, Height = 1,
                            RequiredRight = "Насосная.Control"
                        }
                    ]
                }
            ]
            : []
    };

    // --- генерация ---

    [Fact]
    public void Generator_CreatesUserStationAndSystemRightTags()
    {
        var config = Project();

        SessionTagGenerator.AppendSessionTags(config);

        var byName = config.Tags.ToDictionary(t => t.Name);
        Assert.Equal(TagDataType.String, byName[SessionSystemTags.UserName].DataType);
        Assert.Contains(SessionSystemTags.UserIsAuthenticated, byName.Keys);
        Assert.Contains(SessionSystemTags.UserIsLocked, byName.Keys);
        Assert.Contains(SessionSystemTags.StationName, byName.Keys);
        Assert.Contains(SessionSystemTags.StationIsConnected, byName.Keys);

        // системные права + права ролей проекта
        Assert.Contains(SessionSystemTags.RightTag(SystemPermissions.Operate), byName.Keys);
        Assert.Contains(SessionSystemTags.RightTag("Уставки.Edit"), byName.Keys);
    }

    [Fact]
    public void Generator_IncludesRightsUsedOnlyInSchemes()
    {
        // роль под право появится позже, а привязка @Right.X должна
        // компилироваться уже сейчас
        var config = Project(withScheme: true);

        SessionTagGenerator.AppendSessionTags(config);

        var names = config.Tags.Select(t => t.Name).ToHashSet();
        Assert.Contains(SessionSystemTags.RightTag("Насосная.Control"), names);
        Assert.Contains(SessionSystemTags.RightTag("Уставки.View"), names);
    }

    [Fact]
    public void Generator_TagsAreSessionScopedReadOnlyAndDeterministic()
    {
        var first = Project();
        var second = Project();

        SessionTagGenerator.AppendSessionTags(first);
        SessionTagGenerator.AppendSessionTags(second);

        foreach (var tag in first.Tags.Where(t => t.Origin == TagOrigin.Session))
        {
            Assert.Equal(TagScope.Session, tag.Scope);
            Assert.False(tag.IsWritable);   // логин со схемы не переписывают
            Assert.False(tag.IsArchived);
        }

        // один проект — одни Id: от порядка генерации зависит весь пакет
        Assert.Equal(
            first.Tags.Select(t => (t.Id.Value, t.Name)),
            second.Tags.Select(t => (t.Id.Value, t.Name)));
    }

    // --- заполнение ---

    private (SessionTagPublisher Publisher, SessionService Sessions, UserStore Store,
        ITagTable Tags, Dictionary<string, TagId> ByName) Build(AuthMode mode = AuthMode.Full)
    {
        var config = Project();
        SessionTagGenerator.AppendSessionTags(config);

        var byName = config.Tags.ToDictionary(t => t.Name, t => t.Id);
        var epochs = new EpochCounter();
        var shared = new TagTableImpl(config.Tags.Count, epochs);
        var tags = SessionTagRouter.Wrap(shared, config.Tags, epochs);

        var store = new UserStore(_dir, Users);
        var sessions = new SessionService(store, Users, mode, _time);
        var rights = config.Tags
            .Where(t => t.Name.StartsWith(SessionSystemTags.RightPrefix, StringComparison.Ordinal))
            .Select(t => t.Name[SessionSystemTags.RightPrefix.Length..])
            .ToArray();

        var publisher = new SessionTagPublisher(tags, sessions,
            name => byName.TryGetValue(name, out var id) ? id : null,
            "АРМ-1", rights);

        return (publisher, sessions, store, tags, byName);
    }

    private static double Number(ITagTable tags, Dictionary<string, TagId> byName, string name)
        => tags.Read(byName[name]).Value;

    private static string Text(ITagTable tags, Dictionary<string, TagId> byName, string name)
        => tags.ReadString(byName[name]).Text;

    [Fact]
    public void Publisher_NoSession_EmptyLoginAndNoRights()
    {
        var (publisher, _, _, tags, byName) = Build();
        using var _p = publisher;

        Assert.Equal("", Text(tags, byName, SessionSystemTags.UserName));
        Assert.Equal(0, Number(tags, byName, SessionSystemTags.UserIsAuthenticated));
        Assert.Equal(0, Number(tags, byName,
            SessionSystemTags.RightTag(SystemPermissions.Operate)));

        // станция известна и без входа
        Assert.Equal("АРМ-1", Text(tags, byName, SessionSystemTags.StationName));
        Assert.Equal(1, Number(tags, byName, SessionSystemTags.StationIsConnected));
    }

    [Fact]
    public void Publisher_Login_PublishesLoginAndRights()
    {
        var (publisher, sessions, store, tags, byName) = Build();
        using var _p = publisher;
        store.AddUser("Ivanov", "password1", ["Оператор"]);

        sessions.Authenticate("ivanov", "password1");

        Assert.Equal("Ivanov", Text(tags, byName, SessionSystemTags.UserName));
        Assert.Equal(1, Number(tags, byName, SessionSystemTags.UserIsAuthenticated));
        Assert.Equal(1, Number(tags, byName,
            SessionSystemTags.RightTag(SystemPermissions.Operate)));
        Assert.Equal(1, Number(tags, byName,
            SessionSystemTags.RightTag(SystemPermissions.View))); // базовое право
        Assert.Equal(0, Number(tags, byName, SessionSystemTags.RightTag("Уставки.Edit")));
    }

    [Fact]
    public void Publisher_Logout_ClearsLoginAndRights()
    {
        var (publisher, sessions, store, tags, byName) = Build();
        using var _p = publisher;
        store.AddUser("ivanov", "password1", ["Оператор"]);
        sessions.Authenticate("ivanov", "password1");

        sessions.Logout();

        Assert.Equal("", Text(tags, byName, SessionSystemTags.UserName));
        Assert.Equal(0, Number(tags, byName, SessionSystemTags.UserIsAuthenticated));
        // снятые права гасятся, а не залипают
        Assert.Equal(0, Number(tags, byName,
            SessionSystemTags.RightTag(SystemPermissions.Operate)));
    }

    [Fact]
    public void Publisher_IdleLock_KeepsLoginAndDropsRightsToView()
    {
        var (publisher, sessions, store, tags, byName) = Build();
        using var _p = publisher;
        store.AddUser("ivanov", "password1", ["Оператор"]);
        sessions.Authenticate("ivanov", "password1");

        _time.Advance(TimeSpan.FromMinutes(11));
        sessions.Evaluate();

        // человек тот же — логин остаётся, но управление недоступно
        Assert.Equal("ivanov", Text(tags, byName, SessionSystemTags.UserName));
        Assert.Equal(1, Number(tags, byName, SessionSystemTags.UserIsLocked));
        Assert.Equal(0, Number(tags, byName,
            SessionSystemTags.RightTag(SystemPermissions.Operate)));
        Assert.Equal(1, Number(tags, byName,
            SessionSystemTags.RightTag(SystemPermissions.View)));
    }

    [Fact]
    public void Publisher_LocalMode_PublishesBuiltInSessionAtStart()
    {
        var (publisher, sessions, _, tags, byName) = Build(AuthMode.Local);
        using var _p = publisher;

        Assert.Equal(sessions.Current!.Login, Text(tags, byName, SessionSystemTags.UserName));
        Assert.Equal(1, Number(tags, byName, SessionSystemTags.UserIsAuthenticated));
        // локальному администратору разрешено всё, включая проектные права
        Assert.Equal(1, Number(tags, byName, SessionSystemTags.RightTag("Уставки.Edit")));
    }

    [Fact]
    public void Publisher_WritesToSessionTableOnly()
    {
        var config = Project();
        SessionTagGenerator.AppendSessionTags(config);
        var byName = config.Tags.ToDictionary(t => t.Name, t => t.Id);
        var epochs = new EpochCounter();
        var shared = new TagTableImpl(config.Tags.Count, epochs);
        var tags = SessionTagRouter.Wrap(shared, config.Tags, epochs);

        var store = new UserStore(_dir, Users);
        var sessions = new SessionService(store, Users, AuthMode.Local, _time);
        using var publisher = new SessionTagPublisher(tags, sessions,
            name => byName.TryGetValue(name, out var id) ? id : null, "АРМ-1", []);

        // на сервере значения нет: слот общей таблицы остался нетронутым
        Assert.Equal(Quality.Bad,
            shared.Read(byName[SessionSystemTags.UserIsAuthenticated]).Quality);
        Assert.Equal(1, tags.Read(byName[SessionSystemTags.UserIsAuthenticated]).Value);
    }
}
