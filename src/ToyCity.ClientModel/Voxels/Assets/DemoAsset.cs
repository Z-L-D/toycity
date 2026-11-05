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
}
