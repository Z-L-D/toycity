using static ToyCity.ClientModel.Graphics.Ground.GroundNoise;

namespace ToyCity.ClientModel.Graphics.Ground;

public static partial class GroundTextures
{
    /// <summary>
    /// Rock faces, each as its kind breaks (the sim's four, Geology.cs, and a plain grey bedrock for ground of no
    /// known kind). Shale is blue-grey, in thin beds split into flakes. Sandstone is tan and rust in thick cross-bedded
    /// layers, worn smooth. Limestone is pale grey-cream in blocks cut by joints, pitted, streaked darker. Granite is
    /// pinkish grey, speckled with crystals, broken by few rounded joints. The beds run east-west across the texture,
    /// so on a cliff they lie level. Lichen grows on all of them.
    /// </summary>
    internal static GroundTexture Rock(int n, float metres, string kind)
    {
        float px = n / metres;
        var (a, b, beds, joints, seed) = kind switch
        {
            "shale" => (Paint.Of(0x4E5059), Paint.Of(0x777A82), metres / 0.15f, 5, 81),
            "sandstone" => (Paint.Of(0x84684A), Paint.Of(0xB89B72), metres / 0.6f, 3, 83),
            "limestone" => (Paint.Of(0x9A9486), Paint.Of(0xC9C4B5), metres / 1.0f, 6, 85),
            "granite" => (Paint.Of(0x786663), Paint.Of(0xA8958F), 0f, 3, 87),
            _ => (Paint.Of(0x625E58), Paint.Of(0x8E8981), metres / 0.8f, 4, 89),
        };
        var c = new GroundCanvas(n, a, 0f, 0.85f);
        int bedCount = Math.Max(1, (int)MathF.Round(beds));
        c.Each((i, u, v) =>
        {
            float warp = 0.04f * Fbm(u, v, 2, 3, seed);
            float big = Fbm(u, v, 3, 5, seed + 1), fine = Fbm(u, v, 24, 3, seed + 2);
            var block = Cells(u + 0.05f * Fbm(u, v, 5, 3, seed + 3), v + 0.04f * Fbm(u, v, 5, 3, seed + 10), joints, seed + 4, 0.8f, kind == "granite" ? 1f : 1.8f);
            // Joints are thin and wander, and only some run far: most die out part way.
            float reach = Smooth(0.05f, 0.35f, Fbm(u, v, 4, 3, seed + 11) + 0.1f);
            float crack = 1f - (1f - Smooth(0.0f, 0.025f, block.F2 - block.F1)) * reach;
            float h = 0.45f + 0.3f * big + 0.12f * fine + 0.1f * ((block.Id & 255) / 255f);
            float tone = 0.5f + 0.5f * big;
            if (bedCount > 1)
            {
                float bed = (v + warp) * bedCount, f = bed - MathF.Floor(bed);
                int which = (int)MathF.Floor(bed);
                float bedTone = Rand(which, 0, seed + 5);
                // Each bed stands out or back a little, with a dark parting line between.
                h += 0.18f * (bedTone - 0.5f) - 0.25f * Smooth(0.10f, 0.0f, MathF.Min(f, 1 - f) * bedCount / metres * 2);
                tone = Lerp(tone, bedTone, kind == "sandstone" ? 0.7f : 0.4f);
                if (kind == "shale") h += 0.12f * MathF.Abs(Perlin(u * 40, bed * 2, 40, seed + 6));
            }
            h *= 0.7f + 0.3f * crack;
            c.H[i] = h;
            var col = a.To(b, Math.Clamp(tone + 0.15f * fine, 0f, 1f)) * (0.62f + 0.38f * crack) * (0.92f + 0.16f * ((block.Id & 255) / 255f));
            if (kind == "granite")
            {
                float g = Rand((int)(u * n), (int)(v * n), seed + 7);
                if (g < 0.07f) col = Paint.Of(0x2E2A2A); else if (g > 0.93f) col = Paint.Of(0xE3D6D0); else if (g > 0.86f) col = Paint.Of(0xB98F82);
            }
            if (kind == "limestone")
            {
                float pit = Cells(u, v, (int)(metres * 14), seed + 8, 1f).F1;
                if (pit < 0.12f) { col *= 0.7f; c.H[i] -= 0.05f; }
                col *= 1f - 0.18f * Smooth(0.2f, 0.7f, Fbm(u * 6, v * 0.5f, 6, 3, seed + 9));  // rain streaks run down
            }
            c.Set(i, col);
        });
        // Lichen in pale green-grey and orange patches on the tops.
        var r = new Scatter(seed);
        for (int k = 0; k < (int)(metres * metres * 2); k++)
        {
            float u = r.Next(), v = r.Next();
            var col = r.Chance(0.7f) ? Paint.Of(0x9DA38A) : Paint.Of(0xB4864A);
            float s = r.Range(0.02f, 0.08f) * px;
            for (int d = 0; d < 6; d++)
                c.Dab(u + r.Range(-1, 1) * s / n, v + r.Range(-1, 1) * s / n, s * r.Range(0.3f, 0.7f), col, 0.35f, 0.6f, 0.3f);
        }
        c.ShadeHollows(0.35f);
        return c.Finish("", "", metres, kind == "shale" ? 0.15f : 0.3f);
    }

