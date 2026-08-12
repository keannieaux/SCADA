using Avalonia.Controls;
using SCADA.ViewModels;

namespace SCADA.Views;

public partial class MainWindow : Window
{
    // Используется дизайнером Avalonia и XAML-загрузчиком
    public MainWindow() : this(new MainViewModel(new LoginViewModel(), new SettingsViewModel())) { }
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
