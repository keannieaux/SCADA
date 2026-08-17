using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SCADA.Views;
namespace SCADA.ViewModels;

public partial class EditorViewModel : ViewModelBase
{
    public TagsViewModel TagsViewModel { get; }

    [ObservableProperty] public partial bool IsRunning { get; set; }
    public event Action? RunStarted;
    public event Action? RunStopped;

    public EditorViewModel(TagsViewModel tagsViewModel)
    {
        TagsViewModel=tagsViewModel;
    }

    [RelayCommand]
    private void ToggleRun()
    {
        IsRunning=!IsRunning;

        if(IsRunning)
            RunStarted?.Invoke();
        else
            RunStopped?.Invoke();
    }
}
