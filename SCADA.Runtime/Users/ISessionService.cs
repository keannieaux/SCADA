using SCADA.Core.Users;

namespace SCADA.Runtime.Users;

/// <summary>
/// Сессии пользователей (docs/users-plan.md §6). На локальном мастер-АРМе
/// активна одна сессия — за пультом один оператор; в remote-варианте
/// таблицу сессий будет держать сервер, поэтому идентификатор сессии есть
/// уже сейчас (§8).
/// </summary>
public interface ISessionService
{
    /// <summary>Текущая сессия или null, если никто не вошёл.</summary>
    SessionInfo? Current { get; }

    /// <summary>Вход выполнен (в том числе автологин в AuthMode.Local).</summary>
    event Action<SessionInfo>? SessionStarted;

    /// <summary>Сессия заблокирована или завершена — с причиной и исходом.</summary>
    event Action<SessionEndedEventArgs>? SessionEnded;

    /// <summary>Вход по логину и паролю. Неверные данные — null (без
    /// исключения: это штатный ответ окну входа) и текущая сессия не
    /// затрагивается.</summary>
    SessionInfo? Authenticate(string login, string password);

    /// <summary>Отметить действие оператора — отодвигает автоблокировку.
    /// На заблокированной сессии ничего не делает: движение мыши не
    /// должно её оживлять.</summary>
    void Touch();

    /// <summary>Снять блокировку паролем того же пользователя.</summary>
    bool Unlock(string password);

    /// <summary>Явный выход (<see cref="SessionEndReason.Manual"/>).</summary>
    void Logout();

    /// <summary>Проверить правила завершения (§6.1). Вызывается тиком
    /// рантайма; отдельный вызов из UI безвреден.</summary>
    void Evaluate();
}

/// <summary>Событие завершения сессии: что за сессия, почему и с каким исходом.</summary>
public sealed record SessionEndedEventArgs(
    SessionInfo Session, SessionEndReason Reason, SessionEndAction Action);
