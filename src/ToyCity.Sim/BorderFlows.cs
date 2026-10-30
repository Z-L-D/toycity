namespace ToyCity.Sim;

/// <summary>
/// What crosses one border in a day, both ways, as agreed with the city on the other side. Both cities work it out
/// from the same published numbers, so what one sends the other receives: the ledgers balance.
/// </summary>
public struct BorderFlow
{
    /// <summary>Residents of this city who work across the border, and the neighbour's who work here.</summary>
    public int CommuteOut, CommuteIn;
    /// <summary>Shoppers each way.</summary>
    public int ShoppersOut, ShoppersIn;
    /// <summary>Visitors each way, come for the sights.</summary>
    public int TouristsOut, TouristsIn;
    /// <summary>Tons of goods a day each way.</summary>
    public int FreightOut, FreightIn;
    /// <summary>Vehicles an hour the connection can carry: the lesser of the two sides'.</summary>
    public int Capacity;

    public readonly int Vehicles => CommuteOut + CommuteIn + (ShoppersOut + ShoppersIn + TouristsOut + TouristsIn) / 2 + (FreightOut + FreightIn) / 10;
}

public sealed partial class World
{
    // What this city published at the start of the month: its neighbours read the same numbers.
    public const int PubPopulation = 0, PubJobSeekers = 1, PubSpareJobs = 2, PubShopJobs = 3, PubAttraction = 4, PubFreightOut = 5,
        PubFreightWant = 6, PubSideCapacity = 7, PubCount = 11;

    /// <summary>This city's numbers as of the start of the month, as its neighbours know them. Saved.</summary>
    public readonly int[] Published = new int[PubCount];
    /// <summary>This month's flows across each side, agreed with the neighbour there. Saved.</summary>
    public readonly BorderFlow[] Flows = new BorderFlow[4];

    public int CommuteOutTotal => Flows.Sum(f => f.CommuteOut);
    public int CommuteInTotal => Flows.Sum(f => f.CommuteIn);

    /// <summary>
    /// Monthly. Works out each border's flows from last month's published numbers, this city's and its neighbour's,
    /// with the same formula both sides use; then publishes this month's. A neighbour that hasn't published yet (just
    /// founded) flows nothing until it has.
    /// </summary>
    void RunBorderFlows()
    {
        for (int side = 0; side < 4; side++)
            Flows[side] = NextDoor[side] is { Stats.Length: PubCount } n ? Flow(Published, n.Stats, side) : default;
        Published[PubPopulation] = Population;
        Published[PubJobSeekers] = Math.Max(0, LaborForce - Workers);
        Published[PubSpareJobs] = Math.Max(0, Jobs - Workers);
        Published[PubShopJobs] = JobsCommercial;
        Published[PubAttraction] = Attraction;
        Published[PubFreightOut] = JobsIndustrial / 3;
        Published[PubFreightWant] = Population / 12;
        for (int s = 0; s < 4; s++) Published[PubSideCapacity + s] = SideCapacity[s];
    }

    /// <summary>
    /// The flows across one side between a city (<paramref name="me"/>) and its neighbour (<paramref name="them"/>).
    /// Swapping the two and the side gives the same flows the other way round, which is what makes the ledgers balance.
    /// </summary>
    public static BorderFlow Flow(int[] me, int[] them, int side)
    {
        int cap = Math.Min(me[PubSideCapacity + side], them[PubSideCapacity + Opposite(side)]);
        if (cap <= 0) return default;
        // Each kind of trip gets a share of the road: commuters half, shoppers and visitors a quarter, trucks the rest.
        int Commute(int[] from, int[] to) => Math.Min(Math.Min(from[PubJobSeekers], to[PubSpareJobs]) / 2, cap / 4);
        int Shop(int[] from, int[] to) => Math.Min(Math.Min(from[PubPopulation] / 20, to[PubShopJobs]), cap / 8);
        int Tour(int[] from, int[] to) => Math.Min(from[PubPopulation] / 40 * to[PubAttraction] / (to[PubAttraction] + 20), cap / 8);
        int Goods(int[] from, int[] to) => Math.Min(Math.Min(from[PubFreightOut], to[PubFreightWant]) / 2, cap);
        return new BorderFlow
        {
            Capacity = cap,
            CommuteOut = Commute(me, them), CommuteIn = Commute(them, me),
            ShoppersOut = Shop(me, them), ShoppersIn = Shop(them, me),
            TouristsOut = Tour(me, them), TouristsIn = Tour(them, me),
            FreightOut = Goods(me, them), FreightIn = Goods(them, me),
        };
    }

    /// <summary>The border's traffic, on the crossing cells and a few cells inward along each road. Monthly, with the rest.</summary>
    void AddBorderTraffic()
    {
        for (int side = 0; side < 4; side++)
        {
            int vehicles = Flows[side].Vehicles;
            if (vehicles == 0 || NextDoor[side] is not { } n) continue;
            var crossings = new List<int>();
            for (int k = 1; k < SideLength(side) - 1; k++)
                if (Path[SideCell(side, k)] != 0 && n.Path[k] != 0) crossings.Add(SideCell(side, k));
            if (crossings.Count == 0) continue;
            int each = vehicles * 2 / crossings.Count;
            int step = side switch { 0 => Width, 1 => -1, 2 => -Width, _ => 1 };
            foreach (int c in crossings)
            {
                // Trips fade as they turn off into the city.
                int i = c;
                for (int d = 0; d < 8 && i >= 0 && i < N && Path[i] != 0; d++, i += step)
                    Trips[i] += each * (8 - d) / 8;
            }
        }
    }

    void WriteFlows(BinaryWriter w)
    {
        foreach (var v in Published) w.Write(v);
        foreach (var f in Flows)
        {
            w.Write(f.CommuteOut); w.Write(f.CommuteIn); w.Write(f.ShoppersOut); w.Write(f.ShoppersIn);
            w.Write(f.TouristsOut); w.Write(f.TouristsIn); w.Write(f.FreightOut); w.Write(f.FreightIn); w.Write(f.Capacity);
        }
    }

    void ReadFlows(BinaryReader r)
    {
        for (int k = 0; k < PubCount; k++) Published[k] = r.ReadInt32();
        for (int s = 0; s < 4; s++)
            Flows[s] = new BorderFlow
            {
                CommuteOut = r.ReadInt32(), CommuteIn = r.ReadInt32(), ShoppersOut = r.ReadInt32(), ShoppersIn = r.ReadInt32(),
                TouristsOut = r.ReadInt32(), TouristsIn = r.ReadInt32(), FreightOut = r.ReadInt32(), FreightIn = r.ReadInt32(), Capacity = r.ReadInt32(),
            };
    }
}
