# Release candidate, part 2.2: the photo importer

The asset editor can build a facade from a photo. You open a photo of a building and drag four corners onto the
facade's corners. Then you say how wide and tall it is in metres, and it's built into the model: straightened, cut to
the asset's voxels, its colours matched to the palette, and its windows and doors set back.

- Each colour can be moved in or out by its own distance.
- A photo can go on any side of what's built, and one photo can be wrapped round all four sides.
- A depth map pushes each voxel in or out. Making one from the photo itself, with a depth model on the player's
  machine, is planned. The hook for it is in place.
- A new select tool picks a patch of one colour anywhere in a model and pushes it in or pulls it out.

## What changed

- **`PhotoImport` (ClientModel `Voxels/Assets/PhotoImport.cs`):**
  - **Straightening.** A projective map (homography) from the four corners to a flat rectangle undoes the
    perspective, so a facade photographed from the side or from below comes out square. Each cell is the average of
    up to 4 × 4 samples, so a big photo cut down small doesn't shimmer.
  - **Scale.** The facade's width and height in metres set its size in voxels: 0.5 m ÷ the asset's detail a voxel.
    A 12 m front is 96 voxels across at 12.5 cm.
  - **Colours.** The cells are grouped into a few colours (6 by default) by k-means, started the same way every time,
    so a photo always comes out the same. A group that's only the blur between two others, like the pink line where
    a white frame meets red brick, is folded into them. Each group is then drawn in the nearest palette colour.
  - **Simplifying.** A speckle filter (each pass, a cell takes the group of at least 3 of it and its four neighbours)
    clears lone voxels and ragged edges but keeps square corners and one-voxel glazing bars. Fewer colours and
    straighter edges mean the wall meshes into fewer, bigger flat pieces: on the example, 1,864 triangles at 16
    colours unfiltered against 924 at 6 colours filtered twice.
  - **Each colour's distance from the face.** The groups that look like openings start set back 25 cm: those much
    darker than the wall, or bluer than they are red, like glass. Any colour can be given its own distance, in
    (glass, a doorway) or out (a sill, a cornice, a band). An opening is never cut right through the wall.
  - **Building it on a side.** The facade goes on the side chosen (south, the front, by default; or east, north or
    west), standing on the ground, as one undo step.
    - Seen from outside, it's the right way round on every side: the left of an east face is its south end.
    - On an empty model it goes at that edge, centred. On a model with voxels it goes onto the outside of what's
      there on that side, centred on it. So a block built from the front photo takes a second, third and fourth
      photo on its other sides.
    - Its depth is 0.5 m by default, a facade to put on a building. A deeper one is a solid block with the wall's
      main colour inside.
    - The model grows to fit on its south and east sides if it must, which starts the undo history again.
    - A facade bigger than a model can hold (256 voxels) is refused, with a note to use bigger voxels.
  - **Wrapping all four sides.** One photo makes a block as wide as the photo and as deep as the depth setting (as
    deep as it's wide when that's under a metre), with the facade on all four sides as one undo step. On a side
    longer or shorter than the photo, the middle is kept and the bays are repeated out to the corners.
  - **Relief.** Each cell can be moved in or out on top of its colour's distance, up to a distance set in the panel
    (0.5 m by default).
    - **From a depth map:** a greyscale picture lined up with the photo, lighter nearer. It's read between the same
      corners, and can be a different size from the photo. The facade's middle grey stays on the face.
    - **From the photo itself: planned, not built.** `IDepthEstimator` and `DepthEstimators`
      (`DepthEstimate.cs`) are the hook for a depth model run on the player's machine. `DepthEstimators.ToDepthMap`
      turns a model's raw output into a depth map, which then takes the same path as a loaded one. None ships, so
      the panel's **Depth from the photo** button is off, with a note to load a depth map instead. The plan is
      [docs/notes/depth-from-a-photo.md](notes/depth-from-a-photo.md): Depth Anything V2 Small (Apache-2.0, about
      100 MB, downloaded on first use) through ONNX Runtime. Marigold and the bigger Depth Anything models are ruled
      out on weight and licence.
