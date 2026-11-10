namespace ToyCity.ClientModel.Graphics.Ground;

/// <summary>A colour to paint the ground's textures with: red, green and blue from 0 to 1, as the screen shows them (sRGB).</summary>
public readonly record struct Paint(float R, float G, float B)
{
    /// <summary>From 0xRRGGBB.</summary>
    public static Paint Of(uint rgb) => new((rgb >> 16 & 255) / 255f, (rgb >> 8 & 255) / 255f, (rgb & 255) / 255f);
    public static Paint operator *(Paint a, float k) => new(a.R * k, a.G * k, a.B * k);
    public static Paint operator +(Paint a, Paint b) => new(a.R + b.R, a.G + b.G, a.B + b.B);
    /// <summary>Part of the way from this colour to another.</summary>
    public Paint To(Paint b, float t) => new(R + (b.R - R) * t, G + (b.G - G) * t, B + (b.B - B) * t);
    /// <summary>The colour between several, <paramref name="t"/> from 0 (the first) to 1 (the last).</summary>
    public static Paint Ramp(float t, params Paint[] stops)
    {
        if (stops.Length == 1) return stops[0];
        float x = Math.Clamp(t, 0f, 1f) * (stops.Length - 1);
        int i = Math.Min((int)x, stops.Length - 2);
        return stops[i].To(stops[i + 1], x - i);
    }
    /// <summary>Greyer (below 1) or stronger (above 1) about its own brightness.</summary>
    public Paint Saturated(float k)
    {
        float l = 0.3f * R + 0.59f * G + 0.11f * B;
        return new(l + (R - l) * k, l + (G - l) * k, l + (B - l) * k);
    }
}

/// <summary>
/// What a ground texture is painted on (RC part 6): a square of pixels that wraps at its edges, each with a colour,
/// a height (0 the lowest of its relief to 1 the highest) and a roughness. Everything drawn on it wraps round, so
/// the finished texture tiles. <see cref="Finish"/> turns it into a <see cref="GroundTexture"/>, working out the
/// normals and the shading of its hollows from the heights.
/// </summary>
public sealed class GroundCanvas
{
    /// <summary>Pixels a side.</summary>
    public readonly int N;
    public readonly float[] R, G, B, H, Rough;

    public GroundCanvas(int n, Paint colour, float height = 0f, float rough = 0.9f)
    {
        N = n;
        R = new float[n * n]; G = new float[n * n]; B = new float[n * n]; H = new float[n * n]; Rough = new float[n * n];
        Array.Fill(R, colour.R); Array.Fill(G, colour.G); Array.Fill(B, colour.B); Array.Fill(H, height); Array.Fill(Rough, rough);
    }

    /// <summary>A pixel's index, wrapping round the edges.</summary>
    public int At(int x, int y)
    {
        x %= N; if (x < 0) x += N;
        y %= N; if (y < 0) y += N;
        return y * N + x;
    }

    public Paint Get(int i) => new(R[i], G[i], B[i]);
    public void Set(int i, Paint c) { R[i] = c.R; G[i] = c.G; B[i] = c.B; }
    /// <summary>Paints over a pixel, <paramref name="a"/> of the way to the colour.</summary>
    public void Mix(int i, Paint c, float a)
    {
        R[i] += (c.R - R[i]) * a; G[i] += (c.G - G[i]) * a; B[i] += (c.B - B[i]) * a;
    }
    /// <summary>Darkens or lightens a pixel.</summary>
    public void Scale(int i, float k) { R[i] *= k; G[i] *= k; B[i] *= k; }

    public delegate void PixelAt(int i, float u, float v);
    /// <summary>Every pixel in turn, with where its middle is (0 to 1 across and down).</summary>
    public void Each(PixelAt f)
    {
        float inv = 1f / N;
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
                f(y * N + x, (x + 0.5f) * inv, (y + 0.5f) * inv);
    }

    public delegate void StampAt(int i, float dx, float dy);
    /// <summary>
    /// Every pixel within <paramref name="radiusPx"/> pixels (as a square) of a point given 0 to 1 across the tile,
    /// wrapped round the edges, with how far its middle is from the point, in pixels.
    /// </summary>
    public void Around(float u, float v, float radiusPx, StampAt f)
    {
        float cx = u * N - 0.5f, cy = v * N - 0.5f;
        int x0 = (int)MathF.Floor(cx - radiusPx), x1 = (int)MathF.Ceiling(cx + radiusPx);
        int y0 = (int)MathF.Floor(cy - radiusPx), y1 = (int)MathF.Ceiling(cy + radiusPx);
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
                f(At(x, y), x - cx, y - cy);
    }

