using System.Numerics;

namespace ToyCity.ClientModel.Voxels;

/// <summary>
/// Smooth shapes: a voxel model whose palette marks some materials smooth (domes, leaves, roofs) meshed so those
/// parts curve and the rest keep their edges. Dual contouring places one corner per cell of the dual grid: where
/// every voxel around it is sharp, at the point the surrounding faces meet (so a box stays a box); where any is
/// smooth, at the average of the surface crossings, as surface nets would. The smooth corners are relaxed toward
/// their neighbours and the quads around them are subdivided (Catmull-Clark), with the sharp corners and the edges
/// between them held as creases. The result is cut to a triangle budget by coarser voxels, then fewer subdivisions,
/// and as a last resort by clustering vertices.
/// </summary>
public static class ShapeMesher
{
    /// <summary>Bumped whenever the output changes, so meshes cached on disk are made again.</summary>
    public const int Version = 1;

    /// <summary>The quad mesh in model coordinates (x east, y south, z up; a voxel spans v..v+1).</summary>
    sealed class Quads
    {
        public readonly List<Vector3> P = new();
        /// <summary>A corner with only sharp voxels around it: it keeps its place.</summary>
        public readonly List<bool> Sharp = new();
        public readonly List<Face> F = new();
    }

    record struct Face(int A, int B, int C, int D, uint Color, bool Smooth)
    {
        public int this[int k] => k switch { 0 => A, 1 => B, 2 => C, _ => D };
    }

    /// <summary>
    /// The model as smooth shapes, in at most <paramref name="budget"/> triangles. <paramref name="start"/> is the
    /// coarsest the voxels start at (1 full resolution, 2 half); <paramref name="scale"/> is world units a voxel.
    /// </summary>
    public static MeshData Mesh(VoxModel m, float scale, int budget, int start = 1)
    {
        Quads? last = null;
        int lastF = start;
        for (int f = Math.Max(1, start); f <= 16; f *= 2)
        {
            var src = f == 1 ? m : m.Downsample(f);
            if (!src.AnySmooth)
            {
                // Nothing to round off: the greedy mesher gives the same blocks in far fewer triangles.
                var blocks = VoxMesher.Mesh(src, scale * f);
                if (blocks.Indices.Count / 3 <= budget || Math.Max(src.SizeX, Math.Max(src.SizeY, src.SizeZ)) <= 4) return Decimate.Cluster(blocks, budget);
                continue;
            }
            var q = Contour(src);
            last = q; lastF = f;
            Relax(q);
            var sub = Subdivide(q);
            if (sub.F.Count * 2 <= budget) return Emit(sub, scale * f);
            if (q.F.Count * 2 <= budget) return Emit(q, scale * f);
            if (Math.Max(src.SizeX, Math.Max(src.SizeY, src.SizeZ)) <= 4) break;
        }
        return last == null ? new MeshData() : Decimate.Cluster(Emit(last, scale * lastF), budget);
    }

