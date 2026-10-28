using Godot;
using ToyCity.ClientModel;
using ToyCity.Protocol.V0;

namespace ToyCity.Godot;

/// <summary>
/// The city in 3D, one world unit to a cell: terrain as stepped blocks in chunks rebuilt where cells change, a
/// water plane, and buildings as voxel models, one instanced batch per type and lot size, with a coarser mesh at
/// far zoom levels. It also answers how tall each cell stands, which picking needs.
/// </summary>
public partial class CityView : Node3D
{
    const int Chunk = 32;
    const float Base = -0.5f;  // the bottom of the map's side walls
    readonly CityMirror _city;
    MeshInstance3D[] _chunks = System.Array.Empty<MeshInstance3D>();
    MeshInstance3D? _water;
    MultiMeshInstance3D? _blocks;  // roadside stops, and buildings of a type the catalog doesn't know
    readonly BuildingModels _models = new();
    readonly Dictionary<(string, int, int), MultiMeshInstance3D> _batches = new();
    StandardMaterial3D? _voxelMaterial;
    bool _far;
    MeshInstance3D? _cursor;
    float[] _top = System.Array.Empty<float>();
    StandardMaterial3D? _ground;
    public float MaxTop { get; private set; }

    public CityView(CityMirror city) => _city = city;

    public float Ground(int i) => _city.Heights[i] * ClassicView.HeightStep;
    /// <summary>The highest thing on a cell: its building's roof, else the ground (or the water's surface).</summary>
    public float Top(int x, int y) => _top[_city.Idx(x, y)];

