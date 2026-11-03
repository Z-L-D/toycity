namespace ToyCity.Sim;

/// <summary>How far a city's sewage plants treat what they take before it runs out into the water.</summary>
public enum Treatment : byte { Raw = 0, Primary = 1, Secondary = 2, Tertiary = 3 }

/// <summary>The mayor sets how far the sewage plants treat the sewage they take (Beta 4, part 3).</summary>
public sealed record SetTreatmentAction(Treatment Level) : SimAction;

/// <summary>
/// The region tells a city how foul the water is in a river where it comes in across a border (Beta 4, part 3).
/// X and Y are the city's own cell; Foul is 0 (clean) to 255. Logged, so a city's log replays it alone.
/// </summary>
public sealed record WaterInflowAction(int X, int Y, int Foul) : SimAction;

/// <summary>
/// Beta 4, part 3: water quality. What the city puts in its water stays in it and goes where the water goes. Raw
/// sewage, industry and landfill near the water foul it, and so does what the sewage plants let out, by how far the
/// mayor has them treat it. Rivers carry it downstream, over borders when the region lets them, and lakes and the sea
/// spread it about. Pumps drink what's around them, so foul water upstream reaches the taps unless a water treatment
/// works on the mains cleans it. Foul water smells, keeps visitors away and puts people off living by it, and foul
/// taps make people ill. None of it is forbidden: a mayor may dump raw sewage in the river to save money, and the
/// town downstream will hear of it.
/// </summary>
public sealed partial class World
{
    /// <summary>Each water cell's foulness, 0 clean to 255, worked out monthly. Saved.</summary>
    public byte[] Foul = Array.Empty<byte>();

    /// <summary>Each cell's tap water foulness, 0 clean to 255, from the pumps and treatment works on its mains. Saved.</summary>
    public byte[] TapFoul = Array.Empty<byte>();

    /// <summary>The smell of foul water over the land beside it, 0 to 255. Worked out with <see cref="Foul"/>, not saved.</summary>
    public byte[] Stench => _stench ??= MakeStench();
    byte[]? _stench;

    /// <summary>How far the sewage plants treat sewage; secondary unless the mayor says otherwise. Saved.</summary>
    public Treatment SewageTreatment { get; private set; } = Treatment.Secondary;

    /// <summary>The foulness coming in across a border at each cell where a river enters, from the region. Saved.</summary>
    public readonly SortedDictionary<int, byte> Inflow = new();

    /// <summary>Water cells, and those of them foul enough to smell (over <see cref="SmellsAt"/>), at the last count.</summary>
    public int WaterCells, FoulCells;

    /// <summary>The worst water in the city and the worst tap water, 0 to 255.</summary>
    public int WorstFoul, WorstTap;

    /// <summary>Foulness at which water smells, kills fish and stops being fit to swim in.</summary>
    public const int SmellsAt = 60;
    /// <summary>Tap water foulness at which the city must tell people to boil it.</summary>
    public const int BoilAt = 60;
    /// <summary>Tap water foulness at which people fall ill from it.</summary>
    public const int SickAt = 140;

    /// <summary>The share of what a plant takes that it lets out untreated at each level, in percent.</summary>
    public static readonly int[] TreatmentResidualPct = { 100, 50, 15, 3 };
    /// <summary>A sewage plant's upkeep at each level, in percent of its usual.</summary>
    public static readonly int[] TreatmentUpkeepPct = { 25, 60, 100, 160 };
    public static readonly string[] TreatmentNames = { "None (raw sewage out)", "Primary", "Secondary", "Tertiary" };

    /// <summary>Sewage a day that fouls a cell of water fully, at the plant's outfall, before treatment.</summary>
    const int OutfallPerFoul = 30;
    /// <summary>How far, in cells, a plant looks for water to let out into.</summary>
    const int OutfallReach = 16;
    /// <summary>How far, in cells, a pump draws water from the river or lake beside it.</summary>
    const int IntakeReach = 4;

    bool _boilNotified, _sickNotified, _fishNotified;
    long[]? _sewerMade, _sewerCap;

    void ApplyTreatment(SetTreatmentAction a)
    {
        var was = SewageTreatment;
        SewageTreatment = a.Level;
        if (a.Level == Treatment.Raw && was != Treatment.Raw && Buildings.Any(b => b != null && TypeOf(b).SewageCap > 0))
            AddHeadline($"{Name}'s sewage plants stop treating: the sewage goes into the water raw.", "warning");
    }

