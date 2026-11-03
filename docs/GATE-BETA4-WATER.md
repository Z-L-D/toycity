# Beta 4, part 3 gate: water quality

Part 3 of the Beta 4 plan, which Zac accepted on 2026-11-02 ("Water quality: accepted. Immoral gameplay should
always be allowed"). On 2026-11-03 he added that foul water crossing into the next city should be "a togglable option
per region". Before this part, sewage only fouled the ground near the buildings that made it, and a treatment plant
made it disappear. Rivers, lakes and the sea were always clean.

## What changed

- **Foul water.** Each water cell has a foulness from 0 (clean) to 255, worked out monthly. It's saved (save format
  16). It comes from:
  - water pollution that reaches the water: industry, landfill, and raw sewage soaking in from the buildings that
    made it;
  - each sewage plant's **outfall**, in the nearest water within 16 cells of it. The outfall carries what the plant
    treats, less what treatment takes out. A plant with no water nearby lets it soak into the ground around it
    instead;
  - a river coming in across a border from a city upstream (below).
- **Where foul water goes.** It runs down a river and loses little a cell, so a river stays foul for a long way below
  an outfall. Along a level stretch it runs towards where the river next steps down. In lakes and the sea it spreads
  out and thins quickly. It never climbs a river.
- **Sewage treatment** is a city setting on the budget panel:

  | Level | Let out untreated | Plants' upkeep |
  | --- | --- | --- |
  | None (raw sewage out) | 100% | 25% |
  | Primary | 50% | 60% |
  | Secondary (the usual) | 15% | 100% |
  | Tertiary | 3% | 160% |

  Setting it to none is allowed and saves three quarters of the plants' upkeep. The news reports it.
- **Tap water.** Each pump draws the worst water within 4 cells of it: the river or lake beside it, or the polluted
  ground it stands on. Each water network's tap water is the pumps' water, weighted by how much each pumps.
  - A new **Water Treatment Works** ($4,000, 3 × 2, $150 a month) cleans the mains it stands beside. It cleans 12,000
    units a day, and takes out 95% of the dirt in the share of the water it keeps up with.
  - A pump's info says how clean the water it draws is. A works' info says how clean the tap water is.
- **What foul water does.** None of it is forbidden. It just costs the city:
  - **Smell:** land within 3 cells of water fouler than 60 smells. That's a new "Foul water nearby" factor on
    residential and commercial desirability and on land value.
  - **Tap water:** tap water fouler than 30 is a new "Unsafe tap water" factor on residential and commercial
    desirability.
    - Above 60, a **boil-water notice** goes out, as a notice, a headline and a warning alert.
    - Above 140, people fall ill: a headline and a danger alert.
  - **Visitors:** the city draws up to 40% fewer visitors as more of its water turns foul.
  - **News:** a headline reports dead fish when any of the water passes 180.
  - **Alerts:** a "foul water" alert appears once 8 or more water cells smell.
- **Across borders.** The region carries each river's foulness from the city it leaves to the city it enters. It
  goes as a logged action (`WaterInflowAction`), the way floods do, so a city's log still replays on its own. The
  region sends one only when the foulness changes.
  - **The region's option** "Rivers carry foul water on into the next city" is on by default. It's on the region
    panel in both clients. An admin can change it, or the sole mayor when only one player has a city in the region.
    It's saved with the region. Turned off, each river comes in clean.
- **The water quality layer** (overlay 21) is in both clients. It shows the water's own foulness on water cells, and
  on land the worse of the smell and the tap water.
- **Protocol:**
  - `SetTreatment` (an action)
  - `SetRegionWater` (a client message)
  - `RegionWater` (a logged region record)
  - `Stats.water_quality`
  - `BuildingTypeInfo.water_treat`
  - `RegionState.water_stays` and `RegionSave.water_stays`
  - `OVERLAY_WATER_QUALITY`

## Checks

| Check | Where |
| --- | --- |
| The less the plants treat, the fouler the water at their outfall (none > primary > secondary > tertiary, tertiary nearly clean) and the cheaper their upkeep | `WaterQualityTests` (Sim) |
| Raw sewage out smells: the land beside it gets the "Foul water nearby" factor, the water's info says it's foul, and the foul-water alert shows | `WaterQualityTests` |
| Foul water runs down a river, never up it, and fouls cells below | `WaterQualityTests` |
| A town's raw sewage reaches its taps through its pumps; a treatment works on the mains cleans it to a tenth | `WaterQualityTests` |
| Water quality saves, loads and replays to the same state hash | `WaterQualityTests` |
| Only the region brings water in; out-of-range actions are refused | `WaterQualityTests` |
| In a region, foul water leaving one city comes into the next downstream; turned off, it comes in clean | `WaterQualityTests` |
| On a real server: the budget panel sets the treatment, the stats carry it and the upkeep, the water quality layer comes back, and only an admin may change the region's option | `ClockClientTests.TheMayorSetsTreatmentAndSeesTheWater` (ClientModel) |
| Snow thaws by the calendar's season too, even on a fast calendar | `SnowTests` |
| The hill gate's sewage plant lets out raw sewage until the water below it is foul. The layer shows it, and it's pictured from both cameras | CI step "Hills" (`Main.Hills.cs`) |

## Results

- **Tests:** 265 in the sim pass, 8 of them new. 105 in the client model pass, 1 of them new.
- **Locally, the hill gate passed every check**, including its new one:

  ```
  HILLS ok  raw sewage fouls the water: 1% of the water is foul, sewage treatment: none (raw sewage out). Worst 152 at 97,20; plants' upkeep 25%
  HILLS | classic, water quality | 38 | 133.3 | 144.3 | 144.5 | 7.5 | 136,553 | 219 |
  HILLS | free, the foul river | 38 | 133.3 | 144.7 | 147.0 | 7.5 | 113,133 | 208 |
  ```

  The Godot client now draws map layers on the water as well as the ground, so the water quality layer shows the
  plume where the river leaves the outfall and spreads into the sea.
- **The web client**, against a local server: the hill town with a sewage plant by the coast, treatment set to none.
  The worst water reached 100, and 13 cells smelled. Set back to tertiary, the worst fell to 19 a few months later.
- **Fixed on the way: snow in summer.** At a fast calendar pace, the day clock barely moves, so snow that melts by
  the clock's hour lay on into June. Snow now also thaws by the calendar's own days with the season: 4 cm a day from
  April to October and 8 mm in March and November. It doesn't thaw that way while it's still snowing. A new test
  covers it (`SnowTests.ASummerThawsTheSnowEvenOnAFastCalendar`).
- **Screenshots** are in [`docs/screenshots/beta4-water`](screenshots/beta4-water).

## Known limits

- Foul water isn't drawn any differently on the map yet: the water quality layer shows it. A murky colour on the
  water itself wants each cell's foulness sent to the clients, which can come with the graphics work.
- Foul water doesn't damage farms or fishing yet: the farming side game will read it.
- Groundwater only carries what's spread around the buildings that pollute it. It doesn't flow.
- At a fast calendar pace, snow on the roads still clears by the day clock's hour, so it can stay on the roads after
  the ground has thawed. It wants the same calendar thaw as the ground.

## What's next

Part 1, direct dispatch.
