namespace SCADA.Core.Tags;

public class TagLoggingConfiguration
{
    public bool LogOnChange { get; set; }
    public TimeSpan? Interval { get; set; }
    public IReadOnlyList<LogScheduleEntry> Schedule { get; set; } = Array.Empty<LogScheduleEntry>();

    public DateTimeOffset GetNextLoggingTime(DateTimeOffset now, DateTimeOffset lastLoggedTime)
    {
        DateTimeOffset? intervalTime = Interval.HasValue
            ? (lastLoggedTime == default ? now : lastLoggedTime) + Interval.Value
            : null;

        DateTimeOffset? scheduleTime = Schedule.Count > 0
            ? Schedule.Min(s => s.GetNextOccurrence(now))
            : null;

        if (intervalTime.HasValue && scheduleTime.HasValue)
            return intervalTime.Value < scheduleTime.Value
                ? intervalTime.Value
                : scheduleTime.Value;

        return intervalTime ?? scheduleTime ?? DateTimeOffset.MaxValue;
    }
}

public class LogScheduleEntry
{
    public required TimeOnly Time { get; set; }
    public DayOfWeek? DayOfWeek { get; set; }
    public int? DayOfMonth { get; set; }
    public int? Month { get; set; }

    public DateTimeOffset GetNextOccurrence(DateTimeOffset from)
    {
        var candidate = new DateTimeOffset(from.UtcDateTime.Date, TimeSpan.Zero) + Time.ToTimeSpan();

        // если время уже прошло сегодня — начинаем с завтра
        if (candidate <= from)
            candidate = candidate.AddDays(1);

        // ищем ближайший подходящий день
        for (int i = 0; i < 366; i++)
        {
            if (DayOfWeek.HasValue && candidate.DayOfWeek != DayOfWeek.Value)
            {
                candidate = candidate.AddDays(1);
                continue;
            }

            if (DayOfMonth.HasValue && candidate.Day != DayOfMonth.Value)
            {
                candidate = candidate.AddDays(1);
                continue;
            }

            if (Month.HasValue && candidate.Month != Month.Value)
            {
                candidate = candidate.AddDays(1);
                continue;
            }

            return candidate;
        }

        return DateTimeOffset.MaxValue;
    }
}
