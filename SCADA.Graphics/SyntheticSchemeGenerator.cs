using SCADA.Core.Schemes;

namespace SCADA.Graphics;

public static class SyntheticSchemeGenerator
{
    /// <summary>
    /// Нагрузочная схема: count элементов, каждый с привязками цвета и текста.
    /// volatileEvery &gt; 0 — каждый N-й элемент получает volatile-привязку
    /// вращения «now() * 90 % 360» (B0.4, анимация от времени).
    /// </summary>
    public static Scheme Generate(int count, IReadOnlyList<string> tagNames, int volatileEvery = 0)
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

            var bindings = new List<ElementBinding>
            {
                new()
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
                new() { PropertyId = SchemeProperty.Text, Expression = tagName },
            };

            if (volatileEvery > 0 && i % volatileEvery == 0)
                bindings.Add(new ElementBinding
                {
                    PropertyId = SchemeProperty.Rotation,
                    Expression = "now() * 90 % 360",
                    Volatile = true
                });

            elements.Add(new SchemeElement
            {
                Id = Guid.NewGuid(),
                Kind = i % 2 == 0 ? ElementKind.Rectangle : ElementKind.Ellipse,
                X = col * cellSize,
                Y = row * cellSize,
                Width = cellSize - gap,
                Height = cellSize - gap,
                Bindings = bindings
            });
        }

        return new Scheme { Id = Guid.NewGuid(), Name = "Нагрузочный тест", Elements = elements };
    }
}
