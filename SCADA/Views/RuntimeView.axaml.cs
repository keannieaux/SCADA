using Avalonia.Controls;
using SCADA.ViewModels;

namespace SCADA.Views;

public partial class RuntimeView : Window
{
    public RuntimeView(RuntimeViewModel viewModel)
    {
        InitializeComponent();
        DataContext=viewModel;
    }
}
