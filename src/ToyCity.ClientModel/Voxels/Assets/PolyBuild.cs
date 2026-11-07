namespace ToyCity.ClientModel.Voxels.Assets;

/// <summary>
/// A house of polygon walls and parts (RC part 5): the outline, how many storeys, and its finishes. The front is the
/// south side (the street, at the larger y), with the front door in the middle.
/// </summary>
public sealed record PolyHouseSpec(double X0, double Y0, double X1, double Y1, int Storeys = 2, string Walls = "brick-red")
{
    public double StoreyHeight { get; init; } = 3.0;
    public double Thickness { get; init; } = 0.3;
    /// <summary>Window and door frames, and glazing bars.</summary>
    public string Trim { get; init; } = "trim-white";
    /// <summary>The plinth, sills, lintels and the band between floors; "" for none (a timber house).</summary>
    public string Dressing { get; init; } = "stone-ashlar";
    public string Roof { get; init; } = "slate";
    public byte RoofColour { get; init; } = AssetPalette.Slate;
    public RoofStyle Style { get; init; } = RoofStyle.Gable;
    public double Pitch { get; init; } = 38;
    public int OverhangCm { get; init; } = 35;
    public bool Chimney { get; init; } = true;
    /// <summary>A garage door in place of the front's last ground floor window.</summary>
    public bool Garage { get; init; }
}

/// <summary>Builds things of polygon walls and parts, for the examples and the realistic set.</summary>
public static class PolyBuild
{
    /// <summary>
    /// Adds a house's shapes, walls standing on <paramref name="ground"/> metres, and returns its auto-roof (the roof's
    /// finish its covering, its gable ends the walls' finish).
    /// </summary>
    public static RoofSpec House(Action<Shape> add, PolyHouseSpec s, double ground)
    {
        double x0 = s.X0, x1 = s.X1, y0 = s.Y0, y1 = s.Y1, t = s.Thickness;
        double h = s.Storeys * s.StoreyHeight + 0.2, z = ground;
        bool dressed = s.Dressing.Length > 0;
        var runs = new[] { (x0, y0, x1, y0), (x1, y0, x1, y1), (x1, y1, x0, y1), (x0, y1, x0, y0) };
        // A plinth just proud of the walls: each run starts 5 cm before its corner and stands 5 cm out.
        if (dressed)
            foreach (var (ax, ay, bx, by) in runs)
            {
                double ox = Math.Sign(bx - ax), oy = Math.Sign(by - ay);
                add(Parts.Wall(ax - ox * 0.05 + oy * 0.05, ay - oy * 0.05 - ox * 0.05, bx + ox * 0.05 + oy * 0.05, by + oy * 0.05 - ox * 0.05, z, 0.45, 0.1, s.Dressing));
            }
        var walls = runs.Select(r => Parts.Wall(r.Item1, r.Item2, r.Item3, r.Item4, z, h, t, s.Walls)).ToList();
        var (north, east, south, west) = (walls[0], walls[1], walls[2], walls[3]);
        var parts = new List<Shape>();
        void Put(Shape wall, PartKind k, double centre, double sill)
        {
            double w = Parts.SizeOf(k).W;
            if (Parts.Insert(wall, k, centre - w / 2, sill, frame: s.Trim) is not { } p) return;
            parts.AddRange(p);
            if (!dressed) return;
            // A lintel over each opening.
            var o = wall.Openings[^1];
            var (lx, ly, lz) = wall.At(o.X - 0.1, -0.03, o.Z + o.H);
            parts.Add(Parts.Box(lx, ly, lz, o.W + 0.2, 0.15, 0.22, s.Dressing, wall.Turn));
        }
        // Windows evenly along each wall, a bay about every 3.3 m, on every floor; the front door in the middle bay of
        // the front, the back door in the back's first.
        foreach (var wall in walls)
        {
            int bays = Math.Max(1, (int)Math.Round(wall.Width / 3.3));
            for (int b = 0; b < bays; b++)
            {
                double c = wall.Width * (b + 0.5) / bays;
                for (int f = 0; f < s.Storeys; f++)
                {
                    double sill = f * s.StoreyHeight + 0.8;
                    if (f == 0 && wall == south && b == bays / 2) { Put(wall, PartKind.Door, c, 0); continue; }
                    if (f == 0 && wall == south && s.Garage && b == bays - 1 && bays > 1) { Put(wall, PartKind.GarageDoor, c, 0); continue; }
                    if (f == 0 && wall == north && b == 0) { Put(wall, PartKind.Door, c, 0); continue; }
                    Put(wall, PartKind.Window, c, sill);
                }
            }
        }
        foreach (var w in walls) add(w);
        foreach (var p in parts) add(p);
        // A band between the floors, standing 4 cm proud all round, and floors inside for when it's wrecked.
        for (int f = 1; f < s.Storeys; f++)
        {
            if (dressed)
                foreach (var w in walls)
                {
                    var (bx, by, bz) = w.At(-0.04, -0.04, f * s.StoreyHeight);
                    add(Parts.Box(bx, by, bz, w.Width + 0.08, 0.05, 0.18, s.Dressing, w.Turn));
                }
            add(Parts.Box(x0 + t, y0 + t, z + f * s.StoreyHeight - 0.1, x1 - x0 - 2 * t, y1 - y0 - 2 * t, 0.2, "wood-planks"));
        }
        // The chimney, up through the east end of the roof.
        if (s.Chimney)
        {
            double ridge = Math.Min(x1 - x0, y1 - y0) / 2 * Math.Tan(s.Pitch * Math.PI / 180);
            add(Parts.Box(x1 - 1.2, (y0 + y1) / 2 - 0.4, z, 0.9, 0.8, h + ridge + 0.8, s.Walls.StartsWith("brick") ? s.Walls : "brick-red"));
        }
        return new RoofSpec(s.Style, s.Pitch, s.OverhangCm, s.RoofColour, false, s.Roof) { GableFinish = s.Walls };
    }
}
