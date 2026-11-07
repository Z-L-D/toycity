# Release candidate, part 4: the path designer

Streetmix's cross-sections as the city's path types. You lay a path out left to right as strips, the city works out
what it makes of it, and it goes in the build menu to build like any other.

- **Twelve kinds of strip:** sidewalk, trees, bike lane, shared path, parking lane, drive lane, turn lane, lane with
  tram tracks, tram track, median, shoulder and shared lane (a country lane). Each has its own widths, 2 to 40 m
  across in all, up to 16 strips.
- **The numbers follow from the strips,** with rules fitted to the built-in types, so a design laid out like the
  built-in road comes out close to it.
- **Designs are the city's own:** in the build menu under Your designs, saved with the city, and redesigned in place.

## What changed

- **The rules (`PathRules`, compiled into both the simulation and the client model):**
  - **Who uses it:**
    - cars with a drive, turn, tram or shared lane;
    - people with a sidewalk or a shared path;
    - bikes with a bike lane or shared path, or in the traffic of a town road up to 50 km/h.
  - **What kind it is:** cars and shoulders with no sidewalk make a highway: no frontage, 90, 100 or 110 km/h by its
    lanes.
  - **Town road speeds:**
    - 30 with lanes under 3.25 m, or trams in the traffic;
    - 60 for a stroad: three lanes or more with nothing to slow it (no parking, bikes, trees or median);
    - 40 split by a median with a lane each way;
    - 50 otherwise.
  - **Capacity:**
    - roads: 1,200 an hour a highway lane, 600 a town lane (300 if narrow, 250 with trams in it), 15% more one way,
      and 300 more with a turn lane;
    - paths: 400 people an hour a metre of sidewalk, and 750 bikes a bike lane.
  - **Cost, noise and the rest:**
    - cost by the metre of each strip, upkeep a hundredth of it a month;
    - noise rises with speed and lanes, and falls with trees and a median;
    - the steepest it climbs, whether a footpath turns to stairs, what it's paved with, its winter tier and
      drainage, its colour.
  - **What it won't build:** a turn lane needs drive lanes, parking needs traffic, only a road with drive lanes is
    one way, and a shared lane doesn't go with drive lanes. Each refusal says why.
- **The simulation (`PathDesigns.cs`):**
  - **Its own path types:** each city now has its own copy of the content's path types, with its designs on the end.
    Everything that reads a path type reads a design the same way.
  - **`DesignPath`:**
    - With no id it makes a new design, `design-1` and on. It's free.
    - With an id it redesigns one: every cell built with it becomes the new design, and the difference in cost is
      charged if it's dearer.
    - Built-in types can't be redesigned. A city keeps up to 24 designs.
  - **`DeletePathDesign`:** refused while any cell is built with it. The designs after it move up the list, and
    their cells with them.
  - **Saving (format 22):**
    - the designs are saved by their strips, and their numbers are worked out again on loading;
    - the built-in count is saved too, so a design's cells move if built-in types are added later.
  - **Merging cities:** a city absorbing another takes its designs, under new ids where the ids are taken.
  - **Who can do it:** both actions are in the Planning portfolio.
- **The protocol:**
  - two new actions;
  - path types carry their strips, whether they're designed, and their capacity, noise, upkeep and exact width;
  - a tick sends the whole list of path types again when it changes.
- **The client model:**
  - **`PathDesigner`:** the designer, the same for any client:
    - strips to pick, add after the one picked, take out, move left or right, and widen a quarter metre at a time
      within their kind's widths;
    - start from a plain street or from any path type but rail;
    - one way;
    - the city's numbers as it changes, or why it can't be built;
    - Save makes the action.
  - **`CityMirror`:** takes the new list of path types and redraws every road.
  - **`BuildMenu`:** Transport has a Your designs group, the city's designs and **Design a path…**, which opens the
    designer without changing the tool.
  - **`RoadLook`:** draws a design from its strips:
    - the carriageway kerb to kerb, squeezed to fit a cell as the built-in roads are;
    - its lanes, and its median or tram reservation;
    - sidewalks if it has them;
    - bike lanes. Built-in roads with bike lanes in their cross-section paint them too.
