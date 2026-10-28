using Godot;
using ToyCity.ClientModel;

namespace ToyCity.Godot;

/// <summary>
/// Street names along the roads, as the news and petitions use them: one label for each straight run long enough
/// to hold it, standing over the middle of the run. East-west runs take the street's name, north-south ones the
/// avenue's, through crossings, so an avenue gets one label. Shown at the two closest zoom levels.
/// </summary>
public partial class StreetLabels : Node3D
{
    readonly CityMirror _city;
    readonly CityView _view;
    int _builtFor = -1;
    double _wait;

    public StreetLabels(CityMirror city, CityView view) => (_city, _view) = (city, view);

    /// <summary>Rebuilds the labels when the roads changed, at most every couple of seconds.</summary>
    public void Refresh(double delta)
    {
        _wait -= delta;
        if (!_city.Loaded || _city.RoadVersion == _builtFor || _wait > 0) return;
        _builtFor = _city.RoadVersion;
        _wait = 2;
        foreach (var child in GetChildren()) child.QueueFree();
        bool Path(int x, int y) => _city.InBounds(x, y) && _city.Paths[_city.Idx(x, y)] != 0;
        for (int y = 0; y < _city.Height; y++)
        {
            int start = -1;
            for (int x = 0; x <= _city.Width; x++)
            {
                bool on = x < _city.Width && Path(x, y) && _city.RoadEW(x, y);
                if (on && start < 0) start = x;
                if (!on && start >= 0) { Add(_city.Streets.RoadName(false, y), start, y, x - start, false); start = -1; }
            }
        }
        for (int x = 0; x < _city.Width; x++)
        {
            int start = -1;
            for (int y = 0; y <= _city.Height; y++)
            {
                bool on = y < _city.Height && Path(x, y) && _city.RoadNS(x, y);
                if (on && start < 0) start = y;
                if (!on && start >= 0) { Add(_city.Streets.RoadName(true, x), x, start, y - start, true); start = -1; }
            }
        }
    }

    void Add(string name, int x, int y, int cells, bool vertical)
    {
        const float PixelSize = 0.01f;
        const int FontSize = 40;
        // Roughly how many cells the name takes up; skip runs too short to hold it.
        float width = name.Length * FontSize * 0.55f * PixelSize;
        if (width + 1 > cells) return;
        float cx = vertical ? x + 0.5f : x + cells / 2f, cy = vertical ? y + cells / 2f : y + 0.5f;
        int ix = Math.Clamp((int)cx, 0, _city.Width - 1), iy = Math.Clamp((int)cy, 0, _city.Height - 1);
        AddChild(new Label3D
        {
            Text = name,
            FontSize = FontSize,
            PixelSize = PixelSize,
            OutlineSize = 10,
            Modulate = new Color(0.96f, 0.95f, 0.9f),
            OutlineModulate = new Color(0.08f, 0.09f, 0.12f, 0.9f),
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            Position = new Vector3(cx, _view.Ground(_city.Idx(ix, iy)) + 0.35f, cy),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
    }
}
