# Beta 2: Together

Beta 2 turns ToyCity into a game several people play at once: accounts on a server you host, each player on their
own seat, consent and disputes between them, and de-linking when a partnership ends. It also brings the emergency
services side game, the transport beta and smooth shapes. This page records the rules as built, part by part.
The plan: https://claude.ai/artifact/UiLRUDmMwxwAh2sJjmPtYk

## Part 1: accounts and a dedicated server

### Solo and dedicated

| | Solo (the default) | Dedicated (`--dedicated`) |
| --- | --- | --- |
| Listens on | this machine only (`localhost:5180`) | every network the machine is on (`0.0.0.0:5180`) |
| Signing in | none: every connection plays as `player`, the name every earlier city gave its mayor | a name and password, or a token from an earlier sign-in |
| Who may save, load, found a city and set the speed | the player | the admins in host.json |

Run a dedicated server with

    dotnet run --project src/ToyCity.Server -c Release -- --dedicated

`--urls` still picks the address. `ToyCity:Dedicated=true` in settings or the environment does the same as the flag.

### host.json

Written beside the saves with the defaults the first time a dedicated server starts (or at `ToyCity:HostConfig`).
Edit it and restart the server.

```json
{
  "name": "ToyCity server",
  "join": "Open",
  "inviteCode": "",
  "allowed": [],
  "admins": []
}
```

- **name** is shown on the sign-in screen.
- **join**: `Open` (anyone may make an account), `Invite` (a new account needs `inviteCode`), or `List` (only the
  names in `allowed`). Players who already have an account can always sign in.
- **admins** may save, load, found a new city and change the speed. While the list is empty, the first account made
  becomes an admin and is written into the file.

### Accounts

Each server keeps its own accounts in `accounts.json` beside its saves, so a self-hosted or offline server needs
nothing outside itself. Sign-in through Steam or Discord can come later on top of this.

- Names are 2 to 24 letters, numbers, spaces, `-` and `_`, starting with a letter or number, unique ignoring case.
  Passwords need at least 6 characters.
- Passwords are kept only as salted PBKDF2-SHA256 hashes (100,000 rounds).
- Signing in gives a token good for 30 days. Each token works once and is swapped for a new one, and only its SHA-256
  hash is stored. An account keeps at most 8 at a time (one per device, roughly).
- A connection that fails to sign in 5 times is closed.
- Until a connection signs in it gets the Welcome and nothing else; anything but `SignIn` is answered "Sign in
  first."

### Protocol

`Welcome` now says whether the server needs a sign-in, whether new accounts need an invite code, and, when the
connection is signed in already (solo), who it plays as and whether it's an admin. `SignIn` carries a name and
password (with `create` to make the account, and the invite code) or a token. The answer is `SignedIn` (the name as
first spelled, a new token, admin or not) followed by the city, or an `Error`.

### In the clients

- **Godot:** a sign-in panel appears when a server asks, with the server's address to change, name, password, and
  the invite code when the server needs one. It also appears when the server can't be reached for 5 seconds, so the
  address can be fixed. Tokens are kept per server in `user://tokens.json` and the last server joined in
  `user://server.txt`, so the next start signs in by itself. On the command line, `--user NAME --password PASSWORD`
  signs in without the panel (add `--new-account` to make the account first).
- **Web:** a sign-in dialog, with the token kept in the browser per server. Players who aren't admins don't see
  New city, Save, Load or the speed buttons.

### Tests

`AccountTests` (the account store and host.json rules) and `DedicatedServerTests` (a real dedicated server: nothing
before sign-in, a wrong invite refused, the first account promoted, a non-admin refused, a token signing back in,
the connection closed after 5 failures). CI also runs the Godot smoke test against a dedicated server, signing in
from the command line.

## Part 2: players on seats

### Who may do what

Every action now carries the player who sent it (`SimAction.By`), taken by the server from the connection and never
from the message. It is logged with the action and saved (`ActionRecord.by`), so a replay or a loaded save applies
the same rules to the same people. The sim checks it before applying anything (`World.Forbidden`):

| Action | Who may |
| --- | --- |
| Anything done to the city (paths, zones, buildings, taxes, budget, laws, petitions, disasters, leases) | the mayor seat's owner |
| A side game's commands | that side game's seat owner |
| Giving up a lease as its operator | the side game's seat owner |
| Claiming a free seat | anyone, for themselves only |
| Handing over, or changing how a seat is played | its owner (who can give it to another player) |
| Consent settings | each player for themselves |

