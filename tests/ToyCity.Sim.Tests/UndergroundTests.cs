namespace ToyCity.Sim.Tests;

/// <summary>Beta 3.2, underground: sewers laid with the water mains, treatment plants, and raw sewage.</summary>
public class UndergroundTests
{
    /// <summary>A flat site with a road and its pipe, a coal plant and a pump, and high-rise flats along it.</summary>
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

    [Fact]
    public void ASmallTownGetsByOnItsSepticTanks()
    {
        var w = Fixture.Starter(42);
        w.Run(120);
        Assert.True(w.SewageProduced > 0, "a town makes sewage");
        Assert.Equal(0, w.SewageCapacity);
        Assert.Equal(0, w.SewageUntreated);
        Assert.Equal(0, w.SewagePenalty);
        Assert.DoesNotContain(w.Alerts(), a => a.Key == "sewage");
    }

    [Fact]
    public void ABigTownWithoutAPlantFoulsItsWaterUntilOneIsBuilt()
    {
        var (w, x, road) = Flats();
        Assert.True(w.SewageProduced > World.SepticCapacity, $"{w.SewageProduced} a day");
        Assert.True(w.SewageUntreated > 0);
        Assert.True(w.SewagePenalty > 0);
        Assert.Contains(w.Alerts(), a => a.Key == "sewage");
        Assert.Contains(w.Notices, n => n.Text.Contains("Raw sewage"));

        // A month on, the raw sewage has seeped into the water by the flats, and homes there are less wanted.
        int far = w.Idx(x + 27, road + 5);
        RunPastMonth(w);
        Assert.True(w.WaterPollution[far] > 0, "raw sewage in the water by the flats");
        var why = new List<Factor>();
        w.DesirabilityOf(far, Zone.Residential, why);
        Assert.Contains(why, f => f.Label == "Raw sewage" && f.Value < 0);

        // A sewage plant beside the pipe treats it all.
        w.Enqueue(new PlaceBuildingAction(x + 20, road - 3, "sewage-plant"));
        w.Run(2);
        Assert.True(w.SewageCapacity > 0, "the plant treats sewage");
        Assert.Equal(0, w.SewageUntreated);
        Assert.True(w.Sewered[far] > 1, "the flats' sewage reaches the plant");
        Assert.DoesNotContain(w.Alerts(), a => a.Key == "sewage");
        RunPastMonth(w);
        Assert.Equal(0, w.WaterPollution[far]);
        Assert.Contains("sewage", w.Why(x + 21, road - 2).Summary.ToLowerInvariant());

        // The sewer is the water main's: dig up the pipe by the plant, and the sewage is raw again.
        w.Enqueue(new BulldozeAction(new CellRect(x + 17, road, x + 30, road), Underground: true));
        w.Run(2);
        Assert.True(w.SewageUntreated > 0);
    }

    [Fact]
    public void TheSewersAreSaved()
    {
        var (w, x, road) = Flats();
        w.Enqueue(new PlaceBuildingAction(x + 20, road - 3, "sewage-plant"));
        w.Run(3);
        var back = World.FromSnapshot(Fixture.Content, w.Snapshot());
        Assert.Equal(w.SewageCapacity, back.SewageCapacity);
        Assert.Equal(w.SewageProduced, back.SewageProduced);
        Assert.Equal(w.Sewered, back.Sewered);
        Assert.Equal(w.StateHash(), back.StateHash());
        w.Run(5);
        back.Run(5);
        Assert.Equal(w.StateHash(), back.StateHash());
    }

    static void RunPastMonth(World w)
    {
        int month = w.Month;
        for (int k = 0; k < 40 && w.Month == month; k++) w.Step();
        w.Step();
    }
}
