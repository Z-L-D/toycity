namespace ToyCity.ClientModel.Voxels.Assets;

/// <summary>
/// A finish (RC part 5): what a polygon surface is made of, drawn as a texture that tiles every so many metres: brick,
/// stone, stucco, siding, shingles, slate, metal, glass, wood and paint. Each is made here as pixels (colour, and a
/// height for its relief), so both clients and the voxels of a wreck agree on it. A finish from a photo or an
/// imported model's texture is one too, its pixels given rather than made.
/// </summary>
public sealed class Finish
{
    public string Id = "", Name = "";
    /// <summary>How many metres one copy of the texture covers, across and up.</summary>
    public double TileW = 1, TileH = 1;
    /// <summary>The pixels, RGBA by row from the top, <see cref="Size"/> wide and <see cref="SizeY"/> high.</summary>
    public byte[] Rgba = Array.Empty<byte>();
    /// <summary>The relief, 0 deep (a mortar joint) to 255 proud, one a pixel; empty for none.</summary>
    public byte[] Height = Array.Empty<byte>();
    public int Size, SizeY;
    public float Roughness = 0.85f, Metallic;
    /// <summary>Whether it's see-through-ish glass: a little reflective, drawn smooth.</summary>
    public bool Glassy;
    /// <summary>A model's own texture, mapped by its own coordinates rather than by the metre.</summary>
    public bool Mapped;
    /// <summary>The palette entry nearest its average colour, for a voxel that can't be sampled.</summary>
    public byte Palette;

    /// <summary>The colour at a point on it, in metres (or the model's own coordinates for a mapped one), as 0xRRGGBB.</summary>
    public uint Sample(double u, double v)
    {
        if (Size == 0) return 0x808080;
        double fu = Mapped ? u : u / TileW, fv = Mapped ? v : v / TileH;
        fu -= Math.Floor(fu); fv -= Math.Floor(fv);
        int x = Math.Clamp((int)(fu * Size), 0, Size - 1);
        // Rows run from the top: up the wall is up the picture.
        int y = Math.Clamp((int)((Mapped ? fv : 1 - fv) * SizeY), 0, SizeY - 1);
        int i = (y * Size + x) * 4;
        return (uint)(Rgba[i] << 16 | Rgba[i + 1] << 8 | Rgba[i + 2]);
    }

    uint? _average;
    public uint Average() => _average ??= Mean();
    uint Mean()
    {
        long r = 0, g = 0, b = 0;
        int n = Size * SizeY;
        for (int i = 0; i < n; i++) { r += Rgba[i * 4]; g += Rgba[i * 4 + 1]; b += Rgba[i * 4 + 2]; }
        return n == 0 ? 0x808080 : (uint)((r / n) << 16 | (g / n) << 8 | (b / n));
    }
}

/// <summary>The finishes there are: the made ones, by id, and any added from photos or imported models.</summary>
public static class Finishes
{
    public const int Pixels = 256;

    static readonly Dictionary<string, Finish> Added = new();
    static readonly Dictionary<string, Finish> Made = new();

    /// <summary>The made finishes, in the order the editor lists them: id, name and group.</summary>
    public static readonly (string Id, string Name, string Group)[] List =
    {
        ("brick-red", "Red brick", "Walls"), ("brick-buff", "Buff brick", "Walls"), ("brick-painted", "Painted brick", "Walls"),
        ("stone-ashlar", "Ashlar stone", "Walls"), ("stone-rubble", "Fieldstone", "Walls"), ("stucco", "Stucco", "Walls"),
        ("render-cream", "Cream render", "Walls"), ("siding-white", "White clapboard", "Walls"), ("siding-blue", "Blue clapboard", "Walls"),
        ("siding-sage", "Sage clapboard", "Walls"), ("board-batten", "Board and batten", "Walls"), ("concrete", "Board-formed concrete", "Walls"),
        ("shingles-grey", "Grey shingles", "Roofs"), ("shingles-brown", "Brown shingles", "Roofs"), ("slate", "Slate", "Roofs"),
        ("tile-clay", "Clay tile", "Roofs"), ("metal-seam", "Standing-seam metal", "Roofs"),
        ("glass", "Glass", "Trim"), ("trim-white", "White paint", "Trim"), ("trim-black", "Black paint", "Trim"),
        ("wood-planks", "Wood planks", "Trim"), ("wood-door", "Panelled door", "Trim"), ("copper", "Weathered copper", "Roofs"),
    };

