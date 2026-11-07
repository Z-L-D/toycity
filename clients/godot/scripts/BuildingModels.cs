using Godot;
using ToyCity.ClientModel.Voxels;
using ToyCity.ClientModel.Voxels.Assets;
using ToyCity.Protocol.V0;

namespace ToyCity.Godot;

/// <summary>
/// One model per building type and lot size, meshed four ways: full detail for close zoom levels, a quarter of
/// the resolution for far ones, half for close ones at lower detail settings, and smooth shapes for the free
/// camera up close (<see cref="ShapeMesher"/>: smooth materials curve, sharp ones keep their edges, cut to the
/// graphics options' triangle budget and kept on disk under user://cache/shapes); the last two made the first
/// time they're needed.
///
/// Types without a file are drawn from the realistic asset set (<see cref="RealSet"/>), in a few variants so a street
/// of one type isn't one model copied; the generated placeholder is the last resort.
///
/// Artists drop files in res://vox/ and nothing else changes; models saved from the asset editor's "Use for a
/// building" go in user://vox/ and win over res://vox/. A model with a {name}.asset.json beside it saying its
/// detail (2, 4 or 8 voxels a half metre) is drawn that much smaller, so a fine model keeps its size on the lot, and
/// one whose sidecar has an auto-roof gets that smooth polygon roof on its walls at every zoom level. For a type, the first of these found is used:
/// {type}-{w}x{d}.vox, {type}.vox, {type}-{w}x{d}.glb or .gltf, {type}.glb or .gltf; otherwise the generated
/// placeholder. A .vox model's smooth materials come from the file, or from a {name}.shape file beside it
/// (lines like "smooth = 6 7 40-52"). A glTF model is in metres, y up, set on the lot's corner and shrunk to fit.
/// </summary>
public sealed class BuildingModels
{
    public sealed class Model
    {
        public required Lazy<ArrayMesh> Near;
        public required ArrayMesh Far;
        public required Lazy<ArrayMesh> Mid;
        public required Lazy<ArrayMesh> Smooth;
        /// <summary>
        /// The voxels with holes knocked through and charred, for a building that burns, falls derelict or comes
        /// down: the destruction swap. The same as the others for a glTF model, which has no voxels.
        /// </summary>
        public required Lazy<ArrayMesh> Wrecked;
        /// <summary>How many smooth shapes are drawn as polygons.</summary>
        public int Shapes;
        public required float Height;
        public required bool FromFile;
        /// <summary>The voxels, for remaking the smooth shapes when the options change; null for a glTF model.</summary>
        public VoxModel? Voxels;
        public float Scale;
        /// <summary>The auto-roof's mesh, added to every detail level; empty without one.</summary>
        public MeshData Roof = new();
    }

    readonly Dictionary<(string, int, int, int), Model> _cache = new();
    readonly ShapeCache _shapes = new(ProjectSettings.GlobalizePath("user://cache/shapes"));
    (int Start, int Budget) _shapeOptions = (2, 6_000);

    /// <summary>The finest detail the city draws, in voxels a half metre.</summary>
    public const int MaxDrawnDetail = 4;

    /// <summary>The finest detail the city draws a generated house at.</summary>
    public const int HouseDrawnDetail = 2;

    public int Count => _cache.Count;
    /// <summary>Models made so far and the time spent making them (not their lazy mid and smooth meshes), for the gate runs.</summary>
    public static int Made;
    public static double MakingSeconds;
    public int ShapesMade => _shapes.Builds;
    public int ShapesRead => _shapes.Hits;

    /// <summary>The graphics options' smooth shape detail: returns true when it changed, and the smooth meshes are made again as needed.</summary>
    public bool SetShapes(int start, int budget)
    {
        if (_shapeOptions == (start, budget)) return false;
        _shapeOptions = (start, budget);
        foreach (var m in _cache.Values)
            if (m.Voxels is { } vox && m.Shapes == 0) m.Smooth = SmoothOf(vox, m.Scale, m.Roof);
        return true;
    }

    Lazy<ArrayMesh> SmoothOf(VoxModel vox, float scale, MeshData roof)
    {
        var (start, budget) = _shapeOptions;
        return new Lazy<ArrayMesh>(() => ToMesh(Copy(_shapes.Get(vox, scale, budget, start)).Append(roof)));
    }

