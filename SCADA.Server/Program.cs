using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using SCADA.Alarms;
using SCADA.Drivers.Modbus;
using SCADA.Drivers.Simulator;
using SCADA.Package.Builder;
using SCADA.Runtime.Alarms;
using SCADA.Runtime.Historian;
using SCADA.Runtime.Polling;
using SCADA.Runtime.Runtime;

// Composition root сервера сбора данных: регистрация драйверов (ТЗ §7.2),
// чтение конфигурации процесса и запуск RuntimeHost. Само связывание ядра
// (таблица → движок → сигнализация → архив) живёт внутри RuntimeHost.

var builder = Host.CreateApplicationBuilder(args);

// Внешние драйверы регистрируются здесь — Runtime о них не знает (ТЗ §7.2).
// Симулятор тоже подключается в composition root: он нужен для проверки
// мнемосхем без реального ПЛК (режим исполнения/симуляции в редакторе).
DriverFactory.Register("simulator", () => new SimulatorDriver());
DriverFactory.Register("modbus-tcp", () => new ModbusTcpDriver());

string projectPath = builder.Configuration["Runtime:Project"]
    ?? throw new InvalidOperationException("Не задан путь к проекту (Runtime:Project)");

// Рантайм работает только с собранным пакетом (A5.9). Для удобства демо:
// если указан каталог исходников, собираем пакет на месте и запускаем его.
if (Directory.Exists(projectPath))
{
    string packagePath = Path.Combine(projectPath, "output", "DemoProject.scadapkg");
    Console.WriteLine($"[демо] сборка пакета из исходников: {projectPath} → {packagePath}");
    var build = ProjectBuildService.Build(projectPath, packagePath);
    foreach (var diagnostic in build.Diagnostics)
        Console.WriteLine($"[сборка] {diagnostic}");
    if (!build.Success)
        throw new InvalidOperationException("Сборка демо-проекта не удалась");
    projectPath = packagePath;
}

var journal = new JournalOptions();
builder.Configuration.GetSection("Runtime:Journal").Bind(journal);
var alarms = new AlarmPipelineOptions();
builder.Configuration.GetSection("Runtime:Alarms").Bind(alarms);
var archive = new ArchiveOptions();
builder.Configuration.GetSection("Runtime:Archive").Bind(archive);
var historyLimits = new HistoryQueryLimits();
builder.Configuration.GetSection("Runtime:HistoryLimits").Bind(historyLimits);

var options = new RuntimeHostOptions
{
    ProjectPath = projectPath,
    PollPeriod = TimeSpan.FromMilliseconds(
        builder.Configuration.GetValue("Runtime:PollPeriodMs", 100)),
    Journal = journal,
    Alarms = alarms,
    Archive = archive,
    HistoryLimits = historyLimits
};

await RuntimeHost.RunAsync(options);
