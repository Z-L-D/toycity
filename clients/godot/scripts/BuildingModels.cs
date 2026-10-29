using Godot;
using ToyCity.ClientModel.Voxels;
using ToyCity.Protocol.V0;

namespace ToyCity.Godot;

/// <summary>
/// One model per building type and lot size, meshed four ways: full detail for close zoom levels, a quarter of
/// the resolution for far ones, half for close ones at lower detail settings, and smooth shapes for the free
/// camera up close (<see cref="ShapeMesher"/>: smooth materials curve, sharp ones keep their edges, cut to the
/// graphics options' triangle budget and kept on disk under user://cache/shapes); the last two made the first
/// time they're needed.
///
/// Artists drop files in res://vox/ and nothing else changes. For a type, the first of these found is used:
/// {type}-{w}x{d}.vox, {type}.vox, {type}-{w}x{d}.glb or .gltf, {type}.glb or .gltf; otherwise the generated
/// placeholder. A .vox model's smooth materials come from the file, or from a {name}.shape file beside it
/// (lines like "smooth = 6 7 40-52"). A glTF model is in metres, y up, set on the lot's corner and shrunk to fit.
/// </summary>
public sealed class BuildingModels
{
    public sealed class Model
    {
        public required ArrayMesh Near, Far;
        public required Lazy<ArrayMesh> Mid;
        public required Lazy<ArrayMesh> Smooth;
        public required float Height;
        public required bool FromFile;
        /// <summary>The voxels, for remaking the smooth shapes when the options change; null for a glTF model.</summary>
        public VoxModel? Voxels;
        public float Scale;
    }

    readonly Dictionary<(string, int, int), Model> _cache = new();
    readonly ShapeCache _shapes = new(ProjectSettings.GlobalizePath("user://cache/shapes"));
    (int Start, int Budget) _shapeOptions = (2, 6_000);

    public int Count => _cache.Count;
    public int ShapesMade => _shapes.Builds;
    public int ShapesRead => _shapes.Hits;

    /// <summary>The graphics options' smooth shape detail: returns true when it changed, and the smooth meshes are made again as needed.</summary>
    public bool SetShapes(int start, int budget)
    {
        if (_shapeOptions == (start, budget)) return false;
        _shapeOptions = (start, budget);
        foreach (var m in _cache.Values)
            if (m.Voxels is { } vox) m.Smooth = SmoothOf(vox, m.Scale);
        return true;
    }

    Lazy<ArrayMesh> SmoothOf(VoxModel vox, float scale)
    {
        var (start, budget) = _shapeOptions;
        return new Lazy<ArrayMesh>(() => ToMesh(_shapes.Get(vox, scale, budget, start)));
    }

    public Model Get(BuildingTypeInfo t, int w, int d)
    {
        if (_cache.TryGetValue((t.Id, w, d), out var m)) return m;
        var vox = LoadVox($"{t.Id}-{w}x{d}") ?? LoadVox(t.Id);
        if (vox == null && (LoadGltf($"{t.Id}-{w}x{d}") ?? LoadGltf(t.Id)) is { } gltf)
            m = FromGltf(GltfFile.FitToLot(gltf, w, d));
        else
        {
            bool fromFile = vox != null;
            vox ??= Placeholder.Build(t, w, d);
            // A file's model is scaled to fit the lot if it was made for another size.
            float fit = Mathf.Min((float)w * VoxModel.PerCell / vox.SizeX, (float)d * VoxModel.PerCell / vox.SizeY);
            float scale = Mathf.Min(1, fit) / VoxModel.PerCell;
            var near = VoxMesher.Mesh(vox, scale);
            var far = VoxMesher.Mesh(vox.Downsample(4), 4 * scale);
            var model = vox;
            m = new Model
            {
                Near = ToMesh(near), Far = ToMesh(far),
                Mid = new Lazy<ArrayMesh>(() => ToMesh(VoxMesher.Mesh(model.Downsample(2), 2 * scale))),
                Smooth = SmoothOf(vox, scale),
                Height = near.Positions.Count > 0 ? near.Positions.Max(p => p.Y) : 0, FromFile = fromFile, Voxels = vox, Scale = scale,
            };
        }
        _cache[(t.Id, w, d)] = m;
        return m;
    }

    /// <summary>A glTF model: already smooth, so its detail levels are the same mesh cut to fewer triangles.</summary>
    static Model FromGltf(MeshData mesh)
    {
        var near = Decimate.Cluster(mesh, 40_000);
        var smooth = ToMesh(near);
        return new Model
        {
            Near = smooth, Far = ToMesh(Decimate.Cluster(mesh, 800)),
            Mid = new Lazy<ArrayMesh>(() => ToMesh(Decimate.Cluster(mesh, 6_000))), Smooth = new Lazy<ArrayMesh>(() => smooth),
            Height = mesh.Positions.Count > 0 ? mesh.Positions.Max(p => p.Y) : 0, FromFile = true,
        };
    }

    static VoxModel? LoadVox(string name)
    {
        string path = $"res://vox/{name}.vox";
        if (!global::Godot.FileAccess.FileExists(path)) return null;
        try
        {
            var m = VoxFile.Read(new MemoryStream(global::Godot.FileAccess.GetFileAsBytes(path)));
            string shapes = $"res://vox/{name}.shape";
            if (global::Godot.FileAccess.FileExists(shapes))
                foreach (var problem in VoxFile.ApplyShapes(m, global::Godot.FileAccess.GetFileAsString(shapes))) GD.PushWarning($"{shapes}: {problem}");
            return m;
        }
        catch (Exception e) { GD.PushWarning($"{path}: {e.Message}"); return null; }
    }

    static MeshData? LoadGltf(string name)
    {
        foreach (string ext in new[] { "glb", "gltf" })
        {
            string path = $"res://vox/{name}.{ext}";
            if (!global::Godot.FileAccess.FileExists(path)) continue;
            try
            {
                return GltfFile.Read(global::Godot.FileAccess.GetFileAsBytes(path), uri =>
                {
                    string beside = $"res://vox/{uri}";
                    return !uri.Contains("..") && global::Godot.FileAccess.FileExists(beside) ? global::Godot.FileAccess.GetFileAsBytes(beside) : null;
                });
            }
            catch (Exception e) when (e is InvalidDataException or System.Text.Json.JsonException or FormatException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
            {
                GD.PushWarning($"{path}: {e.Message}");
            }
        }
        return null;
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
