using SCADA.Core.Tags;
using SCADA.Runtime.Configuration;

namespace SCADA.Runtime.Historian;

/// <summary>Оценка объёма архива для одного тега.</summary>
public readonly record struct TagVolumeEstimate(
    TagId Id,
    string Name,
    LoggingMode Mode,
    double PointsPerDay,
    double BytesPerDay)
{
    public double BytesPerYear => BytesPerDay * 365;
}

/// <summary>Оценка объёма архива для проекта.</summary>
public readonly record struct ArchiveVolumeEstimate(
    int ArchivedTags,
    int TotalTags,
    double BytesPerDay,
    double BytesAtRetention,
    int RetentionDays,
    int UnquantizedTags,
    IReadOnlyList<TagVolumeEstimate> TopConsumers)
{
    public double GigabytesAtRetention => BytesAtRetention / 1024 / 1024 / 1024;

    public double MegabytesPerDay => BytesPerDay / 1024 / 1024;
}

/// <summary>
/// Расчёт ожидаемого объёма архива по конфигурации проекта (ТЗ §4.3).
/// </summary>
/// <remarks>
/// Даёт два эффекта. Первый: требование к оборудованию выдвигается заказчику
/// на этапе проектирования, а не при заполнении диска. Второй: критерий
/// приёмки M4 (ТЗ §17) сравнивает фактический рост с этим расчётом, а не с
/// угаданной константой — такой критерий проверяем при любом исходе и заодно
/// проверяет сам калькулятор.
/// </remarks>
public static class ArchiveVolumeCalculator
{
    // Константы получены замером на модельных сигналах и удерживаются в
    // соответствии тестом CodecCalibrationTests: при изменении кодеков он
    // падает, не давая оценке объёма тихо разойтись с реальностью.

    /// <summary>
    /// Байт на отсчёт аналогового тега на решётке: дрожание в младший разряд
    /// АЦП, 20 % изменившихся отсчётов — форма установившегося режима.
    /// </summary>
    private const double AnalogBytesPerPoint = 0.55;

    /// <summary>
    /// Байт на отсчёт вычисляемого тега без решётки. Соседние значения
    /// различаются почти всей мантиссой, у XOR не остаётся ни ведущих, ни
    /// хвостовых нулей, и кодек **раздувает** данные: 8,4 байта против 8 байт
    /// несжатого значения. Лечится объявлением <c>Precision</c> (§7.3).
    /// </summary>
    private const double UnquantizedFloatBytesPerPoint = 8.4;

    /// <summary>
    /// Байт на отсчёт настоящего float32 из ПЛК, расширенного до double:
    /// 29 младших бит мантиссы нулевые, и XOR это использует.
    /// </summary>
    private const double Float32BytesPerPoint = 1.4;

    /// <summary>Байт на переключение дискретного тега.</summary>
    private const double DiscreteBytesPerPoint = 0.5;

    /// <summary>Заголовок блока с полями масштаба плюс CRC (§8.3).</summary>
    private const int BlockOverheadBytes = 76;

    /// <summary>Вместимость блока по умолчанию, если не задана явно (§8.6).</summary>
    private const int DefaultBlockPoints = 4096;

    /// <summary>Часовой таймаут закрытия блока (§8.6) в блоках на сутки.</summary>
    private const int MaxBlocksPerDayByTimeout = 24;

    /// <summary>
    /// Сколько раз в сутки меняется дискретный тег, если иного не известно.
    /// Оценка, а не измерение: частота переключений зависит от техпроцесса.
    /// Занижение здесь безопаснее завышения — дискреты дают доли процента
    /// объёма, а завышенная оценка спровоцировала бы лишние требования к диску.
    /// </summary>
    public const double DefaultDiscreteChangesPerDay = 100;

    /// <param name="blockPoints">
    /// Вместимость блока: влияет и на накладные расходы заголовков, и на пик
    /// памяти. Ноль — взять значение по умолчанию.
    /// </param>
    public static ArchiveVolumeEstimate Estimate(
        ProjectConfiguration config, int retentionDays,
        double defaultIntervalSeconds = 1.0,
        double discreteChangesPerDay = DefaultDiscreteChangesPerDay,
        int blockPoints = 0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(retentionDays, 1);

        int effectiveBlockPoints = blockPoints > 0 ? blockPoints : DefaultBlockPoints;

        var perTag = new List<TagVolumeEstimate>();
        double totalBytesPerDay = 0;
        int unquantized = 0;

        foreach (var tag in config.Tags)
        {
            if (!tag.IsArchived)
                continue;

            var estimate = EstimateTag(tag, defaultIntervalSeconds, discreteChangesPerDay,
                effectiveBlockPoints);
            perTag.Add(estimate);
            totalBytesPerDay += estimate.BytesPerDay;

            if (LacksLattice(tag))
                unquantized++;
        }

        perTag.Sort((a, b) => b.BytesPerDay.CompareTo(a.BytesPerDay));

        return new ArchiveVolumeEstimate(
            ArchivedTags: perTag.Count,
            TotalTags: config.Tags.Count,
            BytesPerDay: totalBytesPerDay,
            BytesAtRetention: totalBytesPerDay * retentionDays,
            RetentionDays: retentionDays,
            UnquantizedTags: unquantized,
            TopConsumers: perTag.Take(10).ToArray());
    }

