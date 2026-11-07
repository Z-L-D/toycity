using Godot;
using ToyCity.ClientModel;
using ToyCity.ClientModel.Tools;
using P = ToyCity.Protocol.V0;

namespace ToyCity.Godot;

/// <summary>
/// The path designer (RC part 4), opened from the build menu's Your designs: a path's cross-section laid out left to
/// right as strips, as in Streetmix, drawn as you'd see it standing in the road. Click a strip to pick it, then widen,
/// move or take it out; add strips from the palette. The numbers the city will make of it show as it changes. Save
/// sends the design to the city, which puts it in the build menu.
/// </summary>
public partial class PathDesignerPanel : CanvasLayer
{
    readonly CityMirror _city;
    readonly Action<P.Action> _act;
    public readonly PathDesigner Model = new();
    Section? _section;
    LineEdit? _name;
    CheckButton? _oneWay;
    Label? _picked, _problem, _title;
    GridContainer? _stats;
    VBoxContainer? _designs;
    OptionButton? _startFrom;
    Button? _save;
    int _seen = -1, _seenProfiles = -1;
    /// <summary>The name of a new design just saved: once the city has it, carry on as that one.</summary>
    string? _awaiting;

    public PathDesignerPanel(CityMirror city, Action<P.Action> act) { _city = city; _act = act; }

    public void Toggle() { Visible = !Visible; _seenProfiles = -1; }

