using SCADA.Core.Schemes;

namespace SCADA.Graphics;

public static class SyntheticSchemeGenerator
{
    public static Scheme Generate(int count, IReadOnlyList<string> tagNames)
    {
        const int columns = 25;
        const double cellSize = 20;
        const double gap = 4;

        var elements = new List<SchemeElement>(count);

        for (int i = 0; i < count; i++)
        {
            int col = i % columns;
            int row = i / columns;
            string tagName = tagNames[i % tagNames.Count];

            elements.Add(new SchemeElement
            {
                Id = Guid.NewGuid(),
                Kind = i % 2 == 0 ? ElementKind.Rectangle : ElementKind.Ellipse,
                X = col * cellSize,
                Y = row * cellSize,
                Width = cellSize - gap,
                Height = cellSize - gap,
                Bindings =
                [
                    new ElementBinding
                    {
                        PropertyId = SchemeProperty.FillColor,
                        Expression = tagName,
                        Mapping = StopMapping.Discrete,
                        Stops =
                        [
                            new Stop(0, PropertyValue.FromColor(0xFF33383D)),
                            new Stop(60, PropertyValue.FromColor(0xFFE8A33D)),
                            new Stop(85, PropertyValue.FromColor(0xFFE5484D)),
                        ]
                    },
                    new ElementBinding{PropertyId=SchemeProperty.Text, Expression=tagName},
                ]
            });
        }

        return new Scheme { Id = Guid.NewGuid(), Name = "Нагрузочный тест", Elements = elements };
    }
}
