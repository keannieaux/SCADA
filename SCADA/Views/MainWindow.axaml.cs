using Avalonia.Controls;
using SCADA.ViewModels;

namespace SCADA.Views;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
