namespace ToyCity.Sim.Tests;

/// <summary>Beta 4, part 3: water quality. Foul water, sewage treatment, tap water, and rivers over borders.</summary>
public class WaterQualityTests
{
    /// <summary>The underground tests' flats: a road with its pipe, a coal plant, two pumps and high-rise flats along it.</summary>
    static (World W, int X, int Road) Flats()
    {
        var (w, x, y) = Fixture.FlatSite();
        int road = y + 12;
        w.Funds = 1_000_000;
        w.Enqueue(new PlacePathAction(x, road, x + 30, road, "road"));
        w.Enqueue(new PlaceUtilityAction(x, road, x + 30, road, UtilityKind.WaterPipe));
        w.Enqueue(new PlaceBuildingAction(x + 12, road - 4, "coal-plant"));
        w.Enqueue(new PlaceBuildingAction(x + 2, road - 2, "water-pump"));
        w.Enqueue(new PlaceBuildingAction(x + 5, road - 2, "water-pump"));
        w.Step();
        var flats = Fixture.Content.Zoned(Zone.Residential, Level.High, Level.High);
        for (int k = 0; k < 9; k++)
        {
            w.AddBuilding(flats, x + 2 + 3 * k, road + 1, 0, 2, 2);
            w.AddBuilding(flats, x + 2 + 3 * k, road + 4, 0, 2, 2);
        }
        w.Run(2);
        return (w, x, road);
    }

    static void RunPastMonth(World w)
    {
        int month = w.Month;
        for (int k = 0; k < 40 && w.Month == month; k++) w.Step();
        w.Step();
    }

    /// <summary>The flats with a sewage plant at a treatment level, a month on: the plant, and the water it lets out into.</summary>
    static (World W, Building Plant, int Outfall) Treated(Treatment level)
    {
        var (w, x, road) = Flats();
        w.Enqueue(new SetTreatmentAction(level));
        // South of the flats, towards the river, on a pipe of its own from the road.
        w.Enqueue(new PlaceUtilityAction(x + 19, road, x + 19, road + 12, UtilityKind.WaterPipe));
        w.Enqueue(new PlaceUtilityAction(x + 16, road - 1, x + 16, road + 9, UtilityKind.PowerLine));
        w.Enqueue(new PlaceBuildingAction(x + 16, road + 10, "sewage-plant"));
        w.Run(2);
        RunPastMonth(w);
        var plant = w.Buildings.First(b => b != null && w.Content.Types[b.Type].SewageCap > 0)!;
        return (w, plant, w.NearestWater(plant));
    }

    [Fact]
    public void TheLessThePlantsTreatTheFoulerTheWaterAndTheCheaperTheUpkeep()
    {
        var foul = new int[4];
        var upkeep = new long[4];
        for (int k = 0; k < 4; k++)
        {
            var (w, plant, outfall) = Treated((Treatment)k);
            Assert.True(outfall >= 0, "the plant has water to let out into");
            Assert.Equal(0, w.SewageUntreated);
            foul[k] = w.Foul[outfall];
            upkeep[k] = -w.LastBudget.First(l => l.Label == "Water pumps").Amount;
        }
        Assert.True(foul[0] > foul[1] && foul[1] > foul[2] && foul[2] > foul[3], $"foul at the outfall: {string.Join(", ", foul)}");
        Assert.True(foul[0] > World.SmellsAt, $"raw sewage fouls the water: {foul[0]}");
        Assert.True(foul[3] < 20, $"tertiary treatment leaves it nearly clean: {foul[3]}");
        Assert.True(upkeep[0] < upkeep[1] && upkeep[1] < upkeep[2] && upkeep[2] < upkeep[3], $"upkeep: {string.Join(", ", upkeep)}");
    }

    [Fact]
    public void RawSewageOutSmellsAndPutsPeopleOff()
    {
        var (w, plant, outfall) = Treated(Treatment.Raw);
        // Land beside the outfall smells, and a home there would be less wanted for it.
        int x = outfall % w.Width, y = outfall / w.Width;
        int land = -1;
        for (int r = 1; r <= 3 && land < 0; r++)
            for (int dy = -r; dy <= r && land < 0; dy++)
            for (int dx = -r; dx <= r && land < 0; dx++)
                if (w.InBounds(x + dx, y + dy) && !w.IsWater(w.Idx(x + dx, y + dy))) land = w.Idx(x + dx, y + dy);
        Assert.True(land >= 0);
        Assert.True(w.Stench[land] > 0, "the land beside foul water smells");
        var why = new List<Factor>();
        w.DesirabilityOf(land, Zone.Residential, why);
        Assert.Contains(why, f => f.Label == "Foul water nearby" && f.Value < 0);
        Assert.Contains("foul", w.Why(x, y).Summary);
        Assert.Contains(w.Alerts(), a => a.Key == "foul-water");
    }

