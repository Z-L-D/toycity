using System.Numerics;

namespace ToyCity.ClientModel;

/// <summary>How the classic camera looks at the city: the SC4 angle, straight down, or low from the side.</summary>
public enum ViewMode { Classic, TopDown, Side }

/// <summary>
/// The classic camera's geometry, with no engine types so it can be tested: an orthographic view of a point on
/// the map from one of four compass rotations at one of five zoom levels. World units are cells: x east, z south
/// (the map's y), y up. Cell (x, y) covers [x, x+1] × [y, y+1]. The Godot camera takes its pose from here, and
/// picking uses the same rays, so what the tests check is what the player clicks.
/// </summary>
public sealed class ClassicView
{
    /// <summary>Height of the view at each zoom level, in cells: 0 shows a whole 128-cell city, 4 a few lots.</summary>
    public static readonly float[] ZoomSizes = { 128, 64, 32, 16, 8 };
    /// <summary>One terrain height step, in cells (2 m on a 16 m cell).</summary>
    public const float HeightStep = 0.125f;
    /// <summary>
    /// How far back the camera sits from its target: just past the nearest ground the screen shows, plus room for
    /// the tallest tower. Keeping it short keeps the shadow pass to what's on screen.
    /// </summary>
    public float Distance => Size / 2 / MathF.Max(MathF.Sin(Rad(Pitch)), 0.05f) + 24;

    public Vector2 Target;       // the map point at the centre of the screen, in cells
    public int Rotation;         // 0..3: looking north-west, north-east, south-east, south-west
    public int Zoom = 1;         // 0..4
    public ViewMode Mode = ViewMode.Classic;
    public float TargetHeight;   // the ground height under the target, in cells

    public float Size => ZoomSizes[Zoom];
    public float Pitch => Mode switch { ViewMode.TopDown => 90f, ViewMode.Side => 12f, _ => 35f };
    public float Yaw => 45f + 90f * Rotation;

    public void Rotate(int by) => Rotation = ((Rotation + by) % 4 + 4) % 4;
    public void ZoomBy(int by) => Zoom = Math.Clamp(Zoom + by, 0, ZoomSizes.Length - 1);

    static float Rad(float deg) => deg * MathF.PI / 180f;

    /// <summary>The direction the camera looks.</summary>
    public Vector3 Forward
    {
        get
        {
            float p = Rad(Pitch), y = Rad(Yaw);
            return Vector3.Normalize(new Vector3(-MathF.Cos(p) * MathF.Sin(y), -MathF.Sin(p), -MathF.Cos(p) * MathF.Cos(y)));
        }
    }

    /// <summary>Screen right, along the ground.</summary>
    public Vector3 Right { get { float y = Rad(Yaw); return new Vector3(MathF.Cos(y), 0, -MathF.Sin(y)); } }

    /// <summary>Screen up.</summary>
    public Vector3 Up => Vector3.Cross(Right, Forward);

    public Vector3 Position => new Vector3(Target.X, TargetHeight, Target.Y) - Forward * Distance;

    /// <summary>The ray through a pixel of a viewport w × h pixels.</summary>
    public (Vector3 Origin, Vector3 Dir) Ray(float sx, float sy, float w, float h)
    {
        float u = (sx / w - 0.5f) * Size * (w / h), v = (0.5f - sy / h) * Size;
        return (Position + Right * u + Up * v, Forward);
    }

    /// <summary>Where a world point lands on a viewport w × h pixels.</summary>
    public Vector2 ToScreen(Vector3 p, float w, float h)
    {
        var d = p - Position;
        float u = Vector3.Dot(d, Right), v = Vector3.Dot(d, Up);
        return new Vector2((u / (Size * (w / h)) + 0.5f) * w, (0.5f - v / Size) * h);
    }

    /// <summary>Moves the target by a screen-space amount in pixels, along the ground.</summary>
    public void Pan(float dxPixels, float dyPixels, float h)
    {
        float perPixel = Size / h;
        var r = Right;
        var f = Vector3.Normalize(new Vector3(MathF.Sin(Rad(Yaw)), 0, MathF.Cos(Rad(Yaw)))); // screen down, along the ground
        // A pixel up the screen covers more ground the flatter the view.
        float depth = perPixel / MathF.Max(MathF.Sin(Rad(Pitch)), 0.2f);
        Target += new Vector2(r.X, r.Z) * dxPixels * perPixel + new Vector2(f.X, f.Z) * dyPixels * depth;
    }

    public void Clamp(int width, int height) =>
        Target = new Vector2(Math.Clamp(Target.X, 0, width), Math.Clamp(Target.Y, 0, height));

    /// <summary>
    /// The cell a ray first hits, given each cell's top in cells (ground or building), or null off the map.
    /// Walks the cells under the ray from where it is above the highest top until it drops below a cell's top.
    /// </summary>
    public static (int X, int Y)? Pick(Vector3 o, Vector3 d, int width, int height, Func<int, int, float> top, float maxTop)
    {
        if (d.Y >= 0) return null;
        // Start where the ray comes down through the highest top, end where it reaches the lowest ground.
        float t0 = (o.Y - (maxTop + 0.01f)) / -d.Y, t1 = (o.Y + 1f) / -d.Y;
        var a = o + d * t0;
        float hx = d.X, hz = d.Z;
        if (MathF.Abs(hx) < 1e-6f && MathF.Abs(hz) < 1e-6f)
        {
            int cx = (int)MathF.Floor(a.X), cz = (int)MathF.Floor(a.Z);
            return cx >= 0 && cz >= 0 && cx < width && cz < height ? (cx, cz) : null;
        }
        // Grid walk (Amanatides & Woo) over the ray's shadow on the ground.
        int x = (int)MathF.Floor(a.X), z = (int)MathF.Floor(a.Z);
        int sx = hx > 0 ? 1 : -1, sz = hz > 0 ? 1 : -1;
        float tdx = MathF.Abs(hx) < 1e-9f ? float.PositiveInfinity : MathF.Abs(1 / hx);
        float tdz = MathF.Abs(hz) < 1e-9f ? float.PositiveInfinity : MathF.Abs(1 / hz);
        float tmx = float.IsPositiveInfinity(tdx) ? float.PositiveInfinity : ((hx > 0 ? x + 1 - a.X : a.X - x) * tdx);
        float tmz = float.IsPositiveInfinity(tdz) ? float.PositiveInfinity : ((hz > 0 ? z + 1 - a.Z : a.Z - z) * tdz);
        float t = 0, span = t1 - t0;
        while (t <= span)
        {
            float exit = MathF.Min(tmx, tmz);
            if (x >= 0 && z >= 0 && x < width && z < height)
            {
                float yExit = a.Y + d.Y * exit;
                if (yExit <= top(x, z)) return (x, z);
            }
            if (tmx < tmz) { x += sx; t = tmx; tmx += tdx; }
            else { z += sz; t = tmz; tmz += tdz; }
        }
        return null;
    }
}
