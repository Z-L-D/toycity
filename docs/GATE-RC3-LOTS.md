# Release candidate, part 3: the lot editor

SimCity 4's Lot Editor, in the Godot client. You put a building on a lot, then dress the lot round it with ground,
car parks, fences, plants and props, and the game draws that building type with the whole lot.

- A lot is one to four cells each way (16 m a cell), its front to the street on the south.
- The building can be the asset editor's example house, any asset you've saved, or one of the realistic set's
  models with its own ground taken up.
- Drag out ground and car parks, drag fences from end to end, click to plant and to place props.
- Pick anything up to move it, turn it or take it off. Every change can be undone.
- **Use for a building** builds the lot into one model, its roof and smooth shapes and all, for the building type
  chosen.

## What changed

- **`LotDoc` (ClientModel `Voxels/Assets/LotDoc.cs`):** a lot as a list of what's where, in metres from its
  north-west corner.
  - **The building:** where its model comes from, its corner and its turn. A model is cut to what's there, so it's
    placed by its own footprint, and turning it keeps it about its middle.
  - **Ground:** a patch of lawn, concrete, asphalt, paving slabs (stone with mortar joints every 60 cm), gravel,
    sand, soil, a flower bed (soil with flowers dotted in four colours) or a pond.
  - **Car parks:** asphalt with bays 2.5 m wide and 5 m deep along the longer side, painted lines between them, and
    rows in pairs facing an aisle (6 m, or down to 4 m to fit the second row in). Busy fills 90% of the bays, half
    full 50%, empty none. Cars come in the library's colours.
  - **Fences:** a run at any angle, drawn a column of voxels at a time. Picket (white, pickets and two rails, posts
    every 2 m), post and rail, a brick wall with a concrete coping, a hedge 0.7 m thick and 1.4 m tall, or
    chain-link 1.8 m tall.
  - **Plants:** the plant layer's kinds, broadleaf, conifer, scrub and reeds, plus an orchard's fruit tree (with
    fruit on it) and a clump of flowers. The trees' trunks and crowns are smooth shapes, as the set's are.
  - **Props:** any of the library's props (bench, lamp, tree, car, planter, bin, café table, crate, hedge), turned
    a quarter at a time.
  - Each thing has a seed, so a plant's size and which bays have cars come out the same every time, and adding one
    thing leaves the rest as they were.
  - **Undo:** every change is one step, up to 200, and a change that changes nothing isn't one.
  - **Picking:** a click finds the thing on top: a prop or plant, then a fence, then the building, then the ground.
  - **Building it:** `Compose` makes one model the size of the whole lot, at the finest detail that fits it in 256
    voxels, up to 12.5 cm (the finest the city draws). One cell or two is 12.5 cm, three or four is 25 cm. It lays
    the ground, then patches and car parks, then the building at its place and turn with its roof and shapes, then
    fences, plants and props, each clipped to the lot.
  - **Files:** a lot saves as a small `.lot.json` of what's where. **Use for a building** writes
    `{type}-{w}x{d}.vox` with its sidecar, the whole lot rather than cut down, so it lines up with the lot in the
    city. **Save as an asset** writes it to your assets, to finish by hand in the asset editor.
- **`AssetDoc`:** `FromModel` makes an asset from a finished model, and `UseForBuilding` can write the model whole.
- **`DemoAsset`:** `House(garden: false)` is the brick house and its porch alone. `HouseLot` is the example lot, the
  house with a drive, a car, a paved path, a picket fence with a gap for the path, a hedge, a brick wall, flower
  beds, shrubs, reeds, three kinds of tree, a bench, a lamp and a bin. `ShopLot` is the set's corner shop on a lot two
  cells wide with a car park behind chain-link.
- **The lot editor (Godot `LotEditor.cs`),** opened with **Lot editor** on the asset editor's top bar, or with
  `--lot-editor`:
  - The lot on its cell, with a metre grid, the street and pavement along its front, and land round it.
  - **Tools (keys 1 to 7):** Pick up, Building, Ground, Car park, Fence, Plants, Props, each with its choices
    listed. An outline shows what a click would place, or the patch, car park or fence being dragged. Patches snap
    to half metres, the rest to quarter metres, and Shift keeps a fence straight along the lot.
  - **Pick up:** drag to move, with outlines where it was and where it's going. R turns it, Delete takes it off.
  - **The top bar:** name, size (1×1 to 4×4 cells), New, Example, Open, Save, Undo, Redo, Clear, and back to the asset
    editor.
  - **Use it:** the building type, **Use for a building** and **Save as asset**. A type whose lot is another size
    is drawn with it only where one grows the lot's size.
  - The rest of the lot can be any of the ground surfaces, lawn by default.

## Checks

- **ClientModel `LotDocTests`, 13 new tests:**
  - An empty lot is lawn the size of the lot, at 12.5 cm for one or two cells and 25 cm for three or four.
  - The building stands where it's put: brick only inside its footprint, and its roof finds its walls on the lot.
  - Turning the building turns it about its middle, swaps its sides and keeps every voxel. Four turns bring it
    back.
  - Ground patches lie on the ground: the drive is all concrete, paving has joints, the bed has flowers.
  - Fences run along their line at their height: pickets no higher than 1.1 m, the hedge thick and leafy, and a
    slanting brick wall only along its diagonal.
  - A car park has painted bays, two rows facing an aisle across 14 m, and cars as full as asked: none when
    empty, more when busy than half full. The same seed parks the same cars.
  - Every plant kind builds: the trees as shapes high up, fruit on the fruit tree, reeds standing.
  - Props come from the library and turn, and snap pieces aren't props.
  - Clicking finds the thing on top, and moving it moves it.
  - Every change undoes and redoes, a no-op isn't a step, and a lot saves and opens to the same voxels.
  - Adding a thing leaves the rest as they were.
  - The example lot used for a building is the whole lot, 128 × 128 at 12.5 cm, with its gable roof and shapes.
  - The set's models come without their ground, and a lot two cells wide is 256 × 128.
- **Godot `--lot-editor --shots`,** a new CI step:
  - six shots of the example lots, picking up and moving the car, and dragging out a car park;
  - `LOT MOVED car to 14, 14`;
  - `LOT FILES OK WHOLE r-l-m-1x1.vox 128x128x97 voxels at 0.125 m, 19 things, 7 shapes, roof Gable`;
  - then `LOT EDITOR OK`. It runs in about 20 s.
- Shots are in [`docs/screenshots/rc3-lots`](screenshots/rc3-lots). The city looks the same as in part 2.2, so
  there are no new gallery, hills or web client shots.

## Known limits

- **Only the Godot client has it.** The web client draws buildings from the server's types as before.
- **One building a lot.** Placing another moves it. A lot of several buildings would be a building of its own, made
  in the asset editor.
- **The set's models keep their own trees and props.** Only their ground is taken up, so a set model can bring a
  tree you'd rather move. Save it as an asset and take it out in the asset editor, then use that.
- **A shed roof faces the way it was made.** A turned building's gable and hip roofs follow its walls, but a shed
  roof still rises to the south or east.
- **Flat lots only.** The lot is built flat, as every building is. On a slope the city stands it on its platform.
- **Plants are drawn as voxels and shapes on the lot,** not as the plant layer's swaying trees. Their wind and
  seasons are left for later.
