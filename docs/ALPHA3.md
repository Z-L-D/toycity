# Alpha 3: First link

Alpha 3 proves the side-game contract with transport: a player runs a bus company inside a city,
with visible effects in both directions, while the city's own seat runs on stasis, caretaker or
autopilot as they choose. This page records the rules and numbers as built, next to
[ALPHA1.md](ALPHA1.md) and [ALPHA2.md](ALPHA2.md).

## Gates

| Gate | How it is checked |
| --- | --- |
| A player runs a bus company inside a city with visible effects in both directions, while the city sits in stasis, caretaker or autopilot as chosen | Part 6 |
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
| Lease | `LeasedLot` per lot: where, its district, its terms, any change on notice, bad standing | jobs per leased lot |
| Signals | the day; districts with people, jobs, land value, crime, health and this game's rating there (monthly); commuter trips between districts (monthly); yesterday's riders per line | transit lines (stops, headway, fare, seats an hour); taxable revenue; a 0-100 service quality; a status line |
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

## Save format

Format version 3 adds the side games (each one's own saved state, its last output and its riders),
the incidents not yet handed on, and the last 50 incidents. The content files changed too (district
names), so Alpha 2 saves don't load.
