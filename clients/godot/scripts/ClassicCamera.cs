using Godot;
using ToyCity.ClientModel;

namespace ToyCity.Godot;

/// <summary>
/// The classic camera, as SC4's: orthographic, four rotations (comma and period), five zoom levels (wheel, plus
/// and minus, Page Up and Down), top-down (T) and side (Y) views that toggle back to the classic angle, and
/// panning by arrow keys or WASD, by dragging with the right or middle button, or by pushing the mouse
/// against a window edge. Its pose comes from <see cref="ClassicView"/>, which picking shares.
/// </summary>
public partial class ClassicCamera : Camera3D
{
    public readonly ClassicView View = new();
    public int MapWidth, MapHeight;
    public Func<float, float, float>? GroundAt;  // ground height under a map point, so the view centres on hills
    public bool EdgePan = true;
    /// <summary>How hard an earthquake shakes the view, in cells; the view itself (and picking) stays put.</summary>
    public float Shake;
    const float KeyPanPixels = 900, EdgePanPixels = 700, Edge = 6;
    bool _dragging;

    public override void _Ready()
    {
        Projection = ProjectionType.Orthogonal;
        KeepAspect = KeepAspectEnum.Height;
        Near = 1;
        Far = 600;
        Apply();
    }

    /// <summary>Moves the camera to a map point.</summary>
    public void CentreOn(float x, float y)
    {
        View.Target = new System.Numerics.Vector2(x, y);
        Apply();
    }

    public override void _UnhandledInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventKey { Pressed: true, Echo: false } k:
                switch (k.Keycode)
                {
                    case Key.Comma: View.Rotate(-1); break;
                    case Key.Period: View.Rotate(1); break;
                    case Key.Equal or Key.KpAdd or Key.Pageup: View.ZoomBy(1); break;
                    case Key.Minus or Key.KpSubtract or Key.Pagedown: View.ZoomBy(-1); break;
                    case Key.T: View.Mode = View.Mode == ViewMode.TopDown ? ViewMode.Classic : ViewMode.TopDown; break;
                    case Key.Y: View.Mode = View.Mode == ViewMode.Side ? ViewMode.Classic : ViewMode.Side; break;
                    default: return;
                }
                GetViewport().SetInputAsHandled();
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp }:
                View.ZoomBy(1); break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelDown }:
                View.ZoomBy(-1); break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Right or MouseButton.Middle } b:
                _dragging = b.Pressed; break;
            case InputEventMouseMotion m when _dragging:
                View.Pan(-m.Relative.X, -m.Relative.Y, GetViewport().GetVisibleRect().Size.Y); break;
        }
    }

    public override void _Process(double delta)
    {
        var size = GetViewport().GetVisibleRect().Size;
        float dt = (float)delta, dx = 0, dy = 0;
        if (Input.IsKeyPressed(Key.Left) || Input.IsKeyPressed(Key.A)) dx -= 1;
        if (Input.IsKeyPressed(Key.Right) || Input.IsKeyPressed(Key.D)) dx += 1;
        if (Input.IsKeyPressed(Key.Up) || Input.IsKeyPressed(Key.W)) dy -= 1;
        if (Input.IsKeyPressed(Key.Down) || Input.IsKeyPressed(Key.S)) dy += 1;
        dx *= KeyPanPixels * dt;
        dy *= KeyPanPixels * dt;
        if (EdgePan && !_dragging && DisplayServer.WindowIsFocused())
        {
            var m = GetViewport().GetMousePosition();
            bool inside = m.X >= 0 && m.Y >= 0 && m.X <= size.X && m.Y <= size.Y;
            if (inside && m.X < Edge) dx -= EdgePanPixels * dt;
            if (inside && m.X > size.X - Edge) dx += EdgePanPixels * dt;
            if (inside && m.Y < Edge) dy -= EdgePanPixels * dt;
            if (inside && m.Y > size.Y - Edge) dy += EdgePanPixels * dt;
        }
        if (dx != 0 || dy != 0) View.Pan(dx, dy, size.Y);
        Apply();
    }

    /// <summary>Puts the Godot camera where the view says.</summary>
    void Apply()
    {
        if (MapWidth > 0) View.Clamp(MapWidth, MapHeight);
        if (GroundAt != null) View.TargetHeight = GroundAt(View.Target.X, View.Target.Y);
        Size = View.Size;
        var r = View.Right; var u = View.Up; var f = View.Forward; var p = View.Position;
        if (Shake > 0) p += (r * (float)GD.RandRange(-1.0, 1.0) + u * (float)GD.RandRange(-1.0, 1.0)) * Shake;
        Transform = new Transform3D(new Basis(new Vector3(r.X, r.Y, r.Z), new Vector3(u.X, u.Y, u.Z), new Vector3(-f.X, -f.Y, -f.Z)), new Vector3(p.X, p.Y, p.Z));
    }

    /// <summary>The cell under a screen point, using the view's own rays.</summary>
    public (int X, int Y)? Pick(Vector2 screen, CityView city)
    {
        var size = GetViewport().GetVisibleRect().Size;
        var (o, d) = View.Ray(screen.X, screen.Y, size.X, size.Y);
        return ClassicView.Pick(o, d, MapWidth, MapHeight, city.Top, city.MaxTop);
    }
}
