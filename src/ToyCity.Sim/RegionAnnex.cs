namespace ToyCity.Sim;

/// <summary>
/// Annexing (Beta 4, part 6, stage 2): a city takes on the open land beside it and its map grows, 1 × 1 to 2 × 1 to
/// 2 × 2 tiles and on, always a rectangle (an L would need both corner tiles bought at once). The city as it stands
/// moves onto the bigger map whole, and the new land comes with the region's ground. A city of several tiles is
/// counted from its top-left tile (<see cref="RegionTile.Anchor"/>), and each of its sides may border one other city
/// at most, so the border ledgers still pair one city with one city.
/// </summary>
public sealed partial class Region
{
    /// <summary>A city's tiles: its top-left tile's column and row, and how many tiles it spans each way.</summary>
    public (int Col, int Row, int Cols, int Rows) Footprint(RegionTile t)
    {
        var a = t.Anchor ?? t;
        if (a.City is not { } w) return (t.Col, t.Row, 1, 1);
        return (a.Col, a.Row, Math.Max(1, w.Width / TileSize), Math.Max(1, w.HeightCells / TileSize));
    }

    /// <summary>The tiles just across one side of a city (or of a tile of open land), in order along that side.</summary>
    public IEnumerable<RegionTile?> AcrossSide(RegionTile t, int side)
    {
        var (c0, r0, cols, rows) = Footprint(t);
        int n = side is 0 or 2 ? cols : rows;
        for (int k = 0; k < n; k++)
            yield return side switch
            {
                0 => TileAt(c0 + k, r0 - 1),
                1 => TileAt(c0 + cols, r0 + k),
                2 => TileAt(c0 + k, r0 + rows),
                _ => TileAt(c0 - 1, r0 + k),
            };
    }

    /// <summary>The other city across a side of this one (its top-left tile), or null: there's at most one.</summary>
    public RegionTile? NeighbourOn(RegionTile t, int side)
    {
        var me = (t.Anchor ?? t).City;
        foreach (var o in AcrossSide(t, side))
            if (o?.City is { } c && c != me) return o.Anchor;
        return null;
    }

    /// <summary>
    /// What a city hears across one of its sides: each neighbour's edge, put where it meets this side. For two cities
    /// of one tile each this is just the neighbour's own edge.
    /// </summary>
    NeighbourEdgeAction EdgeIn(RegionTile t, int side, Dictionary<(World, int), NeighbourEdgeAction> made)
    {
        var a = t.Anchor ?? t;
        int opp = World.Opposite(side), s = TileSize, seg = 0;
        NeighbourEdgeAction? first = null;
        List<int>? paths = null, power = null, pipes = null, fire = null, movers = null;
        foreach (var o in AcrossSide(a, side))
        {
            int ours = seg++ * s;
            if (o?.City is not { } n || n == a.City) continue;
            if (!made.TryGetValue((n, opp), out var e)) made[(n, opp)] = e = n.EdgeOut(opp);
            var na = o.Anchor!;
            int theirs = (side is 0 or 2 ? o.Col - na.Col : o.Row - na.Row) * s;
            // The whole of a one-tile neighbour along a one-tile side: as it is.
            if (first == null && ours == 0 && theirs == 0 && SideLength(a, side) == s && n.SideLength(opp) == s) return e;
            first ??= e;
            paths ??= new(); power ??= new(); pipes ??= new(); fire ??= new(); movers ??= new();
            int Map(int k) => k >= theirs && k < theirs + s ? k - theirs + ours : -1;
            for (int j = 0; j + 1 < e.Paths.Length; j += 2) if (Map(e.Paths[j]) is >= 0 and var k) { paths.Add(k); paths.Add(e.Paths[j + 1]); }
            foreach (int k in e.Power) if (Map(k) is >= 0 and var m) power.Add(m);
            foreach (int k in e.Pipes) if (Map(k) is >= 0 and var m) pipes.Add(m);
            foreach (int k in e.Fire ?? Array.Empty<int>()) if (Map(k) is >= 0 and var m) fire.Add(m);
            var mv = e.Movers ?? Array.Empty<int>();
            for (int j = 0; j + 4 < mv.Length; j += 5)
                if (Map(mv[j + 1]) is >= 0 and var m) movers.AddRange(new[] { mv[j], m, mv[j + 2], mv[j + 3], mv[j + 4] });
        }
        if (first == null) return new NeighbourEdgeAction(side, "", "", Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>());
        return new NeighbourEdgeAction(side, first.Name, first.Mayor, paths!.ToArray(), power!.ToArray(), pipes!.ToArray(), first.Stats, fire!.ToArray(), movers!.ToArray());
    }

