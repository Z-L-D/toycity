using System.Numerics;

namespace ToyCity.ClientModel.Voxels;

/// <summary>
/// Smoothed voxel geometry for the free camera up close: naive surface nets. Each corner where solid meets air
/// gets one vertex, at the average of where the voxel edges around it cross the surface, and each voxel face
/// against the air becomes a quad joining the four vertices around it. Steps become slopes and corners round
/// off, while the model keeps its shape and colours. Like <see cref="VoxMesher"/>, a sealed cavity counts as solid.
/// </summary>
public static class SurfaceNets
{
    public static MeshData Mesh(VoxModel m, float scale = 1f / VoxModel.PerCell)
    {
        int sx = m.SizeX, sy = m.SizeY, sz = m.SizeZ;
        var air = VoxMesher.OpenAir(m);
        bool Solid(int x, int y, int z) => m.In(x, y, z) && !air[x + sx * (y + sy * z)];
        // Dual cells run from -1 to size-1 on each axis; cell (i, j, k) has voxel centres i..i+1 at its corners.
        int cx = sx + 1, cy = sy + 1, cz = sz + 1;
        var index = new int[cx * cy * cz];
        Array.Fill(index, -1);
        var mesh = new MeshData();
        var normals = new List<Vector3>();
        int Cell(int i, int j, int k) => (i + 1) + cx * ((j + 1) + cy * (k + 1));

        int Vertex(int i, int j, int k)
        {
            int c = Cell(i, j, k);
            if (index[c] >= 0) return index[c];
            // The average of the crossing points on the cell's twelve edges, in voxel-centre coordinates.
            var sum = Vector3.Zero;
            int n = 0;
            uint colour = 0;
            for (int e = 0; e < 12; e++)
            {
                int axis = e / 4, a = e % 4 & 1, b = e % 4 >> 1;
                var p0 = axis switch { 0 => (0, a, b), 1 => (a, 0, b), _ => (a, b, 0) };
                var p1 = axis switch { 0 => (1, a, b), 1 => (a, 1, b), _ => (a, b, 1) };
                bool s0 = Solid(i + p0.Item1, j + p0.Item2, k + p0.Item3), s1 = Solid(i + p1.Item1, j + p1.Item2, k + p1.Item3);
                if (s0 == s1) continue;
                sum += new Vector3(i + (p0.Item1 + p1.Item1) / 2f, j + (p0.Item2 + p1.Item2) / 2f, k + (p0.Item3 + p1.Item3) / 2f);
                n++;
                var (vx, vy, vz) = s0 ? (i + p0.Item1, j + p0.Item2, k + p0.Item3) : (i + p1.Item1, j + p1.Item2, k + p1.Item3);
                // Take the colour of a voxel on the surface, preferring the highest so roofs keep theirs.
                byte v = m[vx, vy, vz];
                if (v != 0 && (colour == 0 || e >= 8)) colour = m.Palette[v];
            }
            var p = sum / Math.Max(1, n) + new Vector3(0.5f);
            index[c] = mesh.Positions.Count;
            mesh.Positions.Add(new Vector3(p.X * scale, p.Z * scale, p.Y * scale));  // model (x, y, z up) to world (x, z, y)
            mesh.Colors.Add(colour != 0 ? colour : 0xff808080);
            normals.Add(Vector3.Zero);
            return index[c];
        }

        void Quad(int a, int b, int c, int d)
        {
            // Counter-clockwise from outside (the swap from model to world axes mirrors the order the loops give);
            // each corner's normal gathers the faces around it.
            var n = Vector3.Cross(mesh.Positions[b] - mesh.Positions[a], mesh.Positions[c] - mesh.Positions[a]);
            foreach (int v in new[] { a, b, c, d }) normals[v] += n;
            mesh.Indices.AddRange(new[] { a, b, c, a, c, d });
        }

        // Every voxel face between solid and air, along each axis: the four dual cells sharing that face's edge.
        for (int z = -1; z < sz; z++)
            for (int y = -1; y < sy; y++)
                for (int x = -1; x < sx; x++)
                {
                    bool here = Solid(x, y, z);
                    // +x face: cells around the edge from (x, y, z) to (x + 1, y, z) vary in y and z.
                    if (here != Solid(x + 1, y, z) && y >= 0 && z >= 0)
                    {
                        int a = Vertex(x, y - 1, z - 1), b = Vertex(x, y, z - 1), c = Vertex(x, y, z), d = Vertex(x, y - 1, z);
                        if (here) Quad(a, d, c, b); else Quad(a, b, c, d);
                    }
                    if (here != Solid(x, y + 1, z) && x >= 0 && z >= 0)
                    {
                        int a = Vertex(x - 1, y, z - 1), b = Vertex(x - 1, y, z), c = Vertex(x, y, z), d = Vertex(x, y, z - 1);
                        if (here) Quad(a, d, c, b); else Quad(a, b, c, d);
                    }
                    if (here != Solid(x, y, z + 1) && x >= 0 && y >= 0)
                    {
                        int a = Vertex(x - 1, y - 1, z), b = Vertex(x, y - 1, z), c = Vertex(x, y, z), d = Vertex(x - 1, y, z);
                        if (here) Quad(a, d, c, b); else Quad(a, b, c, d);
                    }
                }
        foreach (var n in normals) mesh.Normals.Add(n.LengthSquared() > 0 ? Vector3.Normalize(n) : Vector3.UnitY);
        return mesh;
    }
}
