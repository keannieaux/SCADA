using System.IO;
using SCADA.Runtime.Configuration;
using SCADA.Runtime.Polling;
using SCADA.Runtime.Runtime;
using Avalonia;
using System;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using SCADA.ViewModels;
using SCADA.Views;
using SCADA.Core.Tags;

namespace SCADA;

sealed class Program
{
    public static IServiceProvider Services {get; private set;} = null!;
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        var projectPath = Path.Combine(AppContext.BaseDirectory, "TestProject");
        var config = ProjectLoader.Load(projectPath);
        var tagTable = new SCADA.Runtime.TagTable.TagTable(config.Tags.Count);
        var runtimeClient = new LocalRuntimeClient(tagTable);
        var pollingEngine = new PollingEngine(config, tagTable);

        builder.Services.AddSingleton<ITagTable>(tagTable);
        builder.Services.AddTransient<SchemesViewModel>();
        builder.Services.AddSingleton<IRuntimeClient>(runtimeClient);
        builder.Services.AddSingleton(pollingEngine);
        builder.Services.AddSingleton<LoginViewModel>();
        builder.Services.AddSingleton<EditorViewModel>();
        builder.Services.AddTransient<RuntimeViewModel>();
        builder.Services.AddTransient<RuntimeView>();
        builder.Services.AddSingleton<MainViewModel>();
        builder.Services.AddSingleton<MainWindow>();
        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton<TagsViewModel>();

        Services = builder.Build().Services;
        Services.GetRequiredService<PollingEngine>().StartAsync();


        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
