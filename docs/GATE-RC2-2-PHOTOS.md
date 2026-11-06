# Release candidate, part 2.2: the photo importer

The asset editor can build a facade from a photo. You open a photo of a building and drag four corners onto the
facade's corners. Then you say how wide and tall it is in metres, and it's built into the model on the front:
straightened, cut to the asset's voxels, its colours matched to the palette, and its windows and doors set back.

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
  - **Windows and doors set back.** The groups that look like openings are set back from the face by 25 cm by
    default: those much darker than the wall, or bluer than they are red, like glass. The player can set back or
    bring forward any colour.
  - **Building it.** The facade goes on the model's front (south) side, centred and standing on the ground, as one
    undo step. Its depth is 0.5 m by default, a facade to put on a building. A deeper one is a solid block with
    sides and top in the wall's main colour. The model grows to fit if it must, which starts the undo history again.
    A facade bigger than a model can hold (256 voxels) is refused, with a note to use bigger voxels.
- **The editor's photo panel** (Godot `AssetEditor.Photo.cs`, `PhotoCanvas.cs`), opened with **From a photo** on the
  top bar:
  - **The photo**, with four corners to drag. Inside them, dashed lines every 3 m (a storey), metre ticks along the
    bottom and a person 1.8 m tall are drawn in the photo's own perspective. You can tell the size is right when the
    lines fall on the floors.
  - **The facade as it will be built**, one pixel a voxel in palette colours, with the set-back parts shaded. Storey
    lines, a person and a 1 m bar give the scale.
  - **Settings:** width, height, depth, how far to set back, how many colours and how many filter passes. A new
    photo's height is guessed from the shape of its corners.
  - **A swatch for each colour.** Click one to set it back or bring it forward.
  - **Open photo…** takes PNG, JPEG or WebP. A photo bigger than 2048 px is cut down to it.
- **Example photo (`DemoAsset.FacadePhoto`).** A made-up photo of a three-storey brick terrace 12 m × 9 m, seen
  from below and to the left, with grain like a real photo. It's used for the checks and the editor's shots.

## Checks

- **ClientModel `PhotoImportTests`, 7 new tests:**
  - A homography takes the four corners where they go and back, and refuses three corners in a line.
  - The keystoned example straightens with glass in every window and brick between them.
  - Read at 25 cm, the facade is 48 × 36 voxels. The wall is brick and stays on the face. The glass in every window
    and the door are set back.
  - Building it is one undo step. Brick is on the face, and at a window the face is open with glass one voxel back.
    Nothing is behind a thin facade, and undo empties the model.
  - A deep facade is a solid block with brick sides and back.
  - The model grows to fit, and a facade too big for a model is refused.
  - Fewer colours and the filter each mesh the wall in fewer triangles.
- **Godot `--editor --shots`:** three new shots and
  `EDITOR PHOTO OK 96x72 voxels, 5 colours, 3 set back` (then `EDITOR OK`).
- Shots are in [`docs/screenshots/rc2-2-photos`](screenshots/rc2-2-photos). The city looks the same as in part 2.1,
  so there are no new gallery, hills or web client shots.

## Known limits

- **One face at a time.** The importer builds the front (south) face only. There's no way yet to put a photo on
  a side or the back.
- **Flat, except what's set back.** Sills, cornices and bays that stick out in the photo are drawn flat on the face.
  Only the set-back colours get depth.
- **Colours are matched by eye, not by material.** A group goes to the nearest palette colour, which may be a ramp
  colour rather than a named material like Brick. Pick a swatch's colour by hand by painting it afterwards.
- **Tested on a made-up photo.** The example is drawn in code so the checks are exact. Real photos with shadows,
  trees or cars in front will need more colours, more filtering or some paint afterwards.
