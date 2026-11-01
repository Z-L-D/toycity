using Godot;
using ToyCity.ClientModel;

namespace ToyCity.Godot;

/// <summary>
/// The roads as they'd look from the air (Beta 3.1 part 4), draped on the ground's corners so they climb and bank
/// with it. Each path cell is drawn from its profile's look (RoadLook) and the sides it joins its neighbours on:
/// <list type="bullet">
/// <item>concrete sidewalks to the cell's edge on town roads, grass verges on highways;</item>
/// <item>the carriageway, in asphalt with a little wear, or packed earth, gravel or a red bike lane;</item>
/// <item>kerbs along the carriageway's edges, and the painted lines: a double yellow, dashed lane lines, solid edge
/// lines, a barrier or a grass median, tram rails, and zebra crossings where three or more roads meet;</item>
/// <item>rail on a bed of ballast with sleepers and two steel rails.</item>
/// </list>
/// A path over a river is a bridge: the same layers on a flat deck a step over the water, with parapets and a pier.
/// The map is cut into chunks of 16 × 16 cells, and a chunk is rebuilt only when a path or the ground in it changes.
/// </summary>
public partial class RoadsView : Node3D
{
    const int Chunk = 16;
    // Heights above the ground, in world units (a cell is 1): each layer clears the one under it.
    const float LiftWalk = 0.022f, LiftDeck = 0.028f, LiftKerb = 0.031f, LiftPaint = 0.033f, LiftRail = 0.045f;

    static readonly Color Sidewalk = new(0.66f, 0.65f, 0.62f), Kerb = new(0.78f, 0.77f, 0.74f);
    static readonly Color Asphalt = new(0.25f, 0.25f, 0.26f), Earth = new(0.48f, 0.40f, 0.30f), Gravel = new(0.62f, 0.58f, 0.52f);
    static readonly Color BikeRed = new(0.52f, 0.25f, 0.22f), Pavers = new(0.70f, 0.68f, 0.64f);
    static readonly Color White = new(0.90f, 0.90f, 0.88f), Yellow = new(0.88f, 0.72f, 0.20f), Grass = new(0.30f, 0.40f, 0.19f);
    static readonly Color Ballast = new(0.45f, 0.42f, 0.38f), Sleeper = new(0.30f, 0.24f, 0.19f), Steel = new(0.72f, 0.73f, 0.75f);
    static readonly Color Barrier = new(0.74f, 0.74f, 0.72f);
    static readonly Color Limestone = new(0.76f, 0.73f, 0.66f), Brick = new(0.55f, 0.31f, 0.24f), Concrete = new(0.63f, 0.63f, 0.60f);
    static readonly Vector2[] Dir = { new(0, -1), new(1, 0), new(0, 1), new(-1, 0) };

    readonly CityMirror _city;
    readonly CityView _view;
    readonly StandardMaterial3D _material = new()
    {
        VertexColorUseAsAlbedo = true, Roughness = 0.92f, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };
    MeshInstance3D?[] _chunks = Array.Empty<MeshInstance3D?>();
    ulong[] _sums = Array.Empty<ulong>();
    int _cols, _rows, _builtFor = -1;
    readonly List<Vector3> _v = new(), _n = new();
    readonly List<Color> _c = new();
    /// <summary>On a bridge, the deck's height for everything drawn on the cell; null on the ground.</summary>
    float? _deck;

    public RoadsView(CityMirror city, CityView view) => (_city, _view) = (city, view);

    public override void _Process(double delta)
    {
        if (!_city.Loaded || _city.RoadVersion == _builtFor) return;
        _builtFor = _city.RoadVersion;
        int cols = (_city.Width + Chunk - 1) / Chunk, rows = (_city.Height + Chunk - 1) / Chunk;
        if (cols != _cols || rows != _rows)
        {
            foreach (var m in _chunks) m?.QueueFree();
            (_cols, _rows) = (cols, rows);
            _chunks = new MeshInstance3D?[cols * rows];
            _sums = new ulong[cols * rows];
        }
        for (int k = 0; k < cols * rows; k++)
        {
            ulong sum = Sum(k % cols, k / cols);
            if (sum == _sums[k] && _chunks[k] != null) continue;
            _sums[k] = sum;
            BuildChunk(k);
        }
    }

