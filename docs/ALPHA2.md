# Alpha 2: a city that talks back

Alpha 2 makes the city explain itself. A new player should be able to find out why anything is
happening without leaving the game. This page records the rules and numbers as built, next to
[ALPHA1.md](ALPHA1.md), which still describes everything Alpha 2 did not change.

## Gates

| Gate | How it is checked |
| --- | --- |
| A new player can find out why anything is happening without leaving the game | Why panel, demand panel, alerts, forecast and guide; `TalkBackTests` check that every alert, demand verdict and why-requirement links to a guide article |
| The built-in ordinances all run on the rule-graph format | Part 2 |
| The benchmark sets agent tiers on two different machines | Part 7 |

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

## Balance changes

- A path now carries twice its hourly capacity in commuter trips a day (Alpha 1 used a quarter).
  The forecast showed the default starter town gridlocking its apartment blocks into abandonment
  within a year, which then crashed shop and factory demand. Starter towns on four seeds now grow
  and hold steady with no abandonment over four years.

## Save format

Format version 2 adds the neighbour connection and the utility totals of the last tick, so a
loaded city's queries and demand limits match the city that was saved.
