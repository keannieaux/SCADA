using CommunityToolkit.Mvvm.ComponentModel;

namespace SCADA.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    public LoginViewModel LoginViewModel { get; }
    public EditorViewModel EditorViewModel { get; }

    [ObservableProperty] public partial bool IsLoggedIn { get; set; }

    public MainViewModel(LoginViewModel loginViewModel, EditorViewModel editorViewModel)
    {
        LoginViewModel = loginViewModel;
        LoginViewModel.LoginSucceeded += () => IsLoggedIn = true;

        EditorViewModel = editorViewModel;
    }
}
