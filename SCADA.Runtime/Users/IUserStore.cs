using SCADA.Core.Users;

namespace SCADA.Runtime.Users;

/// <summary>
/// Хранилище пользователей станции (docs/users-plan.md §4.2). users.json
/// живёт в папке проекта (данные эксплуатации, в пакет не входят).
/// Реализация одна — файловая; интерфейс отдельный, чтобы сессии и
/// утилита scada-user не зависели от формата, а тесты могли подставить
/// заглушку.
/// </summary>
public interface IUserStore
{
    /// <summary>Снимок списка пользователей.</summary>
    IReadOnlyList<UserDefinition> Users { get; }

    /// <summary>Поиск по логину (Ordinal — логины регистрозависимы).</summary>
    UserDefinition? Find(string login);

    /// <summary>Проверка пароля. При успехе и устаревших параметрах хеша
    /// (меньше итераций, чем актуальный дефолт) хеш прозрачно переписывается
    /// — upgrade-on-login, смена алгоритма не требует сброса паролей.</summary>
    bool VerifyPassword(string login, string password);

    /// <summary>Новый пользователь. Логин уникален, пароль не короче
    /// UsersConfiguration.MinPasswordLength, роли должны существовать
    /// в проекте. Нарушения — UserStoreException.</summary>
    void AddUser(string login, string password, IReadOnlyList<string> roles);

    /// <summary>Удаление. Нельзя удалить последнего носителя ManageUsers —
    /// восстановление после этого только утилитой scada-user.</summary>
    void RemoveUser(string login);

    /// <summary>Смена/сброс пароля (хешируется актуальными параметрами).</summary>
    void SetPassword(string login, string newPassword);

    /// <summary>Замена списка ролей. Роли должны существовать в проекте.</summary>
    void SetRoles(string login, IReadOnlyList<string> roles);

    /// <summary>Гарантирует существование хотя бы одного носителя ManageUsers:
    /// если ни одного нет (удалили последнего админа, потерян файл) — создаёт
    /// дефолтного admin. Вызывается рантаймом при старте и утилитой.
    /// В проекте без ролей (AuthMode.Local) — no-op.</summary>
    void EnsureAdmin();
}
