# Release candidate, part 5: polygon buildings

Buildings can now be made of real walls with real materials, not only voxels. A wall is a slab of brick, render,
stone or clapboard with its texture on it, windows and doors are parts you put in and take out, and photos and
models from other programs come in as textured walls and meshes. When a building is wrecked, its surface turns into
voxels in the colours of its textures, and it breaks up the way the voxel houses do. The voxel tools stay as they
were, as the easy way in.

- **23 textured finishes:** red, buff and painted brick, cream render, stucco, ashlar and rubble stone, clapboard
  siding, board and batten, wood planks, slate, clay tiles, shingles, seamed metal, copper, concrete, glass and trims,
  each a real texture laid on in metres, so a brick is the size of a brick on any wall.
- **Walls with openings:** draw walls round a plan by clicking, then put windows and doors into them. Each opening is
  cut through the wall, with a frame, a sill and glass, and can be taken out again.
- **A polygon house:** "Brick house" builds a two-storey house of walls, windows in bays on each floor, front and back
  doors, lintels, bands, floors, a chimney, and a slate roof whose gable ends are brick.
- **Walled houses in the city:** the realistic set's middle and high-wealth houses now come in a walled version, in
  brick, render, stone or clapboard, with slate, shingles or clay tiles.
- **Photos as textures:** a photo of a facade, straightened, goes on as a textured wall at its real size.
- **glTF and OBJ import, glb export:** models come in with their textures and are scaled to fit; anything made in the
  editor can go out as a .glb.
- **Voxels on damage:** a polygon building's surface is turned into voxels when it's wrecked, each in the colour of its
  texture there, the nearest the asset palette has.

## What changed

- **The client model:**
  - **`Finishes`:** the textured finishes, drawn in code as tileable pictures with a size in metres. Each has an
    average colour that stands in for it far off and in voxels. Photos and model textures are added as "given"
    finishes and saved inside the asset's sidecar, so an asset carries its own pictures.
  - **`Shapes`:** a new Wall kind (a slab with openings cut through it) and an Imported kind (a triangle mesh in
    metres). Both mesh with UVs and finishes, turn into voxels, mirror, and save in the sidecar.
  - **`Parts`:** windows (several sizes, with frames in white paint, dark wood or metal), doors, garage doors and
    arches, each fitted to a wall's thickness and put in from either side.
  - **`PolyBuild`:** builds a walled house from a spec (size, storeys, wall finish, trim, roof, pitch, overhang,
    chimney, garage).
  - **`AutoRoof`:** roofs take a finish, and their gable ends take the walls' finish.
  - **`VoxMesher`:** a mesh carries UVs and a finish per vertex, and a finish can be laid on chosen faces only.
  - **`AssetDoc`:** finds the shape under the cursor, puts a part into a wall or takes one out (one undo step each),
    paints a wall, and imports a model centred and scaled to fit.
  - **`PhotoImport`:** a straightened photo becomes a finish the size of the facade, and a wall at the outside of the
    building.
  - **`ImageDecode`:** reads and writes PNGs without an image library; a client can hand it its own loader for JPEG
    and the rest.
  - **`ObjFile`:** reads OBJ models with their .mtl colours and textures.
  - **`GltfFile`:** reads each material's base colour texture, and writes a .glb with its textures in it.
  - **`MeshVoxels`:** turns a mesh's surface into voxels in its textures' colours.
  - **`RealSet`:** walled houses for middle and high wealth. Each car is now one colour all over (they were picking a
    colour per voxel).
- **The Godot client:**
  - **The Wall tool (key 0):**
    - draw walls round a plan by clicking corners;
    - put a window or door in where you click, or take one out;
    - paint a wall with a finish picked from a list with pictures;
    - height and thickness sliders, the part and its frames;
    - Brick house, Import a model…, Export .glb.
  - **From a photo:** "As a textured wall".
  - **The roof list** has textured and plain versions of each roof.
  - **Drawing:** one surface per finish with its texture, in the editor, the lot editor, the gallery and the city.
    The city's building shader draws them. Textures far off fall back to their average colour.
  - **Imported models in the city** keep their textures close up (up to 200,000 triangles) and are turned into
    voxels at 25 cm when wrecked.
  - **Pictures:** JPEG, PNG and WebP textures are read through Godot.
  - **The gallery** has a walled houses group, drawn as the city draws them.

## Checks

- **ClientModel `PolygonBuildingTests`, 15 new tests:**
  - All 23 finishes tile both ways, have a size in metres and an average colour, and red brick is red with mortar.
  - A wall is a slab, and an opening goes right through it with glass behind.
  - A wall's voxels take its texture's colours.
  - Walls and their finishes survive a save and load.
  - Every part fits the walls it's made for.
  - A finish laid on chosen faces leaves the rest as they were.
  - The polygon house: its gable triangles stand upright and are brick, and it survives a save and load.
  - The realistic set's walled houses.
  - A car is one colour.
  - Parts put in and taken out, with undo.
  - A photo wall, with its texture saved in the asset.
  - A PNG round trip.
  - A .glb export imported again, with its textures.
  - An imported OBJ shape.
  - A glTF building in the city wrecked to voxels.
- **Godot `--editor --shots`, new log lines:**
  - `EDITOR POLY OK 131 walls and boxes, 40 openings, 7 finishes, roof slate, 78829 voxels`
  - `EDITOR WALLS OK 4 walls clicked round, 5 windows and doors put in, 1 taken out, side painted stone, roof tile-clay with render-cream gables`
    (the Wall tool driven by clicks, as a player would)
  - `EDITOR PHOTO WALL OK photo:2f7ef33d 12 x 9 m, 62712 voxels, texture saved with it True`
  - `EDITOR GLB OK exported 561 KB, imported 3,802 triangles with 8 textures, 93,887 voxels kept`
  - `EDITOR OBJ OK 14 triangles, textures model:ca0200fe100100`
  - CI checks for each line.
- **Godot `--gallery`:** 63 types, with the walled houses drawn through the city's building shader.
- **Godot `--gate`:** passes as before.
- **All suites:** ClientModel 203 passed, Sim 321 passed.
- Shots are in [`docs/screenshots/rc5-polygons`](screenshots/rc5-polygons).

## Known limits

- **Wrecks of walled houses show black arcs.** These are the roof shell's charred voxels, the same as for the voxel
  houses.
- **The textures are drawn in code.** They're tileable and the right size, but they aren't photographs. A photo can be
  used as a texture on any wall.
- **Walls are straight.** There are no curved walls, and walls meet at corners by overlapping.
- **Lathe and sweep modelling aren't in this part.** Round towers and mouldings are made with the shape tool's
  cylinders and boxes, or imported.
- **OBJ import reads the diffuse colour and texture only.** Normal maps, roughness and the rest are left off, as they
  are for glTF.
- **Only the Godot client draws the textures.**
