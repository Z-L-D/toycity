using Godot;
using ToyCity.ClientModel;
using ToyCity.ClientModel.Voxels;
using ToyCity.Protocol.V0;

namespace ToyCity.Godot;

/// <summary>
/// Disasters on the move: the Clockwork Colossus walking (it turns to face where it's going and bobs with each
/// step), tornadoes spinning, and the ground shaking under an earthquake. Fires show on the buildings themselves.
/// Positions arrive daily, so each is eased toward its latest spot.
/// </summary>
public partial class DisasterView : Node3D
{
    readonly CityMirror _city;
    readonly Dictionary<int, MeshInstance3D> _movers = new();
    readonly Dictionary<int, Vector3> _shown = new();
    ArrayMesh? _colossus, _tornado;
    double _time;
    /// <summary>How hard the ground shakes now, 0 when no earthquake is on.</summary>
    public float Shake { get; private set; }

    public DisasterView(CityMirror city) => _city = city;

    public override void _Ready()
    {
        _colossus = BuildingModels.ToMesh(VoxMesher.Mesh(Placeholder.Colossus()));
        _tornado = BuildingModels.ToMesh(VoxMesher.Mesh(Placeholder.Tornado()));
    }

    static readonly StandardMaterial3D Voxels = new() { VertexColorUseAsAlbedo = true, VertexColorIsSrgb = true, Roughness = 0.6f, Metallic = 0.3f };

    public override void _Process(double delta)
    {
        _time += delta;
        Shake = 0;
        if (!_city.Loaded) return;
        var seen = new HashSet<int>();
        foreach (var d in _city.Stats.Disasters)
        {
            if (d.Kind == DisasterKind.DisasterEarthquake) { Shake = Mathf.Max(Shake, Mathf.Min(1, d.DaysLeft / 3f) * 0.25f); continue; }
            if (d.Kind is not (DisasterKind.DisasterRobot or DisasterKind.DisasterTornado)) continue;
            seen.Add(d.Id);
            float x = d.X / 256f, y = d.Y / 256f;
            int cx = Mathf.Clamp((int)x, 0, _city.Width - 1), cy = Mathf.Clamp((int)y, 0, _city.Height - 1);
            var target = new Vector3(x, _city.Heights[_city.Idx(cx, cy)] * ClassicView.HeightStep, y);
            if (!_movers.TryGetValue(d.Id, out var m))
            {
                bool robot = d.Kind == DisasterKind.DisasterRobot;
                m = new MeshInstance3D { Mesh = robot ? _colossus : _tornado, MaterialOverride = Voxels };
                AddChild(m);
                _movers[d.Id] = m;
                _shown[d.Id] = target;
            }
            var was = _shown[d.Id];
            var now = was.Lerp(target, Mathf.Min(1, (float)delta * 3));
            _shown[d.Id] = now;
            if (d.Kind == DisasterKind.DisasterRobot)
            {
                // Face the way it's walking; models face +z (south) at no rotation.
                var step = target - was;
                float yaw = step.LengthSquared() > 1e-4f ? Mathf.Atan2(step.X, step.Z) : m.Rotation.Y;
                float bob = Mathf.Abs(Mathf.Sin((float)_time * 4)) * 0.08f;
                m.Transform = new Transform3D(new Basis(Vector3.Up, yaw), now + new Vector3(0, bob, 0)) * Transform3D.Identity.Translated(new Vector3(-1, 0, -1));
            }
            else
                m.Transform = new Transform3D(new Basis(Vector3.Up, (float)_time * 3), now) * Transform3D.Identity.Translated(new Vector3(-1.5f, 0, -1.5f));
        }
        foreach (var id in _movers.Keys.Where(k => !seen.Contains(k)).ToList()) { _movers[id].QueueFree(); _movers.Remove(id); _shown.Remove(id); }
    }
}
