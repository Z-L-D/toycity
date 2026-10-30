using Godot;
using ToyCity.ClientModel.Graphics;

namespace ToyCity.Godot;

/// <summary>
/// The graphics options over the map: a preset (Low to Ultra, or Custom once anything is changed by hand) and each
/// option on its own, with a line of help for the one under the mouse, and the frame rate now so a change can be
/// judged as it's made. Every change applies at once and is saved by Main.
/// </summary>
public partial class GraphicsPanel : CanvasLayer
{
    public GraphicsSettings Settings { get; private set; }
    public event Action<GraphicsSettings>? Changed;
    OptionButton? _preset;
    readonly List<OptionButton> _rows = new();
    Label? _help, _fps;
    string _renderer = "";

    public GraphicsPanel(GraphicsSettings settings) => Settings = settings;

    public override void _Ready()
    {
        Layer = 6;
        Visible = false;
        _renderer = $"{RenderingServer.GetVideoAdapterName()} · {RenderingServer.GetCurrentRenderingMethod()}";
        var centre = new CenterContainer();
        centre.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        // A click outside the panel closes it.
        centre.MouseFilter = Control.MouseFilterEnum.Stop;
        centre.GuiInput += e =>
        {
            if (e is not InputEventMouseButton { Pressed: true } mb || mb.ButtonIndex is not (MouseButton.Left or MouseButton.Right)) return;
            Visible = false;
            centre.AcceptEvent();
        };
        AddChild(centre);
        var panel = new PanelContainer { CustomMinimumSize = new Vector2(520, 0) };
        panel.AddThemeStyleboxOverride("panel", UiTheme.Floating());
        centre.AddChild(panel);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 8);
        panel.AddChild(box);
        var title = new Label { Text = "Graphics" };
        title.AddThemeFontSizeOverride("font_size", 20);
        box.AddChild(title);
        box.AddChild(new Label { Text = _renderer, Modulate = new Color(1, 1, 1, 0.6f) });

        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 16);
        box.AddChild(grid);
        grid.AddChild(new Label { Text = "Preset" });
        _preset = new OptionButton { FocusMode = Control.FocusModeEnum.None, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        foreach (var (name, _) in GraphicsSettings.Presets) _preset.AddItem(name);
        _preset.AddItem("Custom");
        _preset.SetItemDisabled(GraphicsSettings.Presets.Length, true);
        _preset.ItemSelected += i => { if (i < GraphicsSettings.Presets.Length) Set(Settings.WithPreset(GraphicsSettings.Presets[i].Settings)); };
        _preset.MouseEntered += () => _help!.Text = "Low suits integrated graphics; Ultra a fast graphics card. Changing any option below makes it Custom.";
        grid.AddChild(_preset);
        foreach (var o in GraphicsSettings.Options)
        {
            grid.AddChild(new Label { Text = o.Label });
            var choice = new OptionButton { FocusMode = Control.FocusModeEnum.None, TooltipText = o.Help, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            foreach (var c in o.Choices) choice.AddItem(c);
            choice.ItemSelected += i => Set(o.Set(Settings, (int)i));
            choice.MouseEntered += () => _help!.Text = o.Help;
            grid.AddChild(choice);
            _rows.Add(choice);
        }
        _help = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(0, 44), Modulate = new Color(1, 1, 1, 0.8f) };
        box.AddChild(_help);
        var foot = new HBoxContainer();
        box.AddChild(foot);
        foot.AddChild(_fps = new Label { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        var close = new Button { Text = "Close", FocusMode = Control.FocusModeEnum.None };
        close.Pressed += () => Visible = false;
        foot.AddChild(close);
        Show(Settings);
    }

    public void Toggle() => Visible = !Visible;

    void Set(GraphicsSettings s)
    {
        if (s == Settings) return;
        Settings = s;
        Show(s);
        Changed?.Invoke(s);
    }

    void Show(GraphicsSettings s)
    {
        string preset = s.PresetName;
        int p = Array.FindIndex(GraphicsSettings.Presets, x => x.Name == preset);
        _preset!.Select(p >= 0 ? p : GraphicsSettings.Presets.Length);
        for (int k = 0; k < _rows.Count; k++) _rows[k].Select(GraphicsSettings.Options[k].Get(s));
    }

    public override void _Process(double delta)
    {
        if (Visible) _fps!.Text = $"{Engine.GetFramesPerSecond():0} fps";
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (Visible && e is InputEventKey { Pressed: true, Keycode: Key.Escape })
        {
            Visible = false;
            GetViewport().SetInputAsHandled();
        }
    }
}
