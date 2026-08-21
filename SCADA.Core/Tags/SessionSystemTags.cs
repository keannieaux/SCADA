namespace SCADA.Core.Tags;

/// <summary>
/// Имена системных сессионных тегов (docs/session-tags-concept.md §3).
/// Единственный источник истины: по этим же именам генератор создаёт теги
/// при сборке, а рантайм находит их для заполнения.
/// Состав append-only: убрать имя = сломать схемы на объектах.
/// </summary>
public static class SessionSystemTags
{
    /// <summary>Логин текущего пользователя; пусто, если никто не вошёл.
    /// Кейс: «Оператор: Иванов» в шапке экрана.</summary>
    public const string UserName = "@User.Name";

    /// <summary>Есть ли активная сессия (0/1).</summary>
    public const string UserIsAuthenticated = "@User.IsAuthenticated";

    /// <summary>Заблокирована ли сессия по бездействию (0/1) — экран
    /// блокировки рисуется по нему же (users-plan.md §6.1).</summary>
    public const string UserIsLocked = "@User.IsLocked";

    /// <summary>Префикс тегов прав: `@Right.Уставки.Edit` = 0/1. По одному
    /// на право, использованное в проекте — это и закрывает кейс «видимость
    /// по праву внутри выражения» без функции hasRight() в ВМ.</summary>
    public const string RightPrefix = "@Right.";

    /// <summary>Имя рабочего места.</summary>
    public const string StationName = "@Station.Name";

    /// <summary>Связь с сервером (0/1). На мастер-АРМе всегда 1; смысл
    /// появляется у клиентского АРМа (M7).</summary>
    public const string StationIsConnected = "@Station.IsConnected";

    public static string RightTag(string permission) => RightPrefix + permission;
}