    /// <summary>The dual-contoured quads, before any smoothing.</summary>
    static Quads Contour(VoxModel m)
    {
        int sx = m.SizeX, sy = m.SizeY, sz = m.SizeZ;
        var air = VoxMesher.OpenAir(m);
        bool Solid(int x, int y, int z) => m.In(x, y, z) && !air[x + sx * (y + sy * z)];
        bool SmoothAt(int x, int y, int z) => Solid(x, y, z) && m.Smooth[m[x, y, z]];
        int cx = sx + 1, cy = sy + 1, cz = sz + 1;
        var index = new int[cx * cy * cz];
        Array.Fill(index, -1);
        var q = new Quads();
        int Cell(int i, int j, int k) => (i + 1) + cx * ((j + 1) + cy * (k + 1));

        int Vertex(int i, int j, int k)
        {
            int c = Cell(i, j, k);
            if (index[c] >= 0) return index[c];
            var sum = Vector3.Zero;
            var axisSum = new float[3];
            var axisCount = new int[3];
            int n = 0;
            for (int e = 0; e < 12; e++)
            {
                int axis = e / 4, a = e % 4 & 1, b = e % 4 >> 1;
                var p0 = axis switch { 0 => (0, a, b), 1 => (a, 0, b), _ => (a, b, 0) };
                var p1 = axis switch { 0 => (1, a, b), 1 => (a, 1, b), _ => (a, b, 1) };
                if (Solid(i + p0.Item1, j + p0.Item2, k + p0.Item3) == Solid(i + p1.Item1, j + p1.Item2, k + p1.Item3)) continue;
                var crossing = new Vector3(i + (p0.Item1 + p1.Item1) / 2f, j + (p0.Item2 + p1.Item2) / 2f, k + (p0.Item3 + p1.Item3) / 2f);
                sum += crossing;
                n++;
                // A crossing along an axis is a face whose normal is that axis: it fixes that coordinate.
                axisSum[axis] += axis switch { 0 => crossing.X, 1 => crossing.Y, _ => crossing.Z };
                axisCount[axis]++;
            }
            bool smooth = false;
            for (int corner = 0; corner < 8 && !smooth; corner++) smooth = SmoothAt(i + (corner & 1), j + (corner >> 1 & 1), k + (corner >> 2));
            var mass = sum / Math.Max(1, n);
            // Sharp: the least-squares meeting point of the faces around it, which for faces along the axes is each
            // axis's mean crossing, or the mass point along an axis no face fixes.
            var p = smooth ? mass : new Vector3(
                axisCount[0] > 0 ? axisSum[0] / axisCount[0] : mass.X,
                axisCount[1] > 0 ? axisSum[1] / axisCount[1] : mass.Y,
                axisCount[2] > 0 ? axisSum[2] / axisCount[2] : mass.Z);
            index[c] = q.P.Count;
            q.P.Add(p + new Vector3(0.5f));
            q.Sharp.Add(!smooth);
            return index[c];
        }

        void Quad(int a, int b, int c, int d, byte voxel)
        {
            bool smooth = !q.Sharp[a] || !q.Sharp[b] || !q.Sharp[c] || !q.Sharp[d];
            q.F.Add(new Face(a, b, c, d, m.Palette[voxel], smooth));
        }

        for (int z = -1; z < sz; z++)
            for (int y = -1; y < sy; y++)
                for (int x = -1; x < sx; x++)
                {
                    bool here = Solid(x, y, z);
                    if (here != Solid(x + 1, y, z) && y >= 0 && z >= 0)
                    {
                        int a = Vertex(x, y - 1, z - 1), b = Vertex(x, y, z - 1), c = Vertex(x, y, z), d = Vertex(x, y - 1, z);
                        if (here) Quad(a, d, c, b, m[x, y, z]); else Quad(a, b, c, d, m[x + 1, y, z]);
                    }
                    if (here != Solid(x, y + 1, z) && x >= 0 && z >= 0)
                    {
                        int a = Vertex(x - 1, y, z - 1), b = Vertex(x - 1, y, z), c = Vertex(x, y, z), d = Vertex(x, y, z - 1);
                        if (here) Quad(a, d, c, b, m[x, y, z]); else Quad(a, b, c, d, m[x, y + 1, z]);
                    }
                    if (here != Solid(x, y, z + 1) && x >= 0 && y >= 0)
                    {
                        int a = Vertex(x - 1, y - 1, z), b = Vertex(x, y - 1, z), c = Vertex(x, y, z), d = Vertex(x - 1, y, z);
                        if (here) Quad(a, d, c, b, m[x, y, z]); else Quad(a, b, c, d, m[x, y, z + 1]);
                    }
                }
        return q;
    }

    /// <summary>Two passes moving each smooth corner halfway to the average of its neighbours, to take out the voxel steps.</summary>
    static void Relax(Quads q)
    {
        var next = new List<int>[q.P.Count];
        foreach (var f in q.F)
            for (int k = 0; k < 4; k++)
            {
                int a = f[k], b = f[(k + 1) % 4];
                (next[a] ??= new()).Add(b);
                (next[b] ??= new()).Add(a);
            }
        for (int pass = 0; pass < 2; pass++)
        {
            var moved = q.P.ToArray();
            for (int v = 0; v < q.P.Count; v++)
            {
                if (q.Sharp[v] || next[v] is not { Count: > 0 } around) continue;
                var avg = Vector3.Zero;
                foreach (int u in around) avg += q.P[u];
                moved[v] = Vector3.Lerp(q.P[v], avg / around.Count, 0.5f);
            }
            for (int v = 0; v < moved.Length; v++) q.P[v] = moved[v];
        }
    }

