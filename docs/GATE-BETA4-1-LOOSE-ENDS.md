# Beta 4.1, part 4: loose ends

Three things left over from Beta 3.2 and Beta 4, each of which changes what a save or the protocol holds. They go in
now so the release candidate can make its save promise over them.

## What changed

- **Rivers carry on into open land already made (`Region.CarryRiversOn`).**
  - Before, in a growing region (or a huge one made a tile at a time), a new tile's river that ran toward a tile
    already made stopped at the edge. The ground there was fixed, because a city might stand on it.
  - Now, if nobody has founded a city on that tile, the river carries on across it. It runs down the open land's
    lowest way, cutting a channel a step deep, and up to three steps through a rise. It runs until it joins a river
    or the sea, or ends in a hollow.
  - If it runs off the tile's far edge into more open land, it carries on there too.
  - The river never climbs, and the tiles' shared edges never move.
  - A tile with a city keeps its ground exactly as it was. The water runs off the edge into it, as before.
  - The region's ground links the new river to the old one across the border, so floods and foul water follow it.
  - Tiles' ground is already saved with the region, so this needs no new save format.
- **The clients are sent each cell's foulness.**
  - `FullState.foul` carries one byte a cell, 0 clean to 255. Dry land is 0.
  - `TickDelta.foul` sends the whole layer again only when it has changed, which happens about monthly.
  - Godot's water shader turns foul water murky, as does the web map's water:
    - from about 15 to 65 (unsafe to drink at 30, it stinks past 60), the water turns a cloudy green-brown and nothing shows
      through it;
    - the worst water carries a drifting scum, and its foam yellows.
  - The water quality layer (overlay 21) still shows the numbers.
- **Snow on the roads thaws with the calendar.**
  - Snow on the ground already thawed by the calendar's days, so a fast calendar didn't keep a winter's snow into
    summer. The roads now do the same, and so do the banks the crews pushed to the kerbs.
  - The thaw is a third of the cover a day from April to October, and 6 % a day in March and November. There is no
    thaw in midwinter or while it's still snowing.

## Checks

- Sim: 4 new tests:
  - `GrowingRegionTests.ARiverCarriesOnIntoOpenLandAlreadyMade` checks:
    - every new tile's river heading into the first tile carries on into it, never climbing;
    - the tiles' shared corners are unchanged.
  - `GrowingRegionTests.ACitysGroundIsLeftAsItIs`: with a city on the first tile, its corners and rivers don't
    change as tiles are added round it.
  - `WaterQualityTests.TheClientsAreSentEachCellsFoulness` checks:
    - the full state carries the layer;
    - a delta sends it once raw sewage fouls the river, and not again until it changes;
    - dry land is 0.
  - `SnowTests.ASummerThawsTheRoadsEvenOnAFastCalendar`: June snow on the roads and in the banks is gone a few
    calendar days after it stops, with the crews unfunded, while January's stays.

  All 312 sim tests pass.
- ClientModel: `FoulMirrorTests` checks that a delta's foulness reaches the mirror.
- The Godot hill gate's sewage plant lets out raw sewage. Its free-camera shot of the foul water now looks down from
  over the bay. Shots in [`docs/screenshots/beta4-1-loose-ends`](screenshots/beta4-1-loose-ends) were taken on that
  city: Godot's classic view and the web map show the murky plume spreading into the bay below the outfall.
- Web `tsc` passes.

## Known limits

- A river carried into open land is a simple channel down the lowest way. The water doesn't pond in the hollow
  where a river ends, and the ground isn't worn around the new river as it is when a tile is first made.
- Murky water is only a colour. Fish, swimming and farms don't read the foulness yet.
