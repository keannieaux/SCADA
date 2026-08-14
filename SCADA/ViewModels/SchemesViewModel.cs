using SCADA.Core.Tags;
using SCADA.Graphics;

namespace SCADA.ViewModels;

public sealed class SchemesViewModel : ViewModelBase
{
    public SchemeCanvas Canvas {get;}

    public SchemesViewModel(ProjectConfiguration config, ITagTable tagTable)
    {
        var scheme=new Scheme
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
                }
            ]
        };

        var catalog=new ProjectTagCatalog(config);
        var elements=SchemeLoader.Compile(scheme,catalog);

        Canvas =new SchemeCanvas(elements, tagTable);
    }
}