    int SideLength(RegionTile a, int side) => a.City!.SideLength(side);

    /// <summary>
    /// Why a city of these tiles would crowd a side of itself or of a neighbour: a side of a city may border only one
    /// other city. <paramref name="who"/> is the city growing, or null for a new city of one tile.
    /// </summary>
    string? CrowdsASide(int col, int row, World? who, int cols = 1, int rows = 1)
    {
        // Its own sides.
        for (int side = 0; side < 4; side++)
        {
            var seen = new HashSet<World>();
            int n = side is 0 or 2 ? cols : rows;
            for (int k = 0; k < n; k++)
            {
                var o = side switch { 0 => TileAt(col + k, row - 1), 1 => TileAt(col + cols, row + k), 2 => TileAt(col + k, row + rows), _ => TileAt(col - 1, row + k) };
                if (o?.City is { } c && c != who) seen.Add(c);
            }
            if (seen.Count > 1) return $"Its {SideName(side)} side would border {string.Join(" and ", seen.Select(c => c.Name))}: a side of a city can border only one other city.";
        }
        // Its neighbours' sides: each that it touches would have it as well as anyone already there.
        for (int side = 0; side < 4; side++)
        {
            int n = side is 0 or 2 ? cols : rows;
            for (int k = 0; k < n; k++)
            {
                var o = side switch { 0 => TileAt(col + k, row - 1), 1 => TileAt(col + cols, row + k), 2 => TileAt(col + k, row + rows), _ => TileAt(col - 1, row + k) };
                if (o?.City is not { } c || c == who) continue;
                foreach (var other in AcrossSide(o.Anchor!, World.Opposite(side)))
                    if (other?.City is { } x && x != c && x != who && !InRect(other, col, row, cols, rows))
                        return $"{c.Name}'s {SideName(World.Opposite(side))} side already borders {x.Name}: a side of a city can border only one other city.";
            }
        }
        return null;
    }

    static bool InRect(RegionTile t, int col, int row, int cols, int rows) => t.Col >= col && t.Col < col + cols && t.Row >= row && t.Row < row + rows;

    static string SideName(int side) => side switch { 0 => "north", 1 => "east", 2 => "south", _ => "west" };

    /// <summary>
    /// The rectangle a city would cover if it took this tile: the least rectangle holding both. Taking a corner tile
    /// takes the tiles between too, since a city is always a rectangle.
    /// </summary>
    public (int Col, int Row, int Cols, int Rows) AnnexRect(RegionTile city, int col, int row)
    {
        var (c0, r0, cols, rows) = Footprint(city);
        int c1 = Math.Max(c0 + cols - 1, col), r1 = Math.Max(r0 + rows - 1, row);
        c0 = Math.Min(c0, col); r0 = Math.Min(r0, row);
        return (c0, r0, c1 - c0 + 1, r1 - r0 + 1);
    }

    /// <summary>What annexing that rectangle costs: each new tile, at a price rising with how big the city gets.</summary>
    public long AnnexPrice(RegionTile city, (int Col, int Row, int Cols, int Rows) to)
    {
        var (_, _, cols, rows) = Footprint(city);
        int added = to.Cols * to.Rows - cols * rows;
        // $25,000 a tile of 128 cells at two tiles, and more for every tile the city already has, by area.
        long perTile = 25_000L * TileSize * TileSize / (128 * 128);
        return added * perTile * (to.Cols * to.Rows) / 2;
    }