    /// <summary>
    /// One Catmull-Clark step over the faces with a smooth corner. Sharp corners stay put, and an edge between two
    /// sharp corners (or one not shared by exactly two faces) is a crease that stays straight, so smooth parts meet
    /// sharp ones without cracks.
    /// </summary>
    static Quads Subdivide(Quads q)
    {
        static (int, int) Key(int a, int b) => a < b ? (a, b) : (b, a);
        var edgeFaces = new Dictionary<(int, int), List<int>>();
        for (int f = 0; f < q.F.Count; f++)
            for (int k = 0; k < 4; k++)
            {
                var key = Key(q.F[f][k], q.F[f][(k + 1) % 4]);
                if (!edgeFaces.TryGetValue(key, out var list)) edgeFaces[key] = list = new List<int>(2);
                list.Add(f);
            }
        var facePoint = q.F.Select(f => (q.P[f.A] + q.P[f.B] + q.P[f.C] + q.P[f.D]) / 4).ToArray();
        bool Crease((int A, int B) e) => edgeFaces[e].Count != 2 || q.Sharp[e.A] && q.Sharp[e.B];

        var r = new Quads();
        // Old corners first, moved if smooth.
        var faceSum = new Vector3[q.P.Count];
        var edgeSum = new Vector3[q.P.Count];
        var faceCount = new int[q.P.Count];
        var edgeCount = new int[q.P.Count];
        var creases = new int[q.P.Count];
        var creaseSum = new Vector3[q.P.Count];
        for (int f = 0; f < q.F.Count; f++)
            for (int k = 0; k < 4; k++) { faceSum[q.F[f][k]] += facePoint[f]; faceCount[q.F[f][k]]++; }
        foreach (var (e, _) in edgeFaces)
        {
            var mid = (q.P[e.Item1] + q.P[e.Item2]) / 2;
            foreach (int v in new[] { e.Item1, e.Item2 }) { edgeSum[v] += mid; edgeCount[v]++; }
            if (Crease(e))
            {
                creases[e.Item1]++; creases[e.Item2]++;
                creaseSum[e.Item1] += q.P[e.Item2]; creaseSum[e.Item2] += q.P[e.Item1];
            }
        }
        for (int v = 0; v < q.P.Count; v++)
        {
            var p = q.P[v];
            int n = edgeCount[v];
            if (!q.Sharp[v] && n > 0)
            {
                if (creases[v] == 2) p = (6 * p + creaseSum[v]) / 8;
                else if (creases[v] == 0 && faceCount[v] == n && n >= 3)
                    p = (faceSum[v] / n + 2 * edgeSum[v] / n + (n - 3) * p) / n;
            }
            r.P.Add(p);
            r.Sharp.Add(q.Sharp[v]);
        }
        // An edge point on every edge of a face being subdivided.
        var edgePoint = new Dictionary<(int, int), int>();
        int EdgePoint(int a, int b)
        {
            var key = Key(a, b);
            if (edgePoint.TryGetValue(key, out int i)) return i;
            var faces = edgeFaces[key];
            bool crease = Crease(key);
            var p = crease ? (q.P[a] + q.P[b]) / 2 : (q.P[a] + q.P[b] + facePoint[faces[0]] + facePoint[faces[1]]) / 4;
            edgePoint[key] = i = r.P.Count;
            r.P.Add(p);
            r.Sharp.Add(crease && q.Sharp[a] && q.Sharp[b]);
            return i;
        }
        for (int f = 0; f < q.F.Count; f++)
        {
            var face = q.F[f];
            if (!face.Smooth) { r.F.Add(face); continue; }
            int centre = r.P.Count;
            r.P.Add(facePoint[f]);
            r.Sharp.Add(false);
            for (int k = 0; k < 4; k++)
            {
                int v = face[k], next = face[(k + 1) % 4], prev = face[(k + 3) % 4];
                r.F.Add(new Face(v, EdgePoint(v, next), centre, EdgePoint(prev, v), face.Color, true));
            }
        }
        return r;
    }

