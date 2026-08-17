using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SCADA.Core.Tags;
using SCADA.Historian;
using SCADA.Runtime.Configuration;
using SCADA.Runtime.Historian;

namespace SCADA.Server;

/// <summary>
/// Жизненный цикл архива внутри хоста: восстановление после аварийного
/// завершения при старте, конвейер, ротация, надзор за местом на диске,
/// обновление диагностики, упорядоченное закрытие блоков при остановке.
/// Логики архивирования здесь нет — только порядок и связывание.
/// </summary>
public sealed class ArchiveHostService(
    ArchivePipeline pipeline,
    IArchiveStore store,
    ArchiveDiagnostics diagnostics,
    IRetentionPolicy retentionPolicy,
    DiskSpaceSupervisor diskSupervisor,
    ArchiveOptions options,
    ProjectConfiguration config,
    ITagTable tagTable,
    ILogger<ArchiveHostService> logger) : BackgroundService
{
    private static readonly TimeSpan RetentionInterval = TimeSpan.FromDays(1);

    private DateTimeOffset _nextRetentionRun = DateTimeOffset.MinValue;
    private int? _forcedRetentionDays;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Порядок обязателен: сначала отыграть журнал, потом пускать конвейер.
        // Иначе новые отсчёты смешаются с восстанавливаемыми и нарушат
        // монотонность потока (§6.3).
        Recover();

        int archivedTags = config.Tags.Count(t => t.IsArchived);
        logger.LogInformation(
            "Архив запущен: {Archived} из {Total} тегов логируется, каталог {Root}, глубина {Days} сут",
            archivedTags, config.Tags.Count, options.Root, options.RetentionDays);

        // Проход ротации при старте: служба могла простоять дольше суток,
        // и ждать ещё сутки, чтобы удалить просроченное, незачем.
        RunRetention();

        await pipeline.StartAsync(stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(options.DiagnosticsIntervalMs));

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                SuperviseDiskSpace();

                if (DateTimeOffset.UtcNow >= _nextRetentionRun)
                    RunRetention();

                FlushDiagnostics();
            }
        }
        catch (OperationCanceledException)
        {
            // штатная остановка хоста
        }

        await StopArchiveAsync();
    }

    private void Recover()
    {
        if (store is not FileArchiveStore fileStore)
            return;

        try
        {
            int recovered = fileStore.RecoverFromWal();
            if (recovered > 0)
            {
                logger.LogWarning(
                    "Из журнала восстановлено {Count} отсчётов — предыдущее завершение было аварийным",
                    recovered);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            // Повреждённый журнал не должен мешать сбору данных (ТЗ §4.2):
            // сообщаем и продолжаем с тем, что уцелело.
            logger.LogError(ex, "Восстановление архива из журнала не завершено");
        }
    }

    /// <summary>
    /// Надзор за местом (ТЗ §8.9). Решение принимает <see cref="DiskSpaceSupervisor"/>,
    /// здесь только исполнение и журналирование: удаление данных заказчика
    /// обязано быть видно, тихая потеря недопустима.
    /// </summary>
    private void SuperviseDiskSpace()
    {
        long freeMb = (long)diagnostics.MeasureFreeDiskMb();
        var decision = diskSupervisor.Evaluate(freeMb, _forcedRetentionDays);

        if (decision.ShouldAlarm && decision.Reason is not null)
            logger.LogWarning("Архив: {Reason}", decision.Reason);

        if (decision.ForcedRetentionDays != _forcedRetentionDays)
        {
            _forcedRetentionDays = decision.ForcedRetentionDays;

            // Ужали срок — освобождаем место немедленно, не дожидаясь суток.
            if (_forcedRetentionDays is not null)
                RunRetention();
        }

        if (store is FileArchiveStore fileStore)
            fileStore.SuspendWriting(decision.SuspendWriting);

        diagnostics.State = decision.State switch
        {
            DiskSpaceState.LowSpace => ArchiveDiagnostics.ArchiveState.LowDiskSpace,
            DiskSpaceState.WritingStopped => ArchiveDiagnostics.ArchiveState.WritingStopped,
            _ => ArchiveDiagnostics.ArchiveState.Normal
        };
    }

    private void RunRetention()
    {
        _nextRetentionRun = DateTimeOffset.UtcNow + RetentionInterval;

        if (store is not FileArchiveStore fileStore)
            return;

        try
        {
            var report = fileStore.ApplyRetention(retentionPolicy,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), _forcedRetentionDays);

            if (report.AnythingDeleted)
            {
                // Штатное удаление по возрасту — Information; досрочное,
                // вызванное нехваткой места, — Warning: это отступление от
                // обещанной заказчику глубины, и оно должно быть заметно.
                var level = _forcedRetentionDays is null ? LogLevel.Information : LogLevel.Warning;
                logger.Log(level,
                    "Ротация архива: удалено {Files} файлов, {Months} каталогов месяцев, " +
                    "освобождено {Mb:F1} МБ, срок {Days} сут",
                    report.DeletedFiles, report.MonthsRemoved,
                    report.FreedBytes / 1024.0 / 1024.0,
                    _forcedRetentionDays ?? options.RetentionDays);
            }

            if (report.HitFloor)
            {
                logger.LogWarning(
                    "Ротация упёрлась в пол хранения {Floor} сут: {Count} потоков не ужаты",
                    retentionPolicy.MinRetentionDays, report.SkippedByFloor);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Проход ротации архива не завершён");
        }
    }

    private void FlushDiagnostics()
    {
        try
        {
            if (store is FileArchiveStore fileStore)
                diagnostics.DroppedNoSpaceCount = fileStore.DroppedNoSpaceCount;

            diagnostics.Flush(tagTable, pipeline, store,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // обход каталога мог не удаться — диагностика не критична
            logger.LogWarning(ex, "Не удалось обновить диагностику архива");
        }
    }

    private async Task StopArchiveAsync()
    {
        // CancellationToken.None намеренно: закрыть открытые блоки надо даже
        // при остановке по таймауту, иначе потеряются данные, уже принятые
        // конвейером. Журнал их удержит, но лишнее восстановление ни к чему.
        await pipeline.StopAsync(CancellationToken.None);
        await store.FlushAsync(CancellationToken.None);

        (store as IDisposable)?.Dispose();
        logger.LogInformation("Архив остановлен, блоки закрыты");
    }
}
