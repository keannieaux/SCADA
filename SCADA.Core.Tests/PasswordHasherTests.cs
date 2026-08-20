using SCADA.Core.Users;

namespace SCADA.Core.Tests;

/// <summary>
/// Хеширование паролей (docs/users-plan.md §4.2). PBKDF2 с солью на
/// пользователя; проверка не должна падать на ручных правках users.json.
/// </summary>
public class PasswordHasherTests
{
    private static UserDefinition MakeUser(string password, int iterations = 1000)
    {
        var (salt, hash) = PasswordHasher.Hash(password, iterations);
        return new UserDefinition
        {
            Login = "ivanov",
            Salt = salt,
            PasswordHash = hash,
            Iterations = iterations
        };
    }

    [Fact]
    public void Verify_CorrectPassword_ReturnsTrue()
    {
        var user = MakeUser("secret");

        Assert.True(PasswordHasher.Verify("secret", user));
    }

    [Fact]
    public void Verify_WrongPassword_ReturnsFalse()
    {
        var user = MakeUser("secret");

        Assert.False(PasswordHasher.Verify("Secret", user));
        Assert.False(PasswordHasher.Verify("secret1", user));
        Assert.False(PasswordHasher.Verify("", user));
    }

    [Fact]
    public void Hash_SamePasswordTwice_DifferentSaltAndHash()
    {
        var (salt1, hash1) = PasswordHasher.Hash("secret");
        var (salt2, hash2) = PasswordHasher.Hash("secret");

        // Соль случайна на каждый хеш — иначе одинаковые пароли видны в файле.
        Assert.NotEqual(salt1, salt2);
        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void Verify_RespectsStoredIterations()
    {
        // Хеш, посчитанный на 1000 итераций, не сойдётся при проверке на 2000.
        var user = MakeUser("secret", iterations: 1000);
        user.Iterations = 2000;

        Assert.False(PasswordHasher.Verify("secret", user));
    }

    [Fact]
    public void Verify_UnknownAlgorithm_ReturnsFalse()
    {
        var user = MakeUser("secret");
        user.Algorithm = "argon2id";

        Assert.False(PasswordHasher.Verify("secret", user));
    }

    [Fact]
    public void Verify_CorruptBase64_ReturnsFalse()
    {
        // users.json может правиться вручную на объекте — логин не должен падать.
        var user = MakeUser("secret");
        user.Salt = "!!!not-base64!!!";

        Assert.False(PasswordHasher.Verify("secret", user));
    }

    [Fact]
    public void Verify_TruncatedHash_ReturnsFalse()
    {
        var user = MakeUser("secret");
        user.PasswordHash = Convert.ToBase64String(new byte[16]); // меньше 32 байт

        Assert.False(PasswordHasher.Verify("secret", user));
    }

    [Fact]
    public void Hash_DefaultIterations_MatchConstant()
    {
        var user = MakeUser("secret", PasswordHasher.DefaultIterations);

        Assert.Equal(PasswordHasher.DefaultIterations, user.Iterations);
        Assert.True(PasswordHasher.Verify("secret", user));
    }
}
