using SCADA.Core.Tags;
using SCADA.Package.Builder;
using SCADA.Runtime.Historian;
using SCADA.Runtime.Runtime;

namespace SCADA.Runtime.Tests;

/// <summary>
/// RuntimeHost: запуск ядра из пакета .scadapkg, переходы состояний и доступ
/// к тегам через Client. Каталог исходников хост не принимает (A5.9).
/// Архив здесь выключен: его конвейер проверен в Archive*Tests,
/// в этих тестах важен жизненный цикл хоста.
/// </summary>
public class RuntimeHostTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    private string ProjectDir => Path.Combine(_dir, "project");
    private string PackagePath => Path.Combine(_dir, "project.scadapkg");

    public RuntimeHostTests()
    {
        Directory.CreateDirectory(ProjectDir);
        File.WriteAllText(Path.Combine(ProjectDir, "project.json"),
            """{"formatVersion": 1, "name": "HostTest", "version": "1.0"}""");
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
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private RuntimeHostOptions Options(string projectPath) => new()
    {
        ProjectPath = projectPath,
        Archive = new ArchiveOptions { Enabled = false }
    };

    [Fact]
    public async Task Package_StartReadStop_StateTransitions()
    {
        PackageBuilder.Build(ProjectDir, PackagePath);

        await using var host = await RuntimeHost.StartAsync(Options(PackagePath));
        Assert.Equal(RuntimeState.Running, host.State);

        var states = new List<RuntimeState>();
        host.StateChanged += states.Add;

        // const:7.5 — детерминированный адрес симулятора, ждём живое значение
        TagValue value = default;
        for (int i = 0; i < 100 && value.Quality != Quality.Good; i++)
        {
            value = host.Client.Read(new TagId(0));
            await Task.Delay(50);
        }
        Assert.Equal(Quality.Good, value.Quality);
        Assert.Equal(7.5, value.Value);

        await host.StopAsync();

        Assert.Equal(RuntimeState.Stopped, host.State);
        Assert.Equal([RuntimeState.Stopped], states);
    }

    [Fact]
    public async Task SourceDirectory_Throws()
    {
        // A5.9: рантайм не исполняет исходный каталог — только собранный пакет
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RuntimeHost.StartAsync(Options(ProjectDir)));
        Assert.Contains(".scadapkg", ex.Message);
    }
}