    void ApplyInflow(WaterInflowAction a)
    {
        int i = Idx(a.X, a.Y);
        if (a.Foul <= 0) Inflow.Remove(i);
        else Inflow[i] = (byte)Math.Min(255, a.Foul);
    }

    /// <summary>A cell's water surface in steps: a river's own, or the sea and lakes' level.</summary>
    int Surface(int i) => IsRiver(i) ? River[i] : WaterLevel;

    /// <summary>
    /// Monthly, after the water pollution is spread: what each sewage plant lets out at its outfall, then the water's
    /// foulness everywhere, carried downstream and spread through lakes and the sea.
    /// </summary>
    internal void RunWaterQuality()
    {
        if (Foul.Length != N) Foul = new byte[N];
        var src = new float[N];
        for (int i = 0; i < N; i++) if (IsWater(i)) src[i] = WaterPollution[i];
        foreach (var (i, v) in Inflow) if (i < N && IsWater(i)) src[i] = Math.Max(src[i], v);

        // Each plant's outfall: what it treats, less what treatment takes out, into the nearest water.
        if (_sewerComp != null && _sewerMade != null && _sewerCap != null)
            foreach (var b in Buildings)
            {
                if (b == null || !b.Operating || !b.Powered || TypeOf(b).SewageCap == 0) continue;
                int g = PlantSewer(b);
                if (g < 0 || g >= _sewerCap.Length || _sewerCap[g] <= 0) continue;
                long cap = TypeOf(b).SewageCap * FundingFactor(b.Funding) / 100;
                long treated = cap * Math.Min(_sewerMade[g], _sewerCap[g]) / _sewerCap[g];
                int value = (int)Math.Min(255, treated * TreatmentResidualPct[(int)SewageTreatment] / 100 / OutfallPerFoul);
                if (value <= 0) continue;
                int at = NearestWater(b);
                if (at >= 0) src[at] = Math.Min(255, src[at] + value);
                // No water near: it soaks into the ground around the plant.
                else Spread(WaterPollution, b.X, b.Y, b.W, b.H, value / 3, 3, false);
            }

        var conc = Carry(src);
        WaterCells = FoulCells = WorstFoul = 0;
        for (int i = 0; i < N; i++)
        {
            Foul[i] = (byte)Math.Clamp((int)MathF.Round(conc[i]), 0, 255);
            if (!IsWater(i)) continue;
            WaterCells++;
            if (Foul[i] > SmellsAt) FoulCells++;
            WorstFoul = Math.Max(WorstFoul, Foul[i]);
        }
        _stench = null;

        if (WorstFoul > 180 && !_fishNotified && Age > 0)
        {
            int at = Array.IndexOf(Foul, (byte)WorstFoul);
            AddHeadline($"Dead fish float in the water at {Name}: it's too foul for anything to live in.", "warning", at % Width, at / Width);
        }
        _fishNotified = WorstFoul > 180;
    }

    /// <summary>
    /// Carries the foulness from where it goes in: down a river at little loss a cell, along a level stretch towards
    /// where it next steps down, and through still water losing more as it spreads. The foulness of a cell is the most
    /// any source upstream of it brings, so it's worked out like a shortest path, the foulest first.
    /// </summary>
    internal float[] Carry(float[] src)
    {
        // Along a level stretch of river, the steps to where it next drops: which way is downstream on the flat.
        var toDrop = new int[N];
        Array.Fill(toDrop, int.MaxValue);
        var q = new Queue<int>();
        for (int i = 0; i < N; i++)
        {
            if (!IsRiver(i)) continue;
            int x = i % Width, y = i / Width, s = River[i];
            bool drops = false, edge = x == 0 || y == 0 || x == Width - 1 || y == HeightCells - 1;
            for (int k = 0; k < 4 && !drops; k++)
            {
                int nx = x + Dx4[k], ny = y + Dy4[k];
                if (InBounds(nx, ny) && IsWater(Idx(nx, ny)) && Surface(Idx(nx, ny)) < s) drops = true;
            }
            if (drops || edge) { toDrop[i] = 0; q.Enqueue(i); }
        }
        while (q.Count > 0)
        {
            int i = q.Dequeue();
            int x = i % Width, y = i / Width;
            for (int k = 0; k < 4; k++)
            {
                int nx = x + Dx4[k], ny = y + Dy4[k];
                if (!InBounds(nx, ny)) continue;
                int j = Idx(nx, ny);
                if (!IsRiver(j) || River[j] != River[i] || toDrop[j] <= toDrop[i] + 1) continue;
                toDrop[j] = toDrop[i] + 1;
                q.Enqueue(j);
            }
        }

        var conc = new float[N];
        var heap = new PriorityQueue<int, (float, int)>();
        for (int i = 0; i < N; i++)
            if (src[i] > 0) { conc[i] = src[i]; heap.Enqueue(i, (-src[i], i)); }
        while (heap.TryDequeue(out int i, out var pri))
        {
            if (-pri.Item1 < conc[i]) continue;
            int x = i % Width, y = i / Width, s = Surface(i);
            bool river = IsRiver(i);
            for (int k = 0; k < 4; k++)
            {
                int nx = x + Dx4[k], ny = y + Dy4[k];
                if (!InBounds(nx, ny)) continue;
                int j = Idx(nx, ny);
                if (!IsWater(j)) continue;
                int t = Surface(j);
                if (t > s) continue;
                float f;
                if (t < s) f = 0.97f;                  // down a step
                else if (!river || !IsRiver(j)) f = 0.88f;  // through still water
                else f = toDrop[j] < toDrop[i] ? 0.97f : toDrop[j] == toDrop[i] ? 0.9f : 0.6f;  // along the flat, mostly downstream
                float v = conc[i] * f;
                if (v < 1 || v <= conc[j]) continue;
                conc[j] = v;
                heap.Enqueue(j, (-v, j));
            }
        }
        return conc;
    }

