using System.Numerics;

namespace ToyCity.ClientModel.Tests;

/// <summary>The classic camera: a click lands on the cell drawn under the cursor at every rotation, zoom and view.</summary>
public class ClassicViewTests
{
    const int W = 48, H = 40;
    const float Vw = 1600, Vh = 900;

    static float[] Hills(int seed)
    {
        var r = new Random(seed);
        var tops = new float[W * H];
        for (int i = 0; i < tops.Length; i++) tops[i] = r.Next(0, 6) * ClassicView.HeightStep;
        // A few tall towers to hide things behind.
        for (int k = 0; k < 20; k++) tops[r.Next(tops.Length)] = r.Next(4, 20) * 0.25f;
        return tops;
    }

    static IEnumerable<ClassicView> Views()
    {
        foreach (var mode in Enum.GetValues<ViewMode>())
            for (int rot = 0; rot < 4; rot++)
                for (int zoom = 0; zoom < ClassicView.ZoomSizes.Length; zoom++)
                    yield return new ClassicView { Target = new Vector2(W / 2f, H / 2f), Rotation = rot, Zoom = zoom, Mode = mode };
    }

    [Fact]
    public void OnFlatGroundTheCellUnderTheCursorIsTheCellClicked()
    {
        foreach (var v in Views())
            for (int y = 0; y < H; y += 3)
                for (int x = 0; x < W; x += 3)
                {
                    var p = v.ToScreen(new Vector3(x + 0.5f, 0, y + 0.5f), Vw, Vh);
                    var (o, d) = v.Ray(p.X, p.Y, Vw, Vh);
                    Assert.Equal((x, y), ClassicView.Pick(o, d, W, H, (_, _) => 0, 0));
                }
    }

    [Fact]
    public void OnHillsAClickFindsTheCellOrWhateverStandsInFrontOfIt()
    {
        var tops = Hills(7);
        float max = tops.Max();
        float Top(int x, int y) => tops[y * W + x];
        foreach (var v in Views())
        {
            int same = 0, total = 0;
            for (int y = 0; y < H; y += 2)
                for (int x = 0; x < W; x += 2)
                {
                    var p = v.ToScreen(new Vector3(x + 0.5f, Top(x, y), y + 0.5f), Vw, Vh);
                    var (o, d) = v.Ray(p.X, p.Y, Vw, Vh);
                    var hit = ClassicView.Pick(o, d, W, H, Top, max);
                    Assert.NotNull(hit);
                    total++;
                    if (hit == (x, y)) { same++; continue; }
                    // Something nearer the camera hides the cell: it must be in front, and the ray must reach its top.
                    var (hx, hy) = hit!.Value;
                    var toHit = new Vector3(hx + 0.5f - x - 0.5f, 0, hy + 0.5f - y - 0.5f);
                    Assert.True(Vector3.Dot(toHit, v.Forward) < 0.01f, $"{v.Mode} r{v.Rotation} z{v.Zoom}: ({x},{y}) gave ({hx},{hy}) behind it");
                    Assert.True(Top(hx, hy) > Top(x, y), $"{v.Mode} r{v.Rotation}: ({x},{y}) hidden by lower ({hx},{hy})");
                }
            // The low side view sees mostly the fronts of hills, so fewer cells show their own tops.
            Assert.True(same > total * (v.Mode == ViewMode.Side ? 0.35 : 0.6), $"{v.Mode} r{v.Rotation} z{v.Zoom}: only {same} of {total}");
        }
    }

    [Fact]
    public void FourRotationsGoRoundTheCompassAndPanningFollowsTheScreen()
    {
        var v = new ClassicView { Target = new Vector2(20, 20) };
        var seen = new HashSet<(int, int)>();
        for (int r = 0; r < 4; r++)
        {
            v.Rotation = r;
            var f = v.Forward;
            seen.Add((Math.Sign(f.X), Math.Sign(f.Z)));
            // Dragging the map left moves the view right: the screen centre now shows what was to its right.
            var before = v.Target;
            var right = v.ToScreen(new Vector3(before.X, 0, before.Y) + v.Right * 4, Vw, Vh);
            v.Pan(right.X - Vw / 2, 0, Vh);
            Assert.True(Vector2.Distance(v.Target, before + new Vector2(v.Right.X, v.Right.Z) * 4) < 0.01f);
            v.Target = before;
        }
        Assert.Equal(4, seen.Count);
        v.Rotate(-1);
        Assert.Equal(2, v.Rotation);
        v.Zoom = 4;
        v.ZoomBy(3);
        Assert.Equal(4, v.Zoom);
    }
}
