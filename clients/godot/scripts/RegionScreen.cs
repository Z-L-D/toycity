using Godot;
using ToyCity.ClientModel.Panels;

namespace ToyCity.Godot;

/// <summary>
/// The region map, over everything while it's open: the region's tiles as pictures on the left, the picked tile's
/// details and what can be done there on the right (from <see cref="RegionModel"/>). Click a tile to pick it,
/// double-click a city to open it.
/// </summary>
public partial class RegionScreen : CanvasLayer
{
    readonly RegionModel _model;
    Control? _root;
    Label? _title;
    GridContainer? _grid;
    RichTextLabel? _side;
    int _mapShown = -1, _sideShown = -1;
    readonly Func<int> _version;
    /// <summary>The tile buttons, by row * cols + col, for marking the picked one.</summary>
    readonly List<(Button Button, int Col, int Row)> _tiles = new();

    public RegionScreen(RegionModel model, Func<int> version)
    {
        _model = model;
        _version = version;
    }

    public override void _Ready()
    {
        // Above the city's screens and dialogs: the whole interface changes.
        Layer = 6;
        _root = new Panel { Visible = false };
        _root.AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.Bg, null, 0, 0, 0));
        _root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        AddChild(_root);
        var col = new VBoxContainer();
        col.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        col.AddThemeConstantOverride("separation", 0);
        _root.AddChild(col);

        var bar = new PanelContainer { CustomMinimumSize = new Vector2(0, ToolController.TopHeight) };
        bar.AddThemeStyleboxOverride("panel", UiTheme.Docked(Side.Bottom, 12));
        col.AddChild(bar);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 14);
        bar.AddChild(row);
        _title = new Label { VerticalAlignment = VerticalAlignment.Center, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        _title.AddThemeFontSizeOverride("font_size", 15);
        _title.AddThemeColorOverride("font_color", UiTheme.Accent);
        row.AddChild(_title);
        row.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        var back = new Button { Text = "🏙 Back to the city", FocusMode = Control.FocusModeEnum.None, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        back.Pressed += () => _model.Click("rg-close");
        row.AddChild(back);

        var body = new MarginContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        foreach (var side in new[] { "left", "right", "top", "bottom" }) body.AddThemeConstantOverride($"margin_{side}", 16);
        col.AddChild(body);
        var split = new HBoxContainer();
        split.AddThemeConstantOverride("separation", 16);
        body.AddChild(split);
        var mapBox = new AspectRatioContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            AlignmentVertical = AspectRatioContainer.AlignmentMode.Begin };
        split.AddChild(mapBox);
        _grid = new GridContainer();
        _grid.AddThemeConstantOverride("h_separation", 3);
        _grid.AddThemeConstantOverride("v_separation", 3);
        mapBox.AddChild(_grid);
        var sidePanel = new PanelContainer { CustomMinimumSize = new Vector2(320, 0) };
        sidePanel.AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.Panel, UiTheme.Line, 6, 12, 12));
        split.AddChild(sidePanel);
        _side = new RichTextLabel { BbcodeEnabled = true, SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _side.MetaClicked += meta =>
        {
            if (meta.AsString() == "rg-found" && _name != null) _model.NewName = _name.Text;
            _model.Click(meta.AsString());
        };
        var sideCol = new VBoxContainer();
        sidePanel.AddChild(sideCol);
        sideCol.AddChild(_side);
        _name = new LineEdit { Text = _model.NewName, MaxLength = 40, PlaceholderText = "City name", Visible = false };
        _name.TextChanged += t => _model.NewName = t;
        sideCol.AddChild(_name);
        sideCol.MoveChild(_name, 0);
    }

    LineEdit? _name;

    public override void _Process(double delta)
    {
        if (_root == null) return;
        _root.Visible = _model.Active;
        if (!_model.Active) return;
        if (_model.MapVersion != _mapShown) BuildTiles();
        if (_version() != _sideShown)
        {
            _sideShown = _version();
            _title!.Text = "🗺 " + _model.Title();
            _side!.Text = _model.Render();
            _name!.Visible = _model.PickedTile is { Founded: false, Wild: false };
            foreach (var (b, c, r) in _tiles) b.ButtonPressed = _model.Picked == (c, r);
        }
    }

    void BuildTiles()
    {
        _mapShown = _model.MapVersion;
        if (_model.State is not { } s) return;
        foreach (var n in _grid!.GetChildren()) n.QueueFree();
        _tiles.Clear();
        // A growing region shows the tiles in it and a ring of wild land around them, where it can grow (G4).
        var (c0, r0, c1, r1) = _model.Shown;
        _grid.Columns = c1 - c0 + 1;
        ((AspectRatioContainer)_grid.GetParent()).Ratio = (c1 - c0 + 1) / (float)(r1 - r0 + 1);
        int size = RegionModel.PictureSize;
        foreach (var t in s.Tiles.Where(t => t.Col >= c0 && t.Col <= c1 && t.Row >= r0 && t.Row <= r1).OrderBy(t => t.Row).ThenBy(t => t.Col))
        {
            int c = t.Col, r = t.Row;
            if (t.Wild)
            {
                bool can = _model.CanGrow(t);
                var w = new Button
                {
                    ToggleMode = true, FocusMode = Control.FocusModeEnum.None, Text = can ? "＋" : "",
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill,
                    TooltipText = can ? "Wild land: add it to the region" : "Wild land",
                };
                w.AddThemeFontSizeOverride("font_size", 40);
                w.AddThemeColorOverride("font_color", UiTheme.Accent);
                w.AddThemeStyleboxOverride("normal", UiTheme.Box(new Color(0.06f, 0.08f, 0.11f), can ? UiTheme.Accent.Darkened(0.4f) : UiTheme.Line, 2, 0, 0));
                w.Pressed += () => _model.Click($"rg-pick:{c}:{r}");
                _grid.AddChild(w);
                _tiles.Add((w, c, r));
                continue;
            }
            var img = Image.CreateFromData(size, size, false, Image.Format.Rgb8, t.Picture.ToByteArray());
            var b = new Button
            {
                ToggleMode = true, FocusMode = Control.FocusModeEnum.None, ExpandIcon = true, Icon = ImageTexture.CreateFromImage(img),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill,
                TooltipText = (t.Founded ? $"{t.Name} · {t.Mayor} · {t.Population:N0} people" : t.Unsurveyed ? "Open land (a rough look; its ground is made when a city is founded)" : "Open land") + (t.Weather.Length > 0 ? $" · {t.Weather}" : ""),
                TextureFilter = CanvasItem.TextureFilterEnum.Nearest,
            };
            if (!t.Founded) b.Modulate = new Color(0.8f, 0.8f, 0.8f);
            // A city of several tiles: each tile shows its part, and the name sits on the top-left one.
            var (fc, fr, _, _) = RegionModel.Footprint(t);
            bool anchor = fc == c && fr == r;
            bool annex = !t.Founded && _model.AnnexAt(t) is { Why.Length: 0 };
            if (annex) b.TooltipText += " · your city can annex it";
            var label = new Label { Text = (t.Weather.Length > 0 && anchor ? t.Weather.Split(' ')[0] + " " : "") + (t.Founded && anchor ? (t.Computer.Length > 0 ? "🤖 " : "") + t.Name + (t.Players.Count > 0 ? $"  👤{t.Players.Count}" : "") : annex ? "⊕ annex" : ""), Position = new Vector2(6, 4) };
            if (annex) label.AddThemeColorOverride("font_color", UiTheme.Accent);
            label.AddThemeColorOverride("font_outline_color", Colors.Black);
            label.AddThemeConstantOverride("outline_size", 4);
            if (fc == s.HereCol && fr == s.HereRow) label.AddThemeColorOverride("font_color", UiTheme.Accent);
            b.AddChild(label);
            b.Pressed += () => _model.Click($"rg-pick:{c}:{r}");
            b.GuiInput += e =>
            {
                if (e is InputEventMouseButton { DoubleClick: true, ButtonIndex: MouseButton.Left }) { _model.Click($"rg-pick:{c}:{r}"); _model.Click("rg-go"); }
            };
            _grid.AddChild(b);
            _tiles.Add((b, fc, fr));
        }
        _sideShown = -1;
    }

    public override void _UnhandledKeyInput(InputEvent e)
    {
        if (_model.Active && e is InputEventKey { Pressed: true, Keycode: Key.Escape })
        {
            _model.Click("rg-close");
            GetViewport().SetInputAsHandled();
        }
    }
}
