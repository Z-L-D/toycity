# Alpha 1: a city that grows

Alpha 1 is the first playable slice: one city, one player, the 2D web client. The goal is a town
that grows, declines and recovers for reasons the player can see. This page records the rules
and numbers as built, so they can be tuned with the code open beside it.

## Gates

| Gate | How it is checked |
| --- | --- |
| Runs headless | `tools/ToyCity.Bench` runs any number of years with no client; CI runs 5 years |
| Web client connects and builds | Drove the client in a headless browser: paths, zones, civic buildings, bulldoze, layers, save, new city, load |
| Save/load reproduces state | `SaveTests`: save, load, run both on; state hashes match |
| Deterministic replay | `DeterminismTests`: same seed and actions give the same hash; replaying the action log rebuilds the city |
| Every non-growing lot explains why | `GrowthTests.EveryEmptyZonedLotExplainsItself`, and growth uses the same rule function as the why-panel |
| An hour of play | Not yet: needs a person at the keyboard |

## Time

One tick is one day; a month is 30 days and a year 12 months. Speed 1 runs a day every 500 ms,
speed 2 every 150 ms and speed 3 every 50 ms. Building works while paused. The server autosaves
every in-game January 1st to `saves/autosave.toycity`.

## Map

Cells are 16 m (as SimCity 4). Maps are 64, 128 or 256 cells square. Terrain is fractal noise
from the seed with about 18% under water. Paths cannot cross water or climb more than one height
step per cell (bridges and tunnels come later).

## Paths

The 15 profiles from the brief are in `content/profiles.json`. Alpha 1 uses their speed,
capacity, frontage, cost, upkeep and noise; the strip cross-sections are stored for the future
path designer. Highways have no frontage, so lots beside them get no access. One-way roads are
treated as two-way for now.

## Zones and growth

Zoning costs $5 a cell. Buildings grow on lots of several sizes, as in SimCity 4. The sizes are
listed in `content/buildings.json` as frontage × depth, and either side may face the road:

| Density | Lot sizes |
| --- | --- |
| Low | 1×1, 1×2, 2×2 |
| Medium | 1×2, 2×2, 2×3, 3×3 |
| High | 2×2, 2×3, 3×3, 3×4, 4×4 |

When a lot grows, the size is picked at random from those that fit. The 27 templates (zone ×
density × wealth) give residents, jobs and utility use per cell, so a bigger lot holds more and
takes a little longer to build. A lot fits when every cell is empty land in the same zone and
density and one side touches a path with frontage. So a block can be as deep as its deepest lot
on each side; cells no lot can reach are greyed out and crossed on the map.

A lot grows when all of these hold. They are the same checks the why-panel lists.

| Need | Rule |
| --- | --- |
| Road access | Some lot of an allowed size, containing this cell, has a side on a path with frontage |
| Power | On a grid with a plant, and the grid is not in brownout |
| Water | Medium and high density only: within 6 cells of a pipe whose network has a working pump with spare capacity |
| Desirability | At least 5 (low), 25 (medium) or 45 (high) |
| Demand | Demand above zero for a wealth level the land value allows |

Land value sets the highest wealth that can grow: residential and commercial need 60 for $$ and
110 for $$$. Industry picks its wealth from demand only. Growth checks run once a month per lot,
spread over the days, and succeed with a chance of 5% plus a quarter of the demand (at most 30%).
Building takes 10, 20 or 40 days by density, plus two days per extra cell of lot.

Existing buildings redevelop when upzoned, or when land value and demand support a richer
building.

## Decline and recovery

Each month every zoned building is checked against the same needs, with desirability allowed to
fall 30 points below the growth threshold, demand allowed down to -60, and two more needs:

- Homes need at least one job within a 60 minute commute, but only when the city has free jobs.
  Plain unemployment lowers residential demand instead.
- Workplaces need at least one worker, but only when someone in town is looking for work.

Six failing months in a row and the building is abandoned. Three good months and it is fixed up.
After 30 months abandoned it crumbles and the lot can grow again.

## Utilities

- **Power** flows through power lines, buildings, zoned land and paths with frontage (as in
  SimCity 4), but not across highways or open land. Each grid adds up its plants; buildings are
  served in id order until capacity runs out (brownout). Unpowered civic buildings do nothing.
- **Water**: pumps need power and a pipe touching them. Output falls with water pollution at the
  pump. Coverage reaches 6 cells from any pipe.
- **Garbage**: landfills need a road within reach. Uncollected garbage lowers desirability for
  homes and shops by 1 point per 100 units, up to 30.

## Commutes and traffic

Each home sends half its residents to work. Once a month (staggered by building id) a home runs
a shortest-time search over paths from its driveway and fills free jobs at the nearest
workplaces, up to 60 minutes away. Travel time per cell comes from the profile speed, stretched
30× so a 60 minute commute covers about 100 road cells, and slowed by congestion with the BPR
curve (time × (1 + 0.15 (v/c)^4), capped at 5×). Daily capacity is a quarter of the hourly
figure. Ties break on cell index, so results are deterministic.

## Fields

Recomputed monthly:

- **Pollution** from industry, plants, landfills and busy roads, spreading with linear falloff.
- **Noise** from roads (the loudest nearby road counts) and buildings.
- **Coverage** for police, fire, health, education and parks; radius scales with the square root
  of funding.
- **Land value**: base 40, plus elevation, nearby water, parks and services, minus pollution and
  noise.
- **Desirability** per zone. Homes like land value, parks and services and dislike pollution,
  noise, traffic, garbage and long commutes. Shops like land value, passing traffic and
  customers. Industry likes cheap land and fire cover.

## Demand

Demand runs from -100 to 100 for each zone and wealth level, and counts buildings under
construction so the city does not overshoot.

- **Residential**: a regional pull that fades as the city grows, plus jobs minus workers.
- **Commercial**: one shop job per four residents.
- **Industrial**: 50 jobs of export demand plus 45% of the labor force.
- High-wealth residential and high-tech industry need education; dirty industry dislikes it.
- Each tax point above 9% lowers that group's demand by 6; each point below raises it by 6.

Demand by source (local, regional, tourism) with visible capacity limits arrives with regions
and side games.

## Money

Start with $100,000. Taxes are monthly per resident (residential) or per filled job (commercial
and industrial), by wealth. Upkeep is per path cell by profile, 10 cents per cell of power line
or pipe, and each civic building's monthly cost times its funding. Loans of up to $500,000 in
total are repaid over about 10 years at 6% a year. The city can go into the red; the game goes on.

## Performance

On the development container, the starter town runs about 1,000 to 2,000 days a second. The
stress run (25 towns on a 256 map, about 33,000 people and 5,000 buildings) averages 180 days a
second, with month-start days up to about 100 ms. That is fine at speeds 1 and 2 and causes a
small hitch once a month at speed 3. Spreading the monthly field work over several days is the
first optimisation to make.

## Known limits

- Lots are rectangles on the grid; no parcel subdivision of road-enclosed blocks or curved roads yet.
- No bridges, tunnels, rail or transit; one-way roads are two-way.
- No fire, crime or disasters as events, only coverage.
- Building condition and aging are stored but not simulated yet.
- The web client is a debug-quality 2D view; the Godot client comes in Beta 1.
