# River shorelines gate (Beta 4 follow-up)

Zac, 2026-11-03: "the rivers probably need another turn or two. we have made rivers in the prairie kiosk
project. attempt to excavate that concept and see what we can do to improve the shoreline."

## What came from the prairie kiosk

The Flint Hills creek there is drawn quite differently from a grid of cells:

- It follows a smoothed centre line with a half-width and a bed depth at each point.
- Its water is a ribbon that lies over the banks, so the shore is wherever the bank's slope meets the water.
- The ground beside it is coloured in bands: a wet band of mud and cobble at the waterline, then floodplain.

## What changed

- **Shaped channels (sim, `Rivers.Channels`).**
  - Each river follows a line smoothed over three cells up and three down its main stem.
  - Its half-width grows with the water feeding it, from ¾ of a cell up to 2 cells.
  - The channel is cut with a flat bed across the middle half, sides that rise to just under the water, and a short
    cut bank. The bank above then rises a step for every 1¼ cells, where it used to stand as a one-cell
    staircase.
  - The water surface runs smoothly along each stretch, including down rapids.
  - Ground the channel cuts under the river's water joins the river and flows into the nearest cell of the main
    stem downstream, so floods, water quality and growing-region exits follow it.
- **Old regions keep their rivers.** A region regenerates its ground from its seed, so `Region.Channels` is saved
  (RegionSave field 18). Regions saved before this change load with it off. A fixed region's ground with channels
  off hashes exactly as before.
- **Bridges** sit half a step over the water, where they used to sit a whole step over. This is done the same way
  in the sim's grading, the client planner, the web planner and border building. The lower deck still meets a
  road along the bank at an even grade.
- **Godot.**
  - The river sheet runs one cell past the river at the river's level, so the terrain clips it into a contour
    shoreline.
  - The terrain shader colours the banks from a new `river_banks` texture: wet mud and gravel at the waterline,
    silt and a riverbed under the water, and lusher floodplain grass up to about 7 m over the water.
- **Web.**
  - The river's level is blended between cell centres, and the shore is drawn where the ground meets it.
  - The banks get the same wet band and floodplain colours as in Godot.
  - River water is drawn at least a step deep, so it reads as water, not mud.

## Checks

- **Sim tests: 284 pass.** They include the new `RiverChannelTests`:
  - Channels make rivers wider than the old cut on every seed tried, with no steeper banks for each cell of river.
  - No river climbs.
  - A region saves and loads its channel flag, and an older region keeps its old rivers.
- **Client model tests: 114 pass.** The road preview's price matches the server's again with the lower bridge
  deck.
- **CI is green on cc6a0cc.** The run caught two earlier test races that had nothing to do with rivers, and both are
  fixed:
  - The merge test now builds its roads on dry land.
  - The franchise test now waits for the mayor's client.
- **Shots:**
  - Web: Playwright against a local server, seed 42.
  - Godot: the CI mountain gate's contact sheet (software OpenGL).

## Trade-offs to watch

- Rivers are wider, 2 to 4 cells on the plains, so they take more of a city's land.
- Wide rivers on flat floodplains can spread into broad, lake-like reaches.
