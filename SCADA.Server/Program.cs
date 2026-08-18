using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using SCADA.Alarms;
using SCADA.Core.Alarms;
using SCADA.Drivers.Modbus;
using SCADA.Drivers.Simulator;
using SCADA.Expressions.Compiler;
using SCADA.Runtime.Alarms;
using SCADA.Runtime.Configuration;
using SCADA.Runtime.Historian;
using SCADA.Runtime.Polling;
using SCADA.Runtime.Runtime;
using SCADA.Server;

// Composition root сервера сбора данных: регистрация драйверов (ТЗ §7.2),
// чтение конфигурации процесса и запуск RuntimeHost. Само связывание ядра
// (таблица → движок → сигнализация → архив) живёт внутри RuntimeHost.

var builder = Host.CreateApplicationBuilder(args);

// Внешние драйверы регистрируются здесь — Runtime о них не знает (ТЗ §7.2).
// Симулятор тоже подключается в composition root: он нужен не только в dev,
// но и для проверки мнемосхем без реального ПЛК (режим исполнения в редакторе).
DriverFactory.Register("simulator", () => new SimulatorDriver());
DriverFactory.Register("modbus-tcp", () => new ModbusTcpDriver());

string projectPath = builder.Configuration["Runtime:Project"]
    ?? throw new InvalidOperationException("Не задан путь к проекту (Runtime:Project)");

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
    HistoryLimits = historyLimits,
    ExpressionFactory = CreateDevExpressionFactory(projectPath)
};

await RuntimeHost.RunAsync(options);

// dev-режим: условия expression-правил компилируются на месте. Боевая
// поставка получает скомпилированные правила из пакета (§6) и компилятор
// не использует (§5.4) — поэтому фабрика строится только для каталога проекта.
static Func<AlarmRule, PreparedAlarmRule?>? CreateDevExpressionFactory(string projectPath)
{
    if (!Directory.Exists(projectPath))
        return null;

    var catalog = new ProjectTagCatalog(ProjectLoader.Load(projectPath));
    return rule =>
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
