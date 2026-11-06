namespace ToyCity.ClientModel.Voxels.Assets;

/// <summary>Assets built from the library in code: the editor's example house and the gate run's shots.</summary>
public static class DemoAsset
{
    /// <summary>
    /// A two-storey brick house two modules square with a gabled tile roof, a door, windows all round, a timber
    /// porch and a garden: every kind of piece, placed as a player would. Its roof is the smooth auto-roof, or with
    /// <paramref name="autoRoof"/> false, built of the library's voxel roof pieces.
    /// </summary>
    public static AssetDoc House(int detail, bool autoRoof = true)
    {
        var doc = AssetDoc.New("Brick house", detail, 4, 4, 3);
        Piece P(string id) => Pieces.Find(id)!;
        int ox = 1, oy = 1;   // the house stands in frames 1..2 each way, leaving a garden round it
        for (int s = 0; s < 2; s++)
        {
            for (int gx = 0; gx < 2; gx++)
                for (int gy = 0; gy < 2; gy++)
                    doc.PlacePiece(P(s == 1 && gx == 0 && gy == 1 ? "floor-hatch" : "floor"), Material.Wood, ox + gx, oy + gy, s, 0);
            // Front (south) wall: a door and a wide window downstairs, windows up.
            doc.PlacePiece(P(s == 0 ? "wall-door" : "wall-window"), Material.Brick, ox, oy + 1, s, 2);
            doc.PlacePiece(P(s == 0 ? "wall-wide-window" : "wall-window"), Material.Brick, ox + 1, oy + 1, s, 2);
            // Back (north), and the sides.
            doc.PlacePiece(P("wall-window"), Material.Brick, ox, oy, s, 0);
            doc.PlacePiece(P(s == 0 ? "wall" : "wall-window"), Material.Brick, ox + 1, oy, s, 0);
            doc.PlacePiece(P("wall-window"), Material.Brick, ox + 1, oy, s, 1);
            doc.PlacePiece(P("wall"), Material.Brick, ox + 1, oy + 1, s, 1);
            doc.PlacePiece(P("wall"), Material.Brick, ox, oy, s, 3);
            doc.PlacePiece(P("wall-window"), Material.Brick, ox, oy + 1, s, 3);
        }
        doc.PlacePiece(P("stairs"), Material.Wood, ox, oy + 1, 0, 2);
        if (autoRoof) doc.Roof = new RoofSpec(RoofStyle.Gable, 40, 40, AssetPalette.RoofTile);
        // A gable: slopes rising from the north and south edges to a ridge across the middle.
        for (int gx = 0; gx < 2 && !autoRoof; gx++)
        {
            doc.PlacePiece(P("roof-slope"), Material.Wood, ox + gx, oy, 2, 0);
            doc.PlacePiece(P("roof-slope"), Material.Wood, ox + gx, oy + 1, 2, 2);
        }
        // Its ends closed in brick: each end a pair of gable pieces meeting under the ridge.
        if (!autoRoof)
        {
            doc.PlacePiece(P("gable-left"), Material.Brick, ox, oy, 2, 3);
            doc.PlacePiece(P("gable-right"), Material.Brick, ox, oy + 1, 2, 3);
            doc.PlacePiece(P("gable-right"), Material.Brick, ox + 1, oy, 2, 1);
            doc.PlacePiece(P("gable-left"), Material.Brick, ox + 1, oy + 1, 2, 1);
        }
        // A porch on pillars over the door, and a fence along the front of the garden.
        doc.PlacePiece(P("pillar"), Material.Wood, ox, oy + 2, 0, 2);
        doc.PlacePiece(P("pillar"), Material.Wood, ox, oy + 2, 0, 3);
        doc.PlacePiece(P("roof-flat"), Material.Wood, ox, oy + 2, 1, 2);
        for (int gx = 0; gx < 4; gx++) doc.PlacePiece(P("fence"), Material.Wood, gx, 3, 0, 2);
        // Props: hedges, a tree and a conifer, a bench, a lamp, a car on the drive, a planter, a bin and a crate.
        int M = doc.ModuleVoxels, h = detail;
        doc.PlaceProp(P("hedge"), h, 0, 0, 1);
        doc.PlaceProp(P("hedge"), h, M, 0, 1);
        doc.PlaceProp(P("tree"), 3 * M + h, h, 0, 0);
        doc.PlaceProp(P("conifer"), 3 * M + h, 2 * M, 0, 0);
        doc.PlaceProp(P("bench"), 2 * M + 2 * h, 3 * M + 2 * h, 0, 0);
        doc.PlaceProp(P("lamp"), 3 * M + 4 * h, 3 * M + 4 * h, 0, 2);
        doc.PlaceProp(P("car"), h, 2 * M + h, 0, 1);
        doc.PlaceProp(P("planter"), ox * M + 6 * h, (oy + 2) * M + h, 0, 0);
        doc.PlaceProp(P("bin"), (ox + 2) * M + h, (oy + 1) * M + 5 * h, 0, 0);
        doc.PlaceProp(P("crate"), (ox + 2) * M + h, oy * M + h, 0, 0);
        return doc;
    }

