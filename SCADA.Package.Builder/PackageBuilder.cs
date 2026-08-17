using SCADA.Core.Alarms;
using SCADA.Core.Tags;
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

        // M5: expression-правила сигнализации компилируются в общий пул
        // code.bin; правила получают индексы выражений и тегов (§6)
        var allExpressions = new List<CompiledExpression>(expressions ?? []);
        CompileAlarmRules(config, allExpressions);
        writer.AddEntry("code.bin",
            CodeSectionWriter.Write(allExpressions, out var poolIndices));
        RemapAlarmExpressionIndices(config, poolIndices);

        if (config.Alarms.Rules.Count > 0)
        {
            writer.AddEntry("alarms.bin", AlarmsSectionWriter.Write(config.Alarms));
            AddSoundEntries(writer, config, projectDirectory);
        }

        writer.Save(outputPath, config.Name, config.Version);

        ReportArchiveVolume(config, report);
    }

    /// <summary>
    /// Компиляция expression-правил и раннее связывание тегов threshold-правил
    /// (§11.6): в пакете правила ссылаются на индексы, а не на имена.
    /// Ошибка компиляции — ошибка сборки пакета, а не рантайма.
    /// </summary>
    private static void CompileAlarmRules(ProjectConfiguration config,
        List<CompiledExpression> pool)
    {
        if (config.Alarms.Rules.Count == 0)
            return;

        var catalog = new BuilderTagCatalog(config.Tags);
        foreach (var rule in config.Alarms.Rules)
        {
            switch (rule.Type)
            {
                case AlarmType.Threshold:
                    // существование тега гарантирует валидация ProjectLoader
                    rule.CompiledTagIndices =
                        [catalog.GetIndex(rule.TagName!, rule.Name)];
                    break;

                case AlarmType.Expression:
                    CompiledExpression compiled;
                    try
                    {
                        compiled = ExpressionCompiler.Compile(rule.Condition!, catalog);
                    }
                    catch (ExpressionCompileException ex)
                    {
                        throw new InvalidOperationException(
                            $"Правило сигнализации '{rule.Name}': {ex.Message}", ex);
                    }
                    rule.CompiledExpressionIndex = pool.Count; // до дедупликации
                    rule.CompiledTagIndices = compiled.TagIndices;
                    pool.Add(compiled);
                    break;
            }
        }
    }

    /// <summary>Перевод индексов выражений правил из входного списка
    /// в итоговую таблицу пула после дедупликации.</summary>
    private static void RemapAlarmExpressionIndices(ProjectConfiguration config, int[] poolIndices)
    {
        foreach (var rule in config.Alarms.Rules)
            if (rule.CompiledExpressionIndex is int input)
                rule.CompiledExpressionIndex = poolIndices[input];
    }

    /// <summary>
    /// Звуковые файлы (§2.8) копируются в пакет секциями sounds/<имя> —
    /// иначе на объекте звука не будет. Отсутствующий файл — ошибка сборки.
    /// </summary>
    private static void AddSoundEntries(PackageWriter writer,
        ProjectConfiguration config, string projectDirectory)
    {
        foreach (string file in config.Alarms.Sound.Files.Values.Distinct())
        {
            string fullPath = Path.Combine(projectDirectory, file);
            if (!File.Exists(fullPath))
                throw new InvalidOperationException(
                    $"Звуковой файл сигнализации '{file}' не найден в каталоге проекта");
            writer.AddEntry($"sounds/{Path.GetFileName(file)}", File.ReadAllBytes(fullPath));
        }
    }

    /// <summary>Каталог тегов проекта для компилятора выражений при сборке.</summary>
    private sealed class BuilderTagCatalog(IReadOnlyList<TagDefinition> tags)
        : ITagCatalog
    {
        private readonly Dictionary<string, int> _byName =
            tags.ToDictionary(t => t.Name, t => t.Id.Value);

        public bool TryGetIndex(string name, out int index)
            => _byName.TryGetValue(name, out index);

        public int GetIndex(string name, string ruleName)
            => _byName.TryGetValue(name, out int index)
                ? index
                : throw new InvalidOperationException(
                    $"Правило сигнализации '{ruleName}': тег '{name}' не найден");
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
