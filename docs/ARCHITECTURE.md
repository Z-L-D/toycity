# ToyCity architecture

ToyCity is a city simulator in the spirit of SimCity 4. The simulation runs as a headless,
authoritative server; front ends only draw state and send actions. The design brief and
roadmap live outside the repo (see README).

## Layout

| Path | What it is |
| --- | --- |
| `protocol/toycity.proto` | The wire and save format (Protocol Buffers). Single source of truth for every front end. |
| `content/` | Data files: path profiles and building types. Behaviour lives in data where possible. |
| `src/ToyCity.Sim` | The simulation core. No I/O, no threads, no clocks. Deterministic. |
| `src/ToyCity.Protocol` | Generated protobuf types plus mapping between the sim and the wire/save format. |
| `src/ToyCity.Server` | Host: fixed-tick loop, WebSocket endpoint, static file hosting for the web client. |
| `clients/web` | The 2D web client (SimCity 1 style): the debug and admin client. The Godot client (`clients/godot`) is the way to play from Beta 1. |
| `tests/ToyCity.Sim.Tests` | Unit and gate tests (determinism, save/load, growth, why-queries). |
| `tools/ToyCity.Bench` | Headless benchmark harness. |

## Simulation rules

- **One tick is one in-game day.** A month is 30 days and a year is 12 months. Monthly systems
  run on the first day of each month.
- **Determinism.** All randomness comes from the world's seeded `Rng`. Iteration is always in
  index order. Ties in path searches break on cell index. Given the same seed, content and
  action log, two runs produce identical state hashes. Money and most quantities are integers.
- **Actions are applied at tick boundaries.** The server queues client actions and the sim
  applies them in arrival order at the start of the next tick. The applied actions are logged
  with their tick, which is what saves and replays use.
- **Staggered work.** Expensive per-entity work (growth checks, job matching) is spread over the
  30 days of a month by index, so tick cost stays flat.
- **One rule set, two uses.** The requirement checks that decide whether a lot grows are the same
  code the "why" query reports. If the why-panel says a lot is fine, the growth code agrees.

## Data model

The map is a grid of 16 m cells stored as structure-of-arrays (`int[]`, `byte[]` per layer,
indexed `y * width + x`). Buildings live in a dense store indexed by id. Derived layers (power,
water, coverage, pollution, land value, desirability, traffic) are recomputed by their systems.

## Network protocol (v0)

WebSocket at `/ws`, binary frames, one protobuf `ClientMessage` or `ServerMessage` per frame.
On connect the server sends `Welcome` and a `FullState`; afterwards it sends a `TickDelta` each
tick with changed cells, changed buildings and city stats. Queries (`why`, `inspect`, `overlay`)
get direct replies.

## Saves

A save is a `SaveGame` protobuf: format version, content ids, the full world state, and the
action log. Loading a save and continuing produces the same state as never having saved.
