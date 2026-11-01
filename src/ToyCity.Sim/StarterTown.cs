namespace ToyCity.Sim;

/// <summary>
/// Lays out a small working town using only player actions, so the result replays like any other city.
/// Used by tests, the benchmark and the server's demo option.
/// </summary>
public static class StarterTown
{
    public const int Size = 30;

    /// <summary>Finds the flattest all-land block for the town, or null when none fits.</summary>
    public static (int X, int Y)? FindSite(World w, int size = Size + 6)
    {
        (int, int)? best = null;
        int bestScore = int.MaxValue;
        for (int oy = 2; oy + size < w.HeightCells - 2; oy += 2)
        for (int ox = 2; ox + size < w.Width - 2; ox += 2)
        {
            int score = 0;
            bool ok = true;
            for (int y = oy; y < oy + size && ok; y++)
            for (int x = ox; x < ox + size; x++)
            {
                int i = w.Idx(x, y);
                if (w.IsWater(i)) { ok = false; break; }
                if (x + 1 < ox + size) score += Math.Abs(w.Height[i] - w.Height[i + 1]) > 1 ? 50 : Math.Abs(w.Height[i] - w.Height[i + 1]);
                if (y + 1 < oy + size) score += Math.Abs(w.Height[i] - w.Height[i + w.Width]) > 1 ? 50 : Math.Abs(w.Height[i] - w.Height[i + w.Width]);
            }
            if (ok && score < bestScore) { bestScore = score; best = (ox, oy); }
        }
        return best;
    }

    /// <summary>
    /// Queues the actions for a town at (ox, oy): a road grid, R/C/I zones, a coal plant and water. Beta 3.2: the
    /// town sits on a hillside: the site keeps the land's tilt, eased to at most 7%, its streets climb with it, and
    /// homes and shops level their own pads.
    /// </summary>
    public static List<SimAction> Build(int ox, int oy)
    {
        var a = new List<SimAction>();
        int s = Size;
        // The site is evened to a hillside that keeps the land's own tilt (at most 7%), not flattened: the streets
        // then climb it within a road's grade and every lot is gentle enough for its pad.
        a.Add(new TerraformAction(new CellRect(ox, oy, ox + s + 5, oy + s - 1), TerraformMode.Slope));
        // Streets every fifth row: blocks four cells deep take lots facing either street.
        int bottom = oy + s - 1 - (s - 1) % 5;
        for (int y = oy; y <= bottom; y += 5) a.Add(new PlacePathAction(ox, y, ox + s, y, "road"));
        foreach (int c in new[] { 0, 10, 20, s }) a.Add(new PlacePathAction(ox + c, oy, ox + c, bottom, "road"));
        // Residential on the left, commercial in the middle, industry on the right, next to the plant.
        a.Add(new PaintZoneAction(new CellRect(ox + 1, oy + 1, ox + 9, bottom - 1), Zone.Residential, Level.Low));
        a.Add(new PaintZoneAction(new CellRect(ox + 11, oy + 1, ox + 19, oy + 9), Zone.Residential, Level.Medium));
        a.Add(new PaintZoneAction(new CellRect(ox + 11, oy + 11, ox + 19, bottom - 1), Zone.Commercial, Level.Low));
        a.Add(new PaintZoneAction(new CellRect(ox + 21, oy + 1, ox + s - 1, bottom - 1), Zone.Industrial, Level.Low));
        // Services along the east road. Roads carry power, so no power lines are needed.
        a.Add(new PlaceBuildingAction(ox + s + 1, oy + 1, "coal-plant"));
        a.Add(new PlaceBuildingAction(ox + s + 1, oy + 8, "water-pump"));
        a.Add(new PlaceBuildingAction(ox + s + 1, oy + 13, "landfill"));
        // Pipes under every other street, joined under the east road where the pump sits.
        for (int y = oy; y <= bottom; y += 10) a.Add(new PlaceUtilityAction(ox, y, ox + s, y, UtilityKind.WaterPipe));
        a.Add(new PlaceUtilityAction(ox + s, oy, ox + s, bottom, UtilityKind.WaterPipe));
        a.Add(new PlaceBuildingAction(ox + 4, bottom + 1, "small-park"));
        return a;
    }

    /// <summary>Creates a world, finds a site and queues the starter town. Returns false when there is no site.</summary>
    public static bool Apply(World w)
    {
        var site = FindSite(w);
        if (site == null) return false;
        foreach (var act in Build(site.Value.X, site.Value.Y)) w.Enqueue(act);
        return true;
    }
}
