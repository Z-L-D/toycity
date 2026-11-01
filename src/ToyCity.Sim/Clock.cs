namespace ToyCity.Sim;

/// <summary>
/// The mayor sets the calendar's pace (Beta 3.2, C3): how many calendar days pass in one day of the day clock. One of
/// <see cref="World.Paces"/>. In a region every city keeps one pace; its host sends this to them all on one day.
/// </summary>
public sealed record SetPaceAction(int DaysPerClockDay) : SimAction;

/// <summary>
/// Two clocks (Beta 3.2, C1 to C5). The <em>calendar</em> is the tick: a day each, with its months, seasons, growth,
/// building, money and taxes, all tuned to it as before. The <em>day clock</em> runs the hours, in minutes since it
/// started: what a commuter, a dispatcher or the sun goes by. The mayor sets how they relate, from a month of calendar
/// a clock day (slow and life-like) to two years a clock day (about the old pace); the host sets how long a clock day
/// lasts in real time (an hour, half an hour, ten minutes).
/// <para>
/// Life-paced things go by the day clock in <em>turns</em> of 48 minutes: the city raises its own emergency calls,
/// fires burn and spread, storms move, and a side game's calls wait, all a turn at a time. At a month a clock day a
/// turn is a calendar day, so everything plays exactly as it did with one clock; at a faster calendar several calendar
/// days pass in each turn, and a dispatcher still gets their calls at the same rate per hour.
/// </para>
/// </summary>
public sealed partial class World
{
    /// <summary>The calendar paces a mayor can pick, in calendar days per clock day. Each divides a clock day evenly.</summary>
    public static readonly int[] Paces = { 30, 60, 120, 360, 720 };
    /// <summary>A lone city's pace until told otherwise: a month a clock day, a turn a tick, as before the day clock.</summary>
    public const int DefaultPace = 30;
    /// <summary>The pace a city from before the day clock takes up: a year a clock day, so speed 1 grows it as it did.</summary>
    public const int OldSavePace = 360;
    public const int MinutesPerClockDay = 24 * 60;
    /// <summary>A turn of the day clock, in minutes: the step life-paced things take.</summary>
    public const int TurnMinutes = 48;
    /// <summary>Where the day clock starts: Monday, 08:00.</summary>
    public const int ClockStartMinute = 8 * 60;

    /// <summary>Calendar days per clock day: one of <see cref="Paces"/>.</summary>
    public int Pace { get; private set; } = DefaultPace;
    /// <summary>The day clock: minutes since it started (Monday 00:00 is 0).</summary>
    public long ClockMinute { get; private set; } = ClockStartMinute;
    /// <summary>How far the day clock moves each calendar day: 48 minutes at a month a day, 2 at two years.</summary>
    public int ClockMinutesPerTick => MinutesPerClockDay / Pace;
    /// <summary>Turns of the day clock since it started: at a month a clock day, the same as the calendar's tick.</summary>
    public long Turn => (ClockMinute - ClockStartMinute) / TurnMinutes;
    /// <summary>The clocks as they stood when the city was founded, so a replay from its founding starts on the same hour.</summary>
    public long FoundedClockMinute { get; private set; } = ClockStartMinute;
    public int FoundedPace { get; private set; } = DefaultPace;
    /// <summary>True while a step is running whose stretch of the day clock begins a turn.</summary>
    public bool NewTurn { get; private set; } = true;

    public long ClockDay => ClockMinute / MinutesPerClockDay;
    public int Hour => (int)(ClockMinute % MinutesPerClockDay / 60);
    public int MinuteOfHour => (int)(ClockMinute % 60);
    /// <summary>0 Monday .. 6 Sunday.</summary>
    public int Weekday => (int)(ClockDay % 7);

    public static readonly string[] WeekdayNames = { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };
    static readonly string[] MonthNames = { "January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December" };

    /// <summary>Both clocks, as the player reads them: "Tue 14:32 · March, year 3".</summary>
    public string ClockText => $"{WeekdayNames[Weekday]} {Hour:00}:{MinuteOfHour:00} · {MonthNames[Month - 1]}, year {Year - StartYear + 1}";

    public static bool IsPace(int pace) => Array.IndexOf(Paces, pace) >= 0;

    public static string PaceName(int pace) => pace switch
    {
        30 => "A month a day",
        60 => "Two months a day",
        120 => "Four months a day",
        360 => "A year a day",
        720 => "Two years a day",
        _ => $"{pace} days a day",
    };

    /// <summary>Called first in a step: whether this calendar day's stretch of the day clock starts a turn.</summary>
    void BeginClockStep() => NewTurn = (ClockMinute + ClockMinutesPerTick) / TurnMinutes != ClockMinute / TurnMinutes;
    // (The clock starts on a turn, 08:00 being ten of them, so counting from midnight or from the start agrees.)

    /// <summary>Called last in a step: the day clock moves on.</summary>
    void EndClockStep() => ClockMinute += ClockMinutesPerTick;

    void ApplyPace(SetPaceAction a)
    {
        if (!IsPace(a.DaysPerClockDay) || a.DaysPerClockDay == Pace) return;
        Pace = a.DaysPerClockDay;
        // The clock moves on in whole steps of the new pace, so turns keep falling on step boundaries.
        ClockMinute = RoundUp(ClockMinute, ClockMinutesPerTick);
        Notices.Add(new SimNotice($"The calendar runs at {PaceName(Pace).ToLowerInvariant()} of the day clock.", "info"));
    }

    /// <summary>A tile founded into a region takes the region's clocks, so every city reads the same hour.</summary>
    internal void SetClock(long clockMinute, int pace)
    {
        if (IsPace(pace)) Pace = pace;
        ClockMinute = RoundUp(clockMinute, ClockMinutesPerTick);
        if (Tick == FoundedTick) (FoundedClockMinute, FoundedPace) = (ClockMinute, Pace);
    }

    /// <summary>The clocks for a city founded into this one's region today.</summary>
    public (long Minute, int Pace) Clocks => (ClockMinute, Pace);
    /// <summary>The clocks this city was founded on.</summary>
    public (long Minute, int Pace) FoundedClocks => (FoundedClockMinute, FoundedPace);

    static long RoundUp(long minute, int step) => (minute + step - 1) / step * step;

    void WriteClock(BinaryWriter w)
    {
        w.Write(Pace);
        w.Write(ClockMinute);
        w.Write(FoundedPace);
        w.Write(FoundedClockMinute);
    }

    void ReadClock(BinaryReader r)
    {
        int pace = r.ReadInt32();
        Pace = IsPace(pace) ? pace : DefaultPace;
        ClockMinute = r.ReadInt64();
        int founded = r.ReadInt32();
        FoundedPace = IsPace(founded) ? founded : DefaultPace;
        FoundedClockMinute = r.ReadInt64();
    }
}
