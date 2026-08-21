using SCADA.Graphics;
using SCADA.Runtime.Runtime;

namespace SCADA.ViewModels;

public sealed class SchemesViewModel : ViewModelBase
{
    // true — нагрузочная схема 500 элементов, каждый 2-й вращается
    // (docs/scheme-rendering-benchmark.md). Собирается в коде и компилируется
    // на лету: в пакете её нет и быть не должно.
    private const bool UseLoadTestScheme = false;

    public SchemeCanvas Canvas {get;}

    public SchemesViewModel(ProjectConfiguration config, IRuntimeClient runtimeClient)
    {
        var scheme = UseLoadTestScheme
            ? SyntheticSchemeGenerator.Generate(500,
                ["Temperature", "Pressure", "PumpRunning", "Setpoint"], volatileEvery: 2)
            : runtimeClient.GetScheme(StartSchemeName(runtimeClient));

        var elements = UseLoadTestScheme
            ? SchemeLoader.Compile(scheme, new ProjectTagCatalog(config))
            : SchemeLoader.Load(scheme, runtimeClient);

        Canvas = new SchemeCanvas(scheme, elements, runtimeClient, config.Tags.Count);

    }

    // стартовый экран задан в project.json ("startScheme") и доезжает до
    // контракта флагом IsStart; если не задан — берём первую схему пакета
    private static string StartSchemeName(IRuntimeClient client)
    {
        var schemes = client.GetSchemes();
        if (schemes.Count == 0)
            throw new InvalidOperationException("в пакете нет ни одной схемы");

        return (schemes.FirstOrDefault(s => s.IsStart) ?? schemes[0]).Name;
    }
}
