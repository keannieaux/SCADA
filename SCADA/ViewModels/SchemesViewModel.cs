using SCADA.Core.Tags;
using SCADA.Graphics;

namespace SCADA.ViewModels;

public sealed class SchemesViewModel : ViewModelBase
{
    private const bool UseLoadTestScheme = false; // включить обратно для повторной нагрузочной проверки

    public SchemeCanvas Canvas {get;}

    public SchemesViewModel(ProjectConfiguration config, ITagTable tagTable)
    {
        var scheme = UseLoadTestScheme
            ? SyntheticSchemeGenerator.Generate(500, ["Temperature", "Pressure", "PumpRunning", "Setpoint"])
            : new Scheme
            {
                Id=Guid.NewGuid(),
                Name="Тест",
                Elements=
                [
                    new SchemeElement
                    {
                        Id=Guid.NewGuid(),
                        X=20,Y=20, Width=200, Height=120,
                        ValueExpression="Temperature",
                        WarnThreshold=60,
                        CritThreshold=85,
                        QualityTagName="Temperature"
                    },
                    new SchemeElement
                    {
                        Id=Guid.NewGuid(),
                        Kind=ShapeKind.Ellipse,
                        X=260,Y=20,Width=150,Height=150,
                        ValueExpression="Pressure",
                        WarnThreshold=6,
                        CritThreshold=8.5,
                        QualityTagName="Pressure"
                    }
                ]
            };

        var catalog=new ProjectTagCatalog(config);
        var elements=SchemeLoader.Compile(scheme,catalog);

        Canvas =new SchemeCanvas(elements, tagTable, config.Tags.Count);
    }
}
