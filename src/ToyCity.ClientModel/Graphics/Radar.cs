using ToyCity.Protocol.V0;

namespace ToyCity.ClientModel.Graphics;

/// <summary>The radar's products: base reflectivity (dBZ) and base velocity (knots, toward or away from the radar).</summary>
public enum RadarProduct { Reflectivity = 0, Velocity = 1 }

/// <summary>
/// The city's weather radar (Beta 4.1, part 3), drawn the way the National Weather Service's NEXRAD pages show it:
/// base reflectivity and base velocity at the radar's lowest tilts. The server sends the storm cells as they are at a
/// clock minute; this paints them pixel by pixel, the same in both clients (the web client has a word-for-word port in
/// <c>web/src/radar.ts</c>). The radar stands in the middle of the city. Its beam climbs with range, by its tilt and the
/// curve of the earth, so a higher tilt overshoots the tops of shallow snow far off; each cell gets a ragged edge and
/// a mottled core from a noise of its own seed.
/// </summary>
public static class Radar
{
    /// <summary>The lowest four tilts of a NEXRAD volume scan, in degrees.</summary>
    public static readonly double[] Tilts = { 0.5, 0.9, 1.3, 1.8 };

    /// <summary>How far the radar sees, in km.</summary>
    public const double RangeKm = 40;

    /// <summary>The earth's radius for a radar beam (four thirds of the real one), in km.</summary>
    const double BeamEarthKm = 8495;

    /// <summary>The height of the beam's middle over the radar at a range (km), by its tilt.</summary>
    public static double BeamHeightKm(double rangeKm, double tiltDeg) =>
        rangeKm * Math.Sin(tiltDeg * Math.PI / 180) + rangeKm * rangeKm / (2 * BeamEarthKm);

    /// <summary>A cell where it'll be (or was) some hours after the time it was sent for, carried along at its speed.</summary>
    public static StormCell At(StormCell c, double hours) => new()
    {
        X = (float)(c.X + c.Vx * hours), Y = (float)(c.Y + c.Vy * hours), Vx = c.Vx, Vy = c.Vy, Along = c.Along, Across = c.Across,
        Peak = c.Peak, TopKm = c.TopKm, Kind = c.Kind, Seed = c.Seed,
    };

    static uint Hash(int ix, int iy, uint seed)
    {
        uint h = unchecked((uint)ix * 374761393u + (uint)iy * 668265263u + seed * 2246822519u);
        h = unchecked((h ^ (h >> 13)) * 1274126177u);
        return h ^ (h >> 16);
    }

