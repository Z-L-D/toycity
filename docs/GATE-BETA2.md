# Beta 2 gate run

The Beta 2 gate: four players on one dedicated server, playing a live session through the client model, with a
scripted de-link at the end.

## How it runs

`godot --path clients/godot -- --together together.json --server ws://HOST/ws --user "Mayor May" --password secret1
--new-account` (add `--shots DIR` for pictures) against a fresh dedicated server, whose first account becomes the
mayor. CI starts its own dedicated server and runs it after the Beta 1 gate.

The window is Mayor May. Tess, Dan and Sam are client models in the same process, each signed in over its own
connection and driven only through its own panels, as a player's clicks would drive them (`ClientModel/Gate/TogetherRun`):

1. **Everyone in.** May signs in first and is the mayor. The town runs while the other three make accounts and
   join, to day 120.
2. **Seats.** Tess claims the bus company and Dan the emergency services from the seat dialog. Sam claims nothing
   and shows as watching.
3. **Building.** May puts three bus stops along a street with the bus-stop tool, then a bus depot, a fire station
   and a police station off the east road, each where the planner first allows it.
4. **Stations leased.** From each station's panel May leases it to the emergency services on the standard
   public-service terms, and the stations come with their units.
5. **Dispatch.** Dan picks Scene command and turns on Pick responders. From then on Dan's hands confirm the
   computer's picks for each call, run each scene that opens for 6 seconds, and then hand it to the crews.
6. **The company.** In her company view Tess leases the stops and the depot, draws a line through the stops and
   buys four buses.
7. **Live.** The city runs until Dan has sent units to a call, has run a scene through to its report card, and
   commuters ride Tess's line.
8. **Watching.** Sam sees the line on his map but not the company's books. When he tries to set a tax he is
   refused, and only he hears about it.
9. **Negotiating.** May offers Tess $100 off the depot's rent from the lease ladder, and Tess accepts.
10. **The same city.** With the clock stopped, all four clients agree on the day, funds, population and buildings.
11. **De-link.** Tess leaves with a copy from her seat dialog. Her copy arrives. In the shared city the company goes
    back to the computer on caretaker and its leases end, while Dan keeps his seat. Sam sees her go, and the
    mayor's seat dialog lists it. When Tess tries to take the company back, she is refused for the cooldown. All
    four still see the same city.

Any check that fails, or any step that times out, fails the run (exit code 1). `TogetherRunTests` plays the same
session against a dedicated server with the planner standing in for the mayor's Godot tools, then loads Tess's
copy: she is its mayor and runs its bus company there.

## Results (CI)

RESULTS
