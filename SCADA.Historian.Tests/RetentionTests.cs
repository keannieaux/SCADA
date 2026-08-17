using SCADA.Core.Tags;

namespace SCADA.Historian.Tests;

/// <summary>
/// Ротация архива (docs/archive-format.md §15, ТЗ §8.6).
/// Проверяется не только то, что старое удаляется, но и то, что свежее
/// переживает проход при любых обстоятельствах: ошибка здесь означает
/// безвозвратную потерю данных заказчика.
/// </summary>
public class RetentionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    private FileArchiveStore? _store;

    public RetentionTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        _store?.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* временный каталог */ }
    }

    private static readonly ArchiveStreamConfig Analog =
        new(TagDataType.Analog, LoggingMode.Periodic, 1.0, 0.0);

    private static long DaysAgo(long nowMs, int days) => nowMs - days * 86_400_000L;

    private FileArchiveStore CreateStore(params int[] streamIds)
    {
        _store = new FileArchiveStore(_root, durable: false);
        foreach (int id in streamIds)
            _store.RegisterStream(id, Analog);
        return _store;
    }

    private static async Task WriteAtAsync(FileArchiveStore store, int streamId, long timestampMs)
    {
        store.Write(streamId, [new ArchivePoint(timestampMs, 42.0, Quality.Good)]);
        await store.FlushAsync();
    }

    [Fact]
    public async Task OldData_IsDeleted_FreshDataSurvives()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var store = CreateStore(1);

        await WriteAtAsync(store, 1, DaysAgo(now, 500));   // за сроком
        await WriteAtAsync(store, 1, DaysAgo(now, 10));    // свежее

        var report = store.ApplyRetention(new FixedRetentionPolicy(400, 30), now);

        Assert.True(report.AnythingDeleted);
        Assert.Equal(1, report.DeletedFiles);
        Assert.True(report.FreedBytes > 0);

        var buffer = new ArchivePoint[10];
        int count = await store.ReadRawAsync(1, 0, long.MaxValue, buffer, CancellationToken.None);
        Assert.Equal(1, count);
        Assert.Equal(DaysAgo(now, 10), buffer[0].TimestampUtcMs);
    }

    [Fact]
    public async Task NothingExpired_NothingDeleted()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var store = CreateStore(1);

        await WriteAtAsync(store, 1, DaysAgo(now, 10));

        var report = store.ApplyRetention(new FixedRetentionPolicy(400, 30), now);

        Assert.False(report.AnythingDeleted);
        Assert.Equal(0, report.DeletedFiles);
    }

    [Fact]
    public async Task EmptyMonthDirectory_IsRemoved()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var store = CreateStore(1);

        await WriteAtAsync(store, 1, DaysAgo(now, 500));

        var report = store.ApplyRetention(new FixedRetentionPolicy(400, 30), now);

        Assert.Equal(1, report.MonthsRemoved);
        Assert.Empty(Directory.GetDirectories(_root, "2*"));
    }

    [Fact]
    public async Task Floor_ProtectsDataFromForcedShrink()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var store = CreateStore(1);

        await WriteAtAsync(store, 1, DaysAgo(now, 40));

        // Нехватка места пытается ужать архив до 5 суток, но пол — 30.
        var report = store.ApplyRetention(
            new FixedRetentionPolicy(400, 30), now, forcedRetentionDays: 5);

        // 40 суток старше пола в 30 — файл удаляется, но по полу, а не по 5.
        Assert.True(report.HitFloor);
        Assert.Equal(1, report.DeletedFiles);

        // А данные моложе пола не трогаются даже при самом агрессивном ужатии.
        await WriteAtAsync(store, 1, DaysAgo(now, 20));
        var second = store.ApplyRetention(
            new FixedRetentionPolicy(400, 30), now, forcedRetentionDays: 1);

        Assert.Equal(0, second.DeletedFiles);
        Assert.True(second.HitFloor);
    }

    [Fact]
    public async Task DeletionFollowsDataTimestamps_NotDirectoryName()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var store = CreateStore(1);

        await WriteAtAsync(store, 1, DaysAgo(now, 10));

        // Каталог переименован в древний месяц — так выглядит сбитая система
        // при создании файла либо ручное вмешательство.
        string actual = Directory.GetDirectories(_root, "2*").Single();
        string forged = Path.Combine(_root, "2001-01");
        Directory.Move(actual, forged);

        var report = store.ApplyRetention(new FixedRetentionPolicy(400, 30), now);

        // Имя врёт, метки внутри данных — нет: файл остаётся (§15.4).
        Assert.Equal(0, report.DeletedFiles);
        Assert.Single(Directory.GetFiles(forged, "*.dat"));
    }

    [Fact]
    public async Task UnreadableFile_IsNotDeleted()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var store = CreateStore(1);

        await WriteAtAsync(store, 1, DaysAgo(now, 500));

        string file = Directory.GetFiles(_root, "*.dat", SearchOption.AllDirectories).Single();
        File.WriteAllBytes(file, [1, 2, 3]); // заголовок разрушен

        var report = store.ApplyRetention(new FixedRetentionPolicy(400, 30), now);

        // Возраст неизвестен — удалять нельзя: вдруг данные ещё вытащат.
        Assert.Equal(0, report.DeletedFiles);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task PerStreamRetention_IsHonoured()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var store = CreateStore(1, 2);

        await WriteAtAsync(store, 1, DaysAgo(now, 100));
        await WriteAtAsync(store, 2, DaysAgo(now, 100));

        // Поток 1 — вспомогательный (60 сут), поток 2 — аварийный (год).
        var report = store.ApplyRetention(new PerStreamPolicy(), now);

        Assert.Equal(1, report.DeletedFiles);

        var buffer = new ArchivePoint[4];
        Assert.Equal(0, await store.ReadRawAsync(1, 0, long.MaxValue, buffer, CancellationToken.None));
        Assert.Equal(1, await store.ReadRawAsync(2, 0, long.MaxValue, buffer, CancellationToken.None));
    }

    [Fact]
    public async Task SuspendedWriting_CountsLossesInsteadOfThrowing()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var store = CreateStore(1);

        store.SuspendWriting(true);
        store.Write(1, [new ArchivePoint(now, 1.0, Quality.Good),
                        new ArchivePoint(now + 1000, 2.0, Quality.Good)]);

        // Исключение здесь остановило бы конвейер, а с ним сбор данных по всем
        // остальным тегам. Потери считаются и уходят в диагностику (§22).
        Assert.Equal(2, store.DroppedNoSpaceCount);
        Assert.True(store.WritingSuspended);

        var buffer = new ArchivePoint[4];
        Assert.Equal(0, await store.ReadRawAsync(1, 0, long.MaxValue, buffer, CancellationToken.None));

        // Место освободили — запись возобновляется, счётчик эпизода обнуляется.
        store.SuspendWriting(false);
        Assert.Equal(0, store.DroppedNoSpaceCount);

        await WriteAtAsync(store, 1, now + 2000);
        Assert.Equal(1, await store.ReadRawAsync(1, 0, long.MaxValue, buffer, CancellationToken.None));
    }

    [Fact]
    public void Policy_RejectsFloorAboveRetention()
    {
        // Пол выше основной глубины означал бы, что при нехватке места система
        // не может освободить ничего — конфигурация бессмысленна.
        var error = Assert.Throws<ArgumentException>(() => new FixedRetentionPolicy(30, 400));
        Assert.Contains("не может превышать", error.Message);
    }

    /// <summary>Разные сроки по потокам — то, ради чего заведён IRetentionPolicy.</summary>
    private sealed class PerStreamPolicy : IRetentionPolicy
    {
        public int MinRetentionDays => 30;

        public int GetRetentionDays(int streamId) => streamId == 1 ? 60 : 365;
    }
}
