using Godot;
using ToyCity.ClientModel.Voxels;
using ToyCity.ClientModel.Voxels.Assets;

namespace ToyCity.Godot;

/// <summary>
/// The realistic asset set's gallery (RC part 2): every type in the set laid out on lots in groups (houses, flats,
/// towers, shops, offices, industry, utilities, services, parks and landmarks, transit), each group in a picture.
/// Opens with --gallery DIR, saves gallery-*.png there and quits with GALLERY OK; used by the gate run.
/// </summary>
public partial class Gallery : Node3D
{
    readonly string _dir;
    readonly StandardMaterial3D _solid = new() { VertexColorUseAsAlbedo = true, VertexColorIsSrgb = true, Roughness = 0.85f };
    readonly ShaderMaterial _city = new() { Shader = GD.Load<Shader>("res://shaders/building.gdshader") };
    Camera3D _cam = null!;
    Node3D _stage = null!;
    int _step;
    double _wait = 0.5;

    public Gallery(string dir) { _dir = dir; }

    /// <summary>The groups: a title, and the types with their lot sizes and variants in rows.</summary>
    static readonly (string Name, (string Id, int W, int D, int V)[][] Rows)[] Groups =
    {
        ("houses", new[]
        {
            new[] { ("r-l-l", 1, 1, 0), ("r-l-l", 1, 1, 1), ("r-l-l", 1, 1, 2), ("r-l-l", 1, 2, 0) },
            new[] { ("r-l-m", 1, 1, 0), ("r-l-m", 1, 1, 1), ("r-l-m", 1, 1, 2), ("r-l-m", 1, 2, 1) },
            new[] { ("r-l-h", 1, 1, 0), ("r-l-h", 1, 1, 1), ("r-l-h", 1, 1, 2), ("r-l-h", 2, 2, 0) },
        }),
        // RC part 5: the houses built of textured polygon walls, and one wrecked to its voxels.
        ("walled-houses", new[] { new[] { ("r-l-m", 1, 1, 1), ("r-l-h", 1, 1, 1), ("r-l-h", 1, 1, 21) } }),
        ("flats", new[]
        {
            new[] { ("r-m-l", 1, 2, 0), ("r-m-l", 2, 2, 1), ("r-m-m", 1, 2, 0), ("r-m-m", 2, 2, 1) },
            new[] { ("r-m-h", 2, 2, 0), ("r-m-h", 2, 3, 1), ("r-m-m", 2, 3, 2) },
        }),
        ("towers", new[] { new[] { ("r-h-l", 2, 3, 0), ("r-h-m", 2, 2, 0), ("r-h-h", 2, 2, 0), ("c-h-l", 2, 2, 0), ("c-h-m", 2, 3, 0), ("c-h-h", 3, 3, 0) } }),
        ("shops-offices", new[]
        {
            new[] { ("c-l-l", 1, 1, 0), ("c-l-l", 2, 1, 1), ("c-l-m", 1, 1, 0), ("c-l-m", 1, 2, 1), ("c-l-h", 1, 1, 0), ("c-l-h", 2, 2, 1) },
            new[] { ("c-m-l", 2, 2, 0), ("c-m-m", 2, 2, 0), ("c-m-h", 2, 2, 0), ("c-m-h", 2, 3, 1) },
        }),
        ("industry", new[]
        {
            new[] { ("i-l-l", 1, 1, 0), ("i-l-m", 1, 2, 0), ("i-l-h", 2, 2, 0), ("i-m-l", 2, 2, 0), ("i-m-m", 2, 3, 0) },
            new[] { ("i-m-h", 2, 2, 0), ("i-h-l", 3, 3, 0), ("i-h-m", 3, 3, 0), ("i-h-h", 3, 3, 0) },
        }),
        ("utilities", new[]
        {
            new[] { ("coal-plant", 0, 0, 0), ("gas-plant", 0, 0, 0), ("hydro-plant", 0, 0, 0), ("wind-turbine", 0, 0, 0), ("solar-farm", 0, 0, 0) },
            new[] { ("water-pump", 0, 0, 0), ("sewage-plant", 0, 0, 0), ("water-treatment", 0, 0, 0), ("landfill", 0, 0, 0), ("toxic-dump", 0, 0, 0) },
        }),
        ("services", new[]
        {
            new[] { ("police-station", 0, 0, 0), ("fire-station", 0, 0, 0), ("clinic", 0, 0, 0), ("hospital", 0, 0, 0) },
            new[] { ("elementary-school", 0, 0, 0), ("high-school", 0, 0, 0), ("city-hall", 0, 0, 0), ("mayors-house", 0, 0, 0) },
        }),
        ("parks-landmarks", new[]
        {
            new[] { ("small-park", 0, 0, 0), ("pocket-park", 0, 0, 0), ("plaza", 0, 0, 0), ("large-park", 0, 0, 0), ("toy-soldier-statue", 0, 0, 0), ("mayor-statue", 0, 0, 0) },
            new[] { ("rubber-duck-fountain", 0, 0, 0), ("clocktower", 0, 0, 0), ("observation-wheel", 0, 0, 0), ("casino", 0, 0, 0), ("pickle-plant", 0, 0, 0) },
        }),
        ("transit", new[]
        {
            new[] { ("bus-depot", 0, 0, 0), ("rail-station", 0, 0, 0), ("subway-station", 0, 0, 0), ("freight-depot", 0, 0, 0) },
            new[] { ("tram-depot", 0, 0, 0), ("rail-depot", 0, 0, 0), ("subway-depot", 0, 0, 0) },
        }),
        // Round things as voxels (front) and as the shapes' polygons the city draws (back).
        ("shapes", new[]
        {
            new[] { ("coal-plant", 0, 0, 10), ("water-pump", 0, 0, 10), ("city-hall", 0, 0, 10), ("r-l-h", 1, 1, 10) },
            new[] { ("coal-plant", 0, 0, 0), ("water-pump", 0, 0, 0), ("city-hall", 0, 0, 0), ("r-l-h", 1, 1, 0) },
        }),
        // The destruction swap: whole (front) and wrecked, its voxels holed and charred (back).
        ("wrecked", new[]
        {
            new[] { ("r-l-m", 1, 1, 0), ("r-m-m", 2, 2, 0), ("c-m-m", 2, 2, 0), ("i-m-l", 2, 2, 0) },
            new[] { ("r-l-m", 1, 1, 20), ("r-m-m", 2, 2, 20), ("c-m-m", 2, 2, 20), ("i-m-l", 2, 2, 20) },
        }),
    };