    /// <summary>The sewer network a plant stands on, as <see cref="RunSewage"/> found it; -1 for none.</summary>
    int PlantSewer(Building b)
    {
        for (int y = b.Y - 1; y <= b.Y + b.H; y++)
        for (int x = b.X - 1; x <= b.X + b.W; x++)
            if (InBounds(x, y) && _sewerComp![Idx(x, y)] >= 0) return _sewerComp[Idx(x, y)];
        return -1;
    }

    /// <summary>The nearest water cell to a footprint within <see cref="OutfallReach"/>, nearest first then in index order; -1 for none.</summary>
    internal int NearestWater(Building b)
    {
        int best = -1, bestD = int.MaxValue;
        int x0 = Math.Max(0, b.X - OutfallReach), x1 = Math.Min(Width - 1, b.X + b.W - 1 + OutfallReach);
        int y0 = Math.Max(0, b.Y - OutfallReach), y1 = Math.Min(HeightCells - 1, b.Y + b.H - 1 + OutfallReach);
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            int i = Idx(x, y);
            if (!IsWater(i)) continue;
            int dx = Math.Max(0, Math.Max(b.X - x, x - (b.X + b.W - 1))), dy = Math.Max(0, Math.Max(b.Y - y, y - (b.Y + b.H - 1)));
            int d = dx * dx + dy * dy;
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    /// <summary>How foul the water a pump draws is: the worst of the river or lake beside it and the ground it stands on.</summary>
    public int IntakeFoul(Building b)
    {
        int worst = WaterPollution[Idx(b.X, b.Y)];
        if (Foul.Length != N) return worst;
        int x0 = Math.Max(0, b.X - IntakeReach), x1 = Math.Min(Width - 1, b.X + b.W - 1 + IntakeReach);
        int y0 = Math.Max(0, b.Y - IntakeReach), y1 = Math.Min(HeightCells - 1, b.Y + b.H - 1 + IntakeReach);
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
            worst = Math.Max(worst, Foul[Idx(x, y)]);
        return worst;
    }

    /// <summary>
    /// Daily, with the water mains: each network's water is what its pumps draw, by how much each pumps, cleaned by the
    /// treatment works on it as far as they keep up with what's used.
    /// </summary>
    void RunTapWater(int[] pipeComp, int groups, long[] used)
    {
        if (TapFoul.Length != N) TapFoul = new byte[N];
        var drawn = new long[groups];
        var weight = new long[groups];
        var treat = new long[groups];
        foreach (var b in Buildings)
        {
            if (b == null || !b.Operating || !b.Powered) continue;
            var t = TypeOf(b);
            if (t.WaterOut == 0 && t.WaterTreat == 0) continue;
            int g = -1;
            for (int y = b.Y - 1; y <= b.Y + b.H && g < 0; y++)
            for (int x = b.X - 1; x <= b.X + b.W && g < 0; x++)
                if (InBounds(x, y) && pipeComp[Idx(x, y)] >= 0) g = pipeComp[Idx(x, y)];
            if (g < 0) continue;
            if (t.WaterOut > 0)
            {
                long out_ = PumpOutput(b);
                drawn[g] += out_ * IntakeFoul(b);
                weight[g] += out_;
            }
            if (t.WaterTreat > 0) treat[g] += t.WaterTreat * FundingFactor(b.Funding) / 100;
        }
        var tap = new int[groups];
        for (int g = 0; g < groups; g++)
        {
            if (weight[g] == 0) continue;
            long raw = drawn[g] / weight[g];
            long cleaned = used[g] <= 0 ? (treat[g] > 0 ? 100 : 0) : Math.Min(100, treat[g] * 100 / used[g]);
            tap[g] = (int)(raw * (100 - cleaned * 95 / 100) / 100);
        }
        WorstTap = 0;
        for (int i = 0; i < N; i++)
        {
            int g = _waterComp![i];
            int v = g >= 0 && g < groups ? tap[g] : 0;
            TapFoul[i] = (byte)v;
            if (v > WorstTap && ZoneCode[i] != 0) WorstTap = v;
        }

        if (WorstTap > BoilAt && !_boilNotified && Age > 0)
        {
            Notices.Add(new SimNotice($"Boil-water notice: the pumps draw foul water, and it reaches the taps. Build a water treatment works on the mains, or move the pumps away from it.", "warning"));
            AddHeadline($"Boil your water, {Name} is told: what comes out of the taps isn't fit to drink.", "warning");
        }
        _boilNotified = WorstTap > BoilAt;
        if (WorstTap > SickAt && !_sickNotified && Age > 0)
            AddHeadline($"Sickness in {Name}: people are falling ill from the tap water, and the clinics are full.", "danger");
        _sickNotified = WorstTap > SickAt;
    }

    byte[] MakeStench()
    {
        var s = new byte[N];
        if (Foul.Length != N) return s;
        for (int i = 0; i < N; i++)
            if (Foul[i] > SmellsAt && IsWater(i)) Spread(s, i % Width, i / Width, 1, 1, (Foul[i] - SmellsAt) / 2, 3, true);
        return s;
    }

    /// <summary>What foul water costs a home: its smell, and the tap water when it's not fit to drink.</summary>
    int StenchPenalty(int i) => Stench[i] / 5;
    int TapPenalty(int i) => TapFoul.Length == N && TapFoul[i] > 30 ? (TapFoul[i] - 30) / 3 : 0;

    /// <summary>Visitors keep away from a city whose water is foul: up to 40% fewer.</summary>
    int WaterTourismPct => WaterCells == 0 ? 100 : 100 - Math.Min(40, FoulCells * 100 / WaterCells / 2);

    /// <summary>What the sewage plants' upkeep comes to at the treatment level, in percent.</summary>
    int TreatmentUpkeep => TreatmentUpkeepPct[(int)SewageTreatment];

    /// <summary>A line about the water for the advisors and the water panel.</summary>
    public string WaterQualityText()
    {
        var parts = new List<string>();
        if (WaterCells > 0)
            parts.Add(FoulCells == 0 ? "The water is clean"
                : FoulCells * 100 < WaterCells ? $"{FoulCells:N0} cells of water are foul"
                : $"{FoulCells * 100 / WaterCells}% of the water is foul");
        if (WorstTap > SickAt) parts.Add("the tap water is making people ill");
        else if (WorstTap > BoilAt) parts.Add("the tap water must be boiled");
        else if (WorstTap > 30) parts.Add("the tap water tastes off");
        parts.Add($"sewage treatment: {TreatmentNames[(int)SewageTreatment].ToLowerInvariant()}");
        return string.Join(", ", parts) + ".";
    }

    void WriteWaterQuality(BinaryWriter w)
    {
        w.Write((byte)SewageTreatment);
        WC(w, Foul.Length == N ? Foul : new byte[N]);
        WC(w, TapFoul.Length == N ? TapFoul : new byte[N]);
        // Rivers coming in across a side: the region tells them again on a bigger map.
        var inflow = _shift == null ? Inflow : new SortedDictionary<int, byte>();
        w.Write(inflow.Count);
        foreach (var (i, v) in inflow) { w.Write(i); w.Write(v); }
    }

    void ReadWaterQuality(BinaryReader r, int version)
    {
        Foul = new byte[N];
        TapFoul = new byte[N];
        if (version >= 16)
        {
            SewageTreatment = (Treatment)Math.Min(3, (int)r.ReadByte());
            ReadBytes(r, Foul);
            ReadBytes(r, TapFoul);
            int n = r.ReadInt32();
            for (int k = 0; k < n; k++) { int i = r.ReadInt32(); Inflow[i] = r.ReadByte(); }
        }
        _stench = null;
        WaterCells = FoulCells = WorstFoul = WorstTap = 0;
        for (int i = 0; i < N; i++)
        {
            if (TapFoul[i] > WorstTap && ZoneCode[i] != 0) WorstTap = TapFoul[i];
            if (!IsWater(i)) continue;
            WaterCells++;
            if (Foul[i] > SmellsAt) FoulCells++;
            WorstFoul = Math.Max(WorstFoul, Foul[i]);
        }
        _boilNotified = WorstTap > BoilAt;
        _sickNotified = WorstTap > SickAt;
        _fishNotified = WorstFoul > 180;
    }
}

public sealed partial class Region
{
    /// <summary>
    /// Whether foul water runs on from one city into the next down a river (Beta 4, part 3). On unless the region's
    /// host turns it off, when each city's water is its own.
    /// </summary>
    public bool WaterCrossesBorders { get; set; } = true;