    /// <summary>
    /// The model for a type on a lot w × d cells, in one of the realistic set's variants (ignored for a type drawn
    /// from a file, or one the set has no model for).
    /// </summary>
    public Model Get(BuildingTypeInfo t, int w, int d, int variant = 0)
    {
        int v = RealSet.Has(t.Id) && !HasFile(t.Id, w, d) ? Math.Clamp(variant, 0, RealSet.Variants - 1) : 0;
        if (_cache.TryGetValue((t.Id, w, d, v), out var m)) return m;
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        var (vox, detail, roof, shapes) = LoadVox($"{t.Id}-{w}x{d}") ?? LoadVox(t.Id) ?? (null, 1, null, Array.Empty<Shape>());
        if (vox == null && (LoadGltf($"{t.Id}-{w}x{d}") ?? LoadGltf(t.Id)) is { } gltf)
            m = FromGltf(GltfFile.FitToLot(gltf, w, d), w, d, t.Id.Aggregate(17, (h, c) => h * 31 + c));
        else
        {
            bool fromFile = vox != null;
            if (vox == null && UseRealSet && RealSet.Build(t.Id, w, d, v) is { } real) (vox, detail, roof, shapes) = (real.Model, real.Detail, real.Roof, real.Shapes);
            vox ??= Placeholder.Build(t, w, d);
            if (!UseShapes) shapes = Array.Empty<Shape>();
            // A file's model is scaled to fit the lot if it was made for another size, and a fine one drawn smaller.
            float fit = Mathf.Min((float)w * VoxModel.PerCell / vox.SizeX, (float)d * VoxModel.PerCell / vox.SizeY);
            float scale = Mathf.Min(1f / detail, fit) / VoxModel.PerCell;
            // The city draws a model at 12.5 cm voxels at the finest: a 6.25 cm house has five to seven times the
            // faces for a difference too small to see from a city camera. Its full voxels stay in its file.
            // A generated house is drawn at 25 cm: at 12.5 cm it is four to five times the faces (6,000-17,000
            // quads), and a dense city's closest zoom held 22 million triangles. A generated building on a bigger lot
            // (flats, towers, works) is drawn at half-metre voxels, as the old blocks were. Its round things are
            // polygons whatever its voxels.
            int most = fromFile ? MaxDrawnDetail : t.Id.StartsWith("r-l-") ? HouseDrawnDetail : Math.Max(w, d) > 1 ? 1 : MaxDrawnDetail;
            if (detail > most && vox.SizeX > 1)
            {
                int f = detail / most;
                vox = vox.Downsample(f);
                scale *= f;
                detail = most;
            }
            // The voxels the city meshes beside the shapes' polygons: the model without the shapes' own voxels, found
            // at the detail it's drawn at (a 6.25 cm house's would be 64 times the work).
            var boxy = shapes.Count > 0 ? Draw.Without(vox, detail, shapes) : vox;
            // An auto-roof is smooth polygons already, made from the walls at each zoom level's detail: the roof of a
            // fine model alone would outweigh a far model's walls many times over in a city of thousands.
            var roofMesh = roof != null ? AutoRoof.Build(vox, detail, roof, scale) : new MeshData();
            var roofMid = roof != null && detail > 1 ? AutoRoof.Build(vox.Downsample(detail), 1, roof, scale * detail) : roofMesh;
            if (roofMid.Indices.Count == 0) roofMid = roofMesh;
            var roofFar = Decimate.Cluster(roofMid, Math.Max(24, roofMid.Indices.Count / 3 / 4));
            var far = VoxMesher.Mesh(vox.Downsample(4 * detail), 4 * detail * scale).Append(roofFar);
            // The shapes in world units: the model's scale is a voxel's, and a voxel is half a metre over the detail.
            float perMetre = scale * 2 * detail;
            var palette = vox.Palette;
            MeshData ShapesMesh(double quality)
            {
                var all = new MeshData();
                foreach (var sh in shapes) all.Append(sh.Mesh(perMetre, quality, palette));
                return all;
            }
            var model = vox;
            var walls = boxy;
            // Mid is half-metre voxels for a fine model (what the old blocks were close up), a metre for a coarse one.
            int mid = detail > 1 ? detail : 2;
            var near = new Lazy<ArrayMesh>(() => ToMesh(VoxMesher.Mesh(walls, scale).Append(roofMesh).Append(ShapesMesh(1))));
            int seed = t.Id.Aggregate(17, (h, c) => h * 31 + c) ^ (w * 7919 + d * 104729 + v * 15485863);
            m = new Model
            {
                // Near is made the first time a close zoom level draws it: most models in a big city are only seen far off.
                Near = near, Far = ToMesh(far),
                Mid = new Lazy<ArrayMesh>(() => ToMesh(VoxMesher.Mesh(walls.Downsample(mid), mid * scale).Append(roofMid).Append(ShapesMesh(0.5)))),
                // With shapes, the near model is already smooth where it's round; the smooth meshes are for voxels alone.
                Smooth = shapes.Count > 0 ? near : SmoothOf(vox, scale, roofMesh),
                // Wrecked: the whole model's voxels, shapes and all, holed and charred; the roof's gone with the fire.
                Wrecked = new Lazy<ArrayMesh>(() => ToMesh(VoxMesher.Mesh(Wreck.Carve(model, detail, seed), scale))),
                Height = Math.Max(TopOf(vox) * scale, roofMesh.Positions.Count > 0 ? roofMesh.Positions.Max(p => p.Y) : 0), FromFile = fromFile, Voxels = vox, Scale = scale, Roof = roofMesh,
                Shapes = shapes.Count,
            };
        }
        _cache[(t.Id, w, d, v)] = m;
        Made++;
        MakingSeconds += System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds;
        return m;
    }

