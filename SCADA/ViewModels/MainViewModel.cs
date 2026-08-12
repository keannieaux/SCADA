using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FluentIcons.Common;
namespace SCADA.ViewModels;

public sealed record NavItem(string Title, Icon Icon, ViewModelBase? Page = null);

public partial class MainViewModel : ViewModelBase
{
    public ObservableCollection<NavItem> NavItems { get; }
    [ObservableProperty] public partial NavItem SelectedNavItem { get; set; }
    public LoginViewModel LoginViewModel { get; }
    public SettingsViewModel SettingsViewModel { get; }
    [ObservableProperty] public partial bool IsLoggedIn { get; set; }
    [ObservableProperty] public partial ViewModelBase? CurrentPage { get; set; }
    public MainViewModel(LoginViewModel loginViewModel, SettingsViewModel settingsViewModel)
    {

        LoginViewModel = loginViewModel;
        LoginViewModel.LoginSucceeded += () => IsLoggedIn = true;

        SettingsViewModel=settingsViewModel;

        NavItems=new ObservableCollection<NavItem>
        {
            new NavItem("Обзор", Icon.Grid),
            new NavItem("Теги", Icon.Tag),
            new NavItem("Тренды", Icon.DataTrending),
            new NavItem("Схемы", Icon.Flowchart),
            new NavItem("Аварии", Icon.Alert),
            new NavItem("Настройки", Icon.Settings, SettingsViewModel),
        };

        SelectedNavItem = NavItems[1];
    }

    partial void OnSelectedNavItemChanged(NavItem value) =>CurrentPage = value.Page;
}
