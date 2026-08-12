using Avalonia.Controls;
using SCADA.ViewModels;

namespace SCADA.Views;

public partial class MainWindow : Window
{
    public MainWindow() : this(new MainViewModel(new LoginViewModel(), new EditorViewModel())) { }
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
