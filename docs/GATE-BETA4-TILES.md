# Beta 4, part 7 gate: tile hosts for growing regions

Part 7 of the Beta 4 plan: host a tile of a growing region on its own server and fix what breaks. A city on its own
host can't annex or be absorbed, and the region's charter is kept only by the region's main server.

## What changed

- **A tile of a growing region on its own host.** This was checked end to end on three kinds of change around the
  hosted tile:
  - The region grows beside the hosted city.
  - A computer mayor's city is founded next to it.
  - The days go round in step through both.
  - The tile host's players see the grown region, its wild land and its charter, which come from the main server's
    map.
- **A city of several tiles on its own host.** It was broken. The tile host named the city by the tile it asked for,
  and placed it by the seed of the tile it was founded on. After annexing, either could be the wrong tile.
  - The main server now takes any of the city's tiles and keeps the link at the city's top-left tile, the same place
    it keeps its own cities.
  - The welcome tells the tile host where that tile is (`TileWelcome.city_col` and `city_row`).
- **Hosted cities stay as they are.**
  - A city on its own host can't annex, take in a neighbour or be taken in. The server refuses, and the region map says
    why on the annex and takeover options.
  - Annex and takeover requests from a tile host's players go to the main server, which refuses them the same way.
- **The charter.** A tile host keeps no charter of its own. Charter changes, votes, annexing and takeovers from its
  players go to the main server, and its players see the main server's charter.

## Checks

| Check | Where |
| --- | --- |
| Eastwick annexes the land north of it, then runs on its own host as a city of two tiles, named by its lower tile. The region grows beside it, a computer mayor's city is founded next to it, and the days go round with nothing stopped. A player on Eastwick's host lands in the two-tile city and sees the grown region and the charter. Eastwick's annex options say it runs on its own host, an offer to take it in is refused, and an admin's charter reaches both servers' maps | `GrowingTileHostTests` (ClientModel) |
| The Beta 3 federated region still runs in step, and a computer covers for a tile host that dies | `FederatedTests` |
| The Beta 3 region gate: three tiles on their own hosts for a decade | CI step "Region gate" |

## Results

- **Tests:** every test passes: 282 in the sim and 112 in the client model, one of them new.

## Known limits

- A city on its own host can't annex or be absorbed. To grow it, stop its tile host first. The main server takes the
  city back, and a computer covers for its mayor until the host returns, so meanwhile only an admin can annex for it.
- A tile host knows only its own city. Its players see the rest of the region on the main server's map.

## What's next

Beta 4 is complete with this part. Next is the release candidate (realistic assets, the mod API and editors). Before
it: tuning the GPU presets from Zac's GPU test report, when it comes.
