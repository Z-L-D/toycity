using System.Numerics;
using ToyCity.ClientModel.Voxels.Assets;

namespace ToyCity.ClientModel.Voxels;

/// <summary>
/// Voxels from a polygon model (RC part 5): an imported model has no voxels of its own, so for when it's wrecked its
/// surface is turned into them, each in the colour of its texture there (or its vertex colour), the nearest the
/// asset palette has. The surface only: a building is hollow, and the holes knocked in it show that.
/// </summary>
public static class MeshVoxels
{
    /// <summary>
    /// The voxels of a mesh in world units (x east, y up, z south), <paramref name="voxel"/> world units a voxel, in a
    /// model sizeX × sizeY × sizeZ with its corner at the world's origin; anything outside is left off.
    /// </summary>
    public static VoxModel Surface(MeshData mesh, int sizeX, int sizeY, int sizeZ, float voxel)
    {
        var m = new VoxModel(Math.Clamp(sizeX, 1, 256), Math.Clamp(sizeY, 1, 256), Math.Clamp(sizeZ, 1, 256));
        AssetPalette.Apply(m);
        var palette = new PaletteCache();
        Sample(mesh, p => new Vector3(p.X, p.Z, p.Y) / voxel, (x, y, z, rgb) => { if (m.In(x, y, z)) m[x, y, z] = palette.Of(rgb); });
        return m;
    }

    /// <summary>The nearest asset palette colour to a colour, remembered (to 5 bits a channel), never 0.</summary>
    public sealed class PaletteCache
    {
        readonly Dictionary<uint, byte> _known = new();
        public byte Of(uint rgb)
        {
            uint key = rgb & 0xF8F8F8;
            if (!_known.TryGetValue(key, out byte c)) _known[key] = c = Math.Max((byte)1, Finishes.Nearest(key | 0x040404));
            return c;
        }
    }

    /// <summary>
    /// Visits the voxels a mesh's surface passes through, with its colour there (0xRRGGBB, its texture's if it has
    /// one): every triangle sampled at half a voxel. <paramref name="toVoxel"/> takes a vertex to voxel coordinates
    /// (x east, y south, z up). A voxel may be visited more than once.
    /// </summary>
    public static void Sample(MeshData mesh, Func<Vector3, Vector3> toVoxel, Action<int, int, int, uint> visit)
    {
        bool textured = mesh.Textured && mesh.Finish.Count == mesh.Positions.Count && mesh.UVs.Count == mesh.Positions.Count;
        static uint Rgb(uint packed) { var (r, g, b, _) = VoxModel.Unpack(packed); return (uint)(r << 16 | g << 8 | b); }
        for (int t = 0; t + 2 < mesh.Indices.Count; t += 3)
        {
            int ia = mesh.Indices[t], ib = mesh.Indices[t + 1], ic = mesh.Indices[t + 2];
            var a = toVoxel(mesh.Positions[ia]); var b = toVoxel(mesh.Positions[ib]); var c = toVoxel(mesh.Positions[ic]);
            float longest = MathF.Max(Vector3.Distance(a, b), MathF.Max(Vector3.Distance(b, c), Vector3.Distance(c, a)));
            int n = Math.Clamp((int)MathF.Ceiling(longest * 2), 1, 2048);
            var finish = textured && mesh.Finish[ia] > 0 ? Finishes.Get(mesh.FinishIds[mesh.Finish[ia]]) : null;
            uint ca = Rgb(mesh.Colors[ia]), cb = Rgb(mesh.Colors[ib]), cc = Rgb(mesh.Colors[ic]);
            for (int i = 0; i <= n; i++)
                for (int j = 0; j <= n - i; j++)
                {
                    float u = i / (float)n, v = j / (float)n, w = 1 - u - v;
                    var p = a * w + b * u + c * v;
                    uint colour;
                    if (finish != null)
                    {
                        var uv = mesh.UVs[ia] * w + mesh.UVs[ib] * u + mesh.UVs[ic] * v;
                        colour = finish.Sample(uv.X, uv.Y);
                    }
                    else colour = w >= u && w >= v ? ca : u >= v ? cb : cc;
                    visit((int)MathF.Floor(p.X), (int)MathF.Floor(p.Y), (int)MathF.Floor(p.Z), colour);
                }
        }
    }
}
