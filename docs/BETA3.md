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
