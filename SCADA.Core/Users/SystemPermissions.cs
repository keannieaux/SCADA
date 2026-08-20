namespace SCADA.Core.Users;

/// <summary>
/// Системные права (docs/users-plan.md §2.1). Право — строка-идентификатор;
/// системные хардкодятся здесь, их проверяет код ядра (запись в теги,
/// квитирование, управление пользователями). Новое системное право =
/// одна константа + проверка в точке использования; форматы не меняются.
/// Проектные права — произвольные строки из ролей проекта, семантики
/// для ядра не имеют, сверяются только на наличие.
/// </summary>
public static class SystemPermissions
{
    /// <summary>Просмотр. Базовое право любой авторизованной сессии.</summary>
    public const string View = "View";

    /// <summary>Операторская запись в теги (WriteTagsAsync).</summary>
    public const string Operate = "Operate";

    /// <summary>Квитирование аварий.</summary>
    public const string AckAlarms = "AckAlarms";

    /// <summary>Управление пользователями (CRUD, сброс паролей).</summary>
    public const string ManageUsers = "ManageUsers";

    /// <summary>Обновление пакета проекта (под будущий лаунчер, M6.5).</summary>
    public const string UpdateProject = "UpdateProject";

    public static IReadOnlySet<string> All { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            View, Operate, AckAlarms, ManageUsers, UpdateProject
        };

    /// <summary>Системное ли право. false — значит проектное (из ролей проекта).</summary>
    public static bool IsSystem(string permission) => All.Contains(permission);
}
