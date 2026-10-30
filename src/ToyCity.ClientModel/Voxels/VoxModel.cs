namespace ToyCity.ClientModel.Voxels;

/// <summary>
/// A voxel model as MagicaVoxel holds one: up to 256 a side, each voxel a palette index (0 empty, 1..255 a
/// colour). Axes as in MagicaVoxel: x east, y south, z up. At 0.5 m a voxel, one 16 m cell is 32 voxels.
/// </summary>
public sealed class VoxModel
{
    public const int PerCell = 32;
    public readonly int SizeX, SizeY, SizeZ;
    public readonly byte[] Voxels;
    /// <summary>RGBA colours packed 0xAABBGGRR as in the file; entry i is palette index i (entry 0 unused).</summary>
    public readonly uint[] Palette = new uint[256];
    /// <summary>
    /// Which palette entries are smooth: their voxels are rounded off and their surfaces curved when smooth shapes
    /// are drawn, while the rest keep sharp edges. Set in the file's materials or a .shape file beside it.
    /// </summary>
    public readonly bool[] Smooth = new bool[256];

    public bool AnySmooth => Smooth.Any(b => b);

    public VoxModel(int sx, int sy, int sz)
    {
        if (sx is < 1 or > 256 || sy is < 1 or > 256 || sz is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(sx), $"{sx}x{sy}x{sz}: a .vox model is 1..256 a side");
        (SizeX, SizeY, SizeZ) = (sx, sy, sz);
        Voxels = new byte[sx * sy * sz];
    }

    public bool In(int x, int y, int z) => x >= 0 && y >= 0 && z >= 0 && x < SizeX && y < SizeY && z < SizeZ;
    public byte this[int x, int y, int z]
    {
        get => In(x, y, z) ? Voxels[x + SizeX * (y + SizeY * z)] : (byte)0;
        set => Voxels[x + SizeX * (y + SizeY * z)] = value;
    }

    public int Count => Voxels.Count(v => v != 0);

    /// <summary>Fills a box, inclusive of both corners, clipped to the model.</summary>
    public void Fill(int x0, int y0, int z0, int x1, int y1, int z1, byte c)
    {
        for (int z = Math.Max(0, z0); z <= Math.Min(SizeZ - 1, z1); z++)
            for (int y = Math.Max(0, y0); y <= Math.Min(SizeY - 1, y1); y++)
                for (int x = Math.Max(0, x0); x <= Math.Min(SizeX - 1, x1); x++)
                    this[x, y, z] = c;
    }

    /// <summary>A copy with every voxel hidden on all six sides removed: it looks and meshes the same, and saves far smaller.</summary>
    public VoxModel Hollow()
    {
        var m = new VoxModel(SizeX, SizeY, SizeZ);
        Array.Copy(Palette, m.Palette, 256);
        Array.Copy(Smooth, m.Smooth, 256);
        for (int z = 0; z < SizeZ; z++)
            for (int y = 0; y < SizeY; y++)
                for (int x = 0; x < SizeX; x++)
                {
                    byte c = this[x, y, z];
                    if (c == 0) continue;
                    bool hidden = this[x + 1, y, z] != 0 && this[x - 1, y, z] != 0 && this[x, y + 1, z] != 0
                        && this[x, y - 1, z] != 0 && this[x, y, z + 1] != 0 && this[x, y, z - 1] != 0;
                    if (!hidden) m[x, y, z] = c;
                }
        return m;
    }

    /// <summary>
    /// The model at 1/f the resolution, for far zoom levels: each f³ block becomes one voxel of its most common
    /// colour if at least a quarter of it is solid, so roofs and thin walls survive.
    /// </summary>
    public VoxModel Downsample(int f)
    {
        var m = new VoxModel((SizeX + f - 1) / f, (SizeY + f - 1) / f, (SizeZ + f - 1) / f);
        Array.Copy(Palette, m.Palette, 256);
        Array.Copy(Smooth, m.Smooth, 256);
        var count = new int[256];
        for (int z = 0; z < m.SizeZ; z++)
            for (int y = 0; y < m.SizeY; y++)
                for (int x = 0; x < m.SizeX; x++)
                {
                    Array.Clear(count);
                    int solid = 0;
                    for (int dz = 0; dz < f; dz++)
                        for (int dy = 0; dy < f; dy++)
                            for (int dx = 0; dx < f; dx++)
                                if (this[x * f + dx, y * f + dy, z * f + dz] is var c and > 0) { count[c]++; solid++; }
                    if (solid * 4 < f * f * f) continue;
                    int best = 1;
                    for (int c = 2; c < 256; c++) if (count[c] > count[best]) best = c;
                    m[x, y, z] = (byte)best;
                }
        return m;
    }

    public static uint Rgba(byte r, byte g, byte b, byte a = 255) => (uint)(r | g << 8 | b << 16 | a << 24);
    public static (byte R, byte G, byte B, byte A) Unpack(uint c) => ((byte)c, (byte)(c >> 8), (byte)(c >> 16), (byte)(c >> 24));
}
