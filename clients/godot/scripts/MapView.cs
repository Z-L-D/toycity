using Godot;
using ToyCity.ClientModel;

namespace ToyCity.Godot;

/// <summary>
/// Part 1's view: the city as one flat image, a pixel per cell, redrawn where cells and buildings change. The
/// classic voxel view replaces it; it stays as the minimap.
/// </summary>
public partial class MapView : Node2D
{
    readonly CityMirror _city;
    Image? _image;
    ImageTexture? _texture;
    Sprite2D? _sprite;
    int _version = -1;
    public bool Drawn { get; private set; }

    public MapView(CityMirror city) => _city = city;

    static readonly Color Water = new(0.18f, 0.36f, 0.62f), Road = new(0.33f, 0.33f, 0.36f);
    static readonly Color[] ZoneColors = { default, new(0.30f, 0.69f, 0.31f), new(0.29f, 0.50f, 0.88f), new(0.88f, 0.69f, 0.19f) };

    public override void _Ready()
    {
        _sprite = new Sprite2D { Centered = false, TextureFilter = TextureFilterEnum.Nearest };
        AddChild(_sprite);
    }

    public override void _Process(double delta)
    {
        if (!_city.Loaded || _city.Version == _version) return;
        _version = _city.Version;
        var dirty = _city.TakeDirty();
        if (_image == null || _image.GetWidth() != _city.Width || _image.GetHeight() != _city.Height || dirty.All)
        {
            _image = Image.CreateEmpty(_city.Width, _city.Height, false, Image.Format.Rgb8);
            for (int i = 0; i < _city.N; i++) Paint(i);
            _texture = ImageTexture.CreateFromImage(_image);
            _sprite!.Texture = _texture;
        }
        else
        {
            foreach (int i in dirty.Cells) Paint(i);
            foreach (int id in dirty.Buildings)
                if (_city.Buildings.TryGetValue(id, out var b))
                    for (int y = b.Y; y < b.Y + b.Height; y++)
                        for (int x = b.X; x < b.X + b.Width; x++)
                            if (_city.InBounds(x, y)) Paint(_city.Idx(x, y));
            _texture!.Update(_image);
        }
        Fit();
        Drawn = true;
    }

    /// <summary>One cell's colour: a building's type colour, else road, water, zone or the ground by height.</summary>
    void Paint(int i)
    {
        int x = i % _city.Width, y = i / _city.Width;
        Color c;
        if (_city.BuildingAtCell(x, y) is { } b && _city.TypeById.TryGetValue(b.TypeId, out var t)) c = Color.FromHtml(t.Color);
        else if (_city.Paths[i] != 0) c = Road;
        else if (_city.IsWater(i)) c = Water;
        else if (_city.Zones[i] != 0) c = ZoneColors[_city.Zones[i] / 3 % 4].Lerp(new Color(0.42f, 0.55f, 0.30f), 0.6f);
        else c = new Color(0.36f, 0.50f, 0.26f).Lightened((_city.Heights[i] - _city.WaterLevel) / 80f);
        _image!.SetPixel(x, y, c);
    }

    void Fit()
    {
        var size = GetViewportRect().Size;
        float s = Mathf.Floor(Mathf.Min(size.X / _city.Width, size.Y / _city.Height));
        Scale = new Vector2(s, s);
        Position = (size - new Vector2(_city.Width, _city.Height) * s) / 2;
    }
}
