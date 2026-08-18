using SCADA.Core.Alarms;
using SCADA.Core.Tags;
using SCADA.Expressions.Compiler;
using SCADA.Package.Builder.Sections;
using SCADA.Runtime.Configuration;
using SCADA.Runtime.Historian;

namespace SCADA.Package.Builder;

/// <summary>
/// Сборка проекта в .scadapkg со структурированными диагностиками (ТЗ §14.2).
/// В отличие от <see cref="PackageBuilder"/>, не валится первым исключением:
/// собирает все ошибки стадий и возвращает их списком — для панели
/// «Проблемы» в IDE. Пакет пишется только при отсутствии ошибок.
/// </summary>
public static class ProjectBuildService
{
    /// <summary>
    /// Порог предупреждения об объёме архива. Выше него требование к диску
    /// перестаёт быть «поставьте обычный SSD» и требует разговора с заказчиком
    /// на этапе проектирования, а не при заполнении диска (ТЗ §4.3).
    /// </summary>
    private const double VolumeWarningGigabytes = 200;

    /// <summary>Полный цикл: каталог с JSON → диагностики → .scadapkg.</summary>
    public static BuildResult Build(string projectDirectory, string outputPath)
        => BuildCore(projectDirectory, outputPath, expressions: null);

    // Общее ядро конвейера: PackageBuilder.Build работает поверх него,
    // передавая внешние выражения и переводя диагностики в исключение.
    internal static BuildResult BuildCore(string projectDirectory, string outputPath,
        IReadOnlyList<CompiledExpression>? expressions)
    {
        var diagnostics = new List<BuildDiagnostic>();
        try
        {
            // загрузка включает валидацию — битый проект не собирается в пакет
            ProjectConfiguration config;
            try
            {
                config = ProjectLoader.Load(projectDirectory);
            }
            catch (ProjectConfigurationException ex)
            {
                foreach (string error in ex.Errors)
                    diagnostics.Add(new BuildDiagnostic(BuildSeverity.Error, "project", error));
                return new BuildResult(false, null, diagnostics);
            }

            // M5: expression-правила сигнализации компилируются в общий пул
            // code.bin; правила получают индексы выражений и тегов (§6)
            var allExpressions = new List<CompiledExpression>(expressions ?? []);
            CompileAlarmRules(config, allExpressions, diagnostics);
            var soundEntries = CollectSoundEntries(config, projectDirectory, diagnostics);

            bool success = !diagnostics.Any(d => d.Severity == BuildSeverity.Error);
            if (success)
            {
                var writer = new PackageWriter();
                writer.AddEntry("tags.bin", TagsSectionWriter.Write(config.Tags));
                writer.AddEntry("devices.bin",
                    DevicesSectionWriter.Write(config.Channels, config.Devices));
                writer.AddEntry("code.bin",
                    CodeSectionWriter.Write(allExpressions, out var poolIndices));
                RemapAlarmExpressionIndices(config, poolIndices);

                if (config.Alarms.Rules.Count > 0)
                {
                    writer.AddEntry("alarms.bin", AlarmsSectionWriter.Write(config.Alarms));
                    foreach (var (entry, bytes) in soundEntries)
                        writer.AddEntry(entry, bytes);
                }

                writer.Save(outputPath, config.Name, config.Version);
            }

            ReportArchiveVolume(config, diagnostics);

            return new BuildResult(success, success ? outputPath : null, diagnostics);
        }
        catch (Exception ex)
        {
            // непредвиденное (IOException и т.п.) — одна ошибка с типом и сообщением
            diagnostics.Add(new BuildDiagnostic(BuildSeverity.Error, "build",
                $"{ex.GetType().Name}: {ex.Message}"));
            return new BuildResult(false, null, diagnostics);
        }
    }

    /// <summary>
    /// Компиляция expression-правил и раннее связывание тегов threshold-правил
    /// (§11.6): в пакете правила ссылаются на индексы, а не на имена.
    /// Собираются ВСЕ ошибки правил — ошибка компиляции это ошибка сборки
    /// пакета, а не рантайма, и интегратор должен видеть их разом.
    /// </summary>
    private static void CompileAlarmRules(ProjectConfiguration config,
        List<CompiledExpression> pool, List<BuildDiagnostic> diagnostics)
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
                    try
                    {
                        CompiledExpression compiled =
                            ExpressionCompiler.Compile(rule.Condition!, catalog);
                        rule.CompiledExpressionIndex = pool.Count; // до дедупликации
                        rule.CompiledTagIndices = compiled.TagIndices;
                        pool.Add(compiled);
                    }
                    catch (ExpressionCompileException ex)
                    {
                        diagnostics.Add(new BuildDiagnostic(BuildSeverity.Error,
                            $"alarm:{rule.Name}",
                            $"Правило сигнализации '{rule.Name}': {ex.Message}"));
                    }
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
    /// Звуковые файлы (§2.8) копируются в пакет секциями sounds/&lt;имя&gt; —
    /// иначе на объекте звука не будет. Отсутствующий файл — ошибка сборки;
    /// собираются все отсутствующие, а не первый.
    /// </summary>
    private static List<(string Entry, byte[] Bytes)> CollectSoundEntries(
        ProjectConfiguration config, string projectDirectory,
        List<BuildDiagnostic> diagnostics)
    {
        var entries = new List<(string, byte[])>();
        foreach (string file in config.Alarms.Sound.Files.Values.Distinct())
        {
            string fullPath = Path.Combine(projectDirectory, file);
            if (!File.Exists(fullPath))
            {
                diagnostics.Add(new BuildDiagnostic(BuildSeverity.Error, "alarms:sounds",
                    $"Звуковой файл сигнализации '{file}' не найден в каталоге проекта"));
                continue;
            }
            entries.Add(($"sounds/{Path.GetFileName(file)}", File.ReadAllBytes(fullPath)));
        }
        return entries;
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
    private static void ReportArchiveVolume(ProjectConfiguration config,
        List<BuildDiagnostic> diagnostics)
    {
        var options = new ArchiveOptions();
        int archivedTags = config.Tags.Count(t => t.IsArchived);
        int blockPoints = options.ResolveBlockPoints(archivedTags);

        var estimate = ArchiveVolumeCalculator.Estimate(config, options.RetentionDays,
            blockPoints: blockPoints);

        diagnostics.Add(new BuildDiagnostic(BuildSeverity.Info, "archive",
            ArchiveVolumeCalculator.Format(estimate)));

        // Память под открытые блоки — вторая статья, которую интегратор не
        // может посчитать сам и которая раньше нигде не фигурировала.
        diagnostics.Add(new BuildDiagnostic(BuildSeverity.Info, "archive",
            $"Память под открытые блоки: {options.EstimateOpenBlockMemoryMb(archivedTags):F0} МБ " +
            $"(блок {blockPoints} отсчётов, бюджет {options.MaxOpenBlockMemoryMb} МБ)"));

        if (estimate.GigabytesAtRetention > VolumeWarningGigabytes)
        {
            diagnostics.Add(new BuildDiagnostic(BuildSeverity.Warning, "archive",
                $"ВНИМАНИЕ: расчётный объём архива {estimate.GigabytesAtRetention:F0} ГБ " +
                $"за {estimate.RetentionDays} суток. Проверьте частоту логирования " +
                "и согласуйте требования к диску с заказчиком до развёртывания."));
        }
    }
}
