namespace ToyCity.ClientModel.Voxels.Assets;

/// <summary>The kinds of part set into a wall.</summary>
public enum PartKind { Window, TallWindow, Door, GarageDoor, Shopfront }

/// <summary>
/// Polygon walls and the parts set into them (RC part 5): a wall is a slab with its openings cut through, and a
/// window or door is its opening plus a frame, glass or a door leaf, a sill or a step, as smooth shapes with their own
/// finishes. Everything is in metres; a part goes into a wall at a place along its front and up it.
/// </summary>
public static class Parts
{
    /// <summary>The usual size of each part (width, height, and its sill height above the wall's foot).</summary>
    public static (double W, double H, double Sill) SizeOf(PartKind k) => k switch
    {
        PartKind.Window => (1.0, 1.3, 0.9),
        PartKind.TallWindow => (0.9, 2.0, 0.5),
        PartKind.Door => (1.0, 2.2, 0),
        PartKind.GarageDoor => (2.6, 2.2, 0),
        _ => (3.0, 2.6, 0.3),
    };

    public static readonly (PartKind Kind, string Name)[] List =
    {
        (PartKind.Window, "Window"), (PartKind.TallWindow, "Tall window"), (PartKind.Door, "Door"),
        (PartKind.GarageDoor, "Garage door"), (PartKind.Shopfront, "Shopfront"),
    };

    /// <summary>
    /// A wall from (x0, y0) to (x1, y1) at height <paramref name="z"/>, its front to the left of the way it runs: going
    /// round a house north, east, south then west side (clockwise seen from above), every front faces out.
    /// </summary>
    public static Shape Wall(double x0, double y0, double x1, double y1, double z, double height, double thick, string finish)
    {
        double len = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
        return new Shape
        {
            Kind = ShapeKind.Wall, X = x0, Y = y0, Z = z, Turn = Math.Atan2(y1 - y0, x1 - x0),
            Width = Math.Max(0.05, len), Height = height, Depth = thick, Finish = finish, Colour = Finishes.Get(finish)?.Palette ?? AssetPalette.Concrete,
        };
    }

    /// <summary>A plain box: a wall with no openings, <paramref name="w"/> along, <paramref name="d"/> into, <paramref name="h"/> up.</summary>
    public static Shape Box(double x, double y, double z, double w, double d, double h, string finish, double turn = 0) => new()
    {
        Kind = ShapeKind.Wall, X = x, Y = y, Z = z, Turn = turn, Width = w, Depth = d, Height = h, Finish = finish,
        Colour = Finishes.Get(finish)?.Palette ?? AssetPalette.Concrete,
    };

    /// <summary>A box set into a wall, in the wall's own frame: along it, into it (negative stands proud of its front), up it.</summary>
    static Shape In(Shape wall, double along, double into, double up, double w, double d, double h, string finish)
    {
        var (x, y, z) = wall.At(along, into, up);
        return Box(x, y, z, w, d, h, finish, wall.Turn);
    }

    /// <summary>
    /// Cuts a part's opening into a wall at <paramref name="along"/> metres from its corner (the opening's left edge)
    /// and returns the part's shapes: frame, glass or leaf, sill or step. Null if it doesn't fit in the wall.
    /// </summary>
    public static List<Shape>? Insert(Shape wall, PartKind kind, double along, double? sill = null, double? width = null, double? height = null, string frame = "trim-white")
    {
        if (wall.Kind != ShapeKind.Wall) return null;
        var (dw, dh, ds) = SizeOf(kind);
        double w = width ?? dw, h = height ?? dh, z = sill ?? ds;
        if (along < 0.05 || along + w > wall.Width - 0.05 || z + h > wall.Height - 0.05 || z < 0) return null;
        // No two openings overlap.
        if (wall.Openings.Any(o => along < o.X + o.W + 0.1 && along + w > o.X - 0.1 && z < o.Z + o.H && z + h > o.Z)) return null;
        wall.Openings.Add((along, z, w, h));
        var parts = new List<Shape>();
        double t = wall.Depth, set = Math.Min(0.1, t * 0.4);   // how far the frame sits back from the wall's face
        const double f = 0.07, fd = 0.08;                      // frame width and depth
        bool glazed = kind is PartKind.Window or PartKind.TallWindow or PartKind.Shopfront;
        // The frame: a ring round the opening, its own wall with one opening.
        var ring = In(wall, along, set, z, w, fd, h, frame);
        ring.Openings.Add((f, f, w - 2 * f, h - 2 * f));
        parts.Add(ring);
        if (glazed)
        {
            parts.Add(In(wall, along + f, set + fd / 2 - 0.01, z + f, w - 2 * f, 0.02, h - 2 * f, "glass"));
            // Glazing bars: a window two panes across and two up; a shopfront's mullions every metre or so.
            int across = kind == PartKind.Shopfront ? Math.Max(2, (int)Math.Round(w)) : 2;
            for (int k = 1; k < across; k++)
                parts.Add(In(wall, along + w * k / across - 0.02, set + fd / 2 - 0.03, z + f, 0.04, 0.05, h - 2 * f, frame));
            int up = kind == PartKind.TallWindow ? 3 : kind == PartKind.Shopfront ? 1 : 2;
            for (int k = 1; k < up; k++)
                parts.Add(In(wall, along + f, set + fd / 2 - 0.03, z + h * k / up - 0.02, w - 2 * f, 0.05, 0.04, frame));
            // A shopfront has a stall riser under its glass; the rest a sill standing proud of the wall.
            if (kind != PartKind.Shopfront)
                parts.Add(In(wall, along - 0.05, -0.05, z - 0.06, w + 0.1, set + 0.05 + fd, 0.06, frame == "trim-white" ? "stone-ashlar" : frame));
        }
        else if (kind == PartKind.GarageDoor)
        {
            // Sections across, each a panel of the door.
            parts.Add(In(wall, along + f, set + fd / 2, z, w - 2 * f, 0.04, h - f, "siding-white"));
        }
        else
        {
            parts.Add(In(wall, along + f, set + fd / 2 - 0.02, z, w - 2 * f, 0.05, h - f, "wood-door"));
            // A stone step before it.
            parts.Add(In(wall, along - 0.15, -0.35, z, w + 0.3, 0.35 + set, 0.15, "stone-ashlar"));
        }
        return parts;
    }

    /// <summary>
    /// The same wall facing the other way: it runs back from its far end along what was its back, so its front is
    /// where its back was, its openings in the same places.
    /// </summary>
    public static Shape Flipped(Shape wall)
    {
        var f = wall.Clone();
        var (x, y, _) = wall.At(wall.Width, wall.Depth, 0);
        f.X = x; f.Y = y; f.Turn = wall.Turn + Math.PI;
        f.Openings.Clear();
        foreach (var o in wall.Openings) f.Openings.Add((wall.Width - o.X - o.W, o.Z, o.W, o.H));
        return f;
    }

    /// <summary>Takes a part's opening back out of a wall (the opening at <paramref name="along"/>, or the one nearest).</summary>
    public static bool Remove(Shape wall, double along)
    {
        if (wall.Openings.Count == 0) return false;
        var nearest = wall.Openings.OrderBy(o => Math.Abs(o.X + o.W / 2 - along)).First();
        return wall.Openings.Remove(nearest);
    }
}
