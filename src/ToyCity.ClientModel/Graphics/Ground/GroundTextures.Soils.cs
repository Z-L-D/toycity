using static ToyCity.ClientModel.Graphics.Ground.GroundNoise;

namespace ToyCity.ClientModel.Graphics.Ground;

public static partial class GroundTextures
{
    /// <summary>
    /// Bare earth: crumbly soil in clods, lighter where it's dried on top, with small stones and the odd root or
    /// dead stalk. Tilled soil is darker, raked into furrows running east-west every 30 cm.
    /// </summary>
    internal static GroundTexture Earth(int n, float metres, bool tilled)
    {
        float px = n / metres;
        Paint dark = tilled ? Paint.Of(0x3F3124) : Paint.Of(0x4D3D2A), light = tilled ? Paint.Of(0x6E5A44) : Paint.Of(0x8A7357);
        var c = new GroundCanvas(n, dark, 0f, 0.95f);
        int furrows = (int)MathF.Round(metres / 0.3f);
        c.Each((i, u, v) =>
        {
            float big = Fbm(u, v, 4, 4, 41), crumb = Fbm(u, v, 48, 3, 43);
            var cl = Cells(u, v, (int)(metres * 9), 45, 0.9f);
            float clod = Smooth(0.0f, 0.35f, cl.F2 - cl.F1);
            float h = 0.3f + 0.3f * big + 0.08f * clod + 0.14f * crumb;
            if (tilled)
            {
                float f = 0.5f + 0.5f * MathF.Sin((v * furrows + 0.08f * Fbm(u, v, 6, 2, 47)) * MathF.Tau);
                h = 0.15f + 0.55f * f + 0.15f * clod + 0.08f * crumb;
            }
            c.H[i] = h;
            float dryTop = Math.Clamp(h * 1.2f - 0.15f + 0.3f * big, 0f, 1f);
            c.Set(i, dark.To(light, dryTop) * (0.9f + 0.2f * ((cl.Id & 255) / 255f)));
        });
        var r = new Scatter(tilled ? 2002 : 2001);
        for (int k = 0; k < (int)(metres * metres * 30); k++)
        {
            float u = r.Next(), v = r.Next(), s = r.Range(0.006f, 0.022f) * px;
            var st = Paint.Of(0x8B8175) * r.Range(0.7f, 1.2f);
            c.Lump(u, v, s, s * r.Range(0.6f, 1f), r.Range(0, MathF.Tau), 0.55f, 0.85f, (d, _) => st * (1.1f - 0.35f * d), 0.7f);
        }
        if (!tilled)
            for (int k = 0; k < (int)(metres * metres * 4); k++)
            {
                float u = r.Next(), v = r.Next();
                var stalk = Paint.Of(0x9C8A62) * r.Range(0.7f, 1.1f);
                c.Blade(u, v, r.Range(0, MathF.Tau), r.Range(0.04f, 0.12f) * px, r.Range(-0.4f, 0.4f), MathF.Max(1f, 0.008f * px), stalk * 0.8f, stalk, 0.6f, 0.7f, 0.85f, 0.2f);
            }
        c.ShadeHollows(0.3f);
        return c.Finish("", "", metres, 0.05f);
    }

    /// <summary>Mud: wet soil, dark and smooth, puddled in the hollows with a sheen, rutted and pocked by feet.</summary>
    internal static GroundTexture Mud(int n, float metres)
    {
        float px = n / metres;
        Paint wet = Paint.Of(0x4E4231), drier = Paint.Of(0x7A6850), water = Paint.Of(0x3A362E);
        var c = new GroundCanvas(n, wet, 0f, 0.6f);
        c.Each((i, u, v) =>
        {
            float big = Fbm(u, v, 3, 4, 51), mid = Fbm(u, v, 14, 3, 53);
            float h = 0.45f + 0.35f * big + 0.12f * mid;
            c.H[i] = h;
            float puddle = Smooth(0.36f, 0.28f, h);
            c.Set(i, wet.To(drier, Smooth(0.45f, 0.75f, h + 0.1f * mid)).To(water, puddle * 0.8f) * (0.92f + 0.16f * Rand((int)(u * n), (int)(v * n), 55)));
            c.Rough[i] = Lerp(0.75f, 0.08f, puddle);
            if (puddle > 0.5f) c.H[i] = 0.3f;  // water lies flat
        });
        var r = new Scatter(2101);
        for (int k = 0; k < (int)(metres * metres * 1.5f); k++)
        {
            float u = r.Next(), v = r.Next(), s = r.Range(0.04f, 0.07f) * px;
            c.Lump(u, v, s, s * 0.6f, r.Range(0, MathF.Tau), 0.40f, 0.33f, (d, _) => wet * 0.85f, 1f, 1f, 0.3f);  // footprints: dents
        }
        c.ShadeHollows(0.2f);
        return c.Finish("", "", metres, 0.04f);
    }

