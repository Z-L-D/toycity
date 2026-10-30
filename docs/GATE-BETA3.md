# Beta 3 gate run

The Beta 3 gate: a three-tile region, each tile on its own server process, with two human mayors and a computer
mayor, runs an in-game decade without desync, and its border ledgers balance every day.

## How it runs

`dotnet test tests/ToyCity.ClientModel.Tests --filter "Category=Gate"` (`RegionGateTests`). CI runs it as its own
step after the other tests, with the detailed logger, and copies its `GATE` lines into the run summary.

1. **The region host.** A dedicated server with a link key and a 3 × 3 region. May signs in first, so she is the
   admin and Toyville's mayor.
2. **Founding.** Tess makes an account on the region host and founds Westbrook on the tile west of Toyville, with a
   starter town. May founds Eastfield on the tile east of Toyville and hands it to a growth-hungry computer mayor.
3. **Tile hosts.** Two more server processes start with `--tile-host`, one for Westbrook and one for Eastfield.
   The region map shows both addresses. Tess signs in to Westbrook's host with her region account and lands in her
   own city as its mayor.
4. **A road across the border.** With the clock held, each mayor plans a road from their town out to the shared
   border on the same row, over open land beyond everything built, and both draw it the way a player would. Then each
   zones homes beside it.
5. **A decade at full speed.** The region runs at speed 3 for 3,600 days. Every day the region host replays the
   tile hosts' days on check copies and compares the border ledgers: what one city sends across a border, its
   neighbour must receive. Every month it compares the state hashes.
6. **Checks.**
   - The region never stops, which means no hash desync and no ledger mismatch.
   - At least 119 months of ledgers balance.
   - People actually cross the borders.
   - Nobody's city was taken over.
   - Tess is still Westbrook's mayor.
   - Both tile hosts still serve their cities.
   - Westbrook's host is on the region's day.
   - Both towns have people.

## Results

| | Where | Result |
| --- | --- | --- |
| Border road | CI and local | Row 4: Toyville's road from (88, 4) out to column 86 and on to the west edge; Westbrook's from (46, 2) to column 52, down to row 4 and on to the east edge. Both built on the first row tried. |
| A decade | CI, Release | 3,601 days in **41 s** |
| A decade | Local container, Debug | 3,601 days in 141 s |
| Ledgers | both | **121 months balanced**, no mismatch, region never stopped |
| Crossing the borders | both | Up to **616 a day** (commuters, shoppers, tourists and freight across all borders) |
| Takeovers | both | None. Tess kept Westbrook and both tile hosts kept their cities. |

CI run: [CI #88](https://github.com/Z-L-D/toycity/actions/runs/41125053394) at 41925cd. All three jobs passed: server
(209 sim tests, 87 client-model tests, the region gate, the headless five-year run), web, and godot.

### The earlier gates, on the same run

- **Beta 1 gate** (the Alpha 3 loop on a 256 × 256 benchmark city, 6,980 buildings): every check passed.
- **Beta 2 gate** (four players on a dedicated server): every check passed in 135 s. That covers:
  - seats from the seat dialog;
  - stations leased as public services, with units;
  - Tess's line running;
  - Dan's scenes and report card;
  - Sam watching without the books;
  - May's offer accepted;
  - Tess's de-link with a copy, and the 88-day refusal after it.

  Peak process memory was 671 MB.

### Screenshots

The godot job's contact sheets and full-size UI shots are in the `godot-views` artifact of each CI run. Two worth
looking at:

- **Diagonal roads.** The road tool has Straight, Diagonal and Curve buttons. A diagonal being drawn previews its
  cells and price ("Diagonal, 23 cells: $340", grading included). The first shot of the curved road was refused
  because a building stood in its way. The shot now lays the road on open land below the town, from commit 087c241
  on.
- **The scene in the 3D city.** Dispatch's scene sits on the real buildings, with officers, SWAT, a suspect and a
  hostage as boxes, and a shot line. Two SWAT officers are box-selected and Breach is armed. The order list shows
  only the orders the selection can give: Cordon, Search and Breach for SWAT.

## What the gate found

- **Leasing several lots in a row was blocked** by the bus company's new waiting line from part 12. The Beta 1 and
  Beta 2 gates both timed out on "the leases". Now leases and lease releases are always sent, and only taking over
  or handing back the company waits.
- **Daily ledger checks** were added for this gate. Before, the region only compared monthly hashes, which can't
  tell whether a commuter who left one city arrived in the next. The checks found no imbalance over the decade.
- **The test's first road planner** started from the town's outermost street and ran into buildings and off the
  map. It now plans both mayors' roads first, over open land beyond everything built, and builds only when both
  can.

## Not covered by this gate

- **The manual multiplayer check.** Zac postponed the hands-on run on one Windows PC to the next phase. Its step-by-step
  guide (kept with the project files, not in the repo) now has a step 5 for a region over three servers.
- **These are downscopes, not failures:**
  - Transit deals are a capacity shuttle across the border, not company lines running into the neighbour.
  - Computer mayors don't build roads toward their neighbours.
  - There are no floods.
  - Remote side games aren't supported on federated tiles.
  - The region host trusts tile hosts for player names.
  - Money from crooked ways stays inside the service's books.
  - When a scene opens, the game slows to normal speed, not further.
  - The web client's bus company and its new, save and load don't show their waits yet.
  - The web client's scene is still a 2D canvas.
