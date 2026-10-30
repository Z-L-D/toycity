using Godot;
using ToyCity.ClientModel;
using ToyCity.ClientModel.Panels;

namespace ToyCity.Godot;

/// <summary>
/// The followed commuters on the map, moving along their routes through the morning and evening rush on the
/// view's own clock (<see cref="Commuters"/>): yellow drive, blue ride transit. A followed commuter is drawn
/// larger, with their whole route, wherever they are, even at home or at work.
/// </summary>
public partial class AgentsView : Node3D
{
    readonly CityMirror _city;
    readonly PanelModel _panel;
    readonly CityView _view;
    MultiMeshInstance3D? _dots;
    MeshInstance3D? _marker, _route;
    long? _routeOf;
    static readonly Color Drive = new(1, 0.82f, 0.35f), Ride = new(0.35f, 0.82f, 1);

    /// <summary>Where the followed commuter is now, in cells, or null.</summary>
    public Vector2? FollowedAt { get; private set; }
    /// <summary>Which way the followed commuter is heading, along the ground.</summary>
    public Vector2 FollowedDir { get; private set; }
    public string FollowedStatus { get; private set; } = "";
    public double Minute { get; private set; }

    public AgentsView(CityMirror city, PanelModel panel, CityView view) => (_city, _panel, _view) = (city, panel, view);

    public override void _Ready()
    {
        var unshaded = new StandardMaterial3D { VertexColorUseAsAlbedo = true, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
        _dots = new MultiMeshInstance3D
        {
            Multimesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = true, Mesh = new BoxMesh { Size = new Vector3(0.22f, 0.22f, 0.22f) } },
            MaterialOverride = unshaded,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(_dots);
        _marker = new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = 0.3f, Height = 0.6f },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = Colors.White, EmissionEnabled = true, Emission = new Color(1, 1, 1), EmissionEnergyMultiplier = 0.4f },
            Visible = false,
        };
        AddChild(_marker);
        _route = new MeshInstance3D
        {
            Mesh = new ImmediateMesh(),
            MaterialOverride = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = new Color(1, 1, 1, 0.8f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha, NoDepthTest = true },
        };
        AddChild(_route);
    }

    public float Height(float x, float y)
    {
        return _view.Surface(x, y) + 0.15f;
    }

    public override void _Process(double delta)
    {
        var trips = _panel.Roster?.Trips;
        var mm = _dots!.Multimesh;
        // The clock stands still while the game is paused, as the buses do.
        if (_city.Stats.Speed > 0) _seconds += delta;
        var clock = Commuters.Clock(_seconds);
        Minute = clock.Minute;
        int n = 0;
        if (trips != null && _panel.ShowAgents && _city.Loaded)
        {
            if (mm.InstanceCount < trips.Count) mm.InstanceCount = trips.Count;
            foreach (var t in trips)
            {
                if (t.Id == _panel.Followed) continue;
                if (Commuters.At(t, clock, _city.Width) is not { State: TripState.Moving } p) continue;
                mm.SetInstanceTransform(n, new Transform3D(Basis.Identity, new Vector3(p.X + 0.5f, Height(p.X + 0.5f, p.Y + 0.5f), p.Y + 0.5f)));
                mm.SetInstanceColor(n, t.ByTransit ? Ride : Drive);
                n++;
            }
        }
        mm.VisibleInstanceCount = n;
        Followed(clock);
    }
    double _seconds;

    void Followed((double Minute, bool Evening) clock)
    {
        var t = _panel.FollowedTrip;
        if (t == null || Commuters.At(t, clock, _city.Width) is not { } p)
        {
            _marker!.Visible = false;
            if (_routeOf != null) { ((ImmediateMesh)_route!.Mesh).ClearSurfaces(); _routeOf = null; }
            FollowedAt = null;
            return;
        }
        var at = new Vector2(p.X + 0.5f, p.Y + 0.5f);
        if (FollowedAt is { } before && (at - before).LengthSquared() > 1e-6f) FollowedDir = (at - before).Normalized();
        FollowedAt = at;
        FollowedStatus = Commuters.Status(t, p.State, clock.Evening);
        _marker!.Visible = true;
        float pulse = 1 + 0.2f * MathF.Sin(Time.GetTicksMsec() / 200f);
        _marker.Scale = Vector3.One * pulse;
        _marker.Position = new Vector3(p.X + 0.5f, Height(p.X + 0.5f, p.Y + 0.5f) + 0.25f, p.Y + 0.5f);
        ((StandardMaterial3D)_marker.MaterialOverride).AlbedoColor = t.ByTransit ? Ride : Drive;
        if (_routeOf == t.Id) return;
        _routeOf = t.Id;
        var mesh = (ImmediateMesh)_route!.Mesh;
        mesh.ClearSurfaces();
        if (t.Cells.Count < 2) return;
        mesh.SurfaceBegin(Mesh.PrimitiveType.LineStrip);
        foreach (int c in t.Cells)
        {
            float x = c % _city.Width + 0.5f, y = c / _city.Width + 0.5f;
            mesh.SurfaceAddVertex(new Vector3(x, Height(x, y) + 0.05f, y));
        }
        mesh.SurfaceEnd();
    }
}
