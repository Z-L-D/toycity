# Beta 4.1, part 2: farm country

Zac, 2026-10-30: land gets a fertility value from flatness and closeness to waterways and floodzones; farms
auto-populate fertile areas at map start with hectare-based road grids and windbreaks between. Zac, 2026-11-04:
"another component that might be pre-RC work is the concept I mentioned about how regions could be started that are
prefilled with farms in low lying areas."

The farm side game stays in 2.x. This part is the ground it will grow on: farmland the mayor's city starts with,
builds over and earns a little from.

## What changed

- **Fertility** (`World.FertilityAt`, 0 to 100) is worked out from the ground, never saved. A cell scores best when:
  - it is flat (nothing is ploughed past a 20 % slope);
  - its soil is deep (thin soil over rock still ploughs, bare rock doesn't);
  - it lies low (full marks up to 30 m over the sea, falling to nothing at 150 m);
  - it is near a river, where the floodplain's silt adds up to a quarter.

  The wet margins of the water score nothing. The query panel shows each cell's fertility, its farm and its plants.
- **Farm country** (`Farms.cs`, save format 20) is an option for a new region or a lone city.
  - Farms are laid on a grid of 256 m blocks in the region's own coordinates. A tile's side is a whole number of
    blocks, so the grid carries on across tile borders.
  - A block is farmed when four fifths of its cells are fertile and the block is fertile as a whole.
  - Each farmed block has:
    - a farm track along its edges;
    - four fields of about 1.5 hectares, each with its own crop (wheat, corn, soybeans, hay or canola) and rows
      running one way or the other;
    - on two blocks in three, a farmstead in the corner;
    - on one in three, a windbreak of conifers across the middle.
  - Fields push the woods back. A field never grows trees, and the tree planter skips it.
- **The city and its farms.**
  - Building, paving, zoning or flooding a cell takes its farm the same day.
  - Bulldozing clears farmland for nothing.
  - The farms pay a "Farm rates" line on the budget: a dollar a month for every 20 field cells.
- **Toy Valley**, the new server's region, is farm country. Its south and east tiles are farmland down to the sea.
  Toyville itself is in the hills, with a few fields.
- **Protocol.**
  - `NewRegionRequest.farms` and `NewCityRequest.farms` choose farm country.
  - `RegionSave.farms` saves the region's choice.
  - `FullState.farms` and `CellUpdate.farm` carry each cell's farm, packed as `FarmLand` packs it.
- **Godot.**
  - The ground shader draws the farms from a texel per cell:
    - fields in their crop's colour for the time of year (green wheat in spring, gold in June, stubble, then
      ploughed earth; corn tall and green through summer, then tan; yellow canola in May), with their rows;
    - farm tracks as dirt with two wheel ruts and grass either side;
    - farmyards in gravel.
  - `FarmsView` stands a farmstead on each yard: a white house, a red barn with a gable roof, two silos and a
    machine shed. They are all drawn in one instanced batch.
- **Web.**
  - The map draws fields in the same colours, from the same table, with their rows when close.
  - Farm tracks are drawn as dirt strips, and farmsteads from above.
  - The New dialog has a "Farm country" box, ticked by default.

## Checks

- Sim: 6 new tests in `FarmTests`:
  - farm country on three seeds, with farms only on the grid and on fertile ground (seed 42 is all mountains and
    has none);
  - fertility favours low ground and is nothing at the water's edge;
  - paving and bulldozing take farms, and fields don't grow trees;
  - the farm rates, and the query panel;
  - save, load and replay;
  - two tiles on one grid, and a tile replays from its founding with the same farms.

  All 304 sim tests pass.
- Godot smoke run passes. Shots were taken on Eastfield, founded on Toy Valley's east tile, in July and October.
- Web `tsc` passes. Shots were taken on Eastfield in January.

## Known limits

- Farms don't yet grow anything the city uses: no farm jobs, no food or freight. That is the farm side game's job.
- Fields don't change crop from year to year, and there's no harvest traffic.
- The region map doesn't show farmland on tiles nobody has founded yet. It appears when a city is founded there.
- Steep mountain maps have no farm country at all. That is right for the ground, but a region seed decides how much
  farmland there is.
