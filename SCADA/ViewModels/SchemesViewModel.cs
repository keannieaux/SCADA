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
                    },
                    new SchemeElement
                    {
                        Id=Guid.NewGuid(),
                        Kind=ShapeKind.Ellipse,
                        X=440,Y=70,Width=30,Height=30,
                        VisibleExpression="Temperature > 90",
                        BlinkWhenExpression="Temperature > 90"
                    },
                    new SchemeElement
                    {
                        Id=Guid.NewGuid(),
                        X=520,Y=20,Width=80,Height=20,
                        RotationExpression="Pressure*36"
                    },
                    new SchemeElement
                    {
                        Id=Guid.NewGuid(),
                        X=20,Y=170,Width=100,Height=150,
                        FillLevelExpression="Pressure / 10",
                        ValueExpression="Pressure",
                        WarnThreshold=6,
                        CritThreshold=8.5
                    },
                    new SchemeElement
                    {
                        Id=Guid.NewGuid(),
                        X=20,Y=20, Width=200, Height=120,
                        ValueExpression="Temperature",
                        WarnThreshold=60,
                        CritThreshold=85,
                        QualityTagName="Temperature",
                        TextExpression="Temperature",
                        TextFormat="F1",
                        Units="°C"
                    },
                    new SchemeElement
                    {
                        Id=Guid.NewGuid(),
                        X=20,Y=340,Width=20,Height=20,
                        PositionXExpression="Pressure*20"
                    },
                    new SchemeElement
                    {
                        Id=Guid.NewGuid(),
                        Kind=ShapeKind.Symbol,
                        X=620,Y=170,Width=80,Height=80,
                        SymbolPath=Path.Combine(AppContext.BaseDirectory,"Symbols","pump.svg"),
                        RotationExpression="Pressure*36"
                    },
                    new SchemeElement
                    {
                        Id=Guid.NewGuid(),
                        X=740,Y=170,Width=140,Height=60,
                        ValueExpression="PumpCmd",
                        WarnThreshold=0.5,
                        TextExpression="PumpCmd",
                        TextFormat="F0",
                        OnClick=
                        [
                            new ConfirmAction("Переключить насос?"),
                            new ToggleTagAction("PumpCmd")
                        ]
                    }
                ]
            };

        var catalog=new ProjectTagCatalog(config);
        var elements=SchemeLoader.Compile(scheme,catalog);

        Canvas =new SchemeCanvas(elements, tagTable, config.Tags.Count);
    }
}
