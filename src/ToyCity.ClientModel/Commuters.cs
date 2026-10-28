using ToyCity.Protocol.V0;

namespace ToyCity.ClientModel;

public enum TripState { Before, Moving, After }

/// <summary>
/// The commuters drawn on the map, as the web client draws them. The view runs its own clock through the morning
/// and evening rush (fifteen seconds each), whatever the game speed: each commuter leaves home at their own time,
/// follows their route cell by cell and reaches work after their door-to-door minutes, and goes back the other
/// way in the evening.
/// </summary>
public static class Commuters
{
    /// <summary>Evening trips leave this many minutes after the morning ones (7:45 becomes 17:00).</summary>
    public const int EveningShift = 9 * 60 + 15;
    const int RushStart = 7 * 60 + 30, RushLength = 150;
    const double RushSeconds = 15;

    /// <summary>The commuter clock at a moment in seconds: minutes after midnight, and whether it's the evening rush.</summary>
    public static (double Minute, bool Evening) Clock(double seconds)
    {
        double cycle = seconds % (2 * RushSeconds);
        bool evening = cycle >= RushSeconds;
        double m = cycle % RushSeconds / RushSeconds * RushLength;
        return (RushStart + (evening ? EveningShift : 0) + m, evening);
    }

    /// <summary>Where a commuter is at a moment of the clock, in cells, or null for a trip with no route.</summary>
    public static (float X, float Y, TripState State)? At(AgentTripInfo t, (double Minute, bool Evening) clock, int width)
    {
        int n = t.Cells.Count;
        if (n == 0) return null;
        double leave = clock.Evening ? t.DepartMinute + EveningShift : t.DepartMinute;
        double f = Math.Clamp((clock.Minute - leave) / Math.Max(1, t.Minutes), 0, 1);
        var state = clock.Minute < leave ? TripState.Before : f >= 1 ? TripState.After : TripState.Moving;
        double along = (clock.Evening ? 1 - f : f) * (n - 1);
        int k = (int)Math.Floor(along);
        int a = t.Cells[k], b = t.Cells[Math.Min(n - 1, k + 1)];
        float u = (float)(along - k);
        return (a % width * (1 - u) + b % width * u, a / width * (1 - u) + b / width * u, state);
    }

    /// <summary>What a followed commuter is doing, in words.</summary>
    public static string Status(AgentTripInfo t, TripState s, bool evening)
    {
        string how = t.ByTransit ? "riding transit" : "driving";
        return evening
            ? s switch { TripState.Before => "still at work", TripState.Moving => $"{how} home", _ => "home for the evening" }
            : s switch { TripState.Before => "at home, getting ready", TripState.Moving => $"{how} to work", _ => "at work" };
    }

    public static string TimeOfDay(double minute)
    {
        int hh = (int)(minute / 60), mm = (int)(minute % 60);
        return $"{(hh + 11) % 12 + 1}:{mm:00} {(hh % 24 < 12 ? "am" : "pm")}";
    }
}
