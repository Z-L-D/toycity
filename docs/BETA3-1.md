# Beta 3.1: the ground, as it really is

Beta 3.1 moves the terrain to SimCity 4's model, takes the look toward realism, and builds out what Beta 3 left
smaller than planned. It is built part by part. Each part's rules are written down here as they're built.

1. The ground as corners, in metres
2. Mountains and valleys from the generator
3. The Godot terrain renderer
4. Realistic roads
5. Rivers and floods
6. Bus lines into neighbouring cities, and computer mayors who build roads to their neighbours
7. The HTML client catches up
8. Gate

## Part 1: the ground as corners, in metres

- **Corners, not cells.** The ground is a height at every corner of the grid, in decimetres: (width + 1) by
  (height + 1) of them. That's what SimCity 4 kept, and it lets a cell slope any way at all. Neighbouring region
  tiles share their edge corners, so the ground meets exactly at a border.
- **Steps are derived.** The rules that count steps keep working as they did. These are what's water, how steep a
  road may climb (one step a cell), and how uneven a lot may be. A cell's step is its four corners averaged, in
  steps of 2 m. A cell is 16 m across, so one step a cell is a 12.5% grade.
- **Terraform works on corners.**
  - Raise and Lower move every corner under the brush by a step.
  - Level brings them to the first cell's height.
  - Smooth averages each corner with the eight around it.
  - Corners under a building or a path stay put.
  - The ground just outside the brush shares its edge corners, so it slopes up to meet the brush rather than leaving a wall.
- **Roads, pads and the computer's levelling** set the cells' steps they need exactly. The ground then follows:
  every corner they touch goes to the average of the cells around it. So a road climbing a step a cell becomes a
  smooth ramp, a pad is flat, and the land beside slopes to meet them.
- **Saves.** The save format is 8 and carries the corners. An older save loads with each corner the average of the
  cells around it, so its terraces become slopes.
- **Protocol.** The full state carries `corners`, two bytes per corner. A delta lists the corners that moved. A
  client talking to an older server makes corners from the cells.

### Tests

`CornerTests` covers four things:
- Each step is its corners averaged.
- Neighbouring tiles share their edge corners.
- Raising moves corners and the ground beside it slopes.
- Corners survive save and load.

The grading tests now expect the cliff's face to be a sloped cell, not a wall.

## Part 2: mountains and valleys from the generator

- **Lowland** is five octaves of gradient noise. It's sampled through a slow warp, so coasts and ranges bend instead of
  running straight.
- **Mountains** rise where a slow mask, about two cities across, climbs past its middle. There, ridged noise makes
  sharp crests and long spurs, up to 400 m or so. Some maps get a range and some are all lowland and lakes.
- **Rain wears the ground.** Seeded droplets, about one for every two corners, run downhill. They cut where they speed
  up and drop silt where they slow or pool, so valleys deepen and fans spread at their feet.
- **Water.** The lowest 18% of the map is under water, with a bed shelving to 10 m deep, and the shore starts just
  above the waterline.
- **Height range.** The highest step is now 250 (500 m).
- **Regions** generate one map for all their tiles, so ranges cross borders.
- **Automation.** The computer counts a pad's earthworks against its monthly cap before it places a building.
  Sloping ground made that cost show up.

### Tests

`GeneratorTests` checks three things:
- Some seeds have mountains and some don't.
- The same seed always makes the same ground.
- About a fifth of the map is water.

Seven tests that leaned on a particular seed's old map now use other seeds.