    /// <summary>Each place a river runs over a border: the tile and cell it leaves, and the tile and cell it comes in at.</summary>
    List<(int From, int FromCell, int To, int ToCell)>? _waterExits;

    /// <summary>A cell of a tile, as a cell of the city on it: a city of several tiles holds each at its place.</summary>
    public (World City, int Cell) CityCell(RegionTile t, int tileCell)
    {
        var w = t.City!;
        var a = t.Anchor ?? t;
        int s = TileSize, x = tileCell % s + (t.Col - a.Col) * s, y = tileCell / s + (t.Row - a.Row) * s;
        return (w, y * w.Width + x);
    }

    List<(int From, int FromCell, int To, int ToCell)> WaterExits()
    {
        if (_waterExits != null) return _waterExits;
        var g = Ground;
        int wide = BoxCols * TileSize, s = TileSize;
        var exits = new List<(int, int, int, int)>();
        for (int i = 0; i < g.Rivers.Length; i++)
        {
            int d = g.Downstream[i];
            if (g.Rivers[i] == 0 || d < 0) continue;
            int a = (BoxRow + i / wide / s) * Cols + BoxCol + i % wide / s, b = (BoxRow + d / wide / s) * Cols + BoxCol + d % wide / s;
            if (a == b) continue;
            exits.Add((a, i / wide % s * s + i % wide % s, b, d / wide % s * s + d % wide % s));
        }
        return _waterExits = exits;
    }

