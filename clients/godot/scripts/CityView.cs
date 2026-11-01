using Godot;
using ToyCity.ClientModel.Graphics;
using ToyCity.ClientModel;
using ToyCity.Protocol.V0;

namespace ToyCity.Godot;

/// <summary>
/// The city in 3D, one world unit to a cell: the ground as SimCity 4 drew it, a heightfield from the height at every
/// grid corner (see <see cref="TerrainShape"/>), in chunks rebuilt where corners move and drawn coarser far from the
/// camera when the settings say so; a water sheet coloured by its depth; and buildings as voxel models, one instanced batch per type and lot size, with a coarser mesh at
/// far zoom levels. Buildings rise layer by layer as they're built and crumble when they go, and show their
/// wear, graffiti, fire and abandonment (shaders/building.gdshader). It also answers how tall each cell stands,
/// which picking needs.
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
    // One batch per model and 32-cell region of the map, so the camera skips regions off screen.
    readonly Dictionary<(string, int, int, int), MultiMeshInstance3D> _batches = new();
    const int Region = 32;
    readonly Dictionary<(string, int, int, int), List<(BuildingState B, float Left)>> _members = new();  // Left < 0: standing
    readonly BuildingLife _life = new();
    bool _wasAnimating;
    ShaderMaterial? _voxelMaterial;
    ModelDetail _detail = ModelDetail.Near, _rest = ModelDetail.Near;
    (int X, int Y) _focus = (-99, -99);  // the region the smooth detail is centred on
    int _radius;
    MeshInstance3D? _cursor;
    bool _fullTerrain = true;
    int[] _stride = System.Array.Empty<int>();  // each chunk's drawn detail: 1 every corner, 2 every other, 4
    double _lodAt;
    Image? _cellImage;
    ImageTexture? _cellTexture;
    Image? _groundImage;
    ImageTexture? _groundTexture;
    ShaderMaterial? _waterMaterial;
    /// <summary>The water's material, shared with the rivers and floods (RiversView).</summary>
    public ShaderMaterial? WaterMaterial => _waterMaterial;
    float[] _top = System.Array.Empty<float>();
    ShaderMaterial? _ground;
    double _flashUntil = -1;
    public float MaxTop { get; private set; }

    public CityView(CityMirror city) => _city = city;
    /// <summary>The city this draws.</summary>
    public CityMirror City => _city;

    public float Ground(int i) => _city.Heights[i] * ClassicView.HeightStep;
    /// <summary>The ground's surface at a point (in cells), as drawn.</summary>
    public float Surface(float x, float y) => TerrainShape.Surface(_city, x, y) * ClassicView.HeightStep;
    /// <summary>The highest thing on a cell: its building's roof, else the ground (or the water's surface).</summary>
    public float Top(int x, int y) => _top[_city.Idx(x, y)];

    public override void _Ready()
    {
        _ground = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/terrain.gdshader") };
        _blocks = new MultiMeshInstance3D
        {
            Multimesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = true, Mesh = new BoxMesh { Size = Vector3.One } },
            MaterialOverride = new StandardMaterial3D { VertexColorUseAsAlbedo = true, VertexColorIsSrgb = true, Roughness = 0.9f },
        };
        AddChild(_blocks);
        _voxelMaterial = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/building.gdshader") };
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
            _stride = new int[cw * ch];
            System.Array.Fill(_stride, 1);
            for (int k = 0; k < _chunks.Length; k++) { _chunks[k] = new MeshInstance3D { MaterialOverride = _ground }; AddChild(_chunks[k]); }
            for (int k = 0; k < _chunks.Length; k++) BuildChunk(k % cw, k / cw);
            PaintCells(null);
            _life.Reset();
            BuildWater();
            BuildBlocks();
            return;
        }
        var touched = new HashSet<int>();
        foreach (int i in cells)
        {
            int x = i % _city.Width, y = i / _city.Width;
            // A cell's walls and corners belong to it and its neighbours, so a change at a chunk's edge or corner
            // redraws the chunks beside it too.
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                    touched.Add(Math.Clamp((y + dy) / Chunk, 0, ch - 1) * cw + Math.Clamp((x + dx) / Chunk, 0, cw - 1));
        }
        foreach (int k in touched) BuildChunk(k % cw, k / cw);
        if (cells.Length > 0) { PaintCells(cells); PaintGround(); }
        if (cells.Length > 0 || buildings.Length > 0) BuildBlocks();
    }

    /// <summary>
    /// The graphics options' smooth terrain and smooth shape detail. Changing the terrain redraws the ground;
    /// changing the detail remakes the smooth models as they're next drawn.
    /// </summary>
    public void SetShapes(bool fullTerrain, int start, int budget)
    {
        if (fullTerrain != _fullTerrain) { _fullTerrain = fullTerrain; _lodAt = 0; }
        if (_models.SetShapes(start, budget))
            foreach (var (key, mmi) in _batches)
                if (_city.TypeById.TryGetValue(key.Item1, out var t)) mmi.Multimesh.Mesh = MeshOf(_models.Get(t, key.Item2, key.Item3), key.Item4);
    }

    public int ShapesMade => _models.ShapesMade;
    public int ShapesRead => _models.ShapesRead;

    /// <summary>A map layer over the ground, one pixel per cell, or null for none.</summary>
    public void SetOverlay(Image? image)
    {
        _ground!.SetShaderParameter("use_overlay", image != null);
        if (image == null) return;
        _ground.SetShaderParameter("overlay", ImageTexture.CreateFromImage(image));
        _ground.SetShaderParameter("map_size", new Vector2(image.GetWidth(), image.GetHeight()));
    }

    /// <summary>Outlines the cell the panel is showing.</summary>
    public void Select((int X, int Y)? cell) =>
        _ground!.SetShaderParameter("selected", cell is { } c ? new Vector2(c.X, c.Y) : new Vector2(-1, -1));

    /// <summary>Pulses an area for a few seconds: what an alert or petition is about.</summary>
    public void Flash(int x0, int y0, int x1, int y1)
    {
        _ground!.SetShaderParameter("flash_rect", new Vector4(Math.Min(x0, x1), Math.Min(y0, y1), Math.Max(x0, x1), Math.Max(y0, y1)));
        _flashUntil = Time.GetTicksMsec() / 1000.0 + 6;
    }

    public override void _Process(double delta)
    {
        UpdateDetail();
        if (_flashUntil < 0) return;
        double now = Time.GetTicksMsec() / 1000.0, left = _flashUntil - now;
        // A slow pulse, fading out over the last second.
        float pulse = 0.5f + 0.5f * MathF.Sin((float)now * 5.5f);
        _ground!.SetShaderParameter("flash_strength", left <= 0 ? 0f : (0.15f + 0.35f * pulse) * (float)Math.Min(1, left));
        if (left <= 0) _flashUntil = -1;
    }

    /// <summary>
    /// A few times a second: each chunk's detail from how far it is from the camera. Near ground always has every
    /// corner; with distant terrain set coarse, ground beyond 60 cells takes every other and beyond 140 every fourth.
    /// </summary>
    void UpdateDetail()
    {
        double now = Time.GetTicksMsec() / 1000.0;
        if (_chunks.Length == 0 || now < _lodAt) return;
        _lodAt = now + 0.3;
        var cam = GetViewport()?.GetCamera3D();
        if (cam == null) return;
        int cw = (_city.Width + Chunk - 1) / Chunk;
        for (int k = 0; k < _chunks.Length; k++)
        {
            int cx = k % cw, cy = k / cw;
            var centre = new Vector3(cx * Chunk + Chunk / 2f, Ground(_city.Idx(Math.Min(_city.Width - 1, cx * Chunk + Chunk / 2), Math.Min(_city.Height - 1, cy * Chunk + Chunk / 2))), cy * Chunk + Chunk / 2f);
            float d = cam.GlobalPosition.DistanceTo(centre) - Chunk * 0.7f;
            int want = _fullTerrain || d < 60 ? 1 : d < 140 ? 2 : 4;
            if (want != _stride[k]) { _stride[k] = want; BuildChunk(cx, cy); }
        }
    }

    /// <summary>
    /// One chunk of ground: a grid of the corners' heights (every one, or every second or fourth far away), two
    /// triangles to a square split north-west to south-east as <see cref="TerrainShape.Surface"/> reads them, normals
    /// from the slope around each corner so light falls smoothly across the cells. A skirt hangs under its edges to
    /// close any gap beside a chunk drawn at another detail, and at the map's edge a wall drops to the base.
    /// </summary>
    void BuildChunk(int cx, int cy)
    {
        int cw = (_city.Width + Chunk - 1) / Chunk;
        int k = cy * cw + cx, stride = _stride.Length > k ? _stride[k] : 1;
        int x0 = cx * Chunk, y0 = cy * Chunk, x1 = Math.Min(_city.Width, x0 + Chunk), y1 = Math.Min(_city.Height, y0 + Chunk);
        const float step = ClassicView.HeightStep;
        float H(int x, int y) => _city.CornerSteps(x, y) * step;
        var xs = new List<int>();
        for (int x = x0; x < x1; x += stride) xs.Add(x);
        xs.Add(x1);
        var ys = new List<int>();
        for (int y = y0; y < y1; y += stride) ys.Add(y);
        ys.Add(y1);
        var verts = new List<Vector3>();
        var normals = new List<Vector3>();
        var colors = new List<Color>();
        var index = new List<int>();
        int nx = xs.Count;
        foreach (int y in ys)
            foreach (int x in xs)
            {
                verts.Add(new Vector3(x, H(x, y), y));
                float gx = (H(x + 1, y) - H(x - 1, y)) / 2, gz = (H(x, y + 1) - H(x, y - 1)) / 2;
                normals.Add(new Vector3(-gx, 1, -gz).Normalized());
                colors.Add(Colors.White);
            }
        for (int j = 0; j + 1 < ys.Count; j++)
            for (int i = 0; i + 1 < nx; i++)
            {
                int a = j * nx + i, b = a + 1, c = a + nx + 1, d = a + nx;
                index.Add(a); index.Add(b); index.Add(c);
                index.Add(a); index.Add(c); index.Add(d);
            }
        // Skirts and walls: along each edge, a strip from the ground down to a little under it, or to the base at the
        // map's edge.
        void Edge(IReadOnlyList<(int X, int Y)> line, Vector3 outward, bool mapEdge)
        {
            for (int e = 0; e + 1 < line.Count; e++)
            {
                var (ax, ay) = line[e];
                var (bx, by) = line[e + 1];
                float ha = H(ax, ay), hb = H(bx, by);
                float la = mapEdge ? Base : ha - 0.6f, lb = mapEdge ? Base : hb - 0.6f;
                int s0 = verts.Count;
                verts.Add(new Vector3(ax, ha, ay)); verts.Add(new Vector3(bx, hb, by)); verts.Add(new Vector3(bx, lb, by)); verts.Add(new Vector3(ax, la, ay));
                for (int q = 0; q < 4; q++) { normals.Add(outward); colors.Add(new Color(1, 1, 1, 0)); }
                index.Add(s0); index.Add(s0 + 1); index.Add(s0 + 2);
                index.Add(s0); index.Add(s0 + 2); index.Add(s0 + 3);
            }
        }
        Edge(xs.Select(x => (x, y0)).ToList(), Vector3.Forward, y0 == 0);
        Edge(xs.Select(x => (x, y1)).ToList(), Vector3.Back, y1 == _city.Height);
        Edge(ys.Select(y => (x0, y)).ToList(), Vector3.Left, x0 == 0);
        Edge(ys.Select(y => (x1, y)).ToList(), Vector3.Right, x1 == _city.Width);
        var arrays = new global::Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
        arrays[(int)Mesh.ArrayType.Normal] = normals.ToArray();
        arrays[(int)Mesh.ArrayType.Color] = colors.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = index.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        _chunks[k].Mesh = mesh;
    }

    /// <summary>
    /// The city's marks on the ground, one texel a cell: roads (until they have their own shapes) and a zone's tint
    /// on empty zoned land. Only the given cells, or all of them.
    /// </summary>
    void PaintCells(int[]? cells)
    {
        int w = _city.Width, h = _city.Height;
        if (_cellImage == null || _cellImage.GetWidth() != w || _cellImage.GetHeight() != h)
        {
            _cellImage = Image.CreateEmpty(w, h, false, Image.Format.Rgba8);
            cells = null;
        }
        void Paint(int i) => _cellImage.SetPixel(i % w, i / w, Palette.Mark(_city, i));
        if (cells == null) for (int i = 0; i < _city.N; i++) Paint(i);
        else foreach (int i in cells) Paint(i);
        if (_cellTexture == null || cells == null) _cellTexture = ImageTexture.CreateFromImage(_cellImage);
        else _cellTexture.Update(_cellImage);
        _ground!.SetShaderParameter("cells", _cellTexture);
        _ground.SetShaderParameter("map_size", new Vector2(w, h));
    }

    /// <summary>The corner heights as a texture, for the water to know how deep it is.</summary>
    void PaintGround()
    {
        int w = _city.Width + 1, h = _city.Height + 1;
        bool fresh = _groundImage == null || _groundImage.GetWidth() != w || _groundImage.GetHeight() != h;
        if (fresh) _groundImage = Image.CreateEmpty(w, h, false, Image.Format.Rf);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                _groundImage!.SetPixel(x, y, new Color(_city.CornerSteps(x, y) * ClassicView.HeightStep, 0, 0));
        if (fresh || _groundTexture == null) _groundTexture = ImageTexture.CreateFromImage(_groundImage);
        else _groundTexture.Update(_groundImage);
        _waterMaterial?.SetShaderParameter("ground", _groundTexture);
    }

    void BuildWater()
    {
        _water?.QueueFree();
        float level = (_city.WaterLevel - 0.35f) * ClassicView.HeightStep;
        // The ground shades its shore and bed by height against the water's surface.
        _ground!.SetShaderParameter("water_y", level);
        _ground.SetShaderParameter("height_step", ClassicView.HeightStep);
        _waterMaterial = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/water.gdshader") };
        _waterMaterial.SetShaderParameter("map_size", new Vector2(_city.Width, _city.Height));
        _waterMaterial.SetShaderParameter("height_step", ClassicView.HeightStep);
        PaintGround();
        _water = new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(_city.Width, _city.Height) },
            Position = new Vector3(_city.Width / 2f, level, _city.Height / 2f),
            MaterialOverride = _waterMaterial,
        };
        AddChild(_water);
    }

    /// <summary>
    /// Which models the buildings are drawn with (<see cref="GraphicsSettings.ClassicDetail"/> and FreeDetail pick
    /// them). Smooth applies to the regions within <paramref name="radius"/> of the one around the focus; the rest
    /// use <paramref name="rest"/>.
    /// </summary>
    public void SetDetail(ModelDetail detail, ModelDetail rest, float focusX, float focusY, int radius)
    {
        var focus = ((int)focusX / Region, (int)focusY / Region);
        if (detail == _detail && (detail != ModelDetail.Smooth || focus == _focus && rest == _rest && radius == _radius)) return;
        (_detail, _rest, _focus, _radius) = (detail, rest, focus, radius);
        foreach (var (key, mmi) in _batches)
            if (_city.TypeById.TryGetValue(key.Item1, out var t)) mmi.Multimesh.Mesh = MeshOf(_models.Get(t, key.Item2, key.Item3), key.Item4);
    }

    Mesh MeshOf(BuildingModels.Model m, int region)
    {
        var d = _detail == ModelDetail.Smooth && (Math.Abs(region % 1024 - _focus.X) > _radius || Math.Abs(region / 1024 - _focus.Y) > _radius) ? _rest : _detail;
        return d switch { ModelDetail.Far => m.Far, ModelDetail.Mid => m.Mid.Value, ModelDetail.Smooth => m.Smooth.Value, _ => m.Near };
    }

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

        var groups = new Dictionary<(string, int, int, int), List<(BuildingState B, float Left)>>();
        var stops = new List<BuildingState>();
        void Add(BuildingState b, float left)
        {
            if (!_city.TypeById.TryGetValue(b.TypeId, out var t) || t.Roadside) { if (left < 0) stops.Add(b); return; }
            var key = (b.TypeId, b.Width, b.Height, b.Y / Region * 1024 + b.X / Region);
            if (!groups.TryGetValue(key, out var list)) groups[key] = list = new();
            list.Add((b, left));
        }
        foreach (var b in _city.Buildings.Values) Add(b, -1);
        foreach (var (b, left) in _life.Crumbling) Add(b, left);
        foreach (var key in _batches.Keys.Where(k => !groups.ContainsKey(k)).ToList()) { _batches[key].QueueFree(); _batches.Remove(key); _members.Remove(key); }
        foreach (var (key, list) in groups)
        {
            var model = _models.Get(_city.TypeById[key.Item1], key.Item2, key.Item3);
            if (!_batches.TryGetValue(key, out var mmi))
            {
                mmi = new MultiMeshInstance3D
                {
                    Multimesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = true, UseCustomData = true, Mesh = MeshOf(model, key.Item4) },
                    MaterialOverride = _voxelMaterial,
                };
                AddChild(mmi);
                _batches[key] = mmi;
            }
            _members[key] = list;
            var mm = mmi.Multimesh;
            mm.InstanceCount = list.Count;
            for (int k = 0; k < list.Count; k++)
            {
                var b = list[k].B;
                float g = 0;
                for (int y = b.Y; y < b.Y + b.Height; y++)
                    for (int x = b.X; x < b.X + b.Width; x++)
                        if (_city.InBounds(x, y)) g = Mathf.Max(g, Ground(_city.Idx(x, y)));
                mm.SetInstanceTransform(k, new Transform3D(Basis.Identity, new Vector3(b.X, g, b.Y)));
                var life = list[k].Left < 0 ? _life.Of(b) : _life.OfCrumbling(b, list[k].Left);
                mm.SetInstanceCustomData(k, Custom(life, model.Height));
                mm.SetInstanceColor(k, Colors.White);  // colours on as well: without them the compatibility renderer drops the vertex colours
                if (list[k].Left >= 0) continue;
                float top = g + model.Height * life.Standing;
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

    /// <summary>A building's life as the shader reads it (see building.gdshader).</summary>
    static Color Custom(Life life, float height) =>
        new(life.Standing >= 1 && !life.Flags.HasFlag(LifeFlags.Crumbling) ? 99f : height * life.Standing, life.Wear, life.Graffiti, (float)(int)life.Flags);

    /// <summary>
    /// Moves building life on a frame: buildings just removed start crumbling, and while anything rises or
    /// crumbles the instances' life data is refreshed.
    /// </summary>
    public void Tick(double dt, List<BuildingState> removed)
    {
        if (!_city.Loaded || _batches.Count == 0 && removed.Count == 0) return;
        _life.Advance(_city, dt, removed);
        int crumbling = _life.Crumbling.Count();
        if (removed.Count > 0 || crumbling != _lastCrumbling) { _lastCrumbling = crumbling; BuildBlocks(); }
        else if (_life.Animating || _wasAnimating) UpdateLife();
        _wasAnimating = _life.Animating;
    }
    int _lastCrumbling;

    void UpdateLife()
    {
        var crumbling = _life.Crumbling.ToDictionary(c => c.B.Id, c => c.Left);
        foreach (var (key, list) in _members)
        {
            var mm = _batches[key].Multimesh;
            var model = _models.Get(_city.TypeById[key.Item1], key.Item2, key.Item3);
            for (int k = 0; k < list.Count && k < mm.InstanceCount; k++)
            {
                var (b, left) = list[k];
                var life = left < 0 ? _life.Of(_city.Buildings.GetValueOrDefault(b.Id, b)) : _life.OfCrumbling(b, crumbling.GetValueOrDefault(b.Id, 0));
                mm.SetInstanceCustomData(k, Custom(life, model.Height));
            }
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
