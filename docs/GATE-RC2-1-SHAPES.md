# Release candidate, part 2.1: smooth shapes and the destruction swap

Round things are now real curves. Domes, towers, columns, tanks and trees were stacks of voxels, so they looked
stepped up close and caught the sun badly. They're now drawn as polygons from a 2D profile.

Each building keeps its voxels. When it burns, falls derelict or comes down, the city swaps to those voxels, with
holes knocked through and charred.

## What changed

- **Shapes (`Shape`).** A shape is a 2D profile in metres, made into a solid one of three ways:
  - A **lathe** spins the profile round an upright axis: a dome, a column, a tank or a cooling tower.
  - An **extrude** pulls an outline straight, up from a plan or back from a front or side view: an arched gateway or
    a wall with a curved top.
  - A **sweep** draws the profile along a path: a cornice round a corner or a handrail.

  The profile's straight runs make flat faces. Where it bends gently, the surface is lit smoothly; corners sharper
  than 35° stay sharp, like the rim of a tank. A closed profile makes a solid. An open one makes a thin shell, drawn
  on both sides.
- **Voxels from shapes.** A shape fills its own voxels with the same inside test the polygons follow. These voxels
  are kept for the editor and for wrecked buildings.
- **Drawing shapes over voxels.** The city meshes the model without the shapes' voxels and draws the shapes' polygons
  in their place.
  - The removed voxels become **ghosts**: solid, so the faces round them aren't drawn (a dome's hollow, the roof
    under a tank), but with no faces of their own.
  - The removal reaches most of a voxel past the surface, so no voxel pokes through between a curve's facets.
- **The asset set uses shapes.** `Draw.Cyl`, `Draw.Waisted` and `Draw.Blob` now make shapes. That covers every
  cylinder, cone, cooling tower, dome, crown and tree in the set. Anything thinner than 15 cm stays as voxels: a
  post isn't worth its polygons.
- **The destruction swap.** A building that is burning, coming down, or derelict and below 30% condition is drawn
  as its whole voxel model, shapes included.
  - That model is wrecked by `Wreck.Carve`: holes are knocked through the roof and the walls, their edges are
    charred, and rubble lies in heaps below.
  - Anything the holes cut loose, like a chimney's top or half a crown, is taken away.
  - The holes come from a seed, so a building is wrecked the same way every time.
  - The roof goes with the fire.
- **The editor's shape tool.** The shape tool (key 8) has a panel with a profile canvas:
  - The canvas has guides for scale: half-metre and metre lines, a line every storey (3 m), a person 1.8 m tall, a
    1 m bar, and the axis for a lathe.
  - For a lathe, the other half of the profile is shown faintly so the outline reads as a solid.
  - Click to add a point, drag a point to move it, right-click to take one away, and use the wheel to show more or
    less. Points snap to 12.5 cm.
  - Presets give a starting profile: column, dome, tank, vase, ring, arch, cornice and rail.
  - Place a shape with a click, and turn it with R.
  - A sweep's path is clicked out in the view. Enter places it and Esc starts again.
  - "Show voxels" shows what's kept for wrecking.
  - Adding a shape is one undo step. Mirror, symmetry and clear move or take away shapes with the voxels.
  - Shapes save in the asset's sidecar, so "Use for a building" keeps them, and the city draws them smooth.
- **The example rotunda (`DemoAsset.Rotunda`).** A round temple built with every kind of shape:
  - a stepped plinth, ten columns, a band, a copper dome and an urn (lathes);
  - an arched gateway (an extrude);
  - a rail round the lawn (a sweep).
- **Gallery.** Two new groups:
  - `shapes`: the coal plant, the water tower, city hall and a house, drawn as voxels and then with shapes;
  - `wrecked`: four buildings whole and wrecked.

## Triangles

The shapes are lighter than the voxels they replace:

| Model | As voxels | With shapes |
| --- | --- | --- |
| Coal plant | 31,900 | 8,200 |
| Water pump | 6,700 | 1,700 |
| City hall | 13,500 | 4,100 |

This counts the city hall at 25 cm and the others at their drawn detail.

A round shape gets a side for about every 1.2 m of its rim, with at least 12 sides and at most 32.

## Dense-city benchmark

The CI benchmark (`--bench`, High, software OpenGL on the runner) now times frames by the wall clock. Godot caps a
frame's delta at 8 physics steps (133 ms), so on software OpenGL every level read 133 ms for frames that took about a
second, and the "8 seconds" measured at each level ran for nearly two minutes. That took the step past its 10-minute
limit. With the wall clock the run takes about 4 minutes here, and the times are true:

| Zoom | Frame (software OpenGL) | Triangles | Draw calls |
| --- | --- | --- | --- |
| 0 | 973 ms | 3,152,591 | 1655 |
| 1 | 1290 ms | 3,144,089 | 1601 |
| 2 | 977 ms | 2,477,645 | 1109 |
| 3 | 1478 ms | 6,958,423 | 721 |
| 4 | 1337 ms | 6,401,457 | 498 |

Against part 2 (PR #27), the closest zoom draws 6.4M triangles instead of 10.1M. Software OpenGL times say nothing
about a real graphics card; compare the triangle and draw call counts.

## Checks

- **ClientModel `LatheShapeTests`, 10 new tests:**
  - A cylinder and a dome are closed with every triangle facing out: their volume from the triangles is within 5% of
    the exact volume, and so are their voxels. The cylinder's rim is creased and the dome's curve is smooth.
  - A ring is hollow. An arch has its opening, its jambs and its depth. A cornice sweeps round a corner with the
    right volume on both legs.
  - Every preset saves to JSON and comes back the same.
  - In the editor, a shape is one undo step. Mirror and clear move or take away shapes. Shapes survive save, load
    and "Use for a building".
  - The rotunda uses all three kinds of shape.
  - Under its polygons, no shape voxel is left in the set's models, and the models with shapes are fewer triangles.
  - Ghost voxels hide faces without drawing any, and survive downsampling.
  - A wreck is the same every time for a seed, holed, charred, and has nothing left hanging.
- **Godot `--editor`:** the rotunda shots, and `EDITOR SHAPES OK 20 shapes` for the shapes saved and read back.
- **Godot `--gallery`:** 11 pictures, including `shapes` and `wrecked` (`GALLERY OK`).
- **Godot `--hills`:** the hill town with smooth shapes (`HILLS OK`).
- Shots are in [`docs/screenshots/rc2-1-shapes`](screenshots/rc2-1-shapes).

## Known limits

- **Only the round things are polygons.** Walls, windows and boxes are still voxels. They mesh flat (a plain wall is
  two triangles), so there's no step to see, but their edges are square.
- **Sloped surfaces not made as shapes still step.** Ramps, sloped ground pieces and the library's voxel roof
  pieces still step. The auto-roof is already polygons.
- **The wrecked look is one level.** It doesn't get worse over a fire, and nothing falls.
- **No photo importer yet.** The photo-to-voxel importer moves to part 2.2.
