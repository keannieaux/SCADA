using SCADA.Core.Users;
using SCADA.Runtime.Configuration;

namespace SCADA.Runtime.Tests;

/// <summary>
/// Загрузка и валидация roles.json (docs/users-plan.md §4.1).
/// Файл опционален: его отсутствие = проект без разграничения (AuthMode.Local).
/// users.json загрузчиком не читается — это данные эксплуатации (§3).
/// </summary>
public class RolesLoaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

    public RolesLoaderTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private void WriteFile(string name, string content) =>
        File.WriteAllText(Path.Combine(_dir, name), content);

    // Минимальный валидный проект, к которому тесты добавляют roles.json
    private void WriteValidProject()
    {
        WriteFile("project.json", """
            {"formatVersion": 1, "name": "TestProject", "version": "1.0"}
            """);
        WriteFile("devices.json", """
            {
              "formatVersion": 1,
              "channels": [{"id": 0, "name": "Ch0", "channelType": "modbus-tcp"}],
              "devices": [{"id": 0, "name": "PLC0", "driverName": "simulator", "channelId": 0}]
            }
            """);
        WriteFile("tags.json", """
            {
              "formatVersion": 1,
              "tags": [
                {"id": 0, "name": "Pump1.Running", "dataType": "discrete", "deviceId": 0}
              ]
            }
            """);
    }

    private void WriteValidRoles()
    {
        WriteFile("roles.json", """
            {
              "formatVersion": 1,
              "minPasswordLength": 6,
              "idleTimeoutMinutes": 15,
              "roles": [
                { "name": "Наблюдатель", "permissions": ["View"] },
                { "name": "Оператор", "permissions": ["View", "Operate", "AckAlarms"] },
                { "name": "Технолог", "permissions": ["View", "Уставки.Edit"] }
              ]
            }
            """);
    }

    [Fact]
    public void NoRolesFile_EmptyConfiguration()
    {
        WriteValidProject();

        var config = ProjectLoader.Load(_dir);

        Assert.Empty(config.Users.Roles);
        Assert.True(config.Users.MinPasswordLength > 0);
        Assert.False(config.Users.IsConfigured); // нет roles.json — нет и секции в пакете
    }

    [Fact]
    public void ValidFile_LoadsRolesAndSettings()
    {
        WriteValidProject();
        WriteValidRoles();

        var config = ProjectLoader.Load(_dir);

        Assert.Equal(3, config.Users.Roles.Count);
        Assert.Equal(6, config.Users.MinPasswordLength);
        Assert.Equal(15, config.Users.IdleTimeoutMinutes);
        Assert.True(config.Users.IsConfigured);

        var technologist = config.Users.Roles.Single(r => r.Name == "Технолог");
        // проектное право — произвольная строка, валидатор её пропускает
        Assert.Contains("Уставки.Edit", technologist.Permissions);
        Assert.Contains(SystemPermissions.View, technologist.Permissions);
    }

    [Fact]
    public void DuplicateRoleName_Fails()
    {
        WriteValidProject();
        WriteFile("roles.json", """
            {
              "formatVersion": 1,
              "roles": [
                { "name": "Оператор", "permissions": ["View"] },
                { "name": "Оператор", "permissions": ["Operate"] }
              ]
            }
            """);

        var ex = Assert.Throws<ProjectConfigurationException>(() => ProjectLoader.Load(_dir));
        Assert.Contains(ex.Errors, e => e.Contains("Дубликат имени роли 'Оператор'"));
    }

    [Fact]
    public void DuplicatePermissionInRole_Fails()
    {
        WriteValidProject();
        WriteFile("roles.json", """
            {
              "formatVersion": 1,
              "roles": [{ "name": "Оператор", "permissions": ["View", "View"] }]
            }
            """);

        var ex = Assert.Throws<ProjectConfigurationException>(() => ProjectLoader.Load(_dir));
        Assert.Contains(ex.Errors, e => e.Contains("право 'View' задано дважды"));
    }

    [Fact]
    public void EmptyRoleName_Fails()
    {
        WriteValidProject();
        WriteFile("roles.json", """
            {
              "formatVersion": 1,
              "roles": [{ "name": "  ", "permissions": ["View"] }]
            }
            """);

        var ex = Assert.Throws<ProjectConfigurationException>(() => ProjectLoader.Load(_dir));
        Assert.Contains(ex.Errors, e => e.Contains("пустым именем"));
    }

    [Fact]
    public void NegativeIdleTimeout_Fails()
    {
        WriteValidProject();
        WriteFile("roles.json", """
            {
              "formatVersion": 1,
              "idleTimeoutMinutes": -5,
              "roles": [{ "name": "Оператор", "permissions": ["View"] }]
            }
            """);

        var ex = Assert.Throws<ProjectConfigurationException>(() => ProjectLoader.Load(_dir));
        Assert.Contains(ex.Errors, e => e.Contains("idleTimeoutMinutes"));
    }

    [Fact]
    public void BadFormatVersion_Fails()
    {
        WriteValidProject();
        WriteFile("roles.json", """
            { "formatVersion": 99, "roles": [] }
            """);

        var ex = Assert.Throws<ProjectConfigurationException>(() => ProjectLoader.Load(_dir));
        Assert.Contains(ex.Errors, e => e.Contains("roles.json") && e.Contains("версия формата"));
    }
}
