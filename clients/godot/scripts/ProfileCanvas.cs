using Godot;
using ToyCity.ClientModel.Voxels.Assets;

namespace ToyCity.Godot;

/// <summary>
/// Where a shape's 2D profile is drawn, on a grid in metres with guides for scale: half-metre and metre lines, a
/// line every storey (3 m), a person 1.8 m tall, and for a lathe the axis it spins round. Click to add a point, drag
/// one to move it, right-click to take one away, wheel to show more or less ground. Points snap to the grid.
/// </summary>
public partial class ProfileCanvas : Control
{
    public List<(double A, double B)> Points = new();
    public ShapeKind Kind = ShapeKind.Lathe;
    public ExtrudeAxis Axis = ExtrudeAxis.Back;
    public bool Closed = true;
    /// <summary>Metres across the canvas.</summary>
    public double Span = 8;
    public double Snap = 0.125;
    public event Action? Changed;
    int _drag = -1;
    const float Margin = 18;

    static readonly Color Paper = new(0.10f, 0.12f, 0.16f, 0.92f), Minor = new(1, 1, 1, 0.06f), Major = new(1, 1, 1, 0.16f),
        Storey = new(0.55f, 0.75f, 1f, 0.45f), AxisLine = new(1f, 0.75f, 0.35f, 0.8f), Line = new(0.55f, 0.95f, 1f), Dot = Colors.White,
        Mirror = new(0.55f, 0.95f, 1f, 0.22f), Person = new(1, 1, 1, 0.3f);

    /// <summary>Metres a pixel.</summary>
    double Mpp => Span / Math.Max(1, Size.X - 2 * Margin);

    /// <summary>The profile's origin on the canvas: a lathe's axis at the left; elevations centred, standing on the bottom; plans and sections centred.</summary>
    Vector2 Origin => Kind switch
    {
        ShapeKind.Lathe => new Vector2(Margin + (Size.X - 2 * Margin) * 0.12f, Size.Y - Margin),
        ShapeKind.Extrude when Axis != ExtrudeAxis.Up => new Vector2(Size.X / 2, Size.Y - Margin),
        _ => new Vector2(Size.X / 2, Size.Y / 2),
    };

    Vector2 ToPx((double A, double B) p) => Origin + new Vector2((float)(p.A / Mpp), (float)(-p.B / Mpp));

    (double A, double B) FromPx(Vector2 px)
    {
        var d = px - Origin;
        double a = Math.Round(d.X * Mpp / Snap) * Snap, b = Math.Round(-d.Y * Mpp / Snap) * Snap;
        if (Kind == ShapeKind.Lathe) a = Math.Max(0, a);
        return (a, b);
    }

