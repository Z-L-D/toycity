using static ToyCity.ClientModel.Voxels.Assets.AssetPalette;
using G = ToyCity.ClientModel.Voxels.Assets.Draw.Glazing;

namespace ToyCity.ClientModel.Voxels.Assets;

/// <summary>The realistic set's civic buildings: plants, services, parks, transit depots, landmarks and rewards.</summary>
public static partial class RealSet
{
    /// <summary>A bar from one point to another, about <paramref name="r"/> metres thick.</summary>
    static void Line(Draw dr, double x0, double y0, double z0, double x1, double y1, double z1, double r, byte c)
    {
        double len = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0) + (z1 - z0) * (z1 - z0));
        int n = Math.Max(1, (int)Math.Ceiling(len / (dr.V * 0.7)));
        for (int i = 0; i <= n; i++)
        {
            double f = i / (double)n, x = x0 + (x1 - x0) * f, y = y0 + (y1 - y0) * f, z = z0 + (z1 - z0) * f;
            dr.Box(x - r, y - r, z - r, x + r, y + r, z + r, c);
        }
    }

    /// <summary>A chain-link fence round the lot, a metre in from the edge, with a gate gap at the front.</summary>
    static void Perimeter(Draw dr, Lot l, byte c = Metal, double h = 2)
    {
        double a = 0.5, b = l.W - 0.5, e = l.D - 0.5;
        dr.Box(a, a, 0, b, a + dr.V, h, c);
        dr.Box(a, a, 0, a + dr.V, e, h, c);
        dr.Box(b - dr.V, a, 0, b, e, h, c);
        dr.Box(a, e - dr.V, 0, l.W / 2 - 3, e, h, c);
        dr.Box(l.W / 2 + 3, e - dr.V, 0, b, e, h, c);
    }

    /// <summary>Railway track along x: ballast, sleepers and two rails.</summary>
    static void Track(Draw dr, double x0, double x1, double y)
    {
        dr.Box(x0, y - 1.6, 0, x1, y + 1.6, 0.3, StoneDark);
        for (double x = x0 + 0.3; x < x1; x += 0.7) dr.Box(x, y - 1.3, 0.3, x + 0.25, y + 1.3, 0.45, WoodDark);
        dr.Box(x0, y - 0.8, 0.45, x1, y - 0.7, 0.65, Metal);
        dr.Box(x0, y + 0.7, 0.45, x1, y + 0.8, 0.65, Metal);
    }

    /// <summary>A railcar on a track along x: a box with windows and a roof.</summary>
    static void Railcar(Draw dr, double x, double y, double len, byte body, bool windows = true, double h = 3.8)
    {
        dr.Box(x, y - 1.4, 0.8, x + len, y + 1.4, h, body);
        if (windows)
            for (double wx = x + 1; wx < x + len - 1.5; wx += 2.2)
            {
                dr.Box(wx, y - 1.45, 2, wx + 1.4, y + 1.45, 3.1, Glass);
            }
        dr.Box(x + 0.2, y - 1.2, h, x + len - 0.2, y + 1.2, h + 0.3, Darker(body));
        dr.Box(x + 1, y - 1, 0.45, x + len - 1, y + 1, 0.8, Black);
    }

    static void Bus(Draw dr, double x, double y, byte body, bool alongY = false)
    {
        if (alongY)
        {
            dr.Box(x - 1.25, y, 0.4, x + 1.25, y + 12, 3.2, body);
            dr.Box(x - 1.3, y + 1, 1.6, x + 1.3, y + 11.5, 2.8, Glass);
            dr.Box(x - 1.3, y + 11.7, 1.1, x + 1.3, y + 12.05, 2.9, Glass);
            dr.Box(x - 1.2, y + 0.5, 3.2, x + 1.2, y + 11.5, 3.4, Trim);
        }
        else
        {
            dr.Box(x, y - 1.25, 0.4, x + 12, y + 1.25, 3.2, body);
            dr.Box(x + 1, y - 1.3, 1.6, x + 11.5, y + 1.3, 2.8, Glass);
            dr.Box(x + 0.5, y - 1.2, 3.2, x + 11.5, y + 1.2, 3.4, Trim);
        }
    }

    // ---- power ------------------------------------------------------------------------------------------------------

    static void CoalPlant(Lot l)
    {
        double stack = 100 + l.R.Next(15);
        var dr = l.Begin(stack + 1);
        dr.Ground(Soil);
        double W = l.W, D = l.D;
        // The boiler house, the turbine hall in front of it, and the stack behind.
        Shed(dr, W * 0.08, D * 0.15, W * 0.45, D * 0.4, 32, BrickOf(l), 1, MetalDark);
        dr.FlatRoof(W * 0.08, D * 0.15, W * 0.45, D * 0.4, 32, MetalDark, plant: true);
        Shed(dr, W * 0.08, D * 0.4, W * 0.55, D * 0.55, 18, Metal, 3, MetalDark);
        l.Roof = null;
        dr.FlatRoof(W * 0.08, D * 0.4, W * 0.55, D * 0.55, 18, MetalDark, plant: false);
        dr.Cyl(W * 0.25, D * 0.08, 4, 0, stack, Concrete, 2.8);
        for (double z = stack - 14; z < stack - 2; z += 5) dr.Cyl(W * 0.25, D * 0.08, 3.4, z, z + 2, Red);
        dr.Cyl(W * 0.25, D * 0.08, 2.3, stack - 1, stack, Black);
        // Two cooling towers.
        for (int i = 0; i < 2; i++)
            dr.Waisted(W * 0.6 + i * W * 0.26, D * 0.3, 10, 6.5, 8, 0, 48, Concrete);
        // The coal heap and a conveyor up to the boiler house.
        dr.Blob(W * 0.25, D * 0.8, 0, 13, 7, 6, Black, 0.2, StoneDark);
        for (int i = 0; i < 24; i++)
        {
            double f = i / 23.0;
            dr.Box(W * 0.25 - 0.8, D * 0.72 - f * D * 0.17, 4 + f * 22, W * 0.25 + 0.8, D * 0.72 - f * D * 0.17 + 1.6, 4 + f * 22 + 1.2, MetalDark);
        }
        Track(dr, 0, W, D - 4);
        for (double x = 2; x < W - 12; x += 10) Railcar(dr, x, D - 4, 9, MetalDark, false, 3);
    }

    static void GasPlant(Lot l)
    {
        var dr = l.Begin(45);
        dr.Ground(Concrete);
        double W = l.W, D = l.D;
        Shed(dr, 3, 3, W - 3, D * 0.4, 14, l.Pick(Metal, Cream, Draw.Ramp(9, 8)), 2, MetalDark);
        dr.FlatRoof(3, 3, W - 3, D * 0.4, 14, MetalDark);
        for (int i = 0; i < 3; i++)
        {
            double x = W * (0.2 + i * 0.3);
            dr.Box(x - 2.5, D * 0.4, 0, x + 2.5, D * 0.4 + 6, 10, Metal);
            dr.Cyl(x, D * 0.4 + 3, 1.8, 10, 40, Concrete, 1.5);
            dr.Cyl(x, D * 0.4 + 3, 1.2, 39.5, 40, Black);
        }
        for (int i = 0; i < 3; i++) dr.Cyl(W * (0.2 + i * 0.3), D * 0.8, 5, 0, 9, Trim);
        for (int i = 0; i < 3; i++) dr.Blob(W * (0.2 + i * 0.3), D * 0.8, 9, 5, 5, 2, Trim);
        Perimeter(dr, l);
    }

    static void HydroPlant(Lot l)
    {
        var dr = l.Begin(20);
        dr.Ground(Concrete);
        double W = l.W, D = l.D;
        double top = dr.Block(3, D * 0.35, W - 3, D * 0.75, 0, 2, 5, Concrete, ConcreteDark, G.Ribbon, 4, true, G.Punched);
        dr.FlatRoof(3, D * 0.35, W - 3, D * 0.75, top, ConcreteDark);
        // Penstocks down from the dam side, and the tailrace.
        for (int i = 0; i < 3; i++)
        {
            double x = W * (0.25 + i * 0.25);
            for (int s = 0; s < 14; s++) dr.Cyl(x, D * 0.35 - s * 0.8, 1.3, 2 + s * 0.9, 3.4 + s * 0.9, MetalDark);
        }
        dr.Box(2, D * 0.75, 0, W - 2, D * 0.82, dr.V * 2, Water);
        // The switchyard.
        for (double x = 4; x < W - 4; x += 5)
        {
            dr.Box(x, D - 6, 0, x + 2, D - 4, 2.5, Metal);
            dr.Box(x + 0.9, D - 5.2, 2.5, x + 1.1, D - 4.8, 7, MetalDark);
            dr.Box(x - 1, D - 5.1, 6.5, x + 3, D - 4.9, 6.7, MetalDark);
        }
    }

    static void WindTurbine(Lot l)
    {
        double hub = 40;
        var dr = l.Begin(hub + 22);
        dr.Ground(Grass);
        double c = l.W / 2, cy = l.D / 2;
        dr.Cyl(c, cy, 3, 0, 1, Concrete);
        dr.Cyl(c, cy, 1.4, 1, hub, Trim, 0.8);
        dr.Box(c - 1, cy - 4, hub - 1, c + 1, cy + 2, hub + 1.5, Trim);
        dr.Blob(c, cy + 2.6, hub, 0.9, 0.9, 0.9, Trim);
        // Three blades in the plane facing south, one pointing up.
        for (int i = 0; i < 3; i++)
        {
            double a = Math.PI / 2 + i * 2 * Math.PI / 3 + l.Variant * 0.4;
            Line(dr, c, cy + 2.8, hub, c + Math.Cos(a) * 19, cy + 2.8, hub + Math.Sin(a) * 19, 0.35, Trim);
        }
    }

    static void SolarFarm(Lot l)
    {
        var dr = l.Begin(6);
        dr.Ground(Grass);
        for (double y = 3; y < l.D - 4; y += 4.5)
            for (double x = 2; x < l.W - 6; x += 7)
            {
                for (int s = 0; s < 4; s++) dr.Box(x, y + s * 0.6, 0.8 + s * 0.4, x + 6.2, y + s * 0.6 + 0.6, 1.1 + s * 0.4, Draw.Ramp(9, 2));
                dr.Box(x + 1, y + 1, 0, x + 1.2, y + 1.2, 1.4, Metal);
                dr.Box(x + 5, y + 1, 0, x + 5.2, y + 1.2, 1.4, Metal);
            }
        dr.Box(l.W / 2 - 3, l.D - 4, 0, l.W / 2 + 3, l.D - 1, 3, Trim);
        Perimeter(dr, l);
    }

    // ---- water and waste --------------------------------------------------------------------------------------------

    static void WaterPump(Lot l)
    {
        var dr = l.Begin(20);
        dr.Ground(Grass);
        double W = l.W, D = l.D;
        dr.Block(4, D * 0.45, W * 0.55, D - 5, 0, 1, 5, BrickOf(l), Trim, G.Punched, 2.5);
        l.Roof = new RoofSpec(RoofStyle.Gable, 38, 40, Slate);
        // A water tower on legs.
        double tx = W * 0.75, ty = D * 0.35;
        foreach (var (dx, dy) in new[] { (-3.0, -3.0), (3.0, -3.0), (-3.0, 3.0), (3.0, 3.0) }) Line(dr, tx + dx, ty + dy, 0, tx + dx * 0.6, ty + dy * 0.6, 12, 0.3, Metal);
        dr.Cyl(tx, ty, 4.5, 12, 17, Draw.Ramp(9, 11));
        dr.Cyl(tx, ty, 4.5, 17, 19, Draw.Ramp(9, 11), 0.5);
        for (double x = 2; x < W * 0.55; x += 1.2) dr.Box(x, D * 0.35, 0, x + 1, D * 0.35 + 1, 0.8, MetalDark);
        Path(dr, W * 0.3 - 1, D - 5, W * 0.3 + 1, D);
    }

    static void SewagePlant(Lot l)
    {
        var dr = l.Begin(10);
        dr.Ground(Grass);
        double W = l.W, D = l.D;
        // Round clarifiers, rectangular aeration tanks, a control building.
        for (int i = 0; i < 2; i++)
        {
            double cx = W * 0.25 + i * W * 0.3, cy = D * 0.3, r = Math.Min(W, D) * 0.12;
            dr.Cyl(cx, cy, r, 0, 1.6, Concrete, null, true);
            dr.Cyl(cx, cy, r - 0.3, 0, 1.2, Draw.Ramp(4, 4));
            Line(dr, cx - r, cy, 1.8, cx + r, cy, 1.8, 0.25, Yellow);
        }
        for (double y = D * 0.55; y < D * 0.8; y += 4)
        {
            dr.Box(3, y, 0, W * 0.7, y + 3.5, 1.5, Concrete);
            dr.Box(3.3, y + 0.3, 0, W * 0.7 - 0.3, y + 3.2, 1.3, Draw.Ramp(3, 4));
        }
        double top = dr.Block(W * 0.75, D * 0.5, W - 3, D - 4, 0, 2, 3.5, Trim, Concrete, G.Punched, 3);
        dr.FlatRoof(W * 0.75, D * 0.5, W - 3, D - 4, top, Concrete);
        Perimeter(dr, l);
    }

    static void WaterTreatment(Lot l)
    {
        var dr = l.Begin(14);
        dr.Ground(Grass);
        double W = l.W, D = l.D;
        for (double x = 3; x < W * 0.6; x += 7)
        {
            dr.Box(x, 3, 0, x + 6, D * 0.55, 1.6, Concrete);
            dr.Box(x + 0.3, 3.3, 0, x + 5.7, D * 0.55 - 0.3, 1.4, Water);
        }
        double top = dr.Block(W * 0.65, 3, W - 3, D * 0.5, 0, 2, 4, Cream, Trim, G.Punched, 3);
        l.Roof = new RoofSpec(RoofStyle.Hip, 25, 40, RoofTile);
        _ = top;
        dr.Cyl(W * 0.3, D * 0.78, 5, 0, 10, Draw.Ramp(9, 11));
        dr.Cyl(W * 0.3, D * 0.78, 5, 10, 11, Trim, 4);
        Perimeter(dr, l);
    }

    static void Landfill(Lot l)
    {
        var dr = l.Begin(16);
        dr.Ground(Soil);
        double W = l.W, D = l.D;
        byte[] junk = { Draw.Ramp(2, 5), StoneDark, Draw.Ramp(5, 3), Soil, Draw.Ramp(1, 6), Metal };
        for (int i = 0; i < 5; i++)
            dr.Blob(l.Between(W * 0.2, W * 0.8), l.Between(D * 0.2, D * 0.7), 0, l.Between(8, 14), l.Between(7, 12), l.Between(5, 10), junk[i % junk.Length], 0.3, junk[(i + 2) % junk.Length]);
        // Grassed-over cells at the back, the tip face, a compactor and a truck.
        dr.Blob(W * 0.5, 4, 0, W * 0.45, 7, 6, Grass, 0.1);
        dr.Box(W * 0.6, D - 12, 0, W * 0.6 + 6, D - 9, 3.5, Yellow);
        Truck(dr, l, W * 0.2, D - 8);
        Perimeter(dr, l, Draw.Ramp(5, 4), 2.5);
    }

    static void ToxicDump(Lot l)
    {
        var dr = l.Begin(8);
        dr.Ground(Soil);
        double W = l.W, D = l.D;
        for (int i = 0; i < 3; i++)
        {
            double cx = l.Between(W * 0.2, W * 0.8), cy = l.Between(D * 0.2, D * 0.6);
            dr.Blob(cx, cy, -1, l.Between(4, 7), l.Between(3, 6), 1.3, Draw.Ramp(4, 9));
        }
        for (int i = 0; i < 70; i++)
        {
            double x = l.Between(3, W - 3), y = l.Between(3, D - 5);
            dr.Cyl(x, y, 0.45, 0, 1.2, l.Pick(Yellow, Yellow, Draw.Ramp(4, 6), Black, Red));
        }
        for (double x = 4; x < W - 4; x += 12) { dr.Box(x, D - 3, 0, x + 0.15, D - 2.9, 3, MetalDark); dr.Box(x - 0.8, D - 3.05, 2.2, x + 1, D - 2.95, 3, Yellow); }
        Perimeter(dr, l, MetalDark, 3);
    }

    // ---- services ---------------------------------------------------------------------------------------------------

    static void Police(Lot l)
    {
        var dr = l.Begin(14);
        dr.Ground(Concrete);
        double W = l.W, D = l.D;
        double top = dr.Block(3, D * 0.3, W - 3, D - 6, 0, 2, 3.8, BrickOf(l), Blue, G.Punched, 3);
        dr.FlatRoof(3, D * 0.3, W - 3, D - 6, top, Blue);
        Sign(dr, W / 2 - 5, W / 2 + 5, D - 6, 3.2, 0.8, Blue, Trim);
        dr.Box(W / 2 - 3, D - 6, 2.6, W / 2 + 3, D - 4, 2.9, MetalDark);
        for (double x = 3; x < W - 5; x += 3.2)
            dr.Prop("car", x + 1.3, D * 0.15, 1, dr.V, c => c == Red ? ((int)x & 2) == 0 ? Trim : Black : c);
        dr.Box(3, D - 2, 0, 3.15, D - 1.85, 9, Chrome);
        dr.Box(3.15, D - 2, 7.5, 5, D - 1.95, 9, Blue);
        dr.Box(W - 6, D * 0.3 - 0.5, top, W - 5, D * 0.3 + 0.5, top + 6, MetalDark);
    }

    static void FireStation(Lot l)
    {
        var dr = l.Begin(18);
        dr.Ground(Concrete);
        double W = l.W, D = l.D, y1 = D - 8, y0 = D * 0.25;
        byte wall = l.Pick(Brick, Draw.Ramp(0, 4));
        double top = dr.Block(3, y0, W - 8, y1, 0, 2, 4.2, wall, Trim, G.Punched, 3, false);
        dr.FlatRoof(3, y0, W - 8, y1, top, Trim);
        // Three bay doors, one open with an engine nosing out.
        for (int i = 0; i < 3; i++)
        {
            double a = 4 + i * (W - 13) / 3, b = a + (W - 13) / 3 - 1;
            dr.Box(a, y1 - 0.4, 0, b, y1, 3.8, i == 1 ? (byte)0 : Red);
            for (double z = 0.5; z < 3.8 && i != 1; z += 0.5) dr.Box(a, y1 - dr.V, z, b, y1, z + dr.V, Draw.Ramp(0, 5));
            if (i == 1)
            {
                dr.Box(a + 0.6, y1 - 6, 0.5, b - 0.6, y1 + 2.5, 3.2, Red);
                dr.Box(a + 0.6, y1 + 1.2, 1.8, b - 0.6, y1 + 2.55, 2.9, Glass);
                dr.Box(a + 0.8, y1 - 5, 3.2, b - 0.8, y1, 3.5, Chrome);
            }
        }
        // The hose tower.
        double tt = dr.Block(W - 8, y0, W - 3, y0 + 5, 0, 4, 3.8, wall, Trim, G.Punched, 2.5, false);
        dr.FlatRoof(W - 8, y0, W - 3, y0 + 5, tt, Trim, plant: false);
        Sign(dr, 5, W - 10, y1, top - 1.4, 1, Trim, Red);
        dr.Prop("lamp", W - 2, D - 1);
    }

    static void Clinic(Lot l)
    {
        var dr = l.Begin(12);
        dr.Ground(Grass);
        double W = l.W, D = l.D;
        double top = dr.Block(3, D * 0.4, W - 3, D - 6, 0, 2, 3.6, Trim, Draw.Ramp(9, 9), G.Ribbon, 3);
        dr.FlatRoof(3, D * 0.4, W - 3, D - 6, top, Trim);
        double cx = W / 2, cz = top - 1.6;
        dr.Box(cx - 0.4, D - 6, cz - 1.2, cx + 0.4, D - 5.8, cz + 1.2, Red);
        dr.Box(cx - 1.2, D - 6, cz - 0.4, cx + 1.2, D - 5.8, cz + 0.4, Red);
        dr.Box(cx - 3, D - 6, 2.8, cx + 3, D - 3.5, 3.1, Trim);
        dr.CarPark(2, 1, W - 2, D * 0.4 - 1, 0.5);
        Path(dr, cx - 1.5, D - 6, cx + 1.5, D);
        Trees(l, dr, 2, 1.5, D - 4, W - 1.5, D - 1, 0.2, 0.7);
    }

    static void Hospital(Lot l)
    {
        int storeys = 7 + l.R.Next(3);
        var dr = l.Begin(storeys * 3.6 + 8);
        dr.Ground(Grass);
        double W = l.W, D = l.D;
        double top = dr.Block(W * 0.25, D * 0.15, W * 0.75, D * 0.55, 0, storeys, 3.6, Trim, Draw.Ramp(9, 10), G.Ribbon, 3);
        dr.FlatRoof(W * 0.25, D * 0.15, W * 0.75, D * 0.55, top, Trim, plant: false);
        // A helipad on the roof.
        dr.Cyl(W * 0.5, D * 0.35, 6, top, top + 0.4, ConcreteDark);
        dr.Box(W * 0.5 - 2, D * 0.35 - 0.3, top + 0.4, W * 0.5 + 2, D * 0.35 + 0.3, top + 0.5, Trim);
        dr.Box(W * 0.5 - 2, D * 0.35 - 2.5, top + 0.4, W * 0.5 - 1.4, D * 0.35 + 2.5, top + 0.5, Trim);
        dr.Box(W * 0.5 + 1.4, D * 0.35 - 2.5, top + 0.4, W * 0.5 + 2, D * 0.35 + 2.5, top + 0.5, Trim);
        // Low wings either side and the emergency entrance.
        foreach (var (a, b) in new[] { (3.0, W * 0.25), (W * 0.75, W - 3) })
        {
            double wt = dr.Block(a, D * 0.2, b, D * 0.7, 0, 2, 3.6, Trim, Draw.Ramp(9, 10), G.Ribbon, 3);
            dr.FlatRoof(a, D * 0.2, b, D * 0.7, wt, Trim);
        }
        dr.Box(W * 0.3, D * 0.55, 3.4, W * 0.7, D * 0.55 + 5, 3.8, Red);
        Sign(dr, W * 0.35, W * 0.65, D * 0.55 + 5, 3.8, 1, Red, Trim);
        dr.CarPark(3, D * 0.75, W - 3, D - 2, 0.6);
    }

    static void ElementarySchool(Lot l)
    {
        var dr = l.Begin(12);
        dr.Ground(Grass);
        double W = l.W, D = l.D;
        byte wall = BrickOf(l);
        dr.Block(3, 3, W * 0.6, D * 0.45, 0, 2, 3.5, wall, Trim, G.Punched, 2.4);
        dr.Block(3, D * 0.45, W * 0.3, D - 5, 0, 2, 3.5, wall, Trim, G.Punched, 2.4);
        l.Roof = new RoofSpec(RoofStyle.Hip, 24, 50, l.Pick(Slate, RoofTile));
        // A playground: tarmac with a climbing frame and a slide, and a playing field.
        Path(dr, W * 0.35, D * 0.55, W - 3, D - 3, Asphalt);
        dr.Box(W * 0.45, D * 0.6, 0, W * 0.45 + 0.15, D * 0.6 + 0.15, 2.5, Red);
        dr.Box(W * 0.45 + 3, D * 0.6, 0, W * 0.45 + 3.15, D * 0.6 + 0.15, 2.5, Red);
        dr.Box(W * 0.45, D * 0.6, 2.5, W * 0.45 + 3.15, D * 0.6 + 0.15, 2.65, Red);
        for (int i = 0; i < 10; i++) dr.Box(W * 0.6 + i * 0.4, D * 0.65, 2.2 - i * 0.22, W * 0.6 + i * 0.4 + 0.4, D * 0.65 + 0.8, 2.4 - i * 0.22, Yellow);
        Trees(l, dr, 3, W * 0.65, 2, W - 2, D * 0.5, 0.3);
        dr.Box(W * 0.3 - 0.1, D - 2, 0, W * 0.3, D - 1.9, 8, Chrome);
    }

    static void HighSchool(Lot l)
    {
        var dr = l.Begin(16);
        dr.Ground(Grass);
        double W = l.W, D = l.D;
        byte wall = l.Pick(BrickOf(l), Cream);
        double top = dr.Block(3, 3, W * 0.65, D * 0.3, 0, 3, 3.6, wall, Trim, G.Ribbon, 3);
        dr.FlatRoof(3, 3, W * 0.65, D * 0.3, top, Trim);
        dr.Block(W * 0.68, 3, W - 3, D * 0.3, 0, 1, 8, wall, Trim, G.Ribbon, 4, true);
        l.Roof = null;
        dr.FlatRoof(W * 0.68, 3, W - 3, D * 0.3, 8, MetalDark, plant: false);
        // A running track round a football pitch.
        double fx0 = 4, fx1 = W - 4, fy0 = D * 0.38, fy1 = D - 4;
        Path(dr, fx0, fy0, fx1, fy1, Draw.Ramp(0, 7));
        dr.Box(fx0 + 3, fy0 + 3, 0, fx1 - 3, fy1 - 3, dr.V * 1.5, Draw.Ramp(5, 6));
        dr.Box((fx0 + fx1) / 2 - 0.1, fy0 + 3, 0, (fx0 + fx1) / 2 + 0.1, fy1 - 3, dr.V * 2, Trim);
        foreach (double gx in new[] { fx0 + 3, fx1 - 3.15 }) dr.Box(gx, (fy0 + fy1) / 2 - 3, 0, gx + 0.15, (fy0 + fy1) / 2 + 3, 2.4, Trim);
    }

    // ---- parks ------------------------------------------------------------------------------------------------------

    static void SmallPark(Lot l)
    {
        var dr = l.Begin(10);
        dr.Ground(Grass);
        Path(dr, l.W / 2 - 1, 0, l.W / 2 + 1, l.D, Sand);
        Path(dr, 0, l.D / 2 - 1, l.W, l.D / 2 + 1, Sand);
        Trees(l, dr, 4, 1.5, 1.5, l.W / 2 - 2, l.D / 2 - 2, 0.3);
        Trees(l, dr, 3, l.W / 2 + 2, l.D / 2 + 2, l.W - 1.5, l.D - 1.5, 0.3);
        dr.Prop("bench", l.W / 2 + 3, l.D / 2 - 2);
        dr.Prop("bench", l.W / 2 - 3, l.D / 2 + 2);
        dr.Prop("lamp", l.W / 2 + 1.5, l.D / 2 + 1.5);
        dr.Prop("hedge", 3, l.D - 2);
    }

    static void PocketPark(Lot l)
    {
        var dr = l.Begin(9);
        dr.Ground(Stone);
        dr.Box(2, 2, 0, l.W - 2, l.D - 5, 0.6, Grass);
        dr.Box(2, 2, 0, l.W - 2, 2.3, 0.7, StoneDark);
        dr.Tree(l.W / 2, l.D / 2 - 1, false, 1.1);
        for (double x = 2; x < l.W - 2; x += 3) dr.Prop("planter", x + 1, l.D - 3);
        dr.Prop("bench", l.W / 2, l.D - 1.5);
        dr.Prop("table", 3, l.D - 1.5);
    }

    static void Plaza(Lot l)
    {
        var dr = l.Begin(10);
        dr.Ground(Stone);
        double cx = l.W / 2, cy = l.D / 2;
        for (double x = 0; x < l.W; x += 4) for (double y = 0; y < l.D; y += 4) if (((int)(x / 4 + y / 4) & 1) == 0) dr.Box(x, y, 0, x + 4, y + 4, dr.V, Draw.Ramp(2, 10));
        dr.Cyl(cx, cy, 5, 0, 0.8, StoneDark);
        dr.Cyl(cx, cy, 4.6, 0.3, 0.7, Water);
        dr.Cyl(cx, cy, 0.6, 0.7, 3, Stone);
        dr.Cyl(cx, cy, 1.5, 3, 3.3, Stone);
        dr.Blob(cx, cy, 3.5, 0.6, 0.6, 1, Water);
        foreach (var (dx, dy) in new[] { (-1, -1), (1, -1), (-1, 1), (1, 1) })
        {
            dr.Tree(cx + dx * l.W * 0.33, cy + dy * l.D * 0.33, false, 0.8);
            dr.Prop("bench", cx + dx * 7, cy + dy * 3, dx > 0 ? 1 : 3);
            dr.Prop("lamp", cx + dx * 9, cy + dy * 9);
        }
    }

    static void LargePark(Lot l)
    {
        var dr = l.Begin(12);
        dr.Ground(Grass);
        double W = l.W, D = l.D;
        // A pond, winding paths, woods and a bandstand.
        dr.Blob(W * 0.65, D * 0.35, -0.5, W * 0.2, D * 0.13, 1, Water);
        for (int i = 0; i < 60; i++)
        {
            double t = i / 59.0, x = W * 0.1 + t * W * 0.8, y = D * 0.6 + Math.Sin(t * 6) * D * 0.12;
            dr.Box(x - 0.9, y - 0.9, 0, x + 0.9, y + 0.9, dr.V, Sand);
        }
        Path(dr, W * 0.3 - 0.9, D * 0.6, W * 0.3 + 0.9, D, Sand);
        Trees(l, dr, 16, 2, 2, W * 0.45, D * 0.45, 0.45);
        Trees(l, dr, 8, W * 0.55, D * 0.7, W - 2, D - 2, 0.3);
        double bx = W * 0.25, by = D * 0.82;
        dr.Cyl(bx, by, 4, 0, 0.6, Stone);
        for (int i = 0; i < 6; i++) { double a = i * Math.PI / 3; dr.Box(bx + Math.Cos(a) * 3.4 - 0.15, by + Math.Sin(a) * 3.4 - 0.15, 0.6, bx + Math.Cos(a) * 3.4 + 0.15, by + Math.Sin(a) * 3.4 + 0.15, 3.2, Trim); }
        dr.Cyl(bx, by, 4.3, 3.2, 5, Draw.Ramp(5, 4), 0.3);
        for (double x = 6; x < W - 6; x += 9) dr.Prop("bench", x, D * 0.6 + 2.4);
    }

    // ---- transit ----------------------------------------------------------------------------------------------------

    static void BusDepot(Lot l)
    {
        var dr = l.Begin(10);
        dr.Ground(Asphalt);
        double W = l.W, D = l.D;
        Shed(dr, 2, 2, W - 2, D * 0.55, 6.5, l.Pick(Cream, Metal, BrickOf(l)), Math.Max(3, (int)(W / 7)), MetalDark);
        dr.FlatRoof(2, 2, W - 2, D * 0.55, 6.5, MetalDark, plant: false);
        byte body = l.Pick(Yellow, Red, Blue, Draw.Ramp(5, 6));
        for (double x = 3; x < W - 4; x += 4) Bus(dr, x + 1.3, D * 0.55 + 1.5, body, true);
    }

    static void RailStation(Lot l)
    {
        var dr = l.Begin(14);
        dr.Ground(Stone);
        double W = l.W, D = l.D;
        Track(dr, 0, W, 4);
        Track(dr, 0, W, 12);
        // Platforms with canopies between the tracks and at the front.
        dr.Box(0, 6, 0, W, 10, 1, Concrete);
        for (double x = 2; x < W - 2; x += 5) dr.Box(x, 7.9, 1, x + 0.2, 8.1, 4, MetalDark);
        dr.Box(1, 6.2, 4, W - 1, 9.8, 4.3, Draw.Ramp(5, 4));
        double top = dr.Block(4, 15, W - 4, D - 3, 0, 1, 5, BrickOf(l), Trim, G.Punched, 3);
        l.Roof = new RoofSpec(RoofStyle.Gable, 35, 50, Slate);
        _ = top;
        dr.Cyl(W / 2, D - 2.8, 0.7, 3.5, 4.9, Trim);
        Railcar(dr, 2, 12, W * 0.6, l.Pick(Red, Blue, Draw.Ramp(5, 5)));
    }

    static void SubwayStation(Lot l)
    {
        var dr = l.Begin(8);
        dr.Ground(Stone);
        double cx = l.W / 2, cy = l.D * 0.55;
        // A glazed entrance pavilion over the stairs down, a sign mast and benches.
        dr.Box(cx - 5, cy - 4, 0, cx + 5, cy + 4, 0.3, StoneDark);
        dr.Box(cx - 2, cy - 3, -0.1, cx + 2, cy + 3, 0.31, Black);
        for (int s = 0; s < 6; s++) dr.Box(cx - 2, cy - 3 + s * 1, 0, cx + 2, cy - 2 + s * 1, Math.Max(dr.V, 0.3 - s * 0.05), Concrete);
        dr.Box(cx - 3, cy - 4, 0, cx + 3, cy + 4, 3.2, Glass);
        dr.Box(cx - 2.9, cy - 3.9, 0, cx + 2.9, cy + 3.9, 3.1, 0);
        dr.Box(cx - 2, cy + 3.8, 0, cx + 2, cy + 4, 2.6, 0);
        dr.Box(cx - 3.2, cy - 4.2, 3.2, cx + 3.2, cy + 4.2, 3.5, MetalDark);
        dr.Box(cx + 5, cy + 4, 0, cx + 5.2, cy + 4.2, 5, MetalDark);
        dr.Cyl(cx + 5.1, cy + 4.1, 0.8, 5, 6.2, Red);
        Trees(l, dr, 3, 2, 2, l.W - 2, cy - 6, 0.2, 0.8);
        dr.Prop("bench", cx - 6, cy + 2, 1);
    }

    static void Depot(Lot l, byte body, double carLen, bool tram)
    {
        var dr = l.Begin(10);
        dr.Ground(StoneDark);
        double W = l.W, D = l.D;
        int tracks = Math.Max(2, (int)((D - 4) / 5));
        for (int i = 0; i < tracks; i++) Track(dr, 0, W, 3 + i * 5);
        // The shed over the back tracks.
        double sd = Math.Min(D * 0.5, 3 + 5 * (tracks / 2) - 1);
        dr.Box(W * 0.45, 0.5, 0, W - 1, 0.8, 7, l.Pick(BrickOf(l), Metal));
        dr.Box(W - 1.3, 0.5, 0, W - 1, sd, 7, l.Pick(BrickOf(l), Metal));
        for (double x = W * 0.45; x < W - 1; x += 6) dr.Box(x, sd - 0.3, 0, x + 0.3, sd, 7, MetalDark);
        dr.Box(W * 0.45, 0.5, 7, W - 1, sd, 7.3, MetalDark);
        for (int i = 0; i < tracks; i++)
            if (l.R.Next(3) > 0) Railcar(dr, l.Between(2, Math.Max(2.5, W - carLen - 2)), 3 + i * 5, carLen, body, true, tram ? 3.4 : 3.8);
        if (tram) for (int i = 0; i < tracks; i++) dr.Box(0, 3 + i * 5 - 0.05, 5.6, W * 0.45, 3 + i * 5 + 0.05, 5.7, Black);
    }

    static void TramDepot(Lot l) => Depot(l, l.Pick(Red, Yellow, Draw.Ramp(5, 6)), 18, true);
    static void RailYard(Lot l) => Depot(l, l.Pick(Blue, Red, Draw.Ramp(9, 6)), 22, false);
    static void SubwayYard(Lot l) => Depot(l, l.Pick(Chrome, Metal), 18, false);

    static void FreightDepot(Lot l)
    {
        var dr = l.Begin(18);
        dr.Ground(Concrete);
        double W = l.W, D = l.D;
        Shed(dr, 2, 2, W * 0.5, D * 0.45, 8, Metal, 3, Draw.Ramp(9, 5));
        dr.FlatRoof(2, 2, W * 0.5, D * 0.45, 8, MetalDark, plant: false);
        // Stacks of containers and a gantry crane over them.
        byte[] cols = { Red, Blue, Draw.Ramp(5, 5), Yellow, Draw.Ramp(1, 6), Draw.Ramp(10, 5), Trim };
        for (double x = W * 0.55; x < W - 7; x += 6.4)
            for (double y = 3; y < D - 6; y += 2.8)
            {
                int high = 1 + l.R.Next(3);
                for (int s = 0; s < high; s++) dr.Box(x, y, s * 2.6, x + 6.1, y + 2.5, s * 2.6 + 2.55, cols[l.R.Next(cols.Length)]);
            }
        double cx = W * 0.75;
        dr.Box(W * 0.53, 2, 0, W * 0.53 + 0.6, 2.6, 12, Yellow);
        dr.Box(W - 3, 2, 0, W - 2.4, 2.6, 12, Yellow);
        dr.Box(W * 0.53, D - 6, 0, W * 0.53 + 0.6, D - 5.4, 12, Yellow);
        dr.Box(W - 3, D - 6, 0, W - 2.4, D - 5.4, 12, Yellow);
        dr.Box(W * 0.53, 2, 12, W - 2.4, 2.6, 13, Yellow);
        dr.Box(W * 0.53, D - 6, 12, W - 2.4, D - 5.4, 13, Yellow);
        dr.Box(cx - 1.5, 2, 13, cx + 1.5, D - 5.4, 13.8, Yellow);
        Truck(dr, l, 4, D * 0.45 + 3);
        Track(dr, 0, W, D - 2.5);
    }

    // ---- landmarks and rewards -------------------------------------------------------------------------------------

    /// <summary>A figure on a plinth: legs, a coat, arms, a head and a hat, in two colours.</summary>
    static void Figure(Draw dr, double cx, double cy, double z, double s, byte coat, byte trousers, byte skin, byte hat, bool tall)
    {
        dr.Box(cx - 0.5 * s, cy - 0.3 * s, z, cx - 0.1 * s, cy + 0.3 * s, z + 1.6 * s, trousers);
        dr.Box(cx + 0.1 * s, cy - 0.3 * s, z, cx + 0.5 * s, cy + 0.3 * s, z + 1.6 * s, trousers);
        dr.Box(cx - 0.6 * s, cy - 0.4 * s, z + 1.6 * s, cx + 0.6 * s, cy + 0.4 * s, z + 3.2 * s, coat);
        dr.Box(cx - 0.9 * s, cy - 0.25 * s, z + 1.7 * s, cx - 0.6 * s, cy + 0.25 * s, z + 3.1 * s, coat);
        dr.Box(cx + 0.6 * s, cy - 0.25 * s, z + 1.7 * s, cx + 0.9 * s, cy + 0.25 * s, z + 3.1 * s, coat);
        dr.Blob(cx, cy, z + 3.6 * s, 0.4 * s, 0.4 * s, 0.45 * s, skin);
        if (tall) dr.Box(cx - 0.4 * s, cy - 0.4 * s, z + 3.9 * s, cx + 0.4 * s, cy + 0.4 * s, z + 4.9 * s, hat);
        else dr.Box(cx - 0.5 * s, cy - 0.5 * s, z + 3.9 * s, cx + 0.5 * s, cy + 0.5 * s, z + 4.1 * s, hat);
    }

    static void ToySoldier(Lot l)
    {
        var dr = l.Begin(14);
        dr.Ground(Grass);
        double cx = l.W / 2, cy = l.D / 2;
        Path(dr, cx - 4, cy - 4, cx + 4, cy + 4, Stone);
        dr.Box(cx - 1.8, cy - 1.8, 0, cx + 1.8, cy + 1.8, 2, StoneDark);
        Figure(dr, cx, cy, 2, 2.2, Red, Blue, Draw.Ramp(1, 11), Black, true);
        dr.Box(cx + 1.8, cy - 0.15, 3, cx + 2.1, cy + 0.15, 9, Chrome);
        foreach (var (dx, dy) in new[] { (-5.0, -5.0), (5.0, 5.0) }) dr.Tree(cx + dx, cy + dy, true, 0.7);
    }

    static void MayorStatue(Lot l)
    {
        var dr = l.Begin(13);
        dr.Ground(Grass);
        double cx = l.W / 2, cy = l.D / 2;
        Path(dr, cx - 4, cy - 4, cx + 4, cy + 4, Stone);
        dr.Box(cx - 2, cy - 2, 0, cx + 2, cy + 2, 2.4, Stone);
        Figure(dr, cx, cy, 2.4, 2, Draw.Ramp(2, 6), Draw.Ramp(2, 5), Draw.Ramp(2, 6), Draw.Ramp(2, 5), true);
        dr.Box(cx + 0.9 * 2, cy - 0.2, 2.4 + 3 * 2, cx + 1.4 * 2, cy + 0.2, 2.4 + 4.5 * 2, Draw.Ramp(2, 6));
        foreach (var (dx, dy) in new[] { (-5.0, 5.0), (5.0, 5.0) }) dr.Prop("planter", cx + dx, cy + dy);
    }

    static void DuckFountain(Lot l)
    {
        var dr = l.Begin(14);
        dr.Ground(Stone);
        double cx = l.W / 2, cy = l.D / 2;
        dr.Cyl(cx, cy, 11, 0, 1, StoneDark);
        dr.Cyl(cx, cy, 10.5, 0.4, 0.9, Water);
        // The duck: a body, a head, a beak, eyes.
        dr.Blob(cx, cy + 1, 3, 5, 6, 3.6, Yellow);
        dr.Blob(cx, cy - 2, 6, 1.8, 2, 0.6, Yellow);
        dr.Blob(cx, cy - 3.8, 8.5, 2.6, 2.6, 2.6, Yellow);
        dr.Blob(cx, cy - 6.4, 8.2, 1.4, 1.6, 0.6, Draw.Ramp(2, 7));
        dr.Box(cx - 1.4, cy - 6.1, 9.2, cx - 0.8, cy - 5.9, 9.8, Black);
        dr.Box(cx + 0.8, cy - 6.1, 9.2, cx + 1.4, cy - 5.9, 9.8, Black);
        dr.Blob(cx, cy + 6.8, 4.6, 1.5, 1.4, 1.3, Yellow);
        // Jets of water round it.
        for (int i = 0; i < 8; i++) { double a = i * Math.PI / 4; dr.Cyl(cx + Math.Cos(a) * 8.5, cy + Math.Sin(a) * 8.5, 0.2, 0.9, 3, Draw.Ramp(8, 13)); }
        foreach (var (dx, dy) in new[] { (-1, -1), (1, -1), (-1, 1), (1, 1) }) dr.Prop("bench", cx + dx * 13, cy + dy * 13, dx > 0 ? 1 : 3);
    }

    static void Clocktower(Lot l)
    {
        double h = 42;
        var dr = l.Begin(h + 10);
        dr.Ground(Stone);
        double cx = l.W / 2, cy = l.D / 2, s = 4.5;
        dr.Box(cx - s - 1, cy - s - 1, 0, cx + s + 1, cy + s + 1, 3, StoneDark);
        dr.Box(cx - s, cy - s, 3, cx + s, cy + s, h, Stone);
        for (double z = 6; z < h - 8; z += 6) dr.Box(cx - s - 0.2, cy - s - 0.2, z, cx + s + 0.2, cy + s + 0.2, z + 0.5, StoneDark);
        // A clock face on each side.
        double fz = h - 5;
        foreach (var (nx, ny) in new[] { (0, 1), (0, -1), (1, 0), (-1, 0) })
        {
            for (int a = 0; a < 48; a++)
            {
                double t = a * Math.PI / 24, r = 3;
                double u = Math.Cos(t) * r, v = Math.Sin(t) * r;
                double fx = cx + (ny != 0 ? u : nx * (s + 0.1)), fy = cy + (nx != 0 ? u : ny * (s + 0.1));
                dr.Box(fx - 0.2, fy - 0.2, fz + v - 0.2, fx + 0.2, fy + 0.2, fz + v + 0.2, Yellow);
            }
            double px = cx + nx * (s + 0.05), py = cy + ny * (s + 0.05);
            dr.Box(px - (ny != 0 ? 2.8 : 0.1), py - (nx != 0 ? 2.8 : 0.1), fz - 2.8, px + (ny != 0 ? 2.8 : 0.1), py + (nx != 0 ? 2.8 : 0.1), fz + 2.8, Trim);
            dr.Box(px - (ny != 0 ? 0.15 : 0.2), py - (nx != 0 ? 0.15 : 0.2), fz, px + (ny != 0 ? 0.15 : 0.2), py + (nx != 0 ? 0.15 : 0.2), fz + 2.2, Black);
            dr.Box(px - (ny != 0 ? 0 : 0.2), py - (nx != 0 ? 0 : 0.2), fz - 0.15, px + (ny != 0 ? 1.6 : 0.2), py + (nx != 0 ? 1.6 : 0.2), fz + 0.15, Black);
        }
        // A pyramid cap and a big wind-up key on the side.
        for (int i = 0; i < 8; i++) dr.Box(cx - s + i * 0.55, cy - s + i * 0.55, h + i, cx + s - i * 0.55, cy + s - i * 0.55, h + i + 1, Slate);
        dr.Cyl(cx, cy, 0.25, h + 8, h + 10, Yellow);
        dr.Box(cx + s, cy - 0.3, h * 0.45 - 0.3, cx + s + 2, cy + 0.3, h * 0.45 + 0.3, Yellow);
        dr.Box(cx + s + 2, cy - 2, h * 0.45 - 1.2, cx + s + 2.4, cy + 2, h * 0.45 + 1.2, Yellow);
    }

    static void Wheel(Lot l)
    {
        double r = Math.Min(l.W, l.D) * 0.4, hub = r + 3;
        var dr = l.Begin(hub + r + 3);
        dr.Ground(Stone);
        double cx = l.W / 2, cy = l.D / 2;
        // Two A-frame legs either side of the wheel's plane (it faces south), the rim, spokes and gondolas.
        foreach (double y in new[] { cy - 2, cy + 2 })
        {
            Line(dr, cx - r * 0.5, y + (y < cy ? -2 : 2), 0, cx, y, hub, 0.4, Trim);
            Line(dr, cx + r * 0.5, y + (y < cy ? -2 : 2), 0, cx, y, hub, 0.4, Trim);
        }
        Line(dr, cx, cy - 2, hub, cx, cy + 2, hub, 0.7, Chrome);
        int spokes = 16;
        for (int i = 0; i < spokes; i++)
        {
            double a0 = i * 2 * Math.PI / spokes, a1 = (i + 1) * 2 * Math.PI / spokes;
            foreach (double y in new[] { cy - 1, cy + 1 })
            {
                Line(dr, cx + Math.Cos(a0) * r, y, hub + Math.Sin(a0) * r, cx + Math.Cos(a1) * r, y, hub + Math.Sin(a1) * r, 0.25, Trim);
                Line(dr, cx, y, hub, cx + Math.Cos(a0) * r, y, hub + Math.Sin(a0) * r, 0.12, Trim);
            }
            double gx = cx + Math.Cos(a0) * r, gz = hub + Math.Sin(a0) * r;
            dr.Box(gx - 1, cy - 1.2, gz - 2.6, gx + 1, cy + 1.2, gz - 0.6, new[] { Red, Blue, Yellow, Draw.Ramp(5, 6) }[i & 3]);
            dr.Box(gx - 0.9, cy - 1.25, gz - 2, gx + 0.9, cy + 1.25, gz - 1.1, Glass);
        }
        dr.Box(cx - 6, cy + 3, 0, cx + 6, cy + 6, 3, l.Pick(Red, Draw.Ramp(10, 6)));
        dr.Box(cx - 6.2, cy + 2.8, 3, cx + 6.2, cy + 6.2, 3.3, Trim);
    }

    static void MayorsHouse(Lot l)
    {
        var dr = l.Begin(14);
        dr.Ground(Grass);
        double W = l.W, D = l.D;
        var frames = new HashSet<(int, int)>();
        int mx = (int)(W / 4);
        for (int x = 1; x < mx - 1; x++) for (int y = 2; y < 5; y++) frames.Add((x, y));
        frames.Add((1, 5)); frames.Add((mx - 2, 5));
        Shell(dr, 0, 0, frames, 2, Material.Stone, (mx / 2, 4), null, 5);
        l.Roof = new RoofSpec(RoofStyle.Hip, 34, 50, Slate);
        // A portico of columns at the door.
        double dc = mx / 2 * 4 + 2, fy = 5 * 4;
        for (int i = -1; i <= 2; i++) dr.Cyl(dc - 2 + i * 1.6, fy + 2.5, 0.3, 0, 5.6, Trim);
        dr.Box(dc - 4, fy - 0.2, 5.6, dc + 4, fy + 3.2, 6.1, Trim);
        // Gardens: hedged lawns, a fountain and a drive.
        Path(dr, dc - 1.5, fy, dc + 1.5, D, Sand);
        dr.Cyl(dc, D - 5, 2.5, 0, 0.7, Stone);
        dr.Cyl(dc, D - 5, 2.2, 0.3, 0.6, Water);
        dr.Cyl(dc, D - 5, 0.3, 0.6, 2.4, Stone);
        for (double x = 1; x < W - 1; x += 3) if (Math.Abs(x + 1.5 - dc) > 3) dr.Prop("hedge", x + 1.5, D - 1);
        Trees(l, dr, 6, 2, 1.5, W - 2, 7, 0.4);
    }

    static void CityHall(Lot l)
    {
        var dr = l.Begin(36);
        dr.Ground(Stone);
        double W = l.W, D = l.D, x0 = 5, x1 = W - 5, y0 = 6, y1 = D - 12;
        double top = dr.Block(x0, y0, x1, y1, 1.5, 3, 4.5, Stone, StoneDark, G.Punched, 3);
        dr.Box(x0 - 1, y0 - 1, 0, x1 + 1, y1 + 4, 1.5, StoneDark);
        // Steps and a colonnade with a pediment at the front.
        for (int s = 0; s < 5; s++) dr.Box(W / 2 - 10, y1 + 4 + s * 0.6, 0, W / 2 + 10, y1 + 4.6 + s * 0.6, 1.5 - s * 0.3, Stone);
        for (double x = W / 2 - 9; x <= W / 2 + 9; x += 2.25) dr.Cyl(x, y1 + 2.5, 0.55, 1.5, top - 1, Trim);
        dr.Box(W / 2 - 10, y1, top - 1, W / 2 + 10, y1 + 3.5, top, Trim);
        for (int i = 0; i < 6; i++) dr.Box(W / 2 - 10 + i * 1.65, y1, top + i * 0.6, W / 2 + 10 - i * 1.65, y1 + 3.5, top + (i + 1) * 0.6, Trim);
        dr.FlatRoof(x0, y0, x1, y1, top, StoneDark, plant: false);
        // The dome on its drum.
        double cx = W / 2, cy = (y0 + y1) / 2;
        dr.Cyl(cx, cy, 8, top, top + 4, Stone);
        for (int i = 0; i < 16; i++) { double a = i * Math.PI / 8; dr.Box(cx + Math.Cos(a) * 8 - 0.4, cy + Math.Sin(a) * 8 - 0.4, top + 0.8, cx + Math.Cos(a) * 8 + 0.4, cy + Math.Sin(a) * 8 + 0.4, top + 3.2, Glass); }
        dr.Blob(cx, cy, top + 4, 7.6, 7.6, 7.6, Draw.Ramp(5, 7));
        dr.Cyl(cx, cy, 1.2, top + 11, top + 13, Trim);
        dr.Cyl(cx, cy, 0.2, top + 13, top + 15.5, Yellow);
        for (double x = 4; x < W - 3; x += 7) if (Math.Abs(x - W / 2) > 11) dr.Tree(x, D - 3, false, 0.8);
        dr.Box(W / 2 - 14, D - 4, 0, W / 2 - 13.8, D - 3.8, 12, Chrome);
    }

    static void Casino(Lot l)
    {
        var dr = l.Begin(24);
        dr.Ground(Asphalt);
        double W = l.W, D = l.D, x0 = 3, x1 = W - 3, y0 = 3, y1 = D - 11;
        double top = dr.Block(x0, y0, x1, y1, 0, 3, 4.5, Draw.Ramp(0, 5), Yellow, G.Ribbon, 3, true, G.Curtain, Draw.Ramp(2, 8), Yellow);
        dr.FlatRoof(x0, y0, x1, y1, top, Yellow, plant: false, rim: 1.5);
        // A canopy of lights over the entrance, a giant lug nut on a pole, a lit sign.
        dr.Box(W / 2 - 10, y1, 3.6, W / 2 + 10, y1 + 6, 4.2, Yellow);
        for (double x = W / 2 - 9.5; x < W / 2 + 10; x += 1) dr.Box(x, y1 + 5.9, 3.4, x + 0.4, y1 + 6.1, 3.6, Lamp);
        Sign(dr, x0 + 2, x1 - 2, y1, top - 2.6, 2.4, Draw.Ramp(11, 6), Lamp);
        double nx = W - 8, ny = D - 6;
        dr.Box(nx - 0.4, ny - 0.4, 0, nx + 0.4, ny + 0.4, 12, Chrome);
        for (int i = 0; i < 6; i++)
        {
            double a0 = i * Math.PI / 3, a1 = (i + 1) * Math.PI / 3;
            Line(dr, nx + Math.Cos(a0) * 3.5, ny, 16 + Math.Sin(a0) * 3.5, nx + Math.Cos(a1) * 3.5, ny, 16 + Math.Sin(a1) * 3.5, 0.9, Chrome);
        }
        dr.Box(nx - 1, ny - 0.8, 15, nx + 1, ny + 0.8, 17, Lamp);
        for (double x = 3; x < W - 12; x += 3.2) dr.Prop("car", x + 1.3, D - 3, 1, dr.V, c => c == Red ? dr.CarColour() : c);
    }

    static void PicklePlant(Lot l)
    {
        var dr = l.Begin(26);
        dr.Ground(Concrete);
        double W = l.W, D = l.D;
        Shed(dr, 3, 3, W * 0.65, D * 0.6, 9, l.Pick(Trim, Cream), 2, Draw.Ramp(5, 4));
        dr.FlatRoof(3, 3, W * 0.65, D * 0.6, 9, Draw.Ramp(5, 4), plant: false);
        // The giant pickle on the roof, bumps and all.
        double px = W * 0.34, py = D * 0.32;
        dr.Blob(px, py, 14, 12, 4.5, 4.5, Draw.Ramp(5, 5), 0.15, Draw.Ramp(5, 3));
        for (int i = 0; i < 18; i++) dr.Blob(px + l.Between(-10, 10), py + l.Between(-3.5, 3.5), 14 + l.Between(-3.5, 3.5), 0.6, 0.6, 0.6, Draw.Ramp(5, 7));
        dr.Box(px - 0.5, py - 0.5, 9, px + 0.5, py + 0.5, 10.2, MetalDark);
        // Brine tanks and a jar sign.
        for (int i = 0; i < 4; i++) dr.Cyl(W * 0.78, 5 + i * (D * 0.6 / 4), 3, 0, 12, Chrome);
        dr.Cyl(W * 0.5, D - 6, 2.2, 0, 5, Glass);
        dr.Cyl(W * 0.5, D - 6, 2.4, 5, 5.8, Red);
        dr.Blob(W * 0.5, D - 6, 2.5, 1, 1, 2, Draw.Ramp(5, 5));
        Truck(dr, l, 4, D * 0.6 + 2.5);
    }
}