    /// <summary>A checksum of what a chunk's roads depend on: the paths in and around it, and the ground's corners.</summary>
    ulong Sum(int cx, int cy)
    {
        ulong h = 1469598103934665603UL;
        void Mix(ulong v) { h ^= v; h *= 1099511628211UL; }
        for (int y = cy * Chunk - 1; y <= (cy + 1) * Chunk; y++)
            for (int x = cx * Chunk - 1; x <= (cx + 1) * Chunk; x++)
            {
                if (!_city.InBounds(x, y)) continue;
                int i = _city.Idx(x, y);
                Mix(_city.Paths[i]);
                Mix(_city.PathSurfaces.Length > i ? _city.PathSurfaces[i] : 0UL);
                if (_city.Paths[i] != 0) Mix(RoadShape.UnderStrip(_city, i) ? 1UL : 2UL);
            }
        for (int y = cy * Chunk; y <= Math.Min(_city.Height, (cy + 1) * Chunk); y++)
            for (int x = cx * Chunk; x <= Math.Min(_city.Width, (cx + 1) * Chunk); x++)
                Mix(_city.Corners[_city.CornerIdx(x, y)]);
        return h;
    }

    void BuildChunk(int k)
    {
        _v.Clear(); _n.Clear(); _c.Clear();
        int x0 = k % _cols * Chunk, y0 = k / _cols * Chunk;
        for (int y = y0; y < Math.Min(_city.Height, y0 + Chunk); y++)
            for (int x = x0; x < Math.Min(_city.Width, x0 + Chunk); x++)
            {
                int i = _city.Idx(x, y);
                if (_city.Paths[i] == 0 || RoadShape.UnderStrip(_city, i)) continue;
                // A tunnel is under the ground (the underground view shows it); where it meets the open air, a portal.
                if (_city.IsTunnel(i))
                {
                    Portals(x, y);
                    continue;
                }
                // A path over a river is a bridge: its deck where the server graded it, at least a step over the water.
                _deck = _city.IsRiver(i) ? (_city.PathDecks[i] > 0 ? _city.PathDecks[i] / 20f : _city.Rivers[i] + 1) * ClassicView.HeightStep : null;
                var look = RoadLook.For(_city.ProfileAt(i), _city.SurfaceAt(i));
                int arms = RoadLook.Arms(_city, i);
                // Beta 3.2: a footpath steeper than its surface holds a ramp is built as stairs.
                if (_deck == null && RoadLook.IsStairs(_city, i, arms)) Stairs(x, y, look, arms);
                else Cell(x, y, look, arms);
                if (_deck == null) RetainingWalls(x, y, arms);
                if (_deck is { } deck) Bridge(x, y, look, arms, deck);
                _deck = null;
            }
        if (_v.Count == 0)
        {
            _chunks[k]?.QueueFree();
            _chunks[k] = null;
            return;
        }
        var arrays = new global::Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = _v.ToArray();
        arrays[(int)Mesh.ArrayType.Normal] = _n.ToArray();
        arrays[(int)Mesh.ArrayType.Color] = _c.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        if (_chunks[k] == null)
        {
            _chunks[k] = new MeshInstance3D { MaterialOverride = _material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            AddChild(_chunks[k]);
        }
        _chunks[k]!.Mesh = mesh;
    }

    /// <summary>One path cell: its surfaces, edges and paint, for the sides it joins.</summary>
    void Cell(int x, int y, RoadLook look, int arms)
    {
        var centre = new Vector2(x + 0.5f, y + 0.5f);
        float w = look.Width, hw = w / 2;
        bool straight = arms == 0b0101 || arms == 0b1010;
        int joins = System.Numerics.BitOperations.PopCount((uint)arms);

        // Under the carriageway: sidewalks to the cell's edge on a town road.
        if (look.Sidewalks) Rect(centre, Vector2.Right, Vector2.Down, -0.5f, 0.5f, -0.5f, 0.5f, LiftWalk, Sidewalk, true);

        var surface = Colour(look.Paving);
        // The carriageway: a square where the arms meet, then each arm out to the cell's edge.
        Rect(centre, Vector2.Right, Vector2.Down, -hw, hw, -hw, hw, LiftDeck, surface, true);
        for (int a = 0; a < 4; a++)
            if ((arms & 1 << a) != 0) Arm(centre, a, hw, 0.5f, -hw, hw, LiftDeck, surface, true);

        if (look.Paving == Paving.Rail)
        {
            Track(centre, arms, hw * 0.55f, true);
            return;
        }
        if (!look.Painted && !(look.Paving == Paving.Brick && look.Sidewalks)) return;

        // Kerbs along the open sides of the carriageway on a town road.
        if (look.Sidewalks)
        {
            const float kerb = 0.022f;
            for (int a = 0; a < 4; a++)
            {
                if ((arms & 1 << a) != 0)
                {
                    // Along the arm, both sides.
                    Arm(centre, a, hw, 0.5f, -hw - kerb, -hw, LiftKerb, Kerb, false);
                    Arm(centre, a, hw, 0.5f, hw, hw + kerb, LiftKerb, Kerb, false);
                }
                else
                {
                    // Across the end of the square, where no road leaves.
                    Arm(centre, a, hw, hw + kerb, -hw - kerb, hw + kerb, LiftKerb, Kerb, false);
                }
            }
        }

        // The median down the middle of a divided road.
        float mh = look.MedianM / RoadLook.CellM / 2;
        foreach (var (from, to) in Runs(arms, straight, hw))
        {
            var (a, s0, s1) = (from, to.S0, to.S1);
            switch (look.Centre)
            {
                case CentreLine.GrassMedian: Arm(centre, a, s0, s1, -mh, mh, LiftPaint, Grass, false); break;
                case CentreLine.TramReservation: Arm(centre, a, s0, s1, -mh, mh, LiftPaint, Pavers, false); break;
                case CentreLine.Barrier: Arm(centre, a, s0, s1, -0.012f, 0.012f, LiftRail, Barrier, false); break;
            }
        }

        // Paint: lane lines (dashed between same-way lanes, a double yellow or the median's edge at the middle),
        // and solid white edge lines on a highway.
        const float line = 0.008f;
        foreach (var (a, run) in Runs(arms, straight, hw))
        {
            foreach (var (off, isCentre) in look.LaneLines())
            {
                if (isCentre)
                {
                    if (look.Centre == CentreLine.DoubleYellow)
                    {
                        Arm(centre, a, run.S0, run.S1, -line * 2.2f, -line * 0.8f, LiftPaint, Yellow, false);
                        Arm(centre, a, run.S0, run.S1, line * 0.8f, line * 2.2f, LiftPaint, Yellow, false);
                    }
                    else if (look.Centre is CentreLine.GrassMedian or CentreLine.TramReservation or CentreLine.Barrier)
                    {
                        Arm(centre, a, run.S0, run.S1, -mh - line * 1.5f, -mh - line * 0.5f, LiftPaint, Yellow, false);
                        Arm(centre, a, run.S0, run.S1, mh + line * 0.5f, mh + line * 1.5f, LiftPaint, Yellow, false);
                    }
                    continue;
                }
                Dashes(centre, a, run.S0, run.S1, off - line / 2, off + line / 2);
            }
            if (look.EdgeLines)
            {
                Arm(centre, a, run.S0, run.S1, -hw + 0.02f, -hw + 0.02f + line, LiftPaint, White, false);
                Arm(centre, a, run.S0, run.S1, hw - 0.02f - line, hw - 0.02f, LiftPaint, White, false);
            }
            if (look.TramTracks > 0)
            {
                float lane = look.Centre == CentreLine.TramReservation ? mh / 2 : (hw - mh) / 2;
                foreach (float o in new[] { -lane - (look.Centre == CentreLine.TramReservation ? 0 : mh), lane + (look.Centre == CentreLine.TramReservation ? 0 : mh) })
                    foreach (float g in new[] { -0.045f, 0.045f })
                        Arm(centre, a, run.S0, run.S1, o + g - 0.004f, o + g + 0.004f, LiftPaint + 0.001f, Steel, false);
            }
        }

        // Zebra crossings across each road into a junction, on town roads.
        if (look.Crosswalks && joins >= 3)
            for (int a = 0; a < 4; a++)
            {
                if ((arms & 1 << a) == 0) continue;
                for (float t = -hw + 0.03f; t < hw - 0.03f; t += 0.05f)
                    Arm(centre, a, hw + 0.02f, hw + 0.12f, t, t + 0.025f, LiftPaint, White, false);
            }
    }

    /// <summary>
    /// Retaining walls (Beta 3.2): where the ground beside a path climbs or falls away steeper than a soil bank stands
    /// (lots, other roads or water kept its banks from spreading, or a natural cliff), a dressed-stone wall along the
    /// path's edge, from the road to the ground beyond.
    /// </summary>
    void RetainingWalls(int x, int y, int arms)
    {
        var stone = new Color(0.56f, 0.53f, 0.48f);
        var centre = new Vector2(x + 0.5f, y + 0.5f);
        for (int a = 0; a < 4; a++)
        {
            if ((arms & 1 << a) != 0) continue;
            int nx = x + (int)Dir[a].X, ny = y + (int)Dir[a].Y;
            if (!_city.InBounds(nx, ny)) continue;
            int j = _city.Idx(nx, ny);
            if (_city.Paths[j] != 0 || _city.IsWater(j)) continue;
            var edge = centre + Dir[a] * 0.5f;
            var far = centre + Dir[a] * 1.45f;
            float he = _view.Surface(edge.X, edge.Y), hf = _view.Surface(far.X, far.Y);
            // A cell is a world unit across and a world unit is 16 m high, so this is rise over run.
            if (Math.Abs(hf - he) / 0.95f < 0.8f) continue;
            var v = new Vector2(-Dir[a].Y, Dir[a].X);
            float top = Math.Min(Math.Max(he, hf), Math.Min(he, hf) + 0.5f);
            Wall(edge - v * 0.5f, edge + v * 0.5f, Math.Min(he, hf) - 0.01f, top, stone);
            // A coping along the top.
            Wall(edge - v * 0.5f + Dir[a] * 0.012f, edge + v * 0.5f + Dir[a] * 0.012f, top - 0.004f, top + 0.004f, stone.Lightened(0.15f));
        }
    }

    /// <summary>
    /// A tunnel cell's portals (Beta 3.2): on each side where the path comes out onto open ground, a concrete headwall
    /// across the cut with the dark mouth of the bore in it, standing from the road up to the ground above.
    /// </summary>
    void Portals(int x, int y)
    {
        int i = _city.Idx(x, y);
        float floor = _city.PathDecks[i] / 20f * ClassicView.HeightStep;
        var look = RoadLook.For(_city.ProfileAt(i), _city.SurfaceAt(i));
        float hw = Math.Max(look.Sidewalks ? 0.45f : look.Width / 2 + 0.05f, 0.12f);
        var concrete = new Color(0.66f, 0.65f, 0.61f);
        var mouth = new Color(0.05f, 0.05f, 0.06f);
        for (int a = 0; a < 4; a++)
        {
            int nx = x + (int)Dir[a].X, ny = y + (int)Dir[a].Y;
            if (!_city.InBounds(nx, ny)) continue;
            int j = _city.Idx(nx, ny);
            if (_city.Paths[j] == 0 || _city.IsTunnel(j)) continue;
            // The face stands on the cell's edge toward the open road.
            var centre = new Vector2(x + 0.5f, y + 0.5f) + Dir[a] * 0.5f;
            var v = new Vector2(-Dir[a].Y, Dir[a].X);
            float top = Math.Max(_view.Surface(centre.X, centre.Y), floor + 0.25f);
            float clear = 0.0035f * 16 * 5.5f;  // 5.5 m of headroom
            var l = centre - v * (hw + 0.08f);
            var r = centre + v * (hw + 0.08f);
            Wall(l, r, floor, top, concrete);
            // The bore: a dark opening the road's width, a little in front of the face.
            Wall(centre - v * hw + Dir[a] * 0.004f, centre + v * hw + Dir[a] * 0.004f, floor + LiftDeck, floor + clear, mouth);
        }
    }

    static Color Colour(Paving p) => p switch
    {
        Paving.Dirt => Earth, Paving.Gravel => Gravel, Paving.Limestone => Limestone, Paving.Brick => Brick,
        Paving.BikeLane => BikeRed, Paving.Concrete => Pavers, Paving.Rail => Ballast, _ => Asphalt,
    };

    /// <summary>
    /// A footpath steep enough to be stairs (Beta 3.2): level treads with a riser up to each, along the way it runs,
    /// and a handrail post-and-rail either side. Treads are about 30 cm deep for the climb they take, so a gentle
    /// stair has long treads and a steep one short.
    /// </summary>
    void Stairs(int x, int y, RoadLook look, int arms)
    {
        var centre = new Vector2(x + 0.5f, y + 0.5f);
        float hw = Math.Max(look.Width / 2, 0.09f);
        var colour = Colour(look.Paving);
        bool ns = (arms & 0b0101) != 0 && (arms & 0b1010) == 0 || arms == 0 && Math.Abs(_view.Surface(centre.X, y) - _view.Surface(centre.X, y + 1)) >= Math.Abs(_view.Surface(x, centre.Y) - _view.Surface(x + 1, centre.Y));
        var u = ns ? new Vector2(0, 1) : new Vector2(1, 0);
        var v = new Vector2(-u.Y, u.X);
        float h0 = _view.Surface((centre - u * 0.5f).X, (centre - u * 0.5f).Y), h1 = _view.Surface((centre + u * 0.5f).X, (centre + u * 0.5f).Y);
        // A riser of about 17 cm: a cell is 16 m, a world unit, so 17 cm is about 0.0106.
        int steps = Math.Clamp((int)MathF.Ceiling(Math.Abs(h1 - h0) / 0.0106f), 2, 96);
        var riserColour = colour.Darkened(0.25f);
        for (int k = 0; k < steps; k++)
        {
            float s0 = -0.5f + (float)k / steps, s1 = -0.5f + (float)(k + 1) / steps;
            float h = h0 + (h1 - h0) * (k + 0.5f) / steps + LiftDeck;
            var a = centre + u * s0 + v * -hw;
            var b = centre + u * s0 + v * hw;
            var c = centre + u * s1 + v * hw;
            var d = centre + u * s1 + v * -hw;
            Quad(new Vector3(a.X, h, a.Y), new Vector3(b.X, h, b.Y), new Vector3(c.X, h, c.Y), new Vector3(d.X, h, d.Y), colour);
            // The riser from this tread up (or down) to the next.
            float next = k + 1 < steps ? h0 + (h1 - h0) * (k + 1.5f) / steps + LiftDeck : h;
            if (k + 1 < steps) Wall(d, c, Math.Min(h, next), Math.Max(h, next), riserColour);
        }
        // Handrails: a rail a metre over the nosings, either side.
        var steel = new Color(0.35f, 0.36f, 0.38f);
        for (int side = -1; side <= 1; side += 2)
        {
            var a = centre - u * 0.5f + v * (side * (hw + 0.005f));
            var b = centre + u * 0.5f + v * (side * (hw + 0.005f));
            Rail(new Vector3(a.X, h0 + LiftDeck + 0.0625f, a.Y), new Vector3(b.X, h1 + LiftDeck + 0.0625f, b.Y), steel);
            for (int k = 0; k <= 4; k++)
            {
                var p = a.Lerp(b, k / 4f);
                float hp = h0 + (h1 - h0) * k / 4f + LiftDeck;
                Wall(p - u * 0.002f, p + u * 0.002f, hp, hp + 0.0625f, steel);
            }
        }
    }

    /// <summary>A flat quad from four points, its normal facing up.</summary>
    void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color colour)
    {
        var n = (b - a).Cross(d - a).Normalized();
        if (n.Y < 0) n = -n;
        foreach (var p in new[] { a, b, c, a, c, d })
        {
            _v.Add(p);
            _n.Add(n);
            _c.Add(colour);
        }
    }

