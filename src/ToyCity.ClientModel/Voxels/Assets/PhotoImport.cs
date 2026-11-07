namespace ToyCity.ClientModel.Voxels.Assets;

/// <summary>A picture as packed RGBA pixels (<see cref="VoxModel.Rgba"/>), row by row from the top.</summary>
public sealed class Photo
{
    public int Width { get; }
    public int Height { get; }
    public uint[] Pixels { get; }

    public Photo(int width, int height, uint[]? pixels = null)
    {
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        Pixels = pixels ?? new uint[Width * Height];
        if (Pixels.Length != Width * Height) throw new ArgumentException("pixels don't match the size", nameof(pixels));
    }

    public uint this[int x, int y]
    {
        get => Pixels[Math.Clamp(x, 0, Width - 1) + Width * Math.Clamp(y, 0, Height - 1)];
        set { if (x >= 0 && y >= 0 && x < Width && y < Height) Pixels[x + Width * y] = value; }
    }

    /// <summary>The colour at a point between pixels, as 0..1 RGB, blended from the four pixels round it.</summary>
    public (double R, double G, double B) Sample(double x, double y)
    {
        x -= 0.5; y -= 0.5;
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        double fx = x - x0, fy = y - y0;
        double r = 0, g = 0, b = 0;
        void Add(int px, int py, double w)
        {
            var (cr, cg, cb, _) = VoxModel.Unpack(this[px, py]);
            r += cr * w; g += cg * w; b += cb * w;
        }
        Add(x0, y0, (1 - fx) * (1 - fy)); Add(x0 + 1, y0, fx * (1 - fy));
        Add(x0, y0 + 1, (1 - fx) * fy); Add(x0 + 1, y0 + 1, fx * fy);
        return (r / 255, g / 255, b / 255);
    }
}

/// <summary>Which side of the model a facade goes on, as the city's compass has it: south is the front.</summary>
public enum FacadeSide { South, East, North, West }

/// <summary>What <see cref="PhotoImport.Build"/> is told: the facade's real size, how deep to make it, how it's simplified.</summary>
public sealed record PhotoOptions
{
    /// <summary>The facade's width and height in metres, between the four corners.</summary>
    public double WidthMetres { get; init; } = 12;
    public double HeightMetres { get; init; } = 9;
    /// <summary>How far back the wall goes. A thin facade by default; deeper makes a solid block of a building.</summary>
    public double DepthMetres { get; init; } = 0.5;
    /// <summary>How many colours the photo is cut down to before they're matched to the palette.</summary>
    public int Colours { get; init; } = 6;
    /// <summary>How many passes of the speckle filter: each makes runs of one colour longer, so the wall meshes in fewer pieces.</summary>
    public int Smooth { get; init; } = 2;
    /// <summary>How far the colours that look like openings (windows, doors) start set back from the face.</summary>
    public double RecessMetres { get; init; } = 0.25;
}

/// <summary>
/// A photo cut down to a facade at voxel scale: each cell's colour group, the groups' colours and the palette entries
/// they map to, and how far each group sits in from the face or stands out of it. Made by
/// <see cref="PhotoImport.Read"/>; change a group's <see cref="Offset"/> or <see cref="Entry"/> before building it.
/// </summary>
public sealed class Facade
{
    /// <summary>Across and up in voxels; row 0 is the top.</summary>
    public int Width { get; init; }
    public int Height { get; init; }
    /// <summary>Each cell's group, row by row from the top.</summary>
    public required byte[] Cells { get; init; }
    /// <summary>Each group's colour in the photo, 0..1 RGB.</summary>
    public required (double R, double G, double B)[] Colours { get; init; }
    /// <summary>The palette entry each group is drawn in.</summary>
    public required byte[] Entry { get; init; }
    /// <summary>
    /// How far each group sits from the face, in metres: negative is set back into the wall (windows, doors),
    /// positive stands out of it (sills, cornices, a band).
    /// </summary>
    public required double[] Offset { get; init; }

    /// <summary>Whether a group is set back into the wall.</summary>
    public bool SetBack(int group) => Offset[group] < 0;

    /// <summary>
    /// A distance from the face for each cell, in metres, added to its group's: from a depth map
    /// (<see cref="PhotoImport.ReliefFromDepthMap"/>), loaded by the player or made by a depth model
    /// (<see cref="DepthEstimators"/>). Null for none.
    /// </summary>
    public double[]? Relief { get; set; }

