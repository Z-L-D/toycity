# ToyCity

An open-source city simulator in the spirit of SimCity 4: quirky, readable and moddable. The
simulation runs as a headless, authoritative server; front ends only draw it and send actions.
Later phases link side games (transport, emergency services, hospital, zoo, theme park, farm,
life sim) to lots in the city for live joined play.

This repository is at **Beta 1: the Godot client.** A city grows from paths, zones and services, every
lot can tell you why it is the way it is, the city seat can be left to the computer, and a bus company
and the emergency services run as side games on leased lots. The Godot client in `clients/godot` is the way to play; the web
client stays as the debug and admin client.

## Quick start

You need the .NET 10 SDK and Node 20 or newer.

```sh
# 1. Build the web client once
cd clients/web && npm install && npm run build && cd ../..

# 2. Run the server (it also serves the client)
dotnet run --project src/ToyCity.Server

# 3. Play: open clients/godot in Godot 4.7.2 .NET and press F5
#    (or open http://localhost:5180 for the web debug and admin client)
```

See [docs/BETA1.md](docs/BETA1.md) for the Godot client's controls, command-line options and frame budget.

To host a city for several players, run `dotnet run --project src/ToyCity.Server -c Release -- --dedicated`; players sign in and `host.json` beside the saves sets who may join. A side game can run in its own process or on another machine with `dotnet run --project src/ToyCity.SideGameHost -c Release -- --port 5190`, listed under `sideGames` in `host.json`. See [docs/BETA2.md](docs/BETA2.md).

The server starts a 128 × 128 map called Toyville with a small working town so there is
something to look at. Use **New city** for a blank map.

For client development, run the server as above and `npm run dev` in `clients/web`, then open
http://localhost:5173 (Vite proxies the WebSocket to port 5180). To point the client at another
server, add `?server=ws://host:port/ws` to the URL.

### Web client controls

| Key | Tool |
| --- | --- |
| Q | Query: click anything to see why it is the way it is |
| B | Bulldoze (in underground view it removes water pipes) |
| R | Build path (pick one of the 15 path types in the toolbar) |
| Z / X / C | Zone residential / commercial / industrial; V cycles density |
| D | De-zone |
| P / W | Power line / water pipe |
| E | Place a civic building (pick it in the toolbar) |
| T | Terraform (raise, lower, level) |
| U | Underground view |
| Space, 1–3 | Pause, speeds |
| Shift while dragging | Draw the vertical leg first |
| Wheel, right-drag, arrows | Zoom and pan |

Hints for the current tool are always shown in the lower-left corner.

## Tests and tools

```sh
dotnet test                                                 # unit and gate tests
dotnet run --project tools/ToyCity.Bench -c Release -- 10   # headless 10-year run with yearly report
TOYCITY_BIG=1 dotnet run --project tools/ToyCity.Bench -c Release -- 5 256   # stress: many towns on a large map
TOYCITY_WHY=1 dotnet run --project tools/ToyCity.Bench -c Release -- 2       # print why-reports for stuck lots
```

## Layout

| Path | What it is |
| --- | --- |
| `protocol/toycity.proto` | Wire and save format, shared by every front end |
| `content/` | Path profiles and building types (data, not code) |
| `src/ToyCity.Sim` | Deterministic simulation core: no I/O, threads or clocks |
| `src/ToyCity.Protocol` | Generated protobuf types and sim ↔ wire mapping, saves |
| `src/ToyCity.Server` | Fixed-tick host, WebSocket endpoint, static hosting |
| `clients/godot` | The Godot 4.7.2 .NET client: the way to play |
| `src/ToyCity.ClientModel` | What a .NET client knows that isn't drawing: the city mirror, tools, panels, the gate run |
| `clients/web` | 2D web client (SimCity 1 style): the debug and admin client since Beta 1 |
| `tests/ToyCity.Sim.Tests` | Tests, including determinism, replay and save gates |
| `tools/ToyCity.Bench` | Headless benchmark and diagnostics |

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for how the pieces fit and
[docs/ALPHA1.md](docs/ALPHA1.md) for the Alpha 1 rules, numbers and known limits.

## License

GNU General Public License v3.0. See [LICENSE](LICENSE).