    /// <summary>A thin sloping rail from a to b: a narrow upright strip.</summary>
    void Rail(Vector3 a, Vector3 b, Color colour)
    {
        var up = new Vector3(0, 0.004f, 0);
        var n = (b - a).Cross(up).Normalized();
        foreach (var p in new[] { a - up, b - up, b + up, a - up, b + up, a + up })
        {
            _v.Add(p);
            _n.Add(n);
            _c.Add(colour);
        }
    }

    /// <summary>
    /// A bridge cell's structure: a concrete parapet along each side the road doesn't leave by, the slab's edge, and
    /// a pier down to the river bed.
    /// </summary>
    void Bridge(int x, int y, RoadLook look, int arms, float deck)
    {
        var centre = new Vector2(x + 0.5f, y + 0.5f);
        float edge = look.Sidewalks ? 0.47f : look.Width / 2 + 0.04f;
        var concrete = new Color(0.62f, 0.61f, 0.58f);
        bool ns = (arms & 0b0101) != 0 || arms == 0;  // the road runs north–south across it
        for (int side = -1; side <= 1; side += 2)
        {
            // Parapets along the run, both sides.
            var u = ns ? new Vector2(0, 1) : new Vector2(1, 0);
            var v = new Vector2(-u.Y, u.X);
            var a = centre + v * (side * edge) - u * 0.5f;
            var b = centre + v * (side * edge) + u * 0.5f;
            Wall(a, b, deck + LiftWalk, deck + 0.075f, concrete);
            // The slab's face, down from the deck.
            Wall(centre + v * (side * (edge + 0.01f)) - u * 0.5f, centre + v * (side * (edge + 0.01f)) + u * 0.5f, deck - 0.09f, deck + LiftWalk, concrete.Darkened(0.15f));
        }
        // A pier in the middle, down to the bed.
        float bed = _view.Surface(centre.X, centre.Y);
        foreach (var (p, q) in new[] { (new Vector2(-0.08f, -0.08f), new Vector2(0.08f, -0.08f)), (new Vector2(0.08f, -0.08f), new Vector2(0.08f, 0.08f)),
                     (new Vector2(0.08f, 0.08f), new Vector2(-0.08f, 0.08f)), (new Vector2(-0.08f, 0.08f), new Vector2(-0.08f, -0.08f)) })
            Wall(centre + p, centre + q, bed - 0.05f, deck - 0.09f, concrete.Darkened(0.1f));
    }

