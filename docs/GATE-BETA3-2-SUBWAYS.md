# Beta 3.2, part 5 gate: subways

Part 5 is plan 5.3 from Part 5 of the Beta 3.2 plan, which Zac accepted on 2026-11-01: subways are a path type
drawn underground, with stations on the surface, and the bus company runs them. It was split off from the
underground PR (part 4, PR #16).

## What changed

- **Subway tunnels** are a layer of their own under the city, apart from the pipes and the road tunnels.
  - You lay them with the utility tool. In the web client it is "Subway tunnel", key J; in Godot it is the third
    utility choice. Picking the tool switches on the underground view.
  - They go under anything (houses, streets, the water mains) at $150 a cell and $1 a cell a month, shown as
    "Subway tunnels" in the budget.
  - Only the underground bulldozer digs them up. The surface bulldozer leaves them alone.
  - Laying one forgets the bus company's routes, so they are planned again with it.
- **Subway stations** (2 × 2, $6,000, $200 a month, 60 kW) stand over a tunnel or right beside it.
  - A station serves 8 cells around and handles 6,000 people a day.
  - A station that is off the tunnel raises the "nothing stops there" alert, and its "why" says it must stand over a
    subway tunnel or beside it.
  - The building preview says the same before you place it.
- **The city's own subway.** A tunnel with stations works as the city's rail lines do: commuters ride it when it
  beats the road. Trains run at 60 km/h.
- **The bus company's Metro lines.**
  - The company can lease subway stations ($250 a month) and a **subway yard** (4 × 3, $1,500 a month).
  - It runs subway trains on Metro lines: $16,000 a train, 600 seats, two crew, six to a yard.
  - Contract v1.6 adds `TransitMode.Subway`, stop kind `subway-station` and depot kind `subway-depot`.
  - The company's screens in both clients show the 🚇 mode, the labels and the problems ("Lease a subway yard
    first", "click its own kind of stop").
- **The underground view** (U) draws subways in red in both clients, deepest of everything (12 m down).
  - In Godot, each station is a box reaching down from the street to its platforms.
  - The web map marks a subway station with 🚇, and its key gains a Subway row.
- Saves are format 14, which adds the subway layer. The protocol carries it in `FullState.subways` and
  `CellUpdate.subway`.

## Checks

| Check | Where |
| --- | --- |
| A tunnel right across the starter town, under its houses and streets, costs $150 a cell, leaves every building standing, carries subway trains only, is saved, survives the surface bulldozer and goes with the underground one | `SubwayTests` (Sim) |
| A home and a factory on a slow dirt road with a subway between them: commuters ride it, faster than the cars of the same lab without it, the station counts riders, and the budget pays for the tunnel; dig up its middle and nobody rides | `SubwayTests` |
| Stations over the tunnel and beside it work; one two cells off it gets the alert and the "why" | `SubwayTests` |
| The company leases two subway stations and a subway yard, opens a Metro line with one train: it has a route and no problem, people ride, and the crews work at the yard (alongside the same test for rail and tram) | `TrackTests` (Sim) |
| The tool's preview prices the tunnel cell for cell; the tunnel reaches the client in a delta and in a full state as one run; a station's preview knows whether a tunnel serves it | `SubwayClientTests` (ClientModel) |
| On the hill gate's town, the gate's tunnel and its way of finding station sites work | `SubwayClientTests` |
| The Godot hill run lays a 25-cell subway under the town, puts a station near each end, and checks what the underground view drew, then looks at it from the free camera | `Main.Hills` ("subway under the town", "subway drawn") |

## Results

CI run 41470159900 on the PR's head was green on every job.

- Tests: 249 in the sim (5 new) and 95 in the client model (2 new), all passing, plus the region gate and the Godot
  gates as before.
- The Godot hill run:

  ```
  HILLS ok  subway under the town: 25 cells along row 15, stations at 91,13 and 95,14
  HILLS ok  underground view: 119 lengths of water main and 119 of sewer, 15 of road tunnel, 2 pumps and plants; buildings hidden
  HILLS ok  subway drawn: 25 lengths of subway tunnel, 2 stations reaching down to it
  ```

- Frame times on the CI runner's software OpenGL:

  | View | Avg ms | Triangles | Draw calls |
  | --- | --- | --- | --- |
  | classic, underground | 133.4 | 43,607 | 165 |
  | free, the tunnel and pipes underground | 133.4 | 65,401 | 194 |
  | free, the subway under the town | 132.4 | 46,749 | 183 |

- In the web client's run on the same town, the two stations had riders within the first month.
- Screenshots are in [`docs/screenshots/beta3-2-subways`](screenshots/beta3-2-subways). The Godot ones are cut
  from the hill run's contact sheet, and the web ones were taken with Playwright.

## What's next

Lighting (plan Part 4), then growing regions (G4).
