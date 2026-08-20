using SCADA.Core.Users;
using SCADA.Package.Builder;
using SCADA.Package.Builder.Sections;
using SCADA.Package.Sections;

namespace SCADA.Package.Tests;

/// <summary>
/// Секция roles.bin: round-trip сериализации и полный цикл
/// roles.json → .scadapkg → UsersConfiguration (docs/users-plan.md §4.1).
/// Пользователей в пакете нет — только роли и политики (§3).
/// </summary>
public class RolesSectionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    private string ProjectDir => Path.Combine(_dir, "project");
    private string PackagePath => Path.Combine(_dir, "project.scadapkg");

    public RolesSectionTests()
    {
        Directory.CreateDirectory(ProjectDir);
        File.WriteAllText(Path.Combine(ProjectDir, "project.json"),
            """{"formatVersion": 1, "name": "PumpStation", "version": "1.0"}""");
        File.WriteAllText(Path.Combine(ProjectDir, "devices.json"), """
            {
              "formatVersion": 1,
              "channels": [{"id": 0, "name": "Ch0", "channelType": "modbus-tcp"}],
              "devices": [{"id": 0, "name": "PLC0", "driverName": "simulator", "channelId": 0}]
            }
            """);
        File.WriteAllText(Path.Combine(ProjectDir, "tags.json"), """
            {
              "formatVersion": 1,
              "tags": [
                {"id": 0, "name": "Pump1.Running", "dataType": "discrete", "deviceId": 0}
              ]
            }
            """);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private void WriteRolesJson() =>
        File.WriteAllText(Path.Combine(ProjectDir, "roles.json"), """
            {
              "formatVersion": 1,
              "minPasswordLength": 8,
              "idleTimeoutMinutes": 20,
              "roles": [
                { "name": "Оператор", "permissions": ["View", "Operate", "AckAlarms"] },
                { "name": "Администратор", "permissions": ["View", "Operate", "AckAlarms", "ManageUsers", "UpdateProject"] }
              ]
            }
            """);

    [Fact]
    public void RoundTrip_PreservesRolesAndSettings()
    {
        var source = new UsersConfiguration
        {
            Roles =
            [
                new RoleDefinition { Name = "Оператор",
                    Permissions = [SystemPermissions.View, SystemPermissions.Operate, "Насосная.Control"] },
                new RoleDefinition { Name = "Пустышка", Permissions = [] }
            ],
            MinPasswordLength = 8,
            IdleTimeoutMinutes = 0 // автоблокировка отключена
        };

        var read = RolesSectionReader.Read(RolesSectionWriter.Write(source));

        Assert.Equal(2, read.Roles.Count);
        Assert.Equal("Оператор", read.Roles[0].Name);
        Assert.Equal(["View", "Operate", "Насосная.Control"], read.Roles[0].Permissions);
        Assert.Empty(read.Roles[1].Permissions);
        Assert.Equal(8, read.MinPasswordLength);
        Assert.Equal(0, read.IdleTimeoutMinutes);
    }

    [Fact]
    public void FullCycle_RolesJsonToPackageAndBack()
    {
        WriteRolesJson();

        PackageBuilder.Build(ProjectDir, PackagePath);
        var config = PackageProjectLoader.Load(PackagePath);

        Assert.Equal(2, config.Users.Roles.Count);
        var admin = config.Users.Roles.Single(r => r.Name == "Администратор");
        Assert.Contains(SystemPermissions.ManageUsers, admin.Permissions);
        Assert.Equal(8, config.Users.MinPasswordLength);
        Assert.Equal(20, config.Users.IdleTimeoutMinutes);

        using var reader = PackageReader.Open(PackagePath);
        Assert.True(reader.HasEntry("roles.bin"));
    }

    [Fact]
    public void RolesJsonWithoutRoles_StillCarriesPolicies()
    {
        // роли ещё не заведены, но политики инженер уже настроил —
        // они обязаны доехать до пакета
        File.WriteAllText(Path.Combine(ProjectDir, "roles.json"), """
            {"formatVersion": 1, "roles": [], "minPasswordLength": 12, "idleTimeoutMinutes": 3}
            """);

        PackageBuilder.Build(ProjectDir, PackagePath);
        var config = PackageProjectLoader.Load(PackagePath);

        Assert.Empty(config.Users.Roles);
        Assert.Equal(12, config.Users.MinPasswordLength);
        Assert.Equal(3, config.Users.IdleTimeoutMinutes);
    }

    [Fact]
    public void NoRolesJson_PackageWithoutRolesSection()
    {
        PackageBuilder.Build(ProjectDir, PackagePath);
        var config = PackageProjectLoader.Load(PackagePath);

        Assert.Empty(config.Users.Roles);
        using var reader = PackageReader.Open(PackagePath);
        Assert.False(reader.HasEntry("roles.bin"));
    }
}
