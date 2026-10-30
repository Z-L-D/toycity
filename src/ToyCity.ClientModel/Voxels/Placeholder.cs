using ToyCity.Protocol.V0;

namespace ToyCity.ClientModel.Voxels;

/// <summary>
/// Stand-in voxel models made from a building type's category, density, wealth and colour, for every type until
/// someone draws a real one. A .vox file dropped in for a type replaces its placeholder; both go through the same
/// mesher. Zoned buildings grow on lots of several sizes, so a model is made for a type on a lot w × d cells.
/// </summary>
public static class Placeholder
{
    // Palette slots.
    const byte Wall = 1, Roof = 2, Window = 3, Trim = 4, Grass = 5, Leaves = 6, Trunk = 7, Stack = 8, Glass = 9, Paving = 10;

    public static VoxModel Build(BuildingTypeInfo t, int w, int d)
    {
        int sx = Math.Clamp(w * VoxModel.PerCell, 1, 256), sy = Math.Clamp(d * VoxModel.PerCell, 1, 256);
        uint seed = Hash(t.Id) ^ (uint)(w * 73 + d * 151);
        var (r, g, b) = Hex(t.Color);
        string kind = t.Category switch
        {
            "zoned" => t.Zone switch { ZoneKind.ZoneResidential => "R", ZoneKind.ZoneCommercial => "C", _ => "I" },
            _ => t.Category,
        };
        int height = kind switch
        {
            "R" or "C" or "I" => t.Density switch
            {
                Density.High => Math.Min(250, 60 + (int)(seed % 50) + 20 * Math.Min(w, d) + (int)t.Wealth * 15),
                Density.Medium => 22 + (int)(seed % 14) + (int)t.Wealth * 4,
                _ => kind == "I" ? 16 + (int)(seed % 6) : 10 + (int)(seed % 4),
            },
            "park" => 26,
            "power" => 40,
            "landmark" => 90,
            "transit" => 14,
            _ => 20 + (int)(seed % 6),
        };
        var m = new VoxModel(sx, sy, Math.Min(256, height + 12));
        m.Palette[Wall] = VoxModel.Rgba(Mix(r, 235, 0.45f), Mix(g, 230, 0.45f), Mix(b, 220, 0.45f));
        m.Palette[Roof] = VoxModel.Rgba(Mix(r, 40, 0.35f), Mix(g, 40, 0.35f), Mix(b, 45, 0.35f));
        m.Palette[Window] = VoxModel.Rgba(70, 95, 125);
        m.Palette[Trim] = VoxModel.Rgba(r, g, b);
        m.Palette[Grass] = VoxModel.Rgba(96, 160, 72);
        m.Palette[Leaves] = VoxModel.Rgba(52, 120, 56);
        m.Palette[Trunk] = VoxModel.Rgba(110, 80, 50);
        m.Palette[Stack] = VoxModel.Rgba(120, 118, 115);
        m.Palette[Glass] = VoxModel.Rgba(Mix(r, 120, 0.7f), Mix(g, 170, 0.7f), Mix(b, 200, 0.7f));
        m.Palette[Paving] = VoxModel.Rgba(170, 168, 160);
        // Leaves and roofs curve when smooth shapes are drawn; walls, windows and paving keep their edges.
        m.Smooth[Leaves] = m.Smooth[Roof] = true;

        const int yard = 3;
        int x0 = yard, y0 = yard, x1 = sx - 1 - yard, y1 = sy - 1 - yard;
        switch (kind)
        {
            case "R" when t.Density == Density.Low:
                m.Fill(0, 0, 0, sx - 1, sy - 1, 0, Grass);
                House(m, x0 + 3, y0 + 3, x1 - 3, y1 - 3, height);
                break;
            case "R" or "C":
                m.Fill(0, 0, 0, sx - 1, sy - 1, 0, kind == "C" ? Paving : Grass);
                Block(m, x0, y0, x1, y1, height, t.Density == Density.High && kind == "C" ? Glass : Wall, kind == "C");
                break;
            case "I":
                m.Fill(0, 0, 0, sx - 1, sy - 1, 0, Paving);
                Factory(m, x0, y0, x1, y1, height, seed);
                break;
            case "park":
                m.Fill(0, 0, 0, sx - 1, sy - 1, 0, Grass);
                for (int k = 0; k < w * d * 3; k++)
                {
                    uint h = seed * 2654435761u + (uint)k * 40503u;
                    int tx = 5 + (int)(h % (uint)Math.Max(1, sx - 10)), ty = 5 + (int)(h / 7 % (uint)Math.Max(1, sy - 10));
                    Tree(m, tx, ty, 10 + (int)(h % 9));
                }
                break;
            case "transit":
                m.Fill(0, 0, 0, sx - 1, sy - 1, 1, Paving);
                m.Fill(x0, y0 + 4, x1, y1 - 4, 2, 10, Wall);
                m.Fill(x0 - 1, y0 + 2, x1 + 1, y1 - 2, 11, 12, Trim);
                break;
            case "power":
                m.Fill(0, 0, 0, sx - 1, sy - 1, 0, Paving);
                Block(m, x0, y0, x1 - sx / 3, y1, height / 2, Wall, false);
                for (int k = 0; k < 2; k++)
                {
                    int cx = x1 - sx / 6, cy = y0 + 6 + k * Math.Max(8, (y1 - y0) / 2);
                    m.Fill(cx - 3, cy - 3, 1, cx + 3, cy + 3, height + 10, Stack);
                }
                break;
            case "landmark":
                m.Fill(0, 0, 0, sx - 1, sy - 1, 0, Paving);
                for (int z = 1, inset = 0; z < height; z += 12, inset += 3)
                    Block(m, x0 + inset, y0 + inset, x1 - inset, y1 - inset, Math.Min(height, z + 12), Wall, false, z);
                m.Fill(sx / 2 - 1, sy / 2 - 1, height, sx / 2 + 1, sy / 2 + 1, height + 10, Trim);
                break;
            default: // civic: a low hall with a band of the type's colour
                m.Fill(0, 0, 0, sx - 1, sy - 1, 0, Paving);
                Block(m, x0, y0, x1, y1, height, Wall, false);
                m.Fill(x0, y0, height - 4, x1, y1, height - 2, Trim);
                break;
        }
        return m;
    }

