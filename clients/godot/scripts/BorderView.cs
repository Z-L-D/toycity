using Godot;
using ToyCity.ClientModel;

namespace ToyCity.Godot;

/// <summary>
/// Just past each edge of the map that has a neighbouring city: its roads as stubs (a road of this city meets one
/// there), its power lines and pipes as thin posts, and its name standing over the middle of the edge.
/// </summary>
public partial class BorderView : Node3D
{
    readonly CityMirror _city;
    readonly CityView _view;
    object? _shown;
    static readonly StandardMaterial3D Road = new() { AlbedoColor = new Color(0.25f, 0.25f, 0.26f), Roughness = 0.92f };
    static readonly StandardMaterial3D Edge = new() { AlbedoColor = new Color(1f, 1f, 1f, 0.12f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
    static readonly StandardMaterial3D Power = new() { AlbedoColor = new Color(1f, 0.85f, 0.29f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
    static readonly StandardMaterial3D Pipe = new() { AlbedoColor = new Color(0.37f, 0.78f, 1f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };

    public BorderView(CityMirror city, CityView view)
    {
        _city = city;
        _view = view;
    }

    public override void _Process(double delta)
    {
        if (!_city.Loaded || ReferenceEquals(_shown, _city.Borders)) return;
        _shown = _city.Borders;
        foreach (var n in GetChildren()) n.QueueFree();
        const float depth = 1.5f;
        foreach (var b in _city.Borders)
        {
            bool along = b.Side is 0 or 2;
            int len = along ? _city.Width : _city.Height;
            // The middle of cell k just outside this side, on the ground at the edge.
            Vector3 At(float k)
            {
                float x = b.Side switch { 1 => _city.Width + depth / 2, 3 => -depth / 2, _ => k + 0.5f };
                float z = b.Side switch { 0 => -depth / 2, 2 => _city.Height + depth / 2, _ => k + 0.5f };
                float ex = Math.Clamp(x, 0.5f, _city.Width - 0.5f), ez = Math.Clamp(z, 0.5f, _city.Height - 0.5f);
                return new Vector3(x, _view.Surface(ex, ez), z);
            }
            Vector3 Size(float w, float h) => along ? new Vector3(w, h, depth) : new Vector3(depth, h, w);
            var strip = new MeshInstance3D { Mesh = new BoxMesh { Size = Size(len, 0.02f) }, MaterialOverride = Edge, Position = At((len - 1) / 2f) };
            AddChild(strip);
            for (int j = 0; j + 1 < b.Paths.Count; j += 2)
                AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = Size(1, 0.08f) }, MaterialOverride = Road, Position = At(b.Paths[j]) + new Vector3(0, 0.04f, 0) });
            foreach (int k in b.Power) AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.12f, 1.2f, 0.12f) }, MaterialOverride = Power, Position = At(k) + new Vector3(-0.25f, 0.6f, -0.25f) });
            foreach (int k in b.Pipes) AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.12f, 0.5f, 0.12f) }, MaterialOverride = Pipe, Position = At(k) + new Vector3(0.25f, 0.25f, 0.25f) });
            var label = new Label3D
            {
                Text = $"{new[] { "↑", "→", "↓", "←" }[b.Side]} {b.Name}", FontSize = 96, PixelSize = 0.02f, Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                OutlineSize = 16, Modulate = new Color(1, 1, 1, 0.9f), NoDepthTest = true, Position = At((len - 1) / 2f) + new Vector3(0, 3, 0),
            };
            AddChild(label);
        }
    }
}