    Dictionary<string, (int W, int H)> _civic = new();

    public override void _Ready()
    {
        DirAccess.MakeDirRecursiveAbsolute(_dir);
        foreach (var t in AssetEditor.BuildingTypes()) if (t.W > 0) _civic[t.Id] = (t.W, t.H);
        var env = new global::Godot.Environment
        {
            BackgroundMode = global::Godot.Environment.BGMode.Color, BackgroundColor = new Color("#9db4c8"),
            AmbientLightSource = global::Godot.Environment.AmbientSource.Color, AmbientLightColor = new Color("#b4c3d6"), AmbientLightEnergy = 0.35f,
            TonemapMode = global::Godot.Environment.ToneMapper.Agx, SsaoEnabled = true,
        };
        AddChild(new WorldEnvironment { Environment = env });
        AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-48, -35, 0), LightEnergy = 0.9f, ShadowEnabled = true, DirectionalShadowMaxDistance = 600 });
        AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-30, 150, 0), LightEnergy = 0.18f });
        AddChild(_cam = new Camera3D { Fov = 35, Current = true, Far = 4000 });
        AddChild(_stage = new Node3D());
    }

    public override void _Process(double delta)
    {
        if ((_wait -= delta) > 0) return;
        if (_step > 0)
        {
            string name = $"gallery-{_step}-{Groups[_step - 1].Name}.png";
            GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(_dir, name));
            GD.Print($"GALLERY SHOT {name}");
        }
        if (_step == Groups.Length) { GD.Print($"GALLERY OK {RealSet.Types.Count()} types"); GetTree().Quit(); return; }
        Lay(Groups[_step++]);
        _wait = 0.6;
    }

    /// <summary>Lays a group out: rows front to back, lots side by side with a street's gap, and frames the camera.</summary>
    void Lay((string Name, (string Id, int W, int D, int V)[][] Rows) g)
    {
        foreach (var c in _stage.GetChildren()) c.QueueFree();
        const float gap = 6, cell = (float)RealSet.Cell;
        float z = 0, widest = 0, tallest = 0;
        foreach (var row in g.Rows)
        {
            float x = 0, deep = 0;
            foreach (var (id, w0, d0, v) in row)
            {
                var (w, d) = w0 > 0 ? (w0, d0) : _civic.GetValueOrDefault(id, (1, 1));
                // A variant of 10 more is drawn all voxels (as before shapes), 20 more wrecked.
                int mode = v / 10;
                if (RealSet.Build(id, w, d, v % 10) is not { } r) continue;
                float scale = 1f / r.Detail / VoxModel.PerCell * cell;
                var mesh = VoxMesher.Mesh(mode == 2 ? Wreck.Carve(r.Model, r.Detail, id.Length * 7919 + v) : mode == 1 ? r.Model : r.Boxy, scale);
                if (r.Roof != null && mode != 2) mesh.Append(AutoRoof.Build(r.Model, r.Detail, r.Roof, scale));
                if (mode == 0) foreach (var sh in r.Shapes) mesh.Append(sh.Mesh(scale * 2 * r.Detail, 1, r.Model.Palette));
                if (g.Name == "walled-houses")
                {
                    // RC part 5: drawn as the city draws them, one instance of a multimesh with the building shader,
                    // each finish its own surface with a copy of it.
                    var arr = BuildingModels.ToMesh(mesh);
                    var mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = true, UseCustomData = true, Mesh = arr, InstanceCount = 1 };
                    mm.SetInstanceTransform(0, Transform3D.Identity);
                    mm.SetInstanceColor(0, Colors.White);   // as the city does: without colours the compatibility renderer drops the vertex colours
                    mm.SetInstanceCustomData(0, new Color(1000, 0.15f, 0, 0.5f));
                    var mmi = new MultiMeshInstance3D { Multimesh = mm, Position = new Vector3(x, 0, z) };
                    if (!FinishTextures.Apply(arr, _city)) mmi.MaterialOverride = _city;
                    _stage.AddChild(mmi);
                }
                else
                {
                    var mi = new MeshInstance3D { Position = new Vector3(x, 0, z) };
                    FinishTextures.Show(mi, BuildingModels.ToMesh(mesh), _solid);
                    _stage.AddChild(mi);
                }
                tallest = Math.Max(tallest, mesh.Positions.Count > 0 ? mesh.Positions.Max(p => p.Y) : 0);
                x += w * cell + gap;
                deep = Math.Max(deep, d * cell);
            }
            widest = Math.Max(widest, x - gap);
            z += deep + gap;
        }
        float depth = z - gap;
        // A ground under it all, the grey of a street.
        _stage.AddChild(new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(widest + 40, 0.2f, depth + 40) },
            Position = new Vector3(widest / 2, -0.11f, depth / 2),
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color("#7d7f80"), Roughness = 1 },
        });
        var target = new Vector3(widest / 2, tallest * 0.3f, depth / 2);
        float size = Math.Max(widest, Math.Max(depth * 1.3f, tallest * 1.4f));
        var dir = new Vector3(-0.55f, 0.62f, 1f).Normalized();
        _cam.Position = target + dir * size * 1.08f;
        _cam.LookAt(target, Vector3.Up);
    }
}