    /// <summary>A finish by id, made the first time it's asked for, or null for one there isn't.</summary>
    public static Finish? Get(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        lock (Made)
        {
            if (Added.TryGetValue(id, out var a)) return a;
            if (Made.TryGetValue(id, out var f)) return f;
            if (!List.Any(l => l.Id == id)) return null;
            f = Make(id);
            f.Palette = Nearest(f.Average());
            return Made[id] = f;
        }
    }

    /// <summary>Adds a finish from given pixels (a photo, a model's texture). Its id is "photo:…" or "model:…".</summary>
    public static Finish Add(string id, string name, byte[] rgba, int w, int h, double tileW, double tileH, bool mapped = false)
    {
        var f = new Finish { Id = id, Name = name, Rgba = rgba, Size = w, SizeY = h, TileW = tileW, TileH = tileH, Mapped = mapped, Roughness = 0.8f };
        f.Palette = Nearest(f.Average());
        lock (Made) Added[id] = f;
        return f;
    }

    /// <summary>Whether a finish is one given (a photo's, a model's texture) rather than made here: it's saved with the asset that uses it.</summary>
    public static bool IsGiven(string id) => id.StartsWith("photo:") || id.StartsWith("model:");

    /// <summary>A given finish as JSON, its pixels deflated and in base64, to save beside the asset using it.</summary>
    public static System.Text.Json.Nodes.JsonObject? ToJson(string id)
    {
        if (!IsGiven(id) || Get(id) is not { } f) return null;
        using var mem = new MemoryStream();
        using (var z = new System.IO.Compression.DeflateStream(mem, System.IO.Compression.CompressionLevel.Optimal)) z.Write(f.Rgba);
        return new System.Text.Json.Nodes.JsonObject
        {
            ["id"] = f.Id, ["name"] = f.Name, ["w"] = f.Size, ["h"] = f.SizeY, ["tileW"] = f.TileW, ["tileH"] = f.TileH,
            ["mapped"] = f.Mapped, ["rgba"] = Convert.ToBase64String(mem.ToArray()),
        };
    }

    /// <summary>Adds a given finish saved by <see cref="ToJson"/>; null if it isn't one.</summary>
    public static Finish? FromJson(System.Text.Json.Nodes.JsonNode? n)
    {
        try
        {
            string id = (string?)n?["id"] ?? "";
            int w = (int?)n?["w"] ?? 0, h = (int?)n?["h"] ?? 0;
            if (!IsGiven(id) || w <= 0 || h <= 0 || w * h > 4096 * 4096) return null;
            var packed = Convert.FromBase64String((string?)n!["rgba"] ?? "");
            var rgba = new byte[w * h * 4];
            using (var z = new System.IO.Compression.DeflateStream(new MemoryStream(packed), System.IO.Compression.CompressionMode.Decompress))
                z.ReadExactly(rgba);
            return Add(id, (string?)n["name"] ?? id, rgba, w, h, (double?)n["tileW"] ?? 1, (double?)n["tileH"] ?? 1, (bool?)n["mapped"] ?? false);
        }
        catch (Exception e) when (e is FormatException or InvalidDataException or EndOfStreamException or InvalidOperationException) { return null; }
    }

    public static bool Has(string id) { lock (Made) return Added.ContainsKey(id) || List.Any(l => l.Id == id); }

    /// <summary>The palette entry (of the asset palette) nearest a colour.</summary>
    public static byte Nearest(uint rgb)
    {
        var pal = PaletteRgb;
        int best = 1; long bestD = long.MaxValue;
        int r = (int)(rgb >> 16 & 255), g = (int)(rgb >> 8 & 255), b = (int)(rgb & 255);
        for (int i = 1; i < 256; i++)
        {
            uint c = pal[i];
            int dr = (int)(c & 255) - r, dg = (int)(c >> 8 & 255) - g, db = (int)(c >> 16 & 255) - b;
            long d = 3L * dr * dr + 4L * dg * dg + 2L * db * db;
            if (d < bestD) { bestD = d; best = i; }
        }
        return (byte)best;
    }

    /// <summary>
    /// A finish's average colour packed as a vertex colour (r | g &lt;&lt; 8 | b &lt;&lt; 16 | a &lt;&lt; 24), for a textured vertex:
    /// the texture wins where it's drawn, and a decimated far mesh or a plain material falls back to this.
    /// </summary>
    public static uint VertexColour(string id)
    {
        if (Get(id) is not { } f) return 0xFFFFFFFF;
        uint c = f.Average();
        return 0xFF000000u | (c >> 16 & 255) | (c & 0xFF00) | (c & 255) << 16;
    }

