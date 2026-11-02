# Beta 3.2, part 4 gate: underground

Part 4 is Part 5 of the Beta 3.2 plan (5.1, 5.2 and 5.4), as Zac accepted it on 2026-11-01, with his notes that sewers
come "only if both pipes are laid at once" and that a tunnel shows as one dashed box around its whole length.
Subways (5.3) come in the next PR.

## What changed

- **One pipe, two mains.** The water pipe tool is now the water and sewer pipe: every pipe carries a water main and
  a sewer side by side, so one tool lays both and the sewers run wherever the water does. Every pipe already laid
  has its sewer, so an older city has sewers under every main. Bulldozing in the underground view digs up both.
- **Sewage treatment plants.** A new water building, 3 × 3, $5,000 and $180 a month, that needs power and treats
  8,000 units of sewage a day from the pipes beside it (as a pump pumps into them), scaled by its funding.
- **Sewage.** A building sends down the sewer what it uses of water. Sewers reach the buildings near them as the
  mains do (six cells), and each network's plants treat what reaches them, up to their capacity.
- **A small town can do without**, as in SimCity 4. Its septic tanks take 6,000 units a day, about what one pump
  supplies, a town of three or four thousand. Past that, raw sewage:
  - seeps into the water near the buildings that made it, so pumps near them pump less;
  - lowers desirability for homes by a point per 150 units a day, up to 20, and for shops by half that;
  - puts up an alert and a notice saying to build a plant beside the pipes.
- **Shown in both clients.** The Budget panel's utilities have a Sewage row (made / treated, and what's left raw). The
  plant's "why" says what it treats. The encyclopedia's Water entry explains the sewers.
- **The underground view** (U, or the Layers panel):
  - In Godot, the buildings, roads and traffic go. The ground turns to a cutaway of dark soil with the streets
    overhead as faint pale lines.
  - Beneath it, every pipe shows as its water main (blue, 2.5 m down) and its sewer (brown, 5 m down) side by side.
    They follow the ground over the hills. Road tunnels show at their decks, and the pumps and plants are picked out
    at the surface.
  - It is drawn through the ground, deepest first, from the classic and the free cameras.
  - The web client draws the same: soil, faint streets, both mains, solid tunnels, the pumps and plants, and a key.
  - The panel says how the city's sewage is doing.
- **Tunnels from above** (Zac's note): in the web client's surface view a tunnel is now one dashed outline around its
  whole length, bends and all, not a box on every cell.
- **The benchmark city** gets sewage plants in as many blocks as it has pumps, and pipes under every tenth street
  north to south. That joins its mains into one network, so the whole city's sewage is treated.
- Saves are format 13, which adds the sewers' reach and totals. An older city measures its sewers when it loads.

## Checks

| Check | Where |
| --- | --- |
| A starter town makes sewage and gets by on its septic tanks: nothing raw, no penalty, no alert | `UndergroundTests` (Sim) |
| Flats past the septic tanks: raw sewage, a penalty, the alert and the notice; a month on the water by the flats is fouled and homes there show "Raw sewage"; a plant beside the pipe treats it all and the water clears; dig up the pipe by the plant and it's raw again | `UndergroundTests` |
| The sewers' reach and totals are saved, and a loaded city plays on the same | `UndergroundTests` |
| On the hill gate's town, the gate's way of finding a plant site works; every pipe cell is laid out in a run, the tunnel too, and the pump and plant are picked out | `UndergroundClientTests` (ClientModel) |
| The Godot hill run puts a sewage plant beside the town's pipes, checks it treats the town's sewage, opens the underground view and checks what it drew, then looks at it from the classic and free cameras | `Main.Hills` ("sewage plant beside the pipes", "sewers to the plant", "underground view") |

## Results

CI run 41455572007 on the PR's head was green on every job.

- Tests: 245 in the sim (3 new) and the client model's (1 new), all passing; the region gate and the Godot gate
  (Alpha 3 loop and road weather on the benchmark city, now with its sewage plants) as before.
- The Godot hill run:

  ```
  HILLS ok  sewage plant beside the pipes: at 91,19
  HILLS ok  sewers to the plant: Water mains blue, sewers brown beside them, road tunnels grey. No sewage yet.
  HILLS ok  underground view: 119 lengths of water main and 119 of sewer, 15 of road tunnel, 2 pumps and plants; buildings hidden
  ```

  The hill town is newly founded, so it makes no sewage yet when the plant opens; the client model's test runs it
  two months on and sees it all treated.
- Frame times on the CI runner's software OpenGL, the same as the hill run's other views:

  | View | Avg ms | Triangles | Draw calls |
  | --- | --- | --- | --- |
  | classic, the town on its hillside | 133.2 | 71,303 | 173 |
  | classic, underground | 133.7 | 43,283 | 164 |
  | free, the tunnel and pipes underground | 133.3 | 65,077 | 193 |

- Screenshots: [`docs/screenshots/beta3-2-underground`](screenshots/beta3-2-underground), the Godot ones cut from
  the hill run's contact sheet and the web ones taken with Playwright on the same town.

## What's next

Subways (plan 5.3): a path type drawn in the underground view, with stations on the surface, run by the bus company.
Then lighting, then growing regions.