    /// <summary>An upright quad from a to b, between two heights.</summary>
    void Wall(Vector2 a, Vector2 b, float lo, float hi, Color colour)
    {
        Vector3 a0 = new(a.X, lo, a.Y), b0 = new(b.X, lo, b.Y), b1 = new(b.X, hi, b.Y), a1 = new(a.X, hi, a.Y);
        var n = (b0 - a0).Cross(a1 - a0).Normalized();
        foreach (var p in new[] { a0, b0, b1, a0, b1, a1 })
        {
            _v.Add(p);
            _n.Add(n);
            _c.Add(colour);
        }
    }

    /// <summary>
    /// Where a cell's lines run, as arm and span from the centre: through the whole cell on a straight road, or each
    /// arm from the edge of the junction's square to the cell's edge.
    /// </summary>
    static IEnumerable<(int Arm, (float S0, float S1) Span)> Runs(int arms, bool straight, float hw)
    {
        if (straight)
        {
            int a = (arms & 1) != 0 ? 2 : 1;  // south or east, from the far edge
            yield return (a, (-0.5f, 0.5f));
            yield break;
        }
        int joins = System.Numerics.BitOperations.PopCount((uint)arms);
        float start = joins >= 3 ? hw + 0.13f : hw;
        for (int a = 0; a < 4; a++)
            if ((arms & 1 << a) != 0) yield return (a, (start, 0.5f));
    }

