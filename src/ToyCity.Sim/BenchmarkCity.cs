namespace ToyCity.Sim;

/// <summary>
/// The benchmark city for the Beta 1 gate, the server's counterpart of the client's dense city: roads every fifth
/// row and column over all the dry land, and each 4 × 4 block between them zoned, densest in the middle of the map
/// and thinning to houses at the edges, with a power plant, pumps or a landfill in every so many blocks. Roads, zones
/// and services go through player actions like any city; only the buildings are put straight in, finished, so the
/// map is full on the first day. The server offers it as a new-city option; the frame budget is measured on it.
/// </summary>
public static class BenchmarkCity
{
    public const int Size = 256;

    /// <summary>Lays out the city and builds every lot. Returns the number of blocks built on.</summary>
    public static int Apply(World w)
    {
        // A street every fifth cell can't climb a mountain: the benchmark is about a dense city, so its ground is
        // gentled to rolling hills first (a sixth of the rise), keeping the coast and lakes as they were.
        w.Soften(6);
        long funds = w.Funds;
        w.Funds = 500_000_000;
        int width = w.Width, height = w.HeightCells;
        bool Dry(int x, int y) => w.InBounds(x, y) && !w.IsWater(w.Idx(x, y));
        bool AllDry(int x0, int y0, int x1, int y1)
        {
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++) if (!Dry(x, y)) return false;
            return true;
        }
        // Streets between each pair of crossings, where the land is dry, with pipes under them.
        for (int y = 0; y < height; y += 5)
            for (int x = 0; x + 1 < width; x += 5)
            {
                int x1 = Math.Min(x + 5, width - 1);
                if (!AllDry(x, y, x1, y)) continue;
                w.Enqueue(new PlacePathAction(x, y, x1, y, "road"));
                w.Enqueue(new PlaceUtilityAction(x, y, x1, y, UtilityKind.WaterPipe));
            }
        for (int x = 0; x < width; x += 5)
            for (int y = 0; y + 1 < height; y += 5)
            {
                int y1 = Math.Min(y + 5, height - 1);
                if (AllDry(x, y, x, y1)) w.Enqueue(new PlacePathAction(x, y, x, y1, "road"));
            }
        int blocks = 0;
        for (int by = 1; by + 4 <= height; by += 5)
            for (int bx = 1; bx + 4 <= width; bx += 5)
            {
                if (!AllDry(bx, by, bx + 3, by + 3)) continue;
                blocks++;
                int k = bx / 5 * 7 + by / 5 * 13;
                float dist = MathF.Sqrt(MathF.Pow(bx + 2 - width / 2f, 2) + MathF.Pow(by + 2 - height / 2f, 2)) / (width / 2f);
                // Services first: pumps everywhere, landfills out on the edge, away from the homes.
                int pick = (bx / 5 * 31 + by / 5 * 17) % 23;
                if (pick == 6)
                {
                    w.Enqueue(new PlaceBuildingAction(bx, by, "water-pump"));
                    w.Enqueue(new PlaceBuildingAction(bx + 2, by, "water-pump"));
                    w.Enqueue(new PlaceBuildingAction(bx, by + 2, "water-pump"));
                    w.Enqueue(new PlaceBuildingAction(bx + 2, by + 2, "water-pump"));
                    continue;
                }
                // Plants everywhere, so the parts of the map that water cuts off have their own: cleaner gas toward the middle.
                if (pick is 0 or 12) { w.Enqueue(new PlaceBuildingAction(bx, by, dist > 0.6f ? "coal-plant" : "gas-plant")); continue; }
                if (dist > 0.75f && pick == 18) { w.Enqueue(new PlaceBuildingAction(bx, by, "landfill")); continue; }
                var density = dist < 0.35f ? Level.High : dist < 0.7f ? Level.Medium : Level.Low;
                // Mostly homes, shops in among them, industry only toward the edge.
                var zone = (k % 20) switch { < 14 => Zone.Residential, < 18 => Zone.Commercial, _ => dist > 0.6f ? Zone.Industrial : Zone.Commercial };
                if (zone == Zone.Industrial && density == Level.High) density = Level.Medium;
                w.Enqueue(new PaintZoneAction(new CellRect(bx, by, bx + 3, by + 3), zone, density));
            }
        w.ApplyPending();

        // Every free lot gets the largest building that fits it, of a wealth picked from where it stands.
        for (int i = 0; i < w.N; i++)
        {
            byte code = w.ZoneCode[i];
            if (code == 0 || w.BuildingAt[i] != 0 || w.Path[i] != 0) continue;
            var lots = w.LotPlacements(i);
            if (lots.Count == 0) continue;
            var lot = lots[0];
            uint roll = (uint)i * 2654435761u >> 28;
            var wealth = roll < 8 ? Level.Low : roll < 13 ? Level.Medium : Level.High;
            w.AddBuilding(w.Content.Zoned(World.ZoneOf(code), World.DensityOf(code), wealth), lot.X, lot.Y, 0, lot.W, lot.H);
        }
        foreach (var b in w.Buildings) if (b is { Residents: > 0 }) w.MatchCommutes(b);
        w.ApplyPending();
        w.Funds = Math.Max(funds, 1_000_000);
        return blocks;
    }
}
