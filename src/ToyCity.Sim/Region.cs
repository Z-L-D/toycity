using ToyCity.Contract;

namespace ToyCity.Sim;

/// <summary>One square of a region: open land until someone founds a city on it.</summary>
public sealed class RegionTile
{
    public int Col, Row;
    /// <summary>The tile's own seed, from the region's: its city's randomness starts here.</summary>
    public ulong Seed;
    /// <summary>The city on this tile, or null while it is open land.</summary>
    public World? City;

    public bool Founded => City != null;
    public string Name => City?.Name ?? "Open land";
    /// <summary>Who holds the mayor's seat; empty while the tile is open land.</summary>
    public string Mayor => City?.MayorSeat.Owner ?? "";
}

/// <summary>
/// Modern SC4 regions: a grid of city tiles on one map and one clock. The ground is made once for the whole region
/// and cut into tiles, so edges meet. Every founded city steps one day together; none is ever ahead of another.
/// </summary>
public sealed partial class Region : IDisposable
{
    public const int MaxSide = 8;

    public ContentDb Content { get; }
    public string Name { get; set; }
    public ulong Seed { get; }
    public int Cols { get; }
    public int Rows { get; }
    /// <summary>Cells along a tile's side.</summary>
    public int TileSize { get; }
    /// <summary>The region's day. Every founded city's <see cref="World.Tick"/> equals it between steps.</summary>
    public long Tick { get; private set; }
    public RegionTile[] Tiles { get; }

    byte[]? _heights;

    Region(ContentDb content, string name, ulong seed, int cols, int rows, int tileSize, long tick)
    {
        if (cols is < 1 or > MaxSide || rows is < 1 or > MaxSide) throw new ArgumentOutOfRangeException(nameof(cols), $"a region is 1 to {MaxSide} tiles a side");
        if (tileSize is not (64 or 128 or 256)) throw new ArgumentOutOfRangeException(nameof(tileSize), "tiles are 64, 128 or 256 cells");
        Content = content;
        Name = name;
        Seed = seed;
        Cols = cols;
        Rows = rows;
        TileSize = tileSize;
        Tick = tick;
        Tiles = new RegionTile[cols * rows];
        for (int r = 0; r < rows; r++)
        for (int c = 0; c < cols; c++)
            Tiles[r * cols + c] = new RegionTile { Col = c, Row = r, Seed = TileSeed(seed, c, r) };
    }

    /// <summary>A new region of open land. Found cities on it with <see cref="Found"/>.</summary>
    public static Region Create(ContentDb content, string name, ulong seed, int cols = 3, int rows = 3, int tileSize = 128)
        => new(content, name, seed, cols, rows, tileSize, 0);

    /// <summary>
    /// A region of one tile holding an existing city, so a lone city (an old save, a de-link copy, the benchmark)
    /// plays the same way a region does.
    /// </summary>
    public static Region Single(World city)
    {
        var r = new Region(city.Content, city.Name, city.Seed, 1, 1, city.Width, city.Tick);
        r.Tiles[0].Seed = city.Seed;
        r.Tiles[0].City = city;
        return r;
    }

    /// <summary>Puts back a region from its parts: a save, or a region host rebuilding after a load.</summary>
    public static Region Restore(ContentDb content, string name, ulong seed, int cols, int rows, int tileSize, long tick, IEnumerable<World> cities)
    {
        var r = new Region(content, name, seed, cols, rows, tileSize, tick);
        foreach (var w in cities)
        {
            var t = r.TileOf(w) ?? throw new InvalidDataException($"{w.Name} isn't on a tile of {name}");
            if (w.Tick != tick) throw new InvalidDataException($"{w.Name} is on day {w.Tick}, the region on day {tick}");
            t.City = w;
        }
        r.RestoreDeals();
        return r;
    }

    static ulong TileSeed(ulong seed, int col, int row)
    {
        ulong h = seed ^ ((ulong)(uint)col * 0x9E3779B185EBCA87UL) ^ ((ulong)(uint)row * 0xC2B2AE3D27D4EB4FUL) ^ 0x5851F42D4C957F2DUL;
        h ^= h >> 33; h *= 0xFF51AFD7ED558CCDUL; h ^= h >> 33;
        return h == 0 ? 1 : h;
    }

    public RegionTile? TileAt(int col, int row) => col >= 0 && row >= 0 && col < Cols && row < Rows ? Tiles[row * Cols + col] : null;

    /// <summary>The tile a city stands on, found by its seed (each tile's is different).</summary>
    public RegionTile? TileOf(World w) => Tiles.FirstOrDefault(t => t.City == w) ?? Tiles.FirstOrDefault(t => t.Seed == w.Seed);

    public IEnumerable<World> Cities => Tiles.Where(t => t.City != null).Select(t => t.City!);

    /// <summary>The whole region's ground, row-major over Cols * TileSize by Rows * TileSize cells. Made on first use.</summary>
    public byte[] Heights => _heights ??= World.GenerateHeights(Seed, Cols * TileSize, Rows * TileSize);

    /// <summary>The ground of one tile, cut from the region's.</summary>
    public byte[] TileHeights(int col, int row)
    {
        int s = TileSize, wide = Cols * s;
        var all = Heights;
        var h = new byte[s * s];
        for (int y = 0; y < s; y++) Array.Copy(all, (row * s + y) * wide + col * s, h, y * s, s);
        return h;
    }