An action from nobody in particular (tests, tools, saves from before Beta 2) is allowed as before. A refused action
is answered with a notice to the player who sent it alone; everyone else hears nothing. Notices now carry who they're
for (`SimNotice.To`), and "You're running the city" goes only to the mayor.

### The mayor on a dedicated server

A dedicated server's city needs a real player as mayor. The city it starts with, and any city loaded or founded that
names someone without an account here as mayor (a solo save's `player`, say), goes to the admin who loaded or founded
it, or to the first admin. On a brand-new server that is the first account made.

### Presence

The server sends `Presence` whenever someone joins, leaves or their seats change: every player online and every seat
owner even while away, with their seats, whether they're an admin, and `spectator` for a player online who owns no
seat (they watch, and can claim a free seat). Both clients show it in the top bar: the web client as a name with what
they run under it, dimmed while away; Godot as a line under the status.

### Away automation

On a dedicated server, when a seat owner's last connection closes and they stay gone for `awayAfterSeconds` (in
host.json, 120 by default; 0 turns it off), the server hands each seat they were playing by hand to the computer at
the seat's **away level**: caretaker unless they chose otherwise. The mayor can choose stasis, caretaker or autopilot
(with its style) in the hand-off dialog under "While you're away from this server"; side-game seats can't use stasis.
When the player signs in again, every seat the computer took that way goes straight back to them, with the "while
you were away" report. A seat they handed to the computer on purpose stays where they left it.

The hand-offs are logged as `SeatAwayAction`s by that player, so replays match. Solo play has no away automation: the
city keeps running as you left it, as before.

New actions: `SetAwayLevelAction` (protocol `SetAwayLevel`); `SeatAwayAction` (server only, saved as
`ActionRecord.away`). `SeatState` gains `away_level`, `away_style` and `auto_away`. Save format 5 (Alpha 3 and Beta 1
saves, format 4, don't load).

### Tests

`PlayerTests` (the rules above, refusals to the sender, away and back, who-did-what through save and replay) and
`MultiplayerTests` (two players on a real dedicated server: the first account is mayor, a spectator's refusal reaches
only them, claiming and giving up a seat, the seat going to the computer two seconds after its owner leaves and coming
back when they sign in with their token).

## Part 3: consent, the full version

### Everyone, or the two involved

Each mechanic now has a scope (`World.ScopeOf`):

| Mechanic | Scope | Between |
| --- | --- | --- |
| Bribery | the two involved | the side game's operator and the mayor |
| Exclusive rights | everyone | it shuts out every rival |
| Sabotage of other players | the two involved | the saboteur and the target |
| Hostile evictions | the two involved | the mayor and the lease's operator |
| Absurd disasters | everyone | the Colossus walks over everyone's lots |
| Crime aimed at players' lots | the two involved | the instigator and the target |

A mechanic that reaches everyone is on only while every seat owner allows it, as before (`Allowed`). One between two
players needs only those two (`AllowedBetween`): a third player saying no doesn't stop a bribe between an operator
and the mayor. A computer-run seat with no owner never objects. `WhyOff` gives the reason in words wherever a
mechanic is refused: the host, the city, or the players who won't allow it.

### Pacts

Two players can agree on a mechanic between just them, whatever either allows in general: one offers
(`OfferPactAction`), the other accepts (`AcceptPactAction`), and either can end it or withdraw or turn down an offer
(`EndPactAction`). Offering what the other side has already offered makes the pact at once. Pacts are only for
mechanics between two; one that reaches everyone can't be agreed privately. Pacts and open offers are saved, and
both clients list them under House rules with Accept, Turn down, Withdraw and End buttons, plus a way to offer one to
any other seat owner.

### Minimums

- **The host:** `forbid` in host.json (mechanic names, such as `["Sabotage"]`) forbids them in every city on the
  server. The server applies it with `SetHostRulesAction` whenever a city arrives (start, load, new city); only the
  server can send it, and it is logged (`ActionRecord.host_rules`) so replays agree.
- **The city's owner:** the mayor can forbid any mechanic in their city (`SetCityRuleAction`), shown as a "City
  forbids" column in House rules that only the mayor sees.

Either beats every player's setting and every pact.

### Settings that follow the player

On a dedicated server each account keeps the mechanics its player last allowed (`accounts.json`, `consent`). A player
who hasn't set anything in a city brings those settings with them when they sign in, or when an admin loads or
founds a city while they're online. Changing a setting in any city updates the account.

### The settings table

`ConsentRow` gains `pairwise`, `host_forbids` and `city_forbids`; `ConsentTable` lists `pacts` (with `offer` set for
an offer not yet taken up). Each row's result reads On, Off (with who is blocking), Between two (with who won't),
or Forbidden by the host or by the city.