    public override void _Draw()
    {
        DrawRect(new Rect2(Vector2.Zero, Size), Paper);
        var font = ThemeDB.FallbackFont;
        // Grid lines every half metre, stronger every metre, and a storey line every 3 m (labelled).
        double left = -Origin.X * Mpp, right = (Size.X - Origin.X) * Mpp, top = Origin.Y * Mpp, bottom = -(Size.Y - Origin.Y) * Mpp;
        double step = Span > 24 ? 2 : Span > 12 ? 1 : 0.5;
        for (double a = Math.Ceiling(left / step) * step; a <= right; a += step)
        {
            float x = ToPx((a, 0)).X;
            DrawLine(new Vector2(x, 0), new Vector2(x, Size.Y), Math.Abs(a % 1) < 1e-6 ? Major : Minor);
        }
        for (double b = Math.Ceiling(bottom / step) * step; b <= top; b += step)
        {
            float y = ToPx((0, b)).Y;
            bool storey = Kind != ShapeKind.Sweep && !(Kind == ShapeKind.Extrude && Axis == ExtrudeAxis.Up) && b > 0 && Math.Abs(b % 3) < 1e-6;
            DrawLine(new Vector2(0, y), new Vector2(Size.X, y), storey ? Storey : Math.Abs(b % 1) < 1e-6 ? Major : Minor, storey ? 1.5f : 1);
            if (storey) DrawString(font, new Vector2(Size.X - 70, y - 3), $"storey {b / 3:0}", HorizontalAlignment.Left, -1, 11, Storey);
        }
        // The axis a lathe spins round, or the path a sweep follows (its origin).
        if (Kind == ShapeKind.Lathe)
        {
            DrawDashedLine(new Vector2(Origin.X, 0), new Vector2(Origin.X, Size.Y), AxisLine, 1.5f, 6);
            DrawString(font, new Vector2(Origin.X + 4, 14), "axis", HorizontalAlignment.Left, -1, 11, AxisLine);
        }
        else
        {
            DrawLine(new Vector2(Origin.X - 6, Origin.Y), new Vector2(Origin.X + 6, Origin.Y), AxisLine, 1.5f);
            DrawLine(new Vector2(Origin.X, Origin.Y - 6), new Vector2(Origin.X, Origin.Y + 6), AxisLine, 1.5f);
            if (Kind == ShapeKind.Sweep) DrawString(font, Origin + new Vector2(5, -5), "path", HorizontalAlignment.Left, -1, 11, AxisLine);
        }
        // A person for scale, 1.8 m tall, standing on the ground line (or beside the plan).
        {
            double px = Kind == ShapeKind.Lathe ? right - 1.2 : left + 0.8, pb = Kind is ShapeKind.Lathe || Kind == ShapeKind.Extrude && Axis != ExtrudeAxis.Up ? 0 : bottom + 0.2;
            var foot = ToPx((px, pb));
            float h = (float)(1.8 / Mpp), w = (float)(0.45 / Mpp);
            DrawRect(new Rect2(foot.X - w / 2, foot.Y - h * 0.82f, w, h * 0.82f), Person);
            DrawCircle(new Vector2(foot.X, foot.Y - h * 0.91f), h * 0.09f, Person);
            DrawString(font, new Vector2(foot.X - 14, foot.Y - h - 4), "1.8 m", HorizontalAlignment.Left, -1, 10, Person);
        }
        // A metre scale bar.
        var bar0 = new Vector2(Margin, Size.Y - 6);
        DrawLine(bar0, bar0 + new Vector2((float)(1 / Mpp), 0), Colors.White, 2);
        DrawString(font, bar0 + new Vector2((float)(1 / Mpp) + 4, 4), "1 m", HorizontalAlignment.Left, -1, 10, Colors.White);

        if (Points.Count == 0)
        {
            DrawString(font, new Vector2(Margin, 18), Kind == ShapeKind.Lathe ? "Click to draw half the outline; it spins round the axis" : "Click to draw the outline", HorizontalAlignment.Left, -1, 12, Colors.White);
            return;
        }
        var px2 = Points.Select(ToPx).ToArray();
        // A lathe's other half, faint, so the outline reads as the solid it makes.
        if (Kind == ShapeKind.Lathe)
            for (int i = 0; i + 1 < px2.Length; i++)
                DrawLine(Flip(px2[i]), Flip(px2[i + 1]), Mirror, 1.5f);
        for (int i = 0; i + 1 < px2.Length; i++) DrawLine(px2[i], px2[i + 1], Line, 2);
        bool closes = Kind == ShapeKind.Lathe ? Closed || Points[0].A == 0 && Points[^1].A == 0 : Closed;
        if (closes && px2.Length > 2) DrawDashedLine(px2[^1], px2[0], Line, 1.5f, 5);
        for (int i = 0; i < px2.Length; i++) DrawCircle(px2[i], i == _drag ? 6 : 4, i == 0 ? AxisLine : Dot);
    }

    Vector2 Flip(Vector2 p) => new(2 * Origin.X - p.X, p.Y);

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton mb)
        {
            if (mb.ButtonIndex == MouseButton.WheelUp && mb.Pressed) { Span = Math.Max(2, Span * 0.8); QueueRedraw(); }
            else if (mb.ButtonIndex == MouseButton.WheelDown && mb.Pressed) { Span = Math.Min(64, Span * 1.25); QueueRedraw(); }
            else if (mb.ButtonIndex == MouseButton.Left && mb.Pressed)
            {
                _drag = Nearest(mb.Position, 9);
                if (_drag < 0) { Points.Add(FromPx(mb.Position)); _drag = Points.Count - 1; Changed?.Invoke(); }
                QueueRedraw();
            }
            else if (mb.ButtonIndex == MouseButton.Left) { _drag = -1; QueueRedraw(); }
            else if (mb.ButtonIndex == MouseButton.Right && mb.Pressed && Points.Count > 0)
            {
                int i = Nearest(mb.Position, 12);
                Points.RemoveAt(i >= 0 ? i : Points.Count - 1);
                Changed?.Invoke();
                QueueRedraw();
            }
            AcceptEvent();
        }
        else if (e is InputEventMouseMotion mm && _drag >= 0 && _drag < Points.Count)
        {
            var p = FromPx(mm.Position);
            if (p != Points[_drag]) { Points[_drag] = p; Changed?.Invoke(); QueueRedraw(); }
            AcceptEvent();
        }
    }

    int Nearest(Vector2 at, float within)
    {
        int best = -1;
        float bd = within;
        for (int i = 0; i < Points.Count; i++)
        {
            float d = ToPx(Points[i]).DistanceTo(at);
            if (d <= bd) { bd = d; best = i; }
        }
        return best;
    }

    /// <summary>Shows a profile (a preset's, say) and fits the canvas round it.</summary>
    public void Show(Shape s)
    {
        Kind = s.Kind;
        Axis = s.Axis;
        Closed = s.Closed;
        Points = s.Profile.ToList();
        double extent = Points.Count == 0 ? 4 : Points.Max(p => Math.Max(Math.Abs(p.A) * (Kind == ShapeKind.Lathe ? 1.3 : 2.4), Math.Abs(p.B) * 1.25));
        Span = Math.Clamp(Math.Ceiling(extent + 1), 2, 64);
        QueueRedraw();
    }
}
