# Beta 3.2, part 6 gate: light and shadow

Part 6 is Part 6 of the Beta 3.2 plan (6.1 to 6.6), with the web client's share from Part 7: "Lighting stays simpler
there, with day and night tints and lit windows". Zac accepted the plan on 2026-11-01. Tilt-shift follows his note
that it shows only while querying a tile (or in a photo mode). The water's look follows the Sea of Thieves talk he
pointed to. The sun follows the clock by default (Q4), with a setting to pin it.

## What changed

- **The light model** (`Daylight` in the client model, shared by both clients):
  - It works out the sun's path for a city at 45° north from the day clock's hour and the calendar's month: east in the
    morning, south at noon, west in the evening. It is about 68° up at midsummer noon and 21° at midwinter noon.
  - The sunlight is red at the horizon, golden up to about 12°, and white above 30°. After dark the moon lights the
    city, dim and blue.
  - The sky and the horizon colour follow the sun, the shade is cool blue by day, and distance haze thickens at dawn
    and dusk.
  - Rain and snow dim and grey the sun, sky and shade, and thicken the haze. The more so while a shower falls and in
    heavy weather. A stormy dusk turns the lights on early.
- **Godot** lights the city from the model:
  - The sun's direction, colour and strength follow it, and shadows soften under cloud.
  - A procedural sky, the ambient light and the fog follow it too. The classic camera's haze starts past what it looks
    at; the free camera's thickens with distance.
- **Night** (6.4):
  - Windows light up in rows a storey apart, as many as the building's people. Homes light more than workplaces, and
    services glow when they have power. Abandoned, unpowered or burning buildings stay dark.
  - Lamps throw a pool of warm light on every other road cell. A new road shader does this and keeps part 4's
    wet-road shine.
  - Buses show their lights.
- **The water** (6.5, after the Sea of Thieves talk) reflects the sky's colour at a glancing angle, and in daylight the
  light glows green-blue through the crests of its ripples.
- **The graphics options:**
  - **Lighting** (6.3): sun only, contact shadows (SSAO), bounce light (SSIL), or full (SDFGI). Low and Medium are sun
    only, High has contact shadows, Ultra is full. On the High shadow setting the sun's shadows get soft edges (6.2).
  - **Tilt-shift** (6.5): while a cell is picked with the query tool, the foreground and the distance blur around it.
    It is on except on Low.
  - **Time of day** (6.1): follow the clock, or pin Morning, Noon, Golden hour, Dusk or Night. A pin is set by where
    the sun is, not by the hour, so a pinned golden hour is golden in any month. It isn't part of a preset.
- **The measured runs** (benchmarks, gates, shots) pin noon, so their frame times and pictures compare with earlier
  runs. The hill run tries golden hour and night itself.
- **The web client** (Part 7):
  - A warm wash at golden hour, a grey one under heavy weather, and a dark blue one at night.
  - At night, lit windows on each building (as many as its people) and a pool of street light on every other road cell.
  - Layers has a "Day and night" button that switches to "Always daylight".
- Wet and snowy roads (6.6) were drawn in part 3. Snow on the ground and roofs is left for later.

## Checks

| Check | Where |
| --- | --- |
| The sun rises in the east, peaks in the south, sets in the west; about 68° up at midsummer noon and 21° at midwinter noon; below the horizon at midnight | `DaylightTests` (ClientModel) |
| Golden hour is warmer and dimmer than noon, with thicker haze; night is dark and blue with the lights on; the light always comes down onto the ground, and at noon it travels north | `DaylightTests` |
| Heavy weather dims the sun, greys the sky and thickens the haze; the overcast is read from the city's weather | `DaylightTests` |
| A pinned noon, golden hour and night look the same in January and June | `DaylightTests` |
| Homes light more windows than workplaces, services some, and unpowered, abandoned or empty buildings none | `DaylightTests` |
| The new options round-trip through saving, presets get more lighting from Low to Ultra, and the time of day is kept across presets | `GraphicsSettingsTests` |
| The Godot hill run grows the town, then pins golden hour and night, checks the light and that windows are lit, and measures both cameras at each | `Main.Hills` ("golden hour", "night") |

## Results

CI run 41502387315 on the PR's head was green on every job.

- Tests: 249 in the sim and 101 in the client model (6 new), all passing, plus the region gate and the Godot gates.
- The Godot hill run:

  ```
  HILLS ok  golden hour: sun 6° up at 255°, light (1.00, 0.72, 0.48) × 0.74, night 0.00
  HILLS ok  night: sun -35° up at 0°, light (0.55, 0.65, 0.95) × 0.12, night 1.00; 40 of 57 buildings with lit windows
  ```

- Frame times on the CI runner's software OpenGL:

  | View | Avg ms | Triangles | Draw calls |
  | --- | --- | --- | --- |
  | classic, the town on its hillside (noon) | 132.9 | 78,671 | 177 |
  | classic, golden hour | 133.3 | 121,971 | 222 |
  | free, golden hour | 133.4 | 166,855 | 264 |
  | classic, night | 133.5 | 133,491 | 227 |
  | free, night | 133.6 | 157,639 | 260 |

  The town is bigger by the golden hour and night steps, because it grows first; that is where the extra triangles
  come from.
- The runner's software OpenGL holds every view at about 133 ms a frame, so it can't show what the lighting costs.
  Contact shadows, bounce light and tilt-shift are Forward+ features, which the runner's compatibility renderer leaves
  out, so they need measuring on a real GPU. One sign of a cost is the whole run time: the dense benchmark step took
  7 min 18 s against 4 min 35 s on the subways PR, and the Alpha 3 gate run 14 min 46 s against 10 min 35 s.
- Screenshots are in [`docs/screenshots/beta3-2-light`](screenshots/beta3-2-light). The Godot ones are cut from the
  hill run's contact sheet, and the web ones were taken with Playwright on the same town at a pinned hour.

## What's next

Growing regions (G4): a game starts as one map, and new tiles join on any of its four sides, continuing the terrain,
rivers and geology at the shared edge.
