using Godot;
using ToyCity.ClientModel;

namespace ToyCity.Godot;

/// <summary>
/// The minimap: the city as one flat image, a pixel per cell, redrawn where cells and buildings change, in the
/// lower right corner of the map with a marker where the camera looks. A click on it moves the camera there.
/// </summary>
public partial class MapView : Node2D
{
    readonly CityMirror _city;
    Image? _image;
    ImageTexture? _texture;
    Sprite2D? _sprite;
    public bool Drawn { get; private set; }

    public MapView(CityMirror city) => _city = city;

    public override void _Ready()
    {
        _sprite = new Sprite2D { Centered = false, TextureFilter = TextureFilterEnum.Nearest };
        AddChild(_sprite);
    }

    /// <summary>Redraws what changed; Main hands every view the same dirty set each frame.</summary>
    public void Refresh(bool all, int[] cells)
    {
        if (_image == null || _image.GetWidth() != _city.Width || _image.GetHeight() != _city.Height || all)
        {
            _image = Image.CreateEmpty(_city.Width, _city.Height, false, Image.Format.Rgb8);
            for (int i = 0; i < _city.N; i++) Paint(i);
            _texture = ImageTexture.CreateFromImage(_image);
            _sprite!.Texture = _texture;
        }
        else if (cells.Length > 0)
        {
            foreach (int i in cells) Paint(i);
            _texture!.Update(_image);
        }
        Fit();
        Drawn = true;
    }

    /// <summary>One cell's colour: a building's type colour, else the ground as the 3D view draws it.</summary>
    void Paint(int i)
    {
        int x = i % _city.Width, y = i / _city.Width;
        var c = _city.BuildingAtCell(x, y) is { } b && _city.TypeById.TryGetValue(b.TypeId, out var t) ? Palette.Of(t)
            : _city.IsWater(i) && _city.Paths[i] == 0 ? Palette.Water : Palette.Cell(_city, i);
        _image!.SetPixel(x, y, c);
    }

    /// <summary>The camera's target, in cells; drawn as a small square.</summary>
    public System.Numerics.Vector2 Marker { set { if (value != _marker) { _marker = value; QueueRedraw(); } } }
    System.Numerics.Vector2 _marker;

    public override void _Draw()
    {
        if (!Drawn) return;
        float r = 3 / Scale.X;
        DrawRect(new Rect2(-1 / Scale.X, -1 / Scale.X, _city.Width + 2 / Scale.X, _city.Height + 2 / Scale.X), new Color(0, 0, 0, 0.6f), false, 2 / Scale.X);
        DrawRect(new Rect2(_marker.X - r, _marker.Y - r, 2 * r, 2 * r), Colors.White, false, 1.5f / Scale.X);
    }

    /// <summary>The cell under a screen point, or null off the minimap.</summary>
    public (int X, int Y)? CellAt(Vector2 screen)
    {
        if (!Drawn) return null;
        var p = (screen - Position) / Scale;
        return p.X >= 0 && p.Y >= 0 && p.X < _city.Width && p.Y < _city.Height ? ((int)p.X, (int)p.Y) : null;
    }

    const float Box = 200, Margin = 12;

    /// <summary>How much of the screen's right side the side panel covers: the minimap sits just left of it.</summary>
    public float RightInset
    {
        get => _rightInset;
        set { if (value != _rightInset) { _rightInset = value; if (Drawn) Fit(); } }
    }
    float _rightInset = ToolController.SideWidth;

    void Fit()
    {
        var size = GetViewportRect().Size;
        float s = Box / Mathf.Max(_city.Width, _city.Height);
        Scale = new Vector2(s, s);
        Position = size - new Vector2(_city.Width, _city.Height) * s - new Vector2(Margin + _rightInset, Margin);
        QueueRedraw();
    }
}