    /// <summary>Sand: fine grains in small wind ripples, lighter on the crests, with a scatter of darker grains and shell bits.</summary>
    internal static GroundTexture Sand(int n, float metres)
    {
        float px = n / metres;
        Paint low = Paint.Of(0xA3926C), high = Paint.Of(0xCDBF98);
        var c = new GroundCanvas(n, low, 0f, 0.9f);
        int ripples = (int)MathF.Round(metres / 0.12f);
        c.Each((i, u, v) =>
        {
            float warp = 0.05f * Fbm(u, v, 3, 3, 61);
            float ph = (u + 0.25f * v + warp) * ripples;
            float rip = 1 - MathF.Abs(2 * (ph - MathF.Floor(ph)) - 1);
            rip = MathF.Pow(rip, 1.6f) * (0.6f + 0.4f * Fbm(u, v, 8, 2, 63));
            float big = Fbm(u, v, 4, 3, 65);
            c.H[i] = 0.3f + 0.4f * rip + 0.2f * big;
            float grain = Rand((int)(u * n), (int)(v * n), 67);
            c.Set(i, low.To(high, 0.35f + 0.4f * rip + 0.25f * big) * (0.9f + 0.18f * grain));
        });
        var r = new Scatter(2201);
        for (int k = 0; k < n * n / 60; k++)
        {
            float u = r.Next(), v = r.Next();
            c.Dab(u, v, 0.5f, r.Chance(0.5f) ? Paint.Of(0x6E6252) : Paint.Of(0xE2D8C2), 0.7f, 0.5f, 1f);
        }
        c.ShadeHollows(0.18f);
        return c.Finish("", "", metres, 0.012f, soften: 1);
    }

    /// <summary>
    /// Stones packed on fines. River gravel is rounded pebbles of many rocks, a few to ten centimetres; scree is
    /// sharp, angular broken rock of one grey, larger, loosely sorted.
    /// </summary>
    internal static GroundTexture Stones(int n, float metres, bool scree)
    {
        float px = n / metres;
        Paint fines = scree ? Paint.Of(0x5E5953) : Paint.Of(0x5C5446);
        var c = new GroundCanvas(n, fines, 0f, 0.9f);
        c.Each((i, u, v) => { c.H[i] = 0.1f + 0.1f * Fbm(u, v, 20, 3, 71); c.Scale(i, 0.85f + 0.3f * Rand((int)(u * n), (int)(v * n), 73)); });
        var r = new Scatter(scree ? 2302 : 2301);
        Paint[] river = { Paint.Of(0x8E877A), Paint.Of(0xA59886), Paint.Of(0x6F6A62), Paint.Of(0x9A8B78), Paint.Of(0x7E7466), Paint.Of(0xB4AB9C), Paint.Of(0x8A6F5C) };
        int count = (int)(metres * metres * (scree ? 220 : 420));
        for (int k = 0; k < count; k++)
        {
            float u = r.Next(), v = r.Next();
            float size = scree ? (0.02f + 0.14f * r.Next() * r.Next()) : (0.012f + 0.05f * r.Next() * r.Next());
            float rx = size * px, ry = rx * r.Range(0.55f, 1f);
            var col = scree ? Paint.Of(0x857D72) * r.Range(0.75f, 1.2f) : river[r.Int(river.Length)] * r.Range(0.9f, 1.2f);
            float top = 0.3f + 0.6f * r.Next(), facet = r.Range(0, MathF.Tau);
            c.Lump(u, v, rx, ry, r.Range(0, MathF.Tau), 0.2f, top, (d, a) =>
            {
                // Scree has flat facets lit and shaded; pebbles are smooth with a lighter top.
                float lit = scree ? (MathF.Cos(a * 2 + facet) > 0 ? 1.12f : 0.82f) : 1.1f - 0.3f * d * d;
                return col * lit;
            }, scree ? 0.25f : 1.2f, 0.01f, scree ? 0.85f : 0.65f);
        }
        c.ShadeHollows(0.35f);
        return c.Finish("", "", metres, scree ? 0.12f : 0.05f);
    }
}
