namespace ToyCity.ClientModel.Voxels.Assets;

/// <summary>A building material for the snap pieces: its main colour and its trim, as palette entries.</summary>
public enum Material { Wood = 0, Brick = 1, Concrete = 2, Stone = 3, Glass = 4, Metal = 5 }

/// <summary>
/// The asset editor's palette (RC part 1): the first 31 entries are named colours the pieces and props are built of
/// (each material's main colour and its trim, roof tiles, leaves, paint), and the rest a MagicaVoxel-style ramp of
/// hues by lightness for painting by hand. Every asset made in the editor starts from it, so pieces placed in one
/// model match those in another.
/// </summary>
public static class AssetPalette
{
    public const byte Wood = 1, WoodDark = 2, Brick = 3, Mortar = 4, Concrete = 5, ConcreteDark = 6, Stone = 7, StoneDark = 8,
        Glass = 9, Frame = 10, Metal = 11, MetalDark = 12, RoofTile = 13, Slate = 14, Trim = 15, Grass = 16, Leaf = 17,
        LeafDark = 18, Bark = 19, Asphalt = 20, Red = 21, Yellow = 22, Blue = 23, Black = 24, Lamp = 25, Water = 26,
        Soil = 27, Sand = 28, Tyre = 29, Chrome = 30, Cream = 31;

    /// <summary>The named colours, by entry: name and RGB.</summary>
    public static readonly (byte Index, string Name, uint Rgb)[] Named =
    {
        (Wood, "Wood", 0x9A6B3F), (WoodDark, "Dark wood", 0x6B4528), (Brick, "Brick", 0xA2472F), (Mortar, "Mortar", 0xC9BFAE),
        (Concrete, "Concrete", 0xB8B4AA), (ConcreteDark, "Dark concrete", 0x85827A), (Stone, "Stone", 0x9C978C), (StoneDark, "Dark stone", 0x6E6A62),
        (Glass, "Glass", 0x8EC3D6), (Frame, "Window frame", 0x2F3438), (Metal, "Metal", 0x7A8085), (MetalDark, "Dark metal", 0x4A4F54),
        (RoofTile, "Roof tile", 0x8A3E2B), (Slate, "Slate", 0x4B515A), (Trim, "White trim", 0xEDEAE2), (Grass, "Grass", 0x5E8C3A),
        (Leaf, "Leaves", 0x3F7A34), (LeafDark, "Dark leaves", 0x2C5A27), (Bark, "Bark", 0x5A4030), (Asphalt, "Asphalt", 0x3A3B3D),
        (Red, "Red paint", 0xC23B32), (Yellow, "Yellow paint", 0xE8B931), (Blue, "Blue paint", 0x3366B8), (Black, "Black", 0x1C1D1F),
        (Lamp, "Lamp light", 0xFFE7A8), (Water, "Water", 0x3C7FA8), (Soil, "Soil", 0x6B5338), (Sand, "Sand", 0xD9C79A),
        (Tyre, "Tyre", 0x222222), (Chrome, "Chrome", 0xC8CDD2), (Cream, "Cream render", 0xE6D9B8),
    };

    /// <summary>Each material's main colour and its trim.</summary>
    public static (byte Main, byte Trim) Of(Material m) => m switch
    {
        Material.Wood => (Wood, WoodDark),
        Material.Brick => (Brick, Mortar),
        Material.Concrete => (Concrete, ConcreteDark),
        Material.Stone => (Stone, StoneDark),
        Material.Glass => (Glass, Frame),
        _ => (Metal, MetalDark),
    };

    /// <summary>Fills a model's palette: the named colours, then 14 hues × 16 lightnesses and a grey ramp.</summary>
    public static void Apply(VoxModel m)
    {
        for (int i = 0; i < 256; i++) m.Palette[i] = VoxModel.Rgba(255, 0, 255);
        foreach (var (i, _, rgb) in Named) m.Palette[i] = FromRgb(rgb);
        int at = 32;
        for (int h = 0; h < 14 && at < 256; h++)
            for (int l = 0; l < 16 && at < 256; l++)
                m.Palette[at++] = Hsl(h / 14.0, 0.62, 0.12 + 0.76 * l / 15.0);
        for (int g = 0; at < 256; g++, at++)
        {
            byte v = (byte)Math.Round(255.0 * g / Math.Max(1, 255 - 32 - 14 * 16));
            m.Palette[at] = VoxModel.Rgba(v, v, v);
        }
    }

    static uint FromRgb(uint rgb) => VoxModel.Rgba((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    static uint Hsl(double h, double s, double l)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s, x = c * (1 - Math.Abs(h * 6 % 2 - 1)), m = l - c / 2;
        var (r, g, b) = (int)(h * 6) switch { 0 => (c, x, 0.0), 1 => (x, c, 0.0), 2 => (0.0, c, x), 3 => (0.0, x, c), 4 => (x, 0.0, c), _ => (c, 0.0, x) };
        return VoxModel.Rgba((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    /// <summary>The name of a palette entry: a named colour's, or its number on the ramp.</summary>
    public static string NameOf(byte i) => Named.FirstOrDefault(n => n.Index == i).Name ?? $"Colour {i}";
}
