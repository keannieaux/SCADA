using System.Text.Json;
using SCADA.Core.Users;
using SCADA.Runtime.Configuration;

namespace SCADA.Runtime.Users;

/// <summary>
/// Файловая реализация <see cref="IUserStore"/>: users.json в папке проекта
/// (docs/users-plan.md §4.2).
/// Запись атомарна: временный файл + File.Move поверх — сбой питания посреди
/// записи не оставляет обрезанный JSON. Битый файл при загрузке — исключение,
/// а не молчаливая очистка: пользователей нельзя терять незаметно.
/// Файл перечитывается только при старте: внешние правки подхватываются
/// перезапуском, собственные операции хранилище знает само.
/// Потокобезопасно (один процесс, операции редки — грубая блокировка).
/// </summary>
public sealed class UserStore : IUserStore
{
    /// <summary>Логин учётки восстановления (ensure-admin, сид первого старта).</summary>
    public const string DefaultAdminLogin = "admin";

    /// <summary>Документированный дефолтный пароль учётки восстановления.
    /// Политику длины обходит намеренно: это аварийный вход, пароль меняется
    /// первым действием администратора.</summary>
    public const string DefaultAdminPassword = "admin";

    private readonly string _filePath;
    private readonly UsersConfiguration _configuration;
    private readonly object _gate = new();
    private readonly List<UserDefinition> _users;

    public UserStore(string projectDataDirectory, UsersConfiguration configuration)
    {
        _configuration = configuration;
        _filePath = Path.Combine(projectDataDirectory, "users.json");
        Directory.CreateDirectory(projectDataDirectory);
        _users = Load();

        // сид первого старта: файла нет — создаём учётку восстановления
        if (!File.Exists(_filePath))
        {
            EnsureAdmin();
        }
    }

    public IReadOnlyList<UserDefinition> Users
    {
        get { lock (_gate) return _users.ToArray(); }
    }

    public UserDefinition? Find(string login)
    {
        lock (_gate) return FindCore(login);
    }

    public bool VerifyPassword(string login, string password)
    {
        lock (_gate)
        {
            var user = FindCore(login);
            if (user is null || !PasswordHasher.Verify(password, user))
                return false;

            // upgrade-on-login: хеш на старых параметрах — переписать актуальными
            if (user.Iterations < PasswordHasher.DefaultIterations)
                SetPasswordCore(user, password);
            return true;
        }
    }

    public void AddUser(string login, string password, IReadOnlyList<string> roles)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(login))
                throw new UserStoreException("Логин не может быть пустым");
            if (FindCore(login) is not null)
                throw new UserStoreException($"Пользователь '{login}' уже существует");
            CheckPasswordPolicy(password);
            CheckRolesExist(roles);

            var (salt, hash) = PasswordHasher.Hash(password);
            _users.Add(new UserDefinition
            {
                Login = login,
                Salt = salt,
                PasswordHash = hash,
                Roles = [..roles]
            });
            Save();
        }
    }

    public void RemoveUser(string login)
    {
        lock (_gate)
        {
            var user = FindCore(login)
                ?? throw new UserStoreException($"Пользователь '{login}' не найден");

            // последнего носителя ManageUsers удалять нельзя: без него
            // управление пользователями восстанавливается только утилитой
            if (GrantsManageUsers(user.Roles) &&
                !_users.Where(u => u != user).Any(u => GrantsManageUsers(u.Roles)))
                throw new UserStoreException(
                    $"Пользователь '{login}' — последний с правом ManageUsers, удаление запрещено");

            _users.Remove(user);
            Save();
        }
    }

    public void SetPassword(string login, string newPassword)
    {
        lock (_gate)
        {
            var user = FindCore(login)
                ?? throw new UserStoreException($"Пользователь '{login}' не найден");
            CheckPasswordPolicy(newPassword);
            SetPasswordCore(user, newPassword);
            Save();
        }
    }

    public void SetRoles(string login, IReadOnlyList<string> roles)
    {
        lock (_gate)
        {
            var user = FindCore(login)
                ?? throw new UserStoreException($"Пользователь '{login}' не найден");
            CheckRolesExist(roles);

            if (GrantsManageUsers(user.Roles) && !GrantsManageUsers(roles) &&
                !_users.Where(u => u != user).Any(u => GrantsManageUsers(u.Roles)))
                throw new UserStoreException(
                    $"Пользователь '{login}' — последний с правом ManageUsers, " +
                    "снять с него эту роль нельзя");

            user.Roles = [..roles];
            Save();
        }
    }

    public void EnsureAdmin()
    {
        lock (_gate)
        {
            // проект без ролей — локальный режим, учётка восстановления не нужна
            if (_configuration.Roles.Count == 0)
                return;
            if (_users.Any(u => GrantsManageUsers(u.Roles)))
                return;
            if (FindCore(DefaultAdminLogin) is not null)
                return; // логин занят, но админов всё равно нет — странно, не затираем чужого

            // admin получает все роли проекта: носитель ManageUsers гарантирован,
            // если хоть одна роль его даёт (состав ролей — зона инженера)
            var (salt, hash) = PasswordHasher.Hash(DefaultAdminPassword);
            _users.Add(new UserDefinition
            {
                Login = DefaultAdminLogin,
                Salt = salt,
                PasswordHash = hash,
                Roles = _configuration.Roles.Select(r => r.Name).ToList()
            });
            Save();
        }
    }

    // --- внутреннее (под _gate) ---

    private UserDefinition? FindCore(string login)
        => _users.FirstOrDefault(u => string.Equals(u.Login, login, StringComparison.Ordinal));

    private bool GrantsManageUsers(IEnumerable<string> roleNames)
        => roleNames.Any(name =>
            _configuration.Roles.FirstOrDefault(r => r.Name == name) is { } role &&
            role.Permissions.Contains(SystemPermissions.ManageUsers));

    private void CheckPasswordPolicy(string password)
    {
        if (password.Length < _configuration.MinPasswordLength)
            throw new UserStoreException(
                $"Пароль короче минимальной длины ({_configuration.MinPasswordLength})");
    }

    private void CheckRolesExist(IReadOnlyList<string> roles)
    {
        foreach (string role in roles)
            if (_configuration.Roles.All(r => r.Name != role))
                throw new UserStoreException($"Роль '{role}' не найдена в проекте (roles.json)");
    }

    private void SetPasswordCore(UserDefinition user, string password)
    {
        var (salt, hash) = PasswordHasher.Hash(password);
        user.Algorithm = PasswordHasher.Pbkdf2Sha256;
        user.Iterations = PasswordHasher.DefaultIterations;
        user.Salt = salt;
        user.PasswordHash = hash;
    }

    private List<UserDefinition> Load()
    {
        if (!File.Exists(_filePath))
            return [];
        try
        {
            return JsonSerializer.Deserialize(
                File.ReadAllText(_filePath),
                ProjectJsonContext.Default.ListUserDefinition) ?? [];
        }
        catch (JsonException ex)
        {
            throw new UserStoreException(
                $"users.json повреждён и не прочитан: {ex.Message}. " +
                "Восстановите файл из бэкапа или удалите его для ресида admin (users-plan.md §4.4)", ex);
        }
    }

    /// <summary>Атомарная запись: .tmp + Move поверх (users-plan.md §4.2).</summary>
    private void Save()
    {
        string json = JsonSerializer.Serialize(_users,
            ProjectJsonContext.Default.ListUserDefinition);
        string tempPath = _filePath + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, _filePath, overwrite: true);
    }
}
