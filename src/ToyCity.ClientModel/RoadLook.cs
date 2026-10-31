using System.Text.RegularExpressions;
using ToyCity.Protocol.V0;

namespace ToyCity.ClientModel;

/// <summary>What a path is paved with.</summary>
public enum Paving { Asphalt, Dirt, Gravel, Concrete, BikeLane, Rail }

/// <summary>The line painted down a two-way road's middle.</summary>
public enum CentreLine { None, DoubleYellow, Dashed, Barrier, GrassMedian, TramReservation }

/// <summary>
/// How a path looks from the air (Beta 3.1 part 4), worked out from its profile so a custom profile looks right
/// too. Widths are in metres across a 16 m cell. The rules follow North American practice, as SimCity 4 did:
/// <list type="bullet">
/// <item>A residential street has no centre line; busier two-way roads get a double yellow; lanes going the same
/// way are split by dashed white; a highway has solid white edge lines and, from four lanes, a barrier down the middle.</item>
/// <item>A road with pedestrians has concrete sidewalks to the edge of its cell; a highway has grass verges.</item>
/// <item>A wide avenue keeps 13 m of carriageway and puts the rest in a grass median, or a tram reservation.</item>
/// </list>
/// </summary>
public sealed record RoadLook(Paving Paving, float CarriagewayM, int Lanes, bool OneWay, CentreLine Centre,
    float MedianM, bool Sidewalks, bool EdgeLines, int TramTracks, bool Crosswalks)
{
    public const float CellM = 16f;
    const float MaxCarriagewayM = 13f, MaxHighwayM = 15f;

    public static RoadLook For(PathProfileInfo? p)
    {
        if (p == null) return new RoadLook(Paving.Asphalt, 7, 2, false, CentreLine.None, 0, true, false, 0, false);
        float w = Math.Max(2, p.WidthM);
        if (p.Rail) return new RoadLook(Paving.Rail, 4.5f, 1, false, CentreLine.None, 0, false, false, 1, false);
        if (!p.Cars)
        {
            // Trails, sidewalks and bike paths: one surface the profile's width, nothing painted.
            var pave = p.Bikes && !p.Pedestrians ? Paving.BikeLane
                : p.Bikes && w <= 3 ? Paving.Gravel
                : p.Bikes ? Paving.BikeLane : Paving.Concrete;
            return new RoadLook(pave, Math.Min(w, CellM), 1, false, CentreLine.None, 0, false, false, 0, false);
        }
        int digit = Digit(p.Id);
        if (p.SpeedKmh <= 25)
            return new RoadLook(Paving.Dirt, Math.Min(w, 8), 2, false, CentreLine.None, 0, false, false, 0, false);
        if (!p.Pedestrians)
        {
            int lanes = digit > 0 ? digit : Math.Max(2, (int)(w / 7) * 2);
            return new RoadLook(Paving.Asphalt, Math.Min(w, MaxHighwayM), lanes, p.OneWay,
                p.OneWay ? CentreLine.Dashed : lanes >= 4 ? CentreLine.Barrier : CentreLine.DoubleYellow, lanes >= 4 ? 0.8f : 0,
                false, true, 0, false);
        }
        // Town roads: sidewalks either side, the carriageway what's left (13 m at most), the rest in a median.
        float cw = Math.Clamp(w - 3, 6, MaxCarriagewayM);
        float median = w > CellM ? Math.Min(w - CellM, 4) : 0;
        bool dedicatedTram = p.Tram == "dedicated", sharedTram = p.Tram == "shared";
        int n = digit > 0 ? digit : 2;
        var centre = p.OneWay ? CentreLine.Dashed
            : dedicatedTram ? CentreLine.TramReservation
            : median > 0 ? CentreLine.GrassMedian
            : p.SpeedKmh <= 30 ? CentreLine.None : CentreLine.DoubleYellow;
        if (dedicatedTram) median = Math.Max(median, 4);
        return new RoadLook(Paving.Asphalt, cw, n, p.OneWay, centre, median, true, false,
            dedicatedTram || sharedTram ? 2 : 0, true);
    }

    static int Digit(string id)
    {
        var m = Regex.Match(id ?? "", @"-(\d)$");
        return m.Success ? m.Groups[1].Value[0] - '0' : 0;
    }

    /// <summary>Carriageway width as a fraction of a cell.</summary>
    public float Width => CarriagewayM / CellM;

    /// <summary>Where lane lines run across the carriageway, as offsets from its centre in cells, and whether each is
    /// the centre (two-way) line. Lines between lanes going the same way are dashed.</summary>
    public IEnumerable<(float Offset, bool Centre)> LaneLines()
    {
        if (Paving != Paving.Asphalt || Lanes < 2) yield break;
        float lane = (Width - MedianM / CellM) / Lanes, half = MedianM / CellM / 2;
        if (OneWay)
        {
            for (int k = 1; k < Lanes; k++) yield return (-Width / 2 + k * lane, false);
            yield break;
        }
        yield return (0, true);
        int each = Lanes / 2;
        for (int k = 1; k < each; k++)
        {
            yield return (half + k * lane, false);
            yield return (-half - k * lane, false);
        }
    }

    /// <summary>
    /// Which sides a path cell joins its neighbours on: bit 0 north, 1 east, 2 south, 3 west. Rail joins rail and road
    /// joins road; a cell under an arc or diagonal is drawn by its strip, so nothing joins it.
    /// </summary>
    public static int Arms(CityMirror c, int i)
    {
        int x = i % c.Width, y = i / c.Width, arms = 0;
        bool rail = c.ProfileAt(i)?.Rail == true;
        ReadOnlySpan<(int Dx, int Dy)> dirs = [(0, -1), (1, 0), (0, 1), (-1, 0)];
        for (int k = 0; k < 4; k++)
        {
            int nx = x + dirs[k].Dx, ny = y + dirs[k].Dy;
            if (!c.InBounds(nx, ny)) continue;
            int j = c.Idx(nx, ny);
            if (c.Paths[j] == 0 || RoadShape.UnderStrip(c, j) || (c.ProfileAt(j)?.Rail == true) != rail) continue;
            arms |= 1 << k;
        }
        return arms;
    }

    /// <summary>The painted dashes along a cell's axis, as fractions of the cell: two 4 m dashes a cell, at the same
    /// places in every cell so a line runs on evenly from one cell to the next.</summary>
    public static readonly (float From, float To)[] Dashes = [(0.0625f, 0.3125f), (0.5625f, 0.8125f)];
}
