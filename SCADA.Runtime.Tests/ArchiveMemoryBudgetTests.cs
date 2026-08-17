using SCADA.Runtime.Historian;

namespace SCADA.Runtime.Tests;

/// <summary>
/// Бюджет памяти под открытые блоки (docs/archive-format.md §8.6).
/// </summary>
/// <remarks>
/// Открытый блок держит 24 байта на отсчёт для каждого логируемого тега —
/// статья, которой не было ни в одной оценке до замеров M4. Задавать
/// вместимость блока напрямую значит требовать от интегратора считать
/// «теги × вместимость × 24»; вместо этого задаётся бюджет, а вместимость
/// выводится — тот же приём, что применён к диску.
/// </remarks>
public class ArchiveMemoryBudgetTests
{
    [Fact]
    public void SmallProject_KeepsFullBlock()
    {
        var options = new ArchiveOptions { MaxOpenBlockMemoryMb = 64 };

        // 100 тегов укладываются в бюджет с большим запасом: блок остаётся
        // максимальным, сжатие — наилучшим.
        Assert.Equal(4096, options.ResolveBlockPoints(100));
        Assert.True(options.EstimateOpenBlockMemoryMb(100) < 64);
    }

    [Fact]
    public void LargeProject_ShrinksBlockToFitBudget()
    {
        var options = new ArchiveOptions { MaxOpenBlockMemoryMb = 64 };

        int blockPoints = options.ResolveBlockPoints(2000);
        double memory = options.EstimateOpenBlockMemoryMb(2000);

        Assert.True(blockPoints < 4096, "блок обязан ужаться под бюджет");
        Assert.True(memory <= 64, $"пик памяти {memory:F0} МБ вышел за бюджет 64 МБ");
    }

    [Theory]
    [InlineData(500)]
    [InlineData(2000)]
    [InlineData(6000)]
    [InlineData(20_000)]
    public void MemoryNeverExceedsBudget(int archivedTags)
    {
        var options = new ArchiveOptions { MaxOpenBlockMemoryMb = 64 };

        double memory = options.EstimateOpenBlockMemoryMb(archivedTags);

        // Единственное исключение — нижняя граница вместимости: ниже 256
        // отсчётов заголовок блока начинает стоить как сами данные, и
        // экономия памяти оплачивалась бы ростом архива.
        int blockPoints = options.ResolveBlockPoints(archivedTags);
        if (blockPoints > 256)
            Assert.True(memory <= 64, $"{archivedTags} тегов дали {memory:F0} МБ");
    }

    [Fact]
    public void VeryLargeProject_StopsAtLowerBound()
    {
        var options = new ArchiveOptions { MaxOpenBlockMemoryMb = 8 };

        // 20 000 тегов при 8 МБ требовали бы 17 отсчётов на блок — заголовок
        // стоил бы вчетверо дороже данных. Вместимость упирается в пол,
        // и бюджет сознательно превышается: рост архива хуже, чем память.
        Assert.Equal(256, options.ResolveBlockPoints(20_000));
    }

    [Fact]
    public void ExplicitBlockPoints_OverridesBudget()
    {
        var options = new ArchiveOptions
        {
            MaxOpenBlockMemoryMb = 8,
            BlockPoints = 4096
        };

        // Явное значение отменяет расчёт: инженер вправе решить сам,
        // если знает, что делает.
        Assert.Equal(4096, options.ResolveBlockPoints(2000));
    }

    [Fact]
    public void NoArchivedTags_UsesDefault()
    {
        var options = new ArchiveOptions();

        Assert.Equal(4096, options.ResolveBlockPoints(0));
        Assert.Equal(0, options.EstimateOpenBlockMemoryMb(0));
    }

    [Fact]
    public void BudgetChange_MovesBlockSizeProportionally()
    {
        var small = new ArchiveOptions { MaxOpenBlockMemoryMb = 32 };
        var large = new ArchiveOptions { MaxOpenBlockMemoryMb = 128 };

        Assert.True(large.ResolveBlockPoints(2000) > small.ResolveBlockPoints(2000));
    }
}