- **The Godot client:**
  - **The designer panel,** from Your designs:
    - the cross-section as you'd see it standing in the road, each strip as wide as it is, with a person, a tree, a
      bike, cars with tail lights going away and headlights coming, or a tram on it;
    - click a strip to pick it, then ◀ ▶ − + and Take out;
    - a palette of the twelve kinds;
    - the name, One way, and what the city makes of it, or why not, in red;
    - the city's designs to redesign or delete, a plain street, or any path type to start from.
  - **Bike lanes** are painted green with a white line inside, on road cells and on curved roads alike.
- **The web client:**
  - takes the new list of path types when it changes;
  - shows the city's designs in the build menu under Your designs, to build like any other.

## Checks

- **Sim `PathDesignTests`, 9 new tests:**
  - Every built-in type's strips pass and come out close to its own numbers: who uses it, frontage, one way and tram
    exactly; speed within 40%; capacity and cost within their ranges. Speeds match on all 17.
  - The rules:
    - a stroad is 60 and trees bring it to 50;
    - narrow lanes and trams are 30;
    - a four-lane highway is 100 km/h, 4,800 an hour, with no frontage;
    - one way and a turn lane carry more;
    - a 3 m sidewalk carries 1,200 people;
    - trees are quieter and dearer;
    - the boulevard is a boulevard at 40.
  - Every refusal says why.
  - A design is built like any other path type, only in its own city, at its own cost.
  - Redesigning charges 20 cells' difference. Built-in types can't be redesigned or deleted, nor a design changed
    to one that won't build.
  - A design in use can't be deleted. Deleting another moves the rest up the list, with their cells.
  - Designs survive a save and load with the same state hash, both copies carry on the same, and the log replays to
    the same city.
  - A city absorbing another takes its design under a new id, with its 21 cells.
  - At most 24 designs.
- **ClientModel `PathDesignerTests`, 3 new tests:**
  - Laying out a street, adding trees and a median, widening, moving, taking out, refusals, one way and the 16-strip
    limit.
  - Starting from the built-in boulevard gives its speed and capacity, and Save makes the action.
  - A design reaches the client, is in the build menu only under Your designs, and is drawn with sidewalks, two lanes
    and two bike lanes. The designer's tile doesn't change the tool. A design built, bulldozed and deleted leaves the
    list as it was.
- **Godot `--shots`:**
  - The run makes a tree-lined boulevard (sidewalks, trees, bike lanes, two lanes and a median) at the start.
  - The road tool's shot lays it as the curve and as a straight run below it.
  - Then it shoots the designer redesigning it, and the run up close.
  - New log line: `PATH DESIGNER OK design-1 'Tree-lined boulevard' walk 2.5 | planter 1.5 | bike 1.8 | car 3.5 | median 2 | car 3.5 | bike 1.8 | planter 1.5 | walk 2.5 = boulevard, 40 km/h, 1200 an hour, built on 40 cells`.
  - CI checks for the line and posts the two new shots at full size.
- Shots are in [`docs/screenshots/rc4-paths`](screenshots/rc4-paths).

## Known limits

- **Only the Godot client has the designer panel.** The web client builds the city's designs but can't make them.
- **Street trees and parked cars aren't drawn in the city yet.** A design's trees count toward its noise and cost,
  but in the city its planters are drawn as sidewalk.
- **A wide design is drawn squeezed into its cell.** It's drawn at most 13 m kerb to kerb (15 m for a highway), as
  the built-in roads are. Its numbers come from its full width.
- **Lanes run half each way.** Lanes can't each be given a direction. One way is for the whole road, and turn lanes
  aren't drawn apart from the rest.
- **Rail can't be designed.**
- **Designs belong to one city.** They aren't shared across the region. A city absorbing another is the one way a
  design moves between cities.
