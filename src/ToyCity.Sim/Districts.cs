using ToyCity.Contract;

namespace ToyCity.Sim;

/// <summary>
/// Districts: 32×32-cell squares, the unit side games are rated and paid in. Each has a name from the voice, the
/// same for every city of that size, and a few figures refreshed monthly.
/// </summary>
public sealed partial class World
{
    public const int DistrictSize = 32;

    public int DistrictsAcross => (Width + DistrictSize - 1) / DistrictSize;
    public int DistrictCount => DistrictsAcross * ((HeightCells + DistrictSize - 1) / DistrictSize);

    public int DistrictOf(int cell) => cell / Width / DistrictSize * DistrictsAcross + cell % Width / DistrictSize;

    public int DistrictOf(int x, int y) => y / DistrictSize * DistrictsAcross + x / DistrictSize;

    public string DistrictName(int id) => Content.Voice.District(id);

    public CellRect DistrictRect(int id)
    {
        int x0 = id % DistrictsAcross * DistrictSize, y0 = id / DistrictsAcross * DistrictSize;
        return new CellRect(x0, y0, Math.Min(Width - 1, x0 + DistrictSize - 1), Math.Min(HeightCells - 1, y0 + DistrictSize - 1));
    }

    /// <summary>
    /// Each district's people, jobs, average land value, crime (100 minus police coverage at homes) and health
    /// coverage at homes. Rating is filled in per side game by the caller.
    /// </summary>
    public List<District> DistrictFigures()
    {
        int n = DistrictCount;
        var pop = new int[n];
        var jobs = new int[n];
        var homes = new int[n];
        var police = new long[n];
        var health = new long[n];
        foreach (var b in Buildings)
        {
            if (b is not { Operating: true }) continue;
            int at = Idx(b.X, b.Y), d = DistrictOf(at);
            pop[d] += b.Residents;
            jobs[d] += b.Jobs;
            if (b.Residents > 0)
            {
                homes[d]++;
                police[d] += Coverage[(int)Service.Police][at];
                health[d] += Coverage[(int)Service.Health][at];
            }
        }
        var land = new long[n];
        var cells = new int[n];
        for (int i = 0; i < N; i++) { int d = DistrictOf(i); land[d] += LandValue[i]; cells[d]++; }
        var list = new List<District>(n);
        for (int d = 0; d < n; d++)
        {
            var r = DistrictRect(d);
            int pol = homes[d] == 0 ? 0 : (int)(police[d] / homes[d]);
            int hea = homes[d] == 0 ? 0 : (int)(health[d] / homes[d]);
            list.Add(new District(d, DistrictName(d), r.X0, r.Y0, r.X1, r.Y1, pop[d], jobs[d], cells[d] == 0 ? 0 : (int)(land[d] / cells[d]),
                homes[d] == 0 ? 0 : 100 - Math.Min(100, pol), Math.Min(100, hea), 0));
        }
        return list;
    }

    /// <summary>
    /// Commuter trips a day between districts, from where each household lives to where its workers go, whatever
    /// way they travel now. Side games read it as the demand they could serve.
    /// </summary>
    public List<TripDemand> DistrictDemand()
    {
        var trips = new Dictionary<(int, int), int>();
        foreach (var home in Buildings)
        {
            if (home is not { Operating: true, Residents: > 0 }) continue;
            int from = DistrictOf(home.X, home.Y);
            foreach (var a in home.Assignments)
            {
                if (a.SiteId >= Buildings.Count || Buildings[a.SiteId] is not { } site) continue;
                var key = (from, DistrictOf(site.X, site.Y));
                trips[key] = trips.GetValueOrDefault(key) + a.Count;
            }
        }
        return trips.OrderBy(t => t.Key.Item1).ThenBy(t => t.Key.Item2).Select(t => new TripDemand(t.Key.Item1, t.Key.Item2, t.Value)).ToList();
    }
}