    public override void _Ready()
    {
        Layer = 6;
        Visible = false;
        var centre = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Stop };
        centre.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        AddChild(centre);
        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", UiTheme.Floating());
        centre.AddChild(panel);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 18);
        panel.AddChild(row);

        // Left: the city's designs, and where to start a new one.
        var left = new VBoxContainer { CustomMinimumSize = new Vector2(210, 0) };
        left.AddThemeConstantOverride("separation", 6);
        row.AddChild(left);
        left.AddChild(UiTheme.Heading("Your designs"));
        _designs = new VBoxContainer();
        left.AddChild(_designs);
        left.AddChild(new HSeparator());
        left.AddChild(UiTheme.Heading("Start a new one"));
        var blank = UiTheme.Small(new Button { Text = "A plain street", FocusMode = Control.FocusModeEnum.None });
        blank.Pressed += () => { Model.New(); _awaiting = null; };
        left.AddChild(blank);
        _startFrom = new OptionButton { FocusMode = Control.FocusModeEnum.None, FitToLongestItem = false, CustomMinimumSize = new Vector2(210, 0) };
        _startFrom.ItemSelected += k =>
        {
            if (k <= 0) return;
            var id = _startFrom.GetItemMetadata((int)k).AsString();
            if (_city.Profiles.FirstOrDefault(p => p.Id == id) is { } p) { Model.StartFrom(p); _awaiting = null; }
            _startFrom.Selected = 0;
        };
        left.AddChild(_startFrom);

        // Middle: the cross-section, the picked strip's buttons and the palette.
        var mid = new VBoxContainer();
        mid.AddThemeConstantOverride("separation", 8);
        row.AddChild(mid);
        var top = new HBoxContainer();
        top.AddThemeConstantOverride("separation", 10);
        mid.AddChild(top);
        _title = new Label { Text = "New design" };
        _title.AddThemeFontSizeOverride("font_size", 18);
        top.AddChild(_title);
        _name = new LineEdit { PlaceholderText = "Name it", CustomMinimumSize = new Vector2(260, 0), MaxLength = 40 };
        _name.TextChanged += t => Model.Name = t;
        top.AddChild(_name);
        _oneWay = new CheckButton { Text = "One way", FocusMode = Control.FocusModeEnum.None };
        _oneWay.Toggled += on => Model.SetOneWay(on);
        top.AddChild(_oneWay);

        _section = new Section(this) { CustomMinimumSize = new Vector2(720, 250) };
        mid.AddChild(_section);

        var tools = new HBoxContainer();
        tools.AddThemeConstantOverride("separation", 6);
        mid.AddChild(tools);
        _picked = new Label { CustomMinimumSize = new Vector2(240, 0) };
        tools.AddChild(_picked);
        foreach (var (text, tip, act) in new (string, string, Func<bool>)[]
        {
            ("◀", "Move it left", () => Model.Move(-1)), ("▶", "Move it right", () => Model.Move(1)),
            ("−", "Narrower", () => Model.Widen(-PathDesigner.Step)), ("+", "Wider", () => Model.Widen(PathDesigner.Step)),
            ("Take out", "Take this strip out", Model.Remove),
        })
        {
            var b = UiTheme.Small(new Button { Text = text, TooltipText = tip, FocusMode = Control.FocusModeEnum.None, CustomMinimumSize = new Vector2(34, 0) }, 8, 3, 14);
            b.Pressed += () => act();
            tools.AddChild(b);
        }

        mid.AddChild(UiTheme.Heading("Add a strip (after the one picked)"));
        var palette = new GridContainer { Columns = 6 };
        palette.AddThemeConstantOverride("h_separation", 6);
        palette.AddThemeConstantOverride("v_separation", 6);
        mid.AddChild(palette);
        foreach (var k in PathDesigner.Kinds)
        {
            var b = UiTheme.Small(new Button { Text = k.Name, TooltipText = $"{k.About} {k.MinM:0.##} to {k.MaxM:0.##} m.", FocusMode = Control.FocusModeEnum.None,
                CustomMinimumSize = new Vector2(112, 0), Icon = Swatch(new Color(k.Colour)) }, 6, 4, 12);
            string id = k.Id;
            b.Pressed += () => Model.Add(id);
            palette.AddChild(b);
        }

        // Right: what the city makes of it.
        var right = new VBoxContainer { CustomMinimumSize = new Vector2(250, 0) };
        right.AddThemeConstantOverride("separation", 8);
        row.AddChild(right);
        right.AddChild(UiTheme.Heading("What the city makes of it"));
        _stats = new GridContainer { Columns = 2 };
        _stats.AddThemeConstantOverride("h_separation", 14);
        _stats.AddThemeConstantOverride("v_separation", 4);
        right.AddChild(_stats);
        _problem = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(250, 0) };
        _problem.AddThemeColorOverride("font_color", UiTheme.Bad);
        right.AddChild(_problem);
        right.AddChild(new Control { SizeFlagsVertical = Control.SizeFlags.ExpandFill });
        var buttons = new HBoxContainer();
        buttons.AddThemeConstantOverride("separation", 8);
        right.AddChild(buttons);
        _save = new Button { Text = "Save design", FocusMode = Control.FocusModeEnum.None };
        _save.Pressed += Save;
        buttons.AddChild(_save);
        var close = new Button { Text = "Close", FocusMode = Control.FocusModeEnum.None };
        close.Pressed += () => Visible = false;
        buttons.AddChild(close);
    }

    static Texture2D Swatch(Color c)
    {
        var img = Image.CreateEmpty(14, 14, false, Image.Format.Rgba8);
        img.Fill(c);
        return ImageTexture.CreateFromImage(img);
    }

    public void Save()
    {
        if (Model.Problem != null) return;
        _act(Model.Save());
        if (Model.Id.Length == 0) _awaiting = Model.Name.Trim();
    }

    /// <summary>The design being laid out, for the gate: the strips and what they make.</summary>
    public string Describe() => $"{string.Join(" | ", Model.Strips.Select(s => $"{s.Type} {s.WidthM:0.##}"))} = {Model.Stats?.Kind ?? Model.Problem}";

    public override void _Process(double delta)
    {
        if (!Visible) return;
        if (_seenProfiles != _city.ProfilesVersion) { _seenProfiles = _city.ProfilesVersion; Profiles(); }
        if (_seen == Model.Version) return;
        _seen = Model.Version;
        Refresh();
    }

    void Profiles()
    {
        if (_awaiting != null && _city.Profiles.LastOrDefault(p => p.Custom && (p.Name == _awaiting || _awaiting.Length == 0)) is { } made)
        {
            Model.Edit(made);
            _awaiting = null;
        }
        foreach (var c in _designs!.GetChildren()) c.QueueFree();
        var mine = _city.Profiles.Where(p => p.Custom).ToList();
        if (mine.Count == 0)
            _designs.AddChild(new Label { Text = "None yet. Lay one out and save it.", Modulate = UiTheme.Muted, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(200, 0) });
        foreach (var p in mine)
        {
            var line = new HBoxContainer();
            var edit = UiTheme.Small(new Button { Text = p.Name, ClipText = true, FocusMode = Control.FocusModeEnum.None, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, TooltipText = "Redesign it" });
            var prof = p;
            edit.Pressed += () => { Model.Edit(prof); _awaiting = null; };
            line.AddChild(edit);
            var del = UiTheme.Small(new Button { Text = "✕", FocusMode = Control.FocusModeEnum.None, TooltipText = "Delete it (only when nothing's built with it)" });
            del.Pressed += () => { _act(PathDesigner.Delete(prof.Id)); if (Model.Id == prof.Id) Model.New(); };
            line.AddChild(del);
            _designs.AddChild(line);
        }
        _startFrom!.Clear();
        _startFrom.AddItem("From a path type…");
        foreach (var p in _city.Profiles.Where(PathDesigner.CanStartFrom))
        {
            _startFrom.AddItem(p.Custom ? $"{p.Name} (yours)" : p.Name);
            _startFrom.SetItemMetadata(_startFrom.ItemCount - 1, p.Id);
        }
        _startFrom.Selected = 0;
        _seen = -1;
    }

    void Refresh()
    {
        _title!.Text = Model.Id.Length > 0 ? "Redesign" : "New design";
        if (_name!.Text != Model.Name) _name.Text = Model.Name;
        _oneWay!.SetPressedNoSignal(Model.OneWay);
        _picked!.Text = Model.Selected >= 0 && PathDesigner.KindOf(Model.Strips[Model.Selected].Type) is { } k
            ? $"{k.Name}, {Model.Strips[Model.Selected].WidthM:0.##} m ({k.MinM:0.##} to {k.MaxM:0.##})"
            : "Click a strip to pick it";
        foreach (var c in _stats!.GetChildren()) c.QueueFree();
        var s = Model.Stats;
        _problem!.Text = Model.Problem ?? "";
        _save!.Disabled = s == null;
        _save.Text = Model.Id.Length > 0 ? "Save the redesign" : "Save design";
        void Row(string label, string value)
        {
            _stats.AddChild(new Label { Text = label, Modulate = UiTheme.Muted });
            _stats.AddChild(new Label { Text = value });
        }
        Row("Width", $"{Model.WidthM:0.##} m, {Model.Strips.Count} strips");
        if (s != null)
        {
            Row("It's a", s.Kind + (s.OneWay ? ", one way" : ""));
            Row("For", s.Users);
            Row("Speed", $"{s.SpeedKmh} km/h");
            Row("Carries", s.CapacityText);
            Row("Costs", $"${s.CostPerCell:N0} a cell");
            Row("Upkeep", $"${s.MaintenancePerCell:0.00} a cell a month");
            Row("Noise", $"{s.NoiseDb} dB");
            Row("Buildings", s.Frontage ? "can face it" : "can't face it");
            Row("Climbs", s.Stairs ? $"{s.MaxGradePct}%, then stairs" : $"up to {s.MaxGradePct}%");
            Row("Paved in", string.Join(", ", s.Surfaces));
        }
        _section!.QueueRedraw();
    }

    /// <summary>The cross-section, drawn as seen standing in the road: each strip a column as wide as it is, with what's on it.</summary>
    sealed partial class Section : Control
    {
        readonly PathDesignerPanel _p;
        public Section(PathDesignerPanel p) { _p = p; MouseFilter = MouseFilterEnum.Stop; }

        (float X0, float Scale) Layout()
        {
            double w = Math.Max(_p.Model.WidthM, 8);
            float scale = (float)Math.Min((Size.X - 20) / w, 60);
            return ((float)(Size.X - _p.Model.WidthM * scale) / 2, scale);
        }

        public override void _GuiInput(InputEvent e)
        {
            if (e is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } mb) return;
            var (x, scale) = Layout();
            for (int i = 0; i < _p.Model.Strips.Count; i++)
            {
                float w = (float)_p.Model.Strips[i].WidthM * scale;
                if (mb.Position.X >= x && mb.Position.X < x + w) { _p.Model.Select(i); AcceptEvent(); return; }
                x += w;
            }
            _p.Model.Select(-1);
        }

        public override void _Draw()
        {
            var font = ThemeDB.FallbackFont;
            float ground = Size.Y - 52;
            DrawRect(new Rect2(0, 0, Size.X, ground), new Color("#26324a"));
            DrawRect(new Rect2(0, ground, Size.X, Size.Y - ground), new Color("#3b3326"));
            var (x, scale) = Layout();
            var strips = _p.Model.Strips;
            for (int i = 0; i < strips.Count; i++)
            {
                var s = strips[i];
                var k = PathDesigner.KindOf(s.Type)!;
                float w = (float)s.WidthM * scale;
                bool raised = s.Type is "walk" or "planter" or "shared-path" or "median";
                float top = ground - (raised ? 8 : 0);
                DrawRect(new Rect2(x, top, w, Size.Y - 52 - top + 14), new Color(k.Colour));
                Figure(s.Type, x, w, top, scale, i);
                if (i > 0) DrawLine(new Vector2(x, top - 2), new Vector2(x, ground + 14), new Color(0, 0, 0, 0.35f), 1);
                // The lane markings: dashed white between lanes going the same way.
                if (i > 0 && s.Type is "car" or "turn" && strips[i - 1].Type is "car" or "turn")
                    for (float y = ground + 2; y < ground + 14; y += 6) DrawLine(new Vector2(x, y), new Vector2(x, y + 3), Colors.White, 2);
                string name = k.Name.Length * 6.5f > w ? k.Name.Split(' ')[0] : k.Name;
                DrawString(font, new Vector2(x, ground + 30), name, HorizontalAlignment.Center, w, 11, UiTheme.Soft);
                DrawString(font, new Vector2(x, ground + 46), $"{s.WidthM:0.##} m", HorizontalAlignment.Center, w, 11, UiTheme.Muted);
                if (i == _p.Model.Selected) DrawRect(new Rect2(x + 1, 4, w - 2, ground + 46), UiTheme.Accent, false, 2);
                x += w;
            }
            if (strips.Count == 0) DrawString(font, new Vector2(0, ground / 2), "Add a strip from the palette below.", HorizontalAlignment.Center, Size.X, 14, UiTheme.Muted);
        }

        /// <summary>Whether a lane's traffic comes towards you: the lanes left of the road's middle, on a two-way road.</summary>
        bool Oncoming(int i)
        {
            if (_p.Model.OneWay) return false;
            var lanes = _p.Model.Strips.Select((s, k) => (s, k)).Where(t => t.s.Type is "car" or "car-tram" or "turn").Select(t => t.k).ToList();
            return lanes.IndexOf(i) < lanes.Count / 2;
        }

        /// <summary>What stands on a strip: a person, a tree, a bike, a car, a tram.</summary>
        void Figure(string type, float x, float w, float top, float scale, int i)
        {
            float cx = x + w / 2, m = scale; // m: pixels a metre
            var dark = new Color("#1c2230");
            void Person(float px, Color c)
            {
                DrawCircle(new Vector2(px, top - 1.62f * m), 0.13f * m, c);
                DrawRect(new Rect2(px - 0.2f * m, top - 1.45f * m, 0.4f * m, 0.75f * m), c);
                DrawRect(new Rect2(px - 0.17f * m, top - 0.7f * m, 0.14f * m, 0.7f * m), c);
                DrawRect(new Rect2(px + 0.03f * m, top - 0.7f * m, 0.14f * m, 0.7f * m), c);
            }
            void Car(float px, Color body, bool parked = false, bool oncoming = false)
            {
                float cw = 1.8f * m, h = 1.45f * m;
                DrawRect(new Rect2(px - cw / 2, top - h * 0.62f, cw, h * 0.45f), body);
                DrawRect(new Rect2(px - cw * 0.38f, top - h, cw * 0.76f, h * 0.4f), body.Darkened(0.15f));
                DrawRect(new Rect2(px - cw * 0.32f, top - h * 0.93f, cw * 0.64f, h * 0.28f), new Color("#9fc3e0"));
                DrawRect(new Rect2(px - cw / 2 + 0.05f * m, top - h * 0.2f, 0.3f * m, h * 0.2f), dark);
                DrawRect(new Rect2(px + cw / 2 - 0.35f * m, top - h * 0.2f, 0.3f * m, h * 0.2f), dark);
                // Tail lights going away, headlights coming towards you.
                var lamp = oncoming ? new Color("#fff3c4") : new Color("#ff5a4a");
                if (!parked) { DrawRect(new Rect2(px - cw / 2 + 0.1f * m, top - h * 0.5f, 0.25f * m, 0.12f * m), lamp); DrawRect(new Rect2(px + cw / 2 - 0.35f * m, top - h * 0.5f, 0.25f * m, 0.12f * m), lamp); }
            }
            void Tree(float px)
            {
                DrawRect(new Rect2(px - 0.12f * m, top - 2.2f * m, 0.24f * m, 2.2f * m), new Color("#6b4a2b"));
                DrawCircle(new Vector2(px, top - 3.2f * m), 1.2f * m, new Color("#3f7d34"));
                DrawCircle(new Vector2(px - 0.6f * m, top - 2.7f * m), 0.8f * m, new Color("#4a8c3b"));
                DrawCircle(new Vector2(px + 0.6f * m, top - 2.8f * m), 0.8f * m, new Color("#36702d"));
            }
            void Bike(float px)
            {
                var c = new Color("#e6e9ef");
                DrawArc(new Vector2(px, top - 0.35f * m), 0.33f * m, 0, Mathf.Tau, 16, c, 2);
                DrawRect(new Rect2(px - 0.04f * m, top - 1.1f * m, 0.08f * m, 0.75f * m), c);
                DrawLine(new Vector2(px - 0.3f * m, top - 1.1f * m), new Vector2(px + 0.3f * m, top - 1.1f * m), c, 2);
                DrawCircle(new Vector2(px, top - 1.75f * m), 0.13f * m, new Color("#f3c94a"));
                DrawRect(new Rect2(px - 0.18f * m, top - 1.6f * m, 0.36f * m, 0.55f * m), new Color("#f3c94a"));
            }
            void Tram(float px)
            {
                float tw = 2.4f * m, h = 3.4f * m;
                DrawRect(new Rect2(px - tw / 2, top - h, tw, h - 0.3f * m), new Color("#d9483b"));
                DrawRect(new Rect2(px - tw * 0.4f, top - h * 0.82f, tw * 0.8f, h * 0.3f), new Color("#9fc3e0"));
                DrawRect(new Rect2(px - tw / 2, top - 0.6f * m, tw, 0.18f * m), new Color("#f2efe6"));
                DrawLine(new Vector2(px, top - h), new Vector2(px, top - h - 1.2f * m), dark, 2);
                DrawLine(new Vector2(px - 0.72f * m, top), new Vector2(px - 0.72f * m, top - 0.12f * m), dark, 3);
                DrawLine(new Vector2(px + 0.72f * m, top), new Vector2(px + 0.72f * m, top - 0.12f * m), dark, 3);
            }
            Color[] bodies = { new("#3e6fd1"), new("#c9d2e3"), new("#b8322a"), new("#2f3a4f"), new("#e3b341") };
            switch (type)
            {
                case "walk": Person(cx - w * 0.15f, new Color("#e6e9ef")); if (w > 2.5f * m) Person(cx + w * 0.22f, new Color("#f3c94a")); break;
                case "planter": Tree(cx); break;
                case "bike": Bike(cx); break;
                case "shared-path": Person(cx - w * 0.22f, new Color("#e6e9ef")); Bike(cx + w * 0.2f); break;
                case "parking": Car(cx, bodies[(i + 3) % bodies.Length], parked: true); break;
                case "car": Car(cx, bodies[i % bodies.Length], oncoming: Oncoming(i)); break;
                case "turn":
                    var a = new Color("#f2efe6");
                    DrawLine(new Vector2(cx, top - 0.2f * m), new Vector2(cx, top - 1.2f * m), a, 3);
                    DrawLine(new Vector2(cx, top - 1.2f * m), new Vector2(cx - 0.6f * m, top - 1.2f * m), a, 3);
                    DrawLine(new Vector2(cx - 0.6f * m, top - 1.2f * m), new Vector2(cx - 0.35f * m, top - 1.45f * m), a, 3);
                    DrawLine(new Vector2(cx - 0.6f * m, top - 1.2f * m), new Vector2(cx - 0.35f * m, top - 0.95f * m), a, 3);
                    break;
                case "car-tram": Tram(cx); break;
                case "tram": Tram(cx); break;
                case "median": if (w > 1.6f * m) Tree(cx); else DrawCircle(new Vector2(cx, top - 0.4f * m), 0.45f * m, new Color("#4a8c3b")); break;
                case "shoulder":
                    for (float y = 0; y < 3; y++) DrawLine(new Vector2(x + 3, top + 2 + y * 4), new Vector2(x + w - 3, top + 2 + y * 4), new Color("#f3c94a", 0.6f), 1);
                    break;
                case "shared": Car(cx + w * 0.15f, new Color("#7a8a4f")); Person(cx - w * 0.3f, new Color("#e6e9ef")); break;
            }
        }
    }
}
