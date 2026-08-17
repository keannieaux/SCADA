using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
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
                vm.RunStarted +=OnRunStarted;
                vm.RunStopped+=OnRunStopped;
            }
        };
    }

    private void OnRunStarted()
    {
        _runtimeView=Program.Services.GetRequiredService<RuntimeView>();
        _runtimeView.Closed += (_, _) =>
        {
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
