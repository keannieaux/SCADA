namespace SCADA.Core.Users;

/// <summary>
/// Настройки подсистемы пользователей уровня проекта (docs/users-plan.md):
/// роли (§2.2) и политики (§6). Исходная форма — roles.json, компилируемая —
/// секция roles.bin пакета. Пользователи сюда НЕ входят: они — данные
/// эксплуатации и живут в users.json в папке проекта (§3).
/// Пустые роли = проект без разграничения (AuthMode.Local).
/// </summary>
public class UsersConfiguration
{
    /// <summary>Роли проекта: именованные наборы прав.</summary>
    public IReadOnlyList<RoleDefinition> Roles { get; set; } = [];

    /// <summary>Минимальная длина пароля при создании/смене (проверяется
    /// в UserStore при записи, не здесь).</summary>
    public int MinPasswordLength { get; set; } = 4;

    /// <summary>Таймаут автоблокировки сессии по бездействию, минуты
    /// (ТЗ §13). 0 = автоблокировка отключена.</summary>
    public int SessionTimeoutMinutes { get; set; } = 10;
}