    /// <summary>Whether an artist's or the asset editor's file draws a type on a lot this size.</summary>
    bool HasFile(string id, int w, int d)
    {
        if (_hasFile.TryGetValue((id, w, d), out bool has)) return has;
        has = false;
        foreach (string name in new[] { $"{id}-{w}x{d}", id })
            foreach (string folder in Folders)
                foreach (string ext in new[] { "vox", "glb", "gltf" })
                    has |= global::Godot.FileAccess.FileExists($"{folder}/{name}.{ext}");
        return _hasFile[(id, w, d)] = has;
    }

    readonly Dictionary<(string, int, int), bool> _hasFile = new();

    /// <summary>Off draws the old placeholder blocks for every type without a file (for comparison shots).</summary>
    public static bool UseRealSet = true;

    /// <summary>Off draws the set's round things as their voxels (for comparison shots).</summary>
    public static bool UseShapes = true;

    /// <summary>
    /// A glTF model: already smooth, so its detail levels are the same mesh cut to fewer triangles; a textured one
    /// keeps its texture close up (cutting it down would smear it). Wrecked, it's its surface in 25 cm voxels, in
    /// its texture's colours, holed and charred like any other (RC part 5).
    /// </summary>
    static Model FromGltf(MeshData mesh, int w, int d, int seed)
    {
        var near = mesh.Textured && mesh.Indices.Count / 3 <= 200_000 ? mesh : Decimate.Cluster(mesh, 40_000);
        var smooth = ToMesh(near);
        float height = mesh.Positions.Count > 0 ? mesh.Positions.Max(p => p.Y) : 0;
        const int perCell = VoxModel.PerCell * 2;
        return new Model
        {
            Near = new Lazy<ArrayMesh>(() => smooth), Far = ToMesh(Decimate.Cluster(mesh, 800)),
            Mid = new Lazy<ArrayMesh>(() => ToMesh(Decimate.Cluster(mesh, 6_000))), Smooth = new Lazy<ArrayMesh>(() => smooth),
            Wrecked = new Lazy<ArrayMesh>(() =>
            {
                var vox = MeshVoxels.Surface(mesh, w * perCell, d * perCell, (int)Math.Ceiling(height * perCell) + 1, 1f / perCell);
                return ToMesh(VoxMesher.Mesh(Wreck.Carve(vox, 2, seed), 1f / perCell));
            }),
            Height = height, FromFile = true,
        };
    }

    /// <summary>Where the game looks for models, first to last: the asset editor's, then the artists'.</summary>
    public static readonly string[] Folders = { "user://vox", "res://vox" };

    static MeshData Copy(MeshData m) => new MeshData().Append(m);

    /// <summary>The height of a model's highest solid voxel's top, in voxels.</summary>
    static int TopOf(VoxModel m)
    {
        int layer = m.SizeX * m.SizeY;
        for (int z = m.SizeZ - 1; z >= 0; z--)
            for (int i = z * layer, end = i + layer; i < end; i++)
                if (m.Voxels[i] != 0) return z + 1;
        return 0;
    }

