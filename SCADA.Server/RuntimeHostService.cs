using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SCADA.Runtime.Polling;
using SCADA.Core.Tags;

namespace SCADA.Server;

/// <summary>
/// Жизненный цикл опроса внутри хоста: старт движка при запуске,
/// упорядоченная остановка при завершении процесса (драйверы отключаются,
/// циклы каналов завершаются — а не убиваются посреди записи).
/// Логики здесь нет — только старт/стоп и heartbeat.
/// </summary>
public sealed class RuntimeHostService(
    PollingEngine engine,
    ITagTable tagTable,
    ILogger<RuntimeHostService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        engine.OnDeviceError = (device, ex) =>
            logger.LogWarning("Опрос '{Device}': {Type}: {Message}",
                device.Name, ex.GetType().Name, ex.Message);

        await engine.StartAsync(stoppingToken);
        logger.LogInformation("Опрос запущен");

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                logger.LogInformation("Опрос активен, эпоха {Epoch}", tagTable.CurrentEpoch);
            }
        }
        catch (OperationCanceledException)
        {
            // штатная остановка хоста
        }

        await engine.StopAsync();
        logger.LogInformation("Опрос остановлен");
    }
}
