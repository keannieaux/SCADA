using SCADA.Core.Tags;
using SCADA.Historian;

namespace SCADA.Runtime.Historian;

/// <summary>
/// Диагностика архива (docs/archive-format.md §22, ТЗ §7.5).
/// Публикует состояние подсистемы архивирования теми же средствами, что
/// диагностика каналов: на объекте нет редактора (ТЗ §5.4.2), и вопросы
/// «за какой период есть данные» и «пишется ли архив сейчас» должны иметь
/// ответ в runtime, а не в журналах.
/// </summary>
public sealed class ArchiveDiagnostics
{
    /// <summary>Имя псевдоустройства архива. Префикс '@' зарезервирован за системными.</summary>
    public const string DeviceName = "@Archive";

    public const string PointsPerSecondSuffix = "PointsPerSecond";
    public const string DroppedNonMonotonicSuffix = "DroppedNonMonotonic";
    public const string DroppedNoSpaceSuffix = "DroppedNoSpace";
    public const string WalLagMsSuffix = "WalLagMs";
    public const string LastFlushUtcSuffix = "LastFlushUtc";
    public const string SizeMbSuffix = "SizeMb";
    public const string FreeDiskMbSuffix = "FreeDiskMb";
    public const string DaysRemainingSuffix = "DaysRemaining";
    public const string GrowthMbPerDaySuffix = "GrowthMbPerDay";
    public const string OldestDataUtcSuffix = "OldestDataUtc";
    public const string CrcErrorsSuffix = "CrcErrors";
    public const string StateSuffix = "State";

    /// <summary>Состояние архива для тега <c>@Archive.State</c>.</summary>
    public enum ArchiveState
    {
        Normal = 0,
        LowDiskSpace = 1,
        WritingStopped = 2
    }

    // Состав и порядок метрик фиксированы и append-only: от порядка зависят
    // назначаемые TagId, как и у диагностики каналов.
    private static readonly (string Suffix, TagDataType DataType)[] Metrics =
    [
        (PointsPerSecondSuffix, TagDataType.Analog),
        (DroppedNonMonotonicSuffix, TagDataType.Analog),
        (DroppedNoSpaceSuffix, TagDataType.Analog),
        (WalLagMsSuffix, TagDataType.Analog),
        (LastFlushUtcSuffix, TagDataType.Analog),
        (SizeMbSuffix, TagDataType.Analog),
        (FreeDiskMbSuffix, TagDataType.Analog),
        (DaysRemainingSuffix, TagDataType.Analog),
        (GrowthMbPerDaySuffix, TagDataType.Analog),
        (OldestDataUtcSuffix, TagDataType.Analog),
        (CrcErrorsSuffix, TagDataType.Analog),
        (StateSuffix, TagDataType.Analog)
    ];

    public static IReadOnlyList<(string Suffix, TagDataType DataType)> MetricDefinitions => Metrics;

    public static string TagName(string suffix) => $"{DeviceName}.{suffix}";

    private readonly string _archiveRoot;
    private readonly Dictionary<string, TagId> _tagIds = new(StringComparer.Ordinal);

    private long _lastSampleUtcMs;
    private long _lastWrittenPoints;
    private long _lastSizeBytes;
    private double _growthMbPerDay;
    private double _pointsPerSecond;

    /// <summary>Отсчёты, потерянные из-за нехватки места (ТЗ §8.9).</summary>
    public long DroppedNoSpaceCount { get; set; }

    /// <summary>Ошибки контрольных сумм при чтении архива.</summary>
    public long CrcErrorCount { get; set; }

    /// <summary>Текущее состояние подсистемы.</summary>
    public ArchiveState State { get; set; } = ArchiveState.Normal;

    public ArchiveDiagnostics(string archiveRoot, ProjectConfiguration config)
    {
        _archiveRoot = archiveRoot;

        foreach (var tag in config.Tags)
        {
            if (tag.Name.StartsWith(DeviceName + ".", StringComparison.Ordinal))
                _tagIds[tag.Name] = tag.Id;
        }
    }

