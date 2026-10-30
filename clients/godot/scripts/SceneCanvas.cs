using Godot;
using ToyCity.ClientModel.Panels;
using ToyCity.Emergency.Wire;

namespace ToyCity.Godot;

/// <summary>
/// An emergency scene from above, as the web client draws it: the ground, the fire, the people and the vehicles, with
/// the paths they're on. Clicks go to <see cref="EmergencyModel"/>: left to select or give the picked order, right to
/// send the selected somewhere, a drag to draw a path.
/// </summary>
public partial class SceneCanvas : Control
{
    const float Tile = 12;
    readonly EmergencyModel _model;
    static readonly Color[] Ground = { new("#c9c0a0"), new("#6e6e6e"), new("#a88d6c"), new("#4a7fe0"), new("#7cbf6a"), new("#3a3330") };
    static readonly Color[] Roles = { new("#e04f3a"), new("#f2f2f2"), new("#3f7fe0"), new("#9aa0a8"), new("#f0c030"), new("#a050d0") };
    static readonly Color[] Vehicles = { new("#c0392b"), new("#2c5aa0"), new("#e8e8e8") };

    public SceneCanvas(EmergencyModel model)
    {
        _model = model;
        CustomMinimumSize = new Vector2(36 * Tile, 36 * Tile);
        MouseFilter = MouseFilterEnum.Stop;
    }

    (int X, int Y) TileAt(Vector2 p) => ((int)(p.X / Tile), (int)(p.Y / Tile));

    public override void _GuiInput(InputEvent e)
    {
        var s = _model.Scene;
        if (s == null) return;
        if (e is InputEventMouseButton { Pressed: true } down)
        {
            var (x, y) = TileAt(down.Position);
            if (x < 0 || y < 0 || x >= s.Width || y >= s.Height) return;
            if (down.ButtonIndex == MouseButton.Left) _model.PointerDown(x, y, down.ShiftPressed);
            else if (down.ButtonIndex == MouseButton.Right) _model.MoveTo(x, y);
            AcceptEvent();
            QueueRedraw();
        }
        else if (e is InputEventMouseButton { Pressed: false, ButtonIndex: MouseButton.Left })
        {
            _model.PointerUp();
            QueueRedraw();
        }
        else if (e is InputEventMouseMotion m && _model.Drawing != null)
        {
            var (x, y) = TileAt(m.Position);
            if (x < 0 || y < 0 || x >= s.Width || y >= s.Height) return;
            _model.PointerMove(x, y);
            QueueRedraw();
        }
    }

    public override void _Draw()
    {
        var s = _model.Scene;
        if (s == null) return;
        var tiles = s.Tiles.Span;
        var fire = s.Fire.Span;
        for (int y = 0; y < s.Height; y++)
            for (int x = 0; x < s.Width; x++)
            {
                int i = y * s.Width + x;
                var r = new Rect2(x * Tile, y * Tile, Tile, Tile);
                DrawRect(r, i < tiles.Length && tiles[i] < Ground.Length ? Ground[tiles[i]] : Colors.Black);
                if (i < fire.Length && fire[i] > 0) DrawRect(r, new Color(1, Math.Max(40, 160 - fire[i]) / 255f, 0, 0.35f + fire[i] / 160f));
            }
        Vector2 Centre(int cell) => new((cell % s.Width + 0.5f) * Tile, (cell / s.Width + 0.5f) * Tile);
        void Path(IList<int> cells, Color c)
        {
            for (int k = 1; k < cells.Count; k++) DrawLine(Centre(cells[k - 1]), Centre(cells[k]), c, 2);
        }
        foreach (var v in s.Vehicles)
        {
            Path(v.Path, new Color(1, 1, 1, 0.6f));
            var at = new Vector2(v.X / 16f * Tile, v.Y / 16f * Tile);
            DrawRect(new Rect2(at - new Vector2(Tile * 0.7f, Tile * 0.45f), new Vector2(Tile * 1.4f, Tile * 0.9f)), Vehicles[(int)v.Kind % 3]);
            if (_model.Selected.Contains(v.Id)) DrawRect(new Rect2(at - new Vector2(Tile * 0.8f, Tile * 0.55f), new Vector2(Tile * 1.6f, Tile * 1.1f)), Colors.Yellow, false, 2);
        }
        foreach (var p in s.People)
        {
            if (p.State == "escaped") continue;
            var at = new Vector2(p.X / 16f * Tile, p.Y / 16f * Tile);
            if ((int)p.Role <= 2) Path(p.Path, new Color(1, 0.92f, 0.23f, 0.5f));
            DrawCircle(at, Tile * 0.33f, Roles[(int)p.Role % Roles.Length]);
            if (p.Armed) DrawArc(at, Tile * 0.33f, 0, Mathf.Tau, 16, Colors.Black, 2);
            if (p.Down)
            {
                var c = p.State == "dead" ? Colors.Black : Colors.White;
                DrawLine(at + new Vector2(-4, -4), at + new Vector2(4, 4), c, 1.5f);
                DrawLine(at + new Vector2(4, -4), at + new Vector2(-4, 4), c, 1.5f);
            }
            if (_model.Selected.Contains(p.Id)) DrawArc(at, Tile * 0.5f, 0, Mathf.Tau, 20, Colors.Yellow, 2);
            if (p.Health < 100 && p.State != "dead")
            {
                DrawRect(new Rect2(at + new Vector2(-6, -9), new Vector2(12, 2)), new Color("#300"));
                DrawRect(new Rect2(at + new Vector2(-6, -9), new Vector2(12 * p.Health / 100f, 2)), new Color("#4caf50"));
            }
        }
        if (_model.Drawing is { } d) Path(d, Colors.Yellow);
    }
}
