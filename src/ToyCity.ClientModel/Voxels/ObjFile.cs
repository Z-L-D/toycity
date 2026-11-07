using System.Globalization;
using System.Numerics;

namespace ToyCity.ClientModel.Voxels;

/// <summary>
/// Reads Wavefront OBJ models (RC part 5) into a <see cref="MeshData"/>: vertices, texture coordinates, normals and
/// faces (any polygon, fanned into triangles), with each material's diffuse colour and diffuse texture from the .mtl
/// file it names. Positions in the file's units, taken as metres, y up, like glTF.
/// </summary>
public static class ObjFile
{
    /// <param name="external">Fetches a file the model names (its .mtl, a texture) by its relative path; null if missing.</param>
    public static MeshData Read(string text, Func<string, byte[]?>? external = null)
    {
        var pos = new List<Vector3>();
        var uvs = new List<Vector2>();
        var normals = new List<Vector3>();
        var materials = new Dictionary<string, (uint Colour, string? Finish)>();
        (uint Colour, string? Finish) current = (0xFFC8C8C8, null);
        var mesh = new MeshData();
        var corners = new Dictionary<(int, int, int, string?), int>();
        static float F(string s) => float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
        int Index(string s, int count) { int i = int.Parse(s, CultureInfo.InvariantCulture); return i < 0 ? count + i : i - 1; }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var p = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            switch (p[0])
            {
                case "v" when p.Length >= 4: pos.Add(new Vector3(F(p[1]), F(p[2]), F(p[3]))); break;
                case "vt" when p.Length >= 3: uvs.Add(new Vector2(F(p[1]), 1 - F(p[2]))); break;   // OBJ's v runs up; textures here run down
                case "vn" when p.Length >= 4: normals.Add(Vector3.Normalize(new Vector3(F(p[1]), F(p[2]), F(p[3])))); break;
                case "mtllib" when p.Length >= 2 && external != null:
                    if (external(line[(line.IndexOf(' ') + 1)..].Trim()) is { } mtl) ReadMtl(System.Text.Encoding.UTF8.GetString(mtl), external, materials);
                    break;
                case "usemtl" when p.Length >= 2: current = materials.GetValueOrDefault(p[1], (0xFFC8C8C8, null)); break;
                case "f" when p.Length >= 4:
                {
                    var face = new List<int>();
                    foreach (var corner in p.Skip(1))
                    {
                        var parts = corner.Split('/');
                        int vi = Index(parts[0], pos.Count);
                        int ti = parts.Length > 1 && parts[1].Length > 0 ? Index(parts[1], uvs.Count) : -1;
                        int ni = parts.Length > 2 && parts[2].Length > 0 ? Index(parts[2], normals.Count) : -1;
                        if (vi < 0 || vi >= pos.Count) throw new InvalidDataException($"face uses vertex {parts[0]}, which isn't there");
                        var key = (vi, ti, ni, current.Finish ?? current.Colour.ToString());
                        if (!corners.TryGetValue(key, out int at))
                        {
                            bool tex = current.Finish != null && ti >= 0 && ti < uvs.Count;
                            int fin = tex ? mesh.FinishIndex(current.Finish!) : 0;
                            at = mesh.Add(pos[vi], ni >= 0 && ni < normals.Count ? normals[ni] : Vector3.Zero,
                                tex ? Assets.Finishes.VertexColour(current.Finish!) : current.Colour, fin, tex ? uvs[ti] : Vector2.Zero);
                            corners[key] = at;
                        }
                        face.Add(at);
                    }
                    for (int k = 1; k + 1 < face.Count; k++) mesh.Indices.AddRange(new[] { face[0], face[k], face[k + 1] });
                    break;
                }
            }
        }
        // Faces without normals get theirs from their triangles.
        var sum = new Vector3[mesh.Positions.Count];
        for (int t = 0; t + 2 < mesh.Indices.Count; t += 3)
        {
            int a = mesh.Indices[t], b = mesh.Indices[t + 1], c = mesh.Indices[t + 2];
            var n = Vector3.Cross(mesh.Positions[b] - mesh.Positions[a], mesh.Positions[c] - mesh.Positions[a]);
            sum[a] += n; sum[b] += n; sum[c] += n;
        }
        for (int i = 0; i < mesh.Normals.Count; i++)
            if (mesh.Normals[i] == Vector3.Zero) mesh.Normals[i] = sum[i].LengthSquared() > 0 ? Vector3.Normalize(sum[i]) : Vector3.UnitY;
        return mesh;
    }

    static void ReadMtl(string text, Func<string, byte[]?> external, Dictionary<string, (uint, string?)> into)
    {
        string? name = null;
        uint colour = 0xFFC8C8C8;
        string? finish = null;
        void Keep() { if (name != null) into[name] = (colour, finish); }
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            var p = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 2 || p[0].StartsWith('#')) continue;
            switch (p[0])
            {
                case "newmtl": Keep(); name = p[1]; colour = 0xFFC8C8C8; finish = null; break;
                case "Kd" when p.Length >= 4:
                    static byte B(string s) => (byte)Math.Clamp(Math.Round(float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture) * 255), 0, 255);
                    colour = VoxModel.Rgba(B(p[1]), B(p[2]), B(p[3]));
                    break;
                case "map_Kd":
                    // The file is the last word (options like -s may come first).
                    if (external(p[^1]) is { } bytes && ImageDecode.Read(bytes) is { } pic)
                    {
                        uint hash = 2166136261;
                        for (int i = 0; i < pic.Rgba.Length; i += 7) hash = (hash ^ pic.Rgba[i]) * 16777619;
                        finish = $"model:{hash:x8}{pic.W:x}{pic.H:x}";
                        if (!Assets.Finishes.Has(finish)) Assets.Finishes.Add(finish, p[^1], pic.Rgba, pic.W, pic.H, 1, 1, mapped: true);
                    }
                    break;
            }
        }
        Keep();
    }
}
