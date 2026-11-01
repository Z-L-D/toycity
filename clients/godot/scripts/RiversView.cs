using Godot;
using ToyCity.ClientModel;

namespace ToyCity.Godot;

/// <summary>
/// Rivers and floods (Beta 3.1 part 5): the water over each river cell at its own surface, which steps down the
/// valley with the river, and the floodwater spread over the low ground beside it while the river is up. Each corner
/// of the sheet stands at the average of the water cells around it, so the surface runs smoothly from cell to cell
/// and down rapids. It uses the sea's material, so its colour comes from how deep it is over the ground.
/// </summary>
public partial class RiversView : Node3D
{
    readonly CityMirror _city;
    readonly CityView _view;
    MeshInstance3D? _mesh;
    (int, int) _builtFor = (-1, -1);

    public RiversView(CityMirror city, CityView view) => (_city, _view) = (city, view);

    public override void _Process(double delta)
    {
        if (!_city.Loaded || _view.WaterMaterial == null || (_city.TerrainVersion, _city.FloodStage) == _builtFor) return;
        _builtFor = (_city.TerrainVersion, _city.FloodStage);
        int w = _city.Width, h = _city.Height;
        var flooded = _city.Flooded;
        var level = new float[w * h];
        bool any = false;
        for (int i = 0; i < w * h; i++)
        {
            level[i] = _city.IsRiver(i) ? _city.Rivers[i] + (_city.FloodStage > 0 ? _city.FloodStage : 0) : flooded[i];
            any |= level[i] > 0;
        }
        _mesh?.QueueFree();
        _mesh = null;
        if (!any) return;
        float Corner(int cx, int cy)
        {
            float sum = 0;
            int n = 0;
            for (int dy = -1; dy <= 0; dy++)
                for (int dx = -1; dx <= 0; dx++)
                {
                    int x = cx + dx, y = cy + dy;
                    if (!_city.InBounds(x, y) || level[_city.Idx(x, y)] <= 0) continue;
                    sum += level[_city.Idx(x, y)];
                    n++;
                }
            return (sum / n - 0.35f) * ClassicView.HeightStep;
        }
        var v = new List<Vector3>();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (level[_city.Idx(x, y)] <= 0) continue;
                Vector3 nw = new(x, Corner(x, y), y), ne = new(x + 1, Corner(x + 1, y), y);
                Vector3 se = new(x + 1, Corner(x + 1, y + 1), y + 1), sw = new(x, Corner(x, y + 1), y + 1);
                v.AddRange(new[] { nw, ne, se, nw, se, sw });
            }
        var arrays = new global::Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = v.ToArray();
        arrays[(int)Mesh.ArrayType.Normal] = Enumerable.Repeat(Vector3.Up, v.Count).ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        _mesh = new MeshInstance3D { Mesh = mesh, MaterialOverride = _view.WaterMaterial, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(_mesh);
    }
}
