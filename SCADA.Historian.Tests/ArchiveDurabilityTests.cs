using SCADA.Core.Tags;

namespace SCADA.Historian.Tests;

/// <summary>
/// Отказоустойчивость архива (docs/archive-format.md §12, §16):
/// журнал упреждающей записи, восстановление после аварийного завершения,
/// исключительный захват каталога.
/// </summary>
public class ArchiveDurabilityTests : IDisposable
{
    private const long BaseTime = 1_700_000_000_000L;

    private readonly string _root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public ArchiveDurabilityTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* временный каталог */ }
    }

    private static readonly ArchiveStreamConfig Analog =
        new(TagDataType.Analog, LoggingMode.Periodic, 0.01, 0.0);

    [Fact]
    public async Task CrashWithOpenBlock_DataRecoveredFromJournal()
    {
        // Имитация аварийного завершения: экземпляр бросается без Dispose,
        // открытый блок в файлы потоков не попадает.
        var crashed = new FileArchiveStore(_root, durable: true);
        crashed.RegisterStream(1, Analog);

        for (int i = 0; i < 100; i++)
            crashed.Write(1, [new ArchivePoint(BaseTime + i * 1000L, (7000 + i) * 0.01, Quality.Good)]);

        crashed.FlushJournal();
        ReleaseWithoutClosingBlocks(crashed);

        // Файлы потоков ещё пусты — блок не закрывался.
        Assert.Empty(Directory.GetFiles(_root, "*.dat", SearchOption.AllDirectories));

        using var restarted = new FileArchiveStore(_root, durable: true);
        restarted.RegisterStream(1, Analog);
        restarted.RecoverFromWal();

        var buffer = new ArchivePoint[200];
        int count = await restarted.ReadRawAsync(1, 0, long.MaxValue, buffer, CancellationToken.None);

        Assert.Equal(100, count);
        Assert.Equal(70.0, buffer[0].Value, precision: 8);
        Assert.Equal(70.99, buffer[99].Value, precision: 8);
    }

    [Fact]
    public async Task Recovery_DoesNotDuplicatePointsAlreadyOnDisk()
    {
        var first = new FileArchiveStore(_root, durable: true);
        first.RegisterStream(1, Analog);

        for (int i = 0; i < 10; i++)
            first.Write(1, [new ArchivePoint(BaseTime + i * 1000L, (7000 + i) * 0.01, Quality.Good)]);

        // Часть данных дошла до файлов, журнал при этом отброшен.
        await first.FlushAsync();

        for (int i = 10; i < 20; i++)
            first.Write(1, [new ArchivePoint(BaseTime + i * 1000L, (7000 + i) * 0.01, Quality.Good)]);

        first.FlushJournal();
        ReleaseWithoutClosingBlocks(first);

        using var restarted = new FileArchiveStore(_root, durable: true);
        restarted.RegisterStream(1, Analog);
        restarted.RecoverFromWal();

        var buffer = new ArchivePoint[100];
        int count = await restarted.ReadRawAsync(1, 0, long.MaxValue, buffer, CancellationToken.None);

        // Двадцать точек, а не тридцать: то, что уже на диске, не отыгрывается.
        Assert.Equal(20, count);
        for (int i = 1; i < count; i++)
            Assert.True(buffer[i].TimestampUtcMs > buffer[i - 1].TimestampUtcMs);
    }

    [Fact]
    public void TornJournalTail_IsIgnored_EarlierRecordsSurvive()
    {
        var store = new FileArchiveStore(_root, durable: true);
        store.RegisterStream(1, Analog);

        for (int i = 0; i < 5; i++)
            store.Write(1, [new ArchivePoint(BaseTime + i * 1000L, (7000 + i) * 0.01, Quality.Good)]);

        store.FlushJournal();
        ReleaseWithoutClosingBlocks(store);

        // Портим последнюю запись — так выглядит обрыв питания посреди записи.
        string segment = Directory.GetFiles(Path.Combine(_root, "wal"), "*.wal").Single();
        byte[] data = File.ReadAllBytes(segment);
        data[^1] ^= 0xFF;
        File.WriteAllBytes(segment, data);

        var replayed = ArchiveWal.Replay(_root).ToList();

        // Четыре целых записи читаются, порванная отбрасывается.
        Assert.Equal(4, replayed.Count);
        Assert.All(replayed, r => Assert.Equal(1, r.StreamId));
    }

    [Fact]
    public async Task Journal_IsReclaimedAsBlocksReachDisk()
    {
        // Сегменты крошечные, чтобы ротация случилась на нескольких сотнях
        // записей, а не на 16 МБ.
        using var store = new FileArchiveStore(_root, TimeSpan.FromHours(1), durable: true);
        store.RegisterStream(1, Analog);

        // Набиваем журнал: блок ещё не закрыт, удалять нечего.
        for (int i = 0; i < 2000; i++)
            store.Write(1, [new ArchivePoint(BaseTime + i * 1000L, (7000 + i) * 0.01, Quality.Good)]);

        store.FlushJournal();
        long beforeCommit = store.JournalSizeBytes;
        Assert.True(beforeCommit > 0, "журнал пуст — записи до него не дошли");

        // Данные дошли до файлов потоков — журнал больше не нужен.
        await store.FlushAsync();

        Assert.Equal(0, store.JournalSizeBytes);
    }

    [Fact]
    public void Journal_DoesNotGrowWithoutBound()
    {
        // Сегменты по 2 КБ и блоки по 50 мс: за 20 000 записей журнал
        // прокрутится десятки раз, а данные будут уходить на диск почти сразу.
        // Так проверяется именно освобождение закрытых сегментов, а не
        // отбрасывание журнала целиком при остановке.
        using var store = new FileArchiveStore(_root, TimeSpan.FromMilliseconds(50),
            durable: true, walSegmentBytes: 2048);
        store.RegisterStream(1, Analog);

        const int records = 20_000;
        for (int i = 0; i < records; i++)
        {
            store.Write(1, [new ArchivePoint(BaseTime + i * 1000L, (7000 + i) * 0.01, Quality.Good)]);

            if (i % 500 == 0)
                store.FlushJournal();
        }

        store.FlushJournal();
        long final = store.JournalSizeBytes;

        // Без освобождения 20 000 записей по 29 байт дали бы 580 КБ и росли
        // бы дальше всё время работы службы. Журнал обязан удерживать только
        // незакоммиченный хвост.
        const long unreclaimedSize = records * 29L;
        Assert.True(final < unreclaimedSize / 4,
            $"журнал разросся до {final} байт из {unreclaimedSize} возможных — " +
            "сегменты не освобождаются");
    }

    [Fact]
    public void SecondWriter_IsRefused()
    {
        using var owner = new FileArchiveStore(_root, durable: true);

        var error = Assert.Throws<InvalidOperationException>(
            () => new FileArchiveStore(_root, durable: true));

        Assert.Contains("занят другим процессом", error.Message);
    }

    [Fact]
    public void LockIsReleasedOnDispose()
    {
        using (var first = new FileArchiveStore(_root, durable: true))
        {
            first.RegisterStream(1, Analog);
        }

        // После штатной остановки каталог снова доступен.
        using var second = new FileArchiveStore(_root, durable: true);
        second.RegisterStream(1, Analog);
    }

    [Fact]
    public async Task FlushAsync_DiscardsJournal()
    {
        using var store = new FileArchiveStore(_root, durable: true);
        store.RegisterStream(1, Analog);
        store.Write(1, [new ArchivePoint(BaseTime, 70.0, Quality.Good)]);
        store.FlushJournal();

        Assert.NotEmpty(ArchiveWal.Replay(_root));

        await store.FlushAsync();

        // Блок дошёл до файла — восстанавливать больше нечего.
        Assert.Empty(ArchiveWal.Replay(_root));
    }

    /// <summary>
    /// Отпускает файловые дескрипторы, не закрывая открытые блоки, — так
    /// выглядит падение процесса с точки зрения содержимого каталога.
    /// </summary>
    private static void ReleaseWithoutClosingBlocks(FileArchiveStore store)
    {
        var walField = typeof(FileArchiveStore)
            .GetField("_wal", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        (walField!.GetValue(store) as ArchiveWal)?.Dispose();

        var lockField = typeof(FileArchiveStore)
            .GetField("_directoryLock", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        (lockField!.GetValue(store) as ArchiveDirectoryLock)?.Dispose();
    }
}
