using Godot;
using ToyCity.ClientModel;
using ToyCity.ClientModel.Graphics;
using P = ToyCity.Protocol.V0;

namespace ToyCity.Godot;

/// <summary>
/// The weather radar and station (Beta 4.1, part 3): the city's radar in the style of the National Weather Service's
/// NEXRAD pages, base reflectivity or base velocity at one of the four lowest tilts, still or looping over the last
/// hour, with the station's readings beside it. A storm is set going by dragging on the scope, from where it starts
/// to where it'll be an hour later.
/// </summary>
public partial class RadarPanel : CanvasLayer
{
    readonly CityMirror _city;
    readonly Action<P.Action> _act;
    RadarScope? _scope;
    OptionButton? _product, _tilt, _strength;
    CheckButton? _loop, _draw;
    Label? _title;
    readonly Dictionary<string, Label> _readings = new();
    HBoxContainer? _legend;

    public RadarPanel(CityMirror city, Action<P.Action> act) { _city = city; _act = act; }

    public void Toggle() { Visible = !Visible; if (Visible) _scope?.Refresh(true); }

    public override void _Ready()
    {
        Layer = 6;
        Visible = false;
        var centre = new CenterContainer();
        centre.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        centre.MouseFilter = Control.MouseFilterEnum.Stop;
        centre.GuiInput += e =>
        {
            if (e is not InputEventMouseButton { Pressed: true } mb || mb.ButtonIndex is not (MouseButton.Left or MouseButton.Right)) return;
            Visible = false;
            centre.AcceptEvent();
        };
        AddChild(centre);
        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", UiTheme.Floating());
        centre.AddChild(panel);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 18);
        panel.AddChild(row);

        var left = new VBoxContainer();
        left.AddThemeConstantOverride("separation", 6);
        row.AddChild(left);
        _title = new Label { Text = "Base reflectivity · 0.5°" };
        _title.AddThemeFontSizeOverride("font_size", 18);
        left.AddChild(_title);
        _scope = new RadarScope(this) { CustomMinimumSize = new Vector2(520, 520) };
        left.AddChild(_scope);
        _legend = new HBoxContainer();
        _legend.AddThemeConstantOverride("separation", 0);
        left.AddChild(_legend);

