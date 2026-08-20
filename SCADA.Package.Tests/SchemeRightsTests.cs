using SCADA.Core.Schemes;
using SCADA.Package.Builder;
using SCADA.Package.Builder.Sections;
using SCADA.Package.Sections;

namespace SCADA.Package.Tests;

/// <summary>
/// Права на схемах (docs/users-plan.md §5): перенос через исходники и
/// секцию пакета, умолчания и сверка использованных прав с ролями проекта
/// при сборке.
/// </summary>
public class SchemeRightsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    private string ProjectDir => Path.Combine(_dir, "project");
    private string PackagePath => Path.Combine(_dir, "project.scadapkg");

    public SchemeRightsTests()
    {
        Directory.CreateDirectory(ProjectDir);
        File.WriteAllText(Path.Combine(ProjectDir, "project.json"),
            """{"formatVersion": 1, "name": "BoilerRoom", "version": "1.0"}""");
        File.WriteAllText(Path.Combine(ProjectDir, "devices.json"), """
            {
              "formatVersion": 1,
              "channels": [{"id": 0, "name": "Line1", "channelType": "none"}],
              "devices": [{"id": 0, "name": "PLC1", "driverName": "simulator", "channelId": 0}]
            }
            """);
        File.WriteAllText(Path.Combine(ProjectDir, "tags.json"), """
            {
              "formatVersion": 1,
              "tags": [
                {"id": 0, "name": "Boiler1.Setpoint", "dataType": "analog", "deviceId": 0,
                 "address": "const:20", "isWritable": true}
              ]
            }
            """);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private void WriteScheme(string fileName, string json)
    {
        Directory.CreateDirectory(Path.Combine(ProjectDir, "schemes"));
        File.WriteAllText(Path.Combine(ProjectDir, "schemes", fileName), json);
    }

    private void WriteTemplate(string fileName, string json)
    {
        Directory.CreateDirectory(Path.Combine(ProjectDir, "templates"));
        File.WriteAllText(Path.Combine(ProjectDir, "templates", fileName), json);
    }

    private void WriteRoles(string json)
        => File.WriteAllText(Path.Combine(ProjectDir, "roles.json"), json);

    // --- секция пакета ---

    [Fact]
    public void Section_RoundTripsRightsOfSchemeElementAndAction()
    {
        var scheme = new Scheme
        {
            Id = Guid.NewGuid(),
            Name = "Уставки",
            RequiredRight = "Уставки.View",
            Elements =
            [
                new SchemeElement
                {
                    Id = Guid.NewGuid(), Kind = ElementKind.Rectangle,
                    X = 0, Y = 0, Width = 10, Height = 10,
                    RequiredRight = "Уставки.Edit",
                    DeniedState = DeniedState.Hidden,
                    Events =
                    [
                        new SchemeEvent
                        {
                            Kind = SchemeEventKind.Click,
                            Actions =
                            [
                                new WriteTagAction(SchemeTagRef.Absolute("Boiler1.Setpoint"), 42)
                                {
                                    RequiredRight = "Насосная.Control",
                                    DeniedFeedback = DeniedFeedback.Silent
                                }
                            ]
                        }
                    ]
                }
            ]
        };

        var read = SchemeSectionReader.ReadScheme(SchemeSectionWriter.Write(scheme));

        Assert.Equal("Уставки.View", read.RequiredRight);
        var element = Assert.Single(read.Elements);
        Assert.Equal("Уставки.Edit", element.RequiredRight);
        Assert.Equal(DeniedState.Hidden, element.DeniedState);
        var action = element.Events.Single().Actions.Single();
        Assert.Equal("Насосная.Control", action.RequiredRight);
        Assert.Equal(DeniedFeedback.Silent, action.DeniedFeedback);
    }

    [Fact]
    public void Section_WithoutRights_KeepsDefaults()
    {
        var scheme = new Scheme
        {
            Id = Guid.NewGuid(),
            Name = "Обзор",
            Elements =
            [
                new SchemeElement
                {
                    Id = Guid.NewGuid(), Kind = ElementKind.Rectangle,
                    X = 0, Y = 0, Width = 10, Height = 10,
                    Events =
                    [
                        new SchemeEvent
                        {
                            Kind = SchemeEventKind.Click,
                            Actions = [new BackAction()]
                        }
                    ]
                }
            ]
        };

        var read = SchemeSectionReader.ReadScheme(SchemeSectionWriter.Write(scheme));

        Assert.Null(read.RequiredRight);
        var element = Assert.Single(read.Elements);
        Assert.Null(element.RequiredRight);
        Assert.Equal(DeniedState.Disabled, element.DeniedState); // умолчание §5
        var action = element.Events.Single().Actions.Single();
        Assert.Null(action.RequiredRight);
        Assert.Equal(DeniedFeedback.Notify, action.DeniedFeedback); // умолчание §5
    }

    // --- сборка проекта ---

    private const string SchemeWithRights = """
        {
          "requiredRight": "Уставки.View",
          "elements": [{
            "kind": "Rectangle", "x": 0, "y": 0, "width": 100, "height": 50,
            "requiredRight": "Уставки.Edit", "deniedState": "Hidden",
            "events": [{
              "kind": "Click",
              "actions": [{"type": "WriteTag", "tag": "Boiler1.Setpoint", "value": 42,
                           "requiredRight": "Уставки.Edit"}]
            }]
          }]
        }
        """;

    [Fact]
    public void Build_RightsTravelFromSourcesToPackage()
    {
        WriteRoles("""
            {
              "formatVersion": 1,
              "roles": [{"name": "Технолог", "permissions": ["View", "Уставки.View", "Уставки.Edit"]}]
            }
            """);
        WriteScheme("setpoints.scheme", SchemeWithRights);

        var result = ProjectBuildService.Build(ProjectDir, PackagePath);

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        Assert.DoesNotContain(result.Diagnostics, d => d.Source == "rights");

        var scheme = PackageProjectLoader.Load(PackagePath).Schemes.Single();
        Assert.Equal("Уставки.View", scheme.RequiredRight);
        Assert.Equal("Уставки.Edit", scheme.Elements[0].RequiredRight);
        Assert.Equal(DeniedState.Hidden, scheme.Elements[0].DeniedState);
        Assert.Equal("Уставки.Edit",
            scheme.Elements[0].Events.Single().Actions.Single().RequiredRight);
    }

    [Fact]
    public void Build_RightNotGrantedByAnyRole_Warns()
    {
        // роль даёт «Уставки.Edit», на схеме — «Уставки.Еdit» с русской «Е»
        WriteRoles("""
            {
              "formatVersion": 1,
              "roles": [{"name": "Технолог", "permissions": ["View", "Уставки.View", "Уставки.Edit"]}]
            }
            """);
        WriteScheme("setpoints.scheme", """
            {
              "elements": [{
                "kind": "Rectangle", "x": 0, "y": 0, "width": 100, "height": 50,
                "requiredRight": "Уставки.Еdit"
              }]
            }
            """);

        var result = ProjectBuildService.Build(ProjectDir, PackagePath);

        // опечатка не валит сборку — это предупреждение
        Assert.True(result.Success);
        var warning = Assert.Single(result.Diagnostics, d => d.Source == "rights");
        Assert.Equal(BuildSeverity.Warning, warning.Severity);
        Assert.Contains("Уставки.Еdit", warning.Message);
        Assert.Contains("setpoints", warning.Message);
    }

    [Fact]
    public void Build_SystemRightOnElement_NotReportedAsTypo()
    {
        // системные права выданы по определению — их проверяет ядро
        WriteRoles("""
            {"formatVersion": 1, "roles": [{"name": "Оператор", "permissions": ["View"]}]}
            """);
        WriteScheme("overview.scheme", """
            {
              "elements": [{
                "kind": "Rectangle", "x": 0, "y": 0, "width": 100, "height": 50,
                "requiredRight": "Operate"
              }]
            }
            """);

        var result = ProjectBuildService.Build(ProjectDir, PackagePath);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, d => d.Source == "rights");
    }

    [Fact]
    public void Build_RightsWithoutRolesFile_WarnsOnce()
    {
        WriteScheme("setpoints.scheme", SchemeWithRights);

        var result = ProjectBuildService.Build(ProjectDir, PackagePath);

        Assert.True(result.Success);
        var warning = Assert.Single(result.Diagnostics, d => d.Source == "rights");
        Assert.Contains("нет ни одной роли", warning.Message);
    }

    [Fact]
    public void Build_RequiredRightOnTemplate_Fails()
    {
        WriteTemplate("pump.scheme", """
            {
              "requiredRight": "Насосная.Control",
              "parameters": [{"name": "Prefix"}],
              "elements": []
            }
            """);

        var result = ProjectBuildService.Build(ProjectDir, PackagePath);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics,
            d => d.Severity == BuildSeverity.Error && d.Message.Contains("requiredRight"));
    }
}