    /// <summary>Why a city can't be founded on this tile, or null.</summary>
    public string? CantFound(int col, int row)
    {
        var t = TileAt(col, row);
        if (t == null) return "There is no such tile in this region.";
        if (t.Founded) return $"{t.Name} is already there.";
        return null;
    }

    /// <summary>
    /// Founds a city on open land, today, with <paramref name="mayor"/> in its mayor's seat. A starter town is laid
    /// out by player actions, as a lone city's is, so the city's log replays it. With <paramref name="computer"/> the
    /// mayor is a computer of that temperament, on autopilot from the first day.
    /// </summary>
    public World Found(int col, int row, string name, string mayor, bool starter = false, AutopilotStyle? computer = null)
    {
        if (CantFound(col, row) is { } why) throw new InvalidOperationException(why);
        var t = TileAt(col, row)!;
        if (computer is { } style) mayor = World.ComputerName(style, t.Seed);
        var w = World.CreateTile(Content, t.Seed, TileSize, name, TileHeights(col, row), Tick);
        // The seat changes hands by a logged action, so a replay of the tile puts the same mayor in it.
        if (mayor != w.MayorSeat.Owner && mayor.Length > 0) w.Enqueue(new RenameOwnerAction(w.MayorSeat.Owner, mayor));
        if (starter && StarterTown.FindSite(w) is { } site)
            foreach (var a in StarterTown.Build(site.X, site.Y)) w.Enqueue(a with { By = mayor });
        if (computer is { } st) w.Enqueue(new SetSeatAction(Seat.Mayor, mayor, OperatorLevel.Autopilot, st) { By = mayor });
        w.ApplyPending();
        t.City = w;
        return w;
    }

    /// <summary>Plays a tile's city again from its founding, for desync checks: the same ground, seed and log.</summary>
    public World ReplayTile(RegionTile t, long untilTick)
    {
        var w = t.City ?? throw new InvalidOperationException("nothing to replay on open land");
        var fresh = World.CreateTile(Content, t.Seed, TileSize, w.Name, TileHeights(t.Col, t.Row), w.FoundedTick);
        return World.Replay(fresh, w.Log, untilTick);
    }

    /// <summary>
    /// One day for the whole region. Cities step side by side: each touches only its own state, so the order they run
    /// in can't change the result.
    /// </summary>
    public void Step()
    {
        BeginDay();
        StepCities(Cities.ToArray());
        EndDay();
    }

    /// <summary>
    /// A follower holds only the tiles its host runs, and hears of the rest from the region's host: it doesn't work out
    /// borders, deals or weather itself (part 8). Those arrive as actions from the region's host.
    /// </summary>
    public bool Follower { get; set; }

    /// <summary>
    /// True for a city another host runs (part 8): the region keeps a check copy of it, which must change only as the
    /// city's own host reports. Actions the region gives it wait for the next day's message instead of applying now.
    /// </summary>
    public Func<World, bool>? IsRemote { get; set; }

    /// <summary>Applies what the region just gave a city at once, unless another host runs it.</summary>
    public void ApplyNow(World w)
    {
        if (IsRemote?.Invoke(w) != true) w.ApplyPending();
    }

    /// <summary>The first half of a day: what each city hears from the region before it steps.</summary>
    public void BeginDay()
    {
        if (!Follower) ExchangeBorders();
    }

    /// <summary>Steps these cities one day, side by side.</summary>
    public static void StepCities(IReadOnlyList<World> cities)
    {
        if (cities.Count > 1) Parallel.ForEach(cities, c => c.Step());
        else foreach (var c in cities) c.Step();
    }

    /// <summary>The day is over for every city: the region's day moves on.</summary>
    public void EndDay() => Tick++;

    /// <summary>The tile across a side of this one (0 north, 1 east, 2 south, 3 west), or null past the region's edge.</summary>
    public RegionTile? Across(RegionTile t, int side) => side switch
    {
        0 => TileAt(t.Col, t.Row - 1),
        1 => TileAt(t.Col + 1, t.Row),
        2 => TileAt(t.Col, t.Row + 1),
        _ => TileAt(t.Col - 1, t.Row),
    };

    /// <summary>
    /// Tells each city what its neighbours show across their shared edges, as they stand now: before anyone steps, so
    /// every city reads the others' previous day whatever order they run in. Sent as logged actions, only on change.
    /// Ends any deal that can't go on, in both cities the same day.
    /// </summary>
    public void ExchangeBorders()
    {
        CheckDeals();
        SendWeather();
        var sends = new List<(World, NeighbourEdgeAction)>();
        foreach (var t in Tiles)
        {
            if (t.City is not { } w) continue;
            for (int side = 0; side < 4; side++)
            {
                var a = Across(t, side)?.City is { } n ? n.EdgeOut(World.Opposite(side)) : new NeighbourEdgeAction(side, "", "", Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>());
                if (w.EdgeDiffers(a)) sends.Add((w, a));
            }
        }
        foreach (var (w, a) in sends) w.Enqueue(a);
    }

    public void Run(int days)
    {
        for (int i = 0; i < days; i++) Step();
    }

    /// <summary>Hash of every city in tile order, with the region's day.</summary>
    public ulong StateHash()
    {
        ulong h = (ulong)Tick * 0x9E3779B97F4A7C15UL;
        foreach (var t in Tiles)
            h = (h ^ (t.City?.StateHash() ?? 0)) * 0x100000001B3UL + (ulong)(t.Row * Cols + t.Col);
        return h;
    }

    public void Dispose()
    {
        foreach (var c in Cities) c.Dispose();
    }
}
