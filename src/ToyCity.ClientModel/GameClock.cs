using ToyCity.Protocol.V0;

namespace ToyCity.ClientModel;

/// <summary>
/// Both clocks as a player reads them (Beta 3.2, C1): the day clock's hour and the calendar's month and year, "Tue
/// 14:32 · March, year 3". The server moves the day clock a step each calendar day (48 minutes at a month a clock
/// day, 2 at two years), which at a slow calendar is minutes of real time; between steps the hands run on at the
/// speed's pace (a clock day an hour, half an hour, ten minutes), never past where the next step puts them.
/// </summary>
public sealed class GameClock
{
    /// <summary>A day of the day clock at each speed, in real milliseconds (the server's RegionHost.ClockDayMs).</summary>
    public static readonly int[] ClockDayMs = { 0, 3_600_000, 1_800_000, 600_000 };
    public const int MinutesPerDay = 24 * 60;

    public static readonly int[] Paces = { 30, 60, 120, 360, 720 };
    public static readonly string[] WeekdayNames = { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };
    public static readonly string[] MonthNames = { "January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December" };

    public static string PaceName(int pace) => pace switch
    {
        30 => "A month a day",
        60 => "Two months a day",
        120 => "Four months a day",
        360 => "A year a day",
        720 => "Two years a day",
        _ => $"{pace} days a day",
    };

    long _minute = -1;
    DateTime _at;

    /// <summary>The day clock, in minutes since it started, run on from the last tick to <paramref name="now"/>.</summary>
    public long Minute(CityStats s, DateTime now)
    {
        if (s.ClockMinute != _minute) { _minute = s.ClockMinute; _at = now; }
        int step = s.ClockMinutesPerTick, speed = Math.Clamp(s.Speed, 0, 3);
        if (step <= 0 || speed == 0) return s.ClockMinute;
        double minutesPerMs = (double)MinutesPerDay / ClockDayMs[speed];
        double ran = (now - _at).TotalMilliseconds * minutesPerMs;
        // Up to (not onto) the next step's minute: the server's tick moves it the rest of the way.
        return s.ClockMinute + (long)Math.Min(step - 1, Math.Max(0, ran));
    }

    /// <summary>"Tue 14:32 · March, year 3".</summary>
    public string Text(CityStats s, DateTime now) => Text(Minute(s, now), s.Month, s.Year);

    public static string Text(long minute, int month, int year)
    {
        int weekday = (int)(minute / MinutesPerDay % 7), hour = (int)(minute % MinutesPerDay / 60), min = (int)(minute % 60);
        string m = month is >= 1 and <= 12 ? MonthNames[month - 1] : "";
        return $"{WeekdayNames[weekday]} {hour:00}:{min:00} · {m}, year {Math.Max(1, year - 1999)}";
    }

    /// <summary>
    /// Turns of the day clock (48 minutes each, the step a dispatcher's calls and crews go by) as a span: "48 min",
    /// "2 h 24 min", "1 day".
    /// </summary>
    public static string Span(int turns)
    {
        int m = turns * 48;
        if (m == MinutesPerDay) return "1 day";
        if (m < 60) return $"{m} min";
        return m % 60 == 0 ? $"{m / 60} h" : $"{m / 60} h {m % 60:00} min";
    }

    /// <summary>Where the sun is in the day, 0 at midnight to 1 at the next (for the lighting to come).</summary>
    public double DayFraction(CityStats s, DateTime now) => Minute(s, now) % MinutesPerDay / (double)MinutesPerDay;
}