    static readonly uint[] PaletteRgb = MakePalette();
    static uint[] MakePalette() { var m = new VoxModel(1, 1, 1); AssetPalette.Apply(m); return m.Palette; }

    // ---- Making them ----------------------------------------------------------------------------------------------

    /// <summary>A repeatable random number for a cell of a pattern.</summary>
    static double Hash(int a, int b, int seed = 0)
    {
        uint h = (uint)(a * 374761393 + b * 668265263 + seed * 2147483647);
        h = (h ^ (h >> 13)) * 1274126177;
        h ^= h >> 16;
        return (h & 0xFFFFFF) / (double)0xFFFFFF;
    }

    /// <summary>Smooth noise that tiles every <paramref name="period"/> cells.</summary>
    static double Noise(double x, double y, int period, int seed)
    {
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        double fx = x - x0, fy = y - y0;
        fx = fx * fx * (3 - 2 * fx); fy = fy * fy * (3 - 2 * fy);
        double H(int a, int b) => Hash(((a % period) + period) % period, ((b % period) + period) % period, seed);
        double top = H(x0, y0) * (1 - fx) + H(x0 + 1, y0) * fx, bottom = H(x0, y0 + 1) * (1 - fx) + H(x0 + 1, y0 + 1) * fx;
        return top * (1 - fy) + bottom * fy;
    }

    /// <summary>Noise in layers, 0 to 1, tiling across the texture.</summary>
    static double Grain(double u, double v, int seed, int octaves = 4, int baseCells = 8)
    {
        double sum = 0, amp = 0.5, total = 0;
        for (int o = 0, cells = baseCells; o < octaves; o++, cells *= 2, amp *= 0.5)
        {
            sum += Noise(u * cells, v * cells, cells, seed + o * 17) * amp;
            total += amp;
        }
        return sum / total;
    }

    static (double R, double G, double B) Rgb(uint c) => ((c >> 16 & 255) / 255.0, (c >> 8 & 255) / 255.0, (c & 255) / 255.0);

    /// <summary>
    /// Fills a finish's pixels from a function of where on the tile (u across, v down, 0 to 1) giving a colour and a
    /// height (0 deep, 1 proud).
    /// </summary>
    static Finish Paint(string id, double tileW, double tileH, Func<double, double, ((double R, double G, double B) C, double H)> at, int h = Pixels)
    {
        var f = new Finish { Id = id, Name = List.First(l => l.Id == id).Name, TileW = tileW, TileH = tileH, Size = Pixels, SizeY = h };
        f.Rgba = new byte[Pixels * h * 4];
        f.Height = new byte[Pixels * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < Pixels; x++)
            {
                var (c, ht) = at((x + 0.5) / Pixels, (y + 0.5) / h);
                int i = y * Pixels + x;
                f.Rgba[i * 4] = (byte)Math.Clamp(c.R * 255, 0, 255);
                f.Rgba[i * 4 + 1] = (byte)Math.Clamp(c.G * 255, 0, 255);
                f.Rgba[i * 4 + 2] = (byte)Math.Clamp(c.B * 255, 0, 255);
                f.Rgba[i * 4 + 3] = 255;
                f.Height[i] = (byte)Math.Clamp(ht * 255, 0, 255);
            }
        return f;
    }

    static (double, double, double) Mix((double R, double G, double B) a, (double R, double G, double B) b, double t) =>
        (a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t);

    static (double, double, double) Shade((double R, double G, double B) c, double k) => (c.R * k, c.G * k, c.B * k);

    /// <summary>
    /// Courses of units with joints between, each course offset by <paramref name="offset"/> of a unit: brick, ashlar,
    /// shingles. Returns the unit's row and column and where in it (0 to 1), or null in a joint.
    /// </summary>
    static (int Row, int Col, double U, double V)? Bond(double u, double v, int rows, int cols, double jointU, double jointV, double offset)
    {
        double rv = v * rows;
        int row = (int)Math.Floor(rv);
        double cu = u * cols + (row % 2) * offset * 1.0;
        int col = (int)Math.Floor(cu);
        double iu = cu - col, iv = rv - row;
        if (iu < jointU || iv < jointV) return null;
        return (row, ((col % cols) + cols) % cols, (iu - jointU) / (1 - jointU), (iv - jointV) / (1 - jointV));
    }

