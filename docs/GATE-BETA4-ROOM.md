# Beta 4, part 6 gate: room to grow

Part 6 of the Beta 4 plan: region sizes, annexing open land, taking in a neighbour by a deal, and a region charter.
Zac's answers of 2026-11-03 set the rules: "an L shape would be prohibited and require that you purchase both plots
to form a square", and a neighbour joins only by a deal.

## What changed

- **Region sizes.** A new region is small (3 × 3 tiles), medium (8), large (16) or huge (32). Past 1,024 cells a side
  a region is made a tile at a time: each tile's ground is made when it's first needed and saved with the region. The
  region map shows a rough look at open land that isn't made yet.
- **Growing regions** now grow on a frame of 255 tiles a side, starting in the middle. Regions grown before keep their
  8-tile frame.
- **The region charter** says who adds land, who lets a city annex, and how big a city may get (up to the server's
  limit: 4 × 4 tiles of 128 cells, 8 × 8 of 64). Each rule is one of: the server's admins only, a vote of the mayors,
  or anyone (any mayor).
  - The admin sets it when making the region and can change it at once.
  - Mayors change it by a vote. More than half must agree. Half against, or 30 days with no answer, turns it down.
  - It's saved with the region, along with the votes open.
- **Annexing open land.** A city takes the open tile beside it, and its map grows: 1 × 1 to 2 × 1 to 2 × 2 tiles and on.
  - A city is always a rectangle. Picking a corner tile takes the tiles between as well, as one purchase.
  - The price rises with the city's size: $25,000 a 128-cell tile at two tiles, times the tiles added, times half the
    new size in tiles. Going from one tile to two costs $25,000; from two to four, $100,000. The map shows the price,
    the rectangle and the city's treasury before buying.
  - Under a voting charter the mayor asks and the mayors vote. Under "admins" only an admin can annex.
- **How a city moves onto a bigger map.** The whole city moves as it stands: buildings and their people, roads and
  their strips, pipes, zones, every per-cell layer, people's trips, the bus lines and their stops, the dispatch
  units and their calls. The new land comes with the region's ground, rivers and rock, meeting the city's own at the
  old edge.
- **Taking in a neighbour.** A mayor offers a price to take in the city next door, when the two make a rectangle.
  - A player's city: its mayor agrees or declines on the region map. Offers lapse after 30 days.
  - A computer mayor answers at once, and sells only at its temperament's price: what's built there and its money,
    times 1.0 for a careful mayor, 1.25 for a green one, 1.5 for a growth-hungry one.
  - The two worlds become one, named for the buyer. The neighbour's part of the map, its buildings with their people
    and jobs, its roads, and its treasury and debts come along. Its bus lines and dispatch close. Its deals end in both
    cities, and its mayor, if a player, stays on as a deputy for everything.
- **Cities of several tiles in the region.** A city is counted from its top-left tile. Each side of a city may border
  one other city at most, so the border ledgers still pair one city with one city. Borders, deals, weather, floods
  and foul water down the rivers all follow the city's rectangle.
- **Both clients.** The region map draws a city of several tiles as one picture across its tiles. Open land the
  player's city can annex is marked ⊕. The side panel shows annexing, taking in a neighbour (with a price to raise or
  lower), the offers open both ways, and the votes.
- **Tile hosts.** A city on its own tile host can't annex, take in a neighbour or be taken in.
- **Protocol:**
  - `AnnexRequest`, `TakeoverRequest`, `SetCharter` and `CastVote` (client messages)
  - `RegionState.annex`, `takeovers`, `takeover_options`, `charter`, `votes` and `voters`
  - `RegionTileState.city_col`, `city_row`, `city_cols` and `city_rows`
  - `RegionSave.takeovers`, `charter`, `votes` and `tile_by_tile`

## Checks

| Check | Where |
| --- | --- |
| Region sizes up to huge; a huge region is made a tile at a time; growing past the old 8-tile frame; charter votes pass, fail and lapse | `RoomToGrowTests` (Sim) |
| A bus town moves onto a bigger map east, west and north: every building, road and home where it was, plus the move; the same riders on its line after; it builds on the new land; saves and loads to the same hash | `AnnexTests` |
| The dispatch units move with their city | `AnnexTests` |
| A city annexes beside its neighbours: price paid, people kept, neighbours on each side, a second city on one side refused, ledgers balanced, the map's tiles cut from the city's map; saved and loaded, the same region | `RegionAnnexTests` |
| A corner tile takes the tiles between; a bigger city pays more a tile | `RegionAnnexTests` |
| Two cities become one: every building, road and person, the money less the price, the debts, the account books balanced, the old mayor a deputy; plays a month on; saves and loads | `TakeoverTests` |
| A computer mayor sells only at its price | `TakeoverTests` |
| On a server: a voting charter, an annex voted in, the city on two tiles, a road on the new land | `GrowingRegionClientTests` (ClientModel) |
| On a server: May offers for Eastwick, Tess agrees, both land in the one city with Tess's roads | `GrowingRegionClientTests.TwoMayorsAgreeAndTheirCitiesBecomeOne` |
| CI: a staging tool buys out a computer mayor's town and annexes land beyond on a server, then the Godot shots run pictures the city of 2 × 2 tiles | CI step "Room to grow" (`tools/ToyCity.Stage`) |

## Results

- **Tests:** all 282 in the sim pass, among them the new `RoomToGrowTests`, `AnnexTests`, `RegionAnnexTests` and
  `TakeoverTests`. All 111 in the client model pass, among them the new and extended `GrowingRegionClientTests`.
- **The staging tool, locally:**

  ```
  STAGE Ostby's price: $41,000
  STAGE Halden took in Ostby: 2 x 1 tiles
  STAGE annexing to 2 x 2 for $100,000: ok
  STAGE OK Halden is 2 x 2 tiles
  ```

- **The web client**, against a local server (shots in [`docs/screenshots/beta4-room`](screenshots/beta4-room)):
  - Halden, a starter town on a small region, annexing the tile east for $25,000, then on two tiles.
  - Ostby, a careful computer mayor's town next door, selling for $41,000 (about its treasury, which comes along), and
    the city with both starter towns on its map.
  - The staged city of 2 × 2 tiles on the region map and in the city view.
- **Godot:** see CI below.

## Known limits

- Each side of a city borders at most one other city. A city can't grow along a side that would then touch two.
- A city that has annexed or taken in a neighbour can't be replayed from its founding: its log starts again at the
  move. Saves are unaffected.
- A side game hosted on another server can't follow a city onto a bigger map, so it blocks annexing.
- The taken-in city's district-level records (crime heat, the director's notes) start fresh.
- The giving mayor gets no money of their own: there are no player wallets yet. The price leaves the region.

## What's next

Part 7, tile hosts for growing regions.
