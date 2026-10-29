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
