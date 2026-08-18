using System;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SCADA.Package;
using SCADA.Package.Builder;
using SCADA.Runtime.Runtime;

namespace SCADA.ViewModels;

public partial class EditorViewModel : ViewModelBase
{
    [ObservableProperty] public partial bool IsRunning { get; set; }

    public event Action<BuildResult>? BuildFailed;
    public event Action<RuntimeHost, ProjectConfiguration, IReadOnlyList<BuildDiagnostic>>? RunStarted;
    public event Action? RunStopped;

    [RelayCommand]
    private async Task ToggleRun()
    {
        if (IsRunning)
        {
            IsRunning = false;
            RunStopped?.Invoke();
            return;
        }

        string projectDir = Path.Combine(AppContext.BaseDirectory, "TestProject");
        string outputPath = Path.Combine(projectDir, "output", "TestProject.scadapkg");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var buildResult = ProjectBuildService.Build(projectDir, outputPath);

        if (!buildResult.Success)
        {
            BuildFailed?.Invoke(buildResult);
            return;
        }

        var config = PackageProjectLoader.Load(buildResult.PackagePath!);
        var host = await RuntimeHost.StartAsync(new RuntimeHostOptions { ProjectPath = buildResult.PackagePath! });

        IsRunning = true;
        RunStarted?.Invoke(host, config, buildResult.Diagnostics);
    }
}
