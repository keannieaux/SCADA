using SCADA.Core.Alarms;
using SCADA.Core.Tags;
using SCADA.Expressions;
using SCADA.Package.Builder;
using SCADA.Package.Builder.Sections;
using SCADA.Package.Sections;
using SCADA.Runtime.TagTable;

namespace SCADA.Package.Tests;

/// <summary>
/// Секция alarms.bin: round-trip сериализации и полный цикл
/// alarms.json → .scadapkg → AlarmConfiguration со скомпилированными
/// правилами (docs/M5-plan.md §6).
/// </summary>
public class AlarmsSectionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    private string ProjectDir => Path.Combine(_dir, "project");
    private string PackagePath => Path.Combine(_dir, "project.scadapkg");

    public AlarmsSectionTests()
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
                {"id": 0, "name": "Boiler1.Temp", "dataType": "analog", "deviceId": 0, "address": "sin:10"},
                {"id": 1, "name": "Pump1.Running", "dataType": "discrete", "deviceId": 0, "address": "square:5"}
              ]
            }
            """);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private void WriteAlarmsJson(string? soundFile = "sounds/critical.wav")
    {
        string sound = soundFile is null
            ? "\"sound\": { \"enabled\": false },"
            : $"\"sound\": {{ \"enabled\": true, \"files\": {{ \"Critical\": \"{soundFile}\" }} }},";

        File.WriteAllText(Path.Combine(ProjectDir, "alarms.json"), $$"""
            {
              "formatVersion": 1,
              "templates": {
                "thresholdActive": "{Severity}: {Description}"
              },
              {{sound}}
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
                  "name": "TEMP_HIGH",
                  "type": "Expression",
                  "condition": "Boiler1.Temp > 80",
                  "severity": "High",
                  "requiresAck": false,
                  "description": "Высокая температура"
                }
              ]
            }
            """);
    }

    [Fact]
    public void RoundTrip_ConfigurationSurvives()
    {
        var config = new AlarmConfiguration
        {
            Rules =
            [
                new AlarmRule
                {
                    Name = "Boiler1.Temp",
                    Type = AlarmType.Threshold,
                    TagName = "Boiler1.Temp",
                    Limits =
                    [
                        new ThresholdLimit { Kind = ThresholdKind.HiHi, Value = 95, Severity = AlarmSeverity.Critical },
                        new ThresholdLimit { Kind = ThresholdKind.LoLo, Value = 10 }
                    ],
                    Hysteresis = 2,
                    MinDurationMs = 1000,
                    Area = "Котельная",
                    RequiresAck = true,
                    Description = "Температура котла вне уставок",
                    MessageTemplate = "{Severity}! {Description}",
                    CompiledTagIndices = [0]
                },
                new AlarmRule
                {
                    Name = "TEMP_HIGH",
                    Type = AlarmType.Expression,
                    Condition = "Boiler1.Temp > 80",
                    Severity = AlarmSeverity.High,
                    RequiresAck = false,
                    Description = "Высокая температура",
                    CompiledExpressionIndex = 1,
                    CompiledTagIndices = [0]
                }
            ],
            Templates = new Dictionary<string, string> { ["thresholdActive"] = "{Severity}: {Description}" },
            Sound = new SoundConfiguration
            {
                Enabled = true,
                Files = { [AlarmSeverity.Critical] = "sounds/critical.wav" }
            },
            Defaults = new AlarmDefaults { MinDurationMs = 500 }
        };

        var restored = AlarmsSectionReader.Read(AlarmsSectionWriter.Write(config));

        var threshold = restored.Rules[0];
        Assert.Equal("Boiler1.Temp", threshold.Name);
        Assert.Equal(AlarmType.Threshold, threshold.Type);
        Assert.Equal("Boiler1.Temp", threshold.TagName);
        Assert.Equal(2, threshold.Limits!.Count);
        Assert.Equal(ThresholdKind.HiHi, threshold.Limits[0].Kind);
        Assert.Equal(95, threshold.Limits[0].Value);
        Assert.Equal(AlarmSeverity.Critical, threshold.Limits[0].Severity);
        Assert.Null(threshold.Limits[1].Severity);
        Assert.Equal(2, threshold.Hysteresis);
        Assert.Equal(1000, threshold.MinDurationMs);
        Assert.Equal("Котельная", threshold.Area);
        Assert.True(threshold.RequiresAck);
        Assert.Equal("{Severity}! {Description}", threshold.MessageTemplate);
        Assert.Equal([0], threshold.CompiledTagIndices!);
        Assert.Null(threshold.CompiledExpressionIndex);

        var expression = restored.Rules[1];
        Assert.Equal(AlarmType.Expression, expression.Type);
        Assert.Equal("Boiler1.Temp > 80", expression.Condition);
        Assert.Equal(AlarmSeverity.High, expression.Severity);
        Assert.False(expression.RequiresAck);
        Assert.Null(expression.MessageTemplate);
        Assert.Null(expression.MinDurationMs);
        Assert.Null(expression.TagName);
        Assert.Null(expression.Limits);
        Assert.Equal(1, expression.CompiledExpressionIndex);

        Assert.Equal("{Severity}: {Description}", restored.Templates["thresholdActive"]);
        Assert.True(restored.Sound.Enabled);
        Assert.Equal("sounds/critical.wav", restored.Sound.Files[AlarmSeverity.Critical]);
        Assert.Equal(500, restored.Defaults.MinDurationMs);
    }

    [Fact]
    public void PackageCycle_RulesCompiledAndExecutable()
    {
        WriteAlarmsJson();
        Directory.CreateDirectory(Path.Combine(ProjectDir, "sounds"));
        File.WriteAllBytes(Path.Combine(ProjectDir, "sounds", "critical.wav"), [1, 2, 3]);

        PackageBuilder.Build(ProjectDir, PackagePath);

        using var reader = PackageReader.Open(PackagePath);
        var config = PackageProjectLoader.Load(reader);
        var pool = PackageProjectLoader.LoadCodePool(reader);

        // секции на месте
        Assert.True(reader.HasEntry("alarms.bin"));
        Assert.True(reader.HasEntry("sounds/critical.wav"));
        Assert.Equal([1, 2, 3], reader.ReadEntry("sounds/critical.wav"));

        Assert.Equal(2, config.Alarms.Rules.Count);

        // threshold-правило: тег связан индексом (§11.6)
        var threshold = config.Alarms.Rules[0];
        Assert.Equal("Boiler1.Temp", threshold.Name);
        Assert.Equal([0], threshold.CompiledTagIndices!);
        Assert.Null(threshold.CompiledExpressionIndex);

        // expression-правило: индекс в пуле code.bin, выражение исполняется
        var expression = config.Alarms.Rules[1];
        int poolIndex = Assert.IsType<int>(expression.CompiledExpressionIndex);
        Assert.Equal([0], pool.Expressions[poolIndex].TagIndices);

        var table = new TagTable(capacity: 2);
        table.Write(new TagId(0), new TagValue(100.0, 1000, Quality.Good));
        var context = new EvaluationContext { Tags = table };
        Assert.Equal(1.0, ExpressionVM.Evaluate(pool.ToExpression(poolIndex), context));
    }

    [Fact]
    public void PackageCycle_DuplicateConditions_SharePoolEntry()
    {
        // два правила с одинаковым условием → одна запись в пуле (§14.2)
        File.WriteAllText(Path.Combine(ProjectDir, "alarms.json"), """
            {
              "formatVersion": 1,
              "rules": [
                { "name": "A", "type": "Expression", "condition": "Boiler1.Temp > 80" },
                { "name": "B", "type": "Expression", "condition": "Boiler1.Temp > 80" }
              ]
            }
            """);

        PackageBuilder.Build(ProjectDir, PackagePath);

        using var reader = PackageReader.Open(PackagePath);
        var config = PackageProjectLoader.Load(reader);
        var pool = PackageProjectLoader.LoadCodePool(reader);

        Assert.Single(pool.Expressions);
        Assert.Equal(config.Alarms.Rules[0].CompiledExpressionIndex,
            config.Alarms.Rules[1].CompiledExpressionIndex);
    }

    [Fact]
    public void PackageCycle_MissingSoundFile_FailsBuild()
    {
        WriteAlarmsJson(soundFile: "sounds/absent.wav"); // файл не создаём

        var ex = Assert.Throws<InvalidOperationException>(
            () => PackageBuilder.Build(ProjectDir, PackagePath));
        Assert.Contains("sounds/absent.wav", ex.Message);
    }

    [Fact]
    public void PackageCycle_BadExpression_FailsBuild()
    {
        File.WriteAllText(Path.Combine(ProjectDir, "alarms.json"), """
            {
              "formatVersion": 1,
              "rules": [
                { "name": "BROKEN", "type": "Expression", "condition": "Unknown.Tag > 80" }
              ]
            }
            """);

        var ex = Assert.Throws<InvalidOperationException>(
            () => PackageBuilder.Build(ProjectDir, PackagePath));
        Assert.Contains("BROKEN", ex.Message);
    }

    [Fact]
    public void PackageCycle_NoAlarmsJson_NoSection()
    {
        PackageBuilder.Build(ProjectDir, PackagePath);

        using var reader = PackageReader.Open(PackagePath);
        Assert.False(reader.HasEntry("alarms.bin"));

        var config = PackageProjectLoader.Load(reader);
        Assert.Empty(config.Alarms.Rules);
    }
}
