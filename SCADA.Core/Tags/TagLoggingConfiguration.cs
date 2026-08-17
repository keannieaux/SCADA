namespace SCADA.Core.Tags;

/// <summary>
/// Правило попадания значений тега в архив (docs/archive-format.md §6).
/// </summary>
public class TagLoggingConfiguration
{
    /// <summary>Писать при изменении значения либо качества.</summary>
    public bool LogOnChange { get; set; }

    /// <summary>Писать через фиксированный интервал.</summary>
    public TimeSpan? Interval { get; set; }

    /// <summary>
    /// Писать в заданные моменты местного времени объекта: смена, сутки, месяц.
    /// Для отчётных срезов и показаний счётчиков.
    /// </summary>
    public IReadOnlyList<LogScheduleEntry> Schedule { get; set; } = Array.Empty<LogScheduleEntry>();

    /// <summary>
    /// Ближайший момент записи после <paramref name="now"/>.
    /// </summary>
    /// <param name="siteTimeZone">
    /// Часовой пояс объекта. Расписание задаётся в местном времени: «08:00» —
    /// это начало смены на объекте, а не 08:00 UTC. Пояс приходит параметром,
    /// а не берётся из ОС, потому что сервер может стоять не на объекте
    /// (ТЗ §5.1), а часовой пояс промышленного ПК нередко остаётся тем, что
    /// был в клонированном образе.
    /// </param>
    /// <returns><see cref="DateTimeOffset.MaxValue"/>, если правило не задаёт моментов.</returns>
    public DateTimeOffset GetNextLoggingTime(
        DateTimeOffset now, DateTimeOffset lastLoggedTime, TimeZoneInfo siteTimeZone)
    {
        ArgumentNullException.ThrowIfNull(siteTimeZone);

        DateTimeOffset next = DateTimeOffset.MaxValue;

        if (Interval is { } interval)
        {
            var baseTime = lastLoggedTime == default ? now : lastLoggedTime;
            next = baseTime + interval;
        }

        // Обход циклом, а не LINQ: метод вызывается на каждый записанный
        // отсчёт каждого логируемого тега, а замыкание и энумератор LINQ
        // здесь запрещены (ТЗ §15.2).
        for (int i = 0; i < Schedule.Count; i++)
        {
            var candidate = Schedule[i].GetNextOccurrence(now, siteTimeZone);
            if (candidate < next)
                next = candidate;
        }

        return next;
    }
}

/// <summary>
/// Момент расписания в местном времени объекта. Незаданные поля означают
/// «любой»: одно только <see cref="Time"/> даёт ежедневную запись.
/// </summary>
public class LogScheduleEntry
{
    /// <summary>Время суток по местному времени объекта.</summary>
    public required TimeOnly Time { get; set; }

    public DayOfWeek? DayOfWeek { get; set; }
    public int? DayOfMonth { get; set; }
    public int? Month { get; set; }

    /// <summary>Максимум суток перебора: год плюс запас на високосный.</summary>
    private const int SearchHorizonDays = 366;

    /// <summary>
    /// Ближайшее наступление момента строго после <paramref name="from"/>.
    /// </summary>
    public DateTimeOffset GetNextOccurrence(DateTimeOffset from, TimeZoneInfo siteTimeZone)
    {
        ArgumentNullException.ThrowIfNull(siteTimeZone);

        // Сравнение и перебор идут в местном времени объекта: «первое число
        // месяца» и «понедельник» определены календарём объекта, а не UTC.
        var localFrom = TimeZoneInfo.ConvertTime(from, siteTimeZone);
        var day = localFrom.Date;

        for (int i = 0; i <= SearchHorizonDays; i++, day = day.AddDays(1))
        {
            if (!Matches(day))
                continue;

            if (!TryResolveLocal(day, siteTimeZone, out var occurrence))
                continue;

            if (occurrence > from)
                return occurrence;
        }

        return DateTimeOffset.MaxValue;
    }

    private bool Matches(DateTime day)
    {
        if (DayOfWeek.HasValue && day.DayOfWeek != DayOfWeek.Value)
            return false;

        if (DayOfMonth.HasValue && day.Day != DayOfMonth.Value)
            return false;

        if (Month.HasValue && day.Month != Month.Value)
            return false;

        return true;
    }

    /// <summary>
    /// Превращает местные дату и время в момент UTC с учётом переходов
    /// на летнее время.
    /// </summary>
    /// <remarks>
    /// Два особых случая, и оба обязаны быть решены явно, иначе сменный отчёт
    /// раз в год либо потеряется, либо задвоится:
    /// <list type="bullet">
    /// <item>час, пропущенный при переходе вперёд, местного времени не имеет —
    /// запись сдвигается на ближайший существующий момент;</item>
    /// <item>час, повторённый при переходе назад, наступает дважды — берётся
    /// первое наступление, чтобы запись случилась один раз.</item>
    /// </list>
    /// </remarks>
    private bool TryResolveLocal(DateTime day, TimeZoneInfo siteTimeZone, out DateTimeOffset result)
    {
        var local = DateTime.SpecifyKind(day.Date + Time.ToTimeSpan(), DateTimeKind.Unspecified);

        if (siteTimeZone.IsInvalidTime(local))
        {
            // Пропущенный час: сдвигаемся вперёд по минуте до существующего
            // момента. Переход длится час, поэтому перебор ограничен.
            for (int minute = 1; minute <= 120; minute++)
            {
                var shifted = local.AddMinutes(minute);
                if (!siteTimeZone.IsInvalidTime(shifted))
                {
                    result = ToOffset(shifted, siteTimeZone);
                    return true;
                }
            }

            result = default;
            return false;
        }

        result = ToOffset(local, siteTimeZone);
        return true;
    }

    private static DateTimeOffset ToOffset(DateTime local, TimeZoneInfo siteTimeZone)
    {
        // При неоднозначном времени GetUtcOffset возвращает смещение
        // стандартного времени — то есть ПОЗДНЕЕ наступление. Нам нужно
        // первое, поэтому берём летнее смещение явно.
        if (siteTimeZone.IsAmbiguousTime(local))
        {
            var offsets = siteTimeZone.GetAmbiguousTimeOffsets(local);
            var earliest = offsets[0];
            for (int i = 1; i < offsets.Length; i++)
            {
                if (offsets[i] > earliest)
                    earliest = offsets[i];
            }

            return new DateTimeOffset(local, earliest);
        }

        return new DateTimeOffset(local, siteTimeZone.GetUtcOffset(local));
    }
}
