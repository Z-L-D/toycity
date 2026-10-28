# Alpha 3: First link

Alpha 3 proves the side-game contract with transport: a player runs a bus company inside a city,
with visible effects in both directions, while the city's own seat runs on stasis, caretaker or
autopilot as they choose. This page records the rules and numbers as built, next to
[ALPHA1.md](ALPHA1.md) and [ALPHA2.md](ALPHA2.md).

## Gates

| Gate | How it is checked |
| --- | --- |
| A player runs a bus company inside a city with visible effects in both directions, while the city sits in stasis, caretaker or autopilot as chosen | `GateTests`: the player's bus company with the city on each of the three levels; riders and fares one way, depot jobs, stop riders and fewer car trips the other; stasis holds funds and population, caretaker and autopilot bill the leases, autopilot grows the city |
| The city seat can be held in stasis, caretaker or autopilot, with a "while you were away" report | `SeatTests` |
| Business lease or public service on side-game lots, with notice and bad standing | `LeaseTests` |
| A transport side game: bus lines on leased lots, stops, vehicles, fares, per-route profit and loss, ridership from city demand | `TransportTests` |
| District authority rating and paid actions, gated by the players' consent settings | `AuthorityTests` |
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
| Money | bills: rent, tax, subsidy, compensation (monthly) | what it paid against them |
| Standing | the paid actions on offer, with prices and whether consent allows them (daily); yesterday's paid actions answered, with the rating now | paid actions it buys in a district |
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
| Stasis | Nothing that persists changes: no construction, growth, decline, wear, budget, approval, prompts, life events or disasters. Derived layers (coverage, traffic, desirability) still refresh, commuters keep moving, side games keep running (their leases aren't billed and their ratings and standing hold), and open petitions' deadlines move back a day for every held day. |
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

## The bus company (transport side game, alpha)

`src/ToyCity.Transport` is the first side game. It references `ToyCity.Contract` and nothing else
of the city's; its own commands and view are in `protocol/transport.proto` and travel through the
city protocol as bytes. The server hosts it in every city as **Toy Transit Co.**, which starts
unowned on caretaker with $25,000. Claim it from the Buses tab ("Run it yourself") or hand it to
the computer.

- **Lots.** It leases bus stops and bus depots the mayor builds. Buses live at a depot (12 per
  depot), so a line needs one. The Buses tab lists the company's lots and the free ones it could
  lease on standard terms.
- **Lines.** A line is two or more leased stops in order; it runs both ways along the fastest bus
  route between them. Draw one with "New line…" by clicking the stops on the map. Round trip =
  twice the route time (buses run 10% slower than cars) plus 4 minutes to turn around; headway =
  round trip / working buses, at least 3 minutes; seats an hour = 40 a bus x 60 / headway.
- **Money.** A bus costs $3,000 (sells back for $1,500). Each month a bus costs $500 in drivers'
  wages and $120 upkeep. Fares are the line's fare per boarding, capped by any public-service stop
  on the line. Rent, fare tax and anything overdue is paid on the first; when cash runs short the
  company pays what it can and the lease falls behind. Every line shows its riders, boardings per
  stop, how full it is at the peak, and last month's fares, costs and profit (rent and subsidy are
  shared out by buses).
- **Riders, from the city's commuters.** Leased stops are served only by the company's lines; stops
  nobody leases keep the city's own bus service. In the commute search people walk to a stop
  within its catchment, wait (half the headway on a frequent line; on a sparse one three minutes
  plus a tenth of the headway, since people check the timetable), longer when riders outnumber
  seats at the peak, pay the fare (weighed as time: $1.50 feels like 1.5 minutes), ride stop to
  stop, change lines at a stop or a nearby one, and walk to work. A higher fare or a sparse
  timetable sends people back to their cars or bikes. The company counts riders and boardings
  from those commutes every day and takes the fares; the city's own fares only count its own
  service.
- **Workers without a car.** To give transit something to win, workers without a car now travel
  no faster than a bike (15 km/h) on any street, plus two minutes to set off, instead of a flat
  eight minutes extra. Short trips are as quick as before; long ones are where a bus line helps.
