# Beta 1: the Godot client

Beta 1 makes a Godot 4.7.2 .NET client (`clients/godot`) the main way to play. It talks to the same server
over the same WebSocket and protobuf messages as the web client, which stays as the debug and admin tool.
The plan: https://claude.ai/artifact/7UYCutD5Nbg4KdyMK7ivqo

## Running it

1. Start the server: `dotnet run --project src/ToyCity.Server`
2. Open `clients/godot` in Godot 4.7.2 .NET and press Play (F5). The first open builds the C# project.

Command-line options go after `--`, for example
`godot --path clients/godot -- --server ws://otherhost:5180/ws`:

| Option | What it does |
| --- | --- |
| `--server URL` | the server's WebSocket (default `ws://localhost:5180/ws`) |
| `--smoke` | connect, check the city loads and picking works at all 4 rotations, then quit (CI) |
| `--shots DIR` | with `--smoke`, also save a screenshot of each camera view |
| `--bench FILE` | draw the dense benchmark city and write frame times, triangles, draw calls and memory per zoom level to FILE |

## Code layout

- `src/ToyCity.Wire`: the generated protocol, shared by the server and every .NET client.
- `src/ToyCity.ClientModel`: everything a client knows that isn't drawing. `CityMirror` keeps a copy of the
  city from Full and Delta messages and tracks what changed; `ClassicView` is the camera's geometry and
  picking; `Voxels/` holds the .vox reader and writer, the greedy mesher and the placeholder models;
  `DenseCity` makes the benchmark city; `Tools/Planner` turns a tool and a pointer into a preview and the
  actions to send, with the server's own rules for lines, curves, zoning, footprints and kerbs. No Godot types, so it is all tested in `tests/ToyCity.ClientModel.Tests`.
- `clients/godot/scripts`: the Godot side. `Main` wires it up, `CityView` draws terrain, water and buildings,
  `ClassicCamera` handles input, `MapView` is the minimap, `BuildingModels` turns voxel models into meshes,
  `ToolController` is the toolbar, tool previews and notices.

## Classic camera

Orthographic, at the SC4 angle, as in SimCity 4.

| Keys | |
| --- | --- |
| `,` `.` | rotate a quarter turn |
| `+` `-`, mouse wheel, Page Up/Down | zoom (5 levels) |
| `T` | top-down view (again to go back) |
| `Y` | low side view (again to go back) |
| WASD, arrows, right or middle drag, mouse at the window edge | pan |
| click the minimap | jump there |

Hovering shows the cell, street or building under the cursor. Picking walks the cells under the cursor's
ray, so it finds the roof of a tall building in front rather than the ground behind it.

## Tools

The toolbar runs across the top; each tool shows its options under it and its keys in the lower left.
The preview tints the cells a click or drag would change (red where it can't) and the tag at the cursor
gives the cost. What the preview accepts is what the server accepts: the planner is tested against it.

| Keys | |
| --- | --- |
| `Q` | query: click a cell to select it |
| `B` | bulldoze (drag an area; `U` for underground lines) |
| `R` | roads, rail and other paths (drag; `C` for a curve: drag the ends, then click to set the bend) |
| `Z` | zone (drag an area; `Z` again cycles R, C, I; `V` density; `G` block zoning by click) |
| `X` | de-zone |
| `L` | power lines and water pipes (`L` again switches) |
| `N` | buildings and roadside stops (click; snaps to the nearest spot that touches a road) |
| `F` | terraform |
| `H` | disasters |
| `Alt` | free placement: no snapping, lines go any way |
| `Shift` | an L-shaped line takes its vertical leg first |
| `Space` | pause or resume |
| `Esc` | cancel a drag, or back to query |

Errors and warnings from the city show as notices above the bottom of the screen.

## Voxels

One voxel is 0.5 m, so a 16 m cell is 32 voxels across. Buildings are MagicaVoxel models (axes x east,
y south, z up), meshed greedily: only faces open to the air are drawn, and same-coloured faces merge.
Each type and lot size has a full-detail mesh for zoom levels 3 and 4 and a quarter-resolution one for
zoom levels 0 to 2, where a full-detail voxel is smaller than a pixel. Buildings of one model are drawn as
instances, batched per 32-cell region of the map so off-screen regions are skipped.

**Art.** Until there is real art, every type gets a placeholder made from its category, density, wealth
and colour. To replace one, put a .vox file in `clients/godot/vox/`: `{type}-{w}x{d}.vox` for a zoned
type on a w × d lot, or `{type}.vox` for any size (scaled down to fit the lot if it's bigger). To start from
the placeholders, `dotnet run --project tools/ToyCity.VoxGen -- vox-placeholders` writes them all as .vox
files to open in MagicaVoxel.

## The frame budget

The target is a 2020 laptop with integrated graphics: 60 fps in the classic view on a 128 × 128 city at full
zoom-out, 30 fps in the free camera, under 2 GB of memory. `--bench` measures the classic view on the dense
city (every dry block built up: about 5,700 buildings).

CI runs the benchmark on every push, but its runner has no GPU and renders in software at about 7 fps
whatever the scene, so its frame times say nothing about the target. Its triangle, draw-call and memory
numbers do: about 1 million triangles and 420 draw calls at the far zoom levels, and 560 MB for the whole
process. Real frame times need a run on real hardware:

```
godot --path clients/godot -- --bench bench.json
```
