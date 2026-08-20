namespace SCADA.Core.Users;

/// <summary>
/// Автоблокировка по бездействию (ТЗ §13, docs/users-plan.md §6.1): нет
/// действий оператора дольше заданного времени — сессия блокируется.
/// Исход именно <see cref="SessionEndAction.Lock"/>: оператор отошёл, а не
/// сдал смену, — картина объекта на экране остаётся, разблокировка своим же
/// паролем. <c>0</c> = правило выключено (сессия не истекает).
/// </summary>
public sealed class IdleTimeoutRule(int idleTimeoutMinutes) : ISessionRule
{
    public SessionEndReason Reason => SessionEndReason.Idle;

    public SessionEndAction Action => SessionEndAction.Lock;

    public bool IsExpired(SessionInfo session, DateTimeOffset nowUtc)
        => idleTimeoutMinutes > 0
           && nowUtc - session.LastActivityUtc >= TimeSpan.FromMinutes(idleTimeoutMinutes);
}