    /// <summary>A box with rows of windows on every side and a flat roof; shops get an awning band at street level.</summary>
    static void Block(VoxModel m, int x0, int y0, int x1, int y1, int top, byte wall, bool shop, int from = 1)
    {
        m.Fill(x0, y0, from, x1, y1, top - 1, wall);
        for (int z = from + 3; z < top - 2; z += 6)
            for (int k = 0; k < 2; k++)
            {
                for (int x = x0 + 2; x <= x1 - 2; x += 4) { m[x, y0, z + k] = Window; m[x + 1, y0, z + k] = Window; m[x, y1, z + k] = Window; m[x + 1, y1, z + k] = Window; }
                for (int y = y0 + 2; y <= y1 - 2; y += 4) { m[x0, y, z + k] = Window; m[x0, y + 1, z + k] = Window; m[x1, y, z + k] = Window; m[x1, y + 1, z + k] = Window; }
            }
        m.Fill(x0, y0, top, x1, y1, top, Roof);
        if (shop) m.Fill(x0 - 1, y0 - 1, 6, x1 + 1, y1 + 1, 7, Trim);
    }

    /// <summary>A house: walls with windows, a stepped pitched roof and a chimney.</summary>
    static void House(VoxModel m, int x0, int y0, int x1, int y1, int wallTop)
    {
        Block(m, x0, y0, x1, y1, wallTop, Wall, false);
        int half = (y1 - y0) / 2 + 2;
        for (int k = 0; k <= half && y0 - 1 + k <= y1 + 1 - k; k++)
            m.Fill(x0 - 1, y0 - 1 + k, wallTop + k, x1 + 1, y1 + 1 - k, wallTop + k, Roof);
        m.Fill(x1 - 5, y0 + 3, wallTop, x1 - 3, y0 + 5, wallTop + half, Stack);
    }

