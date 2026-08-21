using SCADA.Core.Schemes;
using SCADA.Runtime.Runtime;
using SCADA.Graphics;

namespace SCADA.ViewModels;

public sealed class SchemesViewModel : ViewModelBase
{
    private const bool UseLoadTestScheme = true; // true — нагрузочная схема 500 элементов, каждый 2-й вращается (docs/scheme-rendering-benchmark.md)

    public SchemeCanvas Canvas {get;}

    public SchemesViewModel(ProjectConfiguration config, IRuntimeClient runtimeClient)
    {
        var scheme = UseLoadTestScheme
            ? SyntheticSchemeGenerator.Generate(500, ["Temperature", "Pressure", "PumpRunning", "Setpoint"], volatileEvery: 2)
            : new Scheme
            {
                Id = Guid.NewGuid(),
                Name = "Тест",
                Elements =
                [
                    new SchemeElement
                    {
                        Id = Guid.NewGuid(),
                        Kind = ElementKind.Rectangle,
                        X = 20, Y = 20, Width = 200, Height = 120,
                        Properties = [new ElementProperty(SchemeProperty.TextFormat, PropertyValue.FromString("F1")),
                                      new ElementProperty(SchemeProperty.Units, PropertyValue.FromString("°C"))],
                        Bindings =
                        [
                            new ElementBinding
                            {
                                PropertyId = SchemeProperty.FillColor,
                                Expression = "Temperature",
                                Mapping = StopMapping.Discrete,
                                Stops =
                                [
                                    new Stop(0, PropertyValue.FromColor(0xFF33383D)),
                                    new Stop(60, PropertyValue.FromColor(0xFFE8A33D)),
                                    new Stop(85, PropertyValue.FromColor(0xFFE5484D)),
                                ]
                            },
                            new ElementBinding { PropertyId = SchemeProperty.Text, Expression = "Temperature" },
                        ]
                    },
                    new SchemeElement
                    {
                        Id = Guid.NewGuid(),
                        Kind = ElementKind.Ellipse,
                        X = 260, Y = 20, Width = 150, Height = 150,
                        Bindings =
                        [
                            new ElementBinding
                            {
                                PropertyId = SchemeProperty.FillColor,
                                Expression = "Pressure",
                                Mapping = StopMapping.Discrete,
                                Stops =
                                [
                                    new Stop(0, PropertyValue.FromColor(0xFF33383D)),
                                    new Stop(6, PropertyValue.FromColor(0xFFE8A33D)),
                                    new Stop(8.5, PropertyValue.FromColor(0xFFE5484D)),
                                ]
                            },
                        ]
                    },
                    new SchemeElement
                    {
                        Id = Guid.NewGuid(),
                        Kind = ElementKind.Ellipse,
                        X = 440, Y = 70, Width = 30, Height = 30,
                        Bindings =
                        [
                            new ElementBinding { PropertyId = SchemeProperty.Visible, Expression = "Temperature > 90" },
                            new ElementBinding { PropertyId = SchemeProperty.Blink, Expression = "Temperature > 90" },
                        ]
                    },
                    new SchemeElement
                    {
                        Id = Guid.NewGuid(),
                        Kind = ElementKind.Rectangle,
                        X = 520, Y = 20, Width = 80, Height = 20,
                        Bindings = [new ElementBinding { PropertyId = SchemeProperty.Rotation, Expression = "now() * 90 % 360", Volatile = true }],
                        Events=
                        [
                            new SchemeEvent
                            {
                                Kind=SchemeEventKind.Click,
                                Actions=[new ShowDialogAction("Клик по повернутому элементу!")]
                            }
                        ]
                    },
                    new SchemeElement
                    {
                        Id = Guid.NewGuid(),
                        Kind = ElementKind.Rectangle,
                        X = 20, Y = 170, Width = 100, Height = 150,
                        Bindings =
                        [
                            new ElementBinding { PropertyId = SchemeProperty.FillLevel, Expression = "Pressure / 10" },
                            new ElementBinding
                            {
                                PropertyId = SchemeProperty.FillColor,
                                Expression = "Pressure",
                                Mapping = StopMapping.Discrete,
                                Stops =
                                [
                                    new Stop(0, PropertyValue.FromColor(0xFF33383D)),
                                    new Stop(6, PropertyValue.FromColor(0xFFE8A33D)),
                                    new Stop(8.5, PropertyValue.FromColor(0xFFE5484D)),
                                ]
                            },
                        ]
                    },
                    new SchemeElement
                    {
                        Id = Guid.NewGuid(),
                        Kind = ElementKind.Rectangle,
                        X = 20, Y = 340, Width = 20, Height = 20,
                        Bindings = [new ElementBinding { PropertyId = SchemeProperty.PositionOffsetX, Expression = "Pressure*20" }]
                    },
                    new SchemeElement
                    {
                        Id = Guid.NewGuid(),
                        Kind = ElementKind.Symbol,
                        X = 620, Y = 170, Width = 80, Height = 80,
                        Properties = [new ElementProperty(SchemeProperty.SymbolName, PropertyValue.FromString("pump"))],
                        Bindings = [new ElementBinding { PropertyId = SchemeProperty.Rotation, Expression = "Pressure*36" }]
                    },
                    new SchemeElement
                    {
                        Id = Guid.NewGuid(),
                        Kind = ElementKind.Rectangle,
                        X = 740, Y = 170, Width = 140, Height = 60,
                        Properties = [new ElementProperty(SchemeProperty.TextFormat, PropertyValue.FromString("F0"))],
                        Bindings =
                        [
                            new ElementBinding
                            {
                                PropertyId = SchemeProperty.FillColor,
                                Expression = "PumpCmd",
                                Mapping = StopMapping.Discrete,
                                Stops =
                                [
                                    new Stop(0, PropertyValue.FromColor(0xFF33383D)),
                                    new Stop(0.5, PropertyValue.FromColor(0xFFE8A33D)),
                                ]
                            },
                            new ElementBinding { PropertyId = SchemeProperty.Text, Expression = "PumpCmd" },
                        ],
                        Events =
                        [
                            new SchemeEvent
                            {
                                Kind = SchemeEventKind.Click,
                                Actions = [new ToggleTagAction(SchemeTagRef.Absolute("PumpCmd")) { Confirmation = "Переключить насос?" }]
                            }
                        ]
                    },
                    new SchemeElement
                    {
                        // C2: значение-выражение — уставка +5 в момент клика
                        Id = Guid.NewGuid(),
                        Kind = ElementKind.Rectangle,
                        X = 740, Y = 240, Width = 140, Height = 40,
                        Properties = [new ElementProperty(SchemeProperty.TextFormat, PropertyValue.FromString("F0"))],
                        Bindings =
                        [
                            new ElementBinding { PropertyId = SchemeProperty.Text, Expression = "Setpoint" },
                        ],
                        Events =
                        [
                            new SchemeEvent
                            {
                                Kind = SchemeEventKind.Click,
                                Actions = [new WriteTagAction(SchemeTagRef.Absolute("Setpoint"), 0)
                                {
                                    ValueExpression = "Setpoint + 5",
                                    Confirmation = "Увеличить уставку на 5?"
                                }]
                            }
                        ]
                    },
                    new SchemeElement
                    {
                        Id = Guid.NewGuid(),
                        Kind = ElementKind.Rectangle,
                        X = 900, Y = 20, Width = 30, Height = 250,
                        Bindings =
                        [
                            new ElementBinding
                            {
                                PropertyId = SchemeProperty.FillColor,
                                Expression = "Temperature",
                                Mapping = StopMapping.Interpolated,
                                Stops =
                                [
                                    new Stop(0, PropertyValue.FromColor(0xFF3D7FE8)),
                                    new Stop(100, PropertyValue.FromColor(0xFFE5484D)),
                                ]
                            },
                        ]
                    }
                ]
            };

        var catalog=new ProjectTagCatalog(config);
        var elements=SchemeLoader.Compile(scheme,catalog);

        Canvas = new SchemeCanvas(elements, runtimeClient, config.Tags.Count);
    }
}
