using Godot;
using ToyCity.ClientModel.Voxels.Assets;

namespace ToyCity.Godot;

/// <summary>
/// The photo importer's left half: the photo, with four corners to drag onto the facade's corners. Inside them it
/// draws the facade's storey lines (every 3 m) and metre ticks along the bottom, bent to the photo's perspective,
/// so it's plain when the size in metres is right: the lines fall on the floors.
/// </summary>
public partial class PhotoCanvas : Control
{
    public Texture2D? Texture;
    public Vector2I PhotoSize = new(1, 1);
    public (double X, double Y)[] Corners = new (double, double)[4];
    public double WidthMetres = 12, HeightMetres = 9;
    /// <summary>A corner moved, and when the drag ends.</summary>
    public event Action? Changed, Released;
    int _drag = -1;

    static readonly Color Paper = new(0.08f, 0.09f, 0.12f), Edge = new(1f, 0.82f, 0.3f), Guide = new(0.55f, 0.85f, 1f, 0.75f), Tick = new(1, 1, 1, 0.7f);
    static readonly string[] Names = { "top left", "top right", "bottom right", "bottom left" };

    /// <summary>Where the photo sits on the canvas, as big as fits.</summary>
    Rect2 Fit()
    {
        float s = Math.Min(Size.X / PhotoSize.X, Size.Y / PhotoSize.Y);
        var size = new Vector2(PhotoSize.X * s, PhotoSize.Y * s);
        return new Rect2((Size - size) / 2, size);
    }

    Vector2 ToPx((double X, double Y) p)
    {
        var f = Fit();
        return f.Position + new Vector2((float)(p.X / PhotoSize.X * f.Size.X), (float)(p.Y / PhotoSize.Y * f.Size.Y));
    }

    (double X, double Y) FromPx(Vector2 px)
    {
        var f = Fit();
        return (Math.Clamp((px.X - f.Position.X) / f.Size.X * PhotoSize.X, 0, PhotoSize.X), Math.Clamp((px.Y - f.Position.Y) / f.Size.Y * PhotoSize.Y, 0, PhotoSize.Y));
    }

    public override void _Draw()
    {
        DrawRect(new Rect2(Vector2.Zero, Size), Paper);
        var font = ThemeDB.FallbackFont;
        if (Texture == null)
        {
            DrawString(font, new Vector2(16, 24), "Open a photo of a building, taken as straight on as you can", HorizontalAlignment.Left, -1, 13, Colors.White);
            return;
        }
        DrawTextureRect(Texture, Fit(), false);
        var px = Corners.Select(ToPx).ToArray();
        // Storey lines and metre ticks, through the same perspective as the corners.
        if (PhotoImport.Homography(new[] { (0.0, 0.0), (1.0, 0.0), (1.0, 1.0), (0.0, 1.0) }, Corners) is { } h && WidthMetres > 0 && HeightMetres > 0)
        {
            Vector2 At(double u, double v) => ToPx(PhotoImport.Map(h, u, v));
            for (double m = 3; m < HeightMetres - 0.01; m += 3)
            {
                double v = 1 - m / HeightMetres;
                DrawDashedLine(At(0, v), At(1, v), Guide, 1.5f, 6);
                DrawString(font, At(1, v) + new Vector2(5, 4), $"{m:0} m", HorizontalAlignment.Left, -1, 11, Guide);
            }
            for (double m = 0; m <= WidthMetres + 0.01; m += 1)
            {
                var a = At(m / WidthMetres, 1);
                DrawLine(a, a + new Vector2(0, Math.Abs(m % 5) < 0.01 ? 9 : 5), Tick, 1.5f);
            }
            // A person 1.8 m tall standing at the bottom-left corner, in the photo's perspective.
            var foot = At(0.4 / WidthMetres, 1);
            var head = At(0.4 / WidthMetres, 1 - 1.8 / HeightMetres);
            float w = Math.Max(3, (At(0.6 / WidthMetres, 1) - At(0.2 / WidthMetres, 1)).Length());
            DrawRect(new Rect2(foot.X - w / 2, head.Y + (foot.Y - head.Y) * 0.18f, w, (foot.Y - head.Y) * 0.82f), new Color(1, 1, 1, 0.45f));
            DrawCircle(new Vector2(head.X, head.Y + (foot.Y - head.Y) * 0.09f), (foot.Y - head.Y) * 0.09f, new Color(1, 1, 1, 0.45f));
        }
        for (int i = 0; i < 4; i++) DrawLine(px[i], px[(i + 1) % 4], Edge, 2);
        for (int i = 0; i < 4; i++)
        {
            DrawCircle(px[i], i == _drag ? 8 : 6, Edge);
            DrawCircle(px[i], i == _drag ? 4 : 3, Paper);
            var off = new Vector2(i is 0 or 3 ? -70 : 10, i < 2 ? -8 : 18);
            DrawString(font, px[i] + off, Names[i], HorizontalAlignment.Left, -1, 11, Edge);
        }
    }

