using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SCADA.Runtime.Users;

namespace SCADA.Runtime.Hosting;

/// <summary>
/// Тик правил завершения сессии (docs/users-plan.md §6.1). Отдельная
/// секунда опроса, а не проверка «по случаю»: оператор должен увидеть
/// блокировку по бездействию, ничего не нажимая. Логики здесь нет —
/// решение принимают правила в <see cref="SessionService"/>.
/// </summary>
public sealed class SessionHostService(
    ISessionService sessions,
    ILogger<SessionHostService> logger) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        sessions.SessionEnded += OnSessionEnded;
        try
        {
            using var timer = new PeriodicTimer(TickInterval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
                sessions.Evaluate();
        }
        catch (OperationCanceledException)
        {
            // штатная остановка хоста
        }
        finally
        {
            sessions.SessionEnded -= OnSessionEnded;
        }
    }

    private void OnSessionEnded(SessionEndedEventArgs e) =>
        logger.LogInformation("Сессия '{Login}' завершена: {Reason} ({Action})",
            e.Session.Login, e.Reason, e.Action);
}
