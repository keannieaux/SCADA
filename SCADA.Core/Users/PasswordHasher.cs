using System.Security.Cryptography;

namespace SCADA.Core.Users;

/// <summary>
/// Хеширование паролей: PBKDF2-HMACSHA256, соль 16 байт, хеш 32 байта
/// (docs/users-plan.md §4.2). Внешних зависимостей нет — классом пользуются
/// и рантайм, и редактор, и утилита scada-user.
/// Диспетчеризация по строке алгоритма из записи пользователя — точка
/// смены алгоритма: новый алгоритм добавляется веткой в HashCore/Verify,
/// старые хеши продолжают работать.
/// </summary>
public static class PasswordHasher
{
    public const string Pbkdf2Sha256 = "pbkdf2-sha256";

    /// <summary>Итерации по умолчанию для новых паролей (~100k, OWASP 2023+).</summary>
    public const int DefaultIterations = 100_000;

    private const int SaltSize = 16;
    private const int HashSize = 32;

    /// <summary>Хеширует пароль свежей солью. Возвращает готовую тройку
    /// base64-полей для <see cref="UserDefinition"/>.</summary>
    public static (string Salt, string Hash) Hash(
        string password, int iterations = DefaultIterations)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(iterations, 1);

        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] hash = HashCore(password, salt, iterations);
        return (Convert.ToBase64String(salt), Convert.ToBase64String(hash));
    }

    /// <summary>Проверяет пароль против записи пользователя. Неизвестный
    /// алгоритм или битые base64-поля — false, а не исключение: записи могут
    /// прийти из отредактированного вручную файла, падать на логине нельзя.</summary>
    public static bool Verify(string password, UserDefinition user)
    {
        if (!string.Equals(user.Algorithm, Pbkdf2Sha256, StringComparison.Ordinal))
            return false;
        if (user.Iterations < 1)
            return false;

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(user.Salt);
            expected = Convert.FromBase64String(user.PasswordHash);
        }
        catch (FormatException)
        {
            return false;
        }
        if (expected.Length != HashSize)
            return false;

        byte[] actual = HashCore(password, salt, user.Iterations);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>Посчитан ли хеш пользователя устаревшими параметрами —
    /// другим алгоритмом или меньшим числом итераций. Знание о том, что
    /// считать устаревшим, живёт здесь же, где алгоритмы: добавляя новый,
    /// правишь одно место, а не условие в хранилище.</summary>
    public static bool NeedsUpgrade(UserDefinition user)
        => !string.Equals(user.Algorithm, Pbkdf2Sha256, StringComparison.Ordinal)
           || user.Iterations < DefaultIterations;

    private static byte[] HashCore(string password, byte[] salt, int iterations)
        => Rfc2898DeriveBytes.Pbkdf2(
            password, salt, iterations, HashAlgorithmName.SHA256, HashSize);
}
