using SCADA.Core.Users;

namespace SCADA.Runtime.Users;

/// <summary>
/// Реализация <see cref="ISessionService"/> (docs/users-plan.md §6).
/// Права считаются один раз при входе: роли — данные пакета, в течение
/// сессии не меняются, а проверка права должна стоить lookup в HashSet.
/// Время берётся из <see cref="TimeProvider"/>, а не из DateTime.UtcNow:
/// иначе правила завершения (§6.1) не проверить тестом.
/// События вызываются вне блокировки — обработчик UI может обратиться
/// к сервису в ответ.
/// </summary>
public sealed class SessionService : ISessionService
{
    private readonly IUserStore _users;
    private readonly UsersConfiguration _configuration;
    private readonly IReadOnlyList<ISessionRule> _rules;
    private readonly TimeProvider _time;
    private readonly object _gate = new();

    private SessionInfo? _current;

    public SessionService(
        IUserStore users,
        UsersConfiguration configuration,
        AuthMode mode = AuthMode.Local,
        TimeProvider? timeProvider = null,
        IReadOnlyList<ISessionRule>? rules = null)
    {
        _users = users;
        _configuration = configuration;
        _time = timeProvider ?? TimeProvider.System;
        // состав правил — из настроек проекта; правило само знает, что
        // делать с выключающим значением (0 = не срабатывать никогда)
        _rules = rules ?? [new IdleTimeoutRule(configuration.IdleTimeoutMinutes)];
        Mode = mode;

        if (mode == AuthMode.Local)
            _current = CreateLocalSession();
    }

    /// <summary>Режим, с которым запущен рантайм (§6).</summary>
    public AuthMode Mode { get; }

    public SessionInfo? Current
    {
        get { lock (_gate) return _current; }
    }

    public event Action<SessionInfo>? SessionStarted;
    public event Action<SessionEndedEventArgs>? SessionEnded;

    public SessionInfo? Authenticate(string login, string password)
    {
        SessionInfo session;
        lock (_gate)
        {
            if (!_users.VerifyPassword(login, password))
                return null;

            // логин из записи, а не из ввода: в аудит должен попасть
            // «Иванов», как заведён, а не «ИВАНОВ», как набрали
            var user = _users.Find(login)!;
            var now = _time.GetUtcNow();
            session = new SessionInfo
            {
                SessionId = Guid.NewGuid(),
                Login = user.Login,
                Permissions = EffectivePermissions(user),
                StartedUtc = now,
                LastActivityUtc = now
            };
            _current = session;
        }

        SessionStarted?.Invoke(session);
        return session;
    }

    public void Touch()
    {
        lock (_gate)
        {
            if (_current is { IsLocked: false } session)
                session.LastActivityUtc = _time.GetUtcNow();
        }
    }

    public bool Unlock(string password)
    {
        lock (_gate)
        {
            if (_current is not { IsLocked: true } session)
                return false;
            // разблокировать может только тот же пользователь: сменить
            // оператора без выхода из системы нельзя (иначе действия
            // сменщика уйдут в аудит под чужим логином)
            if (!_users.VerifyPassword(session.Login, password))
                return false;

            session.IsLocked = false;
            session.LastActivityUtc = _time.GetUtcNow();
            return true;
        }
    }

    public void Logout()
    {
        SessionEndedEventArgs ended;
        lock (_gate)
        {
            if (_current is not { } session)
                return;
            _current = null;
            ended = new SessionEndedEventArgs(
                session, SessionEndReason.Manual, SessionEndAction.Logout);
        }

        SessionEnded?.Invoke(ended);
    }

    public void Evaluate()
    {
        SessionEndedEventArgs? ended = null;
        lock (_gate)
        {
            // локальный администратор не истекает: некому вводить пароль
            // при разблокировке, а разработка экранов не должна прерываться
            if (_current is not { IsBuiltInLocal: false } session)
                return;

            foreach (var rule in _rules)
            {
                if (!rule.IsExpired(session, _time.GetUtcNow()))
                    continue;
                if (rule.Action == SessionEndAction.Lock)
                {
                    // уже заблокирована этим же правилом — повторно не сообщаем:
                    // бездействие продолжается, а событие разовое
                    if (session.IsLocked)
                        continue;
                    session.IsLocked = true;
                }
                else
                {
                    _current = null;
                }

                ended = new SessionEndedEventArgs(session, rule.Reason, rule.Action);
                break;
            }
        }

        if (ended is not null)
            SessionEnded?.Invoke(ended);
    }

    /// <summary>Объединение прав всех ролей пользователя. Просмотр выдаётся
    /// любой авторизованной сессии (§2.1), неизвестная роль (переименовали
    /// в проекте, а users.json остался) прав не даёт и вход не ломает.</summary>
    private IReadOnlySet<string> EffectivePermissions(UserDefinition user)
    {
        var permissions = new HashSet<string>(StringComparer.Ordinal)
        {
            SystemPermissions.View
        };
        foreach (string roleName in user.Roles)
        {
            var role = _configuration.Roles.FirstOrDefault(r => r.Name == roleName);
            if (role is null)
                continue;
            foreach (string permission in role.Permissions)
                permissions.Add(permission);
        }
        return permissions;
    }

    /// <summary>Сессия режима Local: права не проверяются, а логин берётся
    /// от пользователя ОС — так в аудите остаётся хоть какая-то привязка
    /// к человеку за пультом (заглушка "os-user@station" уходит).</summary>
    private SessionInfo CreateLocalSession()
    {
        var now = _time.GetUtcNow();
        return new SessionInfo
        {
            SessionId = Guid.NewGuid(),
            Login = $"{Environment.UserName}@{Environment.MachineName}",
            Permissions = SystemPermissions.All,
            IsBuiltInLocal = true,
            StartedUtc = now,
            LastActivityUtc = now
        };
    }
}
