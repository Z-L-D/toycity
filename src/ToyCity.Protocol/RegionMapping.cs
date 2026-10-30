using Google.Protobuf;
using ToyCity.Sim;
using P = ToyCity.Protocol.V0;

namespace ToyCity.Protocol;

/// <summary>Regions on the wire and on disk.</summary>
public static class RegionMapping
{
    public const uint RegionFormat = 1;
    /// <summary>Pixels along a side of a tile's picture on the region map.</summary>
    public const int PictureSize = 32;

    public static byte[] Save(Region r)
    {
        var s = new P.RegionSave
        {
            FormatVersion = RegionFormat, Name = r.Name, Seed = r.Seed, Cols = r.Cols, Rows = r.Rows, TileSize = r.TileSize, Tick = r.Tick,
        };
        foreach (var t in r.Tiles.Where(t => t.City != null))
            s.Cities.Add(new P.RegionSavedCity { Col = t.Col, Row = t.Row, Save = ByteString.CopyFrom(Mapping.Save(t.City!)) });
        return s.ToByteArray();
    }

    public static Region Load(ContentDb content, byte[] data)
    {
        var s = P.RegionSave.Parser.ParseFrom(data);
        if (s.FormatVersion != RegionFormat) throw new InvalidDataException($"region format {s.FormatVersion} is not supported");
        var cities = new List<World>();
        try
        {
            foreach (var c in s.Cities) cities.Add(Mapping.Load(content, c.Save.ToByteArray()));
            return Region.Restore(content, s.Name, s.Seed, s.Cols, s.Rows, s.TileSize, s.Tick, cities);
        }
        catch
        {
            foreach (var c in cities) c.Dispose();
            throw;
        }
    }

    /// <summary>True when the bytes are a region save rather than a city's.</summary>
    public static bool IsRegion(byte[] data)
    {
        try { var s = P.RegionSave.Parser.ParseFrom(data); return s.FormatVersion == RegionFormat && s.Cols > 0; }
        catch (InvalidProtocolBufferException) { return false; }
    }

    /// <summary>The region map. Pictures are the costly part: pass the ones already made to reuse them.</summary>
    public static P.RegionState State(Region r, int hereCol, int hereRow, int speed, Func<RegionTile, IEnumerable<string>> playersIn,
        IDictionary<int, ByteString>? pictures = null)
    {
        var s = new P.RegionState { Name = r.Name, Cols = r.Cols, Rows = r.Rows, TileSize = r.TileSize, Tick = r.Tick, HereCol = hereCol, HereRow = hereRow, Speed = speed };
        for (int i = 0; i < r.Tiles.Length; i++)
        {
            var t = r.Tiles[i];
            var pic = pictures != null && pictures.TryGetValue(i, out var p) ? p : ByteString.CopyFrom(Picture(r, t));
            if (pictures != null) pictures[i] = pic;
            var ts = new P.RegionTileState { Col = t.Col, Row = t.Row, Founded = t.Founded, Name = t.Name, Mayor = t.Mayor, Picture = pic };
            if (t.City is { } w) { ts.Population = w.Population; ts.Funds = w.Funds; }
            ts.Players.AddRange(playersIn(t));
            s.Tiles.Add(ts);
        }
        return s;
    }

    /// <summary>
    /// A tile seen from above at <see cref="PictureSize"/> pixels a side: water, ground by height, and on a city its
    /// roads, zones and buildings.
    /// </summary>
    public static byte[] Picture(Region r, RegionTile t)
    {
        int n = PictureSize, s = r.TileSize, step = s / n;
        var w = t.City;
        var heights = w?.Height ?? r.TileHeights(t.Col, t.Row);
        int water = w?.WaterLevel ?? 8;
        var px = new byte[n * n * 3];
        for (int py = 0; py < n; py++)
        for (int pxl = 0; pxl < n; pxl++)
        {
            // The most telling cell in the block: a building, then a road, then a zone, then the ground.
            int best = -1, rank = -1;
            for (int y = py * step; y < (py + 1) * step; y++)
            for (int x = pxl * step; x < (pxl + 1) * step; x++)
            {
                int i = y * s + x;
                int k = w == null ? 0 : w.BuildingAt[i] != 0 ? 3 : w.Path[i] != 0 ? 2 : w.ZoneCode[i] != 0 ? 1 : 0;
                if (k > rank) { rank = k; best = i; }
            }
            var (cr, cg, cb) = Colour(w, heights, water, best, rank);
            int o = (py * n + pxl) * 3;
            px[o] = cr; px[o + 1] = cg; px[o + 2] = cb;
        }
        return px;
    }

    static (byte, byte, byte) Colour(World? w, byte[] heights, int water, int i, int rank)
    {
        int h = heights[i];
        if (h < water) return ((byte)(40 + h * 6), (byte)(90 + h * 8), (byte)(150 + h * 8));
        if (w != null && rank == 3)
        {
            var b = w.BuildingAtCell(i)!;
            return w.TypeOf(b).Zone switch
            {
                Zone.Residential => (120, 200, 110),
                Zone.Commercial => (110, 150, 230),
                Zone.Industrial => (230, 200, 90),
                _ => (215, 215, 215),
            };
        }
        if (rank == 2) return (95, 95, 100);
        if (w != null && rank == 1)
            return World.ZoneOf(w.ZoneCode[i]) switch
            {
                Zone.Residential => (80, 140, 75),
                Zone.Commercial => (75, 100, 150),
                _ => (150, 130, 70),
            };
        int g = Math.Clamp(h - water, 0, 24);
        return ((byte)(96 + g * 4), (byte)(140 + g * 2), (byte)(70 + g * 3));
    }
}
