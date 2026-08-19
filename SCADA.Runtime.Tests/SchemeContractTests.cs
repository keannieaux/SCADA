using SCADA.Core.Tags;
using SCADA.Package.Builder;
using SCADA.Package.Sections;
using SCADA.Runtime.Historian;
using SCADA.Runtime.Runtime;
using SCADA.Runtime.Schemes;

namespace SCADA.Runtime.Tests;

/// <summary>
/// Контракт схем в IRuntimeClient (A6, концепт §11): хост на собранном пакете
/// отдаёт список схем, скомпилированную схему по имени, шаблоны, пул байткода,
/// разрешение имён тегов и ассеты пакета.
/// </summary>
public class SchemeContractTests : IDisposable
{
    private static readonly byte[] SvgBytes = "<svg xmlns='http://www.w3.org/2000/svg'/>"u8.ToArray();
    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    private string ProjectDir => Path.Combine(_dir, "project");
    private string PackagePath => Path.Combine(_dir, "project.scadapkg");

    public SchemeContractTests()
    {
        Directory.CreateDirectory(Path.Combine(ProjectDir, "schemes"));
        Directory.CreateDirectory(Path.Combine(ProjectDir, "templates"));
        Directory.CreateDirectory(Path.Combine(ProjectDir, "symbols"));
        Directory.CreateDirectory(Path.Combine(ProjectDir, "images"));

        File.WriteAllText(Path.Combine(ProjectDir, "project.json"),
            """{"formatVersion": 1, "name": "HostTest", "version": "1.0", "startScheme": "main"}""");
        File.WriteAllText(Path.Combine(ProjectDir, "devices.json"), """
            {
              "formatVersion": 1,
              "channels": [{"id": 0, "name": "Ch0", "channelType": "none"}],
              "devices": [{"id": 0, "name": "Sim", "driverName": "simulator", "channelId": 0}]
            }
            """);
        File.WriteAllText(Path.Combine(ProjectDir, "tags.json"), """
            {
              "formatVersion": 1,
              "tags": [
                {"id": 0, "name": "Setpoint", "dataType": "analog", "deviceId": 0, "address": "const:7.5"}
              ]
            }
            """);
        File.WriteAllText(Path.Combine(ProjectDir, "schemes", "main.scheme"), """
            {
              "elements": [{
                "kind": "Rectangle", "x": 0, "y": 0, "width": 100, "height": 50,
                "bindings": [
                  {"property": 1, "expression": "Setpoint * 2",
                   "mapping": "Interpolated",
                   "stops": [{"input": 0, "output": "0"}, {"input": 100, "output": "50"}]}
                ]
              }]
            }
            """);
        File.WriteAllText(Path.Combine(ProjectDir, "templates", "pump.scheme"), """
            {
              "parameters": [{"name": "Prefix", "type": "String"}],
              "elements": [{
                "kind": "Rectangle", "x": 0, "y": 0, "width": 20, "height": 20,
                "bindings": [{"property": 1, "expression": "Setpoint"}]
              }]
            }
            """);
        File.WriteAllBytes(Path.Combine(ProjectDir, "symbols", "valve.svg"), SvgBytes);
        File.WriteAllBytes(Path.Combine(ProjectDir, "images", "logo.png"), PngBytes);

        var result = ProjectBuildService.Build(ProjectDir, PackagePath);
        Assert.True(result.Success, string.Join("; ",
            result.Diagnostics.Select(d => d.Message)));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private async Task<RuntimeHost> StartHost()
        => await RuntimeHost.StartAsync(new RuntimeHostOptions
        {
            ProjectPath = PackagePath,
            Archive = new ArchiveOptions { Enabled = false }
        });

    [Fact]
    public async Task GetSchemes_ReturnsList_AndSchemeByName()
    {
        await using var host = await StartHost();

        var list = host.Client.GetSchemes();
        var info = Assert.Single(list);
        Assert.Equal("main", info.Name);
        Assert.True(info.IsStart); // project.json → манифест пакета → контракт

        var scheme = host.Client.GetScheme("main");
        Assert.Equal(info.Id, scheme.Id);

        var binding = Assert.Single(scheme.Elements[0].Bindings);
        Assert.NotNull(binding.CompiledExpressionIndex);
        Assert.NotNull(binding.CompiledTagIndices);
        Assert.Equal(Core.Schemes.StopMapping.Interpolated, binding.Mapping);
        Assert.Equal(2, binding.Stops!.Count);

        // индекс привязки согласован с пулом: выражение есть и знает свои теги
        var pool = host.Client.GetCodePool();
        var loaded = pool.Expressions[binding.CompiledExpressionIndex.Value];
        Assert.Equal(binding.CompiledTagIndices, loaded.TagIndices);
        Assert.NotNull(host.Client.GetScheme("main").Elements); // повторный вызов — тот же снимок
    }

    [Fact]
    public async Task GetScheme_Unknown_Throws()
    {
        await using var host = await StartHost();
        Assert.Throws<KeyNotFoundException>(() => host.Client.GetScheme("missing"));
    }

    [Fact]
    public async Task GetTemplates_ReturnsTemplate()
    {
        await using var host = await StartHost();

        var template = Assert.Single(host.Client.GetTemplates());
        Assert.Equal("pump", template.Name);
        Assert.Single(template.Parameters);
    }

    [Fact]
    public async Task TryGetTagId_ResolvesProcessAndSystemTags()
    {
        await using var host = await StartHost();

        Assert.True(host.Client.TryGetTagId("Setpoint", out var id));
        Assert.Equal(new TagId(0), id);

        // системные теги аварий генерируются всегда (A5) — доступны действиям схем
        Assert.True(host.Client.TryGetTagId("@AlarmSystem.AnyActive", out _));
        Assert.False(host.Client.TryGetTagId("No.Such.Tag", out _));
    }

    [Fact]
    public async Task Assets_ListedAndReadByteExact()
    {
        await using var host = await StartHost();

        var assets = host.Client.GetAssets();
        Assert.Contains("symbols/valve.svg", assets);
        Assert.Contains("images/logo.png", assets);

        Assert.Equal(SvgBytes, host.Client.GetAsset("symbols/valve.svg"));
        Assert.Equal(PngBytes, host.Client.GetAsset("images/logo.png"));

        // чужие секции пакета через API ассетов недоступны
        Assert.Throws<KeyNotFoundException>(() => host.Client.GetAsset("tags.bin"));
        Assert.Throws<KeyNotFoundException>(() => host.Client.GetAsset("symbols/missing.svg"));
    }

    [Fact]
    public void UnknownStartScheme_FailsBuild()
    {
        // несуществующий стартовый экран — ошибка сборки, а не пустое окно
        // у оператора (валидация в ProjectValidator, общая для редактора)
        File.WriteAllText(Path.Combine(ProjectDir, "project.json"),
            """{"formatVersion": 1, "name": "HostTest", "version": "1.0", "startScheme": "missing"}""");

        var result = ProjectBuildService.Build(ProjectDir,
            Path.Combine(_dir, "bad.scadapkg"));

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics,
            d => d.Message.Contains("'missing'") && d.Message.Contains("тартов"));
    }

    [Fact]
    public void Catalog_DuplicateSchemeName_FailsFast()
    {
        var scheme = new Core.Schemes.Scheme { Id = Guid.NewGuid(), Name = "main", Elements = [] };
        var config = new ProjectConfiguration
        {
            Name = "t",
            Version = "1",
            Schemes = [scheme, scheme]
        };

        var ex = Assert.Throws<InvalidOperationException>(() => new SchemeCatalog(
            config, new CodePool([], []),
            new Dictionary<string, TagId>(), [], "dummy.scadapkg"));
        Assert.Contains("main", ex.Message);
    }
}
