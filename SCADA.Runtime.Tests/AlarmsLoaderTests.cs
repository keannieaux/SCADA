using SCADA.Core.Alarms;
using SCADA.Runtime.Configuration;

namespace SCADA.Runtime.Tests;

/// <summary>
/// Загрузка и валидация alarms.json (docs/M5-plan.md §2.2, §5).
/// Файл опционален: его отсутствие = проект без аварий.
/// </summary>
public class AlarmsLoaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

    public AlarmsLoaderTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private void WriteFile(string name, string content) =>
        File.WriteAllText(Path.Combine(_dir, name), content);

    // Минимальный валидный проект, к которому тесты добавляют alarms.json
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
                {"id": 0, "name": "Boiler1.Temp", "dataType": "analog", "deviceId": 0},
                {"id": 1, "name": "Pump1.Running", "dataType": "discrete", "deviceId": 0}
              ]
            }
            """);
    }

    private void WriteValidAlarms()
    {
        WriteFile("alarms.json", """
            {
              "formatVersion": 1,
              "templates": {
                "thresholdActive": "{Severity}: {Description}"
              },
              "sound": {
                "enabled": true,
                "files": { "Critical": "sounds/critical.wav" }
              },
              "defaults": { "minDurationMs": 500 },
              "rules": [
                {
                  "name": "Boiler1.Temp",
                  "type": "Threshold",
                  "tagName": "Boiler1.Temp",
                  "limits": [
                    { "kind": "HiHi", "value": 95, "severity": "Critical" },
                    { "kind": "Hi",   "value": 80, "severity": "High" },
                    { "kind": "LoLo", "value": 10 }
                  ],
                  "hysteresis": 2,
                  "minDurationMs": 1000,
                  "area": "Котельная",
                  "requiresAck": true,
                  "description": "Температура котла вне уставок"
                },
                {
                  "name": "PUMP_FAILURE",
                  "type": "Expression",
                  "condition": "Pump1.Running && Boiler1.Temp > 90",
                  "severity": "Critical",
                  "area": "Насосная",
                  "requiresAck": true,
                  "description": "Аварийная ситуация насосов"
                }
              ]
            }
            """);
    }

    [Fact]
    public void Load_WithoutAlarmsFile_EmptyAlarmsConfiguration()
    {
        WriteValidProject();

        var config = ProjectLoader.Load(_dir);

        Assert.NotNull(config.Alarms);
        Assert.Empty(config.Alarms.Rules);
    }

    [Fact]
    public void Load_ValidAlarms_ParsesRulesAndSettings()
    {
        WriteValidProject();
        WriteValidAlarms();

        var config = ProjectLoader.Load(_dir);

        Assert.Equal(2, config.Alarms.Rules.Count);
        Assert.Equal(500, config.Alarms.Defaults.MinDurationMs);
        Assert.Equal("{Severity}: {Description}", config.Alarms.Templates["thresholdActive"]);
        Assert.True(config.Alarms.Sound.Enabled);
        Assert.Equal("sounds/critical.wav", config.Alarms.Sound.Files[AlarmSeverity.Critical]);

        var threshold = config.Alarms.Rules[0];
        Assert.Equal(AlarmType.Threshold, threshold.Type);
        Assert.Equal("Boiler1.Temp", threshold.TagName);
        Assert.Equal(3, threshold.Limits!.Count);
        Assert.Equal(ThresholdKind.HiHi, threshold.Limits[0].Kind);
        Assert.Equal(95, threshold.Limits[0].Value);
        Assert.Equal(AlarmSeverity.Critical, threshold.Limits[0].Severity);
        Assert.Null(threshold.Limits[2].Severity); // fallback на severity правила
        Assert.Equal(2, threshold.Hysteresis);
        Assert.Equal(1000, threshold.MinDurationMs);
        Assert.True(threshold.RequiresAck);

        var expression = config.Alarms.Rules[1];
        Assert.Equal(AlarmType.Expression, expression.Type);
        Assert.Equal("Pump1.Running && Boiler1.Temp > 90", expression.Condition);
        Assert.Null(expression.MinDurationMs); // действует дефолт проекта
    }

    [Fact]
    public void Load_InvalidAlarmsJson_Throws()
    {
        WriteValidProject();
        WriteFile("alarms.json", """{"formatVersion": 1, "rules": [""");

        var ex = Assert.Throws<ProjectConfigurationException>(() => ProjectLoader.Load(_dir));

        Assert.Contains(ex.Errors, e => e.Contains("alarms.json") && e.Contains("ошибка JSON"));
    }

    [Fact]
    public void Load_AlarmsWrongFormatVersion_Throws()
    {
        WriteValidProject();
        WriteValidAlarms();
        WriteFile("alarms.json", """{"formatVersion": 2, "rules": []}""");

        var ex = Assert.Throws<ProjectConfigurationException>(() => ProjectLoader.Load(_dir));

        Assert.Contains(ex.Errors, e => e.Contains("alarms.json") && e.Contains("версия формата"));
    }

    [Fact]
    public void Load_DuplicateRuleNames_Throws()
    {
        WriteValidProject();
        WriteFile("alarms.json", """
            {
              "formatVersion": 1,
              "rules": [
                {"name": "R1", "type": "Expression", "condition": "Pump1.Running"},
                {"name": "R1", "type": "Expression", "condition": "!Pump1.Running"}
              ]
            }
            """);

        var ex = Assert.Throws<ProjectConfigurationException>(() => ProjectLoader.Load(_dir));

        Assert.Contains(ex.Errors, e => e.Contains("Дубликат имени правила"));
    }

    [Fact]
    public void Load_ThresholdWithUnknownTag_Throws()
    {
        WriteValidProject();
        WriteFile("alarms.json", """
            {
              "formatVersion": 1,
              "rules": [
                {"name": "R1", "type": "Threshold", "tagName": "No.Such.Tag",
                 "limits": [{"kind": "Hi", "value": 80}]}
              ]
            }
            """);

        var ex = Assert.Throws<ProjectConfigurationException>(() => ProjectLoader.Load(_dir));

        Assert.Contains(ex.Errors, e => e.Contains("R1") && e.Contains("не найден"));
    }

    [Fact]
    public void Load_ThresholdWithoutLimits_Throws()
    {
        WriteValidProject();
        WriteFile("alarms.json", """
            {
              "formatVersion": 1,
              "rules": [
                {"name": "R1", "type": "Threshold", "tagName": "Boiler1.Temp"}
              ]
            }
            """);

        var ex = Assert.Throws<ProjectConfigurationException>(() => ProjectLoader.Load(_dir));

        Assert.Contains(ex.Errors, e => e.Contains("R1") && e.Contains("limits"));
    }

    [Fact]
    public void Load_ThresholdUnorderedLimits_Throws()
    {
        WriteValidProject();
        WriteFile("alarms.json", """
            {
              "formatVersion": 1,
              "rules": [
                {"name": "R1", "type": "Threshold", "tagName": "Boiler1.Temp",
                 "limits": [
                   {"kind": "HiHi", "value": 70},
                   {"kind": "Hi",   "value": 80}
                 ]}
              ]
            }
            """);

        var ex = Assert.Throws<ProjectConfigurationException>(() => ProjectLoader.Load(_dir));

        Assert.Contains(ex.Errors, e => e.Contains("R1") && e.Contains("HiHi") && e.Contains("больше"));
    }

    [Fact]
    public void Load_ExpressionWithoutCondition_Throws()
    {
        WriteValidProject();
        WriteFile("alarms.json", """
            {
              "formatVersion": 1,
              "rules": [{"name": "R1", "type": "Expression"}]
            }
            """);

        var ex = Assert.Throws<ProjectConfigurationException>(() => ProjectLoader.Load(_dir));

        Assert.Contains(ex.Errors, e => e.Contains("R1") && e.Contains("condition"));
    }
}
