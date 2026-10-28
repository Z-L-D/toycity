using Google.Protobuf;
using ToyCity.Protocol.V0;

namespace ToyCity.ClientModel.Tests;

/// <summary>Buildings rising as they're built, crumbling when they go, and showing their wear.</summary>
public class BuildingLifeTests
{
    static CityMirror City(params BuildingState[] buildings)
    {
        var f = new FullState { Width = 8, Height = 8, WaterLevel = 1, Heights = ByteString.CopyFrom(Enumerable.Repeat((byte)4, 64).ToArray()),
            Zones = ByteString.CopyFrom(new byte[64]), Paths = ByteString.CopyFrom(new byte[128]),
            PowerLines = ByteString.CopyFrom(new byte[64]), WaterPipes = ByteString.CopyFrom(new byte[64]), Catalog = new Catalog() };
        f.Catalog.BuildingTypes.Add(new BuildingTypeInfo { Id = "house", Category = "zoned", BuildDays = 10, Width = 1, Height = 1 });
        f.Buildings.AddRange(buildings);
        var c = new CityMirror();
        c.ApplyFull(f);
        return c;
    }

    static TickDelta Delta(params BuildingState[] b) { var d = new TickDelta(); d.Buildings.AddRange(b); return d; }

    [Fact]
    public void ABuildingRisesLayerByLayerAsItsDaysPass()
    {
        // A 2x1 house: 10 build days plus 2 for the extra cell.
        var site = new BuildingState { Id = 1, TypeId = "house", X = 1, Y = 1, Width = 2, Height = 1, ConstructionDaysLeft = 12, Condition = 100 };
        var city = City(site);
        var life = new BuildingLife();
        life.Advance(city, 0.1, Array.Empty<BuildingState>());
        Assert.InRange(life.Of(site).Standing, 0.02f, 0.05f);

        // Half the days pass: the drawn height catches up over a moment, never jumping.
        city.ApplyDelta(Delta(new BuildingState(site) { ConstructionDaysLeft = 6 }));
        var b = city.Buildings[1];
        float last = life.Of(b).Standing;
        for (int frame = 0; frame < 120; frame++)
        {
            life.Advance(city, 1 / 60.0, Array.Empty<BuildingState>());
            float now = life.Of(b).Standing;
            Assert.True(now >= last && now - last < 0.1f, $"frame {frame}: {last} to {now}");
            last = now;
        }
        Assert.Equal(0.5f, last, 2);
        Assert.False(life.Animating);

        city.ApplyDelta(Delta(new BuildingState(b) { ConstructionDaysLeft = 0 }));
        for (int frame = 0; frame < 120; frame++) life.Advance(city, 1 / 60.0, Array.Empty<BuildingState>());
        Assert.Equal(1f, life.Of(city.Buildings[1]).Standing);
    }

    [Fact]
    public void ARemovedBuildingCrumblesThenIsGone()
    {
        var house = new BuildingState { Id = 3, TypeId = "house", X = 2, Y = 2, Width = 1, Height = 1, Condition = 40, Graffiti = 3 };
        var city = City(house);
        var life = new BuildingLife();
        life.Advance(city, 0.1, city.TakeRemoved());
        var d = new TickDelta();
        d.RemovedBuildings.Add(3);
        city.ApplyDelta(d);
        Assert.Empty(city.Buildings);
        life.Advance(city, 0.1, city.TakeRemoved());
        var (gone, left) = Assert.Single(life.Crumbling);
        Assert.Equal(3, gone.Id);
        Assert.True(left is > 0.8f and < 1f);
        var look = life.OfCrumbling(gone, left);
        Assert.True(look.Flags.HasFlag(LifeFlags.Crumbling));
        Assert.True(life.Animating);
        life.Advance(city, BuildingLife.CrumbleSeconds, city.TakeRemoved());
        Assert.Empty(life.Crumbling);
        Assert.False(life.Animating);
    }

    [Fact]
    public void WearGraffitiFireAndAbandonmentComeFromTheServer()
    {
        var life = new BuildingLife();
        var fine = life.Of(new BuildingState { Id = 1, Condition = 100 });
        Assert.Equal((0f, 0f, LifeFlags.None), (fine.Wear, fine.Graffiti, fine.Flags));
        var rough = life.Of(new BuildingState { Id = 2, Condition = 25, Graffiti = 3, Abandoned = true, BurningDays = 2 });
        Assert.Equal(0.75f, rough.Wear, 3);
        Assert.Equal(1f, rough.Graffiti);
        Assert.Equal(LifeFlags.Burning | LifeFlags.Abandoned, rough.Flags);
        Assert.Equal(1f, rough.Standing);
    }
}
