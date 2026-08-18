using Avalonia;
using System;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using SCADA.ViewModels;
using SCADA.Views;
using SCADA.Runtime.Polling;
using SCADA.Drivers.Simulator;
using SCADA.Drivers.Modbus;

namespace SCADA;

sealed class Program
{
    public static IServiceProvider Services {get; private set;} = null!;

    [STAThread]
    public static void Main(string[] args)
    {
        // Драйверы регистрируются в composition root приложения (ТЗ §7.2),
        // по образцу SCADA.Server/Program.cs.
        DriverFactory.Register("simulator", () => new SimulatorDriver());
        DriverFactory.Register("modbus-tcp", () => new ModbusTcpDriver());

        var builder = Host.CreateApplicationBuilder(args);

        builder.Services.AddSingleton<LoginViewModel>();
        builder.Services.AddSingleton<EditorViewModel>();
        builder.Services.AddSingleton<MainViewModel>();
        builder.Services.AddSingleton<MainWindow>();

        Services = builder.Build().Services;

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
