using SCADA.Expressions.Compiler;
using SCADA.Package.Builder.Sections;
using SCADA.Runtime.Configuration;
using SCADA.Runtime.Historian;

namespace SCADA.Package.Builder;

/// <summary>
/// Сборка проекта в .scadapkg — конвейер (ТЗ §14.2):
/// исходный каталог → загрузка+валидация → секции → пакет.
/// Новые стадии (схемы в M3, правила сигнализации в M5) — новые строки здесь.
/// </summary>
public static class PackageBuilder
{
    /// <summary>
    /// Порог предупреждения об объёме архива. Выше него требование к диску
    /// перестаёт быть «поставьте обычный SSD» и требует разговора с заказчиком
    /// на этапе проектирования, а не при заполнении диска (ТЗ §4.3).
    /// </summary>
    private const double VolumeWarningGigabytes = 200;

    public static void Build(string projectDirectory, string outputPath,
        IReadOnlyList<CompiledExpression>? expressions = null,
        Action<string>? report = null)
    {
        // загрузка включает валидацию — битый проект не собирается в пакет
        var config = ProjectLoader.Load(projectDirectory);

        var writer = new PackageWriter();
        writer.AddEntry("tags.bin", TagsSectionWriter.Write(config.Tags));
        writer.AddEntry("devices.bin", DevicesSectionWriter.Write(config.Channels, config.Devices));
        writer.AddEntry("code.bin", CodeSectionWriter.Write(expressions ?? []));

        writer.Save(outputPath, config.Name, config.Version);

        ReportArchiveVolume(config, report);
    }

    /// <summary>
    /// Оценка объёма архива при сборке (ТЗ §4.3). Отсекает единственный
    /// реальный способ исчерпать диск — залогировать все теги на максимальной
    /// частоте, что даёт порядка терабайта в год.
    /// </summary>
    private static void ReportArchiveVolume(ProjectConfiguration config, Action<string>? report)
    {
        if (report is null)
            return;

        var options = new ArchiveOptions();
        int archivedTags = config.Tags.Count(t => t.IsArchived);
        int blockPoints = options.ResolveBlockPoints(archivedTags);

        var estimate = ArchiveVolumeCalculator.Estimate(config, options.RetentionDays,
            blockPoints: blockPoints);

        report(ArchiveVolumeCalculator.Format(estimate));

        // Память под открытые блоки — вторая статья, которую интегратор не
        // может посчитать сам и которая раньше нигде не фигурировала.
        report(
            $"Память под открытые блоки: {options.EstimateOpenBlockMemoryMb(archivedTags):F0} МБ " +
            $"(блок {blockPoints} отсчётов, бюджет {options.MaxOpenBlockMemoryMb} МБ)");

        if (estimate.GigabytesAtRetention > VolumeWarningGigabytes)
        {
            report(
                $"ВНИМАНИЕ: расчётный объём архива {estimate.GigabytesAtRetention:F0} ГБ " +
                $"за {estimate.RetentionDays} суток. Проверьте частоту логирования " +
                "и согласуйте требования к диску с заказчиком до развёртывания.");
        }
    }
}
