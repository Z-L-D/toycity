# ToyCity

An open-source city simulator in the spirit of SimCity 4: quirky, readable and moddable. The
simulation runs as a headless, authoritative server; front ends only draw it and send actions.
Later phases link side games (transport, emergency services, hospital, zoo, theme park, farm,
life sim) to lots in the city for live joined play.

This repository is at **Alpha 1: a city that grows.** You can lay out paths, zone, power and
water a town, set taxes and funding, and watch it grow, decline and recover. Every lot can tell
you why it is the way it is.

## Quick start

You need the .NET 10 SDK and Node 20 or newer.

```sh
# 1. Build the web client once
cd clients/web && npm install && npm run build && cd ../..

# 2. Run the server (it also serves the client)
dotnet run --project src/ToyCity.Server

# 3. Open http://localhost:5180
```

The server starts a 128 × 128 map called Toyville with a small working town so there is
something to look at. Use **New city** for a blank map.

For client development, run the server as above and `npm run dev` in `clients/web`, then open
http://localhost:5173 (Vite proxies the WebSocket to port 5180). To point the client at another
server, add `?server=ws://host:port/ws` to the URL.

### Controls

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
| `clients/web` | 2D web client (SimCity 1 style); also the debug and admin client |
| `tests/ToyCity.Sim.Tests` | Tests, including determinism, replay and save gates |
| `tools/ToyCity.Bench` | Headless benchmark and diagnostics |

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for how the pieces fit and
[docs/ALPHA1.md](docs/ALPHA1.md) for the Alpha 1 rules, numbers and known limits.

## License

Not chosen yet. ToyCity is planned as fully open source, with no paid add-ons or editions.