    /// <summary>Each cell's lightness in the photo, 0..1.</summary>
    public required double[] Light { get; init; }

    /// <summary>A cell's distance from the face in metres: its group's plus its relief.</summary>
    public double OffsetAt(int x, int row) => Offset[this[x, row]] + (Relief?[x + Width * row] ?? 0);
    /// <summary>The share of the facade each group covers.</summary>
    public required double[] Share { get; init; }

    public byte this[int x, int row] => Cells[x + Width * row];

    /// <summary>The group covering the most of the facade: the wall's own colour, used for the sides and top of a block.</summary>
    public int Main => Array.IndexOf(Share, Share.Max());
}

/// <summary>
/// The photo importer (RC part 2.2). A photo of a building is straightened to a flat facade from four corners the
/// player puts on it, cut to voxels at the asset's scale from the facade's size in metres, its colours grouped and
/// matched to the palette, specks filtered out so the wall meshes into big flat pieces, and windows and doors set back
/// from the face. It goes into the model as one undo step, on the front (south) side, like any other voxels: drawn as
/// flat polygons in the city and kept whole for when the building is wrecked.
/// </summary>
public static class PhotoImport
{
    /// <summary>
    /// The 3×3 projective map that takes each of four points <paramref name="from"/> to the matching point
    /// <paramref name="to"/>, as 8 numbers (the ninth is 1). Null if three of the points are in a line.
    /// </summary>
    public static double[]? Homography(IReadOnlyList<(double X, double Y)> from, IReadOnlyList<(double X, double Y)> to)
    {
        if (from.Count != 4 || to.Count != 4) throw new ArgumentException("four points each");
        var a = new double[8, 9];
        for (int i = 0; i < 4; i++)
        {
            var (x, y) = from[i];
            var (u, v) = to[i];
            double[] r1 = { x, y, 1, 0, 0, 0, -u * x, -u * y, u };
            double[] r2 = { 0, 0, 0, x, y, 1, -v * x, -v * y, v };
            for (int j = 0; j < 9; j++) { a[2 * i, j] = r1[j]; a[2 * i + 1, j] = r2[j]; }
        }
        // Gaussian elimination with partial pivoting.
        for (int c = 0; c < 8; c++)
        {
            int p = c;
            for (int r = c + 1; r < 8; r++) if (Math.Abs(a[r, c]) > Math.Abs(a[p, c])) p = r;
            if (Math.Abs(a[p, c]) < 1e-12) return null;
            if (p != c) for (int j = 0; j < 9; j++) (a[p, j], a[c, j]) = (a[c, j], a[p, j]);
            for (int r = 0; r < 8; r++)
            {
                if (r == c) continue;
                double f = a[r, c] / a[c, c];
                if (f == 0) continue;
                for (int j = c; j < 9; j++) a[r, j] -= f * a[c, j];
            }
        }
        var h = new double[8];
        for (int i = 0; i < 8; i++) h[i] = a[i, 8] / a[i, i];
        return h;
    }

    /// <summary>A point through a <see cref="Homography"/>.</summary>
    public static (double X, double Y) Map(double[] h, double x, double y)
    {
        double w = h[6] * x + h[7] * y + 1;
        return ((h[0] * x + h[1] * y + h[2]) / w, (h[3] * x + h[4] * y + h[5]) / w);
    }

    /// <summary>
    /// The four corners a new photo starts with: a little in from its own corners, top-left, top-right, bottom-right,
    /// bottom-left.
    /// </summary>
    public static (double X, double Y)[] StartCorners(Photo p) =>
        new[] { (p.Width * 0.12, p.Height * 0.12), (p.Width * 0.88, p.Height * 0.12), (p.Width * 0.88, p.Height * 0.88), (p.Width * 0.12, p.Height * 0.88) };

    /// <summary>
    /// The facade between four corners (top-left, top-right, bottom-right, bottom-left, in photo pixels) seen
    /// straight on, <paramref name="width"/> × <paramref name="height"/> pixels. Each pixel is the average of a few
    /// samples across it, so a big photo cut down small doesn't shimmer.
    /// </summary>
    public static Photo Straighten(Photo photo, IReadOnlyList<(double X, double Y)> corners, int width, int height)
    {
        var grid = Grid(photo, corners, width, height);
        var outp = new Photo(width, height);
        for (int i = 0; i < grid.Length; i++)
        {
            var (r, g, b) = grid[i];
            outp.Pixels[i] = VoxModel.Rgba(To8(r), To8(g), To8(b));
        }
        return outp;
    }

