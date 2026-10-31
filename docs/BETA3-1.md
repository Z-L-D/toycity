# Beta 3.1: the ground, as it really is

Beta 3.1 moves the terrain to SimCity 4's model, takes the look toward realism, and builds out what Beta 3 left
smaller than planned. It is built part by part. Each part's rules are written down here as they're built.

1. The ground as corners, in metres
2. Mountains and valleys from the generator
3. The Godot terrain renderer
4. Realistic roads
5. Rivers and floods
6. Bus lines into neighbouring cities, and computer mayors who build roads to their neighbours
7. The HTML client catches up
8. Gate

## Part 1: the ground as corners, in metres

- **Corners, not cells.** The ground is a height at every corner of the grid, in decimetres: (width + 1) by
  (height + 1) of them. That's what SimCity 4 kept, and it lets a cell slope any way at all. Neighbouring region
  tiles share their edge corners, so the ground meets exactly at a border.
- **Steps are derived.** The rules that count steps keep working as they did. These are what's water, how steep a
  road may climb (one step a cell), and how uneven a lot may be. A cell's step is its four corners averaged, in
  steps of 2 m. A cell is 16 m across, so one step a cell is a 12.5% grade.
- **Terraform works on corners.**
  - Raise and Lower move every corner under the brush by a step.
  - Level brings them to the first cell's height.
  - Smooth averages each corner with the eight around it.
  - Corners under a building or a path stay put.
  - The ground just outside the brush shares its edge corners, so it slopes up to meet the brush rather than leaving a wall.
- **Roads, pads and the computer's levelling** set the cells' steps they need exactly. The ground then follows:
  every corner they touch goes to the average of the cells around it. So a road climbing a step a cell becomes a
  smooth ramp, a pad is flat, and the land beside slopes to meet them.
- **Saves.** The save format is 8 and carries the corners. An older save loads with each corner the average of the
  cells around it, so its terraces become slopes.
- **Protocol.** The full state carries `corners`, two bytes per corner. A delta lists the corners that moved. A
  client talking to an older server makes corners from the cells.

### Tests

`CornerTests` covers four things:
- Each step is its corners averaged.
- Neighbouring tiles share their edge corners.
- Raising moves corners and the ground beside it slopes.
- Corners survive save and load.

The grading tests now expect the cliff's face to be a sloped cell, not a wall.

## Part 2: mountains and valleys from the generator

- **Lowland** is five octaves of gradient noise. It's sampled through a slow warp, so coasts and ranges bend instead of
  running straight.
- **Mountains** rise where a slow mask, about two cities across, climbs past its middle. There, ridged noise makes
  sharp crests and long spurs, up to 400 m or so. Some maps get a range and some are all lowland and lakes.
- **Rain wears the ground.** Seeded droplets, about one for every two corners, run downhill. They cut where they speed
  up and drop silt where they slow or pool, so valleys deepen and fans spread at their feet.
- **Water.** The lowest 18% of the map is under water, with a bed shelving to 10 m deep, and the shore starts just
  above the waterline.
- **Height range.** The highest step is now 250 (500 m).
- **Regions** generate one map for all their tiles, so ranges cross borders.
- **Automation.** The computer counts a pad's earthworks against its monthly cap before it places a building.
  Sloping ground made that cost show up.

### Tests

`GeneratorTests` checks three things:
- Some seeds have mountains and some don't.
- The same seed always makes the same ground.
- About a fifth of the map is water.

Seven tests that leaned on a particular seed's old map now use other seeds.

## Part 3: the Godot terrain renderer

- **One mesh from the corners.** The ground is the server's corner heights drawn as triangles, each cell split
  north-west to south-east, the same split the client model uses to stand things on the ground. Built cells sit
  flat because their corners are level.
- **Chunks and detail.** The map is drawn in chunks. Chunks near the camera use every corner; farther ones every
  second, then every fourth. Skirts hang under chunk edges so the joins between levels of detail never show sky.
  The "Distant terrain" option (Coarse or Full) keeps every chunk at full detail on high settings.
- **What the ground is made of** goes by slope and height in the shader, as real ground does:
  - meadow and drier grass with patches of bare earth on the flats;
  - earth and then banded rock as the slope steepens;
  - snow above about 300 m where the slope lets it lie;
  - a sandy shore just above the waterline, and a bed darkening with depth below it.
  Noise in world space at three scales breaks every material up.
