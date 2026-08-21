using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SCADA.Alarms;
using SCADA.Core.Alarms;
using SCADA.Core.Tags;
using SCADA.Core.Users;
using SCADA.Historian;
using SCADA.Package;
using SCADA.Package.Sections;
using SCADA.Runtime.Alarms;
using SCADA.Runtime.Audit;
using SCADA.Runtime.Historian;
using SCADA.Runtime.Hosting;
using SCADA.Runtime.Polling;
using SCADA.Runtime.Schemes;
using SCADA.Runtime.TagTable;
using SCADA.Runtime.Users;

namespace SCADA.Runtime.Runtime;

/// <summary>
/// Переиспользуемый composition root исполнения: пакет .scadapkg → конфиг →
/// таблица тегов → движок опроса → сигнализация → архив → клиент.
/// Внутри — собственный Generic Host; наружу только <see cref="Client"/>,
/// состояние и жизненный цикл. Связывание повторяет логику, которая раньше
/// жила в SCADA.Server/Program.cs; логики здесь нет.
/// </summary>
public sealed class RuntimeHost : IAsyncDisposable
{
    private readonly IHost _host;

    // Созданные вручную объекты, переданные в контейнер как экземпляры:
    // контейнер их не освобождает, поэтому освобождаем сами в DisposeAsync
    // (соединения SQLite держат файловый дескриптор events.db).
    private readonly IReadOnlyList<IDisposable> _owned;

    private RuntimeHost(IHost host, IReadOnlyList<IDisposable> owned)
    {
        _host = host;
        _owned = owned;
        Client = host.Services.GetRequiredService<IRuntimeClient>();
    }

    /// <summary>Текущее состояние жизненного цикла.</summary>
    public RuntimeState State { get; private set; } = RuntimeState.Starting;

    /// <summary>Вызывается при каждом переходе <see cref="State"/>.</summary>
    public event Action<RuntimeState>? StateChanged;

    /// <summary>Доступ к оперативным данным запущенного рантайма.</summary>
    public IRuntimeClient Client { get; }

    private void SetState(RuntimeState state)
    {
        if (State == state)
            return;
        State = state;
        StateChanged?.Invoke(state);
    }

    /// <summary>Собирает внутренний хост и запускает движки.</summary>
    public static async Task<RuntimeHost> StartAsync(
        RuntimeHostOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var (host, owned) = BuildHost(options);
        var runtime = new RuntimeHost(host, owned);
        try
        {
            await host.StartAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            runtime.SetState(RuntimeState.Faulted);
            throw;
        }
        runtime.SetState(RuntimeState.Running);
        return runtime;
    }

    /// <summary>Старт + ожидание остановки хоста (сценарий службы).</summary>
    public static async Task RunAsync(RuntimeHostOptions options, CancellationToken ct = default)
    {
        await using var runtime = await StartAsync(options, ct).ConfigureAwait(false);
        try
        {
            await runtime._host.WaitForShutdownAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // остановка по токену — ниже штатный StopAsync
        }
        await runtime.StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Упорядоченная остановка движков. Повторный вызов — no-op.</summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        if (State is RuntimeState.Stopped or RuntimeState.Faulted)
            return;

        try
        {
            await _host.StopAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            SetState(RuntimeState.Faulted);
            throw;
        }
        SetState(RuntimeState.Stopped);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        catch
        {
            // состояние уже Faulted; освобождение ресурсов продолжаем
        }
        _host.Dispose();
        foreach (var disposable in _owned)
            disposable.Dispose();
    }

