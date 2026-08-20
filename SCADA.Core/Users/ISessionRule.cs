namespace SCADA.Core.Users;

/// <summary>
/// Правило завершения сессии (docs/users-plan.md §6.1). Повод завершить
/// сессию — всегда правило: сервис на тике спрашивает их по очереди и не
/// знает, какое сработает. Новый повод (лимит длительности, пересменка) —
/// новая реализация этого интерфейса, вызывающие стороны не меняются.
/// Правило — чистая функция от сессии и времени: без ввода-вывода,
/// проверяется тестом на подставленных часах.
/// </summary>
public interface ISessionRule
{
    /// <summary>Причина для аудита и экрана блокировки.</summary>
    SessionEndReason Reason { get; }

    /// <summary>Исход: блокировка или полный выход.</summary>
    SessionEndAction Action { get; }

    /// <summary>Сработало ли правило к моменту <paramref name="nowUtc"/>.</summary>
    bool IsExpired(SessionInfo session, DateTimeOffset nowUtc);
}
