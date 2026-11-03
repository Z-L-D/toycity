# Beta 4, part 1 gate: direct dispatch

Part 1 of the Beta 4 plan. Zac accepted it on 2026-11-02 ("Direct dispatch (Emergency / SWAT 2 style)"; units can be
moving colour-coded props, and animated people aren't needed). On 2026-11-03 he chose to keep auto-dispatch on by
default, with direct orders layered on top of it. Before this part, a unit was only ever at its station, on its way
to a call (unseen), or at a scene. The player could pick who went to each call from the board, but couldn't move a
unit about the city.

## What changed

- **Every unit is somewhere on the streets.** It starts at its station's door.
  - It drives each leg by the city's fastest route, leaving at a minute of the day clock and taking the route's
    minutes.
  - Standing still, it's where it stopped. A unit back at its station is "home".
  - Where each unit is, and its leg, waypoints and order, are saved with the service. The sim's own save format
    doesn't change: the service saves its own state.
- **Answering calls from where they are.** The computer now picks the nearest units to a call from where they stand,
  not from their stations. The call's response time is counted from there too: one minute to get going on the street
  instead of two at the station. A unit sent to a call drives to the street by it. When the call is over, it drives
  back to its station.
- **Direct orders** (a new `UnitOrder` command), each for one unit or several:
  - **go**: drive to the street nearest a place and wait there, "posted". A posted unit stays ready, so auto-dispatch
    can still send it from there, which is the point of posting it. With **queue**, the place is added after where
    it's going.
  - **scene**: answer a call, as the board's Send does.
  - **hold**: stop where it is. The computer leaves a held unit alone, but the player can still send it.
  - **return**: drive back to its station.
  - **follow**: keep within two cells of another unit.

  A unit busy with a call can't take an order. The clients leave it out and say so, and the service refuses an order
  for one.
- **Both clients draw the units and take orders on the city map.** It's the same in both, from the shared
  `DispatchMap` in the client model (ported line for line to `dispatch.ts`):
  - Each unit is a little vehicle in its service's colours: engines red, patrol cars blue, ambulances white with a
    green stripe, SWAT dark. It faces the way it drives, and its light bar flashes while it moves.
  - Click a unit to pick it, shift-click for more, or drag a box. Ctrl and a number makes a group; the number picks
    it again.
  - Right-click a place to send the picked units, and shift-right-click to queue more places. Right-click a call to
    answer it, or another unit to follow it.
  - The service's left column has **Hold (H)**, **Go home (G)** and **Follow (F)**, a line naming who's picked and
    what each is doing, and a hint for what to do next.
  - The picked units are ringed. The rest of each one's route and its queued places are drawn on the streets.
  - Units standing on one spot fan out around it, so each can be seen and clicked.
- **Smooth driving.** At most speeds a city day comes round faster than its day clock would run (48 minutes of clock
  every few seconds). The top bar's clock waits and jumps, so units are eased by their own clock (`TickClock`). It
  runs across each day's step over as long as the last day took.
  - A leg ordered by command leaves at the next day's minute, so even a short drive is seen.
  - A unit's leg is timed by the minute the clients see once the day is done, not by the minute the day began.
- **Protocol** (emergency service): `Command.order` (`UnitOrder`), and on `UnitView` `cell`, `leg`, `leg_start`,
  `leg_minutes`, `order`, `waypoints`, `follow_id` and `home`.

## Checks

| Check | Where |
| --- | --- |
| Units start at their stations, drive to a call by road and come home after | `DirectDispatchTests` (Sim) |
| A posted unit drives a route, waits where it was sent, is seen driving after the next day, and is the one the computer sends to a call by it, in minutes | `DirectDispatchTests` |
| Held units are left alone by the computer but can be sent to a call by hand | `DirectDispatchTests` |
| Queued places are driven in turn; a follower keeps up; going home ends at the station with no order | `DirectDispatchTests` |
| Busy units, unknown orders, off-map places and unknown units are refused | `DirectDispatchTests` |
| Where units are, and their legs and orders, save and load to the same view and state hash, and run on the same | `DirectDispatchTests` |
| A unit eases along its leg by the clock and faces the way it drives; click, shift-click, box and groups pick units; right-clicks send, answer calls and follow; busy units are left out and named | `EmergencyClientTests` (ClientModel) |
| On a real server, a direct order goes over the wire and the service's own answer comes back | `EmergencyClientTests` |
| In Godot: stations by the hill town's streets are leased to the service the player runs, every unit is boxed and sent across town by a right-click, they drive by road and end up posted where they were sent | CI step "Hills" (`Main.Hills.cs`) |
| In the web client: the same, by mouse, with the routes drawn and Go home (G) after | Playwright, against a local server |

## Results

- **Tests:** 271 in the sim pass, 6 of them new. 107 in the client model pass, 2 of them new.
- **Locally, the hill gate passed every check**, including its new ones:

  ```
  HILLS ok  units at their stations: Engine 1 at 86,17, Engine 2 at 86,17, Ambulance 1 at 84,2, Patrol 1 at 84,22, Patrol 2 at 84,22
  HILLS ok  units drive by road: 5 of 5 on the road: Engine 1 39 cells in 29 min, Engine 2 39 cells in 29 min, Ambulance 1 22 cells in 16 min, Patrol 1 42 cells in 31 min, Patrol 2 42 cells in 31 min
  HILLS ok  units posted where they were sent: 3 posted near 63,2 and 2 sent on to calls: Engine 1 posted, Engine 2 posted, Ambulance 1 on call, Patrol 1 on call, Patrol 2 posted
  HILLS | classic, units driving out | 38 | 133.7 | 145.5 | 147.8 | 7.5 | 160,660 | 238 |
  HILLS | free, units posted on a street | 38 | 133.3 | 144.1 | 145.7 | 7.5 | 296,595 | 216 |
  ```

  Two of the five were sent on to the town's own calls from where they'd been posted, which is auto-dispatch running
  on top of direct orders, as Zac chose.
- **The web client**, against a local server: five stations placed in a starter town and leased, nine units. A box
  dragged over the map picked all nine, and a right-click sent them across town. They drove there along the streets
  with their routes drawn, and G sent them all home.
- **Fixed on the way:** a short drive was over before any client saw it. The city's clock moves a whole step (48
  minutes at the usual pace) each day, and a leg timed from the minute the day began ended inside that step. Legs are
  now timed by the minute the clients see, and the clients ease units by their own smooth clock.
- **Screenshots** are in [`docs/screenshots/beta4-dispatch`](screenshots/beta4-dispatch).

## Known limits

- Units drive along the middle of the street, through each other and through traffic. They don't keep to a lane yet.
- A unit's route is fixed when it sets out. It doesn't re-route around a road built or bulldozed under it, and it
  isn't slowed by snow or floods on the way.
- At a scene, the crews still come in from the edge facing their station, as before, rather than from where they
  were.
- Groups are kept by the client and are lost when it's closed.

## What's next

Part 2, the UI overhaul (variation A) in both clients.
