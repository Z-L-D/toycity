using Godot;

namespace ToyCity.Godot;

/// <summary>
/// The free camera (Tab switches between it and the classic one): a perspective view orbiting a point on the
/// ground. Right-drag turns and tilts, middle-drag or WASD moves, the wheel goes nearer or farther, comma and
/// period turn a quarter. Snap to grid settles the heading on the eight compass points and the point on a cell's
/// middle when you let go; Lock angle keeps the classic tilt so only the heading turns. Ride along sits behind a
/// followed commuter or one of the company's buses, looking the way it goes.
/// </summary>
public partial class FreeCamera : Camera3D
{
    public Vector3 Target;
    public float Yaw = 45, Pitch = 35, Distance = 40;
    public bool Snap = true, LockAngle;
    public int MapWidth = 128, MapHeight = 128;
    public Func<float, float, float>? GroundAt;
    /// <summary>What there is to ride: the followed commuter and each running bus, with where it is and which way it's going.</summary>
    public Func<List<(string Name, Vector3 Pos, Vector3 Dir)>>? Rides;
    /// <summary>Which ride the camera is on: -1 for none.</summary>
    public int Riding = -1;
    public event System.Action? OptionsChanged;
    const float KeyPan = 30;
    bool _orbiting, _panning;
    Vector3 _smoothPos, _smoothLook;
    bool _rideStarted;

    CanvasLayer? _bar;
    Button? _ride;

    public override void _Ready()
    {
        Projection = ProjectionType.Perspective;
        Fov = 50;
        Near = 0.05f;
        Far = 1000;
        // Its options, under the top bar on the left while it's on.
        _bar = new CanvasLayer { Layer = 2, Visible = false };
        AddChild(_bar);
        var row = new HBoxContainer { Position = new Vector2(12, 46) };
        _bar.AddChild(row);
        row.AddChild(new Label { Text = "Free camera (Tab: classic)" });
        var snap = new CheckButton { Text = "Snap to grid", ButtonPressed = Snap, FocusMode = Control.FocusModeEnum.None };
        snap.Toggled += on => { Snap = on; Settle(); };
        row.AddChild(snap);
        var lockAngle = new CheckButton { Text = "Lock angle", ButtonPressed = LockAngle, FocusMode = Control.FocusModeEnum.None };
        lockAngle.Toggled += on => LockAngle = on;
        row.AddChild(lockAngle);
        _ride = new Button { Text = "Ride along: Off", FocusMode = Control.FocusModeEnum.None, TooltipText = "Ride with the followed commuter or one of the company's buses; click again for the next" };
        _ride.Pressed += NextRide;
        row.AddChild(_ride);
    }

    /// <summary>Takes over from the classic camera, looking at what it looked at, from about as far.</summary>
    public void TakeOver(Vector3 target, float yaw, float viewSize)
    {
        (Target, Yaw, Pitch, Distance) = (target, yaw, 35, viewSize * 0.9f + 4);
        Riding = -1;
        Apply(1);
        MakeCurrent();
    }

