namespace ToyCity.Sim;

/// <summary>
/// Snow on the ground (Beta 4, Part 4). Snowfall lays snow over the city during the showers of the day clock, as it
/// does on the roads, and the sun, rain and warm weather melt it, slowly in the depth of winter. The city keeps one
/// depth, the depth on open level ground at its median height; the clients spread it over the ground by height, slope
/// and the way a slope faces (<see cref="SnowDepth.At"/>), so high ground and north-facing slopes keep their snow longest.
/// Saved.
/// </summary>
public sealed partial class World
{
    /// <summary>The most snow that lies, in millimetres.</summary>
    public const int MaxSnowMm = 800;

    /// <summary>Snow on open level ground at the city's median height, in millimetres.</summary>
    public int GroundSnowMm { get; private set; }

    /// <summary>Every step: snow falls in the day clock's showers and melts the rest of the time.</summary>
    void RunSnow()
    {
        int minutes = ClockMinutesPerTick;
        int Per(int mmPerHour) => (int)Math.Max(1, (long)mmPerHour * minutes / 60);
        // Light snow lays half a centimetre an hour, heavy snow one and a half, a blizzard four.
        if (Falling && Weather == WeatherKind.Snow)
            GroundSnowMm = Math.Min(MaxSnowMm, GroundSnowMm + Per(Math.Clamp(WeatherStrength, 1, 3) switch { 1 => 5, 2 => 15, _ => 40 }));
        else if (GroundSnowMm > 0 && Weather != WeatherKind.Ice)
        {
            // Melting, a millimetre an hour in the depth of winter to four centimetres in a heatwave.
            int month = Month;
            int melt = Weather == WeatherKind.Heat ? 40 : Weather == WeatherKind.Rain ? 15
                : month is 12 or 1 or 2 ? 1 : month is 3 or 11 ? 4 : 12;
            // And the calendar's own days thaw it with the season, so a fast calendar doesn't keep a winter's snow
            // into the summer while the day clock barely moves.
            // Not while it's still snowing on and off.
            int thaw = Weather == WeatherKind.Snow || month is 12 or 1 or 2 ? 0 : month is 3 or 11 ? 8 : 40;
            GroundSnowMm = Math.Max(0, GroundSnowMm - Per(melt) - thaw);
        }
    }

    void WriteSnow(BinaryWriter w) => w.Write(GroundSnowMm);
    void ReadSnow(BinaryReader r) => GroundSnowMm = Math.Clamp(r.ReadInt32(), 0, MaxSnowMm);
}
