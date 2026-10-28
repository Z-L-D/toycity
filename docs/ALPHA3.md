# Alpha 3: First link

Alpha 3 proves the side-game contract with transport: a player runs a bus company inside a city,
with visible effects in both directions, while the city's own seat runs on stasis, caretaker or
autopilot as they choose. This page records the rules and numbers as built, next to
[ALPHA1.md](ALPHA1.md) and [ALPHA2.md](ALPHA2.md).

## Gates

| Gate | How it is checked |
| --- | --- |
| A player runs a bus company inside a city with visible effects in both directions, while the city sits in stasis, caretaker or autopilot as chosen | Part 6 |
| The city seat can be held in stasis, caretaker or autopilot, with a "while you were away" report | `SeatTests` |
| Business lease or public service on side-game lots, with notice and bad standing | `LeaseTests` |
| The side-game contract is frozen at v1 | `ContractInfo.Version = 1`; `protocol/contract.proto`; `SideGameTests` round-trip every message |

## The side-game contract (v1)

A side game is a module that references `ToyCity.Contract` and nothing else of the city's. The
city is authoritative outside a leased lot and the side game inside it. In solo play both run in
one process on one tick: each day the city takes its own step, then each side game gets a
`CityToGame`, runs its day and answers with a `GameToCity`. The same messages are written out in
`protocol/contract.proto` for side games hosted in another process (Beta 2).

| Part | City to side game | Side game to city |
| --- | --- | --- |
| Register | | `Manifest`: id, name, contract version, the lot kinds it can lease |
| Lease | `LeasedLot` per lot: where, its district, its terms, any change on notice, bad standing | jobs per leased lot; lots it asks for on standard terms; leases it gives up |
| Signals | the day; districts with people, jobs, land value, crime, health and this game's rating there (monthly); commuter trips between districts (monthly); yesterday's riders per line | transit lines (stops, headway, fare, seats an hour); fares taken at each lot; a 0-100 service quality; a status line |
| Money | bills: rent, tax, subsidy (monthly) | what it paid against them |
| Incidents | everything the city and other side games raised, except its own | its own: breakdowns, injuries, anything |
| Hand-off | who owns the seat and how it's played today (manual, stasis, caretaker, autopilot) | |

Rules every side game keeps:

- **Deterministic.** Its tick runs under the city's lock on the city's thread, in tick order, with
  no clocks, no shared randomness and no unordered iteration. Randomness comes from the city's
  hash (`ICityQueries.Hash`).
- **Saved with the city.** Its state round-trips through `Save` and `Load` exactly. The city's
  save, its forecast (which runs a copy) and its replay (from the action log) all carry it.
- **Commands are logged.** A player's command reaches the side game as its own message bytes
  inside a `SideGameAction`, logged with the city's actions and applied at the start of a tick.
- **Read-only questions.** During its tick or a command it may ask the city for a street route
  between two cells at today's travel times, the district of a cell, and a hash.

A city attaches a side game with `AttachSideGameAction(id)`, also logged, from the catalogue of
side games the build registers. Loading a city that names one this build doesn't have fails with
a clear message instead of dropping it.

**Districts.** Side games are rated and paid by district: 32x32-cell squares (16 on a 128 map, 64
on a 256), each with a name from the voice ("Clockwork Heights", "Button Hollow").

**Incidents.** The city raises one when a disaster starts and when a building catches fire. A side
game's incident reaches the other side games the next day; when it has words, it shows as a
notice, and a serious one makes the news.

## Seats and the computer playing them

A seat is something a player controls: the city's mayor seat, and one operator seat per side game.
Each has an owner (a player, or nobody) and a level: **manual** (the owner plays it), or the
computer at **stasis**, **caretaker** or **autopilot**. `SetSeatAction` claims or hands one over and
is logged. The city always has an owner; a side game's seat starts unowned on caretaker, and the
side game hears the level each day in `CityToGame` and plays itself accordingly. Stasis is only
for the city seat. Nobody can take a seat another player owns.

The top bar shows who runs the city; clicking it opens the hand-off dialog. While the computer has
the city a banner says so with a "Take it back" button. Taking it back shows **While you were
away**: how long, population, jobs, funds, approval, debt and tax changes, every action the
computer took with its reason (click one to go there), and the news in the meantime.