    // Связывание перенесено из SCADA.Server/Program.cs дословно по логике;
    // конфигурация приходит через RuntimeHostOptions, а не из appsettings.
    private static (IHost Host, IReadOnlyList<IDisposable> Owned) BuildHost(
        RuntimeHostOptions options)
    {
        var builder = Host.CreateApplicationBuilder();

        // Рантайм работает только с собранным пакетом .scadapkg (ТЗ §14.2, A5.9):
        // единственный путь загрузки — JSON-исходники читают редактор и сборщик,
        // единственный мост «исходники → пакет» — ProjectBuildService.
        string packagePath = options.ProjectPath;
        if (Directory.Exists(packagePath))
            throw new InvalidOperationException(
                $"Рантайм исполняет только собранный пакет .scadapkg (A5.9), " +
                $"получен каталог исходников: {packagePath}. " +
                "Соберите проект через ProjectBuildService.");
        var owned = new List<IDisposable>();

        using var packageReader = PackageReader.Open(packagePath);
        ProjectConfiguration config = PackageProjectLoader.Load(packageReader);
        CodePool codePool = PackageProjectLoader.LoadCodePool(packageReader);
        // имена секций нужны каталогу схем для списка ассетов — захватываем,
        // пока читатель открыт (дальше ассеты читаются лениво по пути пакета)
        var manifestEntryNames = packageReader.Manifest.Entries
            .Select(e => e.Name).ToArray();

        // Изменяемое состояние живёт под папкой проекта (ТЗ §14.6). Для пакета
        // это каталог, в котором он лежит: сам .scadapkg неизменяем.
        string projectDirectory = Path.GetDirectoryName(Path.GetFullPath(packagePath))!;

        // одна шкала времени на зрителя: общая и сессионная таблицы делят
        // счётчик эпох, поэтому «изменилось после N» сопоставимо между ними
        // (docs/session-tags-concept.md §4)
        var epochs = new EpochCounter();
        var tagTable = new global::SCADA.Runtime.TagTable.TagTable(config.Tags.Count, epochs);

        // M7: запись в устройства — аудит (та же events.db, таблица Audit, ТЗ §13)
        // и персистентность internal-тегов (файл в папке проекта, §14.6)
        var auditJournal = new SqliteAuditJournal(
            Path.Combine(projectDirectory, "events.db"),
            warning => Console.WriteLine(warning));
        owned.Add(auditJournal);
        var persistentTags = new PersistentTagStore(
            Path.Combine(projectDirectory, "persistent-tags.json"));

        var engine = new PollingEngine(config, tagTable, options.PollPeriod,
            audit: auditJournal, persistence: persistentTags);

        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton<ITagTable>(tagTable);
        builder.Services.AddSingleton<IAuditJournal>(auditJournal);
        builder.Services.AddSingleton(engine);
        builder.Services.AddHostedService<RuntimeHostService>();

        // имя → TagId: нужно и системным тегам аварий (концепт §10, движок
        // публикует состояние в TagTable), и сессионным, и каталогу схем
        var tagsByName = new Dictionary<string, TagId>(config.Tags.Count);
        foreach (var tag in config.Tags)
            tagsByName[tag.Name] = tag.Id;

        // --- пользователи и сессии (docs/users-plan.md §6) ---

        // users.json — данные эксплуатации, лежат рядом с журналом и архивом,
        // в пакет не входят (§3). Роли и политики приезжают из пакета.
        var userStore = new UserStore(projectDirectory, config.Users);
        if (userStore.EnsureAdmin())
            Console.WriteLine(
                $"[пользователи] не осталось ни одного пользователя с правом " +
                $"{SystemPermissions.ManageUsers}: создана учётка восстановления " +
                $"'{UserStore.DefaultAdminLogin}' с паролем по умолчанию — смените его");

        // сессионные теги (docs/session-tags-concept.md): их значения живут
        // в локальной таблице клиента. Движок опроса, сигнализация и архив
        // продолжают работать с общей таблицей — они про объект, а не про АРМ
        var clientTagTable = SessionTagRouter.Wrap(tagTable, config.Tags, epochs);
        var sessionTagRouter = clientTagTable as SessionTagRouter;

        var sessions = new SessionService(userStore, config.Users, options.AuthMode);
        // проверки прав в ядре (§5): в режиме Local встроенный администратор
        // разрешает всё — поведение существующих запусков не меняется
        var accessControl = new SessionAccessControl(sessions);

        // системные сессионные теги: заполняются по событиям сессии.
        // Права берутся из конфигурации — теги под них завёл генератор,
        // и гасить снятые права надо по полному набору, а не по выданным
        var rightPermissions = config.Tags
            .Where(t => t.Name.StartsWith(SessionSystemTags.RightPrefix, StringComparison.Ordinal))
            .Select(t => t.Name[SessionSystemTags.RightPrefix.Length..])
            .ToArray();
        var sessionTagPublisher = new SessionTagPublisher(clientTagTable, sessions,
            name => tagsByName.TryGetValue(name, out var id) ? id : null,
            Environment.MachineName, rightPermissions);
        owned.Add(sessionTagPublisher);

        builder.Services.AddSingleton<IUserStore>(userStore);
        builder.Services.AddSingleton<ISessionService>(sessions);
        builder.Services.AddSingleton<IAccessControl>(accessControl);
        builder.Services.AddHostedService<SessionHostService>();

        // --- сигнализация (M5, docs/M5-plan.md) ---

        var journalOptions = options.Journal;
        var alarmOptions = options.Alarms;

        // Боевая поставка: правила уже скомпилированы в пул code.bin пакета (§6),
        // компилятора в рантайме нет (§5.4).
        PreparedAlarmRule? PrepareFromPool(AlarmRule rule)
        {
            if (rule.CompiledExpressionIndex is not int index)
            {
                Console.WriteLine(
                    $"[сигнализация] правило '{rule.Name}': нет скомпилированного условия, правило пропущено");
                return null;
            }
            return new PreparedAlarmRule
            {
                Rule = rule,
                Condition = codePool.ToExpression(index),
                TagIndices = codePool.Expressions[index].TagIndices
            };
        }

        var preparedRules = AlarmRulePreparer.Prepare(
            config.Alarms, config.Tags, PrepareFromPool,
            warning => Console.WriteLine(warning));

        // каталог статических данных проекта (M6): схемы, шаблоны, пул
        // выражений, имена тегов, ассеты — неизменен в течение сессии
        var schemeCatalog = new SchemeCatalog(
            config, codePool, tagsByName, manifestEntryNames, packagePath);

        var alarmTagPublisher = new AlarmTagPublisher(tagTable,
            config.Alarms.Rules.Select(r => r.Name),
            name => tagsByName.TryGetValue(name, out var id) ? id : (TagId?)null);

        var alarmEngine = new AlarmEngine(config.Alarms, preparedRules, tagTable, config.Tags,
            alarmTagPublisher);

        var eventJournalPath = Path.Combine(projectDirectory, "events.db");
        var eventJournal = new SqliteEventJournal(
            eventJournalPath,
            warning => Console.WriteLine(warning));
        owned.Add(eventJournal);

        // восстановление активных аварий из журнала (§7.3):
        // неквитированные переживают перезапуск службы
        var recovered = AlarmStateRecovery.Resolve(
            eventJournal.ReadRecentDesc(alarmOptions.RecoveryReadLimit));
        alarmEngine.RestoreRecovered(recovered);
        if (recovered.Count > 0)
            Console.WriteLine($"[сигнализация] восстановлено активных аварий: {recovered.Count}");

        var alarmBroadcaster = new AlarmChangeBroadcaster();

        builder.Services.AddSingleton(alarmOptions);
        builder.Services.AddSingleton(journalOptions);
        builder.Services.AddSingleton<IAlarmEngine>(alarmEngine);
        builder.Services.AddSingleton<IEventJournal>(eventJournal);
        builder.Services.AddSingleton(alarmBroadcaster);
        builder.Services.AddHostedService(_ => new AlarmPipeline(
            tagTable, alarmEngine, eventJournal, alarmBroadcaster,
            alarmOptions, journalOptions, warning => Console.WriteLine(warning),
            journalSizeMbTag: tagsByName.TryGetValue(
                AlarmTags.SystemTag(AlarmTags.JournalSizeMbSuffix), out var sizeTag)
                ? sizeTag : null,
            journalSizeBytes: () =>
            {
                try { return new FileInfo(eventJournalPath).Length; }
                catch (IOException) { return 0; }
            }));

        // --- архив (ТЗ §8, docs/archive-format.md) ---

        var archiveOptions = options.Archive;

        // Пределы запросов истории проверяются на сервере: он общий для всех АРМов,
        // и один запрос сырого года по сотне тегов не должен ронять остальные (§14.1).
        var queryLimits = options.HistoryLimits;
        builder.Services.AddSingleton(queryLimits);

        if (archiveOptions.Enabled)
        {
            string archiveRoot = archiveOptions.ResolveRoot(projectDirectory);

            var registry = new ArchiveStreamRegistry(archiveRoot,
                warning => Console.WriteLine($"[архив] {warning}"));

            // Вместимость блока выводится из бюджета памяти и числа логируемых тегов:
            // открытый блок держит 24 байта на отсчёт на каждый поток (§8.6).
            int archivedTagCount = config.Tags.Count(t => t.IsArchived);
            int blockPoints = archiveOptions.ResolveBlockPoints(archivedTagCount);

            Console.WriteLine(
                $"[архив] блок: {blockPoints} отсчётов, пик памяти под открытые блоки " +
                $"{archiveOptions.EstimateOpenBlockMemoryMb(archivedTagCount):F0} МБ");

            // durable: журнал и исключительный захват каталога. Второй экземпляр
            // службы на том же каталоге получит отказ при старте, а не повредит архив.
            var store = new FileArchiveStore(archiveRoot,
                TimeSpan.FromMinutes(archiveOptions.BlockTimeoutMinutes),
                durable: true,
                walSegmentBytes: archiveOptions.WalSegmentSizeMb * 1024L * 1024,
                blockPoints: blockPoints);

            // Пояс разрешается на старте: неизвестный идентификатор обязан валить
            // запуск сразу, а не портить сменные отчёты месяцами.
            var siteTimeZone = archiveOptions.ResolveTimeZone();
            Console.WriteLine($"[архив] часовой пояс объекта: {siteTimeZone.Id}");

            var pipeline = new ArchivePipeline(tagTable, store, registry, config,
                new ArchivePipelineOptions
                {
                    TickInterval = TimeSpan.FromMilliseconds(archiveOptions.TickIntervalMs),
                    DefaultInterval = TimeSpan.FromMilliseconds(archiveOptions.DefaultIntervalMs),
                    FlushInterval = TimeSpan.FromMilliseconds(archiveOptions.FlushIntervalMs),
                    SiteTimeZone = siteTimeZone
                });

            // Кольцо последнего часа — горячий кэш фасада: realtime-тренды и правила
            // сигнализации диска не касаются (ТЗ §16.4).
            var ring = new InMemoryHistorian(config.Tags.Count);
            var historian = new RuntimeHistorian(ring, store, registry, config);

            var retentionPolicy = new FixedRetentionPolicy(
                archiveOptions.RetentionDays, archiveOptions.MinRetentionDays);

            var diskSupervisor = new DiskSpaceSupervisor(
                archiveOptions.MinFreeDiskMb, archiveOptions.OnDiskFull, retentionPolicy);

            builder.Services.AddSingleton(archiveOptions);
            builder.Services.AddSingleton<IArchiveStreamRegistry>(registry);
            builder.Services.AddSingleton<IArchiveStore>(store);
            builder.Services.AddSingleton(pipeline);
            builder.Services.AddSingleton<IHistorian>(historian);
            builder.Services.AddSingleton(new ArchiveDiagnostics(archiveRoot, config));
            builder.Services.AddSingleton<IRetentionPolicy>(retentionPolicy);
            builder.Services.AddSingleton(diskSupervisor);
            builder.Services.AddHostedService<ArchiveHostService>();

            builder.Services.AddSingleton<IRuntimeClient>(sp =>
                new LocalRuntimeClient(clientTagTable, sp.GetRequiredService<IHistorian>(),
                    queryLimits, alarmEngine, eventJournal, alarmBroadcaster, engine,
                    schemeCatalog, accessControl, auditJournal, sessionTagRouter));

            Console.WriteLine($"[архив] каталог: {archiveRoot}");
        }
        else
        {
            // Без архива клиент отдаёт текущие значения и пустую историю: схемы и
            // диагностика работают, тренды показывают пустоту вместо падения.
            builder.Services.AddSingleton<IRuntimeClient>(new LocalRuntimeClient(
                clientTagTable, null, queryLimits, alarmEngine, eventJournal, alarmBroadcaster,
                engine, schemeCatalog, accessControl, auditJournal, sessionTagRouter));
            Console.WriteLine("[архив] выключен настройкой Runtime:Archive:Enabled");
        }

        return (builder.Build(), owned);
    }
}