### Tests

`ConsentTests` (a bribe needing only the operator and the mayor while a third player says no; a mechanic that
reaches everyone needing everyone; a pact allowing an eviction and surviving a save; the host's and the city's rules
beating everyone, a pact included) and `MultiplayerTests` (a forbidden mechanic on a real server, and a player's
setting following them into a new city).

## Part 4: roles and council

### Deputies

The mayor can make any other player a deputy mayor with one or more portfolios (`SetDeputyAction`); a deputy with
none is stood down. The city's actions fall under these portfolios (`World.PortfolioFor`):

| Portfolio | Actions |
| --- | --- |
| Planning | roads and paths, zoning, utilities, terraforming, bulldozing |
| Budget | taxes, service funding, loans |
| Services | civic buildings and their upgrades |
| Transit | stops and depots, and leasing lots to side games |

Everything else (ordinances, house rules for the city, handing out portfolios, the council) stays with the mayor.
A refusal names who may do it: "Only the mayor (zac) or a deputy for the budget can do that." Deputies are shown in
the presence bar ("deputy for budget/transit") and are not counted as spectators.

### The council

Off by default. When the mayor seats it (`SetCouncilAction`), tax changes, ordinances and loans become proposals
instead of happening at once. Every seat owner and every deputy is a member. The proposer votes yes; members vote
with `VoteAction`. A proposal is decided as soon as every member has voted, or after `World.VoteDays` (7) on the
votes cast. More yes than no passes; a tie goes the way the mayor voted, and fails if the mayor didn't vote. A passed
proposal is applied as its proposer. If the proposer is the only member, nothing goes to a vote. Dissolving the
council fails whatever is still open. Deputies, the council and proposals are saved, and the server resends them
whenever a vote or a role changes.

### Who sees what

- A side game sends two views: `operator` for its seat's owner (or for everyone while nobody holds the seat) and
  `public` for everyone else. The bus company's public view keeps the lines and lots for the map but drops the cash,
  the books, the standing, the offers and the log. Both clients say "Olu runs this company" in place of those tabs.
- Standing with the city goes only to the side game's operator. The mayor sees each district's rating but not a
  bribe ban (who has been caught); other players see none of it.

### Server renames are logged

When a dedicated server gives the solo "player" seats to an admin, it now does so with `RenameOwnerAction`, logged
as `ActionRecord.rename`, so a replay of a shared city reaches the same state.

### Tests