    [Fact]
    public void FoulWaterRunsDownTheRiverNotUpIt()
    {
        var w = World.Create(Fixture.Content, 42, 128, "Riverton");
        int Surface(int i) => w.IsRiver(i) ? w.River[i] : w.WaterLevel;
        // A river cell well up a river: one with river higher and lower than it.
        int at = Enumerable.Range(0, w.N).Where(w.IsRiver)
            .OrderByDescending(i => Enumerable.Range(0, w.N).Count(j => w.IsRiver(j) && w.River[j] < w.River[i]))
            .First(i => Enumerable.Range(0, w.N).Any(j => w.IsRiver(j) && w.River[j] > w.River[i] + 1));
        var src = new float[w.N];
        src[at] = 200;
        var conc = w.Carry(src);
        for (int i = 0; i < w.N; i++)
            if (w.IsWater(i) && Surface(i) > Surface(at)) Assert.True(conc[i] == 0, $"foul water climbed to {i}");
        int lower = Enumerable.Range(0, w.N).Count(i => w.IsWater(i) && Surface(i) < Surface(at) && conc[i] > 50);
        Assert.True(lower >= 5, $"only {lower} cells downstream are fouled");
        // Nothing reaches water that isn't joined to it, and the sea spreads it thin.
        Assert.True(Enumerable.Range(0, w.N).Count(i => w.IsWater(i) && conc[i] > 0) < Enumerable.Range(0, w.N).Count(w.IsWater));
    }

    [Fact]
    public void FoulTapWaterTillATreatmentWorksCleansIt()
    {
        // No sewage plant: the flats' raw sewage soaks into the ground around the pumps, and comes out of the taps.
        var (w, x, road) = Flats();
        RunPastMonth(w);
        w.Run(2);
        int home = w.Idx(x + 3, road + 2);
        int before = w.TapFoul[home];
        Assert.True(before > 0, "the pumps draw the sewage");
        Assert.Contains("draws water", w.Why(x + 2, road - 2).Summary);

        w.Enqueue(new PlaceBuildingAction(x + 22, road - 2, "water-treatment"));
        w.Run(3);
        Assert.True(w.TapFoul[home] <= before / 10, $"the works cleans the mains: {before} to {w.TapFoul[home]}");
        Assert.Contains("tap water", w.Why(x + 22, road - 2).Summary);
    }

    [Fact]
    public void WaterQualityIsSaved()
    {
        var (w, _, _) = Treated(Treatment.Primary);
        w.Enqueue(new WaterInflowAction(0, 0, 90));
        w.Step();
        var back = World.FromSnapshot(Fixture.Content, w.Snapshot());
        Assert.Equal(Treatment.Primary, back.SewageTreatment);
        Assert.Equal(w.Foul, back.Foul);
        Assert.Equal(w.TapFoul, back.TapFoul);
        Assert.Equal(w.Inflow, back.Inflow);
        Assert.Equal(w.StateHash(), back.StateHash());
        w.Run(40);
        back.Run(40);
        Assert.Equal(w.StateHash(), back.StateHash());
    }

    [Fact]
    public void OnlyTheRegionBringsWaterInAndTreatmentIsTheBudgets()
    {
        var w = Fixture.Starter();
        Assert.NotNull(w.Forbidden(new WaterInflowAction(1, 1, 200) { By = "May" }));
        Assert.NotNull(w.OutOfBounds(new WaterInflowAction(-1, 1, 200)));
        Assert.NotNull(w.OutOfBounds(new SetTreatmentAction((Treatment)9)));
    }

    [Fact]
    public void FoulWaterRunsOnOverABorderUnlessTheRegionKeepsItHome()
    {
        using var r = Region.Create(Fixture.Content, "Rivers", 7, 3, 3, 64);
        int wide = 3 * 64;
        // A river crossing from one tile into the next.
        var g = r.Ground;
        int from = Enumerable.Range(0, g.Rivers.Length).First(i =>
        {
            int d = g.Downstream[i];
            return g.Rivers[i] != 0 && d >= 0 && (i % wide / 64 != d % wide / 64 || i / wide / 64 != d / wide / 64);
        });
        int to = g.Downstream[from];
        var up = r.Found(from % wide / 64, from / wide / 64, "Upton", "May");
        var down = r.Found(to % wide / 64, to / wide / 64, "Downton", "June");
        int ux = from % wide % 64, uy = from / wide % 64, dx = to % wide % 64, dy = to / wide % 64;

        // Upton's water is foul where the river leaves it (as if a town upstream of it fouled it).
        up.Enqueue(new WaterInflowAction(ux, uy, 220));
        for (int k = 0; k < 70 && !(down.Foul.Length == down.N && down.Foul[down.Idx(dx, dy)] > 150); k++) r.Step();
        Assert.True(down.Inflow.GetValueOrDefault(down.Idx(dx, dy)) > 150, "Downton hears the river coming in foul");
        Assert.True(down.Foul[down.Idx(dx, dy)] > 150, "and its own water is foul there");
        Assert.Contains("upstream", down.Why(dx, dy).Summary);

        // The region keeps each city's water its own: the river comes in clean again.
        r.WaterCrossesBorders = false;
        r.Run(2);
        Assert.Empty(down.Inflow);
    }
}
