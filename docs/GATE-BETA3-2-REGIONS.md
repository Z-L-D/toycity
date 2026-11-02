# Beta 3.2, part 7 gate: growing regions

Part 7 is G4 of the Beta 3.2 plan, which Zac accepted on 2026-11-01: "A new game starts as one map. You can add a tile
on any of its four sides, and other players can be offered new tiles. Each new tile generates its terrain, rivers and
geology at that moment, continuing what's at the shared edge." The plan named the catch: rivers were planned for the
whole region at once, so the river generator had to change. It is the last part of Beta 3.2.

## What changed

- **A growing region** starts as one tile in the middle of a frame of 8 × 8 tiles. The rest is wild land until
  someone adds it.
  - In the web client, the new-game dialog has "A growing region" under Start. In Godot, the new-city dialog has a
    "growing region" box.
  - The region map shows the region's tiles and a ring of wild land around them. Wild land beside the region has a
    ＋, and picking it offers "Add this tile to the region".
  - The title says how many tiles the region has ("6 tiles, growing").
- **Who adds a tile:**
  - Any admin can add any tile.
  - A mayor can add the land on a side of their own city.
  - The mayor of every city in the region can add land anywhere beside it, as with the pace and the weather pin.
  - A new tile joins as open land, so it is on offer to every player: you or anyone else can found a city on it.
- **Each new tile's ground is made when it is added, carrying on from its neighbours:**
  - **The ground** comes from the same noise at the same place in the region, so hills, mountain ranges and coasts
    run on across the edge. The sea stands at one level for the whole region (the lowest fifth of the frame's
    ground, sampled every few cells), as in a fixed region.
  - **Rain wears each tile on its own.** The new ground is then bent to meet the corners its neighbours already fix
    (their shared edges, and a lone corner across a diagonal) exactly. The bend fades out over about 24 cells, as a
    smooth relaxation with no creases.
  - **Rivers:** a river that ran off a neighbour's edge comes in at the same cell and carries on downhill. It never
    rises above the level it had at the edge. Water running the other way, towards a tile already made, runs off the
    edge as it would off the map's edge: that tile's ground, and any city on it, doesn't change. Rivers no longer run
    along a seam looking for a way out.
  - **Rock and soil** follow the same beds across the edge, and the soil at the edge is the neighbour's.
- In the region's whole map, a river that runs off one tile flows on into the next. So a flood upstream reaches
  the cities downstream across the seams, as in a fixed region.
- **Saves:** a growing region saves each added tile's ground, since it depends on the order the tiles were added.
  Old region saves load as before.
- **Fixed regions are unchanged.** Their ground is made by the same code with no offsets: the 128-cell test map's
  hash is the one main made before this change.
- **Protocol:**
  - New: `NewRegionRequest.grows`, the `GrowRegion` message, `RegionState.grows` and `RegionTileState.wild` (wild
    tiles carry no picture).
  - Saves gain `RegionSave.grows` and `RegionGrownTile`.

## Checks

| Check | Where |
| --- | --- |
| A growing region starts as one tile of open land; a city founded there stands on the tile's ground; wild land can't be founded on; tiles join only on a side of the region, never past its frame; a fixed region can't grow | `GrowingRegionTests` (Sim) |
| Three tiles added around the first, the last meeting two tiles and a diagonal: every shared corner and its rock meets exactly, and the ground across each seam differs no more than within the tiles | `GrowingRegionTests` |
| A river that leaves the first tile carries on into a tile added beside it, at the same cell, never climbing; in the whole region's map the first tile's river flows on into the new one | `GrowingRegionTests` |
| A fixed region's ground is the same as before (hash of the corners and rivers) | `GrowingRegionTests` |
| A grown region with two cities plays, its ledgers balance, its map leaves wild land without pictures, it saves and loads to the same state and ground, grows on the same after loading, and a city replays from its tile's ground | `GrowingRegionTests` |
| On a server: the new-game dialog starts a growing region; its map shows a ring of wild land; a mayor adds the tile beside her city; a player with no city can't, but founds a city on the new land and then adds the tile beside it | `GrowingRegionClientTests` (ClientModel) |
| The region gate: on a server, a 128-cell region grows on all four sides, a second player founds a city on the river's tile, the region plays a year with its ledgers balanced, and it saves and loads still growing | `GrowingRegionClientTests` (Gate) |

## Results

CI run 41518932258 on the PR's head was green on every job. Its region gate printed the same lines as a local run,
since the ground is made the same way every time; only the timings differ.

- Tests: 254 in the sim (5 new) and 103 in the client model (2 new), all passing.
- The region gate:

  ```
  GATE seed 1: one 128-cell tile at 3,3; a river leaves it on side 0
  GATE added tile 3,2 (N) in 162 ms
  GATE added tile 4,3 (E) in 151 ms
  GATE added tile 3,4 (S) in 162 ms
  GATE added tile 2,3 (W) in 161 ms
  GATE seam N: all 129 corners meet; neighbouring cells across it differ by 0.15 steps on average
  GATE seam E: all 129 corners meet; neighbouring cells across it differ by 0.16 steps on average
  GATE seam S: all 129 corners meet; neighbouring cells across it differ by 0.05 steps on average
  GATE seam W: all 129 corners meet; neighbouring cells across it differ by 0.18 steps on average
  GATE rivers cross 6 seam(s); the tile on side N carries 55 river cells
  GATE a year in 10 s: 2 cities, 2,436 people, ledgers balanced 13 months running
  GATE saved and loaded: 5 tiles, 2 cities, still growing
  ```

- Adding a 128-cell tile takes about a sixth of a second on the CI runner, so the "working…" note shows only briefly.
- Screenshots are in [`docs/screenshots/beta3-2-regions`](screenshots/beta3-2-regions):
  - The web ones were taken with Playwright against a local server: the region from one tile to six, with a second
    city.
  - The Godot one is the region map from a local `--shots` run against the same server. The shots run now ends on
    the region map.
  - The ground picture is the gate's five tiles from above.

## Known limits

- Water can't flow into a tile that is already made, so a river heading that way ends at the seam. A city may stand
  on that tile, and its ground can't change under it.
- The frame is 8 × 8 tiles, as for fixed regions, and the first tile sits near its middle. So a region can grow
  three or four tiles each way.
- A growing region's tiles are hosted by the region's own server. Hosting one of its tiles on a tile host of its
  own (federation) isn't covered yet.

## What's next

Beta 3.2 is complete. Next is the release candidate: realistic assets on the realistic terrain, the mod API and
editors, scenarios, onboarding and packaged builds.
