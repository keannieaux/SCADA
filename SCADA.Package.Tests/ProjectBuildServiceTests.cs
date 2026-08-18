using SCADA.Package.Builder;

namespace SCADA.Package.Tests;

/// <summary>
/// ProjectBuildService: структурированные диагностики сборки проекта —
/// для будущей панели «Проблемы» в IDE. Пакет пишется только при Success.
/// </summary>
public class ProjectBuildServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    private string ProjectDir => Path.Combine(_dir, "project");
    private string PackagePath => Path.Combine(_dir, "project.scadapkg");

    public ProjectBuildServiceTests()
    {
        Directory.CreateDirectory(ProjectDir);
        File.WriteAllText(Path.Combine(ProjectDir, "project.json"),
            """{"formatVersion": 1, "name": "BoilerRoom", "version": "3.1"}""");
        File.WriteAllText(Path.Combine(ProjectDir, "devices.json"), """
            {
              "formatVersion": 1,
              "channels": [{"id": 0, "name": "Line1", "channelType": "modbus-tcp", "configuration": "192.168.0.10:502"}],
              "devices": [{"id": 0, "name": "PLC1", "driverName": "simulator", "channelId": 0}]
            }
            """);
        File.WriteAllText(Path.Combine(ProjectDir, "tags.json"), """
            {
              "formatVersion": 1,
              "tags": [
                {"id": 0, "name": "Boiler1.Temp", "dataType": "analog", "deviceId": 0,
                 "address": "sin:10", "minValue": 0, "maxValue": 150, "units": "°C"},
                {"id": 1, "name": "Pump1.Running", "dataType": "discrete", "deviceId": 0, "address": "square:5"}
              ]
            }
            """);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void ValidProject_Success_PackageWrittenWithVolumeInfo()
    {
        var result = ProjectBuildService.Build(ProjectDir, PackagePath);

        Assert.True(result.Success);
        Assert.Equal(PackagePath, result.PackagePath);
        Assert.True(File.Exists(PackagePath));

        // отчёт об объёме архива — Info-диагностики (ТЗ §4.3)
        Assert.Contains(result.Diagnostics,
            d => d.Severity == BuildSeverity.Info && d.Source == "archive");
        Assert.DoesNotContain(result.Diagnostics,
            d => d.Severity == BuildSeverity.Error);
    }

    [Fact]
    public void BadExpressionRule_Fails_DiagnosticsContainRuleName()
    {
        File.WriteAllText(Path.Combine(ProjectDir, "alarms.json"), """
            {
              "formatVersion": 1,
              "rules": [
                { "name": "BROKEN", "type": "Expression", "condition": "Unknown.Tag > 80" }
              ]
            }
            """);

        var result = ProjectBuildService.Build(ProjectDir, PackagePath);

        Assert.False(result.Success);
        Assert.Null(result.PackagePath);
        Assert.False(File.Exists(PackagePath)); // пакет не пишется при ошибках

        var diagnostic = Assert.Single(result.Diagnostics,
            d => d.Severity == BuildSeverity.Error && d.Source == "alarm:BROKEN");
        Assert.Contains("BROKEN", diagnostic.Message);
    }

    [Fact]
    public void BadTagsJson_Fails_WithProjectErrors()
    {
        File.WriteAllText(Path.Combine(ProjectDir, "tags.json"),
            """{"formatVersion": 1, "tags": [""");

        var result = ProjectBuildService.Build(ProjectDir, PackagePath);

        Assert.False(result.Success);
        Assert.Null(result.PackagePath);
        Assert.False(File.Exists(PackagePath));
        Assert.Contains(result.Diagnostics,
            d => d.Severity == BuildSeverity.Error && d.Source == "project");
    }
}
