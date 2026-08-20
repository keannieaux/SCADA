namespace SCADA.Core.Users;

/// <summary>
/// Активная сессия пользователя (docs/users-plan.md §6). Права считаются
/// один раз при входе и дальше не пересчитываются: проверка права — lookup
/// в готовом множестве, а не обход ролей на каждый клик.
/// Изменяемые поля (<see cref="LastActivityUtc"/>, <see cref="IsLocked"/>)
/// ведёт сервис сессий — снаружи их не трогают.
/// </summary>
public sealed class SessionInfo
{
    /// <summary>Идентификатор сессии. В локальном режиме нужен только для
    /// аудита, в remote станет токеном, который АРМ шлёт с каждым запросом (§8).</summary>
    public required Guid SessionId { get; init; }

    /// <summary>Логин в том регистре, в котором заведён пользователь;
    /// в локальном режиме — пользователь ОС (§6).</summary>
    public required string Login { get; init; }

    /// <summary>Эффективные права: объединение прав всех ролей пользователя
    /// плюс базовый <see cref="SystemPermissions.View"/>.</summary>
    public required IReadOnlySet<string> Permissions { get; init; }

    /// <summary>Встроенный локальный администратор (AuthMode.Local): любое
    /// право считается выданным, правила завершения к нему не применяются.</summary>
    public bool IsBuiltInLocal { get; init; }

    public required DateTimeOffset StartedUtc { get; init; }

    /// <summary>Момент последнего действия оператора — от него считает
    /// правило бездействия.</summary>
    public required DateTimeOffset LastActivityUtc { get; set; }

    /// <summary>Сессия заблокирована правилом: остаётся только просмотр.</summary>
    public bool IsLocked { get; set; }

    /// <summary>Есть ли право с учётом блокировки. Заблокированная сессия
    /// сохраняет только просмотр: оператор видит объект, но не управляет им
    /// (§6.1). Локальный администратор — всё, включая проектные права:
    /// иначе разработка экранов упиралась бы в незаведённые роли.</summary>
    public bool HasPermission(string permission)
    {
        if (IsLocked)
            return string.Equals(permission, SystemPermissions.View, StringComparison.Ordinal);
        return IsBuiltInLocal || Permissions.Contains(permission);
    }
}