    static byte To8(double v) => (byte)Math.Clamp(Math.Round(v * 255), 0, 255);

    /// <summary>The average colour over each cell of a width × height grid laid over the straightened facade.</summary>
    static (double R, double G, double B)[] Grid(Photo photo, IReadOnlyList<(double X, double Y)> corners, int width, int height)
    {
        width = Math.Max(1, width); height = Math.Max(1, height);
        var h = Homography(new[] { (0.0, 0.0), (1.0, 0.0), (1.0, 1.0), (0.0, 1.0) }, corners)
            ?? throw new ArgumentException("three of the corners are in a line", nameof(corners));
        // How many photo pixels one cell covers, roughly: enough samples to see them all, at most 4 × 4.
        double across = Math.Max(Dist(corners[0], corners[1]), Dist(corners[3], corners[2])) / width;
        double down = Math.Max(Dist(corners[0], corners[3]), Dist(corners[1], corners[2])) / height;
        int n = Math.Clamp((int)Math.Ceiling(Math.Max(across, down)), 1, 4);
        var cells = new (double, double, double)[width * height];
        for (int row = 0; row < height; row++)
            for (int x = 0; x < width; x++)
            {
                double r = 0, g = 0, b = 0;
                for (int sy = 0; sy < n; sy++)
                    for (int sx = 0; sx < n; sx++)
                    {
                        var (px, py) = Map(h, (x + (sx + 0.5) / n) / width, (row + (sy + 0.5) / n) / height);
                        var (cr, cg, cb) = photo.Sample(px, py);
                        r += cr; g += cg; b += cb;
                    }
                cells[x + width * row] = (r / (n * n), g / (n * n), b / (n * n));
            }
        return cells;
    }