- **Effects both ways.** City to company: commuter demand, fires and disasters within three cells
  of a stop close it for five days, rent and fare tax, public-service caps and subsidies, the
  seat's level. Company to city: its riders' trips (fewer cars on the road: a bus adds one vehicle
  per twenty riders), drivers' jobs at the depot (two a bus, plus two staff), rent on the budget,
  breakdowns in the notices (one bus in 500 a day, off the road for two days), and its buses on
  the map.
- **The computer running it.** Caretaker keeps it solvent (sells a bus when cash goes negative).
  Autopilot also asks for a depot and up to eight stops (busiest districts first), draws a line
  through its stops nearest-first, buys buses until one comes every 15 minutes or more when a
  line is over 90% full, and sells them when a line runs under 25% full.

### Playing the company

The bus company has its own view in the web client. The 🚌 button in the city's top bar (or Buses →
Switch to the company) swaps the city's top bar, tools and panel for the company's: cash, buses and
service in the top bar; Look, Lease lots and Draw a line tools; and Lines, Lots, Money, Standing and
Log tabs. The map dims the city, marks the company's lots (solid) and free ones (dashed), shades
each stop's walking reach, and draws the routes with their buses (dashed when a line isn't running).
⧉ Own window opens the company at `?view=company`, so one window can show the city and another the
company. In the city view, routes show only under Layers → Bus routes.

A bus stop's walking reach is 8 cells (it was 4 in Alpha 2). At 4, a line through two stops in the
starter town reached too few homes and jobs for anyone to ride. Each line shows how many homes and
jobs its stops reach and, with no riders yet, why.

## Authority rating, paid actions and house rules

Each side game has a rating in each district it has lots in, from -1000 to 1000, shown on its
standing screen (the Buses panel) and in each leased lot's lease panel.

- **Drift.** On the first of the month the rating moves a quarter of the way to a target: (service
  quality - 50) x 4, plus riders at its stops yesterday (up to 400), plus two a job (up to 100),
  minus 250 for each lease there in arrears or bad standing, minus 60 for each incident it caused
  there that month (up to 400), plus 150 for a statue. Districts where it has no lots drift to 0.
  Nothing moves in stasis.
- **What it gates.** Below -200 for three months puts a lease in bad standing (part 3). Below
  -400 the district turns down the side game's requests for more lots there; the mayor can still
  lease by hand.
- **Paid actions** (OpenTTD's town actions). The side game asks through the contract and the city
  answers the next day; the side game pays only for what went ahead.

| Action | Price | Effect | Consent |
| --- | --- | --- | --- |
| Advertising campaign | $2,000 | +200 at once, then drifts back | none |
| Statue in the square | $8,000, to the city | +150 on the target for good; one a district | none |
| Exclusive rights for a year | $12,000, to the city | the city's own buses stop serving unleased stops in the district for 360 days; one holder at a time | exclusive rights |
| Bribe the district council | $5,000 | +400, or one time in three caught: rating -800, a scandal in the news, no bribes there for six months | bribery |

**House rules (consent).** Each player switches mechanics on or off for themselves: bribery,
exclusive rights, sabotage, hostile evictions, absurd disasters and crime aimed at players' lots.
A mechanic is on only while every player who owns a seat allows it; computer-run seats count for
their owner and unowned seats don't count. A new player allows only absurd disasters (what the
game did before). The table (Mayor → House rules) lists every owner against every mechanic with
the result and who is blocking it. Tightening applies to new actions at once; exclusive rights
already bought and evictions under way run out. What each switch does today:

- Bribery and exclusive rights: the paid actions above.
- Hostile evictions: the mayor may end a lease in good standing. It gets its notice period, then
  the city pays six months' rent (at least the standard rent) as compensation.
- Absurd disasters: off, the Clockwork Colossus no longer walks in by itself (the sandbox menu
  still starts one).
- Sabotage and crime aimed at players' lots are in the table but nothing uses them yet.

Not built: host-wide minimum allowances, and agreements between just two players.

## Save format

Format version 3 adds the side games (each one's own saved state, its last output and its riders),
the incidents not yet handed on, the last 50 incidents, and the seats (owner, level, style, cap, the
state at hand-off with the computer's notes so far, and the last report), the leases, the
standings (rating, target, statue, exclusive rights, bribe ban), paid actions waiting for an answer,
compensation waiting to be paid, and each player's consent settings. The content files changed too (district
names, the bus depot), so Alpha 2 saves don't load.
