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