    static double Dist((double X, double Y) a, (double X, double Y) b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>The voxels across and up for a facade of this size at a detail (0.5 m ÷ detail a voxel).</summary>
    public static (int W, int H) VoxelsOf(PhotoOptions o, int detail) =>
        (Math.Max(1, (int)Math.Round(o.WidthMetres * 2 * detail)), Math.Max(1, (int)Math.Round(o.HeightMetres * 2 * detail)));

    /// <summary>
    /// Reads the facade between the corners at the asset's detail: cut to voxels, grouped into
    /// <see cref="PhotoOptions.Colours"/> colours, specks filtered out, each group matched to the nearest palette
    /// entry, and the dark groups that look like glass or openings set back.
    /// </summary>
    public static Facade Read(Photo photo, IReadOnlyList<(double X, double Y)> corners, int detail, PhotoOptions o, uint[]? palette = null)
    {
        var (w, hgt) = VoxelsOf(o, detail);
        var cells = Grid(photo, corners, w, hgt);
        int k = Math.Clamp(o.Colours, 2, 16);
        var (ids, centres) = Group(cells, k);
        MergeBlends(cells, ids, centres);
        for (int i = 0; i < o.Smooth; i++) ids = Despeckle(ids, w, hgt, centres.Length);
        // Groups the filter emptied are dropped; the rest keep their order.
        var count = new int[centres.Length];
        foreach (byte id in ids) count[id]++;
        var keep = Enumerable.Range(0, centres.Length).Where(i => count[i] > 0).ToArray();
        var remap = new byte[centres.Length];
        for (int i = 0; i < keep.Length; i++) remap[keep[i]] = (byte)i;
        for (int i = 0; i < ids.Length; i++) ids[i] = remap[ids[i]];
        var colours = keep.Select(i => centres[i]).ToArray();
        var share = keep.Select(i => count[i] / (double)ids.Length).ToArray();
        if (palette == null)
        {
            var m = new VoxModel(1, 1, 1);
            AssetPalette.Apply(m);
            palette = m.Palette;
        }
        var entry = colours.Select(c => Nearest(palette, c)).ToArray();
        return new Facade { Width = w, Height = hgt, Cells = ids, Colours = colours, Entry = entry, Offset = Openings(colours, share).Select(o2 => o2 ? -o.RecessMetres : 0).ToArray(), Share = share,
            Light = cells.Select(Luma).ToArray() };
    }

    static double Luma((double R, double G, double B) c) => 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;

    /// <summary>
    /// Which groups to set back by default: a group darker than the wall, by a good margin, and not most of the facade
    /// (windows, doors and the shade inside an arch), or one bluer than it is red that is not much lighter than the wall
    /// (glass catching the sky).
    /// </summary>
    static bool[] Openings((double R, double G, double B)[] colours, double[] share)
    {
        int main = Array.IndexOf(share, share.Max());
        double wall = Luma(colours[main]);
        return colours.Select((c, i) => i != main && share[i] < 0.45 &&
            (Luma(c) < wall * 0.7 || (c.B > c.R + 0.06 && Luma(c) <= wall * 1.8))).ToArray();
    }

    /// <summary>The palette entry nearest a colour, by a red-weighted distance that tracks the eye better than plain RGB.</summary>
    public static byte Nearest(uint[] palette, (double R, double G, double B) c)
    {
        byte best = 1;
        double bestD = double.MaxValue;
        for (int i = 1; i < 256; i++)
        {
            if (i == AssetPalette.Lamp) continue;   // a lit lamp glows at night: never a wall colour
            var (r, g, b, a) = VoxModel.Unpack(palette[i]);
            if (a == 0 || (r == 255 && g == 0 && b == 255)) continue;
            double dr = r / 255.0 - c.R, dg = g / 255.0 - c.G, db = b / 255.0 - c.B, rm = (r / 255.0 + c.R) / 2;
            double d = (2 + rm) * dr * dr + 4 * dg * dg + (3 - rm) * db * db;
            if (d < bestD) { bestD = d; best = (byte)i; }
        }
        return best;
    }

    /// <summary>
    /// k-means over the cells' colours, started from spread-out picks (the most common colour first, then each next
    /// the one furthest from those already picked), so the same photo always groups the same way.
    /// </summary>
    static (byte[] Ids, (double R, double G, double B)[] Centres) Group((double R, double G, double B)[] cells, int k)
    {
        static double D((double R, double G, double B) a, (double R, double G, double B) b) =>
            (a.R - b.R) * (a.R - b.R) + (a.G - b.G) * (a.G - b.G) + (a.B - b.B) * (a.B - b.B);
        // Start: the mean, then the furthest from those already chosen, on a sample of at most 4096 cells.
        int step = Math.Max(1, cells.Length / 4096);
        var sample = new List<(double R, double G, double B)>();
        for (int i = 0; i < cells.Length; i += step) sample.Add(cells[i]);
        var centres = new List<(double R, double G, double B)> { (sample.Average(c => c.R), sample.Average(c => c.G), sample.Average(c => c.B)) };
        var near = sample.Select(c => D(c, centres[0])).ToArray();
        while (centres.Count < k)
        {
            int far = Array.IndexOf(near, near.Max());
            if (near[far] < 1e-4) break;   // fewer colours in the photo than asked for
            centres.Add(sample[far]);
            for (int i = 0; i < sample.Count; i++) near[i] = Math.Min(near[i], D(sample[i], sample[far]));
        }
        var ids = new byte[cells.Length];
        for (int pass = 0; pass < 12; pass++)
        {
            bool moved = false;
            for (int i = 0; i < cells.Length; i++)
            {
                byte best = 0;
                double bd = double.MaxValue;
                for (int j = 0; j < centres.Count; j++) { double d = D(cells[i], centres[j]); if (d < bd) { bd = d; best = (byte)j; } }
                if (ids[i] != best || pass == 0) { moved |= ids[i] != best; ids[i] = best; }
            }
            var sum = new (double R, double G, double B, int N)[centres.Count];
            for (int i = 0; i < cells.Length; i++) { var s = sum[ids[i]]; sum[ids[i]] = (s.R + cells[i].R, s.G + cells[i].G, s.B + cells[i].B, s.N + 1); }
            for (int j = 0; j < centres.Count; j++) if (sum[j].N > 0) centres[j] = (sum[j].R / sum[j].N, sum[j].G / sum[j].N, sum[j].B / sum[j].N);
            if (!moved && pass > 0) break;
        }
        return (ids, centres.ToArray());
    }

    /// <summary>
    /// Folds away groups that are only the blur between two others: the pinkish line where a white window frame meets
    /// red brick, say. A group is a blend when its colour lies close to the line between two bigger groups' colours;
    /// each of its cells then goes to whichever of the two it's nearer.
    /// </summary>
    static void MergeBlends((double R, double G, double B)[] cells, byte[] ids, (double R, double G, double B)[] centres)
    {
        int k = centres.Length;
        var count = new int[k];
        foreach (byte id in ids) count[id]++;
        var into = new (int A, int B)?[k];
        for (int c = 0; c < k; c++)
        {
            double best = 0.06;   // how far off the line, in RGB 0..1
            for (int a = 0; a < k; a++)
                for (int b = a + 1; b < k; b++)
                {
                    if (a == c || b == c || count[a] <= count[c] || count[b] <= count[c] || into[a] != null || into[b] != null) continue;
                    var (pa, pb, pc) = (centres[a], centres[b], centres[c]);
                    double dx = pb.R - pa.R, dy = pb.G - pa.G, dz = pb.B - pa.B, len2 = dx * dx + dy * dy + dz * dz;
                    if (len2 < 0.04) continue;   // two groups that close aren't worth a blend between them
                    double t = ((pc.R - pa.R) * dx + (pc.G - pa.G) * dy + (pc.B - pa.B) * dz) / len2;
                    if (t < 0.2 || t > 0.8) continue;
                    double ex = pa.R + t * dx - pc.R, ey = pa.G + t * dy - pc.G, ez = pa.B + t * dz - pc.B, off = Math.Sqrt(ex * ex + ey * ey + ez * ez);
                    if (off < best) { best = off; into[c] = (a, b); }
                }
        }
        static double D((double R, double G, double B) p, (double R, double G, double B) q) => (p.R - q.R) * (p.R - q.R) + (p.G - q.G) * (p.G - q.G) + (p.B - q.B) * (p.B - q.B);
        for (int i = 0; i < ids.Length; i++)
            if (into[ids[i]] is var (a, b))
                ids[i] = (byte)(D(cells[i], centres[a]) <= D(cells[i], centres[b]) ? a : b);
    }

    /// <summary>
    /// One pass of the speckle filter: a cell takes the group that at least 3 of it and its four neighbours are in.
    /// Lone cells and one-cell bumps on an edge go; straight edges, square corners and lines a cell wide (a window's
    /// glazing bar) stay, so the wall meshes in long straight runs.
    /// </summary>
    static byte[] Despeckle(byte[] ids, int w, int h, int groups)
    {
        var outp = (byte[])ids.Clone();
        var votes = new int[groups];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                Array.Clear(votes);
                votes[ids[x + w * y]]++;
                if (x > 0) votes[ids[x - 1 + w * y]]++;
                if (x < w - 1) votes[ids[x + 1 + w * y]]++;
                if (y > 0) votes[ids[x + w * (y - 1)]]++;
                if (y < h - 1) votes[ids[x + w * (y + 1)]]++;
                int best = Array.IndexOf(votes, votes.Max());
                if (votes[best] >= 3) outp[x + w * y] = (byte)best;
            }
        return outp;
    }