    /// <summary>Why this city can't annex that tile (and whatever else a rectangle needs), or null.</summary>
    public string? CantAnnex(RegionTile city, int col, int row)
    {
        if (city.City is not { } w) return "There's no city there.";
        var a = city.Anchor!;
        if (TileAt(col, row) is not { } target) return "There is no such tile in this region.";
        if (target.City == w) return $"That land is {w.Name}'s already.";
        var (c0, r0, cols, rows) = Footprint(a);
        // Only beside the city: a rectangle that would reach past a gap isn't annexing.
        bool beside = (col >= c0 && col < c0 + cols && (row == r0 - 1 || row == r0 + rows)) || (row >= r0 && row < r0 + rows && (col == c0 - 1 || col == c0 + cols))
            || ((col == c0 - 1 || col == c0 + cols) && (row == r0 - 1 || row == r0 + rows));
        if (!beside) return $"A city annexes only the land right beside it.";
        var to = AnnexRect(a, col, row);
        int max = MaxCitySide;
        if (to.Cols > max || to.Rows > max) return $"A city here can be at most {max} × {max} tiles.";
        for (int r = to.Row; r < to.Row + to.Rows; r++)
        for (int c = to.Col; c < to.Col + to.Cols; c++)
        {
            var t = TileAt(c, r)!;
            if (t.City == w) continue;
            if (t.Wild) return "Part of that land isn't in the region yet: add it to the region first.";
            if (t.Founded) return $"{t.Name} is there: a city takes in a neighbour by a deal, not by annexing.";
        }
        if (CrowdsASide(to.Col, to.Row, w, to.Cols, to.Rows) is { } crowd) return crowd;
        if (w.CantResize(to.Cols * TileSize, to.Rows * TileSize, (c0 - to.Col) * TileSize, (r0 - to.Row) * TileSize) is { } why) return why;
        long price = AnnexPrice(a, to);
        if (w.Funds < price) return $"{w.Name} needs ${price:N0} to annex that land, and has ${w.Funds:N0}.";
        return null;
    }

    /// <summary>
    /// The city takes on the open land that makes it the rectangle holding that tile, and pays for it. The city moves
    /// onto its bigger map whole; the new land's ground is the region's, meeting the city's own at the old edge.
    /// Returns the city as it is now: a new <see cref="World"/>, the old one spent (its side games went with it).
    /// </summary>
    public World Annex(RegionTile city, int col, int row)
    {
        if (CantAnnex(city, col, row) is { } why) throw new InvalidOperationException(why);
        var a = city.Anchor!;
        var old = a.City!;
        var (c0, r0, cols, rows) = Footprint(a);
        var to = AnnexRect(a, col, row);
        long price = AnnexPrice(a, to);
        int s = TileSize, dx = (c0 - to.Col) * s, dy = (r0 - to.Row) * s;
        var w = old.Resized(to.Cols * s, to.Rows * s, dx, dy);
        w.PayForLand(price, to.Cols, to.Rows);
        for (int r = to.Row; r < to.Row + to.Rows; r++)
        for (int c = to.Col; c < to.Col + to.Cols; c++)
        {
            var t = TileAt(c, r)!;
            if (t.City == old) continue;
            int x0 = (c - to.Col) * s, y0 = (r - to.Row) * s;
            var (bed, rock) = TileGeology(c, r);
            w.PlaceGround(x0, y0, s, TileCorners(c, r), TileRivers(c, r), bed, rock,
                (x, y) => x >= dx && x <= dx + cols * s && y >= dy && y <= dy + rows * s);
        }
        w.Settle();
        // The tiles: the city's old ones and the new, all pointing at the new world, counted from the new corner.
        var anchor = TileAt(to.Col, to.Row)!;
        int oldIndex = a.Row * Cols + a.Col, newIndex = anchor.Row * Cols + anchor.Col;
        for (int r = to.Row; r < to.Row + to.Rows; r++)
        for (int c = to.Col; c < to.Col + to.Cols; c++)
        {
            var t = TileAt(c, r)!;
            t.City = w;
            t.Anchor = anchor;
        }
        // Deals were made from the old corner tile.
        foreach (var d in Deals)
        {
            if (d.Seller == oldIndex) d.Seller = newIndex;
            if (d.Buyer == oldIndex) d.Buyer = newIndex;
        }
        old.Dispose();
        GroundChanged();
        return w;
    }
}
