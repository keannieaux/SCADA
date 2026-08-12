using CommunityToolkit.Mvvm.ComponentModel;

namespace SCADA.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    public string[] ThemeOptions { get; } = { "Темная", "Светлая", "Системная" };
    public string[] DensityOptions { get; } = { "Компактно", "Обычно" };

    [ObservableProperty] public partial string Theme { get; set; }="Системная";
    [ObservableProperty] public partial bool AlarmSoundEnabled { get; set; } = true;
    [ObservableProperty] public partial string SessionTimeoutMinutes { get; set; } = "15";
    [ObservableProperty] public partial string TableDensity { get; set; } = "Компактно";

}