    /// <summary>Smooth value noise in 0..1 over a lattice one unit apart.</summary>
    public static double Noise(double x, double y, uint seed)
    {
        int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y);
        double fx = x - ix, fy = y - iy;
        fx = fx * fx * (3 - 2 * fx); fy = fy * fy * (3 - 2 * fy);
        const double S = 1.0 / 4294967296.0;
        double a = Hash(ix, iy, seed) * S, b = Hash(ix + 1, iy, seed) * S, c = Hash(ix, iy + 1, seed) * S, d = Hash(ix + 1, iy + 1, seed) * S;
        return a + (b - a) * fx + (c - a) * fy + (a - b - c + d) * fx * fy;
    }

    /// <summary>
    /// What one cell gives the radar at a point (km from the radar): its reflectivity (dBZ, below 0 where there's none)
    /// with its texture, and its speed toward (negative) or away from the radar (knots).
    /// </summary>
    public static (double Dbz, double Knots) Sample(StormCell c, double x, double y, double tiltDeg)
    {
        double sp = Math.Sqrt(c.Vx * c.Vx + c.Vy * c.Vy);
        double ux = sp > 0.01 ? c.Vx / sp : 1, uy = sp > 0.01 ? c.Vy / sp : 0;
        double dx = x - c.X, dy = y - c.Y;
        double a = (dx * ux + dy * uy) / c.Along, b = (-dx * uy + dy * ux) / c.Across;
        double d2 = a * a + b * b;
        if (d2 > 1.6) return (-1, 0);
        // A ragged edge and a mottled core: two octaves of the cell's own noise, a band of heavier rain down its middle.
        double n1 = Noise(x * 0.55, y * 0.55, c.Seed), n2 = Noise(x * 1.7, y * 1.7, c.Seed ^ 0x9E37u);
        double edge = d2 + (n1 - 0.5) * 0.7 + (n2 - 0.5) * 0.25;
        if (edge > 1) return (-1, 0);
        double dbz = c.Peak - 18 * Math.Max(0, edge) + (n2 - 0.5) * 6 + (n1 - 0.5) * 4;
        // A thunderstorm's core runs hotter and tighter: hail in the middle.
        if (c.Kind == 3) dbz += 10 * Math.Max(0, 1 - d2 * 5);
        // The beam climbs with range: as it nears the echo tops it sees less, and over them it sees nothing.
        double range = Math.Sqrt(x * x + y * y);
        double h = BeamHeightKm(range, tiltDeg), top = Math.Max(0.5, c.TopKm);
        if (h > top) return (-1, 0);
        dbz -= 10 * (h / top) * (h / top);
        // Radial speed: the cell's motion along the beam, with a little turbulence; a thunderstorm turns about its core.
        double knots = 0;
        if (range > 0.05)
        {
            double vr = (c.Vx * x + c.Vy * y) / range;
            if (c.Kind == 3)
            {
                // Counter-clockwise about the core, seen along the beam: the couplet of a turning storm.
                double dd = Math.Sqrt(dx * dx + dy * dy);
                double spin = 30 * Math.Exp(-d2 * 6) * Math.Min(1, d2 * 12);
                if (dd > 0.01) vr += spin * (dy * x - dx * y) / (dd * range);
            }
            knots = vr / 1.852 + (n2 - 0.5) * 8;
        }
        return (dbz, knots);
    }

    /// <summary>The radar at a point: the strongest return and its speed.</summary>
    public static (double Dbz, double Knots) SampleAll(IReadOnlyList<StormCell> cells, double x, double y, double tiltDeg)
    {
        double best = -1, kt = 0;
        foreach (var c in cells)
        {
            var (z, v) = Sample(c, x, y, tiltDeg);
            if (z > best) { best = z; kt = v; }
        }
        return (best, kt);
    }

    /// <summary>The NWS base reflectivity colours, from 5 dBZ in steps of 5.</summary>
    public static readonly uint[] DbzColours =
    {
        0x04E9E7, 0x019FF4, 0x0300F4, 0x02FD02, 0x01C501, 0x008E00, 0xFDF802, 0xE5BC00,
        0xFD9500, 0xFD0000, 0xD40000, 0xBC0000, 0xF800FD, 0x9854C6, 0xFDFDFD,
    };

    /// <summary>The colour (0xRRGGBB) for a reflectivity, or null below 5 dBZ.</summary>
    public static uint? ColourForDbz(double dbz) => dbz < 5 ? null : DbzColours[Math.Min(DbzColours.Length - 1, (int)((dbz - 5) / 5))];

    /// <summary>The velocity colours, from 64 knots toward the radar (bright green) through still (grey) to 64 away (bright red).</summary>
    public static readonly uint[] VelocityColours =
    {
        0x02FC02, 0x01E401, 0x01C501, 0x07AC04, 0x068F03, 0x046F02, 0x788C78, 0x8C7878,
        0x6E0000, 0x8E0000, 0xAC0000, 0xC50000, 0xE40000, 0xFC0000,
    };

    /// <summary>The colour for a radial speed in knots (negative toward the radar).</summary>
    public static uint ColourForKnots(double kt)
    {
        int band = (int)Math.Floor(kt / 10) + 7;
        return VelocityColours[Math.Clamp(band, 0, VelocityColours.Length - 1)];
    }

    /// <summary>
    /// Paints the radar into <paramref name="rgba"/> (size × size pixels, four bytes each), the radar in the middle and
    /// <see cref="RangeKm"/> to each edge. Where there's no echo the pixel is left clear, for the map beneath.
    /// </summary>
    public static void Paint(IReadOnlyList<StormCell> cells, RadarProduct product, double tiltDeg, int size, byte[] rgba)
    {
        Array.Clear(rgba);
        if (cells.Count == 0) return;
        double km = 2 * RangeKm / size;
        for (int py = 0; py < size; py++)
            for (int px = 0; px < size; px++)
            {
                double x = (px + 0.5) * km - RangeKm, y = (py + 0.5) * km - RangeKm;
                if (x * x + y * y > RangeKm * RangeKm) continue;
                var (z, kt) = SampleAll(cells, x, y, tiltDeg);
                uint? col = product == RadarProduct.Reflectivity ? ColourForDbz(z) : z >= 5 ? ColourForKnots(kt) : null;
                if (col is not { } c) continue;
                int o = (py * size + px) * 4;
                rgba[o] = (byte)(c >> 16); rgba[o + 1] = (byte)(c >> 8); rgba[o + 2] = (byte)c; rgba[o + 3] = 235;
            }
    }

    /// <summary>The cells for a frame some minutes before (negative) or after the time the sky was sent for.</summary>
    public static List<StormCell> Frame(Sky sky, double minutes) => sky.Cells.Select(c => At(c, minutes / 60)).ToList();

    /// <summary>How many frames the loop has, ten clock minutes apart, ending on the latest.</summary>
    public const int LoopFrames = 6;

    /// <summary>The legend's bands for a product: each colour and the value it starts at.</summary>
    public static IReadOnlyList<(uint Colour, string Label)> Legend(RadarProduct product) => product == RadarProduct.Reflectivity
        ? DbzColours.Select((c, i) => (c, (5 + 5 * i).ToString())).ToList()
        : VelocityColours.Select((c, i) => (c, ((i - 7) * 10).ToString())).ToList();

    static readonly string[] Points = { "N", "NNE", "NE", "ENE", "E", "ESE", "SE", "SSE", "S", "SSW", "SW", "WSW", "W", "WNW", "NW", "NNW" };

    /// <summary>A bearing as one of sixteen compass points.</summary>
    public static string Compass16(int deg) => Points[(int)Math.Round(((deg % 360) + 360) % 360 / 22.5) % 16];

    /// <summary>The weather station's readings as the panel lists them.</summary>
    public static IReadOnlyList<(string Label, string Value)> Station(Sky sky) => new[]
    {
        ("Temperature", $"{sky.TempC:0.0} °C"),
        ("Dew point", $"{sky.DewC:0.0} °C"),
        ("Humidity", $"{sky.Humidity} %"),
        ("Wind", sky.WindKmh <= 1 ? "Calm" : $"{Compass16(sky.WindFromDeg)} {sky.WindKmh} km/h"),
        ("Pressure", $"{sky.PressureHpa:0.0} hPa"),
        ("Rain now", sky.RateMmH < 0.05 ? "None" : $"{sky.RateMmH:0.0} mm/h"),
        ("Since midnight", $"{sky.TodayMm:0.0} mm"),
    };

    /// <summary>
    /// A storm the player draws on the radar: dragged from (<paramref name="x0"/>, <paramref name="y0"/>) to
    /// (<paramref name="x1"/>, <paramref name="y1"/>), km from the radar, the drag being where it'll be in an hour.
    /// Gives the action's start cell on a map of the given size, its heading (0 east, 90 south) and its speed.
    /// </summary>
    public static (int X, int Y, int HeadingDeg, int SpeedKmh) Drawn(double x0, double y0, double x1, double y1, int mapWidth, int mapHeight)
    {
        double dx = x1 - x0, dy = y1 - y0;
        int heading = (int)Math.Round(Math.Atan2(dy, dx) * 180 / Math.PI);
        heading = ((heading % 360) + 360) % 360;
        int speed = (int)Math.Clamp(Math.Round(Math.Sqrt(dx * dx + dy * dy)), 5, 120);
        int cx = (int)Math.Floor(x0 / CellKm + mapWidth / 2.0), cy = (int)Math.Floor(y0 / CellKm + mapHeight / 2.0);
        return (cx, cy, heading, speed);
    }

    /// <summary>Kilometres across a cell of the map, as the sim has it.</summary>
    public const double CellKm = 0.016;

    /// <summary>The bearing a storm heads toward (0 north, 90 east), from its heading on the map (0 east, 90 south).</summary>
    public static int Bearing(int headingDeg) => ((headingDeg + 90) % 360 + 360) % 360;
}