- **The editor's photo panel** (Godot `AssetEditor.Photo.cs`, `PhotoCanvas.cs`), opened with **From a photo** on the
  top bar:
  - **The photo**, with four corners to drag. Inside them, dashed lines every 3 m (a storey), metre ticks along the
    bottom and a person 1.8 m tall are drawn in the photo's own perspective. You can tell the size is right when the
    lines fall on the floors.
  - **The facade as it will be built**, one pixel a voxel in palette colours, with the set-back parts shaded. Storey
    lines, a person and a 1 m bar give the scale.
  - **Settings:** width, height, depth, how far to set back, how many colours and how many filter passes. A new
    photo's height is guessed from the shape of its corners.
  - **A swatch for each colour,** marked ↓ when set back and ↑ when it stands out. Click one, then set its distance.
  - **Relief:** open a depth map or none, and how far it reaches. **Depth from the photo** is there but off until a
    depth model is installed.
  - **The side** to build on, **Build it**, and **Wrap all four sides**.
  - **Open photo…** takes PNG, JPEG or WebP. A photo bigger than 2048 px is cut down to it.
- **The select tool (key 9) in the asset editor** (`AssetEditor.Select.cs`, `AssetDoc.SelectFace` and `Extrude`):
  - Click a voxel to select the patch of its colour showing on that side, joined across the face. Shift+click selects
    every patch of that colour facing that way, like all the windows on a front.
  - Press + to pull the selection out a voxel, or − to push it in. Each press is an undo step.
  - Pulling out adds voxels of each one's colour into open space. Pushing in moves the patch back with its colour
    and stops at the last voxel rather than holing the wall.
  - The selection shows as a blue sheet. Esc lets it go.
- **Example photo (`DemoAsset.FacadePhoto`).** A made-up photo of a three-storey brick terrace 12 m × 9 m, seen
  from below and to the left, with grain like a real photo, and its depth map. They're used for the checks and the
  editor's shots.

## Checks

- **ClientModel `PhotoImportTests`, 17 new tests (14 facts, plus one run for each side):**
  - A homography takes the four corners where they go and back, and refuses three corners in a line.
  - The keystoned example straightens with glass in every window and brick between them.
  - Read at 25 cm, the facade is 48 × 36 voxels. The wall is brick and stays on the face. The glass in every window
    and the door are set back.
  - Building it is one undo step. Brick is on the face, and at a window the face is open with glass one voxel back.
    Nothing is behind a thin facade, and undo empties the model.
  - A deep facade is a solid block with brick sides and back.
  - The model grows to fit, and a facade too big for a model is refused.
  - Fewer colours and the filter each mesh the wall in fewer triangles.
  - A colour given a distance out stands out of the face, and the face moves in to make room.
  - On each side, the facade faces outward the right way round.
  - A second photo goes onto the side of a block that's already there, centred on it.
  - One photo wrapped round a 12 m block has its windows set back on all four sides, and is one undo step.
  - A depth map pushes the glass in and the cornice out, also at half the photo's size. Built with it, the cornice
    stands out of the face.
  - A depth model's raw output becomes a depth map either way round, with outliers clipped. No model is installed.
  - The select tool picks one patch or every patch of a colour, pushes it in without holing the wall, and pulls it
    out only into open space.
- **Godot `--editor --shots`:** five new shots, and:
  - `EDITOR PHOTO OK 96x72 voxels, 5 colours, 3 set back`, with the cornice brought out 25 cm;
  - `EDITOR SELECT OK 807 voxels of Slate pushed in 2`;
  - `EDITOR WRAP OK 102x78x72 voxels, relief from a depth map`;
  - then `EDITOR OK`.
- Shots are in [`docs/screenshots/rc2-2-photos`](screenshots/rc2-2-photos). The city looks the same as in part 2.1,
  so there are no new gallery, hills or web client shots.

## Known limits

- **Standing out past the north or west edge is left off.** The model only grows south and east, so a facade on the
  north or west of a model already at that edge loses whatever would stand out past it.
- **No depth from the photo yet.** The relief needs a depth map made elsewhere, for example with a Depth Anything
  web demo, until the planned model is added.
- **Colours are matched by eye, not by material.** A group goes to the nearest palette colour, which may be a ramp
  colour rather than a named material like Brick. Pick a swatch's colour by hand by painting it afterwards.
- **Tested on a made-up photo.** The example is drawn in code so the checks are exact. Real photos with shadows,
  trees or cars in front will need more colours, more filtering or some paint afterwards.
