using Google.Protobuf;
using ToyCity.Protocol.V0;

namespace ToyCity.ClientModel;

/// <summary>
/// The benchmark city for the frame budget: a server's map with every dry block built up. Roads run every fifth
/// row and column, and each 4 × 4 block between them holds zoned buildings, tallest in the middle of the map and
/// thinning out to houses at the edges, as a city fully grown would, with a share of them going up, worn,
/// abandoned or burning. Only the client draws it; the server's city is untouched.
/// </summary>
public static class DenseCity
{
    public static FullState Make(FullState template)
    {
        var f = template.Clone();
        int w = f.Width, h = f.Height, n = w * h;
        var heights = f.Heights.ToByteArray();
        var rivers = f.Rivers.Length == n ? f.Rivers.ToByteArray() : new byte[n];
        bool Dry(int x, int y) => heights[y * w + x] >= f.WaterLevel && rivers[y * w + x] <= heights[y * w + x];
        var zones = new byte[n];
        var paths = new byte[2 * n];
        f.Buildings.Clear();
        var types = f.Catalog.BuildingTypes.Where(t => t.Category == "zoned").ToList();
        BuildingTypeInfo Pick(ZoneKind z, Density d, int k) =>
            types.Where(t => t.Zone == z && t.Density == d).ElementAtOrDefault(k % Math.Max(1, types.Count(t => t.Zone == z && t.Density == d)))
            ?? types.First(t => t.Zone == z);
        int road = Math.Max(1, f.Catalog.Profiles.ToList().FindIndex(p => p.Frontage) + 1);

        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if ((x % 5 == 0 || y % 5 == 0) && Dry(x, y)) { paths[2 * (y * w + x)] = (byte)road; paths[2 * (y * w + x) + 1] = (byte)(road >> 8); }

        int id = 0;
        for (int by = 1; by + 4 <= h; by += 5)
            for (int bx = 1; bx + 4 <= w; bx += 5)
            {
                bool dry = true;
                for (int y = by; y < by + 4 && dry; y++)
                    for (int x = bx; x < bx + 4; x++) if (!Dry(x, y)) { dry = false; break; }
                if (!dry) continue;
                // Distance from the middle picks density; the block's place picks the zone.
                float dist = MathF.Sqrt(MathF.Pow(bx + 2 - w / 2f, 2) + MathF.Pow(by + 2 - h / 2f, 2)) / (w / 2f);
                var density = dist < 0.35f ? Density.High : dist < 0.7f ? Density.Medium : Density.Low;
                int k = bx / 5 * 7 + by / 5 * 13;
                var zone = (k % 10) switch { < 6 => ZoneKind.ZoneResidential, < 9 => ZoneKind.ZoneCommercial, _ => ZoneKind.ZoneIndustrial };
                // Lots: one 4×4 tower, four 2×2s, or sixteen houses.
                int lot = density switch { Density.High => 4, Density.Medium => 2, _ => 1 };
                for (int ly = by; ly < by + 4; ly += lot)
                    for (int lx = bx; lx < bx + 4; lx += lot)
                    {
                        var t = Pick(zone, density, k + lx + ly);
                        var b = new BuildingState
                        {
                            Id = id++, TypeId = t.Id, X = lx, Y = ly, Width = lot, Height = lot, Zone = zone, Density = density, Wealth = t.Wealth,
                            Name = t.Name, Powered = true, Watered = true, Condition = 100, FundingPercent = 100,
                        };
                        // A city's life on show: some going up, some worn and tagged, a few abandoned or on fire.
                        uint roll = (uint)b.Id * 2654435761u >> 24;
                        if (roll < 20) { b.ConstructionDaysLeft = 1 + (int)(roll % 12); b.Look = BuildingLook.LookUnderConstruction; }
                        else if (roll < 45) { b.Condition = 20 + (int)(roll % 40); b.Graffiti = (int)(roll % 4); }
                        else if (roll < 52) { b.Abandoned = true; b.Condition = 15; b.Graffiti = 3; b.Look = BuildingLook.LookAbandoned; }
                        else if (roll < 55) { b.BurningDays = 3; b.Look = BuildingLook.LookBurning; }
                        f.Buildings.Add(b);
                        for (int y = ly; y < ly + lot; y++)
                            for (int x = lx; x < lx + lot; x++) zones[y * w + x] = (byte)((int)zone * 3 + (int)density);
                    }
            }
        // The Colossus strides down an avenue near the middle, and a tornado spins beside it.
        f.Stats ??= new CityStats();
        f.Stats.Disasters.Clear();
        f.Stats.Disasters.Add(new Disaster { Id = 1, Kind = DisasterKind.DisasterRobot, X = w / 2 / 5 * 5 * 256 + 128, Y = h / 2 * 256, DaysLeft = 30 });
        f.Stats.Disasters.Add(new Disaster { Id = 2, Kind = DisasterKind.DisasterTornado, X = (w / 2 + 4) * 256, Y = (h / 2 + 3) * 256, DaysLeft = 30 });
        f.Zones = ByteString.CopyFrom(zones);
        f.Paths = ByteString.CopyFrom(paths);
        return f;
    }
}