    /// <summary>Dashed white along an arm, at the same places in every cell so a lane line runs on evenly.</summary>
    void Dashes(Vector2 centre, int a, float s0, float s1, float t0, float t1)
    {
        foreach (var (from, to) in RoadLook.Dashes)
        {
            // The dash in the cell's own frame (0 at its north or west edge), turned to the arm's frame.
            float d0 = from - 0.5f, d1 = to - 0.5f;
            if (a is 0 or 3) (d0, d1) = (-d1, -d0);
            float lo = Math.Max(d0, s0), hi = Math.Min(d1, s1);
            if (hi > lo) Arm(centre, a, lo, hi, t0, t1, LiftPaint, White, false);
        }
    }

    /// <summary>A rail line: sleepers across the ballast every 0.6 m or so, then two rails.</summary>
    void Track(Vector2 centre, int arms, float gauge, bool sleepers)
    {
        var runs = new List<(int A, float S0, float S1)>();
        bool straight = arms == 0b0101 || arms == 0b1010;
        if (straight) runs.Add(((arms & 1) != 0 ? 2 : 1, -0.5f, 0.5f));
        else
        {
            for (int a = 0; a < 4; a++) if ((arms & 1 << a) != 0) runs.Add((a, 0, 0.5f));
            if (runs.Count == 0) runs.Add((1, -0.25f, 0.25f));
        }
        foreach (var (a, s0, s1) in runs)
        {
            if (sleepers)
                for (float s = s0 + 0.02f; s < s1 - 0.01f; s += 0.05f)
                    Arm(centre, a, s, s + 0.022f, -gauge * 1.45f, gauge * 1.45f, LiftKerb, Sleeper, false);
            Arm(centre, a, s0, s1, -gauge - 0.006f, -gauge + 0.006f, LiftRail, Steel, false);
            Arm(centre, a, s0, s1, gauge - 0.006f, gauge + 0.006f, LiftRail, Steel, false);
        }
    }