    private static TagVolumeEstimate EstimateTag(
        TagDefinition tag, double defaultIntervalSeconds, double discreteChangesPerDay,
        int blockPoints)
    {
        var mode = LoggingModeHelper.Infer(tag.Logging);
        double pointsPerDay = EstimatePointsPerDay(tag, mode, defaultIntervalSeconds, discreteChangesPerDay);
        double bytesPerPoint = EstimateBytesPerPoint(tag);

        // Блок закрывается по первому из двух событий: 4096 отсчётов либо час
        // от первого отсчёта в блоке (§8.6). Что сработает раньше, то и даёт
        // БОЛЬШЕ блоков, поэтому максимум, а не минимум.
        //
        // Для медленных тегов таймаут доминирует, и накладные расходы
        // становятся основной статьёй: дискрет со 100 переключениями в сутки
        // укладывается в 50 байт полезной нагрузки при 24 заголовках блоков.
        // Наивная модель «отсчёты × байты» занизила бы его в тридцать раз.
        double blocksPerDay = Math.Max(
            MaxBlocksPerDayByTimeout,
            Math.Ceiling(pointsPerDay / blockPoints));

        // Блоков не может быть больше, чем отсчётов: совсем редкий тег
        // (несколько записей в сутки) даёт по блоку на запись, не больше.
        blocksPerDay = Math.Min(blocksPerDay, pointsPerDay);

        double bytesPerDay = pointsPerDay * bytesPerPoint + blocksPerDay * BlockOverheadBytes;

        return new TagVolumeEstimate(tag.Id, tag.Name, mode, pointsPerDay, bytesPerDay);
    }

    private static double EstimatePointsPerDay(
        TagDefinition tag, LoggingMode mode,
        double defaultIntervalSeconds, double discreteChangesPerDay)
    {
        const double SecondsPerDay = 86_400;

        switch (mode)
        {
            case LoggingMode.Periodic:
                double intervalSeconds = tag.Logging?.Interval?.TotalSeconds ?? defaultIntervalSeconds;
                return intervalSeconds <= 0 ? 0 : SecondsPerDay / intervalSeconds;

            case LoggingMode.OnChange:
                // Объём зависит от поведения процесса, а не от конфигурации —
                // это и есть причина, по которой Periodic выбран основным
                // режимом для аналоговых (ТЗ §8.3).
                return discreteChangesPerDay;

            case LoggingMode.Schedule:
                return CountScheduleOccurrencesPerDay(tag.Logging);

            default:
                return 0;
        }
    }

    private static double CountScheduleOccurrencesPerDay(TagLoggingConfiguration? logging)
    {
        if (logging is null || logging.Schedule.Count == 0)
            return 0;

        double perDay = 0;
        foreach (var entry in logging.Schedule)
        {
            // Ежедневная запись даёт 1 в сутки; недельная — 1/7; месячная — 1/30.
            if (entry.DayOfMonth.HasValue || entry.Month.HasValue)
                perDay += 1.0 / 30;
            else if (entry.DayOfWeek.HasValue)
                perDay += 1.0 / 7;
            else
                perDay += 1;
        }

        return perDay;
    }

    /// <summary>
    /// Аналоговый тег, значения которого не лягут на решётку: ни масштаба из
    /// регистра, ни объявленной точности. Самый дорогой случай в архиве.
    /// </summary>
    private static bool LacksLattice(TagDefinition tag)
        => tag.DataType == TagDataType.Analog
           && !tag.Precision.HasValue
           && tag.ScaleFactor is 1.0 or 0.0;

    private static double EstimateBytesPerPoint(TagDefinition tag)
    {
        if (tag.DataType == TagDataType.Discrete)
            return DiscreteBytesPerPoint;

        // Объявленная точность создаёт решётку искусственно (§7.3) и
        // возвращает тег на дешёвый кодек независимо от источника.
        if (tag.Precision.HasValue)
            return AnalogBytesPerPoint;

        // Решётка есть, когда значение получено из регистра масштабированием.
        if (tag.ScaleFactor != 1.0 && tag.ScaleFactor != 0.0)
            return AnalogBytesPerPoint;

        // Решётки нет. Худший случай — вычисляемый тег, где XOR раздувает
        // данные; оцениваем по нему, потому что отличить его от честного
        // float32 по конфигурации нельзя, а занижать оценку опаснее, чем
        // завысить: заниженная оценка обнаружится заполненным диском.
        return UnquantizedFloatBytesPerPoint;
    }

    /// <summary>
    /// Человекочитаемый отчёт для сборки пакета и для разговора с заказчиком
    /// о требованиях к оборудованию.
    /// </summary>
    public static string Format(ArchiveVolumeEstimate estimate)
    {
        var lines = new List<string>
        {
            $"Архив: {estimate.ArchivedTags} логируемых тегов из {estimate.TotalTags}",
            $"Прирост: {estimate.MegabytesPerDay:F1} МБ в сутки",
            $"За срок хранения ({estimate.RetentionDays} сут): {estimate.GigabytesAtRetention:F1} ГБ",
            $"Требование к диску с трёхкратным запасом (ТЗ §4.3): " +
            $"{estimate.GigabytesAtRetention * 3:F0} ГБ"
        };

        if (estimate.TopConsumers.Count > 0)
        {
            lines.Add("Наибольший вклад:");
            foreach (var tag in estimate.TopConsumers.Take(5))
            {
                lines.Add(
                    $"  {tag.Name} ({tag.Mode}): {tag.PointsPerDay:F0} отсч/сут, " +
                    $"{tag.BytesPerYear / 1024 / 1024:F1} МБ/год");
            }
        }

        if (estimate.UnquantizedTags > 0)
        {
            lines.Add(
                $"ВНИМАНИЕ: {estimate.UnquantizedTags} аналоговых тегов без целочисленной " +
                "решётки и без объявленной точности. Такие значения сжатию не поддаются " +
                "и занимают в 15 раз больше остальных. Задайте Precision — это " +
                "единственная настройка, дающая кратную экономию.");
        }

        return string.Join(Environment.NewLine, lines);
    }
}