    /// <summary>A shed with a sawtooth roof and a chimney stack.</summary>
    static void Factory(VoxModel m, int x0, int y0, int x1, int y1, int top, uint seed)
    {
        m.Fill(x0, y0, 1, x1, y1, top - 1, Wall);
        for (int x = x0; x <= x1; x++)
        {
            int rise = (x - x0) % 8;
            m.Fill(x, y0, top, x, y1, top + rise / 2, rise < 6 ? Roof : Glass);
        }
        int cx = x0 + 4 + (int)(seed % 6), cy = y1 - 5;
        m.Fill(cx - 2, cy - 2, top, cx + 2, cy + 2, Math.Min(m.SizeZ - 1, top + 10), Stack);
    }

    /// <summary>The Clockwork Colossus: a brass walker two cells across and three tall, facing +y (south).</summary>
    public static VoxModel Colossus()
    {
        var m = new VoxModel(64, 64, 100);
        const byte Brass = 1, Dark = 2, Eye = 3, Steam = 4;
        m.Palette[Brass] = VoxModel.Rgba(181, 140, 62);
        m.Palette[Dark] = VoxModel.Rgba(92, 70, 40);
        m.Palette[Eye] = VoxModel.Rgba(255, 70, 40);
        m.Palette[Steam] = VoxModel.Rgba(225, 225, 225);
        m.Fill(14, 26, 0, 24, 38, 44, Dark);          // legs
        m.Fill(40, 26, 0, 50, 38, 44, Dark);
        m.Fill(10, 22, 0, 26, 42, 4, Brass);          // feet
        m.Fill(38, 22, 0, 54, 42, 4, Brass);
        m.Fill(8, 18, 44, 56, 46, 80, Brass);         // body
        for (int z = 50; z < 78; z += 8) m.Fill(8, 46, z, 56, 47, z + 2, Dark);  // rivet bands on the front
        m.Fill(0, 26, 52, 7, 36, 76, Dark);           // arms
        m.Fill(57, 26, 52, 63, 36, 76, Dark);
        m.Fill(20, 22, 80, 44, 42, 96, Brass);        // head
        m.Fill(24, 43, 86, 40, 43, 90, Eye);          // the eye, on the face
        m.Fill(28, 28, 96, 34, 34, 99, Steam);        // a puff from the funnel
        return m;
    }

    /// <summary>A tornado: a funnel of grey-brown debris rings, narrow at the ground, four cells tall.</summary>
    public static VoxModel Tornado()
    {
        var m = new VoxModel(96, 96, 128);
        m.Palette[1] = VoxModel.Rgba(150, 145, 135);
        m.Palette[2] = VoxModel.Rgba(110, 100, 85);
        for (int z = 0; z < 128; z++)
        {
            float r = 4 + z * 0.33f;
            for (int a = 0; a < 360; a += 3)
            {
                float t = a * MathF.PI / 180 + z * 0.15f;
                int x = 48 + (int)(MathF.Cos(t) * r), y = 48 + (int)(MathF.Sin(t) * r);
                if (m.In(x, y, z) && (a / 3 + z) % 4 != 0) m[x, y, z] = (byte)(z % 9 < 2 ? 2 : 1);
            }
        }
        return m;
    }

    static void Tree(VoxModel m, int x, int y, int h)
    {
        m.Fill(x, y, 1, x + 1, y + 1, h - 4, Trunk);
        for (int z = h - 6; z <= h + 2; z++)
        {
            int r = z < h - 2 ? 4 : z < h + 1 ? 3 : 2;
            for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                    if (dx * dx + dy * dy <= r * r && m.In(x + dx, y + dy, z) && m[x + dx, y + dy, z] == 0) m[x + dx, y + dy, z] = Leaves;
        }
    }

    static byte Mix(byte a, int b, float t) => (byte)Math.Clamp(a + (b - a) * t, 0, 255);

    static (byte, byte, byte) Hex(string s)
    {
        s = (s ?? "").TrimStart('#');
        if (s.Length != 6) return (150, 150, 150);
        int v = Convert.ToInt32(s, 16);
        return ((byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    static uint Hash(string s)
    {
        uint h = 2166136261;
        foreach (char c in s) h = (h ^ c) * 16777619;
        return h;
    }
}
