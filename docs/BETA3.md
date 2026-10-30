# Beta 3: Regions

Modern SC4 regions: cities side by side on one map and one clock, with real flows across their borders. Beta 3
also brings what came out of playing Beta 2: smooth diagonal roads and shores, terrain smoothing and roads that
grade the ground, direct command of the emergency services in the city view, and a more polished Godot client.

The plan, with the calls made for it: [ToyCity Beta 3 plan](https://claude.ai/artifact/Y24zffrhZDNRi9QpdUiD7m).

## Part 1: regions and the shared clock

### A region

A region (`src/ToyCity.Sim/Region.cs`) is a grid of tiles, 1 to 8 a side, each 64, 128 or 256 cells across
(128 by default, today's city size). Its ground is made once for the whole region and cut into tiles, so a hill
that starts in one tile carries on into the next. A lone city is the same generator at the city's own size, so
cities from before regions have the same ground as they always had.

A tile is open land until someone founds a city on it. Founding makes a full city (`World.CreateTile`) on that
tile's ground, with that tile's own seed, starting on the region's day. A starter town is laid out by player
actions, as before, so the city's log replays it. The founder takes the mayor's seat by a logged rename.

### One clock

Every founded city steps one day together (`Region.Step`); none is ever ahead of another. Cities step side by side
on separate threads: each touches only its own state, so the order can't change the result, and a three-city
region gives the same state hash every run.

A city founded late keeps the region's calendar: its `FoundedTick` is the day it was founded, and the things that
counted from day 0 (the three-year grace before disasters, the first month's checks) count from its founding
instead (`World.Age`). Saves are format 6, which adds the founding day; format 5 saves still load, founded on day 0.

### The server runs a region

The server's `RegionHost` owns the region and its clock, with a `SimHost` for each founded city. A new server
starts Toy Valley: three tiles by three, with Toyville founded in the middle.

- **Connections.** `/ws` lands a player in a city they're mayor of, else the first city. `/ws?tile=COL,ROW` opens
  that city directly, for a second window. A connection moves between cities without reconnecting.
- **Speed.** One speed for the whole region. An admin sets it. A mayor of any city here may ask for slower, and the
  region runs at the slowest ask of anyone online. A mayor's pause lets go after two minutes; an admin's holds.
  When an admin presses play, slower asks are cleared.
- **Saves.** A region of several tiles saves whole, as `NAME.toyregion` (a `RegionSave`: the region's shape and
  day, and a `SaveGame` for each city). A region of one tile saves as a city, `NAME.toycity`, as before. Loading a
  city save makes it a region of one tile. New city still makes a lone city; New region makes a region with a city
  in the middle.
- **Founding.** Any signed-in player may found a city on open land and is its mayor. On a dedicated server a player
  may be mayor of three cities at most, unless they're an admin.
- **Seats while away.** A player looking at a neighbour's city hasn't left their own: the computer takes a seat only
  when its owner is offline everywhere in the region.

### Tests

`RegionTests`: tiles are cut from one map and their seams meet; a city founded late keeps the region's day; a region
is deterministic with cities stepping side by side; a tile replays from its founding to the same hash; a region
saves and carries on identically; the region map shows every tile.

## Part 2: the region map and moving between cities

### Protocol

- `RegionState` (server to client): the region's name, size, day and speed, who holds the speed, which tile this
  connection is in, and each tile: founded or not, name, mayor, population, treasury, who is there now, and a
  32 × 32 picture (water, ground by height, roads, zones and buildings). Sent on joining, monthly, and whenever a
  tile changes hands or players move.
- `FoundCity`, `GoToCity`, `NewRegionRequest` and `RegionRequest` (client to server).

### The screen

Both clients have a **Region** button in the top bar. The region map takes the whole screen: the tiles as pictures
on the left, the picked tile on the right. On a city: its mayor, people, treasury and who's there, with **Open**
(move this window to it) and **Own window** (a second window on that city, `?tile=` in the web client and
`--tile COL,ROW` in Godot). On open land: a name, a starter-town switch and **Found a city here**. Double-click a
city to open it. Whatever is waiting on the server shows at once ("⏳ Opening Eastfield…") until the city arrives,
then the screen goes back to the city, centred on the new town.

The Godot screen's logic is `ClientModel/Panels/RegionModel`, tested without the engine; the screen itself is
`clients/godot/scripts/RegionScreen.cs`. The web client's is `clients/web/src/region.ts`.

### Tests

`RegionClientTests` plays it against a dedicated server: the first account becomes Toyville's mayor; a second
player founds Eastfield from the region screen, opens it on the same connection and the first player's map shows
her move; opening open land and founding twice are refused; a mayor's pause holds both cities on the same day and
an admin's play starts them again.

## Part 3: border connections

### Across the border

Each city knows what its neighbours show across each of its four sides (`World.NextDoor`, 0 north, 1 east, 2 south,
3 west): the neighbour's name and mayor, and which of its edge cells carry a path, a power line or a pipe. Before
every day the region compares what each city shows against what its neighbours last heard and, where it changed,
tells them with a `NeighbourEdgeAction` (`Region.ExchangeBorders`). Everyone hears before anyone steps, so every city
reads its neighbours' previous day whatever order the cities run in. The action is logged in the city that hears it,
so a city's log replays it alone, without its neighbours: what the federated hosts of part 8 rely on.

### When a road connects

A path that leaves the map and is joined to the city is a neighbour connection, as before. Toward open land, or past
the region's edge, the wider world takes it as before. Toward a neighbouring city, it works only where that city's
path meets it, cell for cell. `World.SideCapacity` counts each side's working capacity.

A road that reaches a neighbour where they have none is half a connection and carries nobody. An info alert says so
("A road to Eastfield ends at the border: Eastfield has no road there yet"), and the encyclopedia's neighbour
connections article explains the rule.

### In the clients

The region state carries what the neighbours show across this city's sides (`RegionState.borders`). Both clients
draw it just past the map's edge: road stubs, power and pipe marks, and the neighbour's name. Drawing a road to
within two cells of the edge and three cells along it from a neighbour's road snaps the end onto it
(`Planner.BorderSnap`; `borderSnap` in the web client), so the two meet.

### Tests

`RegionTests.ARoadToTheBorderWorksOnlyWhereTheNeighboursRoadMeetsIt`: a road out to open land connects; once a city
is founded next door, it doesn't until that city builds a road to meet it, and the alert lists it meanwhile; each
city's log, borders included, replays alone to the same hash. `PlannerTests` checks the snapping.

## Part 4: commuters, shoppers, tourists and freight across borders

### Published numbers

At the start of each month every city publishes a handful of numbers (`World.Published`): people, job seekers,
spare jobs, shop jobs, attraction, goods it makes for sale, goods it wants, and each side's working road capacity.
They reach its neighbours with the border (part 3), so each neighbour knows them from the next day.

### Flows

At the start of the next month each city works out what crosses each border to a neighbouring city
(`World.Flows`, `World.Flow`): commuters each way, shoppers each way, visitors each way, and goods each way, each
limited by its share of the connection's capacity (the lesser of the two sides'). Both cities use the same formula
on the same two sets of published numbers, so what one sends the other receives: the ledgers balance exactly. A
border with no working connection carries nothing.

- **Residential.** Residents who work next door have jobs; jobs here taken by the neighbours' commuters don't. Spare
  jobs across a connected border pull people in (on top of the abstract region's pull, which the open sides keep).
- **Commercial.** Shoppers and visitors from next door add regional demand; residents who shop next door take some
  away. Roads to open land still bring the abstract region's shoppers.
- **Industrial.** Neighbours' commuters add to the workforce factories hire from, and goods sold next door add to
  exports.
- **Traffic.** Each border's vehicles load the crossing roads and fade over the first eight cells inward.

The demand panel names the neighbours: "620 of your residents work in Eastfield", "140 shoppers a day come from
Westbrook", "60 tons of goods a day go to Eastfield".

### Tests

`RegionTests.CommutersShoppersAndGoodsCrossAndBothLedgersAgree`: two starter towns joined by one road; every month for
a year each city's flows mirror the other's exactly, something crosses, the crossing has traffic, and the demand panel
names the neighbour.

## Part 5: neighbour deals

### Making a deal

On the region map, a mayor picks a neighbouring city and draws up a deal: **power**, **water**, **garbage**
collection or a **shuttle**, to sell or to buy, an amount and a monthly price. The neighbour's mayor sees it under
"Deals with neighbours" (and a notice in their city) and agrees, declines, or **counters**: the form takes the
offer's terms to change, and sending them back replaces the offer with theirs, for the first mayor to answer. Whoever offered can withdraw it until
then; once it runs, either mayor can end it. One deal of each kind a border, either way.

Each kind needs the two cities joined for it where the border is: a power line of each meeting, a pipe of each, or
a road of each. An offer without one is refused with what to build.

### What a deal does

The region keeps the books (`Region.Deals`, `src/ToyCity.Sim/RegionDeals.cs`). When the neighbour agrees, it sends a
`CityDealAction` to both cities on the same day; each city keeps its side (`World.Deals`, `src/ToyCity.Sim/Deals.cs`)
and the action is logged, so each city's log still replays alone.

- **Power.** The seller's grid at the crossing gives up the amount; the buyer's grid at its crossing gains it.
- **Water.** The same, on the pipe networks at the crossing.
- **Garbage.** The buyer's capacity goes up by the amount; the seller's dumps take that much more in.
- **Shuttle.** Adds its vehicles an hour to that side's road capacity in both cities, so more commuters, shoppers
  and goods cross (part 4). The plan had the transport company's own lines running across the border to stops in
  the neighbour city; that needs a line to live in two cities' states at once, which waits for federated hosting
  (part 8) to settle how tiles share anything beyond the border. Until then a transit deal is this shuttle.
- **Money.** Each day of a deal runs up a day's price in both cities; the budget books it monthly as "Power sold to
  Eastfield" or "Power bought from Westbrook", and the last days are settled when a deal ends. Both cities count the
  same days, so what one pays the other receives.

### When a deal ends by itself

Before each day the region ends, in both cities at once, a deal whose connection broke ("the power lines no longer
meet at the border"), whose buyer can't cover a month, or whose seller has nothing left to send at the crossing.

### Saves

A city's running deals are in its save (format 6). Offers still waiting for an answer are in the region save.

### Tests

`RegionTests.APowerDealFeedsTheNeighbourBothBooksAgreeAndItEndsWhenTheLineIsCut`: no offer until both lines meet;
the offerer can't answer their own offer; once agreed the buyer's grid gains what the seller's gives; three months of
budgets agree to the dollar; cutting the line ends it in both cities; the region saves and each city replays alone.
`RegionClientTests` checks a refused offer clears its "working" line, and the screen lists deals with Agree, Decline
and End where they belong.

## Part 6: computer mayors

### A computer in the mayor's seat

Any tile can have a computer mayor (`src/ToyCity.Sim/ComputerMayors.cs`): Beta 2's autopilot in the mayor's seat,
from the city's first day. It lays new blocks of streets as the city needs them, zones to demand, adds power, water
and services, sets taxes and keeps the budget out of the red, all the way the autopilot already did for a player who
was away. Each has a temperament, shown on the region map next to its name and as 🤖 on its tile:

| Temperament | Autopilot style | Sells at | Pays up to |
| --- | --- | --- | --- |
| growth-hungry | growth | the fair price | half as much again |
| green | green | 10% under | a fifth over |
| careful | profit | a quarter over | the fair price |

A fair price is $1 a month for each 5 kW or 5 water, 3 garbage or 2 vehicles an hour. Computer mayors are named for
their temperament ("Penny (computer)"); account names can't have brackets, so no player is ever taken for one.

### Founding, taking over, handing over

- **Founding.** On open land the region map asks who will be mayor: you, or a growth-hungry, green or careful
  computer. On a dedicated server only admins found cities for computers.
- **Taking over.** Any signed-in player may take over a computer mayor's city and play it by hand (within the three
  cities a player may run).
- **Handing over.** A mayor may hand their city to a computer of any temperament.

All three are logged actions in the city (the seat changes owner, then its level), so a city's log replays them.

### Deals by rules

Before each day the region lets computer mayors answer what was offered them:

- **Asked to sell**, it says no unless it has the amount spare with a margin (a tenth of its own use, at least 50);
  then it agrees at its price or better, or counters once at its price.
- **Asked to buy**, it says no unless it's short (a growth-hungry one always wants a shuttle) and can cover six
  months; then it agrees at its limit or under, or counters once at its limit.
- A computer haggles once: an offer that's already been countered is agreed or declined.

On the first of each month a computer mayor short of power, water or garbage capacity offers to buy a fifth more than
it's short from a neighbour joined to it at the border that has that much to spare. Its answers and offers reach the
other mayor as a notice ("Penny (computer), Botton's mayor, asks $25 a month instead").

What computer mayors don't do yet: run their own lines and roads out to meet a neighbour's at the border. A player
next door builds to meet them.

### Tests

`RegionTests.AComputerMayorGrowsItsCityAndReplaysAlone`: a growth-hungry computer's starter town grows over two years
and its log replays to the same hash; a player takes it over and hands it to a careful computer.
`RegionTests.AComputerMayorCountersALowOfferAndTheDealRuns`: a careful computer counters a low price for power at a
quarter over fair, the counter is agreed and the power flows; a second counter is declined. `RegionClientTests`: the
admin founds a computer's city from the region screen, a player can't, and takes it over instead.

## Part 7: weather and disasters across borders

### Region weather

A region of more than one tile has a sky (`src/ToyCity.Sim/Weather.cs`). Fronts of rain, snow, heat and wind come in
from one edge of the region and drift across it over one to four weeks, a few tiles wide, light, heavy or severe.
Snow comes in winter, heat in summer, rain and wind all year. A front is worked out from the region's seed and the
day alone, so the sky needs no saving and every host computes the same one.

Before each day the region tells each city the weather over its middle when it changes (`WeatherAction`, logged, so a
city's log replays it alone). In the city:

| Weather | Power use | Water use | Fire spread | Visitors |
| --- | --- | --- | --- | --- |
| Heat, per step of strength | +10% | +15% | +25% | +5% |
| Snow | +8% | | −30% | −8% |
| Rain | | −10% | −25% | −12% |
| Wind | | | +25% | |

Severe weather makes the news ("Heatwave over Westbrook: power and water use are up, and fires catch more easily").
Both clients show today's weather after the date in the top bar, and on each tile of the region map. A lone city
has no region sky and plays as before.

### Disasters across borders

`src/ToyCity.Sim/CrossBorder.cs`, through the border exchange of part 3, so each city's log replays it alone:

- **Fire.** A building burning at the border shows on the neighbour's side of the edge. Each day it may set the
  neighbour's building across from it alight (a fifth of the time, more in heat and wind, less in rain and snow).
- **Tornadoes and the Clockwork Colossus.** One that walks off the map toward a neighbouring city walks in there the
  next day at the same place, with its heading and the days it has left.

What crosses follows the stricter of the two cities' rules: nothing crosses into a city with disasters turned off,
and the Colossus stops at the border of a city whose rules forbid absurd disasters ("The robot from Eastfield stopped
at the border: Westbrook's rules forbid absurd disasters").

The plan also had floods following rivers downstream. ToyCity has no floods or rivers yet, so that waits for them.

### Tests

`RegionTests.WeatherFrontsCrossTheRegionAndChangeTheCities` (every city has the region's weather each day, rain, snow
and heat all turn up in two years, the two ends of a region sometimes differ, and each city replays alone; a lone city
stays clear), `AHeatwaveRaisesPowerAndWaterUse`, `TheColossusWalksOverTheBorderUnlessTheNeighbourForbidsIt` and
`FireJumpsTheBorderBetweenBuildingsThatTouchIt`.

## Part 8: federated hosting

One region can now run on several servers: each city on a host of its own, all on the region's one clock.

### Who does what

- **The region host** is an ordinary dedicated server with `linkKey` set in its `host.json`. It keeps the region:
  the clock, the accounts, the map, deals, founding, weather, and a check copy of every city run elsewhere.
- **A tile host** runs one city. Start it with
  `ToyCity.Server --tile-host ws://REGION:5180/tile-link --tile COL,ROW --link-key KEY --public ws://ME:PORT/ws`.
  It joins the region host, gets the city as the region has it, and serves that city's players as usual.

Each day the region host sends every tile host a day start: the day's number and what the region tells that city
(border edges, deals, weather, notices). The tile host steps its city and replies with every action its city applied
that day. The region host replays those on its copy. On the first day of each month the tile host also sends its
city's hash, and the region host checks it against the copy's. If they differ, the region stops, says so on the region
map, and writes `saves/desync-COL-ROW-TICK.txt` with both sides of the difference.

### Players

Players on a tile host sign in with the region host's accounts (the tile host asks for them). Region-wide asks
(deals, founding, the clock, saving, taking over) go over the link to the region host, and answers come back the same
way. The region map shows where each city is hosted. Opening a city that runs elsewhere sends a redirect, and both
clients move there by themselves: the web client reloads with `?server=…&tile=…`, and the Godot client restarts
pointed at the new host. The player signs in there with the same account.

### When a tile host goes quiet

The region holds the day while it waits, and the region map says who it is waiting for. A tile host that hasn't
answered for `takeOverSeconds` (120 by default) is taken over: a computer mayor runs that city on the region host from
its check copy, and the human mayor is remembered. When the tile host comes back, it is sent the city as it now
stands and the mayor gets the seat back.

### Settings

| `host.json` key | What it does |
|---|---|
| `linkKey` | The key tile hosts must give. Empty: no tile hosts. |
| `takeOverSeconds` | How long the region holds for a silent tile host. |
| `publicAddress` | Where players reach the region host, for redirects. Worked out from the link if empty. |
| `fastDayMs` | How long a day takes at full speed (833 ms). Scripted runs set it lower. |

### Limits

- Side games run on another process (`sideGames` in `host.json`) aren't supported for cities on a tile host yet.
- The region host trusts its tile hosts for who their players are. Only give the link key to hosts you trust.
- The links form a star: tile hosts talk only to the region host, never to each other.

### Tests

`FederationReplayTests.ACheckCopyFedTheDaysLogStaysIdentical` (Sim) steps a region the way the two kinds of host do:
one city steps alone as on a tile host, the day's actions go through the wire's records both ways, and its check copy
stays identical for 120 days. `FederatedTests` (ClientModel) starts a region host
and two tile hosts as their own processes: two months go by at full speed with no desync, a player on a tile host
signs in with the region's accounts, opening a city on another host redirects in both directions, and a tile host
that is killed is covered by a computer mayor while the rest of the region carries on in step.

## Part 9: smooth roads and shores

### Diagonals and arcs

The road tool has three modes in both clients: Straight (as before), **Diagonal** and Curve (A in the web client and
C in Godot cycle through them). A diagonal is a straight road at any angle from where the drag starts to where it ends.

The sim still runs every road on cells. A diagonal is laid as the stairs of cells nearest the line (`World.LineCells`,
the same integer steps as the curve tool), so traffic, zoning, frontage and street names work unchanged. What's new
is that an arc or diagonal is **remembered as drawn** (`src/ToyCity.Sim/Strips.cs`): its ends, its bend, its kind
of path and its cells. It is saved with the city (save format 7; format 5 and 6 saves still load), sent in the full
state, and sent again in a day's delta whenever it changes.

Both clients draw a strip as one smooth road from the centre of its first cell to the centre of its last, with
the lane markings or rails of its kind. Its two end cells stay ordinary road cells, so it meets the streets at its
ends. The cells in between are drawn as open ground, tinted with the zone most cells around them have, so the zoning
seems to run up to the road and fill the triangles beside it. In Godot the road is a ribbon laid on the ground
(`StripsView`); on the web it's a stroked line over the map. The shape is in `ClientModel/RoadShape.cs`, mirrored
in `state.ts`.

A strip is forgotten when it stops being whole: a cell of it bulldozed, or laid over with another kind of path. What
is left stays as ordinary road cells. Laying another kind of path along the same line gives a new strip of that
kind.

### Shores

The web client draws the ground four pixels to a cell. Heights blend between the cells' centres, so hills shade
smoothly, and the shoreline runs where the blend of wet and dry cells crosses half. Coasts and lakes curve instead
of stepping, and at each cell's centre the picture still agrees with the sim. Godot already curved its waterline
with smooth terrain (on in every graphics preset but Low).

### The Smooth brush

Terraform has a fourth brush, **Smooth**, beside Raise, Lower and Level. Each open cell in the dragged area takes
the rounded average of the nine cells around it (read before the brush, so the order of cells doesn't matter). It
wears steps down into slopes. Buildings and roads keep their ground, and it costs the usual terraform price per cell
it changes. Smoothing flat ground costs nothing.

### Tests

`StripsTests` (Sim): a diagonal is laid edge to edge within a cell of the true line; diagonals and arcs are
remembered, survive save and load, replay identically and go out in the full state; a strip that is broken or laid
over is forgotten, the delta says so once, and its cells stay road; and the Smooth brush wears a tower down without
touching a road beside it. `PlannerTests.ADiagonalIsPreviewedBuiltAndDrawnAsOneSmoothRoad` (ClientModel) draws a
diagonal with the planner, builds it, and checks the mirror's strip, which cells it draws, and the straight line
from end to end. The CI screenshots of diagonals, arcs and the Smooth brush come with the Beta 3 gate run (part 13).

## Part 10: auto-grading and foundation pads

### Roads grade the ground

A road no longer refuses a slope. As it's laid, the ground under it is graded so it climbs at most one step a cell
(`src/ToyCity.Sim/Grading.cs`). The graded line runs halfway between the highest gentle profile under the ground and
the lowest one over it, so cut and fill roughly balance. Where it crosses or meets a road already there, it keeps
that road's height. It never digs under the waterline. Open land beside a cut or fill is eased to within a step of
the road, so the banks are slopes rather than walls. With smooth terrain on, Godot draws them as embankments and
cuttings, and the web map shades them.

Earthworks cost $10 a step a cell, on top of the road. Both clients' previews add it to the price ("Road, 19 cells:
$1,240 (grading $290)"), worked out the same way as the server. A road is refused only when the roads at its two
ends are too far apart in height for any one-step-a-cell climb between them ("Too steep at 9,7: the roads it joins
are too far apart in height to grade between"). Buildings and water were already refused.

### Foundation pads

A building levels the ground under it to its lot's average height, as SC4's do:

- **A building the mayor places** may stand on a lot that rises up to three steps across it. The pad costs $5 a step
  a cell, and the preview shows it (for example "Police station: $X (pad $Y)"). More than three steps is refused, and the
  message says how much it rises.
- **A grown building** levels its own pad at the developer's cost, so zones off the flat starter platform grow.
  A lot that rises more than three steps across it isn't used.

Pads and grading never move water. The web client now redraws just the ground around changed cells, so pads
levelling during growth don't redraw the whole map.

### Tests

`GradingTests` (Sim): grading climbs at most a step a cell, keeps the roads it meets, stays above water and refuses
only what can't be joined (2,000 random cases); a road up a three-step cliff cuts and fills its way with banks beside
it, costs the earthworks and replays; two roads too close in height can't be joined, but three cells apart they can;
a building levels its pad up to three steps of rise and is refused across four; and a starter town grows off its
platform onto a graded hillside, every building on level ground. `PlannerTests.RoadPreviewsPriceTheGradingAsTheServerDoes`
(ClientModel) checks the preview against the server for 400 diagonals across raised knobs and cliffs.
