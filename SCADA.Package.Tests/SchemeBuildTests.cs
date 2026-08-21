using SCADA.Core.Schemes;
using SCADA.Core.Tags;
using SCADA.Package.Builder;
using SCADA.Package.Sections;

namespace SCADA.Package.Tests;

/// <summary>
/// Шаг сборки схем в ProjectBuildService (концепт §11.4): компиляция
/// выражений привязок/условий в общий пул code.bin, секции schemes/ и
/// templates/, валидация ссылок на теги и шаблоны, ассеты.
/// </summary>
public class SchemeBuildTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    private string ProjectDir => Path.Combine(_dir, "project");
    private string PackagePath => Path.Combine(_dir, "project.scadapkg");

    public SchemeBuildTests()
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

    private void WriteAlarms(string json)
        => File.WriteAllText(Path.Combine(ProjectDir, "alarms.json"), json);

    [Fact]
    public void AlarmSystemTags_CompileInSchemeBindings()
    {
        // системные теги аварий генерируются до компиляции схем (концепт §10,
        // §11.4): привязка на @Alarm.*/@AlarmSystem.* — обычная компиляция
        WriteAlarms("""
            {
              "formatVersion": 1,
              "rules": [{
                "name": "Котельная.Котёл1.Перегрев", "type": "Threshold",
                "tagName": "Boiler1.Temp",
                "limits": [{"kind": "Hi", "value": 80}]
              }]
            }
            """);
        WriteScheme("overview.scheme", """
            {
              "elements": [{
                "kind": "Rectangle", "x": 0, "y": 0, "width": 100, "height": 50,
                "bindings": [
                  {"property": 10, "expression": "@Alarm.Котельная.Котёл1.Перегрев.Active",
                   "mapping": "Discrete",
                   "stops": [{"input": 0, "output": "#FF33383D"},
                             {"input": 1, "output": "#FFFF0000"}]},
                  {"property": 5, "expression": "@AlarmSystem.AnyUnacked > 0"},
                  {"property": 1, "expression": "@AlarmGroup.Котельная.Count"}]
              }]
            }
            """);

        var result = ProjectBuildService.Build(ProjectDir, PackagePath);

        Assert.True(result.Success, string.Join("; ",
            result.Diagnostics.Select(d => d.Message)));
        using var reader = PackageReader.Open(PackagePath);
        var scheme = PackageProjectLoader.Load(reader).Schemes.Single();
        Assert.All(scheme.Elements[0].Bindings,
            b => Assert.NotNull(b.CompiledExpressionIndex));
    }

    [Fact]
    public void UnknownAlarmRuleTag_Fails()
    {
        // правила нет — тега нет: висячая ссылка ловится при сборке
        WriteScheme("overview.scheme", """
            {
              "elements": [{
                "kind": "Rectangle", "x": 0, "y": 0, "width": 100, "height": 50,
                "bindings": [{"property": 5, "expression": "@Alarm.Нет.Такого.Правила.Active"}]
              }]
            }
            """);

        var result = ProjectBuildService.Build(ProjectDir, PackagePath);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics,
            d => d.Severity == BuildSeverity.Error && d.Source == "scheme:overview");
    }

    // схема с привязкой-выражением и действием с условием (§11.4)
    private const string OverviewScheme = """
        {
          "elements": [
            {
              "name": "pipe", "kind": "Rectangle",
              "x": 0, "y": 0, "width": 100, "height": 50,
              "properties": [{"id": 10, "value": "#FF33383D"}],
              "bindings": [{"property": 10, "expression": "Boiler1.Temp * 2", "mapping": "Interpolated",
                            "stops": [{"input": 0, "output": "#FF0000FF"},
                                      {"input": 100, "output": "#FFFF0000"}]}],
              "events": [{"kind": "Click", "actions": [
                {"type": "WriteTag", "tag": "Pump1.Running", "value": 1,
                 "condition": "Boiler1.Temp > 80", "confirm": "Запустить насос?"}]}]
            }
          ]
        }
        """;

    [Fact]
    public void SchemeWithExpressions_Builds_SchemeAndCodeSectionsInPackage()
    {
        WriteScheme("overview.scheme", OverviewScheme);

        var result = ProjectBuildService.Build(ProjectDir, PackagePath);

        Assert.True(result.Success);
        using var reader = PackageReader.Open(PackagePath);

        // секция схемы + выражения в общем пуле code.bin
        Assert.True(reader.HasEntry("schemes/overview.bin"));
        var pool = CodeSectionReader.Read(reader.ReadEntry("code.bin"));
        Assert.Equal(2, pool.Expressions.Length); // привязка + условие действия

        // чтение пакета возвращает схему со скомпилированными индексами
        var config = PackageProjectLoader.Load(reader);
        var scheme = Assert.Single(config.Schemes);
        Assert.Equal("overview", scheme.Name);

        var binding = Assert.Single(scheme.Elements[0].Bindings);
        Assert.NotNull(binding.CompiledExpressionIndex);
        Assert.NotNull(binding.CompiledTagIndices);

        var action = Assert.Single(scheme.Elements[0].Events[0].Actions);
        var writeTag = Assert.IsType<WriteTagAction>(action);
        Assert.Equal(SchemeTagRef.Absolute("Pump1.Running"), writeTag.Tag);
        Assert.Equal("Запустить насос?", writeTag.Confirmation);
        Assert.NotNull(writeTag.CompiledConditionIndex);
        // текст выражений в пакет не пишется (§11.4)
        Assert.Equal("", binding.Expression);
        Assert.Null(action.Condition);
    }

    [Fact]
    public void SchemeNameAndId_DefaultsFromFileName()
    {
        WriteScheme("overview.scheme", """{"elements": []}""");

        var result = ProjectBuildService.Build(ProjectDir, PackagePath);

        Assert.True(result.Success);
        using var reader = PackageReader.Open(PackagePath);
        Assert.True(reader.HasEntry("schemes/overview.bin")); // имя — из имени файла
        var scheme = PackageProjectLoader.Load(reader).Schemes.Single();
        Assert.NotEqual(Guid.Empty, scheme.Id); // id сгенерирован
    }

    [Fact]
    public void BadBindingExpression_Fails_WithSchemeDiagnostic()
    {
        WriteScheme("overview.scheme", """
            {
              "elements": [{
                "kind": "Rectangle", "x": 0, "y": 0, "width": 10, "height": 10,
                "bindings": [{"property": 10, "expression": "Unknown.Tag * 2"}]
              }]
            }
            """);

        var result = ProjectBuildService.Build(ProjectDir, PackagePath);

        Assert.False(result.Success);
        Assert.False(File.Exists(PackagePath));
        var diagnostic = Assert.Single(result.Diagnostics,
            d => d.Severity == BuildSeverity.Error && d.Source == "scheme:overview");
        Assert.Contains("Unknown.Tag", diagnostic.Message);
    }

    [Fact]
    public void NonAnimatableBinding_FailsAtLoad()
    {
        // id 12 (BorderThickness) неанимируем — ошибка ещё при загрузке исходника (§3.2)
        WriteScheme("overview.scheme", """
            {
              "elements": [{
                "kind": "Rectangle", "x": 0, "y": 0, "width": 10, "height": 10,
                "bindings": [{"property": 12, "expression": "Boiler1.Temp"}]
              }]
            }
            """);

        var result = ProjectBuildService.Build(ProjectDir, PackagePath);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics,
            d => d.Severity == BuildSeverity.Error && d.Source == "project"
                 && d.Message.Contains("schemes/overview.scheme"));
    }

    [Fact]
    public void UnknownPropertyInSource_FailsAtLoad()
    {
        // исходник строгий: неизвестный id свойства — ошибка, а не пропуск
        WriteScheme("overview.scheme", """
            {
              "elements": [{
                "kind": "Rectangle", "x": 0, "y": 0, "width": 10, "height": 10,
                "properties": [{"id": 999, "value": "1"}]
              }]
            }
            """);

        var result = ProjectBuildService.Build(ProjectDir, PackagePath);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics,
            d => d.Severity == BuildSeverity.Error && d.Message.Contains("999"));
    }

    [Fact]
    public void ReferencedSymbolMissing_Fails_ExistingSymbol_Packed()
    {
        const string symbolScheme = """
            {
              "elements": [{
                "kind": "Symbol", "x": 0, "y": 0, "width": 24, "height": 24,
                "properties": [{"id": 30, "value": "pump.svg"}]
              }]
            }
            """;

        // без файла — ошибка сборки (§11.4)
        WriteScheme("overview.scheme", symbolScheme);
        var failed = ProjectBuildService.Build(ProjectDir, PackagePath);
        Assert.False(failed.Success);
        Assert.Contains(failed.Diagnostics,
            d => d.Severity == BuildSeverity.Error && d.Source == "schemes:assets"
                 && d.Message.Contains("symbols/pump.svg"));

        // файл появился — пакет собирается, прихватывая и неиспользованные
        // файлы каталога (символы переиспользуются, §3)
        Directory.CreateDirectory(Path.Combine(ProjectDir, "symbols"));
        File.WriteAllText(Path.Combine(ProjectDir, "symbols", "pump.svg"), "<svg/>");
        File.WriteAllText(Path.Combine(ProjectDir, "symbols", "valve.svg"), "<svg/>");

        var result = ProjectBuildService.Build(ProjectDir, PackagePath);

        Assert.True(result.Success);
        using var reader = PackageReader.Open(PackagePath);
        Assert.True(reader.HasEntry("symbols/pump.svg"));
        Assert.True(reader.HasEntry("symbols/valve.svg"));
    }

    [Fact]
    public void InstanceOfUnknownTemplate_Fails()
    {
        WriteScheme("overview.scheme", """
            {
              "elements": [{
                "name": "pump7", "kind": "Instance", "templateName": "nope",
                "x": 0, "y": 0, "width": 40, "height": 40
              }]
            }
            """);

        var result = ProjectBuildService.Build(ProjectDir, PackagePath);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics,
            d => d.Severity == BuildSeverity.Error && d.Source == "scheme:overview"
                 && d.Message.Contains("nope"));
    }

    [Fact]
    public void InstanceWithUndeclaredParameter_Fails()
    {
        WriteTemplate("pump.scheme", """
            {
              "parameters": [{"name": "Prefix", "type": "String", "default": "Н1"}],
              "elements": []
            }
            """);
        WriteScheme("overview.scheme", """
            {
              "elements": [{
                "kind": "Instance", "templateName": "pump",
                "templateParameters": {"Wrong": "Н7"},
                "x": 0, "y": 0, "width": 40, "height": 40
              }]
            }
            """);

        var result = ProjectBuildService.Build(ProjectDir, PackagePath);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics,
            d => d.Severity == BuildSeverity.Error && d.Message.Contains("Wrong"));
    }

    [Fact]
    public void TemplateCycle_Fails()
    {
        // a включает b, b включает a — раскрытие экземпляра не завершится (§7)
        WriteTemplate("a.scheme", """
            {
              "elements": [{"kind": "Instance", "templateName": "b",
                            "x": 0, "y": 0, "width": 10, "height": 10}]
            }
            """);
        WriteTemplate("b.scheme", """
            {
              "elements": [{"kind": "Instance", "templateName": "a",
                            "x": 0, "y": 0, "width": 10, "height": 10}]
            }
            """);

        var result = ProjectBuildService.Build(ProjectDir, PackagePath);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics,
            d => d.Severity == BuildSeverity.Error && d.Message.Contains("Цикл"));
    }

    [Fact]
    public void TemplateWithParametricRef_Builds_TemplateSectionWritten()
    {
        // "{Prefix}.X" — параметрическая ссылка (§4.4): в шаблоне с
        // объявленным параметром это законно; резолв — при раскрытии (§7, B2)
        WriteTemplate("pump.scheme", """
            {
              "parameters": [{"name": "Prefix", "type": "String"}],
              "elements": [{
                "kind": "Rectangle", "x": 0, "y": 0, "width": 10, "height": 10,
                "bindings": [{"property": 1, "expression": "Prefix.Speed * 2"}],
                "events": [{"kind": "Click", "actions": [
                  {"type": "ToggleTag", "tag": "{Prefix}.Run"}]}]
              }]
            }
            """);

        var result = ProjectBuildService.Build(ProjectDir, PackagePath);

        Assert.True(result.Success);
        using var reader = PackageReader.Open(PackagePath);
        Assert.True(reader.HasEntry("templates/pump.bin"));

        var template = PackageProjectLoader.Load(reader).Templates.Single();
        Assert.Equal("pump", template.Name);
        var parameter = Assert.Single(template.Parameters);
        Assert.Equal("Prefix", parameter.Name);
        Assert.Equal(SchemeTagRef.Parametric("Prefix", ".Run"),
            ((ToggleTagAction)template.Elements[0].Events[0].Actions[0]).Tag);
    }

    [Fact]
    public void ParametricRefOutsideTemplate_Fails()
    {
        WriteScheme("overview.scheme", """
            {
              "elements": [{
                "kind": "Rectangle", "x": 0, "y": 0, "width": 10, "height": 10,
                "events": [{"kind": "Click", "actions": [
                  {"type": "ToggleTag", "tag": "{Prefix}.Run"}]}]
              }]
            }
            """);

        var result = ProjectBuildService.Build(ProjectDir, PackagePath);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics,
            d => d.Severity == BuildSeverity.Error && d.Source == "scheme:overview"
                 && d.Message.Contains("вне шаблона"));
    }

    [Fact]
    public void AbsoluteTagRefToUnknownTag_Fails()
    {
        WriteScheme("overview.scheme", """
            {
              "elements": [{
                "kind": "Rectangle", "x": 0, "y": 0, "width": 10, "height": 10,
                "events": [{"kind": "Click", "actions": [
                  {"type": "WriteTag", "tag": "Ghost.Tag", "value": 1}]}]
              }]
            }
            """);

        var result = ProjectBuildService.Build(ProjectDir, PackagePath);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics,
            d => d.Severity == BuildSeverity.Error && d.Source == "scheme:overview"
                 && d.Message.Contains("Ghost.Tag"));
    }

    [Fact]
    public void InvalidSchemeName_Fails()
    {
        // имя становится именем секции пакета — недопустимые символы пути запрещены
        WriteScheme("weird.scheme", """{"name": "a/b", "elements": []}""");

        var result = ProjectBuildService.Build(ProjectDir, PackagePath);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics,
            d => d.Severity == BuildSeverity.Error && d.Message.Contains("a/b"));
    }

    [Fact]
    public void SchemeLevelProperties_RoundtripThroughPackage()
    {
        WriteScheme("overview.scheme", """
            {
              "properties": [{"id": 100, "value": "#FF1B1D20"},
                             {"id": 101, "value": "1280"}],
              "elements": []
            }
            """);

        var result = ProjectBuildService.Build(ProjectDir, PackagePath);

        Assert.True(result.Success);
        using var reader = PackageReader.Open(PackagePath);
        var scheme = PackageProjectLoader.Load(reader).Schemes.Single();
        Assert.Equal(
            [
                new ElementProperty(100, PropertyValue.FromColor(0xFF1B1D20)),
                new ElementProperty(101, PropertyValue.FromNumber(1280)),
            ],
            scheme.Properties);
    }

    [Fact]
    public void UnknownSchemeLevelProperty_FailsAtLoad()
    {
        // исходник строгий и для свойств уровня схемы
        WriteScheme("overview.scheme", """
            {
              "properties": [{"id": 999, "value": "1"}],
              "elements": []
            }
            """);

        var result = ProjectBuildService.Build(ProjectDir, PackagePath);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics,
            d => d.Severity == BuildSeverity.Error && d.Message.Contains("999"));
    }

    [Fact]
    public void SchemeLevelEvents_BuildAndRoundtrip()
    {
        // события экрана (§5.1): условие действия компилируется в общий пул,
        // цепочка переживает пакет
        WriteScheme("overview.scheme", """
            {
              "events": [{"kind": "Opened", "actions": [
                {"type": "WriteTag", "tag": "Pump1.Running", "value": 1,
                 "condition": "Boiler1.Temp > 0"},
                {"type": "ShowDialog", "message": "Экран секции"}]}],
              "elements": []
            }
            """);

        var result = ProjectBuildService.Build(ProjectDir, PackagePath);

        Assert.True(result.Success);
        using var reader = PackageReader.Open(PackagePath);
        var pool = CodeSectionReader.Read(reader.ReadEntry("code.bin"));
        Assert.Single(pool.Expressions); // условие действия события экрана

        var scheme = PackageProjectLoader.Load(reader).Schemes.Single();
        var schemeEvent = Assert.Single(scheme.Events);
        Assert.Equal(SchemeEventKind.Opened, schemeEvent.Kind);
        Assert.Equal(2, schemeEvent.Actions.Count);
        var writeTag = Assert.IsType<WriteTagAction>(schemeEvent.Actions[0]);
        Assert.NotNull(writeTag.CompiledConditionIndex);
        Assert.Null(writeTag.Condition); // текст в пакет не пишется
        Assert.IsType<ShowDialogAction>(schemeEvent.Actions[1]);
    }

    [Fact]
    public void MisplacedEventKinds_FailAtLoad()
    {
        // указательное событие на уровне схемы и Opened на элементе — оба
        // бессмысленны, исходник строгий (§5.1)
        WriteScheme("overview.scheme", """
            {
              "events": [{"kind": "Click", "actions": []}],
              "elements": [{
                "kind": "Rectangle", "x": 0, "y": 0, "width": 10, "height": 10,
                "events": [{"kind": "Opened", "actions": []}]
              }]
            }
            """);

        var result = ProjectBuildService.Build(ProjectDir, PackagePath);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics,
            d => d.Severity == BuildSeverity.Error && d.Message.Contains("Click"));
        Assert.Contains(result.Diagnostics,
            d => d.Severity == BuildSeverity.Error && d.Message.Contains("Opened"));
    }

    [Fact]
    public void SchemeLevelEventWithUnknownTag_Fails()
    {
        WriteScheme("overview.scheme", """
            {
              "events": [{"kind": "Opened", "actions": [
                {"type": "WriteTag", "tag": "Ghost.Tag", "value": 1}]}],
              "elements": []
            }
            """);

        var result = ProjectBuildService.Build(ProjectDir, PackagePath);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics,
            d => d.Severity == BuildSeverity.Error && d.Source == "scheme:overview"
                 && d.Message.Contains("Ghost.Tag"));
    }

    [Fact]
    public void ControlWithConfigJson_Builds()
    {
        // списочный конфиг hosted-контрола — JSON в свойстве ConfigJson (§8)
        WriteScheme("overview.scheme", """
            {
              "elements": [{
                "kind": "Control", "controlType": "trend",
                "x": 0, "y": 0, "width": 400, "height": 200,
                "properties": [{"id": 50, "value": "{\"pens\":[{\"tag\":\"Boiler1.Temp\"}]}"}]
              }]
            }
            """);

        var result = ProjectBuildService.Build(ProjectDir, PackagePath);

        Assert.True(result.Success);
        using var reader = PackageReader.Open(PackagePath);
        var scheme = PackageProjectLoader.Load(reader).Schemes.Single();
        var property = Assert.Single(scheme.Elements[0].Properties);
        Assert.Equal(50, property.PropertyId);
        Assert.Equal("""{"pens":[{"tag":"Boiler1.Temp"}]}""", property.Value.Text);
    }

    [Fact]
    public void ControlWithoutControlType_FailsAtLoad()
    {
        WriteScheme("overview.scheme", """
            {
              "elements": [{"kind": "Control", "x": 0, "y": 0, "width": 10, "height": 10}]
            }
            """);

        var result = ProjectBuildService.Build(ProjectDir, PackagePath);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics,
            d => d.Severity == BuildSeverity.Error && d.Message.Contains("controlType"));
    }

    // строковые теги (концепт §4.6, A7): строковый тег живёт на внутреннем
    // устройстве, привязывается к String-свойству напрямую (без ВМ),
    // в числовых выражениях запрещён ошибкой сборки

    private void WriteDevicesWithInternal()
        => File.WriteAllText(Path.Combine(ProjectDir, "devices.json"), """
            {
              "formatVersion": 1,
              "channels": [
                {"id": 0, "name": "Line1", "channelType": "modbus-tcp", "configuration": "192.168.0.10:502"},
                {"id": 1, "name": "Local", "channelType": "none"}
              ],
              "devices": [
                {"id": 0, "name": "PLC1", "driverName": "simulator", "channelId": 0},
                {"id": 1, "name": "Local", "driverName": "internal", "channelId": 1}
              ]
            }
            """);

    private void WriteTagsWithString()
        => File.WriteAllText(Path.Combine(ProjectDir, "tags.json"), """
            {
              "formatVersion": 1,
              "tags": [
                {"id": 0, "name": "Boiler1.Temp", "dataType": "analog", "deviceId": 0, "address": "sin:10"},
                {"id": 1, "name": "Pump1.Running", "dataType": "discrete", "deviceId": 0, "address": "square:5"},
                {"id": 2, "name": "Status.Text", "dataType": "string", "deviceId": 1}
              ]
            }
            """);

    [Fact]
    public void DirectStringBinding_CompilesAsDirectReference()
    {
        // прямая строковая привязка: без выражения в пуле, прямая ссылка на тег
        WriteDevicesWithInternal();
        WriteTagsWithString();
        WriteScheme("overview.scheme", """
            {
              "elements": [{
                "kind": "Text", "x": 0, "y": 0, "width": 100, "height": 20,
                "bindings": [{"property": 7, "expression": "Status.Text"}]
              }]
            }
            """);

        var result = ProjectBuildService.Build(ProjectDir, PackagePath);

        Assert.True(result.Success, string.Join("; ",
            result.Diagnostics.Select(d => d.Message)));
        using var reader = PackageReader.Open(PackagePath);
        var scheme = PackageProjectLoader.Load(reader).Schemes.Single();
        var binding = Assert.Single(scheme.Elements[0].Bindings);
        Assert.Null(binding.CompiledExpressionIndex);
        Assert.Equal(new[] { 2 }, binding.CompiledTagIndices);
    }

    [Fact]
    public void DirectStringBinding_OnNumericProperty_Fails()
    {
        WriteDevicesWithInternal();
        WriteTagsWithString();
        WriteScheme("overview.scheme", """
            {
              "elements": [{
                "kind": "Rectangle", "x": 0, "y": 0, "width": 10, "height": 10,
                "bindings": [{"property": 1, "expression": "Status.Text"}]
              }]
            }
            """);

        var result = ProjectBuildService.Build(ProjectDir, PackagePath);

        Assert.False(result.Success);
        var diagnostic = Assert.Single(result.Diagnostics,
            d => d.Severity == BuildSeverity.Error && d.Source == "scheme:overview");
        Assert.Contains("Status.Text", diagnostic.Message);
    }

    [Fact]
    public void StringTagInExpression_Fails()
    {
        // ВМ числовая: строковый тег в выражении — ошибка сборки, а не молчаливый
        // числовой слот
        WriteDevicesWithInternal();
        WriteTagsWithString();
        WriteScheme("overview.scheme", """
            {
              "elements": [{
                "kind": "Rectangle", "x": 0, "y": 0, "width": 10, "height": 10,
                "bindings": [{"property": 1, "expression": "Status.Text > 0"}]
              }]
            }
            """);

        var result = ProjectBuildService.Build(ProjectDir, PackagePath);

        Assert.False(result.Success);
        var diagnostic = Assert.Single(result.Diagnostics,
            d => d.Severity == BuildSeverity.Error && d.Source == "scheme:overview");
        Assert.Contains("строковый тег", diagnostic.Message);
    }

    [Fact]
    public void ThresholdRule_OnStringTag_Fails()
    {
        WriteDevicesWithInternal();
        WriteTagsWithString();
        WriteAlarms("""
            {
              "formatVersion": 1,
              "rules": [{
                "name": "Текст.Порог", "type": "Threshold",
                "tagName": "Status.Text",
                "limits": [{"kind": "Hi", "value": 80}]
              }]
            }
            """);

        var result = ProjectBuildService.Build(ProjectDir, PackagePath);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics,
            d => d.Severity == BuildSeverity.Error && d.Message.Contains("строковый"));
    }

    [Fact]
    public void StringTag_SurvivesPackageRoundTrip()
    {
        WriteDevicesWithInternal();
        WriteTagsWithString();

        var result = ProjectBuildService.Build(ProjectDir, PackagePath);

        Assert.True(result.Success, string.Join("; ",
            result.Diagnostics.Select(d => d.Message)));
        using var reader = PackageReader.Open(PackagePath);
        var config = PackageProjectLoader.Load(reader);
        var tag = Assert.Single(config.Tags, t => t.Name == "Status.Text");
        Assert.Equal(TagDataType.String, tag.DataType);
    }

    [Fact]
    public void StringTag_OnExternalDevice_Fails()
    {
        // строковых драйверов пока нет: только internal (валидация при загрузке)
        File.WriteAllText(Path.Combine(ProjectDir, "tags.json"), """
            {
              "formatVersion": 1,
              "tags": [
                {"id": 0, "name": "Boiler1.Temp", "dataType": "analog", "deviceId": 0, "address": "sin:10"},
                {"id": 1, "name": "Status.Text", "dataType": "string", "deviceId": 0, "address": "40001"}
              ]
            }
            """);

        var result = ProjectBuildService.Build(ProjectDir, PackagePath);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics,
            d => d.Severity == BuildSeverity.Error && d.Message.Contains("только на внутренних"));
    }

    [Fact]
    public void StringTag_Archived_Fails()
    {
        WriteDevicesWithInternal();
        File.WriteAllText(Path.Combine(ProjectDir, "tags.json"), """
            {
              "formatVersion": 1,
              "tags": [
                {"id": 0, "name": "Boiler1.Temp", "dataType": "analog", "deviceId": 0, "address": "sin:10"},
                {"id": 1, "name": "Status.Text", "dataType": "string", "deviceId": 1, "isArchived": true}
              ]
            }
            """);

        var result = ProjectBuildService.Build(ProjectDir, PackagePath);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics,
            d => d.Severity == BuildSeverity.Error && d.Message.Contains("не архивируются"));
    }

    // --- C2: выражения в параметрах действий (docs/scheme-controls-plan.md) ---

    [Fact]
    public void WriteTagValueExpression_CompilesIntoPool_AndRoundTrips()
    {
        WriteScheme("overview.scheme", """
            {
              "elements": [{
                "kind": "Rectangle", "x": 0, "y": 0, "width": 10, "height": 10,
                "events": [{"kind": "Click", "actions": [
                  {"type": "WriteTag", "tag": "Pump1.Running",
                   "valueExpression": "Pump1.Running + 1"}]}]
              }]
            }
            """);

        var result = ProjectBuildService.Build(ProjectDir, PackagePath);

        Assert.True(result.Success, string.Join("; ",
            result.Diagnostics.Select(d => d.Message)));
        using var reader = PackageReader.Open(PackagePath);
        var pool = CodeSectionReader.Read(reader.ReadEntry("code.bin"));
        Assert.Single(pool.Expressions); // только значение-выражение, условий нет

        var scheme = PackageProjectLoader.Load(reader).Schemes.Single();
        var writeTag = Assert.IsType<WriteTagAction>(
            Assert.Single(scheme.Elements[0].Events[0].Actions));
        Assert.NotNull(writeTag.CompiledValueIndex);
        Assert.Equal(new[] { 1 }, writeTag.CompiledValueTagIndices); // Pump1.Running
        // текста выражения в пакете нет, исходное Value не задано
        Assert.Null(writeTag.ValueExpression);
        Assert.Equal(0, writeTag.Value);
    }

    [Fact]
    public void WriteTagValueAndExpression_Fails()
    {
        WriteScheme("overview.scheme", """
            {
              "elements": [{
                "kind": "Rectangle", "x": 0, "y": 0, "width": 10, "height": 10,
                "events": [{"kind": "Click", "actions": [
                  {"type": "WriteTag", "tag": "Pump1.Running",
                   "value": 1, "valueExpression": "Pump1.Running + 1"}]}]
              }]
            }
            """);

        var result = ProjectBuildService.Build(ProjectDir, PackagePath);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics,
            d => d.Severity == BuildSeverity.Error && d.Message.Contains("valueExpression"));
    }

    [Fact]
    public void WriteTagWithoutValue_Fails()
    {
        // ни value, ни valueExpression — записывался бы молчаливый ноль
        WriteScheme("overview.scheme", """
            {
              "elements": [{
                "kind": "Rectangle", "x": 0, "y": 0, "width": 10, "height": 10,
                "events": [{"kind": "Click", "actions": [
                  {"type": "WriteTag", "tag": "Pump1.Running"}]}]
              }]
            }
            """);

        var result = ProjectBuildService.Build(ProjectDir, PackagePath);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics,
            d => d.Severity == BuildSeverity.Error && d.Message.Contains("value или valueExpression"));
    }

    [Fact]
    public void WriteTagValueExpression_OnStringTag_Fails()
    {
        // ВМ числовая: строковый тег в значении-выражении — ошибка сборки
        WriteDevicesWithInternal();
        WriteTagsWithString();
        WriteScheme("overview.scheme", """
            {
              "elements": [{
                "kind": "Rectangle", "x": 0, "y": 0, "width": 10, "height": 10,
                "events": [{"kind": "Click", "actions": [
                  {"type": "WriteTag", "tag": "Pump1.Running",
                   "valueExpression": "Status.Text"}]}]
              }]
            }
            """);

        var result = ProjectBuildService.Build(ProjectDir, PackagePath);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics,
            d => d.Severity == BuildSeverity.Error && d.Message.Contains("строков"));
    }

    [Fact]
    public void OpenPopupParameters_Classified_AndRoundTrip()
    {
        // три формы значений (C1 решение 3): константа, ссылка на строковый
        // тег, шаблон с числовым плейсхолдером
        WriteDevicesWithInternal();
        WriteTagsWithString();
        WriteTemplate("pump.scheme", """
            {
              "parameters": [{"name": "Prefix", "type": "String"}],
              "elements": []
            }
            """);
        WriteScheme("overview.scheme", """
            {
              "elements": [{
                "kind": "Rectangle", "x": 0, "y": 0, "width": 10, "height": 10,
                "events": [{"kind": "Click", "actions": [
                  {"type": "OpenPopup", "templateName": "pump",
                   "parameters": {
                     "Const": "Pump5",
                     "Selected": "Status.Text",
                     "ByNumber": "Pump{Boiler1.Temp}"}}]}]
              }]
            }
            """);

        var result = ProjectBuildService.Build(ProjectDir, PackagePath);

        Assert.True(result.Success, string.Join("; ",
            result.Diagnostics.Select(d => d.Message)));
        using var reader = PackageReader.Open(PackagePath);
        var scheme = PackageProjectLoader.Load(reader).Schemes.Single();
        var popup = Assert.IsType<OpenPopupAction>(
            Assert.Single(scheme.Elements[0].Events[0].Actions));

        var compiled = Assert.IsType<List<CompiledActionParameter>>(popup.CompiledParameters);
        Assert.Equal(ActionParamValueKind.Constant,
            compiled.Single(p => p.Name == "Const").Kind);
        var tagRef = compiled.Single(p => p.Name == "Selected");
        Assert.Equal(ActionParamValueKind.StringTagRef, tagRef.Kind);
        Assert.Equal(2, tagRef.TagId); // Status.Text
        Assert.Equal("Pump5", compiled.Single(p => p.Name == "Const").Text);

        // шаблон приезжает разобранным: литералы вокруг плейсхолдеров и
        // индексы выражений пула, исходной строки "Pump{Boiler1.Temp}" нет
        var template = compiled.Single(p => p.Name == "ByNumber");
        Assert.Equal(ActionParamValueKind.Template, template.Kind);
        Assert.Single(template.ExpressionIndices!); // один плейсхолдер
        Assert.Equal(["Pump", ""], template.Literals!);

        // текста выражений в пакете нет (§11), поэтому исходный словарь
        // значений не восстанавливается — рантайму хватает CompiledParameters
        Assert.Null(popup.Parameters);
    }

    [Fact]
    public void OpenPopupTemplate_WithStringTagPlaceholder_Fails()
    {
        WriteDevicesWithInternal();
        WriteTagsWithString();
        WriteTemplate("pump.scheme", """{"elements": []}""");
        WriteScheme("overview.scheme", """
            {
              "elements": [{
                "kind": "Rectangle", "x": 0, "y": 0, "width": 10, "height": 10,
                "events": [{"kind": "Click", "actions": [
                  {"type": "OpenPopup", "templateName": "pump",
                   "parameters": {"Prefix": "Pump{Status.Text}"}}]}]
              }]
            }
            """);

        var result = ProjectBuildService.Build(ProjectDir, PackagePath);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics,
            d => d.Severity == BuildSeverity.Error && d.Message.Contains("строков"));
    }

    [Fact]
    public void OpenPopupTemplate_UnbalancedBraces_Fail()
    {
        WriteTemplate("pump.scheme", """{"elements": []}""");
        WriteScheme("overview.scheme", """
            {
              "elements": [{
                "kind": "Rectangle", "x": 0, "y": 0, "width": 10, "height": 10,
                "events": [{"kind": "Click", "actions": [
                  {"type": "OpenPopup", "templateName": "pump",
                   "parameters": {"Prefix": "Pump{Boiler1.Temp"}}]}]
              }]
            }
            """);

        var result = ProjectBuildService.Build(ProjectDir, PackagePath);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics,
            d => d.Severity == BuildSeverity.Error && d.Message.Contains("незакрытый плейсхолдер"));
    }

    // --- C5: действие «задать свойство элемента» ---

    /// <summary>Схема из кнопки и цели: кнопка задаёт свойство элемента
    /// с именем name, остальное подставляется тестом.</summary>
    private void WriteSetPropertyScheme(string fileName, string targetJson,
        string actionJson)
        => WriteScheme(fileName, $$"""
            {
              "elements": [
                {{targetJson}},
                {
                  "kind": "Rectangle", "name": "Кнопка",
                  "x": 0, "y": 60, "width": 40, "height": 20,
                  "events": [{"kind": "Click", "actions": [{{actionJson}}]}]
                }
              ]
            }
            """);

    private const string PanelJson =
        """{"kind": "Rectangle", "name": "Панель", "x": 0, "y": 0, "width": 100, "height": 50}""";

    [Fact]
    public void SetProperty_ConstantAndExpression_RoundTrip()
    {
        // 5 = Visible (Boolean), 1 = Opacity (Number): константа и выражение
        WriteSetPropertyScheme("overview.scheme", PanelJson,
            """
            {"type": "SetProperty", "element": "Панель", "property": 5, "value": "false"},
            {"type": "SetProperty", "element": "Панель", "property": 1,
             "valueExpression": "Boiler1.Temp / 100"}
            """);

        var result = ProjectBuildService.Build(ProjectDir, PackagePath);

        Assert.True(result.Success, string.Join("; ",
            result.Diagnostics.Select(d => d.Message)));
        using var reader = PackageReader.Open(PackagePath);
        var scheme = PackageProjectLoader.Load(reader).Schemes.Single();
        var actions = scheme.Elements.Single(e => e.Name == "Кнопка")
            .Events[0].Actions;

        var constant = Assert.IsType<SetPropertyAction>(actions[0]);
        Assert.Equal("Панель", constant.ElementName);
        Assert.Equal(5, constant.PropertyId);
        Assert.False(constant.Value!.Value.AsBool);
        Assert.Null(constant.CompiledValueIndex);

        // выражение уехало в общий пул code.bin, текста в секции нет (§11)
        var computed = Assert.IsType<SetPropertyAction>(actions[1]);
        Assert.NotNull(computed.CompiledValueIndex);
        Assert.Null(computed.Value);
        Assert.Null(computed.ValueExpression);
        Assert.Equal([0], computed.CompiledValueTagIndices!); // Boiler1.Temp
    }

    [Fact]
    public void SetProperty_UnknownElement_Fails()
    {
        WriteSetPropertyScheme("overview.scheme", PanelJson,
            """{"type": "SetProperty", "element": "Панел", "property": 5, "value": "false"}""");

        var result = ProjectBuildService.Build(ProjectDir, PackagePath);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics,
            d => d.Severity == BuildSeverity.Error && d.Message.Contains("не найден"));
    }

    [Fact]
    public void SetProperty_ElementOfAnotherScheme_Fails()
    {
        // граница адресации: элемент есть в проекте, но на другой схеме —
        // сборка не может знать, открыта ли та схема в момент клика
        WriteScheme("detail.scheme", $$"""{"elements": [{{PanelJson}}]}""");
        WriteSetPropertyScheme("overview.scheme",
            """{"kind": "Rectangle", "name": "Фон", "x": 0, "y": 0, "width": 10, "height": 10}""",
            """{"type": "SetProperty", "element": "Панель", "property": 5, "value": "false"}""");

        var result = ProjectBuildService.Build(ProjectDir, PackagePath);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics,
            d => d.Severity == BuildSeverity.Error &&
                 d.Message.Contains("не найден") && d.Message.Contains("границы"));
    }

    [Fact]
    public void SetProperty_BoundProperty_Fails()
    {
        // свойством управляет привязка: заданное значение затрёт пересчётом
        WriteSetPropertyScheme("overview.scheme",
            """
            {"kind": "Rectangle", "name": "Панель", "x": 0, "y": 0, "width": 100, "height": 50,
             "bindings": [{"property": 5, "expression": "Pump1.Running"}]}
            """,
            """{"type": "SetProperty", "element": "Панель", "property": 5, "value": "false"}""");

        var result = ProjectBuildService.Build(ProjectDir, PackagePath);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics,
            d => d.Severity == BuildSeverity.Error && d.Message.Contains("управляет привязка"));
    }

    [Fact]
    public void SetProperty_NotAnimatableProperty_Fails()
    {
        // 8 = TextFormat: свойство есть, но в рантайме не меняется
        WriteSetPropertyScheme("overview.scheme", PanelJson,
            """{"type": "SetProperty", "element": "Панель", "property": 8, "value": "F2"}""");

        var result = ProjectBuildService.Build(ProjectDir, PackagePath);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics,
            d => d.Severity == BuildSeverity.Error && d.Message.Contains("неанимируемо"));
    }

    [Fact]
    public void SetProperty_ExpressionOnColor_Fails()
    {
        // 10 = FillColor: вычисляемый цвет — привязка со стопами, не арифметика
        WriteSetPropertyScheme("overview.scheme", PanelJson,
            """
            {"type": "SetProperty", "element": "Панель", "property": 10,
             "valueExpression": "Boiler1.Temp"}
            """);

        var result = ProjectBuildService.Build(ProjectDir, PackagePath);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics,
            d => d.Severity == BuildSeverity.Error &&
                 d.Message.Contains("выражение недопустимо"));
    }

    [Fact]
    public void SetProperty_InsideTemplate_ResolvesWithinTemplate()
    {
        // внутри шаблона цель ищется в его теле — экземпляров может быть сто,
        // и у каждого своя «Крышка»
        WriteTemplate("pump.scheme", """
            {
              "elements": [
                {"kind": "Rectangle", "name": "Крышка", "x": 0, "y": 0, "width": 10, "height": 10},
                {"kind": "Rectangle", "x": 0, "y": 20, "width": 10, "height": 10,
                 "events": [{"kind": "Click", "actions": [
                   {"type": "SetProperty", "element": "Крышка", "property": 5, "value": "true"}]}]}
              ]
            }
            """);
        WriteScheme("overview.scheme", """
            {
              "elements": [{"kind": "Instance", "templateName": "pump",
                            "x": 0, "y": 0, "width": 100, "height": 100}]
            }
            """);

        var result = ProjectBuildService.Build(ProjectDir, PackagePath);

        Assert.True(result.Success, string.Join("; ",
            result.Diagnostics.Select(d => d.Message)));
    }

    [Fact]
    public void DuplicateElementNames_Fail()
    {
        // имя — адрес действия, дубликат делает адрес неоднозначным;
        // регистр не спасает: «Панель» и «панель» — опечатка, а не два элемента
        WriteScheme("overview.scheme", """
            {
              "elements": [
                {"kind": "Rectangle", "name": "Панель", "x": 0, "y": 0, "width": 10, "height": 10},
                {"kind": "Rectangle", "name": "панель", "x": 0, "y": 20, "width": 10, "height": 10}
              ]
            }
            """);

        var result = ProjectBuildService.Build(ProjectDir, PackagePath);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics,
            d => d.Severity == BuildSeverity.Error && d.Message.Contains("встречается 2 раз"));
    }

    [Fact]
    public void SetProperty_ValueAndExpression_Fails()
    {
        WriteSetPropertyScheme("overview.scheme", PanelJson,
            """
            {"type": "SetProperty", "element": "Панель", "property": 1,
             "value": "1", "valueExpression": "Boiler1.Temp"}
            """);

        var result = ProjectBuildService.Build(ProjectDir, PackagePath);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics,
            d => d.Severity == BuildSeverity.Error && d.Message.Contains("оставьте одно"));
    }
}