    // ---- relief ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Relief from a depth map: a greyscale picture lined up with the photo, lighter nearer. It's read between the same
    /// corners (scaled if the map is a different size from the photo), and each cell stands out or sits back by its
    /// lightness against the facade's middle lightness, up to <paramref name="rangeMetres"/> either way at black or white.
    /// </summary>
    public static double[] ReliefFromDepthMap(Facade f, Photo photo, Photo depth, IReadOnlyList<(double X, double Y)> corners, double rangeMetres)
    {
        double sx = depth.Width / (double)photo.Width, sy = depth.Height / (double)photo.Height;
        var scaled = corners.Select(c => (c.X * sx, c.Y * sy)).ToArray();
        var v = Grid(depth, scaled, f.Width, f.Height).Select(Luma).ToArray();
        double mid = Median(v);
        return v.Select(x => Math.Clamp((x - mid) * 2 * rangeMetres, -rangeMetres, rangeMetres)).ToArray();
    }

    static double Median(double[] v)
    {
        var s = (double[])v.Clone();
        Array.Sort(s);
        return s.Length == 0 ? 0 : s[s.Length / 2];
    }

    // ---- building ------------------------------------------------------------------------------------------------

    /// <summary>What <see cref="Build"/> did: the side, where the face went, and whether the model had to grow.</summary>
    public sealed record Placed(FacadeSide Side, int Along0, int Face, int Depth, int Voxels, bool Resized)
    {
        /// <summary>For the south side, the face's row (y) and first column (x).</summary>
        public int FrontY => Face;
        public int X0 => Along0;
    }

