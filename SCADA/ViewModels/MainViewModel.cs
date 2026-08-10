using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FluentIcons.Common;
namespace SCADA.ViewModels;

public sealed record NavItem(string Title, Icon Icon);

public partial class MainViewModel : ViewModelBase
{
    public ObservableCollection<NavItem> NavItems { get; } = new()
    {
        new NavItem("Обзор", Icon.Grid),
        new NavItem("Теги", Icon.Tag),
        new NavItem("Тренды", Icon.DataTrending),
        new NavItem("Схемы", Icon.Flowchart),
        new NavItem("Аварии", Icon.Alert),
        new NavItem("Настройки", Icon.Settings),
    };
    [ObservableProperty] public partial NavItem SelectedNavItem { get; set; }

    public LoginViewModel LoginViewModel { get; }

    [ObservableProperty] public partial bool IsLoggedIn { get; set; }

    public MainViewModel()
    {
        SelectedNavItem = NavItems[1];

        LoginViewModel = new LoginViewModel();
        LoginViewModel.LoginSucceeded += () => IsLoggedIn = true;
    }
}
