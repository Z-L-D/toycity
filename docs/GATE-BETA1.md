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

## Results (CI, 2026-10-28)

Every check passed on the CI runner, in 91 seconds:

| Check | Seen |
| --- | --- |
| Benchmark city founded | 256 × 256, 6,948 buildings |
| Three bus stops, a block bulldozed, a bus depot | all placed with the tools |
| Company taken, stops and depot leased, line drawn | "Gate Line" through the three stops |
| Line running | 4 buses every 8 min, a round trip 34 min |
| Stasis holds funds and population | $996,970 → $996,970, population 675,348 → 675,348 |
| Caretaker bills the leases | $8,673 to the city, and the company paid it |
| Autopilot bills the leases | $9,327 |
| Commuters ride the line | 3,552 riders a day, 296% of seats at the peak |
| Fares come in | $94,404 in the last full month |
| Drivers at the depot | 10 |
| Riders through the city's stops | up to 7,336 a day at the three stops |
| Away report covers the time away | days 4 to 64 |

### Frame times and memory

The runner has no GPU and renders in software at about 7.5 fps whatever the scene, so its frame times say
nothing about the target (60 fps classic at full zoom-out, 30 fps free camera, under 2 GB). What carries over
to real hardware is the scene's cost and the memory:

| What was on screen | Triangles | Draw calls | Process MB |
| --- | --- | --- | --- |
| classic, zoom 0 (full zoom-out) | 1,378,975 | 1,930 | 655 |
| classic, zoom 1 | 532,283 | 920 | 645 |
| classic, zoom 2 | 281,569 | 445 | 646 |
| classic, zoom 3 | 4,313,769 | 294 | 662 |
| classic, zoom 4 | 3,735,275 | 233 | 668 |
| free, 160 cells off | 1,380,499 | 1,884 | 708 |
| free, 60 cells off | 8,352,467 | 920 | 717 |
| free, 18 cells off (smoothed nearby) | 17,136,879 | 473 | 708 |
| loop: placing stops (classic, zoom 3) | 4,387,469 | 300 | 726 |
| loop: company view | 5,930,013 | 471 | 767 |
| loop: stasis (classic, zoom 2, routes shown) | 276,451 | 451 | 767 |
| loop: caretaker | 423,233 | 691 | 793 |
| loop: autopilot (riding a bus) | 16,928,923 | 608 | 862 |

Against the budget:

- **Memory: within budget.** The process peaked at 862 MB, under half the 2 GB allowed, on a city with
  more than four times the area of the 128 × 128 target and about 7,000 buildings.
- **Classic view at full zoom-out: likely within budget.** 1.4 million triangles and about 1,900 draw calls
  on a 256 × 256 city; the target is a 128 × 128 city, about a quarter of that. The draw calls are the cost to
  watch: one batch per building type per region, so a city with every type everywhere is the worst case.
- **Free camera: at risk.** Up close it draws 17 million triangles, nearly all of them the smoothed buildings
  around the focus (smoothing triples a model's triangles and the near models are already full detail), and
  60 cells off it is 8 million. That is more than integrated graphics can draw at 30 fps. The fix is a
  smaller smoothed ring, smoothed meshes simplified before upload, and the far models from a shorter
  distance in the free camera; it needs measuring on real hardware first.
- **Real frame times** need `godot --path clients/godot -- --gate gate.json` (with a server running) or
  `--bench bench.json` on the target laptop.

The run's own JSON is in the CI artifact `godot-views` as `gate.json`, with each frame's phase, average,
95th percentile and worst frame time.

## Real hardware (2026-10-29)

Run by hand on two laptops of about the same age, with Vulkan (Forward+), before the graphics options existed
(so at what is now the High preset, without MSAA).

**Quadro P4200 with Max-Q Design** (discrete), `--bench`, 128 × 128 dense city: 207 to 290 fps at every zoom
level, 95% of frames under 10.3 ms, worst frames 23 to 62 ms; 914 MB.

**Intel HD Graphics 620** (integrated, the target class), "playable but not smooth":

| What was on screen | Avg fps | p95 ms | Triangles |
| --- | --- | --- | --- |
| bench, classic zoom 0 (128 × 128) | 39.5 | 27.6 | 1,146,061 |
| bench, classic zoom 3 | 26.3 | 39.3 | 2,872,945 |
| gate, classic zoom 0 (256 × 256) | 27.1 | 38.1 | 1,390,251 |
| gate, classic zoom 2 | 55.8 | 18.1 | 309,719 |
| gate, classic zoom 3 | 20.5 | 54.8 | 4,576,143 |
| gate, free camera 160 cells off | 29.0 | 34.7 | 1,373,739 |
| gate, free camera 60 cells off | 10.2 | 109.9 | 10,571,781 |
| gate, free camera 18 cells off (smoothed) | 9.0 | 117.7 | 19,060,701 |
| gate, riding a bus | 9.8 | 114.2 | 16,558,213 |

The gate passed every check there too, with memory peaking at 795 MB. Frame time follows the triangle count:
the full-detail models when zoomed in, and the smoothed ones in the free camera. That led to the graphics options
above. Medium (picked on integrated graphics) draws the half-resolution models up close, keeps the far models past
80 cells in the free camera, has no smoothing and uses smaller hard shadows. Low also drops shadows and draws at 75%
resolution. To measure a preset: `--bench bench.json --graphics Low` or `--gate gate.json --graphics Medium`.
