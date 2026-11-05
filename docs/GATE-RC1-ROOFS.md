# Release candidate, part 1.1: finer houses and the auto-roof

Two changes Zac asked for after the asset editor went in. Houses are now made of much finer voxels, and the walls get a
Sims-style roof made of smooth polygons.

## What changed

- **Houses default to 6.25 cm voxels** (about 2.5 inches), 8 times finer than before.
  - Detail 8 joins 1, 2 and 4, and new assets start at it.
  - It is the finest size at which a whole 16 m lot still fits in one MagicaVoxel model, which is at most 256 voxels a
    side. One inch voxels would need several models for each house and about 156 million voxels for each lot.
  - The example house at this size is about 0.9 million voxels and meshes to about 37,000 quads. Far off, it is 87
    quads.
  - The game shrinks a detail-8 model to its lot like any other fine model.
- **The auto-roof (`AutoRoof`).**
  - **Where it goes.** It finds the top of the walls: the highest level that a straight run of wall at least a module
    long tops out at. A tree's crown or a lamp has no such run, so it isn't mistaken for a wall. The roof covers
    everything those walls enclose, whatever its shape.
  - **Styles:**
    - **gable:** two slopes to a ridge along the longer way (or turned), with the gable ends filled in the walls'
      colour;
    - **hip:** every side slopes. On a footprint of right angles this is the footprint's chessboard distance from its
      outline, so an L or a T gets hips at its outer corners and valleys at its inner ones;
    - **shed:** one slope;
    - **flat:** a slab over the walls.
  - **Settings:** pitch (10–60°), overhang (0–100 cm), and a covering: clay tiles, slate, shingles, metal, wood
    shakes or a green roof.
  - **Shape.** It is made of real polygons. Faces on one plane merge into rectangles, and only the cells a hip or
    valley crosses are folded along it. It has a fascia round the edge and a soffit under the overhang. A roof is a
    few thousand triangles, against tens of thousands of quads for the voxel walls under it.
  - **Saving.** Only the settings are saved, in the asset's `.asset.json`. The roof is made again whenever the walls
    change.
- **The editor** has an Auto-roof section: style, covering, pitch, overhang, and "turn the ridge". The example house
  now has an auto gable roof, and a new example has an L-shaped stone house under a hipped slate roof. The voxel roof
  pieces stay in the library for roofs drawn by hand.
- **The game** draws the auto-roof on a building whose model has one (from "Use for a building"). It is the same
  roof at every zoom level and in the smooth shapes, since it is polygons already.

## Checks

- ClientModel `AssetEditorTests`, 17 tests (8 new):
  - houses default to detail 8, and a 16 m lot is 256 voxels across;
  - the detail-8 house meshes to under 200,000 quads;
  - the auto-roof finds two storeys of walls and the house's two-module square at details 1, 2 and 8, and ignores the
    porch, garden and trees;
  - each style, at a 40° pitch, rises the expected height over the eaves, spans the house plus its overhangs, has
    unit normals, and has fewer triangles than half the walls';
  - a hip roof follows the L, its ridges no higher than the wings' half-width allows;
  - the roof saves and opens with the asset, survives "use for a building", and is found again on the cut-down model.
- Godot `--editor --shots`: the house and the L house with their auto-roofs, placing a piece at 6.25 cm, and the
  save, reopen and use-for-a-building check (`EDITOR OK`). Shots are in
  [`docs/screenshots/rc1-roofs`](screenshots/rc1-roofs).

## Known limits

- **Gable ends are flat colour polygons.** They are in the walls' commonest colour, so they lack the speckle and the
  courses of the voxel brick beside them.
- **Faint pinhole cracks can show** along the edges where merged roof faces meet smaller ones.
- **One roof for each enclosed area, at one eaves level.** A house with a lower wing gets the roof over its top
  storey only; give the wing its own asset, or roof it with the voxel pieces.
- **The roof doesn't take part in destruction yet.** The plan is to turn it into voxels with the rest of a building
  when something wrecks it.
