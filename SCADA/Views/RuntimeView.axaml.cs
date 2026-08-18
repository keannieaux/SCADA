using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using SCADA.Runtime.Runtime;
using SCADA.ViewModels;

namespace SCADA.Views;

public partial class RuntimeView : Window
{
    public RuntimeView(RuntimeViewModel viewModel)
    {
        InitializeComponent();
        DataContext=viewModel;

        viewModel.Host.StateChanged += OnHostStateChanged;
    }

    private void OnHostStateChanged(RuntimeState state)
    {
        if (state != RuntimeState.Faulted)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            var dialog = new Window
            {
                Content = new TextBlock
                {
                    Text = "Исполнение аварийно завершилось.",
                    Margin = new Avalonia.Thickness(20)
                },
                SizeToContent = SizeToContent.WidthAndHeight,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            dialog.Show(this);
        });
    }
}
