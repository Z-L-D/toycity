# ToyCity

An open-source city simulator in the spirit of SimCity 4: quirky, readable and moddable. The
simulation runs as a headless, authoritative server; front ends only draw it and send actions.
Later phases link side games (transport, emergency services, hospital, zoo, theme park, farm,
life sim) to lots in the city for live joined play.

This repository is working on **Beta 4**, the last beta before the release candidate. Its first part is a **GPU
test**: one command (or a double-click on Windows) builds the game, starts a server of its own, and measures each
graphics preset and lighting level on the hill town on your own graphics card, then writes a report of frame rates,
1% lows, GPU time and draw calls (see [GPU test](#gpu-test) below). **Snow** now lies on the ground and the roofs:
it builds up in the day clock's snow showers and melts with the season, rain and heat. Both clients spread it the
same way, deeper on high ground and on north-facing slopes and gone from the steep. **Water quality** follows what the
city puts in its water: raw sewage, industry and each sewage plant's outfall foul the rivers, lakes and sea, rivers
carry it downstream (into the next city, unless the region keeps each city's water its own), and pumps draw it into
the taps. The mayor picks how far the plants treat sewage, from none (cheap, and the river pays) to tertiary, and a
new water treatment works cleans the mains. **Direct dispatch** puts every fire engine, patrol car and ambulance on
the city's streets as a little vehicle in its colours: on the service's screen, pick them with a click, shift-clicks
or a box, and right-click to send them somewhere, to a call, or after another unit, while the computer still answers
calls with whoever is nearest. **The new look** (variation A of the UI mockups) gives the map the whole window in
both clients. A build rail down the left opens flyouts of priced tiles, sorted into tabs. The city's name and the
modes sit top right. An L in the bottom left holds the minimap and the panels' buttons up its side, and the date,
speed, money, people, demand, news and alerts along the bottom. The panels float on the right and close. The rest of
Beta 4 is planned: region sizes and annexing, and tile hosts for growing regions.

Beta 3.2 ended with **part 7: growing regions**. A region can start as one
tile and grow: add land on any side of it, and the new tile's ground, rivers and rock are made then, carrying on from
the tiles beside it, so hills, coasts and rivers run on across the seam. Each new tile joins as open land that you or
any other player can found a city on; a mayor can add the land beside their own city. **Part 6: light and shadow** made the sun follow the day clock along its path for the
month, from dawn through golden hour to night, and rain and snow grey the day and thicken the haze. At night the
windows light up by how many people are inside, the streets get pools of lamplight and the buses show their lights.
Contact shadows and bounce light come with the better graphics presets, the view tilt-shifts while you query a cell,
and the time of day can be pinned. The web map tints for golden hour and night. **Part 5: subways** made subway tunnels run under anything, laid with the utility tool,
and subway stations stand over them or beside them. The city can run its own subway, or the bus company can lease the
stations and a subway yard and run Metro lines. **Part 4: underground** made every pipe carry a water main and a sewer side by side,
laid with one tool, and sewage treatment plants beside the pipes treat what reaches them. A small town gets by on its
septic tanks; past that, raw sewage fouls the water and puts people off. The underground view (U) turns the ground
to a cutaway and shows the mains, the sewers and the road tunnels beneath it, and a tunnel seen from above is one
dashed outline. **Part 3: weather on the roads** made showers fall in hours of the day clock: roads
get wet, dirt turns to mud, and snow and freezing rain lie on each road until the sun or the road crews clear them,
highways first, as far as the crews' budget goes. Each surface carries less in bad weather, so commutes, buses and
emergency crews slow down, and ice brings crashes. **Part 2: the clock** gave the game two clocks: a day clock for the hours
(commuters, emergency calls, the sun), where a day lasts an hour at speed 1, half an hour at 2 and ten minutes at 3,
and a calendar for the months (growth, building, money, seasons) whose pace the mayor sets, from a month to two years
in each clock day. **Part 1: hills** made paths climb as steeply as their type and surface allow, with the ground
graded to fit them, tunnels under hills, stairs on steep footpaths, soil over rock, and a starter town on a hillside.
Before that, **Beta 3.1: realistic terrain.** A city grows from paths, zones and services, every lot can
tell you why it is the way it is, cities sit side by side in regions with traffic, trade and deals across their
borders, and a bus company and the emergency services run as side games with their own screens. The ground is an
SC4-style heightfield of corners with mountains, rivers and floods. The Godot client in `clients/godot` is the way
to play; the web client stays as the debug and admin client.

## Screenshots

From the Beta 4 and Beta 3.2 gate runs (Godot, CI runner with software rendering) and the web client. Each release
adds a new set under [`docs/screenshots`](docs/screenshots); the Beta 3.1 set is in
[`docs/screenshots/beta3-1`](docs/screenshots/beta3-1).

Rivers, round 2: meanders, rain-fed rivers, the river brush and dams ([`docs/screenshots/beta4-rivers`](docs/screenshots/beta4-rivers)):

| | |
| --- | --- |
| ![Web client, a dam across a river and the reservoir filled behind it](docs/screenshots/beta4-rivers/web-dam-reservoir.jpg) | ![Web client, the river brush digging a channel 48 m wide from a river](docs/screenshots/beta4-rivers/web-river-preview.jpg) |
| ![Web client, a river in flood after weeks of storms](docs/screenshots/beta4-rivers/web-flow-flood.jpg) | ![Web client, the same reach low after a heatwave](docs/screenshots/beta4-rivers/web-flow-low.jpg) |
| ![Godot client (CI, software OpenGL), a dam across a mountain river and its reservoir](docs/screenshots/beta4-rivers/godot-dam-reservoir.jpg) | ![Godot client, the dam close up](docs/screenshots/beta4-rivers/godot-dam-close.jpg) |

River shorelines ([`docs/screenshots/beta4-rivers`](docs/screenshots/beta4-rivers)):

| | |
| --- | --- |
| ![Godot client (CI, software OpenGL), the mountain city's rivers winding down their valleys in shaped channels](docs/screenshots/beta4-rivers/godot-mountain-rivers.jpg) | ![Web client, the whole map: wide rivers with natural shores around the starter town](docs/screenshots/beta4-rivers/web-river-wide.jpg) |
| ![Web client, a river reach close up: the shore where the bank meets the water, a wet band and floodplain](docs/screenshots/beta4-rivers/web-river-close.jpg) | ![Web client, a river beside the town](docs/screenshots/beta4-rivers/web-river-mid.jpg) |

Beta 4, part 6, room to grow ([`docs/screenshots/beta4-room`](docs/screenshots/beta4-room)):

| | |
| --- | --- |
| ![Web client, the region map offering the open land east of Halden, with the price and the rectangle it would make](docs/screenshots/beta4-room/web-annex-offer.jpg) | ![Web client, Halden on two tiles after annexing](docs/screenshots/beta4-room/web-annexed-region.jpg) |
| ![Web client, a careful computer mayor's town next door, for sale at its price](docs/screenshots/beta4-room/web-takeover-offer.jpg) | ![Web client, the one city with both starter towns on its map](docs/screenshots/beta4-room/web-takeover-city.jpg) |
| ![Web client, the region map with Halden on 2 × 2 tiles](docs/screenshots/beta4-room/web-room-region.jpg) | ![Web client, the city of 2 × 2 tiles](docs/screenshots/beta4-room/web-room-city.jpg) |
| ![Godot client (CI, software OpenGL), the staged city of 2 × 2 tiles, its two towns at the lower left and the annexed hills beyond](docs/screenshots/beta4-room/godot-view-1-classic-r1-z1.jpg) | ![Godot client (CI), the region map: Halden named on its top-left tile, and the price of growing to 3 × 2](docs/screenshots/beta4-room/godot-view-19-region.jpg) |

Beta 4, part 2, the new look ([`docs/screenshots/beta4-ui`](docs/screenshots/beta4-ui)):

| | |
| --- | --- |
| ![Godot, the transport flyout open on its roads tab over the hill town](docs/screenshots/beta4-ui/godot-ui-roads-flyout.jpg) | ![Godot, the civic flyout pinned open and the budget panel floating on the right](docs/screenshots/beta4-ui/godot-ui-civic-and-budget.jpg) |
| Godot (local hill gate run): the build rail, the roads flyout with each road's price per cell, and the L | The civic flyout pinned open, and the budget panel floating clear of the L |
| ![Web client, the transport flyout open on its roads tab](docs/screenshots/beta4-ui/web-ui-roads-flyout.jpg) | ![Web client, drawing a street with the tool's options along the top and the cost chip](docs/screenshots/beta4-ui/web-ui-drawing-a-road.jpg) |
| Web client: the same menu, from the same shared description | Drawing a street: its surface, cut or tunnel, and straight, diagonal or curve along the top, and the cost at the cursor |

Beta 4, part 1, direct dispatch ([`docs/screenshots/beta4-dispatch`](docs/screenshots/beta4-dispatch)):

| | |
| --- | --- |
| ![Godot, units driving out across the hill town with the picked ones' routes](docs/screenshots/beta4-dispatch/godot-units-driving.jpg) | ![Godot, units posted on a street, ringed as picked](docs/screenshots/beta4-dispatch/godot-units-posted.jpg) |
| ![Web client, a box over the town picks every unit](docs/screenshots/beta4-dispatch/web-dispatch-box.jpg) | ![Web client, the units driving across town with their routes drawn](docs/screenshots/beta4-dispatch/web-dispatch-driving.jpg) |

Beta 4, part 3, water quality ([`docs/screenshots/beta4-water`](docs/screenshots/beta4-water)):

| | |
| --- | --- |
| ![Godot, the water quality layer below the sewage plant's outfall](docs/screenshots/beta4-water/godot-water-quality.jpg) | ![Godot, the foul water from the free camera](docs/screenshots/beta4-water/godot-foul-river.jpg) |
| Godot (local hill gate run): the water quality layer, with the sewage plant letting out raw sewage | The river mouth from the free camera, now clear of the earlier blizzard's snow |
| ![Web client, the budget panel's sewage treatment](docs/screenshots/beta4-water/web-raw-sewage.jpg) | ![Web client, the water quality layer](docs/screenshots/beta4-water/web-water-quality.jpg) |
| Web client: sewage treatment set to none, at a quarter of the plants' upkeep | The water quality layer: foul water spreading from the outfall into the sea |

Beta 4, part 4, snow ([`docs/screenshots/beta4-snow`](docs/screenshots/beta4-snow)):

| | |
| --- | --- |
| ![Godot, the hill town under snow from the free camera](docs/screenshots/beta4-snow/godot-snow-free.jpg) | ![Godot, the hill town under snow from the classic camera](docs/screenshots/beta4-snow/godot-snow-classic.jpg) |
| Godot (local hill gate run): 10 cm after a blizzard, on the fields, the roofs and the hills; the rock faces stay bare | Classic camera: snow on every roof and ledge, the streets ploughed |
| ![Web map before the blizzard](docs/screenshots/beta4-snow/web-no-snow-map.jpg) | ![Web map after the blizzard](docs/screenshots/beta4-snow/web-snow-map.jpg) |
| Web client: the hill town's map before the blizzard | The same map under 12 cm: white on the gentle ground, bare on the steep, thinner on the south-facing slopes |

Beta 4, part 5, the GPU test ([`docs/screenshots/beta4-gpu`](docs/screenshots/beta4-gpu)):

![The GPU test's summary over the hill town](docs/screenshots/beta4-gpu/godot-gpu-test-summary.jpg)

The summary at the end of a GPU test, here on the CI runner's software renderer (so its frame rates are no guide to a
real graphics card). The full report, per view, is written beside it as Markdown and JSON.

Part 7, growing regions ([`docs/screenshots/beta3-2-regions`](docs/screenshots/beta3-2-regions)):

| | |
| --- | --- |
| ![Web client, a growing region of one tile](docs/screenshots/beta3-2-regions/web-region-one-tile.jpg) | ![Web client, the region grown to six tiles](docs/screenshots/beta3-2-regions/web-region-grown.jpg) |
| Web client: a growing region starts as one tile, with wild land around it to add | Grown to six tiles with a second city; each new tile carries on the coast and hills of its neighbours |
| ![Godot, the grown region's map](docs/screenshots/beta3-2-regions/godot-region-grown.jpg) | ![The ground of five tiles, made one at a time](docs/screenshots/beta3-2-regions/ground-five-tiles.jpg) |
| Godot: the same region's map, with the land beside it ready to add | The gate's five tiles from above, each made when it was added: the seams (faint lines) meet exactly, and rivers run on across them |

Part 6, light and shadow ([`docs/screenshots/beta3-2-light`](docs/screenshots/beta3-2-light)):

| | |
| --- | --- |
| ![Godot, the hill town at golden hour](docs/screenshots/beta3-2-light/godot-golden-free.jpg) | ![Godot, the hill town at night](docs/screenshots/beta3-2-light/godot-night-free.jpg) |
| Godot (CI hill run): golden hour, the low sun warm on the walls and the hills, haze in the distance | Night: lit windows by how many people are inside, a pool of street light on every other road cell |
| ![Godot classic camera at golden hour](docs/screenshots/beta3-2-light/godot-golden-classic.jpg) | ![Godot classic camera at night](docs/screenshots/beta3-2-light/godot-night-classic.jpg) |
| The classic camera at golden hour, long shadows across the streets | The same at night |
| ![Web client at golden hour](docs/screenshots/beta3-2-light/web-golden.jpg) | ![Web client at night](docs/screenshots/beta3-2-light/web-night.jpg) |
| Web client: a warm wash at golden hour | Night: a dark blue wash, lit windows and street lights |

Part 5, subways ([`docs/screenshots/beta3-2-subways`](docs/screenshots/beta3-2-subways)):

| | |
| --- | --- |
| ![Godot underground view with the subway](docs/screenshots/beta3-2-subways/godot-subway-classic.jpg) | ![Godot free camera over the subway](docs/screenshots/beta3-2-subways/godot-subway-free.jpg) |
| Godot (CI hill run): the subway in red under the town, deepest of all, with its two stations reaching down to it | Free camera: the subway under the town's mains and sewers, the stations at its east end |
| ![Subway stations among the houses](docs/screenshots/beta3-2-subways/web-subway-above.jpg) | ![The subway in the web underground view](docs/screenshots/beta3-2-subways/web-subway-underground.jpg) |
| Web client: two subway stations (🚇) in the hill town, the tunnel out of sight beneath them | Underground: the subway (red) under the town, the stations over it |

Part 4, underground ([`docs/screenshots/beta3-2-underground`](docs/screenshots/beta3-2-underground)):

| | |
| --- | --- |
| ![Godot underground view, classic camera](docs/screenshots/beta3-2-underground/godot-underground-classic.jpg) | ![Godot underground view of the tunnel](docs/screenshots/beta3-2-underground/godot-underground-tunnel.jpg) |
| Godot (CI hill run): the ground a cutaway of soil, streets faint overhead, each pipe's water main and sewer beneath | Free camera over the hill: the road tunnel under it at its deck, the town's mains beyond |
| ![The underground view of the hill town](docs/screenshots/beta3-2-underground/web-underground.jpg) | ![A tunnel in the underground view](docs/screenshots/beta3-2-underground/web-underground-tunnel.jpg) |
| Web client, underground view: each pipe's water main (blue) and sewer (brown), the pump and the new sewage plant beside them, the streets overhead faint | The road tunnel under the hill, solid down here |
| ![A tunnel from above, one dashed outline](docs/screenshots/beta3-2-underground/web-tunnel-above.jpg) | |
| Above ground the tunnel is one dashed outline around its whole length | |

Part 3, weather on the roads ([`docs/screenshots/beta3-2-roads`](docs/screenshots/beta3-2-roads)):

| | |
| --- | --- |
| ![Snow on every road in a blizzard](docs/screenshots/beta3-2-roads/web-snow.jpg) | ![Roads ploughed, snow banked at the kerbs](docs/screenshots/beta3-2-roads/web-ploughed.jpg) |
| Web client: a blizzard with the road crews stood down, snow on every road | The crews on double budget: the roads cleared, the snow banked along the kerbs |
| ![The traffic layer in a storm](docs/screenshots/beta3-2-roads/web-rain-traffic.jpg) | |
| Storms: the traffic layer says what the roads are carrying | |

Part 2, the clock ([`docs/screenshots/beta3-2-clock`](docs/screenshots/beta3-2-clock)):

| | |
| --- | --- |
| ![The web client's top bar with both clocks](docs/screenshots/beta3-2-clock/web-clock-city.jpg) | ![The dispatch screen on the day clock](docs/screenshots/beta3-2-clock/web-dispatch-clock.jpg) |
| Web client: "Mon 09:12 · January, year 1", the calendar at a month a clock day | The dispatch screen reads the day clock; its calls wait in hours and minutes |
| ![The Godot client with the clock and pace menu](docs/screenshots/beta3-2-clock/godot-clock-city.jpg) | ![The Godot dispatch board on the day clock](docs/screenshots/beta3-2-clock/godot-dispatch-clock.jpg) |
| Godot client (CI together run): "Mon 12:02 · May, year 1" with the pace menu at two years a day | Godot dispatch board: "Response, last clock day", a response of 21 min |

Part 1, hills ([`docs/screenshots/beta3-2`](docs/screenshots/beta3-2)):

| | |
| --- | --- |
| ![The hillside town](docs/screenshots/beta3-2/godot-town-from-hills.jpg) | ![A road tunnelling under a hill](docs/screenshots/beta3-2/godot-tunnel.jpg) |
| Godot, free camera: the starter town on its hillside | A road tunnelling under a hill, rock bands in the slopes |
| ![A street on the slope](docs/screenshots/beta3-2/godot-street-on-slope.jpg) | ![The town, classic camera](docs/screenshots/beta3-2/godot-town-classic.jpg) |
| A street climbing the slope | Classic camera: the town's streets graded into the hill |
| ![The web client's hill map](docs/screenshots/beta3-2/web-whole-map.jpg) | ![The web client's tunnel](docs/screenshots/beta3-2/web-tunnel.jpg) |
| Web client: soil, four rocks and snow, the same ground as Godot | Web client: the tunnel drawn dashed under the hill |

## Roadmap

Each phase ends with a gate the build must pass before the next begins.

| Phase | What it brings | State |
| --- | --- | --- |
| Pre-alpha | Deterministic sim core, protocol, saves, server, 2D web client | Done |
| Alpha 1–3 | A city that grows, talks back and links to side games; transport alpha | Done |
| Beta 1 | The Godot client, voxel buildings, graphics options | Done |
| Beta 2 | Multiplayer, consent, de-linking, emergency services alpha, transport beta | Done |
| Beta 3 | Regions, cross-border play, federated hosting, emergency services beta | Done |
| Beta 3.1 | SC4-style terrain, mountains, rivers and floods, realistic ground and roads | Done |
| Beta 3.2 | Hills: paths by type and surface, grading, tunnels, stairs, ground in layers (done); two clocks, a day clock and a calendar at the mayor's pace (done); weather on the roads with freezing rain, mud, snow and a road crews budget (done); underground view with water mains, sewers and treatment plants (done); subways (done); light and shadow through the day and night (done); growing regions (done) | Done |
| Beta 4 | GPU test (done); snow on the ground and roofs (done); water quality (done); direct dispatch (done); the floating build menu and L-shaped info bar in both clients (done); region sizes, annexing, taking in a neighbour by a deal and a region charter (done); tile hosts for growing regions (done) | Done |
| Release candidate | Realistic assets on the realistic terrain; mod API, asset and lot editors, path designer; scenarios; onboarding; packaged builds; save promise | |
| 1.0 | City, transport, emergency services, multiplayer, regions and mods | |
| 1.1–1.4 | Venue core with the hospital, zoo and theme park; ordinance node editor; voice layer; scenario authoring | |
| 1.x | Holidays and seasonal events across the city and every side game, on the shared calendar | |
| 2.x | Farm, then life sim | |

## Releases

| Date | Release | Highlights |
| --- | --- | --- |
| 2026-11-03 | **Rivers, round 2** (PR #20) | Rivers meander off the grid and fill ponds and lakes in the basins the rain keeps up with. Their level follows the last month's rain upstream, from a low trickle after a dry season to floods after weeks of storms. A river brush digs new channels at a chosen width and depth. Dams hold a river up to 16 m, with a reservoir that keeps its level and a river below that neither floods nor runs dry, and a hydro plant at a dam's foot makes power from the fall. [Gate](docs/GATE-RIVERS.md) |
| 2026-11-03 | **River shorelines** (PR #20) | Rivers in shaped channels after the prairie kiosk's Flint Hills creek: a smoothed line, wider as more water feeds it, with a flat bed, sloped sides and a cut bank in place of the one-cell staircase. In both clients the water runs up the bank to a contour shoreline, with wet mud and gravel at the edge and floodplain grass beyond. Bridges sit half a step over the water, and older regions keep their old rivers. [Gate](docs/GATE-RIVERS.md) |
| 2026-11-03 | **Beta 4, part 7: tile hosts for growing regions** (PR #20) | A tile of a growing region runs on its own host while the region grows around it and cities are founded beside it; a city of several tiles can be hosted apart; a hosted city can't annex or be taken in, and the charter stays with the region's main server. [Gate](docs/GATE-BETA4-TILES.md) |
| 2026-11-03 | **Beta 4, part 6: room to grow** (PR #20) | Regions from small to huge (huge made a tile at a time), growing regions on a 255-tile frame, and a region charter of who grows the region, who annexes and how big a city may get, changed by the mayors' vote. A city annexes the open land beside it and moves onto its bigger map whole, always a rectangle, at a price rising with its size; a city takes in its neighbour by a deal (its mayor agrees, or a computer mayor sells at its temperament's price), with everything in both. [Gate](docs/GATE-BETA4-ROOM.md) |
| 2026-11-03 | **Beta 4, part 2: the new look (UI A)** (PR #20) | The mayor's view in both clients: a full-window map under glass panels, a build rail with flyouts of priced tiles in tabs (one shared menu and icon set), the tool's options along the top, the city's name and modes top right, the L of minimap, panel buttons and the city's numbers in the bottom left, and a floating panel that closes. The side games' screens keep their own layout. [Gate](docs/GATE-BETA4-UI.md) |
| 2026-11-03 | **Beta 4, part 1: direct dispatch** (PR #20) | Every emergency unit is on the city's streets, driving each leg by the fastest route; both clients draw them as vehicles in their colours and take orders on the map: click, shift-click, box and numbered groups to pick; right-click a place (shift queues more), a call or a unit to follow; hold and go home. Auto-dispatch stays on and sends units from where they stand. [Gate](docs/GATE-BETA4-DISPATCH.md) |
| 2026-11-03 | **Beta 4, part 3: water quality** (PR #20) | Foul water from raw sewage, industry and the sewage plants' outfalls runs down rivers, over borders (a per-region option) and spreads through lakes and the sea; sewage treatment from none to tertiary on the budget panel; pumps draw it into the taps, and a new water treatment works cleans the mains; smell, unsafe tap water, fewer visitors, boil-water notices and dead fish; a water quality layer in both clients; save format 16. Snow now also thaws by the calendar's season. [Gate](docs/GATE-BETA4-WATER.md) |
| 2026-11-03 | **Beta 4, part 4: snow** (PR #20) | Snow lies on the ground and the roofs, building up in the day clock's showers and melting with the season, rain and heat; deeper high up and facing north, none on the steep; drawn by the Godot terrain and building shaders and on the web map; save format 15. [Gate](docs/GATE-BETA4-SNOW.md) |
| 2026-11-03 | **Beta 4, part 5: GPU test** (PR #20) | `--gpu-test` measures each graphics preset, each lighting level and night on the hill town, with frame rate, 1% lows, GPU and CPU time, draw calls and triangles, writes a Markdown and JSON report and shows a summary; launchers for Windows (double-click) and Linux start a test server of their own. [Gate](docs/GATE-BETA4-GPU.md) |
| 2026-11-02 | **Beta 3.2, part 7: growing regions** (PR #19) | A region that starts as one tile and adds land on any side, each new tile's ground, rivers and rock made then to carry on from its neighbours; new land is open for any player to found a city on; both clients' region maps show the wild land around the region to add. [Gate](docs/GATE-BETA3-2-REGIONS.md) |
| 2026-11-02 | **Beta 3.2, part 6: light and shadow** (PR #18) | The sun follows the day clock and the month, golden hour and night, haze and grey skies by the weather, lit windows, street lights and bus lights at night, sky reflections on the water, contact shadows and bounce light by preset, tilt-shift while querying, a time-of-day pin, and day and night on the web map. [Gate](docs/GATE-BETA3-2-LIGHT.md) |
| 2026-11-02 | **Beta 3.2, part 5: subways** (PR #17) | Subway tunnels laid under anything at $150 a cell, subway stations over or beside them, a subway yard, Metro lines run by the bus company, subways drawn red in both clients' underground views. [Gate](docs/GATE-BETA3-2-SUBWAYS.md) |
| 2026-11-02 | **Beta 3.2, part 4: underground** (PR #16) | One pipe tool lays a water main and a sewer together, sewage treatment plants, raw sewage past a small town's septic tanks fouls the water and lowers desirability, an underground view in both clients with mains, sewers and tunnels under a cutaway ground, tunnels outlined once from above. [Gate](docs/GATE-BETA3-2-UNDERGROUND.md) |
| 2026-11-02 | **Beta 3.2, part 3: weather on the roads** (PR #15) | Freezing rain, showers in hours of the day clock, wet roads, mud on dirt, snow and ice by road tier, a road crews budget that clears highways first, drains on the bigger roads, more crashes in snow and ice, all drawn in both clients. [Gate](docs/GATE-BETA3-2-ROADS.md) |
| 2026-11-01 | **Beta 3.2, part 2: the clock** (PR #14) | Two clocks: a day clock for the hours (calls, crews, fires, storms go by it in 48-minute turns) and a calendar for growth and money, at a pace the mayor picks from a month to two years a clock day; side games and every city in a region share both. [Gate](docs/GATE-BETA3-2-CLOCK.md) |
| 2026-11-01 | **Beta 3.2, part 1: hills** (PR #13) | Path grades by type and surface, every path graded into the ground, tunnels and stairs, made ground with retaining walls, soil over four kinds of rock with cuts that follow the material, a starter town on a hillside. [Gate](docs/GATE-BETA3-2.md) |
| 2026-10-31 | **Beta 3.1** (PR #12) | Corner heightfield, mountain generator, Godot terrain shader, realistic roads, rivers, bridges and floods, computer mayors build to their neighbours, bus lines across borders, web dispatch screen. [Gate](docs/GATE-BETA3-1.md) |
| 2026-10-31 | **Beta 3: Regions** (PR #11) | Regions on one clock, border connections, commuters and freight across borders, neighbour deals, computer mayors, federated hosting, emergency services beta. [Gate](docs/GATE-BETA3.md) |
| 2026-10-30 | **Beta 2: Together** (PR #7, #8) | Accounts and dedicated servers, seats, consent, council, dispute ladder, de-linking, emergency services alpha, trams, trains, freight, smooth shapes. [Gate](docs/GATE-BETA2.md) |
| 2026-10-29 | Graphics options (PR #6) | Low to Ultra presets and each setting on its own |
| 2026-10-28 | **Beta 1: The real client** (PR #5) | Godot client, classic and free cameras, voxel pipeline, company mode. [Gate](docs/GATE-BETA1.md) |
| 2026-10-28 | **Alpha 3: First link** | Side-game contract, seats and automation, the bus company |
| 2026-10-28 | Roadside stops, street names, service gauges | |
| 2026-10-28 | **Alpha 2: A city that talks back** | Demand by source, transit, curved roads, ordinances, events, advisors, disasters, agent tiers |
| 2026-10-27 | **Alpha 1: A city that grows** | Zoning, networks, budget, services, land value, commutes, the "why" query |
| 2026-10-27 | Pre-alpha | Sim core, protocol, server skeleton |

## Quick start

You need the .NET 10 SDK and Node 20 or newer.

```sh
# 1. Build the web client once
cd clients/web && npm install && npm run build && cd ../..

# 2. Run the server (it also serves the client)
dotnet run --project src/ToyCity.Server

# 3. Play: open clients/godot in Godot 4.7.2 .NET and press F5
#    (or open http://localhost:5180 for the web debug and admin client)
```

### GPU test

To measure the game on your own graphics card, set `GODOT` to your Godot 4.7.2 .NET executable and run the GPU
test: on Windows double-click `tools/gpu-test/gpu-test.cmd`, on Linux or macOS run `tools/gpu-test/gpu-test.sh`. It
builds the game, starts a test server of its own on port 5189 with an empty save folder (your own cities aren't
touched), and holds three views of the hill town under each preset, each lighting level and at night, about three
minutes in all. The report lands in `gpu-test/` at the top of the repository. See
[docs/GATE-BETA4-GPU.md](docs/GATE-BETA4-GPU.md).

See [docs/BETA1.md](docs/BETA1.md) for the Godot client's controls, command-line options and frame budget.

To host a city for several players, run `dotnet run --project src/ToyCity.Server -c Release -- --dedicated`; players sign in and `host.json` beside the saves sets who may join. A side game can run in its own process or on another machine with `dotnet run --project src/ToyCity.SideGameHost -c Release -- --port 5190`, listed under `sideGames` in `host.json`. See [docs/BETA2.md](docs/BETA2.md).

The server starts a 128 × 128 map called Toyville with a small working town so there is
something to look at. Use **New city** for a blank map.

For client development, run the server as above and `npm run dev` in `clients/web`, then open
http://localhost:5173 (Vite proxies the WebSocket to port 5180). To point the client at another
server, add `?server=ws://host:port/ws` to the URL.

### Web client controls

| Key | Tool |
| --- | --- |
| Q | Query: click anything to see why it is the way it is |
| B | Bulldoze (in underground view it removes pipes, their water mains and sewers together, and subway tunnels) |
| R | Build path (pick one of the 15 path types in the toolbar) |
| Z / X / C | Zone residential / commercial / industrial; V cycles density |
| D | De-zone |
| P / W / J | Power line / water and sewer pipe / subway tunnel |
| E | Place a civic building (pick it in the toolbar) |
| T | Terraform (raise, lower, level) |
| U | Underground view: water mains, sewers, road tunnels and subways under a cutaway ground |
| Space, 1–3 | Pause, speeds |
| Shift while dragging | Draw the vertical leg first |
| Wheel, right-drag, arrows | Zoom and pan |

Hints for the current tool are always shown in the lower-left corner.

## Tests and tools

```sh
dotnet test                                                 # unit and gate tests
dotnet run --project tools/ToyCity.Bench -c Release -- 10   # headless 10-year run with yearly report
TOYCITY_BIG=1 dotnet run --project tools/ToyCity.Bench -c Release -- 5 256   # stress: many towns on a large map
TOYCITY_WHY=1 dotnet run --project tools/ToyCity.Bench -c Release -- 2       # print why-reports for stuck lots
```

## Layout

| Path | What it is |
| --- | --- |
| `protocol/toycity.proto` | Wire and save format, shared by every front end |
| `content/` | Path profiles and building types (data, not code) |
| `src/ToyCity.Sim` | Deterministic simulation core: no I/O, threads or clocks |
| `src/ToyCity.Protocol` | Generated protobuf types and sim ↔ wire mapping, saves |
| `src/ToyCity.Server` | Fixed-tick host, WebSocket endpoint, static hosting |
| `clients/godot` | The Godot 4.7.2 .NET client: the way to play |
| `src/ToyCity.ClientModel` | What a .NET client knows that isn't drawing: the city mirror, tools, panels, the gate run |
| `clients/web` | 2D web client (SimCity 1 style): the debug and admin client since Beta 1 |
| `tests/ToyCity.Sim.Tests` | Tests, including determinism, replay and save gates |
| `tools/ToyCity.Bench` | Headless benchmark and diagnostics |

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for how the pieces fit and
[docs/ALPHA1.md](docs/ALPHA1.md) for the Alpha 1 rules, numbers and known limits.

## License

GNU General Public License v3.0. See [LICENSE](LICENSE).
