using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SCADA.Core.Tags;
using SCADA.Drivers.Modbus;
using SCADA.Package;
using SCADA.Runtime.Configuration;
using SCADA.Runtime.Polling;
using SCADA.Runtime.TagTable;
using SCADA.Server;

// Composition root сервера сбора данных. Здесь только связывание:
// пакет/проект → конфиг → таблица → движок → жизненный цикл. Логики нет.

var builder = Host.CreateApplicationBuilder(args);

// внешние драйверы регистрируются здесь — Runtime о них не знает (ТЗ §7.2)
DriverFactory.Register("modbus-tcp", () => new ModbusTcpDriver());

var projectPath = builder.Configuration["Runtime:Project"]
    ?? throw new InvalidOperationException("Не задан путь к проекту (Runtime:Project)");
var pollPeriod = TimeSpan.FromMilliseconds(
    builder.Configuration.GetValue("Runtime:PollPeriodMs", 100));

ProjectConfiguration config;
if (Directory.Exists(projectPath))
{
    // dev-режим: исходный каталог проекта без сборки пакета.
    // Боевая поставка работает только с .scadapkg (ТЗ §14.2)
    config = ProjectLoader.Load(projectPath);
    Console.WriteLine($"[dev] проект загружен из исходного каталога: {projectPath}");
}
else
{
    config = PackageProjectLoader.Load(projectPath);
}

var tagTable = new TagTable(config.Tags.Count);
var engine = new PollingEngine(config, tagTable, pollPeriod);

builder.Services.AddSingleton(config);
builder.Services.AddSingleton<ITagTable>(tagTable);
builder.Services.AddSingleton(engine);
builder.Services.AddHostedService<RuntimeHostService>();

var host = builder.Build();
await host.RunAsync();