    /// <summary>
    /// Tells each city how foul the rivers coming into it are, from the city upstream as it stood yesterday: only on a
    /// change, so it's quiet while nothing does. Nothing comes in when the region keeps each city's water its own.
    /// </summary>
    void SendWater()
    {
        if (Tiles.Length < 2) return;
        var coming = new SortedDictionary<(int Tile, int Cell), int>();
        foreach (var (from, fromTileCell, to, toTileCell) in WaterExits())
        {
            if (Tiles[to].City is not { } dest) continue;
            // A river running between two tiles of one city stays inside it.
            if (Tiles[from].City == dest) continue;
            var (_, toCell) = CityCell(Tiles[to], toTileCell);
            int foul = 0;
            if (WaterCrossesBorders && Tiles[from].City is { } src && src.Foul.Length == src.N) foul = src.Foul[CityCell(Tiles[from], fromTileCell).Cell];
            int at = Tiles[to].Anchor!.Row * Cols + Tiles[to].Anchor!.Col;
            coming[(at, toCell)] = Math.Max(coming.GetValueOrDefault((at, toCell)), foul);
        }
        foreach (var ((tile, cell), foul) in coming)
        {
            var w = Tiles[tile].City!;
            int now = w.Inflow.GetValueOrDefault(cell);
            if (foul == now || foul > 0 && Math.Abs(foul - now) <= 2) continue;
            w.Enqueue(new WaterInflowAction(cell % w.Width, cell / w.Width, foul));
        }
    }
}

public sealed partial class World
{
    /// <summary>A word for how foul water is.</summary>
    public static string FoulWord(int foul) => foul switch
    {
        <= 10 => "clean",
        <= 30 => "a little cloudy",
        <= SmellsAt => "murky",
        <= 120 => "foul",
        <= 180 => "very foul",
        _ => "dead: nothing lives in it",
    };

    string WaterHere(int i)
    {
        int f = Foul.Length == N ? Foul[i] : 0;
        string from = Inflow.ContainsKey(i) ? " It comes in from the city upstream." : "";
        return $"The water here is {FoulWord(f)} ({f * 100 / 255}% foul).{from}";
    }

    string TapHere(int i)
    {
        int f = TapFoul.Length == N ? TapFoul[i] : 0;
        return f > SickAt ? "The tap water here is making people ill."
            : f > BoilAt ? "The tap water here must be boiled."
            : f > 30 ? "The tap water here tastes off."
            : "The tap water here is fine to drink.";
    }
}
