# Beta 4, part 4 gate: snow on the ground and roofs

Part 4 of the Beta 4 plan, which Zac accepted on 2026-11-02 ("Snow on the ground and roofs: accepted"). Until now,
snow lay only on the roads (Beta 3.2, part 3) and on mountain tops above about 300 m. A blizzard left the fields and
the roofs green and bare.

## What changed

- **The city keeps a depth of lying snow.**
  - The depth is measured on open level ground at the city's median height.
  - Snow falls in the showers of the day clock, as on the roads:
    - light snow lays half a centimetre an hour
    - heavy snow lays a centimetre and a half
    - a blizzard lays four centimetres, up to 80 cm in all
  - The rest of the time it melts:
    - about a millimetre an hour in December to February
    - four in March and November
    - twelve in the warmer months
    - fifteen in rain
    - forty in a heatwave
  - Under freezing rain it glazes over and doesn't melt.
  - The depth is saved (save format 15), and the roads' weather line reports it ("12 cm of snow lying").
- **Both clients spread that depth over the ground the same way** (`SnowDepth.At`, shared by the sim and the client
  model):
  - **Height:** a quarter more snow for every 100 m above the median height, and less below it. The depth stays
    between half and twice the city's.
  - **Facing:** up to a third more on slopes facing north, out of the sun, and less on slopes facing south.
  - **Slope:** it thins out on ground steeper than about 30° and is gone by 45°, so cliffs and rock faces stay bare.
  - **Thin snow:** it looks patchy under 2 cm and covers the ground completely by 4 cm.
- **Godot:**
  - The terrain shader whitens the ground by that sum.
  - The building shader lays snow on every roof and ledge. A roof holds a little less than the ground beside it,
    because of the warmth inside, and stays bare while the building burns.
  - Two new project shader globals carry the depth and the median height to both shaders: `snow_mm` and
    `snow_median_y`.
- **Web:** the map's ground is painted white by the same sum and repainted when the depth changes by a centimetre.
- **Protocol:** `RoadWeather.snow_mm`.

## Checks

| Check | Where |
| --- | --- |
| A blizzard lays snow, freezing rain leaves it as it is, and a clear sky melts it, slower in deep winter than in spring and slower than rain does | `SnowTests` (Sim) |
| The depth saves and loads | `SnowTests` |
| More snow high up and facing north, none on a cliff, none when none has fallen | `SnowTests` |
| On a real server, a pinned blizzard lays snow the client reads; gentle ground at the median height holds close to the city's depth and shows white | `ClockClientTests.SnowLiesOnTheGroundUnderABlizzard` (ClientModel) |
| The hill gate pins a blizzard over the hill town until 10 cm lie; at least half the dry ground is white by the clients' sum, and the town is measured and pictured from both cameras | CI step "Hills" (`Main.Hills.cs`) |

## Results

- **Tests:** 257 in the sim (3 new) pass. The new client test passes.
- **Locally, the hill gate passed every check**, including:

  ```
  HILLS ok  snow on the ground: 100 mm on level ground at the median height (96 m above the water); 10,243 of 13,473 dry cells white; Snow falling, snow on the roads, snow on the streets, 10 cm of snow lying
  HILLS | classic, snow | 38 | 133.3 | 143.2 | 143.8 | 7.5 | 157,819 | 245 |
  HILLS | free, snow | 38 | 133.1 | 146.7 | 149.1 | 7.5 | 195,933 | 283 |
  ```

  The cells that stay dark are the steep rock faces and the water's edges.
- **CI:** the hills step's timeout went from 900 to 1200 s for the blizzard.
- **Screenshots** are in [`docs/screenshots/beta4-snow`](screenshots/beta4-snow):
  - Godot's classic and free cameras over the snowed-in hill town, from the local gate run.
  - The web map before and after a blizzard, and the town close up, taken with Playwright against a local server.

## Known limits

- Godot's minimap doesn't show the snow yet.
- Snow lies the same on every roof. Roofs over heated and unheated buildings don't differ yet.
- Drifts and the snow banked along ploughed roads are the roads' own (Beta 3.2, part 3). Snow on the ground doesn't
  slow traffic beyond what the roads already carry.

## What's next

Part 3, water quality.
