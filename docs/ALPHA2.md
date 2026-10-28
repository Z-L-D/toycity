# Alpha 2: a city that talks back

Alpha 2 makes the city explain itself. A new player should be able to find out why anything is
happening without leaving the game. This page records the rules and numbers as built, next to
[ALPHA1.md](ALPHA1.md), which still describes everything Alpha 2 did not change.

## Gates

| Gate | How it is checked |
| --- | --- |
| A new player can find out why anything is happening without leaving the game | Why panel, demand panel, alerts, forecast and guide; `TalkBackTests` check that every alert, demand verdict and why-requirement links to a guide article |
| The built-in ordinances all run on the rule-graph format | `OrdinanceTests.EveryBuiltInOrdinanceIsARuleGraph`: every ordinance acts only through graph nodes, and no sim code names an ordinance |
| The benchmark sets agent tiers on two different machines | Part 7 |
| Event director rate-limited; voice can't change the sim | `DirectorTests` |

## Demand by source

Demand is still -100 to 100 per zone and wealth level, but it is now built from named sources,
then adjusted per wealth level, then held down by capacity limits. The demand panel shows every
line and names the one thing holding each zone back.

| Zone | Local | Regional | External |
| --- | --- | --- | --- |
| Residential | Jobs minus workers: 60 × (jobs − workers) / workers | Pull from the region: 40 × S / (S + population) | Sights: parks and landmarks, up to 15 |
| Commercial | One shop job per four residents | Shoppers from neighbours: population / 10, up to a quarter of the connection capacity, when connected | Tourists: 10 shop jobs per point of attraction |
| Industrial | 45% of workers | none | Exports: 50 jobs, plus an eighth of the connection capacity (up to 1,000) when connected |

For commercial and industrial each source asks for a number of jobs; demand is the gap between
what they ask for and the jobs that exist or are being built, as a share of what they ask for.

**Neighbour connections.** A path that runs off the map edge and is joined to a building's
driveway is a neighbour connection. S is 2,000 plus the vehicles an hour those paths carry (up to
20,000), so a town without one sees regional pull halve at 2,000 people, as in Alpha 1. The
connection is measured monthly and whenever a path is built or bulldozed.

**Attraction.** Parks have an attraction score (small park 1, plaza 2, large park 4); landmarks
will add more. It drives tourist shopping and a few residents who come for the sights.

**Adjustments.** Education and city size per wealth level, and taxes, as in Alpha 1.

**Limits.** Power headroom caps every zone's demand: with less than 25% of capacity spare, demand
is capped at four times the spare percentage, so 10% spare caps demand at 40 and a full grid at 0.
With no plant at all, demand is 0. The top bar's RCI bars show demand before limits as a dashed
outline. "Room to grow" counts empty zoned cells that have power and can fit a lot; it does not
change demand, but the panel names it when it is the reason nothing grows.

## Alerts

Alerts are standing problems, worked out from the city on request, worst first. Each has a
location to jump to and a guide topic. The server sends the list when it changes, checking at
most twice a second.

| Alert | When | Goes to |
| --- | --- | --- |
| Treasury in the red | Funds below zero | (budget) |
| No power plant | Zoned land and no plant | The first zoned cell |
| Brownout | Power demand above capacity | A browned-out cell |
| Water shortage | Water demand above capacity | A short cell |
| Garbage | Garbage made above capacity | The landfill |
| Civic building unpowered, pump without pipe, landfill without road | | The building |
| Buildings struggling | Any zoned building with unmet months | The worst one |
| Buildings abandoned | | The first one |
| Stranded zoning | Zoned cells no lot can reach | The first one |
| Demand with no room | Demand of 25 or more and no growable cell | That zone |
| Traffic jam | A path above 125% of capacity | The worst cell |

## Forecast

The forecast copies the city (snapshot and load) and runs the copy ahead 1 to 120 months on a
worker thread, so the live city keeps ticking. Because the sim is deterministic, a forecast
with no plan is exactly what will happen if the player changes nothing; a test checks this.

