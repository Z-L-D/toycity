# Beta 4, part 5 gate: the GPU test

Part 5 of the Beta 4 plan, which Zac accepted on 2026-11-02 ("Lighting cost on a real GPU: accepted, produce a
test"). The lighting PR could only measure on the CI runner's software renderer, where every frame takes about the
same time whatever is drawn, so it couldn't say what contact shadows, bounce light or full lighting cost. This part
gives Zac a test to run on his own graphics card. The presets get tuned from his numbers (plan item 5.3) once he has
run it.

## What changed

- **`--gpu-test DIR`** in the Godot client:
  - It founds the hill town (seed 5, 128 cells, starter town), the same town as the hills gate. It lets the town
    grow for up to a minute so there are people in the windows and traffic in the streets.
  - It then holds three views, each for four seconds after a settling pause:
    - the classic camera over the town
    - the free camera looking at the town from the hills
    - a street close up
  - It does this under eight setups:
    - each of the four presets (Low, Medium, High, Ultra)
    - High with each other lighting level (sun only, bounce light, full)
    - High at night
  - It records, per view:
    - every frame's time, giving the average frame rate and the **1% lows** (the frame rate over the slowest 1% of
      frames)
    - the **GPU time** a frame, as the driver reports it
    - the CPU time spent preparing the frame
    - draw calls and triangles
  - V-sync and the frame cap are off while it measures. The test sets each setup itself and saves none of them, so
    the player's own graphics options are unchanged.
  - **The report:** `gpu-test-<date>.md` and `.json` in DIR. It names the graphics card, renderer, processor, system
    and window size, gives a table per setup and a table per view, and says whether each setup holds 60 fps, holds
    30 fps, or neither.
  - **On screen:** a summary panel with the table, the report's path, "Open the folder" and "Close the game".
  - **Options:**
    - `--gpu-hold SECONDS` changes the hold.
    - `--gpu-quit` closes without the panel.
    - `--shots DIR` saves a picture of the panel.
- **Launchers** in `tools/gpu-test`:
  - **Windows:** double-click `gpu-test.cmd`, which runs `gpu-test.ps1`.
  - **Linux and macOS:** run `gpu-test.sh`.
  - Each one builds the server and the client, starts a server of its own on port 5189 with an empty temporary save
    folder, runs the test against it, and stops the server. The test founds a city, so it never runs against the
    player's own server.
  - They find Godot from `GODOT`, or from `godot` or `godot4` on the path. The report goes to `gpu-test/` at the top
    of the repository, which git ignores.
- **The reckoning** lives in the shared client model (`GpuReport`), so the numbers are tested without Godot.
- The mountain and hill gate runs now also turn v-sync off while they measure, as the benchmark and gate runs
  already did.

## Checks

| Check | Where |
| --- | --- |
| The 1% lows are the slowest 1% of frames; the verdicts' thresholds (60 fps with lows over 45, 30 fps with lows over 22) | `GpuReportTests` (ClientModel) |
| A setup's summary is the average over its views and the worst view's lows; the Markdown and JSON carry the machine, each setup and each view, with "n/a" where the driver reports no GPU time | `GpuReportTests` |
| The Linux launcher builds everything, starts its own server, runs the whole test through to its report and stops the server | CI step "GPU test, through its Linux launcher" (each view held one second) |

## Results

- **Locally:** the launcher ran the test end to end on the compatibility renderer under xvfb.
  - It wrote both reports and the summary.
  - The town reached 444 people in its minute of growth.
  - Every setup ran at 7 to 16 fps, as the software renderer runs everything. The GPU times it reports there are not
    meaningful.
  - The compatibility renderer doesn't draw contact shadows, bounce light or full lighting, and the report says so.
    A real card on the Forward+ renderer is what this test is for.
- **CI:** run 41552928265 on the PR's first head was green on every job. Its launcher step printed the following
  (software OpenGL, so these numbers are no guide to a real card):

  ```
  GPU Low: 16.2 fps, 1% low 15.0, GPU 57.74 ms, under 30 fps
  GPU Medium: 11.4 fps, 1% low 10.1, GPU 83.79 ms, under 30 fps
  GPU High: 7.5 fps, 1% low 6.8, GPU 332.48 ms, under 30 fps
  GPU Ultra: 7.5 fps, 1% low 7.1, GPU 385.30 ms, under 30 fps
  GPU High, sun only: 7.5 fps, 1% low 6.8, GPU 294.44 ms, under 30 fps
  GPU High, bounce light: 7.5 fps, 1% low 6.8, GPU 328.08 ms, under 30 fps
  GPU High, full: 7.6 fps, 1% low 7.0, GPU 327.27 ms, under 30 fps
  GPU High at night: 7.5 fps, 1% low 6.8, GPU 350.93 ms, under 30 fps
  GPU OK
  ```
- **Screenshot:** [`docs/screenshots/beta4-gpu`](screenshots/beta4-gpu), the summary panel at the end of a local run.

## What's next

- Zac runs the test on his card and sends the report. The presets get tuned so Medium and High hold their targets
  (plan item 5.3).
- Next is part 4, snow on the ground and roofs.
