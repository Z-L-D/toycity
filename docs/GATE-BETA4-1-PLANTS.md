# Beta 4.1, part 1: the plant layer

Zac, 2026-11-04: "the terrain is largely missing a plant layer - possibly this is something to tackle before RC?
not sure if its just graphic work."

It is more than graphics: the trees are part of the sim, so they cost money to plant and clear, grow back, show in
land value and are the same for every player and in every replay.

## What changed

- **Two new layers in the sim (`Plants.cs`).**
  - Each cell holds a plant kind and a canopy from 0 to 8 crowns.
  - The kinds are broadleaf, conifer, scrub, reeds and orchard.
  - Save format 19 stores both layers. Older saves grow their woods from the region's seed when they load, then
    clear every zoned or built cell.
- **New ground is wooded.**
  - Woods and clearings come from value noise over the region's seed, so they carry on across tile borders.
  - Lone trees stand in the open.
  - The ground decides the kind:
    - Conifers grow high up, and above about 150 m on north-facing slopes.
    - Scrub grows on thin soil and steep ground.
    - Reeds grow at the water's edge on low, flat ground.
    - Broadleaf grows everywhere else.
    - Bare rock, ground under water and the highest peaks stay empty.
- **Building takes the trees.**
  - Any path, building or dam clears its cell the same day.
  - Bulldozing clears trees for $2 a cell.
- **Regrowth.**
  - Each month, open land that isn't zoned can take a tree from a wood beside it, and thin stands thicken.
  - The odds come from a hash of the region seed, the tick and the cell, so other systems' random numbers are
    untouched.
  - A cleared 21 × 21 patch has about 20 cells of trees back after three years.
- **The tree planter (Land → Plants).**
  - It plants trees, broadleaf, conifers or an orchard at $12 a cell, four crowns deep.
  - It skips paths, buildings, water and anything already that thick.
  - It can be picked from the build menu in both clients.
  - It is a new protocol action (`PlantTrees`). The full state and the cell deltas carry each cell's plant as kind × 16 + crowns.
- **Trees matter.**
  - Greenery is the canopy within two cells of a cell.
  - It adds up to 12 to land value and up to 8 to homes' desirability, both shown as "Trees" in the why panels.
  - It takes up to 10 points off a cell's air pollution each day.
- **Godot (`TreesView`, `shaders/tree.gdshader`).**
  - Each crown is drawn as an instance. Crowns are batched by kind in 32-cell squares, and a square is rebuilt only
    when its plants change.
  - Up close each plant is a proper shape:
    - a broadleaf tree is a trunk and two crowns;
    - a conifer is two cones;
    - an orchard tree is a round crown;
    - scrub is low bushes;
    - reeds are a clump of blades.
  - Far off, or in squares more than 40 cells from the camera, a quarter of the crowns are drawn, each bigger and a few
    triangles, with no reeds and no shadows. This keeps the 256 × 256 mountain gate inside its time on CI's software
    renderer: locally it takes 387 s with trees against 236 s without (it was 472 s before the far cut).
  - The trees sway in the wind and darken at night.
  - The broadleaf trees and orchards turn with the calendar:
    - green from late spring;
    - gold and rust in autumn;
    - bare branches in winter.
  - They hold snow on their upper faces.
  - The graphics detail draws a quarter, half or all of the crowns, with bigger crowns when fewer are drawn.
  - The new `--month N` option pins the season for shots.
- **Web.**
  - Zoomed out, a wooded cell is tinted.
  - Closer in, each crown is drawn from above with its shadow. Conifers are drawn as stars, and bare winter crowns
    are drawn smaller and fainter.
  - The same crown layout is shared with Godot (`TreeLayout`).

## Checks

- Sim: 7 new tests in `PlantTests`:
  - generation over three seeds (15–70 % of land wooded, nothing on water);
  - building and bulldozing clear trees;
  - the planter plants and charges, and refuses where there's nothing to plant;
  - regrowth over three years;
  - the land value and desirability factors;
  - save, load and replay give the same plants;
  - two tiles' woods agree across their border.

  All 298 sim tests pass.
- ClientModel: `TreeLayoutTests` checks:
  - the same cell gives the same crowns every time, all inside the cell;
  - the detail levels, with bigger crowns when fewer are drawn.

  All 117 tests pass.
- Godot smoke run: `trees=30839 on 7773 cells` on the 128 × 128 starter map.
- Godot bench (local, software OpenGL, High). The trees add:

  | View | Triangles added | Total with trees |
  | --- | --- | --- |
  | Zoomed out | about 0.4 million | 2.0 million |
  | Closest zoom | about 1.45 million | 4.7 million |

  The frame time is unchanged on this renderer, which is limited elsewhere. A real GPU's numbers will come from
  `--gpu-test`.

## Known limits

- Fires don't spread through woods yet, and woods don't slow the runoff into rivers.
- Trees stand on zoned lots until something is built there. Regrowth stays off zoned land.
- Farm country (part 2) will add fields, hedgerows and windbreaks. Orchards are the only crop for now.