    /// <summary>
    /// A dab of colour at a height: a soft-edged disc <paramref name="radiusPx"/> pixels across (a single soft pixel
    /// when that's under one). It lands only where it's above what's there, or within <paramref name="under"/> of it,
    /// so things painted higher stay on top whatever order they're drawn in.
    /// </summary>
    public void Dab(float u, float v, float radiusPx, Paint c, float alpha, float height, float under = 0.02f, float rough = -1f)
    {
        float r = MathF.Max(radiusPx, 0.5f);
        Around(u, v, r + 1f, (i, dx, dy) =>
        {
            float d = MathF.Sqrt(dx * dx + dy * dy);
            float cover = Math.Clamp(r + 0.5f - d, 0f, 1f) * alpha;
            if (cover <= 0f || height < H[i] - under) return;
            Mix(i, c, cover);
            if (height > H[i]) H[i] += (height - H[i]) * cover;
            if (rough >= 0f) Rough[i] += (rough - Rough[i]) * cover;
        });
    }

    /// <summary>
    /// A blade or a stalk: a stroke from a root, <paramref name="lengthPx"/> long at <paramref name="angle"/>
    /// (radians, 0 east, turning south), bending by <paramref name="bend"/> radians along its length, as wide as
    /// <paramref name="widthPx"/> at the root and tapering to a point when <paramref name="taper"/> is 1. Its colour
    /// runs from <paramref name="root"/> to <paramref name="tip"/> and its height from h0 to h1.
    /// </summary>
    public void Blade(float u, float v, float angle, float lengthPx, float bend, float widthPx, Paint root, Paint tip,
        float h0, float h1, float alpha = 1f, float taper = 1f, float under = 0.03f)
    {
        int steps = Math.Max(2, (int)MathF.Ceiling(lengthPx * 1.5f));
        float x = u * N, y = v * N, inv = 1f / N;
        float step = lengthPx / steps;
        for (int s = 0; s <= steps; s++)
        {
            float t = s / (float)steps;
            float w = widthPx * (1f - taper * t);
            Dab(x * inv, y * inv, w * 0.5f, root.To(tip, t), alpha, h0 + (h1 - h0) * t, under);
            float a = angle + bend * t;
            x += MathF.Cos(a) * step; y += MathF.Sin(a) * step;
        }
    }

    /// <summary>
    /// A raised lump (a stone, a clod, a leaf): an ellipse rx by ry pixels turned by <paramref name="angle"/>,
    /// domed up from <paramref name="h0"/> at its edge to <paramref name="h1"/> at its middle, coloured by
    /// <paramref name="colour"/> (given how far out, 0 the middle to 1 the edge, and the angle round it).
    /// <paramref name="dome"/> shapes the top: 1 a round dome, towards 0 a flat top with steep sides.
    /// </summary>
    public void Lump(float u, float v, float rx, float ry, float angle, float h0, float h1, Func<float, float, Paint> colour,
        float dome = 1f, float under = 0.01f, float rough = -1f)
    {
        float ca = MathF.Cos(angle), sa = MathF.Sin(angle), reach = MathF.Max(rx, ry) + 1f;
        Around(u, v, reach, (i, dx, dy) =>
        {
            float lx = (dx * ca + dy * sa) / rx, ly = (-dx * sa + dy * ca) / ry;
            float d = MathF.Sqrt(lx * lx + ly * ly);
            if (d >= 1f) return;
            float top = MathF.Pow(1f - d * d, MathF.Max(0.05f, dome) * 0.5f);
            float h = h0 + (h1 - h0) * top;
            if (h < H[i] - under) return;
            // A pixel of soft edge so lumps don't alias.
            float edge = Math.Clamp((1f - d) * MathF.Min(rx, ry), 0f, 1f);
            Mix(i, colour(d, MathF.Atan2(ly, lx)), edge);
            if (h > H[i]) H[i] += (h - H[i]) * edge;
            if (rough >= 0f) Rough[i] += (rough - Rough[i]) * edge;
        });
    }

    /// <summary>The heights smoothed over <paramref name="radius"/> pixels each way (wrapping), for telling hollows from tops.</summary>
    public float[] Blurred(float[] src, int radius)
    {
        var tmp = new float[N * N];
        var dst = new float[N * N];
        float k = 1f / (2 * radius + 1);
        for (int y = 0; y < N; y++)
        {
            float sum = 0;
            for (int d = -radius; d <= radius; d++) sum += src[At(d, y)];
            for (int x = 0; x < N; x++)
            {
                tmp[y * N + x] = sum * k;
                sum += src[At(x + radius + 1, y)] - src[At(x - radius, y)];
            }
        }
        for (int x = 0; x < N; x++)
        {
            float sum = 0;
            for (int d = -radius; d <= radius; d++) sum += tmp[At(x, d)];
            for (int y = 0; y < N; y++)
            {
                dst[y * N + x] = sum * k;
                sum += tmp[At(x, y + radius + 1)] - tmp[At(x, y - radius)];
            }
        }
        return dst;
    }

    /// <summary>
    /// Darkens the hollows and lightens the tops a little, as skylight does: each pixel by how far it is below or
    /// above the ground around it. <paramref name="strength"/> about 0.3 is plenty.
    /// </summary>
    public void ShadeHollows(float strength, int radius = 0)
    {
        var around = Blurred(H, radius > 0 ? radius : Math.Max(2, N / 96));
        for (int i = 0; i < N * N; i++)
            Scale(i, Math.Clamp(1f + (H[i] - around[i]) * strength * 4f, 1f - strength, 1f + strength * 0.5f));
    }

