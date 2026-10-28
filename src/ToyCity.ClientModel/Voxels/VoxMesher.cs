using System.Numerics;

namespace ToyCity.ClientModel.Voxels;

/// <summary>A mesh ready for an engine: world units are cells (x east, y up, z south), triangles counter-clockwise seen from the front.</summary>
public sealed class MeshData
{
    public readonly List<Vector3> Positions = new();
    public readonly List<Vector3> Normals = new();
    public readonly List<uint> Colors = new();   // RGBA packed as in the palette
    public readonly List<int> Indices = new();
    public int Quads => Indices.Count / 6;
}

/// <summary>
/// Greedy meshing: only faces between a voxel and the open air are drawn, and each slice's faces of one colour
/// merge into as few rectangles as possible, so a plain wall is two triangles however many voxels it holds.
/// Sealed cavities inside a model are not air, so a model saved hollow meshes the same as a solid one.
/// </summary>
public static class VoxMesher
{
    public static MeshData Mesh(VoxModel m, float scale = 1f / VoxModel.PerCell)
    {
        var mesh = new MeshData();
        int[] size = { m.SizeX, m.SizeY, m.SizeZ };
        var air = OpenAir(m);
        bool Open(int x, int y, int z) => !m.In(x, y, z) || air[x + m.SizeX * (y + m.SizeY * z)];
        for (int d = 0; d < 3; d++)
        {
            int u = (d + 1) % 3, v = (d + 2) % 3;
            var mask = new byte[size[u] * size[v]];
            var p = new int[3];
            var q = new int[3];
            foreach (int dir in new[] { 1, -1 })
                for (p[d] = 0; p[d] < size[d]; p[d]++)
                {
                    // Faces in this slice: a solid voxel whose neighbour on this side is empty.
                    for (p[v] = 0; p[v] < size[v]; p[v]++)
                        for (p[u] = 0; p[u] < size[u]; p[u]++)
                        {
                            byte c = m[p[0], p[1], p[2]];
                            q[0] = p[0]; q[1] = p[1]; q[2] = p[2];
                            q[d] += dir;
                            mask[p[u] + p[v] * size[u]] = c != 0 && Open(q[0], q[1], q[2]) ? c : (byte)0;
                        }
                    // Merge into rectangles: grow along u, then along v while the whole row matches.
                    for (int b = 0; b < size[v]; b++)
                        for (int a = 0; a < size[u];)
                        {
                            byte c = mask[a + b * size[u]];
                            if (c == 0) { a++; continue; }
                            int w = 1;
                            while (a + w < size[u] && mask[a + w + b * size[u]] == c) w++;
                            int h = 1;
                            for (; b + h < size[v]; h++)
                            {
                                bool row = true;
                                for (int k = 0; k < w && row; k++) row = mask[a + k + (b + h) * size[u]] == c;
                                if (!row) break;
                            }
                            for (int y = 0; y < h; y++) Array.Clear(mask, a + (b + y) * size[u], w);
                            int plane = dir > 0 ? p[d] + 1 : p[d];
                            Emit(mesh, m.Palette[c], d, u, v, plane, a, b, w, h, dir, scale);
                            a += w;
                        }
                }
        }
        return mesh;
    }

    /// <summary>The empty voxels reachable from outside the model.</summary>
    public static bool[] OpenAir(VoxModel m)
    {
        int sx = m.SizeX, sy = m.SizeY, sz = m.SizeZ;
        var air = new bool[sx * sy * sz];
        var queue = new Queue<int>();
        void Visit(int x, int y, int z)
        {
            int i = x + sx * (y + sy * z);
            if (air[i] || m.Voxels[i] != 0) return;
            air[i] = true;
            queue.Enqueue(i);
        }
        for (int z = 0; z < sz; z++)
            for (int y = 0; y < sy; y++)
                for (int x = 0; x < sx; x++)
                    if (x == 0 || y == 0 || z == 0 || x == sx - 1 || y == sy - 1 || z == sz - 1) Visit(x, y, z);
        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            int x = i % sx, y = i / sx % sy, z = i / (sx * sy);
            if (x > 0) Visit(x - 1, y, z);
            if (x < sx - 1) Visit(x + 1, y, z);
            if (y > 0) Visit(x, y - 1, z);
            if (y < sy - 1) Visit(x, y + 1, z);
            if (z > 0) Visit(x, y, z - 1);
            if (z < sz - 1) Visit(x, y, z + 1);
        }
        return air;
    }

    /// <summary>Model axes (x east, y south, z up) to world (x east, y up, z south).</summary>
    static Vector3 World(float[] c, float scale) => new(c[0] * scale, c[2] * scale, c[1] * scale);

    static void Emit(MeshData mesh, uint color, int d, int u, int v, int plane, int a, int b, int w, int h, int dir, float scale)
    {
        Vector3 Corner(int du, int dv)
        {
            var c = new float[3];
            c[d] = plane; c[u] = a + du; c[v] = b + dv;
            return World(c, scale);
        }
        var nm = new float[3];
        nm[d] = dir;
        var normal = World(nm, 1);
        var p0 = Corner(0, 0); var p1 = Corner(w, 0); var p2 = Corner(w, h); var p3 = Corner(0, h);
        int s = mesh.Positions.Count;
        mesh.Positions.AddRange(new[] { p0, p1, p2, p3 });
        for (int k = 0; k < 4; k++) { mesh.Normals.Add(normal); mesh.Colors.Add(color); }
        // Counter-clockwise seen from where the normal points.
        bool ccw = Vector3.Dot(Vector3.Cross(p1 - p0, p2 - p0), normal) > 0;
        if (ccw) mesh.Indices.AddRange(new[] { s, s + 1, s + 2, s, s + 2, s + 3 });
        else mesh.Indices.AddRange(new[] { s, s + 2, s + 1, s, s + 3, s + 2 });
    }
}
