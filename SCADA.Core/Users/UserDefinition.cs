namespace SCADA.Core.Users;

/// <summary>
/// Пользователь станции (docs/users-plan.md §2.3). Данные эксплуатации:
/// живут в users.json в ПАПКЕ проекта, в пакет не входят — иначе смена
/// пароля требовала бы пересборки, а обновление проекта затирало бы
/// учётки, заведённые на объекте (§3).
/// Параметры хеша (алгоритм, итерации) хранятся на пользователя, а не
/// глобально: смена алгоритма в будущем не ломает старые хеши — они
/// проверяются по своим параметрам и прозрачно переписываются при логине.
/// </summary>
public class UserDefinition
{
    /// <summary>Уникальный логин. Сравнение — Ordinal (логины регистрозависимы).</summary>
    public required string Login { get; set; }

    /// <summary>Алгоритм хеширования. Сейчас только PasswordHasher.Pbkdf2Sha256;
    /// неизвестный алгоритм = Verify всегда false.</summary>
    public string Algorithm { get; set; } = PasswordHasher.Pbkdf2Sha256;

    /// <summary>Число итераций PBKDF2, которым посчитан хеш.</summary>
    public int Iterations { get; set; } = PasswordHasher.DefaultIterations;

    /// <summary>Соль, base64 (16 байт).</summary>
    public required string Salt { get; set; }

    /// <summary>Хеш пароля, base64 (32 байта).</summary>
    public required string PasswordHash { get; set; }

    /// <summary>Имена ролей из roles.json. Эффективные права пользователя —
    /// объединение прав всех его ролей.</summary>
    public List<string> Roles { get; set; } = [];
}
