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
