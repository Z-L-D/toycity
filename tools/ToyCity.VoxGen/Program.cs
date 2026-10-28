using ToyCity.ClientModel.Voxels;
using ToyCity.Protocol;
using ToyCity.Sim;

// Writes the placeholder model of every building type as MagicaVoxel .vox files, for an artist to open and
// redraw. Zoned types get one file per lot size they grow on ({type}-{w}x{d}.vox), civic types one ({type}.vox).
// Copy a finished file into clients/godot/vox/ and the game draws it in place of the placeholder.
// Usage: dotnet run --project tools/ToyCity.VoxGen -- [out=vox-placeholders] [type-id ...]
string outDir = args.Length > 0 ? args[0] : "vox-placeholders";
var only = args.Skip(1).ToHashSet();
var content = ContentDb.LoadDefault();
var catalog = Mapping.Catalog(content);
Directory.CreateDirectory(outDir);
int files = 0;
foreach (var t in catalog.BuildingTypes)
{
    if (only.Count > 0 && !only.Contains(t.Id)) continue;
    if (t.Category == "zoned")
        foreach (var (w, d) in content.LotSizes[(int)t.Density].SelectMany(s => new[] { (s.W, s.D), (s.D, s.W) }).Distinct())
        {
            VoxFile.Write(Placeholder.Build(t, w, d).Hollow(), Path.Combine(outDir, $"{t.Id}-{w}x{d}.vox"));
            files++;
        }
    else
    {
        VoxFile.Write(Placeholder.Build(t, t.Width, t.Height).Hollow(), Path.Combine(outDir, $"{t.Id}.vox"));
        files++;
    }
}
Console.WriteLine($"wrote {files} .vox files to {Path.GetFullPath(outDir)}");