| Level | What happens |
| --- | --- |
| Stasis | Nothing that persists changes: no construction, growth, decline, wear, budget, approval, prompts, life events or disasters. Derived layers (coverage, traffic, desirability) still refresh, commuters keep moving, side games keep running, and open petitions' deadlines move back a day for every held day. |
| Caretaker | The city runs as normal. Each month after the budget the computer takes a loan if the treasury is in the red, nudges taxes half a point toward balance (never more than a point from where the owner left them), rebuilds lost civic buildings like for like, and turns down deals. It never zones, builds roads, expands or demolishes. |
| Autopilot | As caretaker, plus: keeps taxes in its style's band, builds power, water and garbage when use nears capacity (polluters at least six cells from homes), builds the building a petition asks for near the petitioners, takes or declines deals by style, zones empty road-enclosed blocks when demand is 20 or more, and lays a new 11x11 block of streets (levelled, with a water pipe joined to the network) when there's demand and no empty block. |

Autopilot styles: **growth** (taxes 7-10%, cheapest power per kW, takes deals that bring money or
jobs), **green** (8-11%, clean power only, parks in land too deep for lots, no polluting deals,
won't put industry next to homes), **profit** (10-13%, takes any deal that pays), **chaos** (5-15%
at random, zones a random kind, flips a coin on deals). The guardrails hold for every style: the
computer never demolishes anything, and never spends more in a month than the owner's cap
(25,000 by default) or than the treasury holds. Loans don't count against the cap.

What the computer does is applied directly, not logged: it is deterministic, so a replay of the
log (with the logged hand-off) runs it again and gets the same city, and a forecast runs it too.

Actions from the client now apply as soon as the server gets them, at any speed, instead of at the
next day. At 10 seconds a day the wait was noticeable. They are logged at the current tick, before
that tick's step, which is where a replay applies them.

**Not built: the map clock tied to the real day.** It was proposed for this alpha, but at the
speeds from Alpha 2 (a game day takes 10 seconds at normal speed) a day-night cycle tied to the
real clock would show the same light for months of game time, so it's dropped until there is a
speed where it makes sense.

## Leases

Side games lease lots from the city: in this alpha, **bus stops** and the new **bus depot** (3x2,
$2,500, built by the mayor like any civic building). The city keeps the building; inside it the
side game decides, so a leased lot can't be bulldozed while the lease stands. A disaster can still
take it, and the lease ends with it. The building's panel shows the lease and lets the mayor lease
an empty lot or change the terms.

| | Business lease | Public service |
| --- | --- | --- |
| Money | the operator pays rent and a tax on the fares taken at the lot, and keeps the rest | the city pays a monthly subsidy |
| Fares | the operator's call | capped by the city (0 = free to ride) |
| Standard terms | rent $40 a month for a stop, $300 for a depot; 10% fare tax; 3 months' notice | (set by the mayor) |

- **Granting.** The mayor, playing by hand, may lease on any terms. A side game can also ask for a
  lot itself; the city grants it on its standard terms. While the city is on stasis, caretaker or
  autopilot the computer grants only standard terms or better for the city.
- **Billing.** On the first of each month the city bills rent, the fare tax on last month's fares
  and anything overdue (or pays the subsidy). What the side game pays shows on the budget as
  "Leases: <name>", subsidies as "Subsidies: <name>". The city keeps billing and paying in stasis:
  that's the side game's business, not the city's growth.
- **Changing terms.** New terms, including a switch between business and public service, start
  after the current terms' notice period. The side game sees them coming in `PendingTerms`.
- **Bad standing.** A lease falls into bad standing when the operator is behind on what it owes,
  or its authority rating in the lot's district stays below -200 for three months. It gets three
  months to put it right; the panel's why-trace lists exactly what's needed ("Pay the $40 owed",
  "Raise the rating in Button Hollow from -260 to -200 or more"). If it doesn't, the mayor may end
  the lease (autopilot does; caretaker doesn't). A lease in good standing can't be ended by the
  city. The operator can give one up whenever it likes; anything owed is written off.
- **Jobs.** The side game says how many people it employs at each lot. They are real jobs: the
  city's commuters fill them like any other.

## Save format

Format version 3 adds the side games (each one's own saved state, its last output and its riders),
the incidents not yet handed on, the last 50 incidents, and the seats (owner, level, style, cap, the
state at hand-off with the computer's notes so far, and the last report), and the leases. The content files changed too (district
names, the bus depot), so Alpha 2 saves don't load.
