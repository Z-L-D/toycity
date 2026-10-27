namespace ToyCity.Sim;

public sealed partial class World
{
    static int ISqrt(int v) => (int)Math.Sqrt(v);

    /// <summary>Distance in cells from (x,y) to the nearest cell of a footprint.</summary>
    static int DistToRect(int x, int y, int rx, int ry, int rw, int rh)
    {
        int dx = Math.Max(0, Math.Max(rx - x, x - (rx + rw - 1)));
        int dy = Math.Max(0, Math.Max(ry - y, y - (ry + rh - 1)));
        return ISqrt(dx * dx + dy * dy);
    }

    /// <summary>Spreads a value from a footprint with linear falloff. Additive, or max-combined for coverage.</summary>
    void Spread(byte[] layer, int rx, int ry, int rw, int rh, int value, int radius, bool useMax)
    {
        if (value <= 0 || radius < 0) return;
        int x0 = Math.Max(0, rx - radius), x1 = Math.Min(Width - 1, rx + rw - 1 + radius);
        int y0 = Math.Max(0, ry - radius), y1 = Math.Min(HeightCells - 1, ry + rh - 1 + radius);
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            int d = DistToRect(x, y, rx, ry, rw, rh);
            if (d > radius) continue;
            int v = value * (radius + 1 - d) / (radius + 1);
            int i = Idx(x, y);
            layer[i] = (byte)Math.Min(255, useMax ? Math.Max(layer[i], v) : layer[i] + v);
        }
    }

    void RunPollutionAndNoise()
    {
        Array.Clear(Pollution);
        Array.Clear(WaterPollution);
        Array.Clear(Noise);
        foreach (var b in Buildings)
        {
            if (b == null || !b.Operating) continue;
            var t = TypeOf(b);
            if (t.Pollution > 0) Spread(Pollution, b.X, b.Y, b.W, b.H, t.Pollution, 2 + t.Pollution / 10, false);
            if (t.WaterPollution > 0) Spread(WaterPollution, b.X, b.Y, b.W, b.H, t.WaterPollution, 1 + t.WaterPollution / 12, false);
            if (t.Noise > 0) Spread(Noise, b.X, b.Y, b.W, b.H, t.Noise, 2, false);
        }
        for (int i = 0; i < N; i++)
        {
            var p = ProfileAt(i);
            if (p == null) continue;
            int x = i % Width, y = i / Width;
            int noise = Math.Max(0, p.NoiseDb - 40) * 2 + Traffic[i] / 4;
            Spread(Noise, x, y, 1, 1, noise, 2, true); // paths don't stack: a grid is as loud as its loudest road
            if (p.Cars && Traffic[i] > 16) Spread(Pollution, x, y, 1, 1, Traffic[i] / 8, 1, false);
        }
    }

    public static Service? ServiceOf(Category c) => c switch
    {
        Category.Police => Service.Police,
        Category.Fire => Service.Fire,
        Category.Health => Service.Health,
        Category.Education => Service.Education,
        Category.Park => Service.Parks,
        _ => null,
    };

    /// <summary>Coverage radius after funding: radius times the square root of funding.</summary>
    public int EffectiveRadius(Building b) => (int)(TypeOf(b).Radius * Math.Sqrt(b.Funding / 100.0));

    void RunCoverage()
    {
        foreach (var c in Coverage) Array.Clear(c);
        foreach (var b in Buildings)
        {
            if (b == null || !b.Operating) continue;
            var t = TypeOf(b);
            if (ServiceOf(t.Category) is not { } s || t.Radius == 0) continue;
            if (!b.Powered) continue; // an unpowered station does nothing
            if (b.Funding == 0) continue;
            Spread(Coverage[(int)s], b.X, b.Y, b.W, b.H, 255, EffectiveRadius(b), true);
        }
    }

    /// <summary>Land value of one cell, with an optional breakdown.</summary>
    public int LandValueOf(int i, List<Factor>? f)
    {
        if (IsWater(i)) return 0;
        int total = 0;
        void Add(string label, int v) { if (v == 0) return; total += v; f?.Add(new Factor(label, v)); }
        Add("Base", 40);
        Add("Elevation", Math.Min(20, (Height[i] - WaterLevel) * 2));
        if (WaterDistance[i] <= 4) Add("Near water", (5 - WaterDistance[i]) * 6);
        Add("Parks", Coverage[(int)Service.Parks][i] / 4);
        Add("Police", Coverage[(int)Service.Police][i] / 12);
        Add("Health care", Coverage[(int)Service.Health][i] / 12);
        Add("Schools", Coverage[(int)Service.Education][i] / 12);
        Add("Pollution", -Pollution[i] / 2);
        Add("Noise", -Noise[i] / 4);
        return Math.Clamp(total, 0, 255);
    }

    void RunLandValue()
    {
        for (int i = 0; i < N; i++) LandValue[i] = (byte)LandValueOf(i, null);
    }

    int NeighbourTraffic(int i)
    {
        int x = i % Width, y = i / Width, best = 0;
        for (int k = 0; k < 4; k++)
        {
            int nx = x + Dx4[k], ny = y + Dy4[k];
            if (InBounds(nx, ny)) best = Math.Max(best, Traffic[Idx(nx, ny)]);
        }
        return best;
    }

    /// <summary>
    /// Desirability of a cell for a zone, with an optional breakdown for the why-panel. The monthly layer and the
    /// why-query both call this, so they always agree.
    /// </summary>
    public int DesirabilityOf(int i, Zone zone, List<Factor>? f)
    {
        if (IsWater(i)) return -100;
        int total = 0;
        void Add(string label, int v) { if (v == 0) return; total += v; f?.Add(new Factor(label, v)); }
        int lv = LandValue[i];
        int traffic = NeighbourTraffic(i);
        switch (zone)
        {
            case Zone.Residential:
                Add("Land value", lv / 2);
                Add("Parks", Coverage[(int)Service.Parks][i] / 6);
                Add("Schools", Coverage[(int)Service.Education][i] / 10);
                Add("Health care", Coverage[(int)Service.Health][i] / 10);
                Add("Police", Coverage[(int)Service.Police][i] / 10);
                Add("Pollution", -Pollution[i] * 2 / 3);
                Add("Noise", -Noise[i] / 4);
                Add("Traffic", -traffic / 6);
                Add("Uncollected garbage", -GarbagePenalty);
                if (BuildingAtCell(i) is { } home && home.CommuteMinutes > 20) Add("Long commute", -(home.CommuteMinutes - 20) / 2);
                break;
            case Zone.Commercial:
                Add("Land value", lv / 3);
                Add("Passing traffic", Math.Min(25, traffic / 4));
                if (traffic > 160) Add("Gridlock", -(traffic - 160) / 4);
                Add("Customers in town", Math.Min(20, Population / 200));
                Add("Police", Coverage[(int)Service.Police][i] / 10);
                Add("Pollution", -Pollution[i] / 3);
                Add("Uncollected garbage", -GarbagePenalty / 2);
                break;
            case Zone.Industrial:
                Add("Base", 35);
                Add("Pricey land", -lv / 5);
                Add("Fire protection", Coverage[(int)Service.Fire][i] / 10);
                if (traffic > 160) Add("Gridlock", -(traffic - 160) / 4);
                break;
        }
        return Math.Clamp(total, -100, 200);
    }

    void RunDesirability()
    {
        for (int i = 0; i < N; i++)
        {
            DesR[i] = (short)DesirabilityOf(i, Zone.Residential, null);
            DesC[i] = (short)DesirabilityOf(i, Zone.Commercial, null);
            DesI[i] = (short)DesirabilityOf(i, Zone.Industrial, null);
        }
    }
}
