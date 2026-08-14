namespace SCADA.Runtime.Historian;

/// <summary>
/// Настройки подсистемы архивирования (docs/archive-format.md §21).
/// Это параметры проекта, а не константы кода: живут в runtime.json папки
/// проекта (ТЗ §14.6), у каждого объекта свои.
/// </summary>
public sealed class ArchiveOptions
{
    /// <summary>
    /// Каталог архива. Относительный путь разрешается от папки проекта —
    /// всё изменяемое состояние живёт под ней (ТЗ §14.6), чтобы резервная
    /// копия объекта была копированием одной папки.
    /// </summary>
    public string Root { get; set; } = "archive";

    /// <summary>Включён ли архив вообще. Выключение — режим отладки.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Часовой пояс объекта в формате IANA (<c>Asia/Yekaterinburg</c>) или
    /// Windows. Пустая строка — пояс операционной системы.
    /// </summary>
    /// <remarks>
    /// Влияет только на режим Schedule и границы отчётных периодов; хранение
    /// всегда в UTC. Задаётся явно, потому что сервер может стоять не на
    /// объекте (ТЗ §5.1), а на промышленных ПК, развёрнутых клонированием
    /// образа, пояс нередко остаётся чужим. При неявной настройке сменные
    /// итоги молча уезжают на несколько часов, и расхождение замечают при
    /// сверке с бумажным журналом через месяцы.
    /// </remarks>
    public string SiteTimeZone { get; set; } = "";

    /// <summary>
    /// Разрешает <see cref="SiteTimeZone"/> в объект. Неизвестный
    /// идентификатор — ошибка конфигурации, а не повод молча взять пояс ОС:
    /// молчаливая подмена и есть тот случай, который потом ищут месяцами.
    /// </summary>
    public TimeZoneInfo ResolveTimeZone()
    {
        if (string.IsNullOrWhiteSpace(SiteTimeZone))
            return TimeZoneInfo.Local;

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(SiteTimeZone);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new InvalidOperationException(
                $"Неизвестный часовой пояс объекта \"{SiteTimeZone}\" " +
                "(Runtime:Archive:SiteTimeZone). Ожидается идентификатор IANA " +
                "вида \"Asia/Yekaterinburg\" либо пустая строка для пояса системы.", ex);
        }
    }

    /// <summary>
    /// Период тика конвейера. Не реже самого частого опроса устройств,
    /// иначе теряются короткие импульсы дискретных тегов (§3).
    /// </summary>
    public int TickIntervalMs { get; set; } = 100;

    /// <summary>Интервал Periodic по умолчанию, если не задан на теге (§6).</summary>
    public int DefaultIntervalMs { get; set; } = 1000;

    /// <summary>
    /// Периодичность сброса журнала на диск. Потолок задан ТЗ §4.2:
    /// потеря при аварийном завершении не более 10 секунд.
    /// </summary>
    public int FlushIntervalMs { get; set; } = 10_000;

    /// <summary>Предельный пролёт блока (§8.6).</summary>
    public int BlockTimeoutMinutes { get; set; } = 60;

    /// <summary>
    /// Бюджет памяти под открытые блоки, МБ (§8.6).
    /// </summary>
    /// <remarks>
    /// Открытый блок держит отсчёты в памяти до закрытия: 24 байта на отсчёт
    /// на каждый логируемый тег. Задавать вместимость блока напрямую значит
    /// требовать от интегратора считать `теги × вместимость × 24` — ровно то,
    /// чего мы не стали требовать от него по диску. Поэтому задаётся бюджет,
    /// а вместимость выводится из него и числа логируемых тегов.
    /// </remarks>
    public int MaxOpenBlockMemoryMb { get; set; } = 64;

    /// <summary>
    /// Вместимость блока в отсчётах. Ноль — вывести из
    /// <see cref="MaxOpenBlockMemoryMb"/>; ненулевое значение отменяет расчёт.
    /// </summary>
    public int BlockPoints { get; set; }

    /// <summary>Байт на отсчёт в открытом блоке: метка, значение, качество.</summary>
    private const int OpenBlockBytesPerPoint = 24;

    /// <summary>Целевая вместимость блока (§8.6): компромисс сжатия и памяти.</summary>
    private const int PreferredBlockPoints = 4096;

    /// <summary>
    /// Нижняя граница вместимости. Ниже неё заголовок блока в 76 байт
    /// начинает стоить сопоставимо с самими данными, и экономия памяти
    /// оплачивается уже заметным ростом архива.
    /// </summary>
    private const int MinBlockPoints = 256;

    /// <summary>
    /// Вместимость блока для заданного числа логируемых тегов.
    /// </summary>
    public int ResolveBlockPoints(int archivedTagCount)
    {
        if (BlockPoints > 0)
            return BlockPoints;

        if (archivedTagCount <= 0)
            return PreferredBlockPoints;

        long budgetBytes = MaxOpenBlockMemoryMb * 1024L * 1024;
        long perStream = budgetBytes / (archivedTagCount * (long)OpenBlockBytesPerPoint);

        return (int)Math.Clamp(perStream, MinBlockPoints, PreferredBlockPoints);
    }

    /// <summary>Ожидаемый пик памяти под открытые блоки, МБ.</summary>
    public double EstimateOpenBlockMemoryMb(int archivedTagCount)
        => (double)ResolveBlockPoints(archivedTagCount) * archivedTagCount
           * OpenBlockBytesPerPoint / 1024 / 1024;

    /// <summary>
    /// Размер сегмента журнала, МБ (§21). Задаёт гранулярность освобождения:
    /// сегмент удаляется целиком, когда все его данные дошли до файлов
    /// потоков. Слишком крупный сегмент удерживает журнал дольше нужного.
    /// </summary>
    public int WalSegmentSizeMb { get; set; } = 16;

    /// <summary>Периодичность обновления диагностических тегов архива (§22).</summary>
    public int DiagnosticsIntervalMs { get; set; } = 5000;

    /// <summary>Глубина хранения (§15). Ротация пока не реализована.</summary>
    public int RetentionDays { get; set; } = 400;

    /// <summary>
    /// Пол досрочного удаления при нехватке места (ТЗ §8.9).
    /// Ниже него данные не удаляются никогда.
    /// </summary>
    public int MinRetentionDays { get; set; } = 30;

    /// <summary>Порог аларма о нехватке места, МБ (ТЗ §8.9).</summary>
    public int MinFreeDiskMb { get; set; } = 5000;

    /// <summary>
    /// Поведение при исчерпании места (ТЗ §8.9). По умолчанию удалять старое:
    /// инцидент, который будут разбирать, происходит сейчас, и остановка
    /// записи гарантирует отсутствие записей именно про него. Ставить
    /// StopWriting стоит там, где срок хранения закреплён договором.
    /// </summary>
    public OnDiskFull OnDiskFull { get; set; } = OnDiskFull.DeleteOldest;

    /// <summary>Абсолютный путь к каталогу архива относительно папки проекта.</summary>
    public string ResolveRoot(string projectDirectory)
        => Path.IsPathRooted(Root) ? Root : Path.Combine(projectDirectory, Root);
}
