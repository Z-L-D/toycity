namespace ToyCity.ClientModel.Graphics.Ground;

/// <summary>
/// The ground's materials as textures (RC part 6), each made here in code as a square that tiles: its colour, its
/// relief (a height, from which come its normals and the shading of its hollows) and its roughness. Made the same on
/// every machine, so both clients and the checks agree on them. A texture's recipe is written against the tile
/// (things are so many metres across), not against pixels, so it's the same ground at 256, 512 or 1024 pixels.
/// </summary>
public static partial class GroundTextures
{
    /// <summary>
    /// The materials, in the order of their layers in the texture arrays: id, name, how many metres a tile covers,
    /// and the colour it averages to (0xRRGGBB), which the maps and the web client paint with.
    /// </summary>
    public static readonly (string Id, string Name, float Metres, uint Average)[] List =
    {
        ("meadow", "Meadow", 8f, 0x566833),
        ("dry-grass", "Dry grass", 8f, 0x837B4F),
        ("earth", "Bare earth", 6f, 0x5F4D37),
        ("tilled", "Tilled soil", 6f, 0x594836),
        ("mud", "Mud", 6f, 0x514433),
        ("sand", "Sand", 6f, 0xB5A57F),
        ("gravel", "River gravel", 4f, 0x807667),
        ("scree", "Scree", 8f, 0x7E776C),
        ("rock", "Bedrock", 12f, 0x79756D),
        ("shale", "Shale", 12f, 0x63666E),
        ("sandstone", "Sandstone", 16f, 0xA08460),
        ("limestone", "Limestone", 16f, 0xB1AB9D),
        ("granite", "Granite", 12f, 0x96847E),
        ("snow", "Snow", 12f, 0xE0E5EE),
        ("leaf-litter", "Leaf litter", 5f, 0x6F5033),
        ("needle-litter", "Needle litter", 4f, 0x614733),
    };

    /// <summary>
    /// Brings a texture's colour to average exactly the listed colour, channel by channel in linear light, so it
    /// looks the same from far off at every size and matches the maps, which paint with the listed colours.
    /// </summary>
    static void Match(GroundTexture t, uint want)
    {
        int n = t.Size * t.Size;
        Span<double> sum = stackalloc double[3];
        for (int i = 0; i < n; i++)
            for (int ch = 0; ch < 3; ch++) sum[ch] += GroundCanvas.Linear(t.Albedo[i * 4 + ch]);
        var lut = new byte[3][];
        for (int ch = 0; ch < 3; ch++)
        {
            double target = GroundCanvas.Linear((byte)(want >> (16 - 8 * ch) & 255)), gain = target / Math.Max(1e-6, sum[ch] / n);
            lut[ch] = new byte[256];
            for (int b = 0; b < 256; b++) lut[ch][b] = (byte)GroundCanvas.Srgb(Math.Min(1, GroundCanvas.Linear((byte)b) * gain));
        }
        double lr = 0, lg = 0, lb = 0;
        for (int i = 0; i < n; i++)
        {
            int o = i * 4;
            t.Albedo[o] = lut[0][t.Albedo[o]]; t.Albedo[o + 1] = lut[1][t.Albedo[o + 1]]; t.Albedo[o + 2] = lut[2][t.Albedo[o + 2]];
            lr += GroundCanvas.Linear(t.Albedo[o]); lg += GroundCanvas.Linear(t.Albedo[o + 1]); lb += GroundCanvas.Linear(t.Albedo[o + 2]);
        }
        t.Average = (uint)(GroundCanvas.Srgb(lr / n) << 16 | GroundCanvas.Srgb(lg / n) << 8 | GroundCanvas.Srgb(lb / n));
    }

    /// <summary>
    /// Bumped whenever a recipe changes, so textures kept on disk from before are made again.
    /// </summary>
    public const int Version = 1;

    /// <summary>Every material's texture at one size, made side by side on the machine's cores, in the list's order.</summary>
    public static GroundTexture[] MakeAll(int size)
    {
        var all = new GroundTexture[List.Length];
        Parallel.For(0, List.Length, i => all[i] = Make(List[i].Id, size));
        return all;
    }

    /// <summary>A material's layer, or -1 for one there isn't.</summary>
    public static int Index(string id)
    {
        for (int i = 0; i < List.Length; i++) if (List[i].Id == id) return i;
        return -1;
    }

    /// <summary>Makes a material's texture, <paramref name="size"/> pixels a side (a power of two, 64 to 2048).</summary>
    public static GroundTexture Make(string id, int size)
    {
        if (size < 64 || size > 2048 || (size & (size - 1)) != 0) throw new ArgumentOutOfRangeException(nameof(size), "a power of two from 64 to 2048");
        int at = Index(id);
        if (at < 0) throw new ArgumentException($"no ground material '{id}'", nameof(id));
        var (_, name, metres, _) = List[at];
        var t = id switch
        {
            "meadow" => Grass(size, metres, dry: false),
            "dry-grass" => Grass(size, metres, dry: true),
            "earth" => Earth(size, metres, tilled: false),
            "tilled" => Earth(size, metres, tilled: true),
            "mud" => Mud(size, metres),
            "sand" => Sand(size, metres),
            "gravel" => Stones(size, metres, scree: false),
            "scree" => Stones(size, metres, scree: true),
            "rock" or "shale" or "sandstone" or "limestone" or "granite" => Rock(size, metres, id),
            "snow" => Snow(size, metres),
            "leaf-litter" => Litter(size, metres, needles: false),
            "needle-litter" => Litter(size, metres, needles: true),
            _ => throw new ArgumentException($"no recipe for '{id}'", nameof(id)),
        };
        t.Id = id; t.Name = name; t.Metres = metres;
        Match(t, List[at].Average);
        return t;
    }
}