    /// <summary>A strip along arm <paramref name="a"/>: from s0 to s1 out from the centre, t0 to t1 across it.</summary>
    void Arm(Vector2 centre, int a, float s0, float s1, float t0, float t1, float lift, Color colour, bool wear)
    {
        var u = Dir[a];
        var v = new Vector2(-u.Y, u.X);
        Rect(centre, u, v, s0, s1, t0, t1, lift, colour, wear);
    }

    /// <summary>
    /// A rectangle in the frame (u, v) about a centre, draped on the ground: split into pieces about a quarter of a
    /// cell across, each corner set on the ground's surface. <paramref name="wear"/> varies the colour a little
    /// across the surface, as sun and traffic do.
    /// </summary>
    void Rect(Vector2 centre, Vector2 u, Vector2 v, float s0, float s1, float t0, float t1, float lift, Color colour, bool wear)
    {
        int ns = Math.Max(1, (int)MathF.Ceiling((s1 - s0) * 4)), nt = Math.Max(1, (int)MathF.Ceiling((t1 - t0) * 4));
        Vector3 At(float s, float t)
        {
            var p = centre + u * s + v * t;
            return new Vector3(p.X, (_deck ?? _view.Surface(p.X, p.Y)) + lift, p.Y);
        }
        for (int i = 0; i < ns; i++)
            for (int j = 0; j < nt; j++)
            {
                float sa = s0 + (s1 - s0) * i / ns, sb = s0 + (s1 - s0) * (i + 1) / ns;
                float ta = t0 + (t1 - t0) * j / nt, tb = t0 + (t1 - t0) * (j + 1) / nt;
                Vector3 p00 = At(sa, ta), p10 = At(sb, ta), p11 = At(sb, tb), p01 = At(sa, tb);
                var n = (p10 - p00).Cross(p01 - p00).Normalized();
                if (n.Y < 0) n = -n;
                var col = colour;
                if (wear)
                {
                    var m = (p00 + p11) / 2;
                    float k = Mathf.Sin(m.X * 7.1f + m.Z * 3.3f) * Mathf.Sin(m.Z * 5.7f - m.X * 2.1f);
                    col = colour.Lightened(0.035f * k).Darkened(0.02f);
                }
                foreach (var p in new[] { p00, p10, p11, p00, p11, p01 })
                {
                    _v.Add(p);
                    _n.Add(n);
                    _c.Add(col);
                }
            }
    }
}