    /// <summary>
    /// An L-shaped stone house, two storeys, under a hipped slate auto-roof: the roof follows the L, hipped at the
    /// outer corners and with a valley in the inner one.
    /// </summary>
    public static AssetDoc LHouse(int detail)
    {
        var doc = AssetDoc.New("Stone house", detail, 4, 4, 3);
        Piece P(string id) => Pieces.Find(id)!;
        var frames = new HashSet<(int, int)> { (0, 0), (1, 0), (2, 0), (0, 1), (0, 2) };
        for (int s = 0; s < 2; s++)
            foreach (var (gx, gy) in frames)
            {
                doc.PlacePiece(P("floor"), Material.Wood, gx, gy, s, 0);
                // A wall on every edge with no frame of the house beyond it: north, east, south, west.
                foreach (var (dx, dy, rot) in new[] { (0, -1, 0), (1, 0, 1), (0, 1, 2), (-1, 0, 3) })
                {
                    if (frames.Contains((gx + dx, gy + dy))) continue;
                    string id = s == 0 && (gx, gy, rot) == (1, 0, 2) ? "wall-door" : (gx + gy + rot) % 3 == 0 ? "wall" : "wall-window";
                    doc.PlacePiece(P(id), Material.Stone, gx, gy, s, rot);
                }
            }
        doc.Roof = new RoofSpec(RoofStyle.Hip, 32, 50, AssetPalette.Slate);
        int M = doc.ModuleVoxels, h = detail;
        doc.PlaceProp(P("tree"), 2 * M + 4 * h, 2 * M + 2 * h, 0, 0);
        doc.PlaceProp(P("bench"), M + 6 * h, M + 6 * h, 0, 0);
        doc.PlaceProp(P("planter"), M + 2 * h, M + 2 * h, 0, 0);
        for (int gx = 0; gx < 4; gx++) doc.PlacePiece(P("fence"), Material.Stone, gx, 3, 0, 2);
        return doc;
    }

    /// <summary>
    /// A round temple on a lawn, made with the shape tool: a stepped plinth, a ring of columns and a band (lathes),
    /// a copper dome with an urn on top, an arched gateway (an extrude) and a rail round the lawn (a sweep).
    /// </summary>
    public static AssetDoc Rotunda(int detail = 4)
    {
        var doc = AssetDoc.New("Rotunda", detail, 4, 4, 5);
        double w = doc.Model.SizeX * 0.5 / detail, cx = w / 2, cy = w / 2 - 1;
        doc.Box(0, 0, 0, doc.Model.SizeX - 1, doc.Model.SizeY - 1, 0, AssetPalette.Grass);
        Shape At(Shape s, double x, double y, double z) { s.X = x; s.Y = y; s.Z = z; return s; }
        doc.AddShape(At(new Shape
        {
            Kind = ShapeKind.Lathe, Colour = AssetPalette.Stone,
            Profile = new() { (0, 0), (6.4, 0), (6.4, 0.35), (6.0, 0.35), (6.0, 0.7), (5.6, 0.7), (5.6, 1.0), (0, 1.0) },
        }, cx, cy, 0.25));
        for (int i = 0; i < 10; i++)
        {
            double a = 2 * Math.PI * i / 10;
            doc.AddShape(At(ShapePresets.Column(5, 0.3, AssetPalette.Trim), cx + Math.Cos(a) * 4.8, cy + Math.Sin(a) * 4.8, 1.25));
        }
        doc.AddShape(At(ShapePresets.Ring(5.4, 1.2, 0.9, AssetPalette.Stone), cx, cy, 6.25));
        doc.AddShape(At(ShapePresets.Dome(4.9, Draw.Ramp(5, 7)), cx, cy, 7.15));
        doc.AddShape(At(ShapePresets.Vase(AssetPalette.Trim), cx, cy, 7.15 + 4.9 * 1.2 - 0.1));
        doc.AddShape(At(ShapePresets.Arch(2.6, 3.4, 0.5, AssetPalette.Brick), cx, w - 1.6, 0.25));
        var rail = ShapePresets.Rail(AssetPalette.Metal, new()
        {
            new(0.8f, 0.8f, 1), new((float)w - 0.8f, 0.8f, 1), new((float)w - 0.8f, (float)w - 0.8f, 1), new(0.8f, (float)w - 0.8f, 1),
        });
        rail.PathClosed = true;
        doc.AddShape(rail);
        foreach (var (x, y) in new[] { (0.8, 0.8), (w - 0.8, 0.8), (w - 0.8, w - 0.8), (0.8, w - 0.8) })
            doc.AddShape(At(ShapePresets.Column(1.1, 0.07, AssetPalette.Metal), x, y, 0.25));
        return doc;
    }