    static Finish Make(string id) => id switch
    {
        "brick-red" => Brick(id, 0xA2472F, 0xC9BFAE, 0.18),
        "brick-buff" => Brick(id, 0xC9A66B, 0xD8D0C0, 0.12),
        "brick-painted" => Brick(id, 0xE8E4DA, 0xD6D1C6, 0.04),
        "stone-ashlar" => Ashlar(id),
        "stone-rubble" => Rubble(id),
        "stucco" => Render(id, 0xD9D2C3, 2.0),
        "render-cream" => Render(id, 0xE6D9B8, 2.0),
        "siding-white" => Siding(id, 0xEEEDE8),
        "siding-blue" => Siding(id, 0x7F93A6),
        "siding-sage" => Siding(id, 0x95A486),
        "board-batten" => BoardBatten(id),
        "concrete" => Concrete(id),
        "shingles-grey" => Shingles(id, 0x55585C, 0.14),
        "shingles-brown" => Shingles(id, 0x5E4A3A, 0.14),
        "slate" => Slate(id),
        "tile-clay" => ClayTile(id),
        "metal-seam" => Seam(id, 0x5F6870),
        "copper" => Seam(id, 0x5E9C86),
        "glass" => Glass(id),
        "trim-white" => Paintwork(id, 0xEDEAE2),
        "trim-black" => Paintwork(id, 0x26292C),
        "wood-planks" => Planks(id),
        "wood-door" => Door(id),
        _ => Paintwork(id, 0x808080),
    };

    /// <summary>Running bond: 215 × 65 mm bricks, 10 mm joints, four bricks across 0.9 m and eight courses up 0.6 m.</summary>
    static Finish Brick(string id, uint brick, uint mortar, double vary)
    {
        var b = Rgb(brick); var m = Rgb(mortar);
        return Paint(id, 0.9, 0.6, (u, v) =>
        {
            var at = Bond(u, v, 8, 4, 0.011, 0.13, 0.5);
            double g = Grain(u, v, 3, 3, 16);
            if (at is not { } k) return (Shade(m, 0.92 + 0.12 * g), 0.15);
            double tone = 1 + (Hash(k.Row, k.Col, 5) - 0.5) * 2 * vary;
            var c = Shade(b, tone * (0.9 + 0.2 * Grain(u, v, 9, 3, 32)));
            // A darker burnt end now and then.
            if (Hash(k.Row, k.Col, 7) > 0.85) c = Mix(c, Shade(b, 0.55), 0.35 * k.U);
            return (c, 0.85 + 0.15 * g);
        });
    }

    static Finish Ashlar(string id)
    {
        var s = Rgb(0xA8A397);
        return Paint(id, 1.2, 0.6, (u, v) =>
        {
            var at = Bond(u, v, 2, 2, 0.01, 0.02, 0.5);
            double g = Grain(u, v, 4, 4, 8);
            if (at is not { } k) return (Shade(s, 0.7), 0.1);
            double tone = 0.88 + 0.22 * Hash(k.Row, k.Col, 11);
            // Each block's faces rubbed a little round its edges.
            double edge = Math.Min(Math.Min(k.U, 1 - k.U) * 6, Math.Min(k.V, 1 - k.V) * 3);
            return (Shade(s, tone * (0.85 + 0.25 * g)), 0.7 + 0.3 * Math.Min(1, edge));
        });
    }