    /// <summary>
    /// Puts a facade into the model as one undo step, on one side, standing on the ground,
    /// <see cref="PhotoOptions.DepthMetres"/> deep. On an empty model it goes at that side's edge, centred along it;
    /// on a model with voxels already, onto the outside of what's there on that side, centred on it, so a block built
    /// from one photo can take another on each side. Each cell is set back or stands out by its distance
    /// (<see cref="Facade.OffsetAt"/>). A deep facade is a solid block whose inside is the wall's main colour.
    /// Anything already in the facade's space is replaced. The model grows to fit on its south and east sides if it
    /// must, which starts the undo history again; anything standing out past the north or west edge is left off.
    /// Null if the facade is bigger than a model can be (256 voxels) at this detail.
    /// </summary>
    public static Placed? Build(AssetDoc doc, Facade f, PhotoOptions o, FacadeSide side = FacadeSide.South)
    {
        int detail = doc.Detail;
        int depth = Math.Clamp((int)Math.Round(o.DepthMetres * 2 * detail), 1, 256);
        int V(double metres) => (int)Math.Round(metres * 2 * detail);
        int outMax = 0;
        for (int row = 0; row < f.Height; row++)
            for (int x = 0; x < f.Width; x++)
                outMax = Math.Max(outMax, V(f.OffsetAt(x, row)));
        if (f.Width > 256 || f.Height > 256 || depth + outMax > 256) return null;
        bool ns = side is FacadeSide.South or FacadeSide.North;
        var bounds = doc.Bounds();
        // Grow the model to fit, when that's allowed (not inside someone else's undo step).
        bool grow = false;
        if (!doc.InStep)
        {
            var m0 = doc.Model;
            int alongSize = ns ? m0.SizeX : m0.SizeY, acrossSize = ns ? m0.SizeY : m0.SizeX;
            int needAlong = Math.Max(alongSize, f.Width), needAcross = acrossSize;
            if (bounds is not { } b0) needAcross = Math.Max(acrossSize, depth + outMax);
            else if (side == FacadeSide.South) needAcross = Math.Max(acrossSize, b0.Y1 + outMax + 1);
            else if (side == FacadeSide.East) needAcross = Math.Max(acrossSize, b0.X1 + outMax + 1);
            int sx = ns ? needAlong : needAcross, sy = ns ? needAcross : needAlong, sz = Math.Max(m0.SizeZ, f.Height);
            if (sx != m0.SizeX || sy != m0.SizeY || sz != m0.SizeZ) { doc.Resize(sx, sy, sz); grow = true; }
        }
        var m = doc.Model;
        int along = ns ? m.SizeX : m.SizeY, across = ns ? m.SizeY : m.SizeX;
        int a0, face;
        if (bounds != null && doc.Bounds() is { } bb)
        {
            a0 = ns ? (bb.X0 + bb.X1 + 1 - f.Width) / 2 : (bb.Y0 + bb.Y1 + 1 - f.Width) / 2;
            face = side switch { FacadeSide.South => bb.Y1, FacadeSide.North => bb.Y0, FacadeSide.East => bb.X1, _ => bb.X0 };
        }
        else
        {
            a0 = (along - f.Width) / 2;
            face = side is FacadeSide.South or FacadeSide.East ? across - 1 - outMax : outMax;
        }
        // Local (u across the face as seen from outside, d into the building) to the model's x and y.
        (int X, int Y) At(int u, int d) => side switch
        {
            FacadeSide.South => (a0 + u, face - d),
            FacadeSide.North => (a0 + f.Width - 1 - u, face + d),
            FacadeSide.East => (face - d, a0 + f.Width - 1 - u),
            _ => (face + d, a0 + u),
        };
        byte main = f.Entry[f.Main];
        bool block = depth > 2 * detail;   // deeper than a metre
        int n = 0;
        bool mine = !doc.InStep;
        if (mine) doc.Begin();
        try
        {
            for (int row = 0; row < f.Height; row++)
            {
                int z = f.Height - 1 - row;
                for (int u = 0; u < f.Width; u++)
                {
                    byte g = f[u, row];
                    int off = V(f.OffsetAt(u, row));
                    // Set back: empty in front, the face (glass, a doorway) at the back of the recess, never through.
                    // Standing out: its colour from its front back to the face. Then the same colour through a thin
                    // facade, and the wall's main colour inside a block.
                    int front = off > 0 ? -off : Math.Min(-off, depth - 1);
                    for (int d = -outMax; d < depth; d++)
                    {
                        byte c = d < front ? (byte)0 : d <= Math.Max(front, 0) || !block ? f.Entry[g] : main;
                        var (x, y) = At(u, d);
                        if (!m.In(x, y, z)) continue;
                        doc.Set(x, y, z, c);
                        if (c != 0) n++;
                    }
                }
            }
        }
        finally { if (mine) doc.End(); }
        return new Placed(side, a0, face, depth, n, grow);
    }