    /// <summary>
    /// Пересчитывает метрики и пишет их в TagTable. Вызывается по таймеру,
    /// не чаще раза в секунду: обход каталога архива не бесплатен.
    /// </summary>
    public void Flush(ITagTable tagTable, ArchivePipeline pipeline, IArchiveStore store, long nowUtcMs)
    {
        long sizeBytes = MeasureArchiveSize();
        UpdateRates(pipeline.WrittenPointsCount, sizeBytes, nowUtcMs);

        double freeDiskMb = MeasureFreeDiskMb();
        double sizeMb = sizeBytes / 1024.0 / 1024.0;

        Write(tagTable, PointsPerSecondSuffix, _pointsPerSecond, nowUtcMs);
        Write(tagTable, DroppedNonMonotonicSuffix, pipeline.DroppedNonMonotonicCount, nowUtcMs);
        Write(tagTable, DroppedNoSpaceSuffix, DroppedNoSpaceCount, nowUtcMs);
        Write(tagTable, SizeMbSuffix, sizeMb, nowUtcMs);
        Write(tagTable, FreeDiskMbSuffix, freeDiskMb, nowUtcMs);
        Write(tagTable, GrowthMbPerDaySuffix, _growthMbPerDay, nowUtcMs);
        Write(tagTable, CrcErrorsSuffix, CrcErrorCount, nowUtcMs);
        Write(tagTable, StateSuffix, (double)State, nowUtcMs);
        Write(tagTable, OldestDataUtcSuffix, FindOldestDataUtcMs(), nowUtcMs);

        // «На сколько суток хватит места» — производная от измеренной скорости
        // роста, а не настройка: порог тревоги задаётся в мегабайтах
        // (ТЗ §8.9), а это число нужно человеку, а не алгоритму.
        double daysRemaining = _growthMbPerDay > 0.01 ? freeDiskMb / _growthMbPerDay : 0.0;
        Write(tagTable, DaysRemainingSuffix, daysRemaining, nowUtcMs);

        if (store is FileArchiveStore fileStore)
        {
            Write(tagTable, WalLagMsSuffix, fileStore.WalLagMs, nowUtcMs);
            Write(tagTable, LastFlushUtcSuffix, fileStore.LastFlushUtcMs, nowUtcMs);
        }
    }

    private void UpdateRates(long writtenPoints, long sizeBytes, long nowUtcMs)
    {
        if (_lastSampleUtcMs == 0)
        {
            _lastSampleUtcMs = nowUtcMs;
            _lastWrittenPoints = writtenPoints;
            _lastSizeBytes = sizeBytes;
            return;
        }

        long elapsedMs = nowUtcMs - _lastSampleUtcMs;
        if (elapsedMs < 1000)
            return;

        _pointsPerSecond = (writtenPoints - _lastWrittenPoints) * 1000.0 / elapsedMs;

        double grownMb = (sizeBytes - _lastSizeBytes) / 1024.0 / 1024.0;
        double elapsedDays = elapsedMs / 86_400_000.0;
        if (elapsedDays > 0)
        {
            // Экспоненциальное сглаживание: мгновенная скорость скачет от
            // закрытия блоков, а показатель нужен для оценки «хватит на N суток».
            double instant = grownMb / elapsedDays;
            _growthMbPerDay = _growthMbPerDay <= 0 ? instant : _growthMbPerDay * 0.9 + instant * 0.1;
        }

        _lastSampleUtcMs = nowUtcMs;
        _lastWrittenPoints = writtenPoints;
        _lastSizeBytes = sizeBytes;
    }

    private long MeasureArchiveSize()
    {
        if (!Directory.Exists(_archiveRoot))
            return 0;

        long total = 0;
        try
        {
            foreach (string file in Directory.EnumerateFiles(_archiveRoot, "*", SearchOption.AllDirectories))
                total += new FileInfo(file).Length;
        }
        catch (IOException)
        {
            // каталог мог измениться во время обхода — покажем что успели
        }

        return total;
    }

    /// <summary>
    /// Свободно на томе архива, МБ. Публичный, потому что тем же числом
    /// оперирует надзиратель за местом (ТЗ §8.9) — измерять его дважды
    /// разными способами значит однажды получить разные ответы.
    /// </summary>
    public double MeasureFreeDiskMb()
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(_archiveRoot));
            if (string.IsNullOrEmpty(root))
                return 0;

            return new DriveInfo(root).AvailableFreeSpace / 1024.0 / 1024.0;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Фактическая глубина архива: самый ранний каталог месяца. Отвечает на
    /// вопрос «за какой период у меня вообще есть данные», на который сейчас
    /// нет другого способа ответить.
    /// </summary>
    private double FindOldestDataUtcMs()
    {
        if (!Directory.Exists(_archiveRoot))
            return 0;

        DateTimeOffset? oldest = null;
        foreach (string directory in Directory.EnumerateDirectories(_archiveRoot))
        {
            string name = Path.GetFileName(directory);
            if (!DateTimeOffset.TryParseExact(name + "-01T00:00:00Z", "yyyy-MM-ddTHH:mm:ssZ",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal
                    | System.Globalization.DateTimeStyles.AdjustToUniversal, out var month))
            {
                continue;
            }

            if (oldest is null || month < oldest)
                oldest = month;
        }

        return oldest?.ToUnixTimeMilliseconds() ?? 0;
    }

    private void Write(ITagTable tagTable, string suffix, double value, long timestampMs)
    {
        if (_tagIds.TryGetValue(TagName(suffix), out var id))
            tagTable.Write(id, new TagValue(value, timestampMs, Quality.Good));
    }
}
