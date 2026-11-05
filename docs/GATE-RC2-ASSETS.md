# Release candidate, part 2: the realistic asset set

Every building now has a proper model in place of the old placeholder blocks. The models are drawn in code from the
asset library's pieces, the auto-roof and new drawing helpers, at the finest detail each lot allows. Zoned types come
in three variants, so a street of them isn't one house copied, and buildings turn to face their road.

## What changed

- **A model for every type (`RealSet`).** This covers 27 zoned types and 36 civic and reward types. The five
  roadside stops are drawn by the game beside the road, as before.
  - **Houses:**
    - a one-storey painted timber bungalow with a chain-link fence, a shed and an old car;
    - a cottage or two-storey house with a picket fence, a porch, a garage and a garden;
    - a brick or stone villa, often L-shaped, with a hipped roof, hedges, a garage and sometimes a pool.

    They're built of the library's wall, floor and stair pieces at 6.25 cm voxels, under auto-roofs in clay tile,
    slate or shingles. On bigger lots they're bigger houses.
  - **Flats:**
    - walk-ups with fire escapes and washing on the roof;
    - brownstone rows of narrow houses in different shades of brick, with stoops and cornices;
    - garden courts: three wings round a courtyard garden under one hipped roof.
  - **Towers:**
    - a brutalist slab of flats with stacked balconies;
    - slim flats over a podium of shops;
    - a glass spire in setback tiers;
    - a discount tower over a big-box store;
    - a dark stone monolith;
    - a glass exchange with a lit crown and a mast.
  - **Shops and offices:**
    - shop rows with striped awnings and signs;
    - a corner bodega with fruit out front;
    - a gabled stone cheese shop with café tables;
    - a strip mall behind its car park, with a pylon sign;
    - a concrete office block;
    - glass offices with a tall atrium.
  - **Industry:**
    - a tannery with open vats;
    - sawtooth-roofed works with lorries;
    - a clean white lab;
    - a brick foundry with chimneys and slag;
    - a big assembly shed with loading docks;
    - a chip fab crowded with rooftop plant;
    - a smelter with a blast furnace, a stack, ore heaps and a conveyor;
    - a solar-roofed robot plant;
    - a glass lab campus.
  - **Civic:**
    - power: a coal plant with a stack, cooling towers and a coal heap; a gas plant; a hydro plant with penstocks; a
      wind turbine; a solar farm;
    - water and waste: a pump with a water tower; sewage and water works with their tanks; a landfill; a toxic dump;
    - services: police, fire (an engine nosing out of its bay), a clinic, a hospital with a helipad, two schools
      with a playground and a running track, city hall with its dome, the mayor's house;
    - parks and plazas;
    - transit: depots with tracks and railcars, a rail station, a subway entrance, a freight depot with stacked
      containers and a gantry crane;
    - the landmarks: the toy soldier, the rubber duck fountain, the wind-up clocktower, the observation wheel, the
      casino with its lug nut, the pickle plant's giant pickle.
- **Detail.** Each model uses the finest detail that fits its lot, and its height, in a `.vox` model's 256 voxels a
  side, up to a cap:
  - houses are made at 6.25 cm voxels;
  - other one-cell lots are made at 12.5 cm;
  - bigger lots are made at 25 cm;
  - tall towers are coarser, down to 50 cm for the tallest.

  The city draws at 12.5 cm at the finest. A 6.25 cm house has five to seven times the faces (40,000–70,000) for a
  difference a city camera can't show, and drawing it at full detail made the hill run two and a half times slower.
  The full voxels are kept for the editor and for the destruction swap. The set draws in metres, so one recipe
  works at any detail.
- **Variants.** Each zoned type has three variants, which differ in:
  - paint and brick;
  - storeys;
  - roof style and covering;
  - footprint and garden.

  A building's variant comes from its id, so every client draws the same one. Each variant of each lot size is made
  once and shared by every building that uses it.
- **Facing the road.** A building turns to face the side of its lot with the most road beside it, south first when
  that's a tie or there's no road.
  - Square lots turn any way.
  - Other lots only turn round, never a quarter.
- **Files still win.** A type with its own `.vox` or glTF file, from an artist or from the asset editor's "Use for a
  building", is drawn from the file as before. The placeholder blocks are now only a fallback.
- **A gallery.** `--gallery DIR` lays every model in the set out by family and saves a picture of each group. The CI
  run takes it and adds a contact sheet (`GALLERY-SHEET`) to its log.

## Checks

- ClientModel `RealSetTests`, 6 new tests:
  - every type except the roadside stops has a model on every lot size it grows on, in every variant;
  - each model covers its lot exactly, is at most 256 voxels a side, and covers its ground (432 models in all);
  - each model uses the finest detail that fits, and houses use 6.25 cm;
  - variants differ; a building always gets the same variant; 300 buildings spread over all three; and making a
    model twice gives the same one;
  - houses, garden courts, the cheese shop and the pitched sheds have an auto-roof that finds their walls (eaves
    2.5–12 m up), not a fence or a tree;
  - a villa meshes to under 200,000 quads (52,334), and its far model to under a quarter of that (92);
  - buildings face the road: each of the four sides, ties, the longer road on a big lot, and only turning round on
    a lot that isn't square.
- Godot `--gallery`: all 63 types, 9 pictures (`GALLERY OK`).
- Godot `--hills`: the hill town drawn with the new set (`HILLS OK`). It made 74 models in 45 s on this machine,
  with 1.2–3.9 million triangles a view.
- Shots are in [`docs/screenshots/rc2-assets`](screenshots/rc2-assets).

## Known limits

- **Models are made on the main thread** the first time a type, lot size and variant is drawn. Each takes 0.1–1 s
  to make and mesh, so a new town takes a little longer to appear the first time.
- **The web client is unchanged.** It draws the city as a map, not models.
- **A lot that isn't square** can't face a road on its long side.
- **No interiors.** Floors and stairs are inside the houses, but the towers are hollow above their floors.
- **The roof and the walls are drawn alike when wrecked.** The destruction swap is next: buildings drawn as
  polygons, switching to their voxels when something wrecks them. The photo importer comes with it.