    /// <summary>
    /// The quads as triangles in world axes. Sharp faces are flat-shaded in their own colour; smooth ones take the
    /// normals of the surface around each corner, so they shade as one curve.
    /// </summary>
    static MeshData Emit(Quads q, float scale)
    {
        var world = q.P.Select(p => new Vector3(p.X * scale, p.Z * scale, p.Y * scale)).ToArray();  // model (x, y, z up) to world (x, z, y)
        var smoothNormal = new Vector3[world.Length];
        foreach (var f in q.F)
        {
            var n = Vector3.Cross(world[f.B] - world[f.A], world[f.C] - world[f.A]) + Vector3.Cross(world[f.C] - world[f.A], world[f.D] - world[f.A]);
            for (int k = 0; k < 4; k++) smoothNormal[f[k]] += n;
        }
        var mesh = new MeshData();
        foreach (var f in q.F)
        {
            var flat = Vector3.Cross(world[f.B] - world[f.A], world[f.C] - world[f.A]) + Vector3.Cross(world[f.C] - world[f.A], world[f.D] - world[f.A]);
            if (flat.LengthSquared() == 0) continue;  // collapsed to a line or a point
            flat = Vector3.Normalize(flat);
            int s = mesh.Positions.Count;
            for (int k = 0; k < 4; k++)
            {
                mesh.Positions.Add(world[f[k]]);
                var n = f.Smooth && smoothNormal[f[k]].LengthSquared() > 0 ? Vector3.Normalize(smoothNormal[f[k]]) : flat;
                mesh.Normals.Add(n);
                mesh.Colors.Add(f.Color);
            }
            mesh.Indices.AddRange(new[] { s, s + 1, s + 2, s, s + 2, s + 3 });
        }
        return mesh;
    }
}

/// <summary>Cutting a mesh down to a triangle budget.</summary>
public static class Decimate
{
    /// <summary>
    /// Vertex clustering: vertices in the same cube of a grid merge into one at their average, and triangles that
    /// collapse are dropped. The cubes grow until the mesh fits the budget. Coarse but quick, and the same every time.
    /// </summary>
    public static MeshData Cluster(MeshData m, int budget)
    {
        if (m.Indices.Count / 3 <= budget || m.Positions.Count == 0) return m;
        var min = m.Positions.Aggregate(Vector3.Min);
        var max = m.Positions.Aggregate(Vector3.Max);
        var extent = max - min;
        float cell = Math.Max(1e-4f, Math.Max(extent.X, Math.Max(extent.Y, extent.Z)) / 128);
        MeshData result = m;
        for (int round = 0; round < 40; round++, cell *= 1.3f)
        {
            result = Grid(m, min, cell);
            if (result.Indices.Count / 3 <= budget) break;
        }
        return result;
    }

    static MeshData Grid(MeshData m, Vector3 min, float cell)
    {
        var slot = new Dictionary<(int, int, int), int>();
        var map = new int[m.Positions.Count];
        var sum = new List<Vector3>();
        var normal = new List<Vector3>();
        var count = new List<int>();
        var colour = new List<uint>();
        for (int i = 0; i < m.Positions.Count; i++)
        {
            var g = (m.Positions[i] - min) / cell;
            var key = ((int)g.X, (int)g.Y, (int)g.Z);
            if (!slot.TryGetValue(key, out int s))
            {
                slot[key] = s = sum.Count;
                sum.Add(Vector3.Zero); normal.Add(Vector3.Zero); count.Add(0); colour.Add(m.Colors[i]);
            }
            sum[s] += m.Positions[i];
            normal[s] += m.Normals[i];
            count[s]++;
            map[i] = s;
        }
        var r = new MeshData();
        for (int s = 0; s < sum.Count; s++)
        {
            r.Positions.Add(sum[s] / count[s]);
            r.Normals.Add(normal[s].LengthSquared() > 0 ? Vector3.Normalize(normal[s]) : Vector3.UnitY);
            r.Colors.Add(colour[s]);
        }
        var seen = new HashSet<(int, int, int)>();
        for (int t = 0; t + 2 < m.Indices.Count; t += 3)
        {
            int a = map[m.Indices[t]], b = map[m.Indices[t + 1]], c = map[m.Indices[t + 2]];
            if (a == b || b == c || a == c) continue;
            // The same three corners once, whichever corner the triangle starts from.
            var key = a < b && a < c ? (a, b, c) : b < c ? (b, c, a) : (c, a, b);
            if (!seen.Add(key)) continue;
            r.Indices.AddRange(new[] { a, b, c });
        }
        return r;
    }
}