    public override void _GuiInput(InputEvent e)
    {
        if (Texture == null) return;
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left } mb)
        {
            if (mb.Pressed)
            {
                _drag = -1;
                float best = 24;
                for (int i = 0; i < 4; i++)
                {
                    float d = ToPx(Corners[i]).DistanceTo(mb.Position);
                    if (d < best) { best = d; _drag = i; }
                }
            }
            else if (_drag >= 0) { _drag = -1; Released?.Invoke(); }
            QueueRedraw();
            AcceptEvent();
        }
        else if (e is InputEventMouseMotion mm && _drag >= 0)
        {
            Corners[_drag] = FromPx(mm.Position);
            Changed?.Invoke();
            QueueRedraw();
            AcceptEvent();
        }
    }
}

/// <summary>
/// The photo importer's right half: the facade as it will be built, one pixel a voxel in palette colours, the
/// parts set back (windows, doors) shaded darker and those standing out lighter, with storey lines every 3 m, a person 1.8 m tall and a 1 m bar for scale.
/// </summary>
public partial class FacadePreview : Control
{
    ImageTexture? _texture;
    double _w = 1, _h = 1;
    static readonly Color Paper = new(0.08f, 0.09f, 0.12f), Guide = new(0.55f, 0.85f, 1f, 0.8f);

    public void Show(Facade f, uint[] palette, double widthMetres, double heightMetres)
    {
        var img = Image.CreateEmpty(f.Width, f.Height, false, Image.Format.Rgba8);
        for (int row = 0; row < f.Height; row++)
            for (int x = 0; x < f.Width; x++)
            {
                byte g = f[x, row];
                var (r, gg, b, _) = ToyCity.ClientModel.Voxels.VoxModel.Unpack(palette[f.Entry[g]]);
                var c = Color.Color8(r, gg, b);
                double off = f.OffsetAt(x, row);
                img.SetPixel(x, row, off < -0.01 ? c.Darkened((float)Math.Min(0.6, 0.25 + off * -0.8)) : off > 0.01 ? c.Lightened((float)Math.Min(0.5, 0.15 + off * 0.8)) : c);
            }
        if (_texture == null || _texture.GetSize() != new Vector2(f.Width, f.Height)) _texture = ImageTexture.CreateFromImage(img);
        else _texture.Update(img);
        (_w, _h) = (widthMetres, heightMetres);
        QueueRedraw();
    }

    public override void _Draw()
    {
        DrawRect(new Rect2(Vector2.Zero, Size), Paper);
        if (_texture == null) return;
        var font = ThemeDB.FallbackFont;
        const float pad = 22;
        float s = (float)Math.Min((Size.X - 2 * pad) / _w, (Size.Y - 2 * pad) / _h);   // pixels a metre
        var size = new Vector2((float)(_w * s), (float)(_h * s));
        var at = new Vector2((Size.X - size.X) / 2, pad + (Size.Y - 2 * pad - size.Y) / 2);
        DrawTextureRect(_texture, new Rect2(at, size), false);
        float ground = at.Y + size.Y;
        for (double m = 3; m < _h - 0.01; m += 3)
        {
            float y = ground - (float)(m * s);
            DrawDashedLine(new Vector2(at.X - 6, y), new Vector2(at.X + size.X + 6, y), Guide, 1.2f, 5);
            DrawString(font, new Vector2(at.X + size.X + 8, y + 4), $"storey {m / 3:0}", HorizontalAlignment.Left, -1, 10, Guide);
        }
        // A person, beside the facade's left edge.
        float ph = 1.8f * s, pw = Math.Max(3, 0.45f * s), px = at.X - pw - 4;
        DrawRect(new Rect2(px, ground - ph * 0.82f, pw, ph * 0.82f), new Color(1, 1, 1, 0.5f));
        DrawCircle(new Vector2(px + pw / 2, ground - ph * 0.91f), ph * 0.09f, new Color(1, 1, 1, 0.5f));
        var bar = new Vector2(at.X, ground + 12);
        DrawLine(bar, bar + new Vector2(s, 0), Colors.White, 2);
        DrawString(font, bar + new Vector2(s + 4, 4), "1 m", HorizontalAlignment.Left, -1, 10, Colors.White);
        DrawString(font, new Vector2(at.X, at.Y - 6), $"{_w:0.##} × {_h:0.##} m", HorizontalAlignment.Left, -1, 11, Colors.White);
    }
}