It reports population, jobs, money, power and water use and abandonment each month, the notices
the copy raised, and alerts that start during the run, with a short summary. A plan is a list
of actions applied to the copy first: in the client, "Plan changes" collects the player's tool
actions instead of sending them, then "Forecast plan" runs them and "Build it" sends them.

## Guide

The guide (encyclopedia) is generated on request from the content files and the live city:
rules (demand, growth, decline, power, water, garbage, traffic, land value, desirability,
pollution, noise, services, taxes, budget, neighbour connections, lots, time, forecast), the
three zones with every template, every civic building and every path profile. Each article ends
with figures from the current city. Why-panel requirements, alerts and demand verdicts link to
their article.

## Ordinances and the rule graph

Ordinances live in `content/ordinances.json`. Each one's behaviour is a rule graph: a list of
nodes evaluated in order, where a node may read any earlier node by id, a plain number, or an
inline node. The sim has no code for any particular ordinance. The same format will carry
scenarios and, later, player-made ordinances from the node editor.

| Kind | Ops |
| --- | --- |
| Select | `select.buildings` with any of `zone` (R/C/I), `density`, `wealth` (L/M/H), `category`, `type`, `zoned` |
| Measure | `measure.count`, `measure.sum` (residents, jobs, workers, powerKw, waterUse, garbage, cells), `measure.avg` (a map layer at the selected buildings), `measure.stat` (population, jobs, workers, laborForce, unemployed, funds, education, attraction, month, year, power, water, garbage, edgeCapacity, buildings) |
| Math | `const`, `math.add`, `sub`, `mul`, `div` (by zero gives 0), `min`, `max`, `percent`, `neg` |
| Test | `test.gt`, `ge`, `lt`, `le`, `eq`, `and`, `or`, `not` (1 or 0) |
| Act | `act.modifier` (key, amount), `act.income` and `act.expense` (label, amount); each takes an optional condition as a second input |