    /// <summary>Snow: wind-smoothed drifts in soft scallops, blue in their hollows, with a crust that sparkles.</summary>
    internal static GroundTexture Snow(int n, float metres)
    {
        Paint shade = Paint.Of(0xC9D3E2), lit = Paint.Of(0xF2F4F7);
        var c = new GroundCanvas(n, lit, 0f, 0.55f);
        c.Each((i, u, v) =>
        {
            float big = Fbm(u, v, 3, 4, 91);
            float drift = Ridged(u + 0.05f * big, v, 4, 3, 93);
            float h = 0.45f + 0.3f * big + 0.15f * drift;
            c.H[i] = h;
            float spark = Rand((int)(u * n), (int)(v * n), 95);
            c.Set(i, shade.To(lit, 0.55f + 0.45f * big) * (spark > 0.995f ? 1.08f : 1f));
            c.Rough[i] = spark > 0.99f ? 0.2f : 0.55f;
        });
        c.ShadeHollows(0.1f);
        return c.Finish("", "", metres, 0.08f, soften: 2);
    }

    /// <summary>
    /// A wood's floor. Under broadleaves, fallen leaves in browns and russets over dark humus, with twigs. Under
    /// conifers, a mat of needles in rust and grey-brown, with cones and bits of bark.
    /// </summary>
    internal static GroundTexture Litter(int n, float metres, bool needles)
    {
        float px = n / metres;
        Paint humus = Paint.Of(0x2E2519);
        var c = new GroundCanvas(n, humus, 0f, 0.9f);
        c.Each((i, u, v) => { c.H[i] = 0.1f * Fbm(u, v, 6, 3, 101); });
        var r = new Scatter(needles ? 2602 : 2601);
        if (needles)
        {
            for (int k = 0; k < (int)(metres * metres * 2400); k++)
            {
                float u = r.Next(), v = r.Next();
                var col = Paint.Ramp(r.Next(), Paint.Of(0x4E3624), Paint.Of(0x74503A), Paint.Of(0x8A6E52)) * r.Range(0.8f, 1.1f);
                float h = r.Range(0.2f, 0.8f);
                c.Blade(u, v, r.Range(0, MathF.Tau), r.Range(0.03f, 0.05f) * px, r.Range(-0.15f, 0.15f), MathF.Max(1f, 0.003f * px), col, col * 1.1f, h, h, 0.9f, 0f, 0.05f);
            }
            for (int k = 0; k < (int)(metres * metres * 1.2f); k++)
            {
                float u = r.Next(), v = r.Next(), s = r.Range(0.025f, 0.04f) * px;
                c.Lump(u, v, s, s * 0.55f, r.Range(0, MathF.Tau), 0.7f, 1f, (d, a) => Paint.Of(0x6A4A30) * (MathF.Sin(a * 6 + d * 10) > 0 ? 1.1f : 0.75f), 1f);
            }
        }
        else
        {
            Paint[] leaves = { Paint.Of(0x82593A), Paint.Of(0x957046), Paint.Of(0x6A5034), Paint.Of(0xA67A48), Paint.Of(0x7A6844), Paint.Of(0x8E5236) };
            for (int k = 0; k < (int)(metres * metres * 900); k++)
            {
                float u = r.Next(), v = r.Next(), s = MathF.Max(1.5f, r.Range(0.025f, 0.05f) * px);
                var col = leaves[r.Int(leaves.Length)] * r.Range(0.75f, 1.1f);
                float h = r.Range(0.2f, 0.9f), ang = r.Range(0, MathF.Tau);
                c.Lump(u, v, s, s * r.Range(0.45f, 0.7f), ang, h, h + 0.08f, (d, a) => col * (1.05f - 0.25f * d) * (MathF.Abs(MathF.Sin(a)) < 0.08f ? 0.8f : 1f), 0.3f, 0.05f);
            }
        }
        for (int k = 0; k < (int)(metres * metres * 3); k++)
        {
            float u = r.Next(), v = r.Next();
            c.Blade(u, v, r.Range(0, MathF.Tau), r.Range(0.1f, 0.3f) * px, r.Range(-0.3f, 0.3f), MathF.Max(1f, 0.008f * px), Paint.Of(0x5A4632), Paint.Of(0x6E5840), 0.9f, 0.95f, 1f, 0.3f);
        }
        c.ShadeHollows(0.35f);
        return c.Finish("", "", metres, 0.03f);
    }
}
