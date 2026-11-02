# Beta 3.2, part 3 gate: weather on the roads

Part 3 is Part 4 of the Beta 3.2 plan (4.1 to 4.6), as Zac accepted it on 2026-11-01, with his note that freezing
rain also raises accidents.

## What changed

- **Freezing rain** joins rain, snow, heat and wind (🧊 freezing drizzle, freezing rain, an ice storm). It comes in
  winter, and now and then in March and November. It ices the roads, raises crashes and puts power use up.
- **Showers on the day clock.** The region's fronts still follow the calendar. Within a front, rain, snow or
  freezing rain falls in some hours of the day clock and not others: about half the hours for a light front, nearly
  all of them for a severe one. The hours are the same over every city of a region.
- **Each surface carries its share of traffic** in each condition (`rainPct`, `stormPct`, `snowPct`, `icePct` in
  `content/profiles.json`), as the plan proposed:

  | Surface | Rain | Heavy rain, storms | Snow | Freezing rain |
  | --- | --- | --- | --- | --- |
  | Dirt | 40% | 10% | 30% | 20% |
  | Gravel, limestone | 80% | 60% | 50% | 30% |
  | Brick | 90% | 80% | 40% | 15% |
  | Asphalt, concrete | 95% | 85% | 35% | 10% |
  | Drained road or highway, crews funded | 100% | 95% | 85% (while it falls) | 75% (while it falls) |

  The share cuts the road's capacity, and bad going slows even an empty road, up to twice as long. Commutes, buses
  and the dispatcher's crews all feel it, since they route on the same travel times. Some people stay home in snow
  and ice: 5% for each step of a snowfall's strength and 10% for each step of freezing rain, and more while the streets stay snowed in.
- **The ground remembers.**
  - Rain wets the roads, and they dry in about three clock hours.
  - It turns dirt to mud, which takes a day or more of the clock to dry.
  - Snow and ice build up on each tier of road (highways, roads, then streets and paths) until the sun melts them,
    slowly in January and fast in a heatwave, or the crews clear them.
- **A road crews budget** in the Budget panel, 0 to 200%, like a civic building's funding. The crews plough and
  salt highways first, then roads, then streets, as far as their budget goes. At 100% they keep up with light snow
  everywhere; in a blizzard they hold the bigger roads and the streets fill. Their cost is a monthly budget line,
  "Road crews", by the cells of each tier. When the streets are snowed in, the newspaper says whether the crews are
  short of money or just still on the main roads.
- **Drains.** Highways and the bigger roads are built with drains. While the crews are funded they carry nearly
  everything in the rain, and shallow floodwater (stage 1) leaves them at 60% where other roads are under water.
- **Crashes.** On a busy road, the chance of a crash rises with what the weather cuts: a little in the wet, twice the
  cut in snow and four times it on ice. The city looks for crashes twice as often while the roads are bad. The calls
  say "a crash on the ice", "a car stuck in the mud", and so on.
- **You can see it**, in both clients:
  - Wet roads darken and shine (in Godot, the road material's roughness drops).
  - Mud browns dirt surfaces.
  - Snow lies white over the carriageway and its paint where the crews haven't been, and ice lies as a grey glaze.
  - Ploughed roads show the snow banked along both kerbs.
  - The "why" for a road names the weather and its share.
  - The traffic layer's legend says what the roads are carrying.
  - The Godot clock's tooltip gives the state of the roads.
- **Pinning the sky.** An admin, or the mayor of every city in the region, can pin the region's weather
  (`PinWeather`), for showing and testing it. It isn't saved. The gate uses it, since a gate city is a lone city
  with no region sky of its own.
- Saves are format 12. An older city starts with dry roads and its crews at 100%. In clear weather on dry roads
  nothing changes: every existing test passes unchanged.

## Checks

| Check | Where |
| --- | --- |
| A dry city plays as before: every road carries 100%, and the crews show on the budget | `RoadWeatherTests` (Sim) |
| Showers fall in about half the clock hours of a light front and nearly all of a severe one | `RoadWeatherTests` |
| Light snow: crews on full budget keep the roads clear, with none it lies; a blizzard: the crews hold the roads, the streets fill, banks form on the roads' kerbs, and the paper says the streets are snowed in | `RoadWeatherTests` |
| Under full snow an asphalt road carries 35%, its traffic level and travel time rise, its "why" says so, and it melts | `RoadWeatherTests` |
| After rain, asphalt is dry within hours while a dirt road is still mud, until it dries | `RoadWeatherTests` |
| An ice storm brings more crash calls than a dry spell, and keeps people home | `RoadWeatherTests` |
| The roads' weather and the crews' budget are saved; a budget out of range is refused | `RoadWeatherTests` |
| On a real server an admin pins a blizzard, the Budget panel sets the crews, the roads show snow, and another player is refused | `ClockClientTests` (ClientModel) |
| The Godot gate pins a blizzard, freezing rain and storms over the gate city and checks each reaches the roads, with the crews set from the Budget panel | `GateRun.Weather` ("snow on the roads", "road crews' budget", "ice on the roads", "rain on the roads") |

## Results

CI run 41445866111 on the PR's head was green on every job.

- Tests: 242 in the sim (7 new) and 93 in the client model (1 new), all passing.
- The Godot gate ran the Alpha 3 loop as before, then the weather steps on the benchmark city (256 × 256, all of its
  roads on the crews' second tier):

  ```
  GATE ok  snow on the roads: Snow falling, snow on the roads; crews 100%, cover 0 / 50 / 0 (highways / roads / streets), wet 0%
  GATE ok  road crews' budget: Snow falling, snow on the roads; crews 200%, cover 0 / 50 / 0 (highways / roads / streets), wet 0%
  GATE ok  ice on the roads: Freezing rain falling, ice on the roads; crews 200%, cover 0 / 51 / 0 (highways / roads / streets), wet 100%
  GATE ok  rain on the roads: Rain falling, ice on the roads; crews 200%, cover 0 / 51 / 0 (highways / roads / streets), wet 100%
  ```

- The gate step took 11 min 13 s (12:42:07 to 12:53:20), its limit raised from 15 to 25 minutes. The weather
  steps run at four months a clock day, so a blizzard snows the roads in about 15 calendar days.
- The screenshots are web client shots in [`docs/screenshots/beta3-2-roads`](screenshots/beta3-2-roads). The Godot
  gate's snow, ice and rain shots are in the CI contact sheet, but at zoom 3 the benchmark city's towers hide most of
  its roads, so they aren't in the README.

## What's next

The lighting PR will add reflections on wet roads and snow on the ground and roofs (plan 6.6). Snow on the land
beside the roads comes with it. Underground comes before it.
