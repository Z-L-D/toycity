namespace ToyCity.Sim;

/// <summary>How a building looks to the player, from fine to derelict. Numbers match the protocol's BuildingCondition.</summary>
public enum BuildingLook : byte { Fine = 0, UnderConstruction = 1, Struggling = 2, RunDown = 3, Abandoned = 4, Derelict = 5, Burning = 6 }

public sealed partial class World
{
    /// <summary>Below this condition a building is run down: it adds to blight around it.</summary>
    public const int RunDownBelow = 35;
    /// <summary>Months abandoned (after the six that led to it) before a building counts as derelict.</summary>
    public const int DerelictAfter = 12;

    // Derived monthly; saved with the other fields.
    public readonly byte[] Litter;
    /// <summary>Grime: run-down and abandoned buildings, graffiti and litter. Lowers desirability and land value.</summary>
    public readonly byte[] Blight;

    public BuildingLook LookOf(Building b)
    {
        if (b.BurningDays > 0) return BuildingLook.Burning;
        if (b.ConstructionLeft > 0) return BuildingLook.UnderConstruction;
        if (b.Abandoned) return b.UnmetMonths >= MonthsToAbandon + DerelictAfter ? BuildingLook.Derelict : BuildingLook.Abandoned;
        if (b.Condition < RunDownBelow) return BuildingLook.RunDown;
        if (b.UnmetMonths > 0) return BuildingLook.Struggling;
        return BuildingLook.Fine;
    }

    /// <summary>
    /// The condition owners keep a building at, with the reasons. Zoned buildings follow land value, minus graffiti,
    /// pollution and litter; civic buildings follow their funding.
    /// </summary>
    public int UpkeepTarget(Building b, List<Factor>? f)
    {
        int total = 0;
        void Add(string label, int v) { if (v == 0) return; total += v; f?.Add(new Factor(label, v)); }
        var t = TypeOf(b);
        int i = Idx(b.X, b.Y);
        if (t.Placeable)
        {
            Add("Funding", Math.Min(100, b.Funding));
            return Math.Clamp(total, 0, 100);
        }
        Add("Owners' budget (land value)", Math.Min(80, LandValue[i] * 2 / 3));
        Add("Base upkeep", 35);
        Add("Graffiti", -8 * b.Graffiti);
        Add("Pollution", -Pollution[i] / 4);
        Add("Litter", -Litter[i] / 4);
        return Math.Clamp(total, 10, 100);
    }

    /// <summary>
    /// Monthly: litter, graffiti and wear, then the blight they leave behind. Runs after pollution and coverage and
    /// before land value and desirability, which both read blight.
    /// </summary>
    void RunWear()
    {
        // Litter: shoppers and passing traffic drop it; collection takes most of it away when there's capacity.
        Array.Clear(Litter);
        foreach (var b in Buildings)
        {
            if (b == null || !b.Operating) continue;
            var t = TypeOf(b);
            if (t.Zone == Zone.Commercial && !t.Placeable) Spread(Litter, b.X, b.Y, b.W, b.H, Math.Min(120, b.Jobs / 6 + 10), 2, false);
            if (t.Attraction > 0) Spread(Litter, b.X, b.Y, b.W, b.H, t.Attraction * 4, 2, false);
        }
        for (int i = 0; i < N; i++)
            if (Traffic[i] > 32) Spread(Litter, i % Width, i / Width, 1, 1, Traffic[i] / 16, 1, false);
        int keep = GarbageCapacity >= GarbageProduced && GarbageCapacity > 0 ? 50 : 100;
        keep = (int)Math.Max(0, keep * (100 + Mod(ModKey.Litter)) / 100);
        if (keep != 100)
            for (int i = 0; i < N; i++) Litter[i] = (byte)(Litter[i] * keep / 100);

        // Graffiti and wear, per building, in id order.
        long graffitiChanceScale = Math.Max(0, 100 + Mod(ModKey.Graffiti));
        foreach (var b in Buildings)
        {
            if (b == null) continue;
            var t = TypeOf(b);
            int i = Idx(b.X, b.Y);
            if (b.ConstructionLeft > 0) { b.Condition = 100; continue; }
            if (b.Abandoned) b.Condition = Math.Max(0, b.Condition - 5);
            else
            {
                int target = UpkeepTarget(b, null);
                b.Condition += Math.Sign(target - b.Condition) * Math.Min(3, Math.Abs(target - b.Condition));
            }
            if (t.Placeable && t.Category is not (Category.Landmark or Category.Reward)) continue;
            int police = Coverage[(int)Service.Police][i] * 100 / 255;
            int chance = Math.Max(0, (30 - police) / 6) + (b.Abandoned ? 8 : 0) + (b.Condition < 50 ? 3 : 0);
            chance = (int)(chance * graffitiChanceScale / 100);
            if (b.Graffiti < 3 && chance > 0 && Rng.Next(100) < chance) b.Graffiti++;
            else if (b.Graffiti > 0 && !b.Abandoned && (police > 40 || Mod(ModKey.Graffiti) <= -50 || (b.Condition >= 50 && Rng.Next(4) == 0))) b.Graffiti--;
        }

        // Blight from all of it. The worst neighbour counts, not the sum, so a dead block is bad but not hopeless.
        Array.Clear(Blight);
        foreach (var b in Buildings)
        {
            if (b == null || b.ConstructionLeft > 0) continue;
            if (b.Abandoned) Spread(Blight, b.X, b.Y, b.W, b.H, LookOf(b) == BuildingLook.Derelict ? 70 : 40, 3, true);
            else if (b.Condition < RunDownBelow) Spread(Blight, b.X, b.Y, b.W, b.H, (RunDownBelow - b.Condition) * 2, 2, true);
            if (b.Graffiti > 0) Spread(Blight, b.X, b.Y, b.W, b.H, b.Graffiti * 10, 2, true);
        }
        for (int i = 0; i < N; i++)
            if (Litter[i] > 20) Blight[i] = (byte)Math.Min(255, Blight[i] + (Litter[i] - 20) / 2);
    }
}