`RolesTests` (a deputy acting only in their portfolio; the council off until the mayor seats it; passing, ties to the
mayor, deadlines, dissolving; save and replay) and `MultiplayerTests` (the public and operator views on a real server,
standings hidden from a rival, a deputy's proposal passed by vote over the wire).

## Part 5: side games in another process

### The host

`src/ToyCity.SideGameHost` runs side games for cities elsewhere: `--port 5190` (default) and `--listen 0.0.0.0` to
take connections from other machines (loopback by default). It references the contract and the games, and no city
code. The wire protocol is `protocol/sidegamehost.proto`, carrying contract v1's own messages (`contract.proto`),
each framed as a 4-byte little-endian length and the message. Its two ends are in `src/ToyCity.Contract.Wire`:
`RemoteSideGame` (the city's end, an `ISideGame` like any other) and `SideGameServer` (the host's end). The contract
messages and their conversions (`ContractWire`) moved there from ToyCity.Protocol, so a host needs nothing else.

A city server puts a game on a host from `host.json`:

```json
"sideGames": [ { "id": "transport", "address": "127.0.0.1:5190", "deadlineMs": 2000 } ]
```

One connection is one game. Each city opens its own, and so does every copy of a city (a forecast opens one for the
forecast and closes it after; `World` is now `IDisposable` for this).

### Calls and questions

The city makes one call at a time (open, new game, tick, command, view, save, load) and waits for the answer. During a
tick or a command the game can ask the city its read-only questions (a street route, a cell's district, a hash);
each goes back over the connection and the host waits for the answer before going on. City width and height travel
with each call.

### Deadlines

A tick or a command that isn't answered within `deadlineMs` is aborted: the city sends `abort` with the call's
number and the host puts the game back as it was before the call (it keeps a saved copy before every call that
changes the game), whether the call was still running, waiting on a question, or had finished late. Both sides then
agree the call never happened, and the city logs that it didn't:

- **A missed day** (`SideGameMissedAction`, "day"): the game sits the day out. Yesterday's lines and jobs carry on,
  the status says so, and nothing is paid or asked for that day, so a missed first of the month leaves the bills
  unpaid as for any operator who didn't pay.
- **A missed command** ("command"): logged in the command's place, and the player is told to try again.

Both are saved as `ActionRecord.missed`, and a replay skips exactly the same day and command, so a replay with the
game in-process reaches the same state as the live city with the game elsewhere. Opening, saving and loading aren't
held to the deadline (15 seconds). A host that won't undo a late call, or whose connection drops, is cut off: from
then on its game misses every day until the server loads the city again. A view that comes late falls back to the
last one the host sent.

### Tests

`RemoteSideGameTests`: the bus company played the same way in-process and on a host ends in the identical state
(and view), carries on in step after a save and load through the host, and survives a forecast; a probe that runs
late on one day and one command is undone on the host, logged, and a replay with the probe in-process matches.
`SideGameHostTests` runs the real host process and a dedicated server whose bus company lives on it.

## Part 6: security hardening

### Bounds on every action

`World.OutOfBounds` runs before the permission check on every action, whoever sent it, and refuses the action whole
when it is outside what any player could do from a client:

- cells on the map (a drag may run up to a map's width past the edge, since the city clips it, but a rectangle must
  touch the map; single cells such as a building's corner or a disaster must be on it);
- enums that exist (zones, densities, modes, mechanics, seat levels and styles, portfolios);
- ids that point at a building;
- taxes 0–20%, funding 0–200%, loans and spending caps up to $1,000,000,000, lease terms in range;
- names up to 40 characters, ids up to 64, side-game commands up to 64 KB.

### Connections

- A message over 1 MB closes the connection (`MessageTooBig`).
- Each connection has a token bucket of 200 messages, refilled at 60 a second; running dry closes it.
- Three unreadable messages close it.
- A message the server fails to handle closes the connection that sent it and is logged; the city and everyone else
  carry on.
- One forecast at a time per connection, with at most 200 planned actions.

### Content is data

Content packs are only ever read from the server's own content folder; nothing a client sends is loaded as content or
code. Loading now refuses unknown fields (a misspelling is an error, not ignored), ids that aren't lowercase letters,
digits and `-`, and sizes, costs, radii and outputs outside what the sim is built for.

### Tests

- `ActionFuzzTests`: 3,000 nonsense actions (every field random, from the mayor, a stranger or nobody) over 120 days
  never throw, and the city replays and reloads to the same state. Without the bounds check the same run runs out
  of memory.
- `FuzzTests` (against the real server): 900 well-formed nonsense messages never drop their sender or stop the city;
  garbage, a 3 MB message and a flood each drop only their sender while another player keeps playing.
- `ContentCheckTests`: unknown fields, a bad id and an out-of-range radius are refused at load.

## Part 7: emergency services alpha

A second side game, `emergency` (src/ToyCity.Emergency, messages in protocol/emergency.proto), on every city the
server runs, beside the bus company. It leases fire stations, police stations, clinics and hospitals as public
services (subsidy $400 a month for fire and police, $300 for a clinic, $800 for a hospital). The city's own fire,
police and health coverage stays as it was: the service adds crews on top.

### What the city sends it (contract v1.1)

The contract stays at version 1; `ContractInfo.Minor` is now 1, and everything added is optional for a side game.

- The city raises crime calls where policing is thin (a district with no police at all gets about one every four
  days, sooner while it's "hot"), crashes on its busiest roads, and an injury when people are inside a building
  that catches fire. At most four a day, and only when a side game is attached.
- `GameToCity.Responses`: how each call ended. Fire `out` puts the building's fire out; `spread` sets a neighbour
  alight. Crime `escaped` heats the district up (+15) and `arrest` cools it (−10). Text goes in the news. The city
  applies each answer once.
- `GameToCity.Holding`: calls a player is handling at the scene. The city holds those still (a held fire neither
  spreads nor goes out by itself) until the answer comes back.
- `ICityQueries.Area`: what is on each cell of a rectangle, for laying a scene out. Remote side games get it over
  the wire like the other queries.

### The service

- A new station comes with units: two engines (four crew each) at a fire station, two patrol cars (two each) at a
  police station, an ambulance at a clinic and two at a hospital. More can be bought, up to what a station holds.
- Wages ($450 a crew member a month) and upkeep come out on the first of the month, the subsidy comes in.
- The computer dispatches the nearest free units of the kind a call needs, plus an ambulance for a serious fire or
  crime. Response time is the street route from the station plus two minutes. A call nobody can answer waits; each
  day it waits it gets worse, and after three days it is lost.
- A call the computer handles comes back the next day, decided by who went, how fast and how bad it was.
- Caretaker keeps the money right; autopilot also leases a station of each kind it lacks and buys units when calls
  found nobody to send.

### A player running it

Three command styles: **Full command** (every call waits for the player, who picks who goes and runs every scene),
**Scene command** (the computer dispatches, the player runs every scene), and **Focus** (the computer handles
everything, the player takes the scenes they want). Three switches: **Pick responders** (the computer proposes,
the player has a day to change it), **Crews use their heads** (on: crews with no orders do the obvious job nearest
them) and **Drivers watch out** (on by default; off, vehicles drive into people in their way).

A scene is the call's patch of the city, 9 × 9 cells at four tiles a cell, run in quarter-second steps only when the
player's client asks (two steps every half second while it's open), so it stays as deterministic as the city: every
step and order is a logged command and all chance comes from the scene's own seeded generator. The player selects
crews and vehicles, sends them somewhere (they find their way) or draws a path they follow exactly, and orders hose,
treat, warn, arrest, taser or shoot. Nothing aims for the player: a shot hits the first person on the line, crew and
bystanders included, and buildings block it. A scene left alone for three days, or still going after ten minutes, is
finished by the crews; so are open scenes when the player hands the seat to the computer.

Each scene gets a report card: 60, plus 15 for each life saved and 20 for a good outcome, less 25 for each life lost,
40 for each person the crews hurt, 30 for each friendly-fire hit and each use of unauthorised force (shooting an
unarmed or unwarned suspect, tasing one without a warning), half the property lost in percent, and 3 a minute over
eight minutes' response. Cards move the service's respect, and respect and outcomes make the service quality the
city's authority rating reads. A vehicle that hit someone is off the road for 30 days and the city hears about it.

The alpha supports the plain bad choices: ignoring calls, sending the wrong crews, driving into people, shooting the
wrong person. The consent-gated crooked ones (slow-walking a district, rackets, looking the other way, overtime
arson, strikes) and SWAT teams, breaching and hostage scenes come in the emergency services beta.

### Clients

- Web: a 🚒 Dispatch button opens the board over the map (calls with tick-and-send dispatch, units, report cards,
  settings, log) and the scene canvas; a scene of the player's opens by itself.
- Godot: the same board as the "dispatch" dialog (`EmergencyModel` in ToyCity.ClientModel), with `SceneCanvas` beside
  it.
- A side game's commands no longer resend the civic state to every client, since a scene sends several a second.

### Tests

- `EmergencyTests` (sim): new stations come with units and the computer answers the city's calls; escapes and arrests
  move a district's heat, once each; an unanswered call in full command gets worse and is lost; a player's scene holds
  the fire until it ends; a driver told not to watch out hits someone and the unit is suspended; a shot hits the
  first person on the line and force is judged; a scene played through commands saves, loads and replays exactly;
  the service on another process ends in the same state.
- `EmergencyClientTests`: against the real server, a player takes the service and changes the style and switches; a
  scene opens by itself and the pointer selects, moves, hoses and draws a path.

## Part 8: the dispute ladder

A lease that one side wants changed no longer has only two moves (new terms after notice, or eviction). Each lease
panel shows a ladder, gentlest first, with whether each rung is open now and the rule behind it:

| Rung | Who | Rule |
| --- | --- | --- |
| Negotiate | either side | New terms start the day the other side accepts. |
| Buy out | either side asks, the city pays | The lease ends the day it's accepted; the city pays the amount, which reaches the game with its next bills. |
| Relocate | either side | The lease moves to a free lot of the same kind with its terms and jobs; the city may offer moving costs. Lines through the old stop are redrawn. |
| Impose terms | the mayor | The old way: new terms after the lease's notice. Open while the mayor plays the city. |
| Evict | the mayor | Open once the lease has been in bad standing past its cure period, or with hostile evictions allowed by every player (notice and compensation). Otherwise closed, with what stands in the way. |

### Offers

- `OfferLeaseAction(lease, fromOperator, kind, terms, amount, toBuildingId, counterOf)` and
  `AnswerOfferAction(offer, accept)`. The operator side is the side game seat's owner; the city side is the mayor, or a
  deputy holding Transit (stops and depots) or Services (stations). Anyone else is refused.
- One offer is open on a lease at a time. A counter comes from the other side and closes the offer it answers. The
  maker can withdraw an offer but not accept it. Offers lapse after 30 days.
- A side the computer plays answers the next day. The city on automation takes terms no worse for it, pays a buyout of
  at most what an eviction would cost, and doesn't pay for moves. A side game on automation takes terms no dearer, a
  buyout of at least an eviction's compensation plus three months' revenue, and a move with $1,000 moving costs paid.
- An accepted buyout or move the city can't afford fails, and says so.
- Offers are part of the save (inside the lease section; the format version is unchanged), the state hash and the
  replay log.

### Clients

- Web and Godot lease panels show the ladder, the offer on the table with Accept / Decline / Counter (or Withdraw for
  its maker), the last few decided offers with how they ended, and buttons to offer terms, a buyout or a move. The
  server sends the free lots a lease could move to.

### Tests

- `DisputeTests` (sim): agreed terms start at once and a counter replaces the offer; only the two sides deal, a transit
  deputy counts, one offer at a time, the maker withdraws; the computer takes a buyout worth more than staying; a move
  keeps the lease and its jobs; the ladder's rungs and their rules, and offers lapse; offers save, load and replay.
- `DisputeClientTests`: against the real server, the mayor leases a stop from its panel, the computer turns down a
  buyout for nothing and takes a paid move.

## Part 9: de-linking

When two players can't settle a dispute, they part ways for good. The player who goes takes a copy of the city as it
is at that moment; the city they leave keeps its name, its log and everyone else.

### Who may de-link whom

- `DelinkAction(player)`. A player may always leave. The mayor may cut a player loose on the same grounds as an
  eviction: one of their leases is past its cure period, or hostile evictions are on between the two. A player who
  holds no lots (a deputy, a spectator with settings) may be cut loose at any time. The mayor can't de-link from
  their own city.
- The dispute ladder gains a last rung, De-link, and the civic state lists every player with a part in the city with
  whether the mayor may cut them loose and the rule behind it.

### The city left behind

- Every lease of the player's side games ends. What they owed is written off; compensation already owed for a
  hostile eviction under way is paid. Open offers on those leases are withdrawn.
- Their seats go to nobody, on caretaker, so the companies keep running for the computer. Their consent settings,
  portfolios and pacts are dropped.
- The de-link is recorded (who, who did it, leases ended, written off, paid) and starts a 90-day cooldown: the player
  can't claim a seat or be made a deputy while the same mayor runs the city.

### The copy

- Built from the snapshot just before the de-link is settled, with the log up to that moment and one more record,
  `Fork`, that makes it the player's: they are its mayor, their seats and leases are as they were, everyone else's
  seats go to the computer, other players' settings and the council go, and it's named "City (player)".
- The copy replays from its own log (the home city's actions, then the fork), so both cities replay exactly.
- The server keeps the copy in its saves ("City player copy tick") and sends it to the player as `DelinkCopy`. The
  web client downloads it as a file; Godot writes it to `user://copies/`.

### Clients

- The seat dialog has a "Parting ways" section: for the mayor, each player with De-link (or why not); for anyone
  else, "Leave with a copy…"; and the past de-links with their settlement and cooldown. The lease panel has the same
  button under the ladder. Both ask for confirmation and say what happens.

### Tests

- `DelinkTests` (sim): a player who leaves takes a copy and the city keeps its own, and both run, save and load; both
  cities replay from their logs; the mayor cuts a player loose only on an eviction's grounds, writing off what's owed;
  the cooldown blocks claims and portfolios until it's over.
- `DelinkClientTests`: on a dedicated server a player claims the bus company, leaves from the seat dialog, gets a
  copy they are mayor of over the wire, and can't take the company back during the cooldown.
