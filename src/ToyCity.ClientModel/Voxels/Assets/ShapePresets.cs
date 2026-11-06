namespace ToyCity.ClientModel.Voxels.Assets;

/// <summary>
/// Ready profiles for the editor's shape tool, in metres: a starting point a player edits, and what the example
/// rotunda is built from. Each is a shape at the origin; the editor moves it to where it's placed.
/// </summary>
public static class ShapePresets
{
    public static readonly string[] Names = { "Column", "Dome", "Tank", "Vase", "Ring", "Arch", "Cornice", "Rail" };

    /// <summary>A preset by name, in a colour; null for an unknown name.</summary>
    public static Shape? Make(string name, byte colour) => name switch
    {
        "Column" => Column(6, 0.35, colour),
        "Dome" => Dome(3, colour),
        "Tank" => Tank(2.5, 5, colour),
        "Vase" => Vase(colour),
        "Ring" => Ring(3, 0.5, 0.6, colour),
        "Arch" => Arch(3, 4.5, 0.6, colour),
        "Cornice" => Cornice(colour, new() { new(0, 0, 0), new(8, 0, 0) }),
        "Rail" => Rail(colour, new() { new(0, 0, 0), new(8, 0, 0) }),
        _ => null,
    };

    /// <summary>A classical column: a stepped base, a shaft narrowing a little up its height, and a flared capital.</summary>
    public static Shape Column(double height, double r, byte c)
    {
        double b = height * 0.06, cap = height * 0.08;
        return new Shape
        {
            Kind = ShapeKind.Lathe, Colour = c,
            Profile = new()
            {
                (0, 0), (r * 1.45, 0), (r * 1.45, b * 0.45), (r * 1.25, b * 0.6), (r * 1.15, b), (r, b * 1.2),
                (r * 0.86, height - cap * 1.1), (r * 0.95, height - cap * 0.9), (r * 1.2, height - cap * 0.35), (r * 1.4, height - cap * 0.25),
                (r * 1.4, height), (0, height),
            },
        };
    }

    /// <summary>A hemispherical dome on a short drum, closed underneath.</summary>
    public static Shape Dome(double r, byte c)
    {
        var s = new Shape { Kind = ShapeKind.Lathe, Colour = c };
        s.Profile.Add((0, 0));
        s.Profile.Add((r, 0));
        const int steps = 12;
        for (int i = 0; i <= steps; i++)
        {
            double a = Math.PI / 2 * i / steps;
            s.Profile.Add((i == steps ? 0 : r * Math.Cos(a), r * 0.2 + r * Math.Sin(a)));
        }
        return s;
    }

    /// <summary>A storage tank: a cylinder with a shallow domed top and a rim.</summary>
    public static Shape Tank(double r, double h, byte c)
    {
        var s = new Shape { Kind = ShapeKind.Lathe, Colour = c, Profile = new() { (0, 0), (r, 0), (r, h), (r * 1.04, h), (r * 1.04, h + 0.15) } };
        const int steps = 6;
        for (int i = 1; i <= steps; i++)
        {
            double a = Math.PI / 2 * i / steps;
            s.Profile.Add((i == steps ? 0 : r * Math.Cos(a), h + 0.15 + r * 0.3 * Math.Sin(a)));
        }
        return s;
    }

    /// <summary>A garden urn or a finial: a foot, a round belly and a lip.</summary>
    public static Shape Vase(byte c) => new()
    {
        Kind = ShapeKind.Lathe, Colour = c,
        Profile = new() { (0, 0), (0.3, 0), (0.3, 0.1), (0.15, 0.2), (0.35, 0.45), (0.45, 0.7), (0.4, 0.95), (0.25, 1.1), (0.35, 1.2), (0.35, 1.3), (0, 1.3) },
    };

    /// <summary>A ring round an axis, rectangular in section: a drum's band, a fountain's rim, a round wall.</summary>
    public static Shape Ring(double r, double thick, double h, byte c) => new()
    {
        Kind = ShapeKind.Lathe, Colour = c, Closed = true,
        Profile = new() { (r - thick, 0), (r, 0), (r, h), (r - thick, h) },
    };

    /// <summary>An arched opening in a wall (its front elevation, pulled back through the wall's thickness).</summary>
    public static Shape Arch(double width, double height, double thick, byte c)
    {
        double half = width / 2, frame = Math.Max(0.4, width * 0.18), spring = height - half;
        var s = new Shape { Kind = ShapeKind.Extrude, Axis = ExtrudeAxis.Back, Depth = thick, Colour = c };
        // Round the outside, then back along the inside of the opening, over its arch: one outline with no hole.
        s.Profile.Add((-half - frame, 0));
        s.Profile.Add((-half, 0));
        s.Profile.Add((-half, spring));
        const int steps = 12;
        for (int i = 1; i < steps; i++)
        {
            double a = Math.PI - Math.PI * i / steps;
            s.Profile.Add((half * Math.Cos(a), spring + half * Math.Sin(a)));
        }
        s.Profile.Add((half, spring));
        s.Profile.Add((half, 0));
        s.Profile.Add((half + frame, 0));
        s.Profile.Add((half + frame, height + frame));
        s.Profile.Add((-half - frame, height + frame));
        return s;
    }

    /// <summary>A moulded cornice along a path: a cyma curve under a flat top, its back on the path.</summary>
    public static Shape Cornice(byte c, List<System.Numerics.Vector3> path) => new()
    {
        Kind = ShapeKind.Sweep, Colour = c, Path = path,
        Profile = new() { (0, 0), (0.1, 0), (0.12, 0.08), (0.22, 0.15), (0.3, 0.28), (0.42, 0.34), (0.5, 0.36), (0.5, 0.5), (0, 0.5) },
    };

    /// <summary>A round handrail along a path.</summary>
    public static Shape Rail(byte c, List<System.Numerics.Vector3> path)
    {
        var s = new Shape { Kind = ShapeKind.Sweep, Colour = c, Path = path };
        for (int i = 0; i < 8; i++) { double a = 2 * Math.PI * i / 8; s.Profile.Add((0.04 * Math.Cos(a), 0.04 * Math.Sin(a))); }
        return s;
    }
}
