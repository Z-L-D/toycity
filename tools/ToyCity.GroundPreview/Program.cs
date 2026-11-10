using System.Diagnostics;
using ToyCity.ClientModel.Graphics.Ground;

// Draws the ground's textures (RC part 6) to PNG files, to look at while writing them.
//   dotnet run --project tools/ToyCity.GroundPreview -- <folder> [size] [id ...]
// For each material it writes
//   <id>-sheet.png  four pictures in a square, each as wide as the texture:
//                     top left      the colour, one tile
//                     top right     the same lit by a low sun from the north-west, through its normals and its hollows' shading
//                     bottom left   the lit tile laid 2 by 2 and shrunk to fit, to show the seams
//                     bottom right  the lit tile laid 6 by 6 and shrunk to fit, to show how it repeats from far off
//   <id>-maps.png   its height, normals, roughness and ambient occlusion side by side
//   <id>-zoom.png   a piece of the lit tile at three times its size
// With no ids it draws them all, and all.png: every material's lit tile, six a row, in the order of the list.

if (args.Length < 1) { Console.Error.WriteLine("usage: GroundPreview <folder> [size] [id ...]"); return 2; }
string folder = args[0];
Directory.CreateDirectory(folder);
bool sized = args.Length > 1 && int.TryParse(args[1], out _);
int size = sized ? int.Parse(args[1]) : 512;
var ids = args.Skip(sized ? 2 : 1).ToArray();
if (ids.Length == 0) ids = GroundTextures.List.Select(l => l.Id).ToArray();

var lits = new List<byte[]>();
foreach (string id in ids)
{
    var clock = Stopwatch.StartNew();
    var t = GroundTextures.Make(id, size);
    clock.Stop();
    lits.Add(GroundSheets.Write(folder, t));
    uint listed = GroundTextures.List[GroundTextures.Index(id)].Average;
    Console.WriteLine($"{id,-14} {clock.ElapsedMilliseconds,5} ms  average #{t.Average:X6} (listed #{listed:X6}, off by {GroundSheets.Off(t.Average, listed)})  roughness {t.Roughness:0.00}  relief {t.Relief * 100:0.0} cm  tile {t.Metres} m");
}
if (lits.Count > 1) GroundSheets.WriteAll(folder, lits, size);
return 0;