    public override void _Ready()
    {
        _ground = new StandardMaterial3D { VertexColorUseAsAlbedo = true, VertexColorIsSrgb = true, Roughness = 1, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
        _blocks = new MultiMeshInstance3D
        {
            Multimesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = true, Mesh = new BoxMesh { Size = Vector3.One } },
            MaterialOverride = new StandardMaterial3D { VertexColorUseAsAlbedo = true, VertexColorIsSrgb = true, Roughness = 0.9f },
        };
        AddChild(_blocks);
        _voxelMaterial = new StandardMaterial3D { VertexColorUseAsAlbedo = true, VertexColorIsSrgb = true, Roughness = 0.95f };
        _cursor = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(1.04f, 0.04f, 1.04f) },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(1, 1, 1, 0.45f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded },
            Visible = false,
        };
        AddChild(_cursor);
    }

    /// <summary>Redraws what changed: the chunks holding dirty cells, and the buildings when any changed.</summary>
    public void Refresh(bool all, int[] cells, int[] buildings)
    {
        int cw = (_city.Width + Chunk - 1) / Chunk, ch = (_city.Height + Chunk - 1) / Chunk;
        if (all || _chunks.Length != cw * ch)
        {
            foreach (var c in _chunks) c.QueueFree();
            _chunks = new MeshInstance3D[cw * ch];
            for (int k = 0; k < _chunks.Length; k++) { _chunks[k] = new MeshInstance3D { MaterialOverride = _ground }; AddChild(_chunks[k]); }
            for (int k = 0; k < _chunks.Length; k++) BuildChunk(k % cw, k / cw);
            BuildWater();
            BuildBlocks();
            return;
        }
        var touched = new HashSet<int>();
        foreach (int i in cells)
        {
            int x = i % _city.Width, y = i / _city.Width;
            // A cell's walls belong to it and its neighbours, so a change at a chunk's edge redraws the neighbour too.
            for (int d = -1; d <= 1; d++)
            {
                touched.Add(Math.Clamp((y) / Chunk, 0, ch - 1) * cw + Math.Clamp((x + d) / Chunk, 0, cw - 1));
                touched.Add(Math.Clamp((y + d) / Chunk, 0, ch - 1) * cw + Math.Clamp(x / Chunk, 0, cw - 1));
            }
        }
        foreach (int k in touched) BuildChunk(k % cw, k / cw);
        if (cells.Length > 0 || buildings.Length > 0) BuildBlocks();
    }

    /// <summary>One chunk of stepped terrain: a flat top per cell, and a wall wherever a neighbour is lower.</summary>
    void BuildChunk(int cx, int cy)
    {
        var verts = new List<Vector3>();
        var normals = new List<Vector3>();
        var colors = new List<Color>();
        var index = new List<int>();
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 n, Color col)
        {
            int s = verts.Count;
            verts.Add(a); verts.Add(b); verts.Add(c); verts.Add(d);
            for (int k = 0; k < 4; k++) { normals.Add(n); colors.Add(col); }
            index.Add(s); index.Add(s + 1); index.Add(s + 2);
            index.Add(s); index.Add(s + 2); index.Add(s + 3);
        }
        int x1 = Math.Min(_city.Width, (cx + 1) * Chunk), y1 = Math.Min(_city.Height, (cy + 1) * Chunk);
        for (int y = cy * Chunk; y < y1; y++)
            for (int x = cx * Chunk; x < x1; x++)
            {
                int i = _city.Idx(x, y);
                float h = Ground(i);
                var col = Palette.Cell(_city, i);
                Quad(new(x, h, y), new(x + 1, h, y), new(x + 1, h, y + 1), new(x, h, y + 1), Vector3.Up, col);
                var wall = col.Darkened(0.25f);
                // North, east, south, west: a wall down to the neighbour, or to the base at the map's edge.
                float Low(int nx, int ny) => _city.InBounds(nx, ny) ? Ground(_city.Idx(nx, ny)) : Base;
                float n = Low(x, y - 1), e = Low(x + 1, y), s = Low(x, y + 1), w = Low(x - 1, y);
                if (n < h) Quad(new(x, h, y), new(x + 1, h, y), new(x + 1, n, y), new(x, n, y), Vector3.Forward, wall);
                if (s < h) Quad(new(x, h, y + 1), new(x + 1, h, y + 1), new(x + 1, s, y + 1), new(x, s, y + 1), Vector3.Back, wall.Darkened(0.1f));
                if (e < h) Quad(new(x + 1, h, y), new(x + 1, h, y + 1), new(x + 1, e, y + 1), new(x + 1, e, y), Vector3.Right, wall.Darkened(0.05f));
                if (w < h) Quad(new(x, h, y), new(x, h, y + 1), new(x, w, y + 1), new(x, w, y), Vector3.Left, wall.Darkened(0.15f));
            }
        var arrays = new global::Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
        arrays[(int)Mesh.ArrayType.Normal] = normals.ToArray();
        arrays[(int)Mesh.ArrayType.Color] = colors.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = index.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        _chunks[cy * ((_city.Width + Chunk - 1) / Chunk) + cx].Mesh = mesh;
    }

    void BuildWater()
    {
        _water?.QueueFree();
        float level = (_city.WaterLevel - 0.35f) * ClassicView.HeightStep;
        _water = new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(_city.Width, _city.Height) },
            Position = new Vector3(_city.Width / 2f, level, _city.Height / 2f),
            MaterialOverride = new StandardMaterial3D { AlbedoColor = Palette.Water, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, Roughness = 0.2f, Metallic = 0.1f },
        };
        AddChild(_water);
    }

    /// <summary>Far zoom levels draw every building with its quarter-resolution mesh.</summary>
    public void SetFar(bool far)
    {
        if (far == _far) return;
        _far = far;
        foreach (var (key, mmi) in _batches)
            if (_city.TypeById.TryGetValue(key.Item1, out var t)) mmi.Multimesh.Mesh = MeshOf(_models.Get(t, key.Item2, key.Item3));
    }

    Mesh MeshOf(BuildingModels.Model m) => _far ? m.Far : m.Near;

    public int Batches => _batches.Count;
    public int Models => _models.Count;

    /// <summary>All the buildings, batched by model; also refreshes each cell's top for picking.</summary>
    void BuildBlocks()
    {
        int n = _city.N;
        if (_top.Length != n) _top = new float[n];
        float water = (_city.WaterLevel - 0.35f) * ClassicView.HeightStep;
        MaxTop = 0;
        for (int i = 0; i < n; i++) { _top[i] = Mathf.Max(Ground(i), water); MaxTop = Mathf.Max(MaxTop, _top[i]); }

        var groups = new Dictionary<(string, int, int), List<BuildingState>>();
        var stops = new List<BuildingState>();
        foreach (var b in _city.Buildings.Values)
        {
            if (!_city.TypeById.TryGetValue(b.TypeId, out var t) || t.Roadside) { stops.Add(b); continue; }
            var key = (b.TypeId, b.Width, b.Height);
            if (!groups.TryGetValue(key, out var list)) groups[key] = list = new();
            list.Add(b);
        }
        foreach (var key in _batches.Keys.Where(k => !groups.ContainsKey(k)).ToList()) { _batches[key].QueueFree(); _batches.Remove(key); }
        foreach (var (key, list) in groups)
        {
            var model = _models.Get(_city.TypeById[key.Item1], key.Item2, key.Item3);
            if (!_batches.TryGetValue(key, out var mmi))
            {
                mmi = new MultiMeshInstance3D
                {
                    Multimesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = true, Mesh = MeshOf(model) },
                    MaterialOverride = _voxelMaterial,
                };
                AddChild(mmi);
                _batches[key] = mmi;
            }
            var mm = mmi.Multimesh;
            mm.InstanceCount = list.Count;
            for (int k = 0; k < list.Count; k++)
            {
                var b = list[k];
                float g = 0;
                for (int y = b.Y; y < b.Y + b.Height; y++)
                    for (int x = b.X; x < b.X + b.Width; x++)
                        if (_city.InBounds(x, y)) g = Mathf.Max(g, Ground(_city.Idx(x, y)));
                // Until part 4's layer-by-layer construction: a building going up stands low and pale.
                float rise = b.ConstructionDaysLeft > 0 ? 0.4f : 1f;
                var color = Colors.White;
                if (b.ConstructionDaysLeft > 0) color = new Color(1.25f, 1.25f, 1.25f);
                if (b.Abandoned) color = new Color(0.5f, 0.48f, 0.45f);
                mm.SetInstanceTransform(k, new Transform3D(Basis.Identity.Scaled(new Vector3(1, rise, 1)), new Vector3(b.X, g, b.Y)));
                mm.SetInstanceColor(k, color);
                float top = g + model.Height * rise;
                for (int y = b.Y; y < b.Y + b.Height; y++)
                    for (int x = b.X; x < b.X + b.Width; x++)
                        if (_city.InBounds(x, y)) { _top[_city.Idx(x, y)] = top; MaxTop = Mathf.Max(MaxTop, top); }
            }
        }

        // Stops: a small post on the kerb side of their road cell.
        var blocks = _blocks!.Multimesh;
        blocks.InstanceCount = stops.Count;
        for (int k = 0; k < stops.Count; k++)
        {
            var b = stops[k];
            if (!_city.InBounds(b.X, b.Y)) { blocks.SetInstanceTransform(k, new Transform3D(Basis.Identity.Scaled(Vector3.Zero), Vector3.Zero)); continue; }
            float g = Ground(_city.Idx(b.X, b.Y));
            var off = b.Side switch { 0 => new Vector3(0, 0, -0.38f), 1 => new Vector3(0.38f, 0, 0), 2 => new Vector3(0, 0, 0.38f), _ => new Vector3(-0.38f, 0, 0) };
            blocks.SetInstanceTransform(k, new Transform3D(Basis.Identity.Scaled(new Vector3(0.18f, 0.18f, 0.18f)), new Vector3(b.X + 0.5f, g + 0.09f, b.Y + 0.5f) + off));
            blocks.SetInstanceColor(k, Colors.Gold);
        }
    }

    /// <summary>Shows the cursor on a cell, or hides it.</summary>
    public void Hover((int X, int Y)? cell)
    {
        if (cell is not { } c) { _cursor!.Visible = false; return; }
        _cursor!.Visible = true;
        _cursor.Position = new Vector3(c.X + 0.5f, Top(c.X, c.Y) + 0.02f, c.Y + 0.5f);
    }
}