    /// <summary>
    /// The finished texture: colour with the height in alpha; the normal from the heights (a height of 1 standing
    /// <paramref name="relief"/> metres over a tile <paramref name="metres"/> across), the roughness, and the
    /// hollows' ambient occlusion. <paramref name="soften"/> smooths the heights over that many pixels (at 512 a
    /// side, scaled for other sizes) before the normals are taken, for a surface of fine things like blades whose
    /// every edge would otherwise glint.
    /// </summary>
    public GroundTexture Finish(string id, string name, float metres, float relief, float occlusion = 0.6f, int soften = 0)
    {
        int n = N;
        var albedo = new byte[n * n * 4];
        var detail = new byte[n * n * 4];
        float lo = float.MaxValue, hi = float.MinValue;
        foreach (float h in H) { lo = MathF.Min(lo, h); hi = MathF.Max(hi, h); }
        float span = MathF.Max(1e-6f, hi - lo);
        // Metres of height per unit of H over metres per pixel: the slope one unit of H a pixel makes.
        float scale = relief / (metres / n);
        // A hollow is where the ground just around a pixel stands lower than the ground further around it.
        var near = Blurred(H, Math.Max(1, n / 256));
        var around = Blurred(H, Math.Max(3, n / 40));
        int blur = soften * n / 512;
        var shape = blur > 0 ? Blurred(H, blur) : H;
        double lr = 0, lg = 0, lb = 0, rough = 0;
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                int i = y * n + x, o = i * 4;
                float h = (H[i] - lo) / span;
                albedo[o] = Byte(R[i]); albedo[o + 1] = Byte(G[i]); albedo[o + 2] = Byte(B[i]); albedo[o + 3] = Byte(h);
                lr += Linear(albedo[o]); lg += Linear(albedo[o + 1]); lb += Linear(albedo[o + 2]);
                // The slope from the pixels either side (wrapping), east and south.
                float sx = (shape[At(x + 1, y)] - shape[At(x - 1, y)]) * 0.5f * scale;
                float sy = (shape[At(x, y + 1)] - shape[At(x, y - 1)]) * 0.5f * scale;
                float inv = 1f / MathF.Sqrt(sx * sx + sy * sy + 1f);
                detail[o] = Byte(0.5f - sx * inv * 0.5f);
                detail[o + 1] = Byte(0.5f - sy * inv * 0.5f);
                detail[o + 2] = Byte(Rough[i]);
                rough += Rough[i];
                float hollow = Math.Clamp((around[i] - near[i]) / span * 5f, 0f, 1f);
                detail[o + 3] = Byte(1f - hollow * occlusion);
            }
        double count = (double)n * n;
        uint average = (uint)(Srgb(lr / count) << 16 | Srgb(lg / count) << 8 | Srgb(lb / count));
        return new GroundTexture { Id = id, Name = name, Size = n, Metres = metres, Relief = relief * span, Albedo = albedo, Detail = detail, Average = average, Roughness = (float)(rough / count) };
    }

    static byte Byte(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);
    /// <summary>An sRGB byte as linear light, 0 to 1.</summary>
    public static double Linear(byte b)
    {
        double c = b / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }
    /// <summary>Linear light, 0 to 1, as an sRGB byte.</summary>
    public static int Srgb(double l)
    {
        double c = l <= 0.0031308 ? l * 12.92 : 1.055 * Math.Pow(l, 1 / 2.4) - 0.055;
        return Math.Clamp((int)Math.Round(c * 255), 0, 255);
    }
}

/// <summary>
/// One of the ground's materials as a texture set (RC part 6), a square that tiles. Rows run from the north (top)
/// to the south, columns from west to east, so on level ground u is east and v is south.
/// </summary>
public sealed class GroundTexture
{
    public string Id = "", Name = "";
    /// <summary>Pixels a side.</summary>
    public int Size;
    /// <summary>How many metres one copy covers.</summary>
    public float Metres;
    /// <summary>How many metres its relief stands, lowest to highest.</summary>
    public float Relief;
    /// <summary>RGBA by row: the colour (sRGB), and in alpha the height, 0 the lowest to 255 the highest.</summary>
    public byte[] Albedo = Array.Empty<byte>();
    /// <summary>
    /// RGBA by row: red and green the normal's lean east and south (128 none, as a tangent-space normal map with
    /// green pointing down the picture), blue the roughness, alpha the ambient occlusion (255 open, less in hollows).
    /// </summary>
    public byte[] Detail = Array.Empty<byte>();
    /// <summary>The colour it averages to from far off, as 0xRRGGBB: the mean of its light, not of its sRGB numbers.</summary>
    public uint Average;
    /// <summary>Its average roughness.</summary>
    public float Roughness;
}
