# Puck

An everything-as-data game engine written from scratch in C# (.NET 10): no
engine framework, no binding library, compute-driven rendering, signed
distance fields all the way down — running at functional parity on **both**
Vulkan and Direct3D 12.

![Avatar showcase capture](docs/images/sample-avatar.png)

## What it does

- **Everything as data.** A run is one versioned JSON document —
  `puck.world.def.v1` describes the world, its screens, its entities, and its
  composition; who is seated is an ordinary owned instance of the same
  document, seeded from the world. One thick validator gates every instance
  before anything swaps in.
- **SDF-native rendering.** Scenes are programs for a small SDF virtual
  machine marched in compute shaders, with GPU-driven culling and a hardware
  ray-query tier (Vulkan ray query + DXR 1.1) sharing one HLSL source.
- **Two backends, one seam.** The same showcase runs on Vulkan or Direct3D 12,
  including zero-copy sharing of GPU surfaces *across* the two APIs in either
  direction, runtime backend hot-switching, and a differential fuzzer that
  holds the backends bit-equivalent.
- **Determinism as a feature.** Fixed-point math, per-tick command snapshots,
  and record/replay are engine primitives; capture runs produce identical
  per-frame pixel hashes.
- **Self-validating.** `Puck.Post` is a fail-isolated power-on self-test (CPU
  pre-flight → GPU smoke → cross-backend parity → live subsystems; run it to
  see the current stage count in its own summary line); the experimental
  emulator cores carry their own mirrored batteries.

There is deliberately no capability catalog: the one that existed asserted a
per-capability verification status that stopped being true when the engine's
self-test was quarantined. Ask the code, or run `Puck.World`.

## Quick start

Requires Windows, .NET 10, `dxc` on `PATH`, and a supported GPU with Vulkan
1.3 or Direct3D 12 Shader Model 6.6. DXC ships with the Vulkan SDK and Windows
SDK.

```powershell
# The overworld — the game, and the default with no flags at all:
dotnet run --project src/Puck.World -c Release

# Bound the run and drive it over stdin (the console is the control plane):
dotnet run --project src/Puck.World -c Release -- --exit-after-seconds 10
```

## Layout

- [src](src) — the engine and the game, split into focused `Puck.*` projects;
  see the [project map](docs/project-map.md)
- [experimental](experimental) — the bare-metal runtime, and the quarantined
  trees (`Puck.Demo`, `Puck.Post`, `tools`, `scripts`): out of the build, read
  as prior art and never built or run, see
  [experimental/README.md](experimental/README.md)
- [docs](docs/README.md) — project map,
  [guide for contributors and agents](docs/agent-guide.md), and the handbooks

Standing on many shoulders — see [ACKNOWLEDGMENTS.md](ACKNOWLEDGMENTS.md).

## License

Puck is **source-available and dual-licensed** — not open source. Noncommercial
use (including by individuals, schools, universities, and government bodies) is free
under the [PolyForm Noncommercial License 1.0.0](LICENSE.md); commercial use requires a
paid license. See [LICENSING.md](LICENSING.md) for who needs what and how to obtain a
commercial license.