- **Marks on the ground.** Over that go the city's marks, one texel a cell: asphalt on road cells and a light tint
  on empty zoned land. The minimap uses the same colours.
- **Water** is a flat sheet at the waterline. Its colour comes from how deep the ground under it is, clear green in
  the shallows and dark blue far out, with foam at the shore and ripples that catch the sun.

## Part 4: realistic roads

- **A look for every profile.** `RoadLook` works out how a path looks from its profile, so a custom profile looks
  right too. It follows North American practice, as SimCity 4 did:
  - a residential street has sidewalks and no centre line;
  - a busier two-way road has a double yellow, and lanes going the same way are split by dashed white;
  - a highway has grass verges, solid white edge lines, and from four lanes a barrier down the middle;
  - a boulevard keeps 13 m of carriageway and puts the rest in a grass median, and a tram avenue in a reservation
    with its tracks; a tram street has rails in its lanes;
  - a dirt road is packed earth, a trail gravel, a bike path red, a sidewalk paving;
  - rail is ballast with sleepers and two steel rails.
- **Drawn on the ground.** Each road cell is built from layers (sidewalk, carriageway, kerbs, paint) in pieces about
  a quarter of a cell across, each set on the ground's surface, so a road climbs and banks with the corners. The
  carriageway is a square where roads meet with an arm out to each side it joins. Lines run evenly from cell to
  cell, and junctions of three or more roads get zebra crossings. The asphalt varies a little in shade.
- **Arcs and diagonals** use the same layers along the line the player drew.
- The map is drawn in chunks of 16 × 16 cells; a chunk is rebuilt only when a path or the ground in it changes.
- The ground no longer paints road cells: the roads are their own shapes. The minimap still shows them in asphalt.

### Tests

`EachRoadLooksLikeItsKind` checks the look of each built-in profile, that every carriageway fits its cell, and that
a road joins roads beside it but not a rail line.

## Part 5: rivers and floods

- **Where rivers run.** The generator works out where rain drains. It floods inward from the sea and the map's
  edges, lowest first, so every cell (even one in a hollow) has a way out, and counts the cells draining through each
  one. Where more than 700 do, a river runs. A region does this on its whole map, so a river runs on from one tile
  into the next.
- **Its own water.** A river's surface follows the ground down and never climbs. Where the ground rises across its
  way, the rim of a hollow, it cuts a gorge through. Its bed is a step (2 m) under the surface and the banks slope
  down to it. Each cell keeps its river surface (`World.River`, saved; `rivers` in the full state). A river cell is
  water, so nothing is built or zoned on it.
- **Bridges.** A road or rail can cross a river, not the sea or a lake. Over the river it's a bridge: the deck is
  fixed a step over the water, the approaches grade to meet it, and the river keeps its bed. A bridge cell costs five
  times the path. Power lines and pipes can cross a river too.
- **Floods follow the rain downstream.** Each day the region works out how high each tile's rivers stand. Heavy rain
  over any stretch upstream raises them a step; storms raise them two. The rise arrives after the days the water
  takes to come down (48 cells a day) and stays two days after the rain. Storms over much of the catchment at once
  make it three. So a storm over the hills floods the town below a day or two later, across a border if the river
  crosses one. The region sends the stage as a logged action (`FloodAction`), as it does the weather. A lone city has
  no region sky, so it has no floods.
- **What floods.** The water spreads from the river over dry ground lower than its raised surface, through cells it
  covers, up to 5 cells out. Every building standing in it loses 6 condition a day. The city hears when the river
  rises, with how many buildings and road cells are under water, and again when it's back in its banks. The client
  works out the same water from the stage.
- **Drawn.** The Godot client draws the river's own sheet of water, its corners averaged so the surface runs down
  the valley, and the floodwater the same way, both with the sea's material. Bridges are flat decks with concrete
  parapets, the slab's edge and a pier. The minimap shows floodwater.

### Tests

`RiverTests` covers five things:
- Rivers never climb on the way down, and each river cell is water.
- A river crosses a tile border, and a tile gets the region's rivers.
- A road bridges a river at five times the cost, the river keeps its bed, and the sea still can't be crossed.
- A flood covers only low dry ground near the river, damages the building there, survives a save, and recedes.
- Over three years of fronts some tile floods, and no flood lasts for months.

Four tests whose sites a new river crossed now use other seeds. The computer-mayor deal test lets its power line
cross a river, since lines may now.
