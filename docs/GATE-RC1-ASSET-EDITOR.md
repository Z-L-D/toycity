# Release candidate, part 1: the asset library and editor

The first part of the release candidate gives the game its own voxel asset tools. There is a library of prebuilt
pieces and props, and an editor to build assets from them and by hand. The building is on a snap grid, after
Fortnite, and the hand work is voxel by voxel, after MagicaVoxel. An asset can then be used as a building type's
model.

## What changed

- **The library (`ClientModel/Voxels/Assets`).**
  - **Snap pieces** fill a frame one grid module square (4 m) and one storey tall (3 m):
    - walls: plain, with a window, with a wide window, with a door, with an arch, and half height;
    - floors: plain and with a stairwell;
    - stairs;
    - roofs: sloping, hipped, flat with a parapet, and gable ends rising either way to close a sloping roof;
    - a pillar and a fence.
  - **Six materials** for the pieces: wood, brick, concrete, stone, glass and metal. Each has a main colour and a
    trim. Brick is speckled with darker bricks.
  - **Props:** a bench, a street lamp, a tree, a conifer, a car, a planter, a bin, a café table, a crate and a hedge.
  - **Built in code at any detail.** Every piece is generated rather than stored, so it is made at the asset's detail:
    - 1 is 0.5 m voxels, the buildings' own scale;
    - 2 is 0.25 m voxels;
    - 4 is 0.125 m voxels.
  - **A shared palette (`AssetPalette`).** It starts with 31 named colours (each material's two, roof tile, slate,
    leaves, bark, asphalt, paint, lamp light, water and more), then a ramp of 14 hues by 16 lightnesses and greys,
    in the style of MagicaVoxel. Every asset starts on it, so pieces from one asset match those in another.
- **The editor document (`AssetDoc`).**
  - **By hand:** set, erase and paint voxels, fill a box (or empty it), paint-bucket a joined patch of one colour,
    mirror the model east to west, or copy the west half onto the east.
  - **From the library:** place a snap piece in a grid frame at a storey, turned a quarter at a time about the frame.
    A wall on the north edge turns onto the east, south and west edges. Only a piece's solid voxels are set, so a
    wall and a floor share a frame.
  - **Props and saved assets** go anywhere. A saved asset made at another detail is scaled to fit, and its colours
    are matched to this palette if it came from somewhere else.
  - **Undo and redo:** every change is one step, and a drag is one step. History is kept to 200 steps.
  - **Files:** an asset saves as a MagicaVoxel `.vox` file, so it opens in MagicaVoxel and comes back. A small
    `.asset.json` beside it keeps the asset's name and detail. A plain `.vox` from anywhere opens at detail 1.
  - **Use for a building:** writes `{type}.vox`, cut down to what's there and standing on the ground, with its detail
    beside it.
  - **Picking (`VoxRay`):** a ray walks voxel by voxel to the first solid voxel and the face it entered by. Missing
    everything, it lands on the ground under the model.
- **The asset editor in Godot (`AssetEditor.cs`).**
  - It opens from the game menu's **Asset editor**, in a window of its own, or with `--editor`. It needs no server.
  - **Across the top:** name, voxel size, New, Open and Save (your assets live in `user://assets`), Import and
    Export `.vox` from anywhere, Undo, Redo, Mirror, Symmetry, Clear, and **Use for a building** with a list of the
    game's building types.
  - **Down the left:** the tools (Place, Add, Erase, Paint, Fill, Box, Pick), the six materials, and the library.
    Each library piece has a thumbnail drawn from its voxels, sorted by kind. Under them is **My assets**, your saved
    assets to place in others.
  - **Down the right:** the palette, with the named colours and the ramp.
  - **In the view:**
    - Right-drag orbits the camera, middle-drag moves it, and the wheel zooms.
    - The grid shows the modules at the current storey. PgUp and PgDn change the storey, and R turns the piece.
    - A ghost of the piece shows where it would go. A cursor box shows the voxel that would be added, erased or
      painted.
    - Keys: Ctrl+Z, Ctrl+Y and Ctrl+S; 1–7 pick the tools; Alt+click picks a colour; M mirrors; F frames the model.
    - Adding by dragging stays on the plane it started on, so it doesn't climb toward the camera.
- **The game draws editor-made buildings (`BuildingModels`).**
  - Models in `user://vox` (where Use for a building puts them) win over `res://vox`.
  - A model with a detail of 2 or 4 is drawn that much smaller, so a fine model keeps its size on the lot.
  - Its far and middle zoom levels are shrunk by its detail too.

## Checks

- ClientModel: `AssetEditorTests`, 9 tests:
  - every piece builds in every material and at every detail; a snap piece is exactly its frame, and only named
    colours are used;
  - a wall turns onto each edge of its frame;
  - pieces in one frame stand together;
  - undo and redo step through every kind of change, and a group undoes as one step;
  - paint only recolours what's there, and the bucket fills a joined patch;
  - a ray finds the face it hits, or the ground;
  - an asset saves and opens with its name and detail; a plain `.vox` opens at detail 1; "use for a building" cuts
    the model down and keeps its detail;
  - a model of another detail is scaled to fit, and another palette's colours are matched;
  - the demo house builds, meshes and undoes back to nothing.
- Godot `--editor --shots DIR`, a new CI step:
  - builds the example house at 0.25 m;
  - takes pictures of it, of a piece being placed and of the props at 0.125 m;
  - saves and reopens the house and checks every voxel, its name and its detail;
  - uses it for a building into a scratch folder, so the other runs' pictures don't change;
  - prints `EDITOR OK`.

  The contact sheet is in the job log between `EDITOR-SHEET-BEGIN` and `EDITOR-SHEET-END`.
- Shots are in [`docs/screenshots/rc1-asset-editor`](screenshots/rc1-asset-editor).

## Known limits

- **Assets aren't shared over the network yet.** An asset used for a building is drawn only by the client whose
  `user://vox` holds it. Other players see the usual model. Sharing assets with a city is for the mod API (RC part 8).
- **The game reads a new model the next time it starts.**
- **The palette is fixed.** You can't change its colours yet. A `.vox` from MagicaVoxel keeps its own palette when
  opened, but colours placed from the library are matched to it.
- **Fine assets are drawn at full detail up close.** A 0.125 m model of a whole lot is a lot of triangles, and its
  smooth shapes are cut to the graphics options' budget like any other model.
- **The editor is in Godot only.** The web client stays the debug and admin client.