        var right = new VBoxContainer { CustomMinimumSize = new Vector2(250, 0) };
        right.AddThemeConstantOverride("separation", 8);
        row.AddChild(right);
        right.AddChild(UiTheme.Heading("Weather station"));
        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 14);
        right.AddChild(grid);
        foreach (var (label, _) in Radar.Station(new P.Sky()))
        {
            grid.AddChild(new Label { Text = label, Modulate = UiTheme.Muted });
            var v = new Label();
            _readings[label] = v;
            grid.AddChild(v);
        }
        right.AddChild(new HSeparator());
        right.AddChild(UiTheme.Heading("Radar"));
        _product = new OptionButton { FocusMode = Control.FocusModeEnum.None };
        _product.AddItem("Base reflectivity");
        _product.AddItem("Base velocity");
        _product.ItemSelected += _ => Changed();
        right.AddChild(_product);
        _tilt = new OptionButton { FocusMode = Control.FocusModeEnum.None };
        for (int k = 0; k < Radar.Tilts.Length; k++) _tilt.AddItem($"Tilt {k + 1} · {Radar.Tilts[k]:0.0}°");
        _tilt.ItemSelected += _ => Changed();
        right.AddChild(_tilt);
        _loop = new CheckButton { Text = "Loop the last hour", FocusMode = Control.FocusModeEnum.None };
        _loop.Toggled += _ => Changed();
        right.AddChild(_loop);
        right.AddChild(new HSeparator());
        right.AddChild(UiTheme.Heading("Set a storm going"));
        _draw = new CheckButton { Text = "Draw on the radar", FocusMode = Control.FocusModeEnum.None };
        right.AddChild(_draw);
        _strength = new OptionButton { FocusMode = Control.FocusModeEnum.None };
        foreach (var s in new[] { "Light", "Heavy", "Severe" }) _strength.AddItem(s);
        _strength.Selected = 1;
        right.AddChild(_strength);
        right.AddChild(new Label
        {
            Text = "Drag from where it starts to where it'll be in an hour.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(240, 0), Modulate = UiTheme.Muted,
        });
        var close = new Button { Text = "Close", FocusMode = Control.FocusModeEnum.None };
        close.Pressed += () => Visible = false;
        right.AddChild(close);
        Changed();
    }

    public RadarProduct Product => (RadarProduct)(_product?.Selected ?? 0);
    public double Tilt => Radar.Tilts[Math.Max(0, _tilt?.Selected ?? 0)];
    public bool Looping => _loop?.ButtonPressed ?? false;
    public bool Drawing => _draw?.ButtonPressed ?? false;
    public P.Sky Sky => _city.Stats.Sky ?? new P.Sky();

    void Changed()
    {
        if (_title != null) _title.Text = $"{(Product == RadarProduct.Reflectivity ? "Base reflectivity" : "Base velocity")} · {Tilt:0.0}°";
        if (_legend != null)
        {
            foreach (var c in _legend.GetChildren()) c.QueueFree();
            _legend.AddChild(new Label { Text = Product == RadarProduct.Reflectivity ? "dBZ " : "kt ", Modulate = UiTheme.Muted });
            foreach (var (col, label) in Radar.Legend(Product))
            {
                var swatch = new PanelContainer { CustomMinimumSize = new Vector2(30, 18) };
                swatch.AddThemeStyleboxOverride("panel", UiTheme.Box(Colour(col), null, 0, 0, 0));
                var l = new Label { Text = label, HorizontalAlignment = HorizontalAlignment.Center };
                l.AddThemeFontSizeOverride("font_size", 10);
                var bg = Colour(col);
                l.AddThemeColorOverride("font_color", bg.Luminance < 0.45f ? new Color(1, 1, 1) : new Color(0, 0, 0));
                swatch.AddChild(l);
                _legend.AddChild(swatch);
            }
        }
        _scope?.Refresh(true);
    }

    public static Color Colour(uint c) => new(((c >> 16) & 255) / 255f, ((c >> 8) & 255) / 255f, (c & 255) / 255f);

    public override void _Process(double delta)
    {
        if (!Visible) return;
        var sky = Sky;
        foreach (var (label, value) in Radar.Station(sky)) if (_readings.TryGetValue(label, out var l)) l.Text = value;
        _scope?.Refresh(false);
    }

    /// <summary>A storm drawn on the scope, from and to in km from the radar.</summary>
    public void Place(Vector2 from, Vector2 to)
    {
        var (x, y, heading, speed) = Radar.Drawn(from.X, from.Y, to.X, to.Y, _city.Width, _city.Height);
        _act(new P.Action { PlaceStorm = new P.PlaceStorm { X = x, Y = y, HeadingDeg = heading, SpeedKmh = speed, Strength = (_strength?.Selected ?? 1) + 1 } });
        _draw!.ButtonPressed = false;
    }

    /// <summary>The scope itself: the painted radar over its rings, the city in the middle, the clock in a corner.</summary>
    public partial class RadarScope : Control
    {
        const int Px = 400;
        readonly RadarPanel _panel;
        readonly byte[] _rgba = new byte[Px * Px * 4];
        readonly List<ImageTexture> _frames = new();
        long _skyMinute = long.MinValue;
        int _skyCells = -1;
        double _clock;
        int _frame;
        Vector2? _dragFrom, _dragTo;

        public RadarScope(RadarPanel panel) { _panel = panel; MouseFilter = MouseFilterEnum.Stop; }

        /// <summary>Repaints when the sky has moved on, or at once when asked.</summary>
        public void Refresh(bool force)
        {
            var sky = _panel.Sky;
            if (!force && sky.ClockMinute == _skyMinute && sky.Cells.Count == _skyCells)
            {
                if (_panel.Looping && _frames.Count > 1)
                {
                    _clock += GetProcessDeltaTime();
                    int f = (int)(_clock / 0.45) % (_frames.Count + 2);
                    if (f != _frame) { _frame = f; QueueRedraw(); }
                }
                return;
            }
            _skyMinute = sky.ClockMinute;
            _skyCells = sky.Cells.Count;
            _frames.Clear();
            int n = _panel.Looping ? Radar.LoopFrames : 1;
            for (int k = n - 1; k >= 0; k--)
            {
                Radar.Paint(Radar.Frame(sky, -10 * k), _panel.Product, _panel.Tilt, Px, _rgba);
                _frames.Add(ImageTexture.CreateFromImage(Image.CreateFromData(Px, Px, false, Image.Format.Rgba8, _rgba)));
            }
            _frame = _frames.Count - 1;
            QueueRedraw();
        }

        Vector2 Km(Vector2 local) => (local / Size - new Vector2(0.5f, 0.5f)) * (float)(2 * Radar.RangeKm);
        Vector2 Local(Vector2 km) => (km / (float)(2 * Radar.RangeKm) + new Vector2(0.5f, 0.5f)) * Size;

        public override void _GuiInput(InputEvent e)
        {
            if (!_panel.Drawing) return;
            if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left } mb)
            {
                if (mb.Pressed) _dragFrom = _dragTo = Km(mb.Position);
                else if (_dragFrom is { } a && _dragTo is { } b)
                {
                    if (a.DistanceTo(b) > 1) _panel.Place(a, b);
                    _dragFrom = _dragTo = null;
                }
                QueueRedraw();
                AcceptEvent();
            }
            else if (e is InputEventMouseMotion mm && _dragFrom != null) { _dragTo = Km(mm.Position); QueueRedraw(); AcceptEvent(); }
        }

        public override void _Draw()
        {
            var size = Size;
            Vector2 mid = size / 2;
            float r = Math.Min(size.X, size.Y) / 2;
            DrawRect(new Rect2(Vector2.Zero, size), new Color("#0b0f16"));
            DrawCircle(mid, r, new Color("#1a2017"));
            // Ten-kilometre grid and range rings.
            var line = new Color(1, 1, 1, 0.08f);
            for (int k = -3; k <= 3; k++)
            {
                float o = mid.X + k * r / 4;
                DrawLine(new Vector2(o, 0), new Vector2(o, size.Y), line);
                DrawLine(new Vector2(0, mid.Y + k * r / 4), new Vector2(size.X, mid.Y + k * r / 4), line);
            }
            int f = Math.Min(_frame, _frames.Count - 1);
            if (f >= 0) DrawTextureRect(_frames[f], new Rect2(Vector2.Zero, size), false);
            var ring = new Color(1, 1, 1, 0.3f);
            var font = ThemeDB.FallbackFont;
            for (int k = 1; k <= 4; k++)
            {
                DrawArc(mid, r * k / 4, 0, Mathf.Tau, 96, ring, 1, true);
                DrawString(font, mid + new Vector2(4, -r * k / 4 + 12), $"{k * 10} km", HorizontalAlignment.Left, -1, 10, ring);
            }
            // The city's ground and the radar in its middle.
            var city = _panel._city;
            var half = new Vector2(city.Width, city.Height) * (float)(Radar.CellKm / (2 * Radar.RangeKm)) * size / 2;
            DrawRect(new Rect2(mid - half, half * 2), new Color(1, 0.8f, 0.3f, 0.9f), false, 1.5f);
            DrawCircle(mid, 2.5f, new Color(1, 1, 1));
            // The time of the frame, as the NWS stamps it.
            long minute = _panel.Sky.ClockMinute - 10 * (_frames.Count - 1 - Math.Max(0, f));
            string stamp = $"{minute % 1440 / 60:00}:{minute % 60:00}" + (_frames.Count > 1 ? $"  frame {Math.Max(0, f) + 1}/{_frames.Count}" : "");
            DrawString(font, new Vector2(8, size.Y - 10), stamp, HorizontalAlignment.Left, -1, 14, new Color(1, 1, 1, 0.9f));
            if (_panel.Sky.Cells.Count == 0) DrawString(font, new Vector2(8, 20), "Clear: no echoes", HorizontalAlignment.Left, -1, 14, new Color(1, 1, 1, 0.6f));
            if (_dragFrom is { } a && _dragTo is { } b)
            {
                Vector2 p = Local(a), q = Local(b);
                var gold = UiTheme.Accent;
                DrawCircle(p, 6, new Color(gold, 0.5f));
                DrawLine(p, q, gold, 2.5f, true);
                Vector2 dir = (q - p).Normalized();
                if (dir != Vector2.Zero)
                {
                    DrawLine(q, q - dir.Rotated(0.45f) * 12, gold, 2.5f, true);
                    DrawLine(q, q - dir.Rotated(-0.45f) * 12, gold, 2.5f, true);
                }
                DrawString(font, q + new Vector2(8, -6), $"{Math.Round(a.DistanceTo(b))} km/h", HorizontalAlignment.Left, -1, 13, gold);
            }
        }
    }
}
