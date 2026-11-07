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

    // RC part 5: textured surfaces. A vertex with a finish is drawn with that finish's texture at its UV (metres, or
    // a model's own coordinates for a mapped finish) rather than its colour. Both lists are empty for a mesh with no
    // finishes, and as long as the positions once any vertex has one (see Pad).

    /// <summary>Texture coordinates, one a vertex, once the mesh has a finish.</summary>
    public readonly List<Vector2> UVs = new();
    /// <summary>Which of <see cref="FinishIds"/> each vertex is, once the mesh has a finish; 0 is none (its colour).</summary>
    public readonly List<int> Finish = new();
    /// <summary>The finishes the mesh uses, by id; the first is none.</summary>
    public readonly List<string> FinishIds = new() { "" };
    public bool Textured => FinishIds.Count > 1;

    /// <summary>The index of a finish in this mesh's list, added if it's new.</summary>
    public int FinishIndex(string id)
    {
        if (string.IsNullOrEmpty(id)) return 0;
        int i = FinishIds.IndexOf(id);
        if (i >= 0) return i;
        FinishIds.Add(id);
        return FinishIds.Count - 1;
    }

    /// <summary>Fills in no finish for the vertices added without one, so the lists line up with the positions.</summary>
    public MeshData Pad()
    {
        if (!Textured) return this;
        while (UVs.Count < Positions.Count) UVs.Add(Vector2.Zero);
        while (Finish.Count < Positions.Count) Finish.Add(0);
        return this;
    }

    /// <summary>Adds a vertex with a finish (index from <see cref="FinishIndex"/>) at a UV.</summary>
    public int Add(Vector3 p, Vector3 n, uint colour, int finish, Vector2 uv)
    {
        if (finish > 0 || Textured) Pad();
        Positions.Add(p); Normals.Add(n); Colors.Add(colour);
        if (finish > 0 || Textured) { UVs.Add(uv); Finish.Add(finish); }
        return Positions.Count - 1;
    }

    /// <summary>
    /// The mesh with a finish on the faces <paramref name="which"/> picks (by their normal, y up), each face's
    /// texture laid along its slope by the metre (<paramref name="metresPerUnit"/> metres a world unit): across the
    /// slope and up it, so shingles run level along a roof. The rest keep their colours.
    /// </summary>
    public MeshData FinishFaces(string finish, float metresPerUnit, Func<Vector3, bool> which) =>
        FinishFaces(finish, metresPerUnit, (_, n) => which(n));

    /// <summary>The same, choosing the faces by their first index (a run of triangles) as well as their slope.</summary>
    public MeshData FinishFaces(string finish, float metresPerUnit, Func<int, Vector3, bool> which)
    {
        var o = new MeshData();
        int fin = o.FinishIndex(finish);
        uint plain = Assets.Finishes.VertexColour(finish);
        for (int i = 0; i + 2 < Indices.Count; i += 3)
        {
            int a = Indices[i], b = Indices[i + 1], c = Indices[i + 2];
            // A sliver keeps its place (so the triangles still line up with the ones asked for by index) but no finish.
            var face = Vector3.Cross(Positions[b] - Positions[a], Positions[c] - Positions[a]);
            bool sliver = face.LengthSquared() < 1e-14f;
            var n = sliver ? Vector3.UnitY : Vector3.Normalize(face);
            if (Vector3.Dot(n, Normals[a] + Normals[b] + Normals[c]) < 0) n = -n;
            bool on = !sliver && which(i, n);
            // Across the slope is level; up it is square to that in the face.
            var across = MathF.Abs(n.Y) > 0.999f ? Vector3.UnitX : Vector3.Normalize(Vector3.Cross(Vector3.UnitY, n));
            var up = Vector3.Cross(n, across);
            int s = o.Positions.Count;
            foreach (int v in new[] { a, b, c })
            {
                var p = Positions[v] * metresPerUnit;
                // Faces left alone keep any finish they had.
                int had = Textured && v < Finish.Count ? o.FinishIndex(FinishIds[Finish[v]]) : 0;
                var hadUv = Textured && v < UVs.Count ? UVs[v] : Vector2.Zero;
                o.Add(Positions[v], Normals[v], on ? plain : Colors[v], on ? fin : had, on ? new Vector2(Vector3.Dot(p, across), Vector3.Dot(p, up)) : hadUv);
            }
            o.Indices.AddRange(new[] { s, s + 1, s + 2 });
        }
        return o;
    }

    /// <summary>Adds another mesh's triangles to this one (the auto-roof onto its walls), and returns this.</summary>
    public MeshData Append(MeshData other)
    {
        int s = Positions.Count;
        bool textured = Textured || other.Textured;
        if (textured)
        {
            // This mesh's own vertices first get their (empty) finish, so the lists line up before adding.
            while (UVs.Count < s) UVs.Add(Vector2.Zero);
            while (Finish.Count < s) Finish.Add(0);
        }
        Positions.AddRange(other.Positions);
        Normals.AddRange(other.Normals);
        Colors.AddRange(other.Colors);
        foreach (int i in other.Indices) Indices.Add(s + i);
        if (textured)
        {
            var map = other.FinishIds.Select(FinishIndex).ToArray();
            for (int i = 0; i < other.Positions.Count; i++)
            {
                UVs.Add(i < other.UVs.Count ? other.UVs[i] : Vector2.Zero);
                Finish.Add(i < other.Finish.Count ? map[other.Finish[i]] : 0);
            }
        }
        return this;
    }
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
        var vox = m.Voxels;
        var ghost = m.Ghost;
        int[] stride = { 1, m.SizeX, m.SizeX * m.SizeY };
        for (int d = 0; d < 3; d++)
        {
            int u = (d + 1) % 3, v = (d + 2) % 3;
            int su = stride[u], sv = stride[v], sd = stride[d], nu = size[u], nv = size[v];
            var mask = new byte[nu * nv];
            var p = new int[3];
            foreach (int dir in new[] { 1, -1 })
                for (p[d] = 0; p[d] < size[d]; p[d]++)
                {
                    // Faces in this slice: a solid voxel whose neighbour on this side is open air (or outside).
                    bool edge = dir > 0 ? p[d] == size[d] - 1 : p[d] == 0;
                    int step = dir * sd, slice = p[d] * sd;
                    bool any = false;
                    for (int b = 0, at = 0; b < nv; b++)
                    {
                        int i = slice + b * sv;
                        for (int a = 0; a < nu; a++, i += su, at++)
                        {
                            byte c = vox[i];
                            byte f = c != 0 && !ghost[c] && (edge || air[i + step]) ? c : (byte)0;
                            mask[at] = f;
                            any |= f != 0;
                        }
                    }
                    if (!any) continue;
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
        var stack = new int[Math.Max(16, sx * sy * sz / 8)];
        int top = 0;
        var vox = m.Voxels;
        void Visit(int i)
        {
            if (air[i] || vox[i] != 0) return;
            air[i] = true;
            if (top == stack.Length) Array.Resize(ref stack, stack.Length * 2);
            stack[top++] = i;
        }
        int plane = sx * sy;
        // Most air is open sky: every empty voxel above a column's top is reached from above. Mark those first,
        // then flood from the edges and from beside them into what's left (rooms through windows, under eaves).
        for (int y = 0; y < sy; y++)
            for (int x = 0; x < sx; x++)
                for (int z = sz - 1, i = x + sx * (y + sy * z); z >= 0 && vox[i] == 0; z--, i -= plane) air[i] = true;
        for (int z = 0, i = 0; z < sz; z++)
            for (int y = 0; y < sy; y++)
                for (int x = 0; x < sx; x++, i++)
                {
                    if (air[i] || vox[i] != 0) continue;
                    if (x == 0 || y == 0 || z == 0 || x == sx - 1 || y == sy - 1 || z == sz - 1
                        || air[i - 1] || air[i + 1] || air[i - sx] || air[i + sx] || air[i - plane] || air[i + plane]) Visit(i);
                }
        while (top > 0)
        {
            int i = stack[--top];
            int x = i % sx, y = i / sx % sy, z = i / plane;
            if (x > 0) Visit(i - 1);
            if (x < sx - 1) Visit(i + 1);
            if (y > 0) Visit(i - sx);
            if (y < sy - 1) Visit(i + sx);
            if (z > 0) Visit(i - plane);
            if (z < sz - 1) Visit(i + plane);
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