    static Finish Rubble(string id)
    {
        // Fieldstones as the cells of a jittered grid (the nearest of nine points), mortar where two cells meet.
        var mortar = Rgb(0xB8B0A0);
        return Paint(id, 1.2, 1.2, (u, v) =>
        {
            const int n = 6;
            double cu = u * n, cv = v * n;
            int ix = (int)cu, iy = (int)cv;
            double best = 9, second = 9; int bx = 0, by = 0;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int gx = ix + dx, gy = iy + dy;
                    int hx = ((gx % n) + n) % n, hy = ((gy % n) + n) % n;
                    double px = gx + 0.2 + 0.6 * Hash(hx, hy, 21), py = gy + 0.2 + 0.6 * Hash(hx, hy, 22);
                    double d = Math.Sqrt((px - cu) * (px - cu) + (py - cv) * (py - cv) * 1.6);
                    if (d < best) { second = best; best = d; bx = hx; by = hy; }
                    else if (d < second) second = d;
                }
            double g = Grain(u, v, 23, 4, 12);
            if (second - best < 0.07) return (Shade(mortar, 0.9 + 0.15 * g), 0.1);
            double t = Hash(bx, by, 24);
            var c = t < 0.33 ? Rgb(0x8F8676) : t < 0.66 ? Rgb(0xA49C8B) : Rgb(0x7D7A72);
            return (Shade(c, 0.8 + 0.35 * g), 0.5 + 0.5 * Math.Min(1, (second - best) * 3));
        });
    }

    static Finish Render(string id, uint colour, double tile)
    {
        var c = Rgb(colour);
        return Paint(id, tile, tile, (u, v) =>
        {
            double g = Grain(u, v, 31, 5, 8), fine = Grain(u, v, 33, 3, 64);
            return (Shade(c, 0.93 + 0.08 * g + 0.04 * fine), 0.5 + 0.5 * fine);
        });
    }

    /// <summary>Clapboard: boards 150 mm to the weather, each with a shadow along its lap.</summary>
    static Finish Siding(string id, uint colour)
    {
        var c = Rgb(colour);
        return Paint(id, 1.2, 0.6, (u, v) =>
        {
            double rv = v * 4, iv = rv - Math.Floor(rv);
            double g = Grain(u, v, 41, 3, 24);
            // Lighter at the top of each board, shadowed under the lap of the one above.
            double k = iv < 0.08 ? 0.62 + 2 * iv : 1.02 - 0.08 * iv;
            return (Shade(c, k * (0.97 + 0.05 * g)), iv < 0.08 ? 0.1 : 0.3 + 0.7 * iv);
        });
    }

    static Finish BoardBatten(string id)
    {
        var c = Rgb(0x6E5A48);
        return Paint(id, 1.2, 1.2, (u, v) =>
        {
            double cu = u * 4, iu = cu - Math.Floor(cu);
            double g = Grain(u * 0.3, v * 3, 43, 4, 8);
            bool batten = iu < 0.1;
            return (Shade(c, (batten ? 1.08 : 0.95) * (0.88 + 0.22 * g)), batten ? 1 : 0.6);
        });
    }

    static Finish Concrete(string id)
    {
        var c = Rgb(0xB0ACA2);
        return Paint(id, 2.4, 1.2, (u, v) =>
        {
            double g = Grain(u, v, 51, 5, 8);
            // Boards 150 mm wide, and form ties every 0.6 m.
            double rv = v * 8, iv = rv - Math.Floor(rv);
            double board = 0.96 + 0.06 * Hash((int)Math.Floor(rv), (int)Math.Floor(u * 2), 53);
            double tu = u * 4 - Math.Floor(u * 4) - 0.5, tv = v * 2 - Math.Floor(v * 2) - 0.5;
            bool tie = tu * tu * 16 + tv * tv * 4 < 0.004;
            var col = Shade(c, board * (0.88 + 0.18 * g) * (iv < 0.04 ? 0.92 : 1));
            return (tie ? Shade(c, 0.55) : col, tie ? 0 : 0.5 + 0.4 * g);
        });
    }

    /// <summary>Three-tab asphalt shingles: courses 140 mm to the weather, tabs a third of a metre, granules speckled.</summary>
    static Finish Shingles(string id, uint colour, double course)
    {
        var c = Rgb(colour);
        int rows = (int)Math.Round(0.56 / course);
        return Paint(id, 1.0, 0.56, (u, v) =>
        {
            var at = Bond(u, v, rows, 3, 0.012, 0.0, 0.5);
            double speck = Hash((int)(u * 512), (int)(v * 512), 61);
            double rv = v * rows, iv = rv - Math.Floor(rv);
            // The bottom edge of the course above shadows the top of this one.
            double k = iv < 0.12 ? 0.6 : 1;
            if (at is not { } a) return (Shade(c, 0.45), 0.1);
            double tone = 0.9 + 0.2 * Hash(a.Row, a.Col, 63);
            return (Shade(c, k * tone * (0.85 + 0.3 * speck)), iv < 0.12 ? 0.2 : 0.4 + 0.6 * iv);
        });
    }

    static Finish Slate(string id)
    {
        return Paint(id, 1.0, 0.6, (u, v) =>
        {
            var at = Bond(u, v, 3, 4, 0.01, 0.0, 0.5);
            double rv = v * 3, iv = rv - Math.Floor(rv);
            double g = Grain(u, v, 71, 3, 32);
            if (at is not { } a) return ((0.18, 0.2, 0.23), 0.1);
            double t = Hash(a.Row, a.Col, 73);
            var c = Mix(Rgb(0x4B515A), Rgb(0x5A5560), t);
            return (Shade(c, (iv < 0.1 ? 0.6 : 1) * (0.88 + 0.2 * g)), iv < 0.1 ? 0.15 : 0.5 + 0.5 * iv);
        });
    }

    static Finish ClayTile(string id)
    {
        var c = Rgb(0xA8492E);
        return Paint(id, 0.8, 0.6, (u, v) =>
        {
            double cu = u * 4, iu = cu - Math.Floor(cu);
            double rv = v * 2, iv = rv - Math.Floor(rv);
            // Each tile a half pipe: light on its crown, dark in the channel.
            double round = Math.Sin(iu * Math.PI);
            double tone = 0.92 + 0.16 * Hash((int)Math.Floor(cu), (int)Math.Floor(rv), 81);
            double g = Grain(u, v, 83, 3, 16);
            return (Shade(c, (0.55 + 0.55 * round) * tone * (iv < 0.1 ? 0.7 : 1) * (0.9 + 0.15 * g)), round * (iv < 0.1 ? 0.5 : 1));
        });
    }

    static Finish Seam(string id, uint colour)
    {
        var c = Rgb(colour);
        var f = Paint(id, 0.9, 1.0, (u, v) =>
        {
            double cu = u * 2, iu = cu - Math.Floor(cu);
            double g = Grain(u, v, 91, 3, 8);
            bool seam = iu < 0.04;
            return (Shade(c, (seam ? 1.2 : iu < 0.1 ? 0.85 : 1) * (0.95 + 0.08 * g)), seam ? 1 : 0.4);
        });
        f.Roughness = 0.45f; f.Metallic = 0.6f;
        return f;
    }

    static Finish Glass(string id)
    {
        var f = Paint(id, 3.0, 3.0, (u, v) =>
        {
            // The sky reflected: lighter up the pane, with a soft diagonal sheen.
            double sheen = Math.Max(0, 1 - Math.Abs((u + v) % 1 - 0.4) * 5) * 0.25;
            var c = Mix(Rgb(0x2E4352), Rgb(0x8EB4C8), 1 - v);
            return (Mix(c, (1, 1, 1), sheen), 0.5);
        });
        f.Roughness = 0.08f; f.Metallic = 0.3f; f.Glassy = true;
        return f;
    }

    static Finish Paintwork(string id, uint colour)
    {
        var c = Rgb(colour);
        var f = Paint(id, 1.0, 1.0, (u, v) => (Shade(c, 0.97 + 0.04 * Grain(u, v, 101, 3, 16)), 0.5), 64);
        f.Roughness = 0.55f;
        return f;
    }

    static Finish Planks(string id)
    {
        var c = Rgb(0x8A5E3B);
        return Paint(id, 0.6, 2.0, (u, v) =>
        {
            double cu = u * 4, iu = cu - Math.Floor(cu);
            int plank = (int)Math.Floor(cu);
            double grain = Grain(u * 0.5 + plank * 0.37, v * 6, 111 + plank, 4, 4);
            double tone = 0.85 + 0.25 * Hash(plank, 0, 113);
            return (Shade(c, tone * (0.8 + 0.35 * grain) * (iu < 0.03 ? 0.5 : 1)), iu < 0.03 ? 0 : 0.6);
        });
    }

    /// <summary>A six-panel door, the whole door one tile (0.9 × 2.1 m), painted deep green.</summary>
    static Finish Door(string id)
    {
        var c = Rgb(0x2F5A46);
        return Paint(id, 0.9, 2.1, (u, v) =>
        {
            double g = Grain(u, v, 121, 3, 16);
            // Panels: two columns, three rows, the middle row short.
            double[] rows = { 0.08, 0.38, 0.46, 0.62, 0.70, 0.92 };
            bool inPanel = false; double depth = 1;
            foreach (var (a, b) in new[] { (0.12, 0.46), (0.54, 0.88) })
                for (int r = 0; r < rows.Length; r += 2)
                    if (u > a && u < b && v > rows[r] && v < rows[r + 1])
                    {
                        inPanel = true;
                        double e = Math.Min(Math.Min(u - a, b - u), Math.Min(v - rows[r], rows[r + 1] - v) * 0.5);
                        depth = e < 0.02 ? 0.3 : 0.6;
                    }
            // The knob, brass.
            double ku = u - 0.86, kv = v - 0.52;
            if (ku * ku + kv * kv * 0.18 < 0.0006) return ((0.78, 0.62, 0.3), 1);
            return (Shade(c, (inPanel ? (depth < 0.5 ? 0.75 : 0.95) : 1) * (0.95 + 0.08 * g)), depth);
        }, 512);
    }
}
