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

            elements.Add(new SchemeElement
            {
                Id = Guid.NewGuid(),
                Kind = i % 2 == 0 ? ShapeKind.Rectangle : ShapeKind.Ellipse,
                X = col * cellSize,
                Y = row * cellSize,
                Width = cellSize - gap,
                Height = cellSize - gap,
                ValueExpression = tagNames[i % tagNames.Count],
                WarnThreshold = 60,
                CritThreshold = 85,
                QualityTagName = tagNames[i % tagNames.Count]
            });
        }

        return new Scheme { Id = Guid.NewGuid(), Name = "Нагрузочный тест", Elements = elements };
    }
}
