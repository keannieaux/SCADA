using Avalonia.Controls;
using SCADA.Package.Builder;
using SCADA.Runtime.Runtime;
using SCADA.ViewModels;

namespace SCADA.Views;

public partial class EditorView : UserControl
{
    private RuntimeView? _runtimeView;

    public EditorView()
    {
        InitializeComponent();

        DataContextChanged += (_,_) =>
        {
            if(DataContext is EditorViewModel vm)
            {
                vm.BuildFailed += OnBuildFailed;
                vm.RunStarted += OnRunStarted;
                vm.RunStopped += OnRunStopped;
            }
        };
    }

    private async void OnBuildFailed(BuildResult result)
    {
        if (TopLevel.GetTopLevel(this) is Window owner)
            await BuildDiagnosticsDialog.Show(owner, result.Diagnostics);
    }

    private async void OnRunStarted(RuntimeHost host, ProjectConfiguration config, IReadOnlyList<BuildDiagnostic> diagnostics)
    {
        if (diagnostics.Count > 0 && TopLevel.GetTopLevel(this) is Window owner)
            await BuildDiagnosticsDialog.Show(owner, diagnostics); // предупреждения — не блокируют запуск

        var tagsViewModel = new TagsViewModel(host.Client, config);
        var schemesViewModel = new SchemesViewModel(config, host.Client);
        var runtimeViewModel = new RuntimeViewModel(host, tagsViewModel, schemesViewModel);

        _runtimeView = new RuntimeView(runtimeViewModel);
        _runtimeView.Closed += (_, _) =>
        {
            _ = host.StopAsync();
            if(DataContext is EditorViewModel vm)
                vm.IsRunning=false;
        };
        _runtimeView.Show();
    }

    private void OnRunStopped()
    {
        _runtimeView?.Close();
        _runtimeView=null;
    }
}