    static (VoxModel?, int, RoofSpec?, IReadOnlyList<Shape>)? LoadVox(string name)
    {
        foreach (string folder in Folders)
        {
            string path = $"{folder}/{name}.vox";
            if (!global::Godot.FileAccess.FileExists(path)) continue;
            try
            {
                var m = VoxFile.Read(new MemoryStream(global::Godot.FileAccess.GetFileAsBytes(path)));
                string shapes = $"{folder}/{name}.shape";
                if (global::Godot.FileAccess.FileExists(shapes))
                    foreach (var problem in VoxFile.ApplyShapes(m, global::Godot.FileAccess.GetFileAsString(shapes))) GD.PushWarning($"{shapes}: {problem}");
                string sidecar = $"{folder}/{name}.asset.json";
                string text = global::Godot.FileAccess.FileExists(sidecar) ? global::Godot.FileAccess.GetFileAsString(sidecar) : "";
                int detail = AssetDoc.ParseSidecar(text) is { } info ? info.Detail : 1;
                return (m, detail, text.Length > 0 ? AssetDoc.ParseRoof(text) : null, text.Length > 0 ? AssetDoc.ParseShapes(text) : Array.Empty<Shape>());
            }
            catch (Exception e) { GD.PushWarning($"{path}: {e.Message}"); }
        }
        return null;
    }

    /// <summary>Forgets every model, so ones saved from the asset editor are read the next time they're drawn.</summary>
    public void Forget() { _cache.Clear(); _hasFile.Clear(); }

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

    /// <summary>
    /// A mesher result as a Godot mesh. Godot's front faces wind clockwise, so each triangle is turned over. A mesh
    /// with finishes (RC part 5) gets a surface for each, named by the finish's id, after the first of plain colours;
    /// <see cref="FinishTextures.Apply"/> gives them their materials.
    /// </summary>
    public static ArrayMesh ToMesh(MeshData m)
    {
        var mesh = new ArrayMesh();
        if (m.Indices.Count == 0) return mesh;
        m.Pad();
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
        if (!m.Textured)
        {
            var index = new int[m.Indices.Count];
            for (int i = 0; i < index.Length; i += 3) { index[i] = m.Indices[i]; index[i + 1] = m.Indices[i + 2]; index[i + 2] = m.Indices[i + 1]; }
            AddSurface(mesh, verts, normals, colors, null, index, "");
            return mesh;
        }
        var uvs = m.UVs.Select(u => new Vector2(u.X, u.Y)).ToArray();
        // Triangles by finish: plain colours first, then each finish its own surface.
        var by = new List<int>[m.FinishIds.Count];
        for (int k = 0; k < by.Length; k++) by[k] = new List<int>();
        for (int i = 0; i + 2 < m.Indices.Count; i += 3)
        {
            int f = m.Finish[m.Indices[i]];
            by[f].Add(m.Indices[i]); by[f].Add(m.Indices[i + 2]); by[f].Add(m.Indices[i + 1]);
        }
        for (int k = 0; k < by.Length; k++)
        {
            if (by[k].Count == 0) continue;
            // Each surface holds only its own vertices.
            var map = new Dictionary<int, int>();
            var idx = new int[by[k].Count];
            for (int i = 0; i < idx.Length; i++)
            {
                int v = by[k][i];
                if (!map.TryGetValue(v, out int j)) map[v] = j = map.Count;
                idx[i] = j;
            }
            var keep = map.OrderBy(e => e.Value).Select(e => e.Key).ToArray();
            AddSurface(mesh, keep.Select(i => verts[i]).ToArray(), keep.Select(i => normals[i]).ToArray(), keep.Select(i => colors[i]).ToArray(),
                keep.Select(i => uvs[i]).ToArray(), idx, m.FinishIds[k]);
        }
        return mesh;
    }

    static void AddSurface(ArrayMesh mesh, Vector3[] verts, Vector3[] normals, Color[] colors, Vector2[]? uvs, int[] index, string name)
    {
        var arrays = new global::Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.Color] = colors;
        if (uvs != null) arrays[(int)Mesh.ArrayType.TexUV] = uvs;
        arrays[(int)Mesh.ArrayType.Index] = index;
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        mesh.SurfaceSetName(mesh.GetSurfaceCount() - 1, name);
    }
}
