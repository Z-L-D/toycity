using Godot;

namespace ToyCity.Godot;

/// <summary>
/// The look of every window and control, matched to the web client's style sheet: dark slate panels, rounded
/// buttons with a thin border that turns gold when pressed, the same colours and text sizes. Set once on the root
/// window, so every control picks it up without its own overrides.
/// </summary>
public static class UiTheme
{
    public static readonly Color Bg = new("#12161f"), Panel = new("#1c2230"), Panel2 = new("#252d3e"), Line = new("#333d52"),
        Text = new("#e6e9ef"), Muted = new("#8f99ad"), Accent = new("#ffcc4d"), Good = new("#4cc27a"), Bad = new("#ff6b5e"),
        Warn = new("#ffb547"), HoverLine = new("#56627c"), Active = new("#3a4660"), Chip = new(10 / 255f, 13 / 255f, 20 / 255f, 0.82f);

    public const int FontSize = 14, SmallSize = 12;

    public static StyleBoxFlat Box(Color bg, Color? border = null, int radius = 6, int padX = 10, int padY = 5, int borderWidth = 1)
    {
        var s = new StyleBoxFlat { BgColor = bg, ContentMarginLeft = padX, ContentMarginRight = padX, ContentMarginTop = padY, ContentMarginBottom = padY };
        s.SetCornerRadiusAll(radius);
        if (border is { } b) { s.BorderColor = b; s.SetBorderWidthAll(borderWidth); }
        s.AntiAliasing = true;
        return s;
    }

    /// <summary>A docked panel: the top bar, the tool column, the side panel. Only the edge facing the map has a border.</summary>
    public static StyleBoxFlat Docked(Side edge, int pad = 8)
    {
        var s = Box(Panel, null, 0, pad, pad);
        s.BorderColor = Line;
        switch (edge)
        {
            case Side.Bottom: s.BorderWidthBottom = 1; break;
            case Side.Right: s.BorderWidthRight = 1; break;
            case Side.Left: s.BorderWidthLeft = 1; break;
            default: s.BorderWidthTop = 1; break;
        }
        return s;
    }

    /// <summary>A floating box over the map: dialogs.</summary>
    public static StyleBoxFlat Floating() => WithShadow(Box(Panel, Line, 10, 18, 16));

    /// <summary>A small dark tag over the map: key hints, toasts, the cost under the cursor, banners.</summary>
    public static StyleBoxFlat ChipBox(Color? border = null) => Box(Chip, border, 5, 8, 3);

    static StyleBoxFlat WithShadow(StyleBoxFlat s)
    {
        s.ShadowColor = new Color(0, 0, 0, 0.45f);
        s.ShadowSize = 16;
        return s;
    }

    /// <summary>
    /// Puts the look into Godot's default theme, which every control falls back to. A theme set on the root window
    /// would stop at each CanvasLayer, and the panels, tools and dialogs each sit on their own.
    /// </summary>
    public static void Apply()
    {
        var t = Build();
        var d = ThemeDB.GetDefaultTheme();
        d.MergeWith(t);
        d.DefaultFont = t.DefaultFont;
        d.DefaultFontSize = FontSize;
        ThemeDB.FallbackFont = t.DefaultFont;
        ThemeDB.FallbackFontSize = FontSize;
    }

