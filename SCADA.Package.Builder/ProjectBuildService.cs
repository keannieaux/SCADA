using SCADA.Core.Alarms;
using SCADA.Core.Schemes;
using SCADA.Core.Tags;
using SCADA.Core.Users;
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

            // строковые теги (концепт §4.6): выражения числовые — страж ловит
            // строковые теги в выражениях и оформляет прямые строковые привязки
            var stringGuard = new StringTagGuard(config.Tags);

            // M5: expression-правила сигнализации компилируются в общий пул
            // code.bin; правила получают индексы выражений и тегов (§6)
            var allExpressions = new List<CompiledExpression>(expressions ?? []);
            CompileAlarmRules(config, allExpressions, diagnostics, stringGuard);
            var soundEntries = CollectSoundEntries(config, projectDirectory, diagnostics);

            // схемы и шаблоны (docs/visualization-concept.md §11.4): выражения
            // привязок и условий действий — в тот же пул code.bin ДО записи;
            // затем ссылки на теги/шаблоны и ассеты. Все ошибки собираются,
            // пакет при них не пишется.
            CompileSchemeExpressions(config, allExpressions, diagnostics, stringGuard);
            ValidateSchemes(config, diagnostics);
            var schemeAssetEntries = CollectSchemeAssetEntries(config, projectDirectory,
                diagnostics);

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
                RemapSchemeExpressionIndices(config, poolIndices);

                if (config.Alarms.Rules.Count > 0)
                {
                    writer.AddEntry("alarms.bin", AlarmsSectionWriter.Write(config.Alarms));
                    foreach (var (entry, bytes) in soundEntries)
                        writer.AddEntry(entry, bytes);
                }

                // роли проекта (docs/users-plan.md §4.1): секция опциональна —
                // проект без roles.json собирается без неё. Условие — наличие
                // файла, а не непустой список ролей: иначе политики (таймаут,
                // длина пароля) из roles.json с пустыми ролями молча терялись
                // бы. Пользователей в пакете нет и не будет (§3)
                if (config.Users.IsConfigured)
                    writer.AddEntry("roles.bin", RolesSectionWriter.Write(config.Users));

                // схемы и шаблоны — секциями schemes/<имя>.bin / templates/<имя>.bin,
                // перечисление на чтении — через манифест (§11.1)
                foreach (var scheme in config.Schemes)
                    writer.AddEntry($"schemes/{scheme.Name}.bin", SchemeSectionWriter.Write(scheme));
                foreach (var template in config.Templates)
                    writer.AddEntry($"templates/{template.Name}.bin",
                        SchemeSectionWriter.WriteTemplate(template));
                foreach (var (entry, bytes) in schemeAssetEntries)
                    writer.AddEntry(entry, bytes);

                writer.Save(outputPath, config.Name, config.Version, config.StartScheme);
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
        List<CompiledExpression> pool, List<BuildDiagnostic> diagnostics,
        StringTagGuard stringGuard)
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
                    stringGuard.CheckCompiled(rule.CompiledTagIndices,
                        $"alarm:{rule.Name}",
                        $"Правило сигнализации '{rule.Name}'", diagnostics);
                    break;

                case AlarmType.Expression:
                    try
                    {
                        CompiledExpression compiled =
                            ExpressionCompiler.Compile(rule.Condition!, catalog);
                        stringGuard.CheckCompiled(compiled.TagIndices,
                            $"alarm:{rule.Name}",
                            $"Правило сигнализации '{rule.Name}'", diagnostics);
                        CheckNoSessionTags(config, rule.Name, compiled.TagIndices, diagnostics);
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


    /// <summary>
    /// Проектная область экрана (концепт §6.1): DesignWidth/DesignHeight —
    /// холст, в единицах которого заданы координаты элементов. Рантайм
    /// вписывает этот холст в окно и обрезает по его границам, поэтому
    /// проверяем две вещи.
    ///
    /// Неположительный размер — ошибка: базовый масштаб считается делением
    /// на него, и в рантайме получился бы Infinity или NaN.
    ///
    /// Элемент целиком за пределами холста — предупреждение: после обрезки
    /// он просто не появится на экране. Частично выходящие не трогаем —
    /// элемент может намеренно выезжать из-за края анимацией.
    /// </summary>
    private static void ValidateDesignArea(ProjectConfiguration config,
        List<BuildDiagnostic> diagnostics)
    {
        foreach (var scheme in config.Schemes)
            CheckArea(scheme.Name, "экран", scheme.Properties, scheme.Elements, diagnostics);
        foreach (var template in config.Templates)
            CheckArea(template.Name, "шаблон", template.Properties, template.Elements, diagnostics);
    }

    private static void CheckArea(string name, string what,
        IReadOnlyList<ElementProperty> properties, IReadOnlyList<SchemeElement> elements,
        List<BuildDiagnostic> diagnostics)
    {
        double width = SchemeNumber(properties, DesignWidthId);
        double height = SchemeNumber(properties, DesignHeightId);
        string source = $"scheme:{name}";

        if (width <= 0 || height <= 0)
        {
            diagnostics.Add(new BuildDiagnostic(BuildSeverity.Error, source,
                $"{what} '{name}': проектная область {width}x{height} — размеры должны быть " +
                "положительными, по ним считается масштаб вписывания в окно"));
            return;
        }

        foreach (var element in elements)
        {
            bool outside = element.X >= width || element.Y >= height
                || element.X + element.Width <= 0 || element.Y + element.Height <= 0;
            if (!outside)
                continue;

            string label = string.IsNullOrEmpty(element.Name)
                ? element.Kind.ToString()
                : $"'{element.Name}'";
            diagnostics.Add(new BuildDiagnostic(BuildSeverity.Warning, source,
                $"{what} '{name}': элемент {label} целиком за проектной областью " +
                $"({width}x{height}) — на экране он не появится"));
        }

        CheckViewport(name, what, properties, source, diagnostics);
    }

    /// <summary>
    /// Пан, зум и начальное приближение. Экран вписывается в окно целиком,
    /// поэтому масштаб меньше вписанного лишён смысла — схема уплыла бы
    /// в угол окна. А начальное приближение при запрещённом пане оставило бы
    /// оператора с обрезанным экраном без возможности его подвинуть.
    /// </summary>
    private static void CheckViewport(string name, string what,
        IReadOnlyList<ElementProperty> properties, string source,
        List<BuildDiagnostic> diagnostics)
    {
        double startZoom = SchemeNumber(properties, StartZoomId);
        double maxZoom = SchemeNumber(properties, MaxZoomId);
        bool allowPanZoom = SchemeFlag(properties, AllowPanZoomId);

        if (maxZoom < 1)
            diagnostics.Add(new BuildDiagnostic(BuildSeverity.Error, source,
                $"{what} '{name}': предел приближения {maxZoom} меньше единицы — " +
                "отдалить экран меньше вписанного нельзя"));

        if (startZoom < 1)
            diagnostics.Add(new BuildDiagnostic(BuildSeverity.Warning, source,
                $"{what} '{name}': начальное приближение {startZoom} меньше единицы — " +
                "экран и так вписан целиком, значение будет поднято до 1"));
        else if (startZoom > maxZoom && maxZoom >= 1)
            diagnostics.Add(new BuildDiagnostic(BuildSeverity.Warning, source,
                $"{what} '{name}': начальное приближение {startZoom} больше предела " +
                $"{maxZoom} — будет ограничено пределом"));

        if (!allowPanZoom && startZoom != 1)
            diagnostics.Add(new BuildDiagnostic(BuildSeverity.Warning, source,
                $"{what} '{name}': начальное приближение задано, но пан и зум запрещены — " +
                "оператор увидел бы обрезанный экран без возможности подвинуть его, " +
                "значение игнорируется"));
    }

    /// <summary>Логическое свойство уровня схемы: заданное или умолчание.</summary>
    private static bool SchemeFlag(IReadOnlyList<ElementProperty> properties, int propertyId)
    {
        foreach (var property in properties)
            if (property.PropertyId == propertyId)
                return property.Value.Number != 0;
        return (ElementSchemas.FindSchemeProperty(propertyId)?.Default.Number ?? 0) != 0;
    }

    /// <summary>Числовое свойство уровня схемы: заданное или умолчание
    /// дескриптора (свойства хранятся разреженно).</summary>
    private static double SchemeNumber(IReadOnlyList<ElementProperty> properties, int propertyId)
    {
        foreach (var property in properties)
            if (property.PropertyId == propertyId)
                return property.Value.Number;
        return ElementSchemas.FindSchemeProperty(propertyId)?.Default.Number ?? 0;
    }

    private const int DesignWidthId = 101;
    private const int DesignHeightId = 102;
    private const int StartZoomId = 103;
    private const int AllowPanZoomId = 104;
    private const int MaxZoomId = 105;

    /// <summary>
    /// Сессионный тег в условии правила сигнализации — ошибка сборки
    /// (docs/session-tags-concept.md §2.3). Правила считаются на сервере,
    /// где сессионных значений нет вовсе: правило читало бы пустоту.
    /// Прямую ссылку порогового правила ловит ProjectValidator по имени тега;
    /// здесь — теги внутри выражения, их знает только компилятор.
    /// </summary>
    private static void CheckNoSessionTags(ProjectConfiguration config, string ruleName,
        int[] tagIndices, List<BuildDiagnostic> diagnostics)
    {
        foreach (int index in tagIndices)
        {
            var tag = config.Tags.FirstOrDefault(t => t.Id.Value == index);
            if (tag is not { Scope: TagScope.Session })
                continue;
            diagnostics.Add(new BuildDiagnostic(BuildSeverity.Error, $"alarm:{ruleName}",
                $"Правило сигнализации '{ruleName}': условие ссылается на сессионный тег " +
                $"'{tag.Name}' — правила считаются на сервере, где сессионных " +
                "значений нет"));
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
    /// Компиляция выражений схем и шаблонов (концепт §11.4): привязки
    /// элементов и условия действий — в общий пул code.bin, как правила
    /// сигнализации. Дедупликация пула покрывает и «одно выражение → группа
    /// свойств» (§4.2). Собираются ВСЕ ошибки компиляции, не первая.
    /// Выражения шаблонов компилируются через каталог с параметрами:
    /// параметрическая ссылка получает индекс-заглушку -1, реальный TagId
    /// подставляется при раскрытии экземпляра в рантайме (§7, B2).
    /// </summary>
    private static void CompileSchemeExpressions(ProjectConfiguration config,
        List<CompiledExpression> pool, List<BuildDiagnostic> diagnostics,
        StringTagGuard stringGuard)
    {
        if (config.Schemes.Count == 0 && config.Templates.Count == 0)
            return;

        var catalog = new BuilderTagCatalog(config.Tags);
        foreach (var scheme in config.Schemes)
        {
            CompileEventConditions(scheme.Events, scheme.Name, "событие экрана",
                catalog, pool, diagnostics, stringGuard);
            CompileSchemeElements(scheme.Name, scheme.Elements, catalog, pool,
                diagnostics, stringGuard);
        }
        foreach (var template in config.Templates)
        {
            var templateCatalog = new TemplateParameterCatalog(template, catalog);
            CompileEventConditions(template.Events, template.Name, "событие шаблона",
                templateCatalog, pool, diagnostics, stringGuard);
            CompileSchemeElements(template.Name, template.Elements,
                templateCatalog, pool, diagnostics, stringGuard);
        }
    }

    private static void CompileSchemeElements(string schemeName,
        IReadOnlyList<SchemeElement> elements, ITagCatalog catalog,
        List<CompiledExpression> pool, List<BuildDiagnostic> diagnostics,
        StringTagGuard stringGuard)
    {
        for (int i = 0; i < elements.Count; i++)
        {
            var element = elements[i];
            string label = ElementLabel(element, i);

            foreach (var binding in element.Bindings)
            {
                // прямая строковая привязка (§4.6): текст — ровно имя
                // строкового тега → без ВМ, прямая ссылка на тег
                if (stringGuard.TryDirectBinding(schemeName, label, element,
                        binding, diagnostics))
                    continue;

                try
                {
                    CompiledExpression compiled =
                        ExpressionCompiler.Compile(binding.Expression, catalog);
                    stringGuard.CheckCompiled(compiled.TagIndices,
                        $"scheme:{schemeName}",
                        $"Схема '{schemeName}', элемент {label}, привязка свойства " +
                        $"{binding.PropertyId}", diagnostics);
                    binding.CompiledExpressionIndex = pool.Count; // до дедупликации
                    binding.CompiledTagIndices = compiled.TagIndices;
                    pool.Add(compiled);
                }
                catch (ExpressionCompileException ex)
                {
                    diagnostics.Add(new BuildDiagnostic(BuildSeverity.Error,
                        $"scheme:{schemeName}",
                        $"Схема '{schemeName}', элемент {label}, привязка свойства " +
                        $"{binding.PropertyId}: {ex.Message}"));
                }
            }

            CompileEventConditions(element.Events, schemeName, $"элемент {label}",
                catalog, pool, diagnostics, stringGuard);
        }
    }

    /// <summary>Компиляция условий действий списка событий (уровень экрана или
    /// элемента — общий механизм, §5.2).</summary>
    private static void CompileEventConditions(IReadOnlyList<SchemeEvent> events,
        string schemeName, string context, ITagCatalog catalog,
        List<CompiledExpression> pool, List<BuildDiagnostic> diagnostics,
        StringTagGuard stringGuard)
    {
        foreach (var schemeEvent in events)
            foreach (var action in schemeEvent.Actions)
            {
                if (action.Condition is null)
                    continue;
                try
                {
                    CompiledExpression compiled =
                        ExpressionCompiler.Compile(action.Condition, catalog);
                    stringGuard.CheckCompiled(compiled.TagIndices,
                        $"scheme:{schemeName}",
                        $"Схема '{schemeName}', {context}, условие действия", diagnostics);
                    action.CompiledConditionIndex = pool.Count; // до дедупликации
                    action.CompiledConditionTagIndices = compiled.TagIndices;
                    pool.Add(compiled);
                }
                catch (ExpressionCompileException ex)
                {
                    diagnostics.Add(new BuildDiagnostic(BuildSeverity.Error,
                        $"scheme:{schemeName}",
                        $"Схема '{schemeName}', {context}, условие действия: " +
                        $"{ex.Message}"));
                }
            }
    }

    /// <summary>Перевод индексов выражений привязок и условий действий
    /// в итоговую таблицу пула после дедупликации.</summary>
    private static void RemapSchemeExpressionIndices(ProjectConfiguration config, int[] poolIndices)
    {
        foreach (var scheme in config.Schemes)
            RemapEventConditions(scheme.Events, poolIndices);
        foreach (var template in config.Templates)
            RemapEventConditions(template.Events, poolIndices);

        foreach (var element in AllSchemeElements(config))
        {
            foreach (var binding in element.Bindings)
                if (binding.CompiledExpressionIndex is int input)
                    binding.CompiledExpressionIndex = poolIndices[input];
            RemapEventConditions(element.Events, poolIndices);
        }
    }

    private static void RemapEventConditions(IReadOnlyList<SchemeEvent> events, int[] poolIndices)
    {
        foreach (var schemeEvent in events)
            foreach (var action in schemeEvent.Actions)
                if (action.CompiledConditionIndex is int input)
                    action.CompiledConditionIndex = poolIndices[input];
    }

    /// <summary>
    /// Валидация схем при сборке (§11.4): имена (становятся именами секций),
    /// абсолютные ссылки на теги в действиях, существование шаблонов у
    /// Instance-элементов, параметры экземпляров по объявлениям шаблона,
    /// рекурсия шаблонов (§7). Абсолютные ссылки в TagId здесь НЕ резолвятся:
    /// в пакете действие хранит имя тега, раннее связывание — на стороне
    /// чтения/рантайма по каталогу проекта (аналог OpenScheme по имени).
    /// </summary>
    private static void ValidateSchemes(ProjectConfiguration config,
        List<BuildDiagnostic> diagnostics)
    {
        if (config.Schemes.Count == 0 && config.Templates.Count == 0)
            return;

        ValidateSchemeNames(config, diagnostics);

        var templatesByName = config.Templates
            .GroupBy(t => t.Name)
            .ToDictionary(g => g.Key, g => g.First());
        var catalog = new BuilderTagCatalog(config.Tags);

        foreach (var scheme in config.Schemes)
        {
            ValidateEventTagRefs(scheme.Events, scheme.Name, "событие экрана",
                templateParameters: null, catalog, diagnostics);
            ValidateSchemeElements(scheme.Name, scheme.Elements, templatesByName,
                templateParameters: null, catalog, diagnostics);
        }
        foreach (var template in config.Templates)
        {
            var templateParameters = template.Parameters.Select(p => p.Name).ToHashSet();
            ValidateEventTagRefs(template.Events, template.Name, "событие шаблона",
                templateParameters, catalog, diagnostics);
            ValidateSchemeElements(template.Name, template.Elements, templatesByName,
                templateParameters, catalog, diagnostics);
        }

        ValidateTemplateCycles(templatesByName, diagnostics);
        ValidateSchemeRights(config, diagnostics);
        ValidateDesignArea(config, diagnostics);
    }

    /// <summary>
    /// Сверка прав, использованных на схемах, с правами ролей проекта
    /// (docs/users-plan.md §5). Право, которого нет ни у одной роли, — почти
    /// всегда опечатка: «Уставки.Еdit» с русской «е» ничем не отличается на
    /// вид, а на объекте кнопка молча не работает.
    /// Это предупреждение, а не ошибка: проектные права — произвольные строки,
    /// роль может появиться позже, а сборка не должна падать из-за этого.
    /// </summary>
    private static void ValidateSchemeRights(ProjectConfiguration config,
        List<BuildDiagnostic> diagnostics)
    {
        var used = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);

        void Use(string? right, string where)
        {
            if (string.IsNullOrWhiteSpace(right))
                return;
            if (!used.TryGetValue(right, out var places))
                used[right] = places = new SortedSet<string>(StringComparer.Ordinal);
            places.Add(where);
        }

        void UseEvents(IReadOnlyList<SchemeEvent> events, string where)
        {
            foreach (var schemeEvent in events)
                foreach (var action in schemeEvent.Actions)
                    Use(action.RequiredRight, where);
        }

        void UseElements(IReadOnlyList<SchemeElement> elements, string where)
        {
            foreach (var element in elements)
            {
                Use(element.RequiredRight, where);
                UseEvents(element.Events, where);
            }
        }

        foreach (var scheme in config.Schemes)
        {
            Use(scheme.RequiredRight, $"экран '{scheme.Name}'");
            UseEvents(scheme.Events, $"экран '{scheme.Name}'");
            UseElements(scheme.Elements, $"экран '{scheme.Name}'");
        }
        foreach (var template in config.Templates)
        {
            UseEvents(template.Events, $"шаблон '{template.Name}'");
            UseElements(template.Elements, $"шаблон '{template.Name}'");
        }

        if (used.Count == 0)
            return;

        if (config.Users.Roles.Count == 0)
        {
            diagnostics.Add(new BuildDiagnostic(BuildSeverity.Warning, "rights",
                $"На схемах заданы права ({string.Join(", ", used.Keys)}), но в проекте " +
                "нет ни одной роли (roles.json): ни один пользователь их не получит"));
            return;
        }

        // системные права выданы по определению: их проверяет ядро, а не роли
        var granted = new HashSet<string>(SystemPermissions.All, StringComparer.Ordinal);
        foreach (var role in config.Users.Roles)
            foreach (string permission in role.Permissions)
                granted.Add(permission);

        foreach (var (right, places) in used)
            if (!granted.Contains(right))
                diagnostics.Add(new BuildDiagnostic(BuildSeverity.Warning, "rights",
                    $"Право '{right}' ({string.Join(", ", places)}) не выдано ни одной " +
                    "роли проекта — опечатка?"));
    }

    /// <summary>Имя схемы/шаблона — имя секции пакета: недопустимые символы
    /// пути и дубликаты — ошибки сборки.</summary>
    private static void ValidateSchemeNames(ProjectConfiguration config,
        List<BuildDiagnostic> diagnostics)
    {
        // '/' недопустим дополнительно: в zip-записях это разделитель
        char[] invalidChars = [..Path.GetInvalidFileNameChars(), '/'];

        void CheckName(string name, string what)
        {
            if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(invalidChars) >= 0)
                diagnostics.Add(new BuildDiagnostic(BuildSeverity.Error, $"scheme:{name}",
                    $"Недопустимое имя {what} '{name}': оно становится именем секции пакета"));
        }

        foreach (var scheme in config.Schemes)
            CheckName(scheme.Name, "схемы");
        foreach (var template in config.Templates)
            CheckName(template.Name, "шаблона");

        foreach (var group in config.Schemes.GroupBy(s => s.Name).Where(g => g.Count() > 1))
            diagnostics.Add(new BuildDiagnostic(BuildSeverity.Error, $"scheme:{group.Key}",
                $"Дубликат имени схемы '{group.Key}'"));
        foreach (var group in config.Templates.GroupBy(t => t.Name).Where(g => g.Count() > 1))
            diagnostics.Add(new BuildDiagnostic(BuildSeverity.Error, $"scheme:{group.Key}",
                $"Дубликат имени шаблона '{group.Key}'"));
    }

    private static void ValidateSchemeElements(string schemeName,
        IReadOnlyList<SchemeElement> elements,
        Dictionary<string, SchemeTemplate> templatesByName,
        HashSet<string>? templateParameters, BuilderTagCatalog catalog,
        List<BuildDiagnostic> diagnostics)
    {
        string source = $"scheme:{schemeName}";

        for (int i = 0; i < elements.Count; i++)
        {
            var element = elements[i];
            string label = ElementLabel(element, i);

            // Instance: шаблон существует, параметры экземпляра объявлены (§7)
            if (element.Kind == ElementKind.Instance &&
                !string.IsNullOrEmpty(element.TemplateName))
            {
                if (!templatesByName.TryGetValue(element.TemplateName, out var template))
                {
                    diagnostics.Add(new BuildDiagnostic(BuildSeverity.Error, source,
                        $"Схема '{schemeName}', элемент {label}: шаблон " +
                        $"'{element.TemplateName}' не найден"));
                }
                else if (element.TemplateParameters is not null)
                {
                    var declared = template.Parameters.Select(p => p.Name).ToHashSet();
                    foreach (string parameter in element.TemplateParameters.Keys)
                        if (!declared.Contains(parameter))
                            diagnostics.Add(new BuildDiagnostic(BuildSeverity.Error, source,
                                $"Схема '{schemeName}', элемент {label}: параметр " +
                                $"'{parameter}' не объявлен в шаблоне '{template.Name}'"));
                }
            }

            // ссылки на теги в действиях: абсолютные — существуют в каталоге,
            // параметрические — объявлены в текущем шаблоне (§4.4, §7)
            ValidateEventTagRefs(element.Events, schemeName, $"элемент {label}",
                templateParameters, catalog, diagnostics);
        }
    }

    /// <summary>Проверка ссылок на теги в действиях списка событий — общая для
    /// событий элемента и событий уровня экрана/шаблона (§5.1).</summary>
    private static void ValidateEventTagRefs(IReadOnlyList<SchemeEvent> events,
        string schemeName, string context, HashSet<string>? templateParameters,
        BuilderTagCatalog catalog, List<BuildDiagnostic> diagnostics)
    {
        string source = $"scheme:{schemeName}";

        foreach (var schemeEvent in events)
            foreach (var action in schemeEvent.Actions)
            {
                var tag = action switch
                {
                    WriteTagAction a => a.Tag,
                    ToggleTagAction a => a.Tag,
                    _ => (SchemeTagRef?)null
                };
                if (tag is not { } tagRef)
                    continue;

                if (tagRef.IsParametric)
                {
                    string head = ParametricHead(tagRef.Name);
                    if (templateParameters is null)
                        diagnostics.Add(new BuildDiagnostic(BuildSeverity.Error, source,
                            $"Схема '{schemeName}', {context}: параметрическая " +
                            $"ссылка '{tagRef.Name}' вне шаблона"));
                    else if (!templateParameters.Contains(head))
                        diagnostics.Add(new BuildDiagnostic(BuildSeverity.Error, source,
                            $"Шаблон '{schemeName}', {context}: параметр " +
                            $"'{head}' не объявлен в шаблоне"));
                }
                else if (!catalog.TryGetIndex(tagRef.Name, out _))
                {
                    diagnostics.Add(new BuildDiagnostic(BuildSeverity.Error, source,
                        $"Схема '{schemeName}', {context}: тег '{tagRef.Name}' не найден"));
                }
            }
    }

    /// <summary>Рекурсия шаблонов (шаблон включает себя — напрямую или по
    /// цепочке) — ошибка сборки (§7): иначе раскрытие экземпляра в рантайме
    /// не завершится. Раскрытие при сборке не выполняется (B2).</summary>
    private static void ValidateTemplateCycles(
        Dictionary<string, SchemeTemplate> templatesByName,
        List<BuildDiagnostic> diagnostics)
    {
        var visiting = new List<string>();   // текущий путь DFS (серые)
        var finished = new HashSet<string>(); // чёрные

        void Visit(string name)
        {
            if (finished.Contains(name))
                return;
            int cycleStart = visiting.IndexOf(name);
            if (cycleStart >= 0)
            {
                string cycle = string.Join(" → ", visiting.Skip(cycleStart).Append(name));
                diagnostics.Add(new BuildDiagnostic(BuildSeverity.Error, $"scheme:{name}",
                    $"Цикл шаблонов: {cycle}"));
                return;
            }

            visiting.Add(name);
            foreach (var element in templatesByName[name].Elements)
                if (element.Kind == ElementKind.Instance &&
                    element.TemplateName is not null &&
                    templatesByName.ContainsKey(element.TemplateName))
                    Visit(element.TemplateName);
            visiting.RemoveAt(visiting.Count - 1);
            finished.Add(name);
        }

        foreach (string name in templatesByName.Keys)
            Visit(name);
    }

    /// <summary>
    /// Ассеты схем (§11.4): упомянутые элементами символы и картинки обязаны
    /// существовать в каталоге проекта, иначе ошибка. Копируются ВСЕ файлы
    /// каталогов symbols/, images/, fonts/ — символы переиспользуются между
    /// схемами, отслеживать использование каждого файла бессмысленно (§3).
    /// </summary>
    private static List<(string Entry, byte[] Bytes)> CollectSchemeAssetEntries(
        ProjectConfiguration config, string projectDirectory,
        List<BuildDiagnostic> diagnostics)
    {
        var entries = new List<(string, byte[])>();
        if (config.Schemes.Count == 0 && config.Templates.Count == 0)
            return entries;

        var referencedSymbols = new HashSet<string>();
        var referencedImages = new HashSet<string>();
        foreach (var element in AllSchemeElements(config))
            foreach (var property in element.Properties)
            {
                // свойства ищем по имени дескриптора, а не по id — id стабильны,
                // но имя читается лучше и переживает перенумерацию реестра
                var def = ElementSchemas.Find(element.Kind, property.PropertyId);
                if (def is null || property.Value.Text is not { Length: > 0 } assetName)
                    continue;
                if (def.Name == "SymbolName")
                    referencedSymbols.Add(assetName);
                else if (def.Name == "ImageName")
                    referencedImages.Add(assetName);
            }

        CheckAssetsExist(projectDirectory, "symbols", referencedSymbols, diagnostics);
        CheckAssetsExist(projectDirectory, "images", referencedImages, diagnostics);

        foreach (string directory in new[] { "symbols", "images", "fonts" })
        {
            string fullDirectory = Path.Combine(projectDirectory, directory);
            if (!Directory.Exists(fullDirectory))
                continue;
            foreach (string file in Directory.EnumerateFiles(fullDirectory).Order())
                entries.Add(($"{directory}/{Path.GetFileName(file)}", File.ReadAllBytes(file)));
        }
        return entries;
    }

    private static void CheckAssetsExist(string projectDirectory, string directory,
        HashSet<string> referenced, List<BuildDiagnostic> diagnostics)
    {
        foreach (string name in referenced.Order())
            if (!File.Exists(Path.Combine(projectDirectory, directory, name)))
                diagnostics.Add(new BuildDiagnostic(BuildSeverity.Error, "schemes:assets",
                    $"Файл '{directory}/{name}', упомянутый на схеме, не найден " +
                    "в каталоге проекта"));
    }

    private static IEnumerable<SchemeElement> AllSchemeElements(ProjectConfiguration config)
        => config.Schemes.SelectMany(s => s.Elements)
            .Concat(config.Templates.SelectMany(t => t.Elements));

    private static string ElementLabel(SchemeElement element, int index)
        => element.Name.Length > 0 ? $"'{element.Name}'" : $"#{index}";

    /// <summary>Голова параметрической ссылки: "Prefix.Скорость" → "Prefix"
    /// (имя параметра шаблона — первый сегмент, §7).</summary>
    private static string ParametricHead(string name)
    {
        int dot = name.IndexOf('.');
        return dot < 0 ? name : name[..dot];
    }

    /// <summary>
    /// Каталог тегов для компиляции выражений шаблона: тег, чей первый
    /// сегмент — объявленный параметр шаблона, считается параметрической
    /// ссылкой и получает индекс-заглушку -1 (реальный TagId подставляется
    /// при раскрытии экземпляра, §7). Остальные имена — в проектный каталог:
    /// неизвестные по-прежнему ошибка компиляции.
    /// </summary>
    private sealed class TemplateParameterCatalog(SchemeTemplate template, ITagCatalog inner)
        : ITagCatalog
    {
        private readonly HashSet<string> _parameters =
            template.Parameters.Select(p => p.Name).ToHashSet();

        public bool TryGetIndex(string name, out int index)
        {
            if (_parameters.Contains(ParametricHead(name)))
            {
                index = -1;
                return true;
            }
            return inner.TryGetIndex(name, out index);
        }
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
    /// Строковые теги при сборке (концепт §4.6): выражения числовые, поэтому
    /// строковый тег в выражении — ошибка сборки (иначе ВМ молча прочитала бы
    /// числовой слот). Исключение — прямая строковая привязка: текст привязки,
    /// в точности равный имени строкового тега, оформляется без ВМ в прямую
    /// ссылку (CompiledExpressionIndex = null, CompiledTagIndices = [tagId]) и
    /// допустима только на свойство типа String.
    /// </summary>
    private sealed class StringTagGuard
    {
        private readonly Dictionary<string, TagDefinition> _byName;
        private readonly TagDefinition?[] _byIndex;

        public StringTagGuard(IReadOnlyList<TagDefinition> tags)
        {
            _byName = tags.ToDictionary(t => t.Name);
            _byIndex = new TagDefinition?[tags.Count == 0 ? 0 : tags.Max(t => t.Id.Value) + 1];
            foreach (var tag in tags) _byIndex[tag.Id.Value] = tag;
        }

        /// <summary>
        /// Пытается оформить привязку как прямую строковую. Возвращает true,
        /// если привязка обработана (оформлена или отклонена ошибкой) и
        /// компилировать её не нужно.
        /// </summary>
        public bool TryDirectBinding(string schemeName, string elementLabel,
            SchemeElement element, ElementBinding binding, List<BuildDiagnostic> diagnostics)
        {
            if (!_byName.TryGetValue(binding.Expression.Trim(), out var tag)
                || tag.DataType != TagDataType.String)
                return false;

            var def = ElementSchemas.Find(element.Kind, binding.PropertyId);
            if (def?.Type != PropertyType.String)
            {
                diagnostics.Add(new BuildDiagnostic(BuildSeverity.Error, $"scheme:{schemeName}",
                    $"Схема '{schemeName}', элемент {elementLabel}: прямая привязка строкового " +
                    $"тега '{tag.Name}' допустима только на свойство типа String " +
                    $"(свойство {binding.PropertyId} — {def?.Type.ToString() ?? "неизвестно"})"));
                return true;
            }

            binding.CompiledExpressionIndex = null;
            binding.CompiledTagIndices = [tag.Id.Value];
            return true;
        }

        /// <summary>Строковый тег в скомпилированном выражении — ошибка сборки.</summary>
        public void CheckCompiled(int[] tagIndices, string source, string context,
            List<BuildDiagnostic> diagnostics)
        {
            foreach (int index in tagIndices)
            {
                if (index < 0 || index >= _byIndex.Length) continue; // -1 — параметрическая заглушка шаблона
                if (_byIndex[index] is { DataType: TagDataType.String } tag)
                    diagnostics.Add(new BuildDiagnostic(BuildSeverity.Error, source,
                        $"{context}: строковый тег '{tag.Name}' не может участвовать в выражении — " +
                        $"выражения числовые (концепт §4.6, §14)"));
            }
        }
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