    /// <summary>
    /// A made-up photo for the photo importer's example and checks: a three-storey brick terrace front 12 m wide and
    /// 9 m tall, seen from a little to the left and below so it's a keystone, against sky and pavement, with a little
    /// grain like a real photo. Returns the photo and the facade's four corners in it (top-left, top-right,
    /// bottom-right, bottom-left), and the facade's size in metres.
    /// </summary>
    public static (Photo Photo, (double X, double Y)[] Corners, double Width, double Height) FacadePhoto(int w = 960, int h = 720)
    {
        const double fw = 12, fh = 9;
        var corners = new[] { (w * 0.22, h * 0.13), (w * 0.82, h * 0.21), (w * 0.80, h * 0.83), (w * 0.24, h * 0.89) };
        // From photo pixels to facade metres: the inverse of where the facade's corners land.
        var toFacade = PhotoImport.Homography(corners, new[] { (0.0, 0.0), (fw, 0.0), (fw, fh), (0.0, fh) })!;
        var photo = new Photo(w, h);
        uint seed = 2463534242;
        double Grain() { seed ^= seed << 13; seed ^= seed >> 17; seed ^= seed << 5; return (seed % 1000) / 1000.0 - 0.5; }
        static (double, double, double) C(uint rgb) => ((rgb >> 16 & 255) / 255.0, (rgb >> 8 & 255) / 255.0, (rgb & 255) / 255.0);
        (double, double, double) Wall(double u, double v)
        {
            // A cornice along the top and a stone band at each floor.
            if (v < 0.45) return C(0xE4E0D6);
            if (Math.Abs(v - 3.0) < 0.12 || Math.Abs(v - 6.0) < 0.12) return C(0xC9C2B2);
            int storey = (int)(v / 3);            // 0 at the top
            double inStorey = v - storey * 3;
            for (int bay = 0; bay < 4; bay++)
            {
                double cx = 1.5 + 3 * bay;
                bool door = storey == 2 && bay == 1;
                double halfW = 0.6, top = door ? 0.7 : 0.8, bottom = door ? 3.0 : 2.4;
                if (Math.Abs(u - cx) < halfW + 0.1 && inStorey > top - 0.1 && inStorey < bottom + (door ? 0 : 0.1))
                {
                    bool frame = Math.Abs(u - cx) > halfW || inStorey < top || inStorey > bottom || (!door && Math.Abs(u - cx) < 0.05);
                    if (frame) return C(0xEDEAE2);
                    if (door) return C(0x4A3426);
                    // Glass: darker low down, with the sky caught in the top.
                    double t = (inStorey - top) / (bottom - top);
                    return t < 0.3 ? C(0x7F9BAE) : C(0x3E5363);
                }
            }
            return C(0xA2472F);
        }
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var (u, v) = PhotoImport.Map(toFacade, x + 0.5, y + 0.5);
                (double R, double G, double B) c;
                if (u >= 0 && u <= fw && v >= 0 && v <= fh) c = Wall(u, v);
                else if (v > fh) c = C(0x8C8A86);   // the pavement
                else if (u < 0 || u > fw) c = v < 0.6 ? C(0xBFD3E6) : C(0x7A6E62);   // the next houses, in shade
                else c = (0.62 + 0.2 * y / h, 0.74 + 0.15 * y / h, 0.92);   // sky
                double g = Grain() * 0.06;
                photo[x, y] = VoxModel.Rgba(B8(c.R + g), B8(c.G + g), B8(c.B + g));
            }
        return (photo, corners, fw, fh);
    }

    static byte B8(double v) => (byte)Math.Clamp(Math.Round(v * 255), 0, 255);
}