    /// <summary>The pick ray through a screen point.</summary>
    public (System.Numerics.Vector3 O, System.Numerics.Vector3 D) Ray(Vector2 screen)
    {
        var o = ProjectRayOrigin(screen);
        var d = ProjectRayNormal(screen);
        return (new(o.X, o.Y, o.Z), new(d.X, d.Y, d.Z));
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (!Current) return;
        switch (e)
        {
            case InputEventKey { Pressed: true, Echo: false, Keycode: Key.Comma }: Turn(-45); break;
            case InputEventKey { Pressed: true, Echo: false, Keycode: Key.Period }: Turn(45); break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp }: Distance = Mathf.Max(1.5f, Distance * 0.85f); break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelDown }: Distance = Mathf.Min(300, Distance * 1.18f); break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Right } b:
                _orbiting = b.Pressed;
                if (!b.Pressed) Settle();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Middle } b:
                _panning = b.Pressed;
                if (!b.Pressed) Settle();
                break;
            case InputEventMouseMotion m when _orbiting:
                Yaw -= m.Relative.X * 0.3f;
                if (!LockAngle) Pitch = Mathf.Clamp(Pitch + m.Relative.Y * 0.2f, 8, 88);
                break;
            case InputEventMouseMotion m when _panning:
                Pan(-m.Relative.X * Distance / 600, -m.Relative.Y * Distance / 600);
                break;
            default: return;
        }
        GetViewport().SetInputAsHandled();
    }

    void Turn(float by)
    {
        Yaw += by;
        Settle();
    }

    /// <summary>With snapping on, the heading settles on the nearest compass point and the target on a cell's middle.</summary>
    void Settle()
    {
        if (!Snap) return;
        Yaw = Mathf.Round(Yaw / 45) * 45;
        Target = new Vector3(Mathf.Floor(Target.X) + 0.5f, Target.Y, Mathf.Floor(Target.Z) + 0.5f);
    }

    void Pan(float right, float down)
    {
        float y = Mathf.DegToRad(Yaw);
        var r = new Vector3(Mathf.Cos(y), 0, -Mathf.Sin(y));
        var f = new Vector3(Mathf.Sin(y), 0, Mathf.Cos(y));  // screen down, along the ground
        Target += r * right + f * down;
        Riding = -1;
    }

    public override void _Process(double delta)
    {
        _bar!.Visible = Current;
        if (!Current) return;
        _ride!.Text = $"Ride along: {RideName}";
        float dt = (float)delta;
        if (GetViewport().GuiGetFocusOwner() is not LineEdit)
        {
            float dx = 0, dy = 0;
            if (Input.IsKeyPressed(Key.A) || Input.IsKeyPressed(Key.Left)) dx -= 1;
            if (Input.IsKeyPressed(Key.D) || Input.IsKeyPressed(Key.Right)) dx += 1;
            if (Input.IsKeyPressed(Key.W) || Input.IsKeyPressed(Key.Up)) dy -= 1;
            if (Input.IsKeyPressed(Key.S) || Input.IsKeyPressed(Key.Down)) dy += 1;
            if (dx != 0 || dy != 0) Pan(dx * KeyPan * dt * Mathf.Max(0.3f, Distance / 40), dy * KeyPan * dt * Mathf.Max(0.3f, Distance / 40));
        }
        if (LockAngle) Pitch = 35;
        Apply(Mathf.Min(1, dt * 6));
    }

    /// <summary>Puts the camera in place, easing toward it by <paramref name="ease"/> (1 jumps).</summary>
    void Apply(float ease)
    {
        Target = new Vector3(Mathf.Clamp(Target.X, 0, MapWidth), Target.Y, Mathf.Clamp(Target.Z, 0, MapHeight));
        if (GroundAt != null) Target.Y = GroundAt(Target.X, Target.Z);
        Vector3 pos, look;
        var rides = Riding >= 0 ? Rides?.Invoke() : null;
        if (rides != null && Riding < rides.Count)
        {
            // Behind and a little above the rider, looking past it the way it's going.
            var (_, p, dir) = rides[Riding];
            var d = dir.LengthSquared() > 1e-6f ? dir.Normalized() : new Vector3(Mathf.Sin(Mathf.DegToRad(Yaw)), 0, Mathf.Cos(Mathf.DegToRad(Yaw)));
            pos = p - d * 1.6f + Vector3.Up * 0.7f;
            look = p + d * 3 + Vector3.Up * 0.3f;
            Target = p;
            if (!_rideStarted) { _smoothPos = pos; _smoothLook = look; _rideStarted = true; }
            ease = Mathf.Min(ease, 0.15f);
        }
        else
        {
            if (Riding >= 0) Riding = -1;  // the ride ended: the bus went off the road, the commuter stopped being drawn
            _rideStarted = false;
            float p = Mathf.DegToRad(Pitch), y = Mathf.DegToRad(Yaw);
            var back = new Vector3(Mathf.Cos(p) * Mathf.Sin(y), Mathf.Sin(p), Mathf.Cos(p) * Mathf.Cos(y));
            pos = Target + back * Distance;
            look = Target;
        }
        _smoothPos = ease >= 1 ? pos : _smoothPos.Lerp(pos, ease);
        _smoothLook = ease >= 1 ? look : _smoothLook.Lerp(look, ease);
        // Never below the ground.
        if (GroundAt != null) _smoothPos.Y = Mathf.Max(_smoothPos.Y, GroundAt(_smoothPos.X, _smoothPos.Z) + 0.3f);
        if ((_smoothLook - _smoothPos).LengthSquared() > 1e-6f) LookAtFromPosition(_smoothPos, _smoothLook, Vector3.Up);
    }

    /// <summary>Rides the next thing there is to ride, or stops riding after the last.</summary>
    public void NextRide()
    {
        int n = Rides?.Invoke().Count ?? 0;
        Riding = Riding + 1 < n ? Riding + 1 : -1;
        _rideStarted = false;
        OptionsChanged?.Invoke();
    }

    public string RideName
    {
        get
        {
            var rides = Riding >= 0 ? Rides?.Invoke() : null;
            return rides != null && Riding < rides.Count ? rides[Riding].Name : "Off";
        }
    }
}
