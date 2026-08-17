using SCADA.Core.Tags;

namespace SCADA.Core.Tests;

/// <summary>
/// Расписание записи в архив (docs/archive-format.md §6).
/// Расписание задаётся в местном времени объекта, поэтому проверяются не
/// только штатные моменты, но и оба перехода на летнее время: ошибка там
/// раз в год теряет либо задваивает сменный отчёт, и обнаруживается это
/// сверкой с бумажным журналом много позже.
/// </summary>
public class LogScheduleTests
{
    /// <summary>UTC+2 зимой, UTC+3 летом — переходы есть, в отличие от России.</summary>
    private static readonly TimeZoneInfo Berlin =
        TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows()
            ? "W. Europe Standard Time"
            : "Europe/Berlin");

    /// <summary>UTC+5 круглый год — перехода нет.</summary>
    private static readonly TimeZoneInfo Yekaterinburg =
        TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows()
            ? "Ekaterinburg Standard Time"
            : "Asia/Yekaterinburg");

    private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute = 0)
        => new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    [Fact]
    public void DailyEntry_FiresAtLocalTime_NotUtc()
    {
        var entry = new LogScheduleEntry { Time = new TimeOnly(8, 0) };

        // Полночь UTC 15 января = 05:00 местного в Екатеринбурге.
        var next = entry.GetNextOccurrence(Utc(2026, 1, 15, 0), Yekaterinburg);

        // Смена начинается в 08:00 на объекте, то есть в 03:00 UTC.
        Assert.Equal(Utc(2026, 1, 15, 3), next.ToUniversalTime());
    }

    [Fact]
    public void DailyEntry_AlreadyPassedToday_MovesToTomorrow()
    {
        var entry = new LogScheduleEntry { Time = new TimeOnly(8, 0) };

        // 10:00 местного — смена уже началась.
        var next = entry.GetNextOccurrence(Utc(2026, 1, 15, 5), Yekaterinburg);

        Assert.Equal(Utc(2026, 1, 16, 3), next.ToUniversalTime());
    }

    [Fact]
    public void WeeklyEntry_PicksRequestedWeekday()
    {
        var entry = new LogScheduleEntry
        {
            Time = new TimeOnly(6, 0),
            DayOfWeek = DayOfWeek.Monday
        };

        // 15 января 2026 — четверг.
        var next = entry.GetNextOccurrence(Utc(2026, 1, 15, 0), Yekaterinburg);
        var local = TimeZoneInfo.ConvertTime(next, Yekaterinburg);

        Assert.Equal(DayOfWeek.Monday, local.DayOfWeek);
        Assert.Equal(19, local.Day);
        Assert.Equal(6, local.Hour);
    }

    [Fact]
    public void MonthlyEntry_PicksRequestedDay()
    {
        var entry = new LogScheduleEntry
        {
            Time = new TimeOnly(0, 0),
            DayOfMonth = 1
        };

        var next = entry.GetNextOccurrence(Utc(2026, 1, 15, 0), Yekaterinburg);
        var local = TimeZoneInfo.ConvertTime(next, Yekaterinburg);

        Assert.Equal(1, local.Day);
        Assert.Equal(2, local.Month);
    }

    [Fact]
    public void SkippedHour_MovesToNearestExistingMoment()
    {
        // 29 марта 2026, Берлин: 02:00 → 03:00, часа 02:30 не существует.
        var entry = new LogScheduleEntry { Time = new TimeOnly(2, 30) };

        var next = entry.GetNextOccurrence(Utc(2026, 3, 28, 12), Berlin);
        var local = TimeZoneInfo.ConvertTime(next, Berlin);

        // Запись не теряется: сдвигается на первый существующий момент.
        Assert.Equal(29, local.Day);
        Assert.False(Berlin.IsInvalidTime(local.DateTime));
        Assert.True(local.Hour >= 3);
    }

    [Fact]
    public void RepeatedHour_FiresOnce_AtFirstOccurrence()
    {
        // 25 октября 2026, Берлин: 03:00 → 02:00, время 02:30 наступает дважды.
        var entry = new LogScheduleEntry { Time = new TimeOnly(2, 30) };

        var next = entry.GetNextOccurrence(Utc(2026, 10, 24, 12), Berlin);

        // Берётся ПЕРВОЕ наступление — летнее смещение UTC+2,
        // то есть 00:30 UTC, а не 01:30.
        Assert.Equal(Utc(2026, 10, 25, 0, 30), next.ToUniversalTime());
    }

    [Fact]
    public void ScheduleAndInterval_NearestWins()
    {
        var config = new TagLoggingConfiguration
        {
            Interval = TimeSpan.FromHours(12),
            Schedule = [new LogScheduleEntry { Time = new TimeOnly(8, 0) }]
        };

        var now = Utc(2026, 1, 15, 0);   // 05:00 местного
        var next = config.GetNextLoggingTime(now, now, Yekaterinburg);

        // Расписание даёт 03:00 UTC, интервал — 12:00 UTC. Ближе расписание.
        Assert.Equal(Utc(2026, 1, 15, 3), next.ToUniversalTime());
    }

    [Fact]
    public void MultipleEntries_NearestWins()
    {
        var config = new TagLoggingConfiguration
        {
            Schedule =
            [
                new LogScheduleEntry { Time = new TimeOnly(20, 0) },
                new LogScheduleEntry { Time = new TimeOnly(8, 0) },
                new LogScheduleEntry { Time = new TimeOnly(14, 0) }
            ]
        };

        var now = Utc(2026, 1, 15, 4);   // 09:00 местного
        var next = config.GetNextLoggingTime(now, now, Yekaterinburg);

        // Ближайшая смена — 14:00 местного = 09:00 UTC.
        Assert.Equal(Utc(2026, 1, 15, 9), next.ToUniversalTime());
    }

    [Fact]
    public void EmptyConfiguration_NeverFires()
    {
        var config = new TagLoggingConfiguration();
        var now = Utc(2026, 1, 15, 0);

        Assert.Equal(DateTimeOffset.MaxValue, config.GetNextLoggingTime(now, now, Yekaterinburg));
    }

    [Fact]
    public void ImpossibleDate_DoesNotHangSearch()
    {
        // 30 февраля не существует: перебор обязан завершиться, а не крутиться.
        var entry = new LogScheduleEntry
        {
            Time = new TimeOnly(8, 0),
            DayOfMonth = 30,
            Month = 2
        };

        Assert.Equal(DateTimeOffset.MaxValue,
            entry.GetNextOccurrence(Utc(2026, 1, 15, 0), Yekaterinburg));
    }

    [Fact]
    public void TimeZoneIsRequired()
    {
        var entry = new LogScheduleEntry { Time = new TimeOnly(8, 0) };

        Assert.Throws<ArgumentNullException>(
            () => entry.GetNextOccurrence(Utc(2026, 1, 15, 0), null!));
    }
}
