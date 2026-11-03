# Beta 4, part 2 gate: the UI overhaul (variation A)

Part 2 of the Beta 4 plan. On 2026-11-03 Zac picked variation A of the three HTML mockups as the working concept
("strong prefer on UI A ... there will likely be further adjustments to A in the future"). Variation C may come back
for the side games, and B was turned down as too much like Cities: Skylines. Before this part, the mayor's view was
docked like a web page: a bar across the top, a column of tool buttons and drop-downs down the left, and a panel down
the right, so the map got what was left.

## What changed

Both clients now show the mayor's view the same way, from one shared description of the build menu in the client
model (`BuildMenu`, ported line for line to the web client's `buildmenu.ts`) and one shared icon set
(`content/ui-icons.json`).

- **The map fills the window.** Everything else floats over it as dark glass panels, with a blue accent and prices
  in gold.
- **The build rail** runs down the left. Query sits at the top, then one button per category: zones, transport,
  utilities, civic, parks, landmarks, land, disasters and bulldoze. Each button shows its key.
- **Flyouts.** A rail button opens its category's flyout beside the rail. It has a title, a line on what the
  category is for, tabs for its groups (Transport has paths, roads, highways, rail and tram, stops, and depots), and
  a tile for each thing to build, with its icon and price.
  - Picking a tile sets the tool and closes the flyout. The pin keeps it open for picking again.
  - Locked things show why ("Reach 2,000 people", or "Built" for a one-of-a-kind building) and can't be picked.
  - Everything the mayor can build is in the menu exactly once. Buildings are sorted by price.
- **The tool in hand** is named along the top with its price, followed by its options:
  - for paths, the surface, cut through or tunnel, and straight, diagonal or curve;
  - for zones, the density, area or block, and leftovers;
  - for terraforming, the mode;
  - for disasters, which one, and how often they happen by themselves.

  The old drop-downs for path type and building are gone: the flyout's tiles replace them.
- **The modes pill**, top right, holds the city's name, then Mayor, Transit, Dispatch and Region. A menu button
  opens who runs the city, the council (web), new city, save, load and graphics (Godot).
- **The L**, bottom left:
  - **The upright** holds the minimap with the view marked on it (click or drag to look elsewhere), and buttons
    for layers, the underground view, commuters, why, inspect, demand, forecast, people, laws and the guide.
  - **The bar** along the bottom holds the date, clock and weather; pause and 1×, 2× and 3×; the calendar's pace;
    funds with last month's change; people; approval; the RCI bars; the news; alerts with a count; and the budget.
- **The panel floats and closes.** It comes up on the right when a tab is asked for (an L button, a query, an alert,
  the approval) and closes with its ✕, its L button again, Escape (web) or P (Godot).
- **The side games' screens** (the bus company and dispatch) keep their own docked layout, as before.

## Checks

| Check | Where |
| --- | --- |
| Everything the mayor builds is in the menu once (every placeable building, every path type, nine zones, three utilities), with buildings at their cost and paths per cell; highways sit apart from roads; a tile sets the tool, a pin keeps the flyout open, a locked thing can't be picked | `BuildMenuTests` (ClientModel) |
| Every icon the menu uses is in the shared set, and it comes out as an SVG in a colour | `UiIconTests` (ClientModel) |
| In Godot, on the hill town: the rail holds nine categories covering every building and path type once; Transport's roads tab shows a tile per road; picking Road sets the path tool, closes the flyout and shows the road's options; the civic flyout pinned and the budget panel open sit inside the screen, clear of the L | CI step "Hills" (`Main.Hills.cs`) |
| In the web client, against a local server: each flyout, picking and drawing a road with the cost chip, the panel, and the dispatch screen still docked | Playwright |

## Results

- **Tests:** 271 in the sim pass. 109 in the client model pass, 2 of them new.
- **Locally, the hill gate passed every check**, including its new ones:

  ```
  HILLS ok  build menu: 9 categories on the rail, 81 things to build: Zones 10, Transport 30, Utilities 11, Civic 6, Parks 4, Landmarks 10, Land 5, Disasters 4, Bulldoze 1
  HILLS ok  flyout open: Transport, Roads: 9 tiles of 9
  HILLS ok  a tile picks the tool: tool Path road; flyout closed; options shown
  HILLS ok  panel floats over the map: panel 1208,64 to 1588,816; bar 832 to 888; upright 12,552; screen 1600×900
  HILLS | classic, the roads flyout | 38 | 133.2 | 143.3 | 148.1 | 7.5 | 177,677 | 349 |
  HILLS | classic, the civic flyout and the budget | 38 | 133.2 | 146.6 | 149.4 | 7.5 | 177,125 | 460 |
  ```

  The glass panels cost about 150 to 250 more draw calls on software rendering. The frame time didn't change.
- **The web client**, against a local server, on a starter town grown to about 400 people:
  - each flyout opened;
  - Street was picked from the roads tab, and the flyout closed with the street's options along the top;
  - a street was drawn with the cost chip at the cursor;
  - the civic flyout was pinned with the budget open;
  - the dispatch screen kept its docked layout, and its City button brought the L back.
- **Screenshots** are in [`docs/screenshots/beta4-ui`](screenshots/beta4-ui).

## Known limits

- The flyout's tiles use the category's icon (a road, a police shield), not a picture of each building.
- Hover tooltips are the engine's own in Godot. The mockup's richer cards (upkeep, coverage) aren't drawn yet.
- Zac expects to adjust A further. Its parts are all built from the shared `BuildMenu` and icons, so changes to what's
  in the menu land in both clients at once.

## What's next

Part 6: room to grow (region sizes, annexing as squares only, and the region charter).