    /// <summary>
    /// The facade repeated or cut to another width: its middle kept in the middle, the rest repeated out to the sides
    /// so bays carry on round a corner. For putting one photo on a longer or shorter side.
    /// </summary>
    public static Facade Fitted(Facade f, int width)
    {
        width = Math.Max(1, width);
        int shift = (width - f.Width) / 2;
        int Src(int x) => ((x - shift) % f.Width + f.Width) % f.Width;
        var cells = new byte[width * f.Height];
        var light = new double[width * f.Height];
        double[]? relief = f.Relief == null ? null : new double[width * f.Height];
        for (int row = 0; row < f.Height; row++)
            for (int x = 0; x < width; x++)
            {
                int s = Src(x) + f.Width * row, t = x + width * row;
                cells[t] = f.Cells[s];
                light[t] = f.Light[s];
                if (relief != null) relief[t] = f.Relief![s];
            }
        return new Facade { Width = width, Height = f.Height, Cells = cells, Colours = f.Colours, Entry = f.Entry, Offset = f.Offset, Share = f.Share, Light = light, Relief = relief };
    }

    /// <summary>
    /// One photo round all four sides, as one undo step, replacing what's in the model: a block the photo's width
    /// across and <see cref="PhotoOptions.DepthMetres"/> deep (as deep as it is wide when that's under a metre), with
    /// the facade on the south and north and fitted to the east and west. Null if it won't fit in a model.
    /// </summary>
    public static Placed? Wrap(AssetDoc doc, Facade f, PhotoOptions o)
    {
        int detail = doc.Detail;
        int deep = o.DepthMetres > 1 ? (int)Math.Round(o.DepthMetres * 2 * detail) : f.Width;
        int outMax = 0;
        for (int row = 0; row < f.Height; row++)
            for (int x = 0; x < f.Width; x++)
                outMax = Math.Max(outMax, (int)Math.Round(f.OffsetAt(x, row) * 2 * detail));
        int sx = f.Width + 2 * outMax, sy = deep + 2 * outMax;
        if (sx > 256 || sy > 256 || f.Height > 256) return null;
        var m = doc.Model;
        if (m.SizeX < sx || m.SizeY < sy || m.SizeZ < f.Height) doc.Resize(Math.Max(m.SizeX, sx), Math.Max(m.SizeY, sy), Math.Max(m.SizeZ, f.Height));
        m = doc.Model;
        // The block's footprint, centred.
        int x0 = (m.SizeX - f.Width) / 2, y0 = (m.SizeY - deep) / 2;
        var thin = o with { DepthMetres = Math.Min(o.DepthMetres, 0.5) };
        doc.Begin();
        try
        {
            for (int z = 0; z < m.SizeZ; z++)
                for (int y = 0; y < m.SizeY; y++)
                    for (int x = 0; x < m.SizeX; x++)
                    {
                        bool inBlock = x >= x0 && x < x0 + f.Width && y >= y0 && y < y0 + deep && z < f.Height;
                        doc.Set(x, y, z, inBlock ? f.Entry[f.Main] : (byte)0);
                    }
            var placed = Build(doc, f, thin, FacadeSide.South)!;
            Build(doc, f, thin, FacadeSide.North);
            var side = Fitted(f, deep);
            Build(doc, side, thin, FacadeSide.East);
            Build(doc, side, thin, FacadeSide.West);
            return placed;
        }
        finally { doc.End(); }
    }

