using System.Text.Json;
using SCADA.Core.Users;
using SCADA.Runtime.Users;

namespace SCADA.Runtime.Tests;

/// <summary>
/// Файловое хранилище пользователей (docs/users-plan.md §4.2, §4.4):
/// сид первого старта, атомарная запись, политика паролей, защита
/// последнего носителя ManageUsers, upgrade-on-login.
/// </summary>
public class UserStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    private string FilePath => Path.Combine(_dir, "users.json");

    private static readonly UsersConfiguration ConfigWithRoles = new()
    {
        Roles =
        [
            new RoleDefinition { Name = "Наблюдатель", Permissions = ["View"] },
            new RoleDefinition { Name = "Оператор", Permissions = ["View", "Operate", "AckAlarms"] },
            new RoleDefinition { Name = "Администратор",
                Permissions = ["View", "Operate", "AckAlarms", "ManageUsers", "UpdateProject"] }
        ],
        MinPasswordLength = 6
    };

    public UserStoreTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void FirstStart_SeedsDefaultAdmin()
    {
        var store = new UserStore(_dir, ConfigWithRoles);

        Assert.True(File.Exists(FilePath));
        var admin = store.Find(UserStore.DefaultAdminLogin);
        Assert.NotNull(admin);
        Assert.Contains("Администратор", admin.Roles); // все роли проекта
        Assert.True(store.VerifyPassword(UserStore.DefaultAdminLogin,
            UserStore.DefaultAdminPassword));
    }

    [Fact]
    public void FirstStart_ProjectWithoutRoles_NoSeed()
    {
        var store = new UserStore(_dir, new UsersConfiguration());

        Assert.Empty(store.Users);
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void AddUser_PersistsAcrossReload()
    {
        var store = new UserStore(_dir, ConfigWithRoles);
        store.AddUser("ivanov", "password1", ["Оператор"]);

        var reloaded = new UserStore(_dir, ConfigWithRoles);

        var user = reloaded.Find("ivanov");
        Assert.NotNull(user);
        Assert.Equal(["Оператор"], user.Roles);
        Assert.True(reloaded.VerifyPassword("ivanov", "password1"));
        Assert.False(reloaded.VerifyPassword("ivanov", "password2"));
    }

    [Fact]
    public void Login_IsCaseInsensitive()
    {
        var store = new UserStore(_dir, ConfigWithRoles);
        store.AddUser("Ivanov", "password1", ["Оператор"]);

        Assert.NotNull(store.Find("ivanov"));
        Assert.Equal("Ivanov", store.Find("IVANOV")!.Login); // регистр ввода сохранён
        Assert.True(store.VerifyPassword("iVaNoV", "password1"));
        Assert.Throws<UserStoreException>(() =>
            store.AddUser("ivanov", "password2", ["Оператор"]));
    }

    [Fact]
    public void Find_ReturnsCopy_MutationDoesNotLeakIntoStore()
    {
        var store = new UserStore(_dir, ConfigWithRoles);
        store.AddUser("ivanov", "password1", ["Оператор"]);

        store.Find("ivanov")!.Roles.Add("Администратор");

        Assert.Equal(["Оператор"], store.Find("ivanov")!.Roles);
    }

    [Fact]
    public void AddUser_DuplicateLogin_Throws()
    {
        var store = new UserStore(_dir, ConfigWithRoles);
        store.AddUser("ivanov", "password1", ["Оператор"]);

        Assert.Throws<UserStoreException>(() =>
            store.AddUser("ivanov", "password2", ["Наблюдатель"]));
    }

    [Fact]
    public void AddUser_ShortPassword_Throws()
    {
        var store = new UserStore(_dir, ConfigWithRoles); // minPasswordLength = 6

        Assert.Throws<UserStoreException>(() =>
            store.AddUser("ivanov", "12345", ["Оператор"]));
    }

    [Fact]
    public void AddUser_UnknownRole_Throws()
    {
        var store = new UserStore(_dir, ConfigWithRoles);

        Assert.Throws<UserStoreException>(() =>
            store.AddUser("ivanov", "password1", ["Призрак"]));
    }

    [Fact]
    public void SetPassword_OldFailsNewWorks()
    {
        var store = new UserStore(_dir, ConfigWithRoles);
        store.AddUser("ivanov", "password1", ["Оператор"]);

        store.SetPassword("ivanov", "password2");

        Assert.False(store.VerifyPassword("ivanov", "password1"));
        Assert.True(store.VerifyPassword("ivanov", "password2"));
    }

    [Fact]
    public void RemoveUser_LastManageUsersBearer_Throws()
    {
        var store = new UserStore(_dir, ConfigWithRoles); // admin засеян — единственный админ

        var ex = Assert.Throws<UserStoreException>(() =>
            store.RemoveUser(UserStore.DefaultAdminLogin));
        Assert.Contains("ManageUsers", ex.Message);
    }

    [Fact]
    public void RemoveUser_NotLastAdmin_Succeeds()
    {
        var store = new UserStore(_dir, ConfigWithRoles);
        store.AddUser("second-admin", "password1", ["Администратор"]);

        store.RemoveUser(UserStore.DefaultAdminLogin);

        Assert.Null(store.Find(UserStore.DefaultAdminLogin));
        Assert.NotNull(store.Find("second-admin"));
    }

    [Fact]
    public void SetRoles_StrippingLastManageUsers_Throws()
    {
        var store = new UserStore(_dir, ConfigWithRoles);

        Assert.Throws<UserStoreException>(() =>
            store.SetRoles(UserStore.DefaultAdminLogin, ["Наблюдатель"]));
    }

    [Fact]
    public void EnsureAdmin_RecreatesBearerWhenNoneLeft()
    {
        // файл существует (сид НЕ сработает в конструкторе), но носителя
        // ManageUsers в нём нет — сценарий «админов удалили файлом»
        WriteUsersJson(MakeUserJson("ivanov", "password1", iterations: 1000,
            roles: "[\"Оператор\"]"));
        var store = new UserStore(_dir, ConfigWithRoles);
        Assert.Null(store.Find(UserStore.DefaultAdminLogin));

        store.EnsureAdmin();

        Assert.NotNull(store.Find(UserStore.DefaultAdminLogin));
    }

    [Fact]
    public void VerifyPassword_UpgradesOutdatedIterations()
    {
        WriteUsersJson(MakeUserJson("ivanov", "password1", iterations: 1000,
            roles: "[\"Оператор\"]"));
        var store = new UserStore(_dir, ConfigWithRoles);

        Assert.True(store.VerifyPassword("ivanov", "password1"));

        var upgraded = store.Find("ivanov")!;
        Assert.Equal(PasswordHasher.DefaultIterations, upgraded.Iterations);

        // апгрейд обязан лечь в файл: иначе пересчёт повторялся бы на каждом
        // входе, а на диске оставались бы старые параметры
        var reloaded = new UserStore(_dir, ConfigWithRoles);
        Assert.Equal(PasswordHasher.DefaultIterations, reloaded.Find("ivanov")!.Iterations);
        Assert.True(reloaded.VerifyPassword("ivanov", "password1"));
    }

    [Fact]
    public void DuplicateLoginInFile_Throws()
    {
        File.WriteAllText(FilePath,
            $"[{MakeUserJson("ivanov", "password1", 1000, "[\"Оператор\"]")}," +
            $"{MakeUserJson("Ivanov", "password2", 1000, "[\"Администратор\"]")}]");

        var ex = Assert.Throws<UserStoreException>(() =>
            new UserStore(_dir, ConfigWithRoles));
        Assert.Contains("больше одного раза", ex.Message);
    }

    [Fact]
    public void CorruptFile_Throws_NotSilentlyWipes()
    {
        File.WriteAllText(FilePath, "{ это не json пользователей");

        var ex = Assert.Throws<UserStoreException>(() =>
            new UserStore(_dir, ConfigWithRoles));
        Assert.Contains("повреждён", ex.Message);
    }

    [Fact]
    public void Save_IsAtomic_NoTempFileLeft()
    {
        var store = new UserStore(_dir, ConfigWithRoles);
        store.AddUser("ivanov", "password1", ["Оператор"]);

        Assert.False(File.Exists(FilePath + ".tmp"));
        // и файл — валидный JSON со всеми пользователями
        var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
        Assert.Equal(2, doc.RootElement.GetArrayLength()); // admin + ivanov
    }

    // --- helpers ---

    private void WriteUsersJson(string userJson)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, $"[{userJson}]");
    }

    private static string MakeUserJson(string login, string password, int iterations,
        string roles)
    {
        var (salt, hash) = PasswordHasher.Hash(password, iterations);
        return $$"""
            {
              "login": "{{login}}",
              "algorithm": "pbkdf2-sha256",
              "iterations": {{iterations}},
              "salt": "{{salt}}",
              "passwordHash": "{{hash}}",
              "roles": {{roles}}
            }
            """;
    }
}
