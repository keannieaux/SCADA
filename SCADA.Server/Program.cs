using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SCADA.Alarms;
using SCADA.Core.Alarms;
using SCADA.Core.Tags;
using SCADA.Drivers.Modbus;
using SCADA.Drivers.Simulator;
using SCADA.Expressions.Compiler;
using SCADA.Package;
using SCADA.Package.Sections;
using SCADA.Historian;
using SCADA.Runtime.Alarms;
using SCADA.Runtime.Audit;
using SCADA.Runtime.Configuration;
using SCADA.Runtime.Historian;
using SCADA.Runtime.Polling;
using SCADA.Runtime.Runtime;
using SCADA.Runtime.TagTable;
using SCADA.Server;

// Composition root сервера сбора данных. Здесь только связывание:
// пакет/проект → конфиг → таблица → движок → жизненный цикл. Логики нет.

var builder = Host.CreateApplicationBuilder(args);

// Внешние драйверы регистрируются здесь — Runtime о них не знает (ТЗ §7.2).
// Симулятор тоже подключается в composition root: он нужен не только в dev,
// но и для проверки мнемосхем без реального ПЛК (режим исполнения в редакторе).
DriverFactory.Register("simulator", () => new SimulatorDriver());
DriverFactory.Register("modbus-tcp", () => new ModbusTcpDriver());

var projectPath = builder.Configuration["Runtime:Project"]
    ?? throw new InvalidOperationException("Не задан путь к проекту (Runtime:Project)");
var pollPeriod = TimeSpan.FromMilliseconds(
    builder.Configuration.GetValue("Runtime:PollPeriodMs", 100));

ProjectConfiguration config;
CodePool? codePool = null;
bool devMode = Directory.Exists(projectPath);
if (devMode)
{
    // dev-режим: исходный каталог проекта без сборки пакета.
    // Боевая поставка работает только с .scadapkg (ТЗ §14.2)
    config = ProjectLoader.Load(projectPath);
    Console.WriteLine($"[dev] проект загружен из исходного каталога: {projectPath}");
}
else
{
    using var packageReader = PackageReader.Open(projectPath);
    config = PackageProjectLoader.Load(packageReader);
    codePool = PackageProjectLoader.LoadCodePool(packageReader);
}

// Изменяемое состояние живёт под папкой проекта (ТЗ §14.6). Для пакета
// это каталог, в котором он лежит: сам .scadapkg неизменяем.
string projectDirectory = devMode
    ? projectPath
    : Path.GetDirectoryName(Path.GetFullPath(projectPath))!;

var tagTable = new TagTable(config.Tags.Count);

// M7: запись в устройства — аудит (та же events.db, таблица Audit, ТЗ §13)
// и персистентность internal-тегов (файл в папке проекта, §14.6)
var auditJournal = new SqliteAuditJournal(
    Path.Combine(projectDirectory, "events.db"),
    warning => Console.WriteLine(warning));
var persistentTags = new PersistentTagStore(
    Path.Combine(projectDirectory, "persistent-tags.json"));

var engine = new PollingEngine(config, tagTable, pollPeriod,
    audit: auditJournal, persistence: persistentTags);

builder.Services.AddSingleton(config);
builder.Services.AddSingleton<ITagTable>(tagTable);
builder.Services.AddSingleton<IAuditJournal>(auditJournal);
builder.Services.AddSingleton(engine);
builder.Services.AddHostedService<RuntimeHostService>();

// --- сигнализация (M5, docs/M5-plan.md) ---

var journalOptions = new JournalOptions();
builder.Configuration.GetSection("Runtime:Journal").Bind(journalOptions);
var alarmOptions = new AlarmPipelineOptions();
builder.Configuration.GetSection("Runtime:Alarms").Bind(alarmOptions);

// dev-режим: условия expression-правил компилируются на месте. Боевая
// поставка получает скомпилированные правила из пакета (§6) и компилятор
// не использует (§5.4).
Func<AlarmRule, PreparedAlarmRule?>? expressionFactory = null;
if (devMode)
{
    var catalog = new ProjectTagCatalog(config);
    expressionFactory = rule =>
    {
        try
        {
            var compiled = ExpressionCompiler.Compile(rule.Condition!, catalog);
            return new PreparedAlarmRule
            {
                Rule = rule,
                Condition = compiled.ToExpression(),
                TagIndices = compiled.TagIndices
            };
        }
        catch (ExpressionCompileException ex)
        {
            Console.WriteLine($"[сигнализация] правило '{rule.Name}': {ex.Message}");
            return null;
        }
    };
}
else if (codePool is not null)
{
    // боевая поставка: правила уже скомпилированы в пул code.bin пакета (§6)
    expressionFactory = rule =>
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
    };
}

var preparedRules = AlarmRulePreparer.Prepare(
    config.Alarms, config.Tags, expressionFactory,
    warning => Console.WriteLine(warning));

var alarmEngine = new AlarmEngine(config.Alarms, preparedRules, tagTable, config.Tags);

var eventJournal = new SqliteEventJournal(
    Path.Combine(projectDirectory, "events.db"),
    warning => Console.WriteLine(warning));

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
    alarmOptions, journalOptions, warning => Console.WriteLine(warning)));

// --- архив (ТЗ §8, docs/archive-format.md) ---

var archiveOptions = new ArchiveOptions();
builder.Configuration.GetSection("Runtime:Archive").Bind(archiveOptions);

// Пределы запросов истории проверяются на сервере: он общий для всех АРМов,
// и один запрос сырого года по сотне тегов не должен ронять остальные (§14.1).
var queryLimits = new HistoryQueryLimits();
builder.Configuration.GetSection("Runtime:HistoryLimits").Bind(queryLimits);
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
        new LocalRuntimeClient(tagTable, sp.GetRequiredService<IHistorian>(), queryLimits,
            alarmEngine, eventJournal, alarmBroadcaster, engine));

    Console.WriteLine($"[архив] каталог: {archiveRoot}");
}
else
{
    // Без архива клиент отдаёт текущие значения и пустую историю: схемы и
    // диагностика работают, тренды показывают пустоту вместо падения.
    builder.Services.AddSingleton<IRuntimeClient>(new LocalRuntimeClient(
        tagTable, null, queryLimits, alarmEngine, eventJournal, alarmBroadcaster, engine));
    Console.WriteLine("[архив] выключен настройкой Runtime:Archive:Enabled");
}

var host = builder.Build();
await host.RunAsync();