    // ---- a photo as a texture (RC part 5) -------------------------------------------------------------------------

    /// <summary>
    /// The facade between four corners as a finish: straightened, at most <paramref name="most"/> pixels on its longer
    /// side, tiling once over the facade's size in metres, so a wall that size starting at its corner shows it once,
    /// whole. Its id is "photo:" and a hash of its pixels, the same for the same picture.
    /// </summary>
    public static Finish AsFinish(Photo photo, IReadOnlyList<(double X, double Y)> corners, PhotoOptions o, int most = 768)
    {
        double perMetre = Math.Min(128, most / Math.Max(o.WidthMetres, o.HeightMetres));
        int w = Math.Max(8, (int)Math.Round(o.WidthMetres * perMetre)), h = Math.Max(8, (int)Math.Round(o.HeightMetres * perMetre));
        var flat = Straighten(photo, corners, w, h);
        var rgba = new byte[w * h * 4];
        uint hash = 2166136261;
        for (int i = 0; i < w * h; i++)
        {
            var (r, g, b, _) = VoxModel.Unpack(flat.Pixels[i]);
            rgba[i * 4] = r; rgba[i * 4 + 1] = g; rgba[i * 4 + 2] = b; rgba[i * 4 + 3] = 255;
            hash = (((hash ^ r) * 16777619 ^ g) * 16777619 ^ b) * 16777619;
        }
        return Finishes.Add($"photo:{hash:x8}", "Photo", rgba, w, h, o.WidthMetres, o.HeightMetres);
    }

    /// <summary>
    /// A photo as a polygon wall (RC part 5): the facade straightened onto a wall its size in metres, standing on the
    /// ground on one side, as one undo step. On a model with voxels above the ground already it goes against the
    /// outside of them on that side, centred on them; otherwise half a metre in from that edge of the model, centred.
    /// Its voxels, for when it's wrecked, are the photo's colours. Null if it doesn't fit in the model.
    /// </summary>
    public static Shape? AsWall(AssetDoc doc, Photo photo, IReadOnlyList<(double X, double Y)> corners, PhotoOptions o, FacadeSide side = FacadeSide.South)
    {
        var m = doc.Model;
        double v = 0.5 / doc.Detail, W = o.WidthMetres, H = o.HeightMetres, t = Math.Clamp(o.DepthMetres, 0.1, 1.0);
        double mx = m.SizeX * v, my = m.SizeY * v, mz = m.SizeZ * v;
        // What's standing above the ground already.
        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
        for (int z = 1; z < m.SizeZ; z++)
            for (int y = 0; y < m.SizeY; y++)
                for (int x = 0; x < m.SizeX; x++)
                    if (m[x, y, z] != 0) { x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y); }
        bool built = x1 >= 0;
        double cx = built ? (x0 + x1 + 1) * v / 2 : mx / 2, cy = built ? (y0 + y1 + 1) * v / 2 : my / 2;
        double ground = v;
        var finish = AsFinish(photo, corners, o);
        Shape wall = side switch
        {
            FacadeSide.South => Parts.Wall(cx + W / 2, built ? (y1 + 1) * v + t : my - 0.5, cx - W / 2, built ? (y1 + 1) * v + t : my - 0.5, ground, H, t, finish.Id),
            FacadeSide.North => Parts.Wall(cx - W / 2, built ? y0 * v - t : 0.5, cx + W / 2, built ? y0 * v - t : 0.5, ground, H, t, finish.Id),
            FacadeSide.East => Parts.Wall(built ? (x1 + 1) * v + t : mx - 0.5, cy - W / 2, built ? (x1 + 1) * v + t : mx - 0.5, cy + W / 2, ground, H, t, finish.Id),
            _ => Parts.Wall(built ? x0 * v - t : 0.5, cy + W / 2, built ? x0 * v - t : 0.5, cy - W / 2, ground, H, t, finish.Id),
        };
        wall.Colour = finish.Palette;
        var (lo, hi) = wall.Bounds();
        const double slack = 0.26;   // Bounds() pads a shape by a quarter metre
        if (lo.X < -slack || lo.Y < -slack || hi.X > mx + slack || hi.Y > my + slack || ground + H > mz) return null;
        doc.AddShape(wall);
        return wall;
    }
}
