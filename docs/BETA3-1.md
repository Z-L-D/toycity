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

- **Embankments.** Levelling used to leave a sheer cliff where the level met the land (the CI screenshots showed the
  starter town on a plateau of rock walls). Now levelled ground meets the land around it on an embankment: each
  corner up to 12 cells out comes to within 3 m a cell of the level, about 1 in 5, cut or fill. Ground by the water
  is left as it is, so levelling never fills the sea, and the embankment comes with the levelling at no extra cost.

### Tests

`LevelledGroundMeetsTheLandOnAnEmbankmentNotACliff` levels the hilliest square on a map and checks every corner
around it is within the embankment's slope, and that only the brush is paid for.

`RiverTests` covers five things:
- Rivers never climb on the way down, and each river cell is water.
- A river crosses a tile border, and a tile gets the region's rivers.
- A road bridges a river at five times the cost, the river keeps its bed, and the sea still can't be crossed.
- A flood covers only low dry ground near the river, damages the building there, survives a save, and recedes.
- Over three years of fronts some tile floods, and no flood lasts for months.

Four tests whose sites a new river crossed now use other seeds. The computer-mayor deal test lets its power line
cross a river, since lines may now.

## Part 6: neighbours that build to meet you, and buses that run on

### Computer mayors build out to the border

Once a month, a computer mayor (or any city on autopilot) looks along each side for a neighbour's road, power line
or water pipe that reaches the border with nothing on its own side to meet it, and builds one, within the month's
spending cap. At most one link of each kind a month (`World.AutopilotBorders`, `src/ToyCity.Sim/BorderBuilding.cs`).

- **Roads take the cheapest way, not the shortest.** A Dijkstra search from the border cell inward costs each step
  10, plus 30 for every step of climb from the last cell (that's the grading), 60 more for a bridge and 4 for a
  turn. So the road follows valleys, keeps fairly straight, and crosses a river only where it must. It's cut into
  straight runs and priced as the server would price them, grading and bridges included, before anything is built.
- **It always leaves the border straight inward,** so the crossing counts for traffic.
- **It joins the town's streets,** the ones that lead to some building's door, not a stray stub.
- **If the money runs out partway,** the half-built road is picked up next month from where it got to, rather than
  left as a stub that blocks the crossing for good.
- Power lines and pipes take the shortest way to the nearest part of their network, and may cross rivers.
- The mayor gets an away note: "Built a road out to meet Westbrook's at the border."

### Bus lines run on into the neighbouring city

A bus line can now carry on past its last stop, over a road crossing into a neighbour.

- The city tells the company where its working crossings are (contract v1.4 `CityToGame.Crossings`: the side, the
  neighbour's name, the crossing cell and the people crossing a day).
- In the company panel's line editor, under the fare, "Runs on into" lists the neighbours a road reaches. Pick one
  and the line's route extends from its last stop to that crossing, with 12 more minutes for the run beyond it. The
  line list shows "→ Botton" after its stops. Only buses run on; trams, trains and freight don't.
- A line lives in one city's state, so what it carries over the border comes from the border's flows, not from
  the neighbour's people. Of everyone crossing that side (commuters, shoppers and visitors), it carries a share
  that grows as it runs more often: 30% at a bus every 10 minutes, at most 40%, and never more than its seats for
  the 16 service hours. They board at its last stop.
- Its seats count toward the side's road capacity, as a shuttle deal's do, so more people can cross.
- The company's save keeps the run-on and the crossings; older saves load without them.

### Tests

- `AComputerMayorBuildsOutToMeetANeighboursRoadLineAndPipe`: a player brings a road, a line and a pipe to the
  border; within a few months the computer next door has met all three, the road reaches its town, the crossing
  carries traffic, and the computer's city still replays alone from its log.
- `ABusLineRunsOnIntoTheNeighbourAndCarriesCommutersOverTheBorder`: a player runs a road to the border and the
  computer meets it. A line asked to run on over a border with no crossing is refused. One run on to the
  neighbour has no problem, names the neighbour, carries through riders and raises the side's capacity, and keeps
  running on after a save.

## Part 7: the HTML client catches up

### Dispatch is a screen of its own

The 🚒 Dispatch button no longer opens a board over the map. As in the Godot client, it switches to the emergency
services' own screen, in place of the city's (the same way the bus company has its own):

- **Top bar:** the service's name (with how many calls wait), the city's date and speed, its money, respect and
  service, who runs it, and "Run it yourself" or "Hand back to the computer". 🏙 City goes back; the service carries on.
- **Left:** the board's pages (Calls, with a badge for calls waiting, Units, Reports, Settings, Log). In a scene,
  what to do with it (Return to calls, 📍 Where is this?, ▶ Run / ⏸ Hold, Hand to crews) and the eleven orders.
- **Middle:** the city map, dimmed, with every open call marked; calls waiting for units pulse red. When a scene
  opens it fills the middle, scaled to fit. "Where is this?" puts it aside to show the city around it.
- **Right:** the board's page, or in a scene who's selected, who is on the way and what has happened.
- **When a scene ends** it stays up as it finished, under its report card (score, response, saved, lost, property
  lost, rules of engagement, notes), until the player goes back to the calls. Escape does the same.
- A player's scene that opens comes up by itself, switching to the service's screen. Opening the bus company leaves
  dispatch, and the other way round. `?view=dispatch` opens straight to it.

### Waits

Anything the server takes a moment over now shows at once:

- **The bus company:** taking it over, handing it to the computer, leasing a lot and giving one up show
  "⏳ Taking over the company…" and so on in the top bar and panel until the company's view shows it done (it hears
  at the start of the city's next day). A refusal clears it. Godot already did this.
- **New, Save and Load** put a "working" card with a spinner in the middle of the map until the new city arrives,
  the save list comes back, or an error does.

### Realistic map colours

The 2D map now looks like the 3D client's ground from straight above:

- **Ground from the corners.** The server already sends the ground's corners; the map draws its surface from them,
  so slopes and embankments are smooth, not stepped by whole cells.
- **Colours by height and slope,** as the terrain shader has them: meadow, drier grass higher up, earth then rock as
  it steepens (measured the same way, one less the ground normal's up part), a sandy shore, rock then snow on the
  heights, with patches of texture a few cells across. Light comes from the north-west.
- **Water** is teal over a bed that darkens with depth, with the odd glint.
- **Rivers** are water at their own surface; **floods** lie muddy over the low ground beside a river in flood,
  worked out exactly as the server does.
- **Roads** follow the same look rules as Godot's: concrete sidewalks with asphalt between in town, a double yellow on
  busier roads and none on slow streets, dashed white on one-ways, edge lines and a barrier on highways, a grass or
  tram median on avenues, earth for dirt roads, ballast for rail. Painted lines stop at junctions.
- **Zones** are lighter tints in the 3D client's zone colours; a scene's tiles use the same ground colours.
- **Bridges** preview as the server prices them: a river cell costs five times, the sea still can't be crossed, and
  the grading treats the deck as fixed.

### Bus lines run on

The web line editor has the same "Runs on into" choice as Godot's (listing each neighbour a road reaches, with how
many people cross a day), and the line list shows "→ Botton" after the stops.
