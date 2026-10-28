# Beta 1 gate run

The Beta 1 gate: play the Alpha 3 loop end to end in the Godot client on the benchmark city, with a frame-time
and memory report against the budget.

## How it runs

`godot --path clients/godot -- --gate gate.json` (add `--shots DIR` for screenshots) against a running server.
CI runs it on every push after the smoke test, the screenshots and the dense-city benchmark.

1. **Found the benchmark city** from the New city dialog (Benchmark city ticked): 256 × 256, roads every fifth
   row and column over the dry land, every block zoned (densest in the middle) and built up at once, with
   plants, pumps and landfills scattered through it. About 7,000 buildings (`Sim/BenchmarkCity`).
2. **Measure the cameras**: the classic view at each zoom level from the middle of the map, then the free
   camera 160, 60 and 18 cells off (the last with smoothed buildings).
3. **Play the loop** through the client's own tools and panels (`ClientModel/Gate/GateRun`), with every frame
   timed under what was on screen:
   - three bus stops along a street with the bus-stop tool, a block cleared with the bulldozer and a depot put on it;
   - the company's view: take the company, lease the stops and the depot, draw a line through the stops, buy four buses;
   - the city seat to the computer on stasis (funds and population hold), caretaker and autopilot (each bills
     the leases at the month's turn), watched from the classic view with the routes shown and from the
     ride-along camera on one of the buses;
   - city to company: commuters ride the line and fares come in; company to city: drivers at the depot and
     riders through the city's stops;
   - take the city back and get the "while you were away" report covering the whole time away.

Any check that fails, or any step that times out, fails the run (exit code 1). `GateRunTests` plays the same
loop against the server with the planner standing in for the Godot tools.

## Results

Filled in from CI below.
