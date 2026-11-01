using ToyCity.Contract;

namespace ToyCity.Sim;

/// <summary>
/// Beta 3.1 part 6: a company's line can run on past its last stop, over a road crossing into a neighbouring city.
/// A line lives in one city's state, so what it carries over the border is counted from the border's flows rather
/// than from the neighbour's people: a share of the commuters crossing that side, more the more often it runs, up to
/// its seats. Its seats also add to the side's capacity, as a shuttle deal's do, so more people cross.
/// </summary>
public sealed partial class World
{
    /// <summary>Hours a day a line carries commuters.</summary>
    public const int ServiceHours = 16;

    /// <summary>The first working crossing into a neighbour on a side, or -1.</summary>
    public int CrossingCell(int side)
    {
        if (NextDoor[side] is not { Name.Length: > 0 }) return -1;
        for (int k = 1; k < SideLength(side) - 1; k++)
        {
            int i = SideCell(side, k);
            int x = i % Width, y = i / Width;
            int inward = x == 0 ? i + 1 : x == Width - 1 ? i - 1 : y == 0 ? i + Width : i - Width;
            if (Path[i] != 0 && Path[inward] != 0 && ProfileAt(i) is { Cars: true } && CrossingMeets(i)) return i;
        }
        return -1;
    }

    /// <summary>The roads into neighbouring cities, for a company to run lines on to (contract v1.4).</summary>
    void FillCrossings(CityToGame input)
    {
        for (int side = 0; side < 4; side++)
        {
            int cell = CrossingCell(side);
            if (cell < 0) continue;
            input.Crossings.Add(new BorderCrossing(side, NextDoor[side]!.Name, cell, PeopleCrossing(side)));
        }
    }

    /// <summary>People crossing a side a day, both ways: commuters, shoppers and visitors.</summary>
    public int PeopleCrossing(int side)
    {
        var f = Flows[side];
        return f.CommuteOut + f.CommuteIn + f.ShoppersOut + f.ShoppersIn + f.TouristsOut + f.TouristsIn;
    }

    /// <summary>
    /// The riders a line running on over a border carries across it today: of the people crossing that side, a
    /// share that grows as the line runs more often (30% every 10 minutes, at most 40%), up to its seats for the day.
    /// </summary>
    public int ThroughRiders(TransitLine line)
    {
        if (line.ThroughSide is < 0 or > 3 || line.HeadwayMinutes <= 0 || CrossingCell(line.ThroughSide) < 0) return 0;
        int crossing = PeopleCrossing(line.ThroughSide);
        int share = Math.Min(40, 300 / line.HeadwayMinutes);
        return Math.Min(line.SeatsPerHour * ServiceHours, crossing * share / 100);
    }

    /// <summary>Seats an hour the through lines add to each side's road capacity.</summary>
    int ThroughSeats(int side)
    {
        int seats = 0;
        foreach (var hg in SideGames)
            foreach (var l in hg.Last.Lines)
                if (l.ThroughSide == side && l.HeadwayMinutes > 0) seats += l.SeatsPerHour;
        return seats;
    }

    /// <summary>Through seats on each side when the crossings were last counted. Not saved: worked out again.</summary>
    int[]? _throughSeen;

    /// <summary>Adds today's through riders to each line's ridership, boarding at its last stop.</summary>
    void CountThroughRiders()
    {
        // When the lines over a border change, the crossings are counted again with their seats.
        var seats = new int[4];
        for (int s = 0; s < 4; s++) seats[s] = ThroughSeats(s);
        if (_throughSeen == null || !seats.AsSpan().SequenceEqual(_throughSeen))
        {
            _throughSeen = seats;
            RunNeighborConnections();
        }
        foreach (var hg in SideGames)
            foreach (var l in hg.Last.Lines)
            {
                int n = l.StopLeaseIds.Count, riders = ThroughRiders(l);
                if (riders == 0 || n == 0) continue;
                if (!hg.Ridership.TryGetValue(l.LineId, out var r)) r = (0, new int[n]);
                if (r.Boardings.Length != n) continue;
                r.Boardings[n - 1] += riders;
                hg.Ridership[l.LineId] = (r.Riders + riders, r.Boardings);
            }
    }
}
