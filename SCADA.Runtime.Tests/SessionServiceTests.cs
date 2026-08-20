using SCADA.Core.Users;
using SCADA.Runtime.Users;

namespace SCADA.Runtime.Tests;

/// <summary>
/// Сессии и режимы аутентификации (docs/users-plan.md §6, §6.1): вход,
/// эффективные права, автоблокировка по бездействию, режим Local.
/// Часы подставляются — правила завершения обязаны проверяться без ожидания.
/// </summary>
public class SessionServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    private readonly FakeTime _time = new(new DateTimeOffset(2026, 8, 20, 8, 0, 0, TimeSpan.Zero));

    private static readonly UsersConfiguration Configuration = new()
    {
        IsConfigured = true,
        Roles =
        [
            // роль намеренно без View: просмотр обязан выдаваться сам (§2.1)
            new RoleDefinition { Name = "Оператор", Permissions = ["Operate", "AckAlarms"] },
            new RoleDefinition { Name = "Технолог", Permissions = ["Уставки.Edit"] },
            new RoleDefinition { Name = "Администратор",
                Permissions = ["View", "Operate", "AckAlarms", "ManageUsers"] }
        ],
        MinPasswordLength = 6,
        IdleTimeoutMinutes = 10
    };

    public SessionServiceTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private (SessionService Sessions, UserStore Users) Build(
        AuthMode mode = AuthMode.Full, UsersConfiguration? configuration = null)
    {
        var config = configuration ?? Configuration;
        var users = new UserStore(_dir, config);
        return (new SessionService(users, config, mode, _time), users);
    }

    [Fact]
    public void Authenticate_ValidCredentials_UnionsPermissionsOfAllRoles()
    {
        var (sessions, users) = Build();
        users.AddUser("Ivanov", "password1", ["Оператор", "Технолог"]);

        var session = sessions.Authenticate("ivanov", "password1"); // регистр не важен

        Assert.NotNull(session);
        Assert.Equal("Ivanov", session.Login); // в аудит идёт логин как заведён
        Assert.True(session.HasPermission(SystemPermissions.Operate));
        Assert.True(session.HasPermission("Уставки.Edit"));
        Assert.True(session.HasPermission(SystemPermissions.View)); // базовое право
        Assert.False(session.HasPermission(SystemPermissions.ManageUsers));
        Assert.Equal(session, sessions.Current);
    }

    [Fact]
    public void Authenticate_WrongPasswordOrUnknownLogin_ReturnsNull()
    {
        var (sessions, users) = Build();
        users.AddUser("ivanov", "password1", ["Оператор"]);

        Assert.Null(sessions.Authenticate("ivanov", "password2"));
        Assert.Null(sessions.Authenticate("petrov", "password1"));
        Assert.Null(sessions.Current);
    }

    [Fact]
    public void Authenticate_UnknownRoleInUsersFile_DoesNotBreakLogin()
    {
        // роль переименовали в проекте, users.json остался со старым именем
        var (sessions, users) = Build();
        users.AddUser("ivanov", "password1", ["Технолог"]);
        var withoutRole = new UsersConfiguration
        {
            IsConfigured = true,
            Roles = [new RoleDefinition { Name = "Оператор", Permissions = ["Operate"] }],
            IdleTimeoutMinutes = 10
        };
        var narrowed = new SessionService(users, withoutRole, AuthMode.Full, _time);

        var session = narrowed.Authenticate("ivanov", "password1");

        Assert.NotNull(session);
        Assert.True(session.HasPermission(SystemPermissions.View));
        Assert.False(session.HasPermission("Уставки.Edit"));
    }

    [Fact]
    public void Idle_LocksSession_LeavingViewOnly()
    {
        var (sessions, users) = Build();
        users.AddUser("ivanov", "password1", ["Оператор"]);
        var ends = new List<SessionEndedEventArgs>();
        sessions.SessionEnded += ends.Add;
        var session = sessions.Authenticate("ivanov", "password1")!;

        _time.Advance(TimeSpan.FromMinutes(9));
        sessions.Evaluate();
        Assert.False(session.IsLocked);

        _time.Advance(TimeSpan.FromMinutes(2)); // 11 минут бездействия
        sessions.Evaluate();

        Assert.True(session.IsLocked);
        Assert.Equal(SessionEndReason.Idle, ends.Single().Reason);
        Assert.Equal(SessionEndAction.Lock, ends.Single().Action);
        // сессия жива, наблюдение продолжается, управление — нет
        Assert.Same(session, sessions.Current);
        Assert.True(session.HasPermission(SystemPermissions.View));
        Assert.False(session.HasPermission(SystemPermissions.Operate));

        // повторные тики не плодят событий
        _time.Advance(TimeSpan.FromMinutes(30));
        sessions.Evaluate();
        Assert.Single(ends);
    }

    [Fact]
    public void Touch_PostponesLock_ButNotOnLockedSession()
    {
        var (sessions, users) = Build();
        users.AddUser("ivanov", "password1", ["Оператор"]);
        var session = sessions.Authenticate("ivanov", "password1")!;

        _time.Advance(TimeSpan.FromMinutes(9));
        sessions.Touch();
        _time.Advance(TimeSpan.FromMinutes(9));
        sessions.Evaluate();
        Assert.False(session.IsLocked);

        _time.Advance(TimeSpan.FromMinutes(2));
        sessions.Evaluate();
        Assert.True(session.IsLocked);

        // движение мыши не оживляет заблокированную сессию
        sessions.Touch();
        Assert.True(session.IsLocked);
    }

    [Fact]
    public void IdleTimeoutZero_NeverLocks()
    {
        var config = new UsersConfiguration
        {
            IsConfigured = true,
            Roles = Configuration.Roles,
            MinPasswordLength = 6,
            IdleTimeoutMinutes = 0 // автоблокировка выключена
        };
        var (sessions, users) = Build(configuration: config);
        users.AddUser("ivanov", "password1", ["Оператор"]);
        var session = sessions.Authenticate("ivanov", "password1")!;

        _time.Advance(TimeSpan.FromDays(3));
        sessions.Evaluate();

        Assert.False(session.IsLocked);
        Assert.True(session.HasPermission(SystemPermissions.Operate));
    }

    [Fact]
    public void Unlock_OnlyWithOwnPassword()
    {
        var (sessions, users) = Build();
        users.AddUser("ivanov", "password1", ["Оператор"]);
        users.AddUser("petrov", "password2", ["Администратор"]);
        var session = sessions.Authenticate("ivanov", "password1")!;
        _time.Advance(TimeSpan.FromMinutes(11));
        sessions.Evaluate();

        Assert.False(sessions.Unlock("password2")); // пароль сменщика не подходит
        Assert.True(session.IsLocked);

        Assert.True(sessions.Unlock("password1"));
        Assert.False(session.IsLocked);
        Assert.True(session.HasPermission(SystemPermissions.Operate));

        // разблокировка отодвигает автоблокировку от момента ввода пароля
        _time.Advance(TimeSpan.FromMinutes(9));
        sessions.Evaluate();
        Assert.False(session.IsLocked);
    }

    [Fact]
    public void Logout_EndsSessionWithManualReason()
    {
        var (sessions, users) = Build();
        users.AddUser("ivanov", "password1", ["Оператор"]);
        var ends = new List<SessionEndedEventArgs>();
        sessions.SessionEnded += ends.Add;
        sessions.Authenticate("ivanov", "password1");

        sessions.Logout();

        Assert.Null(sessions.Current);
        Assert.Equal(SessionEndReason.Manual, ends.Single().Reason);
        Assert.Equal(SessionEndAction.Logout, ends.Single().Action);
    }

    [Fact]
    public void LocalMode_AutoLogin_AllRightsAndNoExpiry()
    {
        var (sessions, _) = Build(AuthMode.Local);

        var session = sessions.Current;
        Assert.NotNull(session);
        Assert.True(session.IsBuiltInLocal);
        Assert.True(session.HasPermission(SystemPermissions.Operate));
        // проектные права тоже: разработка экранов не должна упираться в роли
        Assert.True(session.HasPermission("Уставки.Edit"));

        _time.Advance(TimeSpan.FromDays(1));
        sessions.Evaluate();

        Assert.False(session.IsLocked);
        Assert.Same(session, sessions.Current);
    }

    [Fact]
    public void FullMode_StartsWithoutSession()
    {
        var (sessions, _) = Build();

        Assert.Null(sessions.Current);
    }
}
