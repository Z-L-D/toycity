using Godot;
using ToyCity.ClientModel.Voxels;
using ToyCity.Protocol.V0;

namespace ToyCity.Godot;

/// <summary>
/// One voxel model per building type and lot size, meshed four ways: full detail for close zoom levels, a
/// quarter of the resolution for far ones, half for close ones at lower detail settings, and smoothed (surface
/// nets, at half the resolution) for the free camera up close; the last two made the first time they're needed. A file at res://vox/{type}-{w}x{d}.vox or res://vox/{type}.vox
/// replaces the generated placeholder; artists drop files there and nothing else changes.
/// </summary>
public sealed class BuildingModels
{
    public sealed record Model(ArrayMesh Near, ArrayMesh Far, Lazy<ArrayMesh> Mid, Lazy<ArrayMesh> Smooth, float Height, bool FromFile);
    readonly Dictionary<(string, int, int), Model> _cache = new();

    public int Count => _cache.Count;

    public Model Get(BuildingTypeInfo t, int w, int d)
    {
        if (_cache.TryGetValue((t.Id, w, d), out var m)) return m;
        var vox = Load($"res://vox/{t.Id}-{w}x{d}.vox") ?? Load($"res://vox/{t.Id}.vox");
        bool fromFile = vox != null;
        vox ??= Placeholder.Build(t, w, d);
        // A file's model is scaled to fit the lot if it was made for another size.
        float fit = Mathf.Min((float)w * VoxModel.PerCell / vox.SizeX, (float)d * VoxModel.PerCell / vox.SizeY);
        float scale = Mathf.Min(1, fit) / VoxModel.PerCell;
        var near = VoxMesher.Mesh(vox, scale);
        var far = VoxMesher.Mesh(vox.Downsample(4), 4 * scale);
        var model = vox;
        m = new Model(ToMesh(near), ToMesh(far), new Lazy<ArrayMesh>(() => ToMesh(VoxMesher.Mesh(model.Downsample(2), 2 * scale))), new Lazy<ArrayMesh>(() => ToMesh(SurfaceNets.Mesh(model.Downsample(2), 2 * scale))),
            near.Positions.Count > 0 ? near.Positions.Max(p => p.Y) : 0, fromFile);
        _cache[(t.Id, w, d)] = m;
        return m;
    }

    static VoxModel? Load(string path)
    {
        if (!global::Godot.FileAccess.FileExists(path)) return null;
        try { return VoxFile.Read(new MemoryStream(global::Godot.FileAccess.GetFileAsBytes(path))); }
        catch (Exception e) { GD.PushWarning($"{path}: {e.Message}"); return null; }
    }

    /// <summary>A mesher result as a Godot mesh. Godot's front faces wind clockwise, so each triangle is turned over.</summary>
    public static ArrayMesh ToMesh(MeshData m)
    {
        var mesh = new ArrayMesh();
        if (m.Indices.Count == 0) return mesh;
        var verts = new Vector3[m.Positions.Count];
        var normals = new Vector3[m.Positions.Count];
        var colors = new Color[m.Positions.Count];
        for (int i = 0; i < verts.Length; i++)
        {
            var p = m.Positions[i]; var n = m.Normals[i];
            verts[i] = new Vector3(p.X, p.Y, p.Z);
            normals[i] = new Vector3(n.X, n.Y, n.Z);
            var (r, g, b, a) = VoxModel.Unpack(m.Colors[i]);
            colors[i] = Color.Color8(r, g, b, a);
        }
        var index = new int[m.Indices.Count];
        for (int i = 0; i < index.Length; i += 3) { index[i] = m.Indices[i]; index[i + 1] = m.Indices[i + 2]; index[i + 2] = m.Indices[i + 1]; }
        var arrays = new global::Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.Color] = colors;
        arrays[(int)Mesh.ArrayType.Index] = index;
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }
}