Modifier keys: `power.use`, `water.use`, `garbage`, `pollution.industry`, `traffic` (percent of
zoned buildings' use, factory pollution and commuter trips), `attraction`, `education`,
`landvalue`, `desirability.R/C/I`, `demand.R/C/I`, `demand.I.dirty`, `demand.I.hightech`
(points). Desirability and land value show an "Ordinances" factor in the why panel; demand shows
an "Ordinances" line in the demand panel.

Graphs are checked when content loads: unknown ops, stats, fields, layers, modifier keys,
building types and references to later nodes are errors.

**Running.** On the first of each month (before pollution, land value and the budget) the sim
updates availability, then runs every enacted graph and adds up their modifiers and money.
Enacting or repealing re-runs them at once. Modifiers and money are saved with the city.

**Availability.**

- *Always*: from a minimum population.
- *Unlock*: a small graph whose last value must be non-zero, checked monthly; once unlocked it
  stays unlocked. Park Rangers unlocks with a large park; landmark unlocks come in Part 3.
- *Random*: offered with a yearly chance, for a few months; once enacted it runs for a fixed
  number of months and ends. Street Festival and Film Shoot are the first two.

**Preview.** The Laws tab shows every ordinance's graph run against the city as it is now: each
labelled step with its value, the effects and the monthly net. "Forecast with and without" runs
two one-year forecasts, with the ordinance and without, and shows the difference.

The 15 built-in ordinances: Power Conservation, Water Conservation, Recycling Program, Clean Air
Act, Carpool Incentives, Pro-Reading Campaign, Neighborhood Watch, Free Smoke Detectors, Free
Clinics, Legalize Gambling, Tourism Promotion, Small Business Tax Holiday, Park Rangers, Street
Festival and Film Shoot Permit.

## Event director, petitions and deals

Event templates live in `content/events.json`; every condition and outcome is a rule graph. On
the first of each month the director:

1. Resolves open prompts. A petition is fulfilled as soon as its goal graph (run at the
   petition's location) turns non-zero, and ignored when its deadline passes. A deal is
   declined if the mayor hasn't answered by its deadline.
2. Perhaps raises one petition or deal: a 40% chance, only if fewer than 3 are open and the last
   was at least 2 months ago. Eligible templates have enough population, are past their own
   cooldown, have somewhere to happen, and pass their trigger graph. One is picked by weight
   with the city's random numbers.
3. Perhaps runs one news item (30%), picked the same way.

Templates `locate` their event: the home, shop, factory or road with the lowest or highest
value of a layer (for example the home with the least park coverage). Ties are broken with the
city's random numbers.

Outcome graphs run once when an event resolves. Three ops act only then: `act.grant` (money),
`act.mood` (approval) and `act.unlock` (a building type). If the outcome has `durationMonths`,
its modifiers and money keep running monthly for that long, like a temporary ordinance.

| Kind | Templates |
| --- | --- |
| Petitions | Park, police, school, clinic, lower taxes, fix traffic, recycling |
| Deals | Casino, toxic waste dump, Mega Pickle plant (each unlocks a reward lot), billboards, minor-league team |
| News | Pie contest, weather, the cat on the library board, a pothole named Gerald, smog, jobs |

## Mayor approval

Approval (0 to 100) is worked out monthly: base 45, plus a third of residents' average
desirability (±25), plus services near homes (up to about +15), minus a point per half point of
residential tax above 9%, minus unemployment above 5% (up to 15), plus 2 per landmark or
attractive reward lot (up to 10), plus mood, minus 10 when the treasury is in the red. Mood is
moved by petitions and deals (±2 to ±10 each) and fades by a sixth a month. Approval moves
residential demand by a point per 5 points away from 50.

## Landmarks and reward lots

Placeable types can carry an `unlock` graph; the sim checks locked ones monthly, and they stay
unlocked. One of each may be built (`unique`).

| Kind | Buildings |
| --- | --- |
| Landmarks (free) | Toy Soldier Statue (400 people), Giant Rubber Duck Fountain (1,200), Wind-Up Clocktower (2,500), Observation Wheel (5,000) |
| Reward lots | Mayor's House (60% approval), City Hall (2,000 people and 55%), Statue of the Mayor (80%) |
| Deal lots | Lucky Lug Nut Casino ($900 a month), Toxic Waste Dump ($1,500 a month, heavy pollution), Mega Pickle Plant (180 jobs) |

Landmarks and reward lots with a radius cover their surroundings like a park, and their
attraction feeds tourism and residential sights. `income` is paid monthly under "Deals and
reward lots".

## Advisors and the news ticker

Five advisors (finance, utilities, planning, safety and health, transport and environment) read
the same numbers as the alerts and demand panel and say what they would do. Each message links
to a guide article and, where it has one, a place on the map.

The ticker shows the newest headline, then cycles through recent ones; headlines come from
prompts, outcomes, news items, milestones, laws and unlocks. The last 40 are saved with the city.

## The voice layer

Every sentence comes from `content/voice.json`, keyed by event or situation, with placeholders
filled from facts the sim provides (names, streets, amounts). Variants, names and street names
are picked from a hash, never from the sim's random numbers, so a different voice file can't
change what happens. A test runs the same city with a near-empty voice file and checks that
population, money, approval and every prompt match. A small local language model can later
replace this layer without touching the sim.

## Balance changes

- A path now carries twice its hourly capacity in commuter trips a day (Alpha 1 used a quarter).
  The forecast showed the default starter town gridlocking its apartment blocks into abandonment
  within a year, which then crashed shop and factory demand. Starter towns on four seeds now grow
  and hold steady with no abandonment over four years.

## Save format

Format version 2 adds the neighbour connection and the utility totals of the last tick, so a
loaded city's queries and demand limits match the city that was saved.
