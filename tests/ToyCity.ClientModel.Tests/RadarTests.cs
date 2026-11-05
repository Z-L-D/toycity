using ToyCity.ClientModel.Graphics;
using ToyCity.Protocol.V0;

namespace ToyCity.ClientModel.Tests;

/// <summary>Beta 4.1, part 3: the radar as both clients paint it.</summary>
public class RadarTests
{
    static ToyCity.Protocol.V0.StormCell Cell(float x, float y, float vx, float vy, float peak, float top, int kind = 0) =>
        new() { X = x, Y = y, Vx = vx, Vy = vy, Along = 8, Across = 5, Peak = peak, TopKm = top, Kind = kind, Seed = 7 };

    [Fact]
    public void TheBeamClimbsWithRangeAndTilt()
    {
        // 0.5° at 40 km: 349 m up the slope and 94 m for the earth's curve.
        Assert.InRange(Radar.BeamHeightKm(40, 0.5), 0.43, 0.46);
        Assert.True(Radar.BeamHeightKm(40, 1.8) > Radar.BeamHeightKm(40, 0.5) * 2.5);
        // Shallow snow far off shows at the lowest tilt and is overshot at the highest.
        var snow = Cell(30, 0, 30, 0, 30, 0.8f, 1);
        Assert.True(Radar.Sample(snow, 30, 0, 0.5).Dbz >= 5);
        Assert.True(Radar.Sample(snow, 30, 0, 1.8).Dbz < 0);
    }

    [Fact]
    public void ReflectivityAndVelocityPaintTheNwsColours()
    {
        Assert.Null(Radar.ColourForDbz(3));
        Assert.Equal(0x02FD02u, Radar.ColourForDbz(22));
        Assert.Equal(0xFD0000u, Radar.ColourForDbz(52));
        // A cell heading east: moving away from the radar east of it (reds), toward it west of it (greens).
        var east = Cell(0, 0, 50, 0, 40, 8);
        Assert.True(Radar.Sample(east, 3, 0, 0.5).Knots > 15);
        Assert.True(Radar.Sample(east, -3, 0, 0.5).Knots < -15);
        Assert.True(Radar.ColourForKnots(30) >> 16 > 0x80);
        Assert.True((Radar.ColourForKnots(-30) >> 8 & 0xFF) > 0x80);
        // Painted: an echo where the cell is, clear sky far from it and outside the range.
        int n = 64;
        var px = new byte[n * n * 4];
        Radar.Paint(new[] { east }, RadarProduct.Reflectivity, 0.5, n, px);
        Assert.Equal(235, px[(n / 2 * n + n / 2) * 4 + 3]);
        Assert.Equal(0, px[(2 * n + n / 2) * 4 + 3]);
        Assert.Equal(0, px[3]);
        int lit = Enumerable.Range(0, n * n).Count(i => px[i * 4 + 3] > 0);
        Assert.InRange(lit, 20, n * n / 4);
    }

    [Fact]
    public void TheServersShowersReachTheRadar()
    {
        // A shower hour from the sim, mapped to the wire as the server sends it, paints rain over the city.
        long hour = Enumerable.Range(2400, 48).Select(h => (long)h).First(h => Sim.World.ShowerHour(h, Sim.WeatherKind.Rain, 2));
        var cells = Sim.World.StormCells(hour * 60 + 30, Sim.WeatherKind.Rain, 2, Array.Empty<Sim.World.PlacedStorm>(), 6)
            .Select(c => new ToyCity.Protocol.V0.StormCell { X = (float)c.X, Y = (float)c.Y, Vx = (float)c.Vx, Vy = (float)c.Vy, Along = (float)c.Along, Across = (float)c.Across, Peak = (float)c.Peak, TopKm = (float)c.TopKm, Kind = (int)c.Kind, Seed = c.Seed })
            .ToList();
        Assert.True(Radar.SampleAll(cells, 0, 0, 0.5).Dbz >= 10);
        // The loop's earlier frames carry the cells back along their tracks.
        var sky = new Sky(); sky.Cells.AddRange(cells);
        var before = Radar.Frame(sky, -50);
        Assert.Equal(cells.Count, before.Count);
        Assert.True(before[0].X < cells[0].X == cells[0].Vx > 0);
    }

    [Fact]
    public void ADrawnStormStartsWhereItWasDrawn()
    {
        // Dragged 20 km west of the radar toward 20 km east of where it started: east at 20 km/h.
        var (x, y, heading, speed) = Radar.Drawn(-20, 0, 0, 0, 128, 128);
        Assert.Equal(0, heading);
        Assert.Equal(20, speed);
        double km = (x + 0.5 - 64) * Radar.CellKm;
        Assert.InRange(km, -20.02, -19.98);
        Assert.Equal(64, y);
        Assert.Equal(90, Radar.Drawn(0, 0, 0, 10, 128, 128).HeadingDeg);
        Assert.Equal(180, Radar.Bearing(90));
        Assert.Equal("WSW", Radar.Compass16(250));
        var sky = new Sky { TempC = 12.3f, WindFromDeg = 270, WindKmh = 14, RateMmH = 0 };
        var lines = Radar.Station(sky);
        Assert.Contains(lines, l => l.Value == "W 14 km/h");
        Assert.Contains(lines, l => l.Label == "Rain now" && l.Value == "None");
    }
}