    public static Theme Build()
    {
        var t = new Theme { DefaultFontSize = FontSize };
        // The system's own UI font, as the web client uses; Godot falls back to it for emoji anyway.
        t.DefaultFont = new SystemFont { FontNames = new[] { "Segoe UI", "Inter", "Noto Sans", "Helvetica Neue", "Arial", "sans-serif" }, Antialiasing = TextServer.FontAntialiasing.Gray };

        foreach (var type in new[] { "Button", "OptionButton", "MenuButton", "CheckBox", "CheckButton" })
        {
            t.SetStylebox("normal", type, Box(Panel2, Line));
            t.SetStylebox("hover", type, Box(Panel2, HoverLine));
            t.SetStylebox("pressed", type, Box(Active, Accent));
            t.SetStylebox("hover_pressed", type, Box(Active, Accent));
            t.SetStylebox("disabled", type, Box(Panel2 with { A = 0.5f }, Line with { A = 0.5f }));
            t.SetStylebox("focus", type, new StyleBoxEmpty());
            foreach (var c in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_hover_pressed_color", "font_focus_color" })
                t.SetColor(c, type, Text);
            t.SetColor("font_disabled_color", type, Muted);
            t.SetConstant("h_separation", type, 6);
        }
        // Check boxes read as a row, not a button.
        foreach (var type in new[] { "CheckBox", "CheckButton" })
            foreach (var st in new[] { "normal", "hover", "pressed", "hover_pressed", "disabled" })
                t.SetStylebox(st, type, Box(Colors.Transparent, null, 6, 4, 3));

        t.SetStylebox("normal", "LineEdit", Box(Panel2, Line, 6, 8, 4));
        t.SetStylebox("focus", "LineEdit", Box(Colors.Transparent, Accent, 6, 8, 4));
        t.SetStylebox("read_only", "LineEdit", Box(Panel2 with { A = 0.5f }, Line, 6, 8, 4));
        t.SetColor("font_color", "LineEdit", Text);
        t.SetColor("font_placeholder_color", "LineEdit", Muted);
        t.SetColor("caret_color", "LineEdit", Accent);
        t.SetColor("selection_color", "LineEdit", Active);

        t.SetStylebox("panel", "PanelContainer", Box(Panel, Line, 6, 8, 6));
        t.SetStylebox("panel", "Panel", Box(Panel, Line, 6));
        t.SetStylebox("panel", "PopupMenu", Box(Panel, Line, 6, 4, 4));
        t.SetStylebox("hover", "PopupMenu", Box(Active, null, 4, 8, 3));
        t.SetColor("font_color", "PopupMenu", Text);
        t.SetColor("font_hover_color", "PopupMenu", Text);
        t.SetColor("font_disabled_color", "PopupMenu", Muted);
        t.SetColor("font_separator_color", "PopupMenu", Muted);
        t.SetStylebox("panel", "TooltipPanel", Box(Panel, Line, 5, 8, 4));
        t.SetColor("font_color", "TooltipLabel", Text);
        t.SetFontSize("font_size", "TooltipLabel", SmallSize);

        t.SetColor("font_color", "Label", Text);
        t.SetColor("default_color", "RichTextLabel", Text);
        t.SetStylebox("normal", "RichTextLabel", new StyleBoxEmpty());
        t.SetStylebox("focus", "RichTextLabel", new StyleBoxEmpty());
        t.SetConstant("line_separation", "RichTextLabel", 3);
        t.SetColor("table_border", "RichTextLabel", Line);

        // Thin scroll bars, as the browser's dark ones.
        foreach (var type in new[] { "VScrollBar", "HScrollBar" })
        {
            t.SetStylebox("scroll", type, Box(Colors.Transparent, null, 4, 3, 3));
            t.SetStylebox("scroll_focus", type, Box(Colors.Transparent, null, 4, 3, 3));
            t.SetStylebox("grabber", type, Box(Line, null, 4, 3, 3));
            t.SetStylebox("grabber_highlight", type, Box(HoverLine, null, 4, 3, 3));
            t.SetStylebox("grabber_pressed", type, Box(HoverLine, null, 4, 3, 3));
        }
        t.SetStylebox("slider", "HSlider", Box(Bg, null, 3, 0, 3));
        t.SetStylebox("grabber_area", "HSlider", Box(Active, null, 3, 0, 3));
        t.SetStylebox("grabber_area_highlight", "HSlider", Box(Active, null, 3, 0, 3));
        t.SetStylebox("separator", "HSeparator", new StyleBoxLine { Color = Line, Thickness = 1 });
        return t;
    }

    /// <summary>A muted, upper-case section heading, as the web client's tool column has.</summary>
    public static Label Heading(string text) => new()
    {
        Text = text.ToUpperInvariant(),
        LabelSettings = new LabelSettings { FontSize = 11, FontColor = Muted },
    };

    /// <summary>Makes a button small: the speed buttons, tab buttons and option rows.</summary>
    public static T Small<T>(T b, int padX = 6, int padY = 2, int size = SmallSize) where T : Button
    {
        b.AddThemeFontSizeOverride("font_size", size);
        foreach (var (st, bg, border) in new[] { ("normal", Panel2, Line), ("hover", Panel2, HoverLine), ("pressed", Active, Accent), ("hover_pressed", Active, Accent) })
            b.AddThemeStyleboxOverride(st, Box(bg, border, 6, padX, padY));
        return b;
    }

    // ---------------------------------------------------------------------------------------------------------
    // The UI overhaul, variation A (Beta 4, part 2): glass panels floating over the map, a blue accent, gold prices.

    public static readonly Color Glass = new(14 / 255f, 19 / 255f, 31 / 255f, 0.93f), GlassLine = new(1, 1, 1, 0.09f),
        UiAccent = new("#3e6fd1"), Gold = new("#f3c94a"), Soft = new("#c9d2e3"), Label2 = new("#7d889e");

    /// <summary>A glass panel over the map, with a soft shadow.</summary>
    public static StyleBoxFlat GlassBox(int radius = 12, int padX = 8, int padY = 8)
    {
        var s = Box(Glass, GlassLine, radius, padX, padY);
        s.ShadowColor = new Color(0, 0, 0, 0.3f);
        s.ShadowSize = 12;
        s.ShadowOffset = new Vector2(0, 4);
        return s;
    }

    static readonly Dictionary<(string, int, Color), Texture2D> Icons = new();

    /// <summary>One of the shared UI icons, drawn at a size in a colour.</summary>
    public static Texture2D Icon(string name, int size = 20, Color? colour = null)
    {
        var c = colour ?? Soft;
        if (Icons.TryGetValue((name, size, c), out var t)) return t;
        var img = new Image();
        img.LoadSvgFromString(ToyCity.ClientModel.Tools.UiIcons.Svg(name, "#" + c.ToHtml(false)), size / 24f);
        return Icons[(name, size, c)] = ImageTexture.CreateFromImage(img);
    }

    /// <summary>A flat square button holding an icon: blue when pressed, a faint fill on hover.</summary>
    public static Button IconButton(string icon, string tip, int size = 20, int pad = 8)
    {
        var b = new Button { Icon = Icon(icon, size), TooltipText = tip, FocusMode = Control.FocusModeEnum.None, IconAlignment = HorizontalAlignment.Center, ExpandIcon = false };
        Flat(b, pad, pad, 8);
        b.AddThemeColorOverride("icon_normal_color", Soft);
        b.AddThemeColorOverride("icon_pressed_color", Colors.White);
        b.AddThemeColorOverride("icon_hover_pressed_color", Colors.White);
        return b;
    }

    /// <summary>Glass-styled flat button states: clear, a faint fill on hover, the blue accent when pressed.</summary>
    public static T Flat<T>(T b, int padX = 10, int padY = 4, int radius = 8) where T : Button
    {
        b.AddThemeStyleboxOverride("normal", Box(Colors.Transparent, null, radius, padX, padY));
        b.AddThemeStyleboxOverride("hover", Box(new Color(1, 1, 1, 0.07f), null, radius, padX, padY));
        b.AddThemeStyleboxOverride("pressed", Box(UiAccent, null, radius, padX, padY));
        b.AddThemeStyleboxOverride("hover_pressed", Box(UiAccent, null, radius, padX, padY));
        b.AddThemeColorOverride("font_color", Soft);
        b.AddThemeColorOverride("font_pressed_color", Colors.White);
        b.AddThemeColorOverride("font_hover_pressed_color", Colors.White);
        b.AddThemeColorOverride("font_hover_color", Colors.White);
        return b;
    }
}
