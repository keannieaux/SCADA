using CommunityToolkit.Mvvm.ComponentModel;

namespace SCADA.ViewModels;

public partial class EditorViewModel : ViewModelBase
{
    public string[] Modes {get; } = ["Разработка", "Исполненение"];

    [ObservableProperty] public partial string SelectedMode {get; set; }="Разработка";
}
