using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SCADA.ViewModels;

public partial class LoginViewModel : ViewModelBase
{
    [ObservableProperty] public partial string Username { get; set; } = "";
    [ObservableProperty] public partial string Password { get; set; } = "";

    public event Action? LoginSucceeded;

    [RelayCommand]
    private void Login()
    {
        if (Username == "admin" && Password == "admin")
        {
            LoginSucceeded?.Invoke();
        }
        else
        {
            // Handle login failure (e.g., show an error message)
        }
    }
}
