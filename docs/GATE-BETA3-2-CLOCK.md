# Beta 3.2, part 2 gate: the clock

Part 2 splits the game's time into two clocks (C1 to C5 of the Beta 3.2 plan, as Zac accepted them on 2026-11-01).

- **The day clock** runs the hours: commuters, emergency calls, crews on the road, and later the sun and showers.
  A clock day lasts an hour of real time at speed 1, half an hour at speed 2 and ten minutes at speed 3.
- **The calendar** runs the months: growth, building, money, taxes and seasons. Its pace is set apart from the day
  clock, and the mayor picks it from five paces:

  | Pace | Calendar days per clock day | A calendar day at speed 1 / 3 | A year at speed 1 / 3 |
  | --- | --- | --- | --- |
  | A month a day | 30 | 2 min / 20 s | 12 h / 2 h |
  | Two months a day | 60 | 1 min / 10 s | 6 h / 1 h |
  | Four months a day | 120 | 30 s / 5 s | 3 h / 30 min |
  | A year a day (a new region's default) | 360 | 10 s / 1.7 s | 1 h / 10 min |
  | Two years a day | 720 | 5 s / 0.8 s | 30 min / 5 min |

  At a year a day, speed 1 grows a city exactly as fast as it always has. Two years a day at speed 3 is the old
  full speed. Growth and money are still counted per calendar day and month (C5), so their tuning hasn't changed.

## How it works

- The sim's step is still a calendar day. Each step moves the day clock on by a clock day's share: 48 minutes at a
  month a day, down to 2 at two years a day (`World.ClockMinute`, `World.Pace`, `src/ToyCity.Sim/Clock.cs`).
- Life-paced things go by the day clock in **turns** of 48 minutes. These are:
  - the city raising its own crime and crash calls
  - fires burning and spreading
  - storms moving
  - the dispatch game's calls waiting, crews coming back, and scenes left alone

  At a month a day every calendar day is a turn, so the game plays exactly as it did with one clock. Every existing
  test passes, and only one needed a change, to a wording. At a faster calendar several calendar days pass in a turn,
  and calls still come at one rate per clock hour.
- Side games get both clocks (contract v1.5: `ClockMinute`, `ClockMinutesPerTick`, `NewTurn`, `Turn`), so a side
  game keeps the calendar's seasons and months and the day clock's hours (C4). The dispatch game:
  - now counts its waits in turns
  - logs by the day clock ("Tue 14:32: …")
  - shows waits in clock time ("waiting 1 h 36 min", "back in 1 day")
- A region shares both clocks. A city founded later takes the region's clocks, and a replay from its founding starts
  on the same hour.
- The server paces itself by the day clock: a calendar day lasts a clock day's real time divided by the pace. The
  region's pace is a logged action in every city, sent on to tile hosts with the day's other actions. An admin can
  set it, or the mayor when every city in the region is theirs.
- Both clients show the time as "Tue 14:32 · March, year 3", with the hands running on between ticks, and have a
  pace menu beside the speed buttons. The bus company and dispatch screens show the same clock.
- Saves are format 11.
  - An older city takes up a year a day.
  - Its clock is set where the region's calendar puts it.
  - An older dispatch save has its day counts moved onto turns.

## Checks

| Check | Where |
| --- | --- |
| At a month a day every calendar day is a turn, and the clock reads "Tue 16:00 · February, year 1" after 40 days | `ClockTests` (Sim) |
| At a year a day, 360 calendar days are one clock day of 30 turns; a pace off the list is refused, and a player can't set it on one city | `ClockTests` |
| The city's own calls come at one rate per clock hour: 72 calls in two clock days at a month a day, 73 at a year a day | `ClockTests` |
| At a year a day, an unanswered call still waits after 3 calendar days and is lost after 3 turns (2 h 24 min of clock, 36 calendar days) | `ClockTests` |
| A tile founded later reads the region's hour; its replay and its save keep the clocks | `ClockTests` |
| A new server's region keeps a year a day; an admin sets two years a day and every city follows; another player is refused | `ClockClientTests` (ClientModel, a real server) |
| The hands run on between ticks at the speed's pace but never onto the next tick's minute | `ClockClientTests` |
| The Godot gate sets the calendar's pace from the client and checks the clock moves two minutes a calendar day | `GateRun` ("two clocks") |

The Alpha 3 loop and the together run keep their old timings by setting two years a day at their start.

## Results

CI_RESULTS

## What's next

The sun following the day clock comes with the lighting PR. Showers that play out in clock hours come with weather on
the roads. Holidays and seasonal events on the shared calendar are on the roadmap for 1.x.
