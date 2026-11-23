using NewsDesk.Core.Model;

namespace NewsDesk.Core.Scheduling;

public static class ScheduleCalculator
{
    /// <summary>
    /// Next due time strictly after <paramref name="afterUtc"/>, in UTC. Returns null for manual schedules.
    /// Daily and weekly times are interpreted in the project timezone, so DST changes keep the local wall-clock time.
    /// </summary>
    public static DateTimeOffset? NextDue(Schedule schedule, string timeZoneId, DateTimeOffset afterUtc)
    {
        switch (schedule.Kind)
        {
            case ScheduleKind.Manual:
                return null;
            case ScheduleKind.Interval:
                return afterUtc.AddMinutes(Math.Max(Schedule.MinimumIntervalMinutes, schedule.IntervalMinutes));
        }

        var tz = FindZone(timeZoneId);
        var localNow = TimeZoneInfo.ConvertTime(afterUtc, tz);
        var allowed = schedule.Kind == ScheduleKind.Weekly && schedule.Days.Count > 0 ? schedule.Days.ToHashSet() : null;
        for (var d = 0; d <= 8; d++)
        {
            var date = DateOnly.FromDateTime(localNow.Date).AddDays(d);
            if (allowed is not null && !allowed.Contains(date.DayOfWeek)) continue;
            var local = date.ToDateTime(schedule.TimeOfDay);
            if (tz.IsInvalidTime(local)) local = local.AddHours(1); // skipped by spring-forward
            var utc = new DateTimeOffset(local, tz.GetUtcOffset(local)).ToUniversalTime();
            if (utc > afterUtc) return utc;
        }
        return null;
    }

    public static TimeZoneInfo FindZone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.Local; }
        catch (InvalidTimeZoneException) { return TimeZoneInfo.Local; }
    }
}
