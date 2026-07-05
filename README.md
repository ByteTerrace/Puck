# Puck

An everything-as-data game engine written from scratch in C# (.NET 10): no
engine framework, no binding library, compute-driven rendering, signed
distance fields all the way down — running at functional parity on **both**
Vulkan and Direct3D 12.

![Avatar showcase capture](docs/images/sample-avatar.png)

## What it does

- **Everything as data.** A run is one versioned JSON document
  (`puck.run.v1`, [schema](schema/run.schema.json)) describing the window,
  scene, viewports, and composition graph; the demo's CLI flags are just
  synthesized documents. Annotated examples in [docs/examples](docs/examples).
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
- **Self-validating.** `Puck.Post` is a 29-stage power-on self-test (CPU
  pre-flight → GPU smoke → cross-backend parity → live subsystems); the
  experimental emulator cores carry their own mirrored batteries.

The full inventory — including controller input, live-camera and desktop
content sources, VRR present timing, the Game Boy / GBA emulator cores, and
the bare-metal UEFI runtime — lives in the
**[capability catalog](docs/capability-catalog.md)**.

## Quick start

Requires Windows, .NET 10, a Vulkan GPU, and `dxc` on `PATH` (ships with the
Vulkan SDK and the Windows SDK).

```powershell
# The overworld — the demo, and the default with no flags at all:
dotnet run --project src/Puck.Demo -c Release

# Is the engine healthy on this machine?
dotnet run --project src/Puck.Post -c Release
```

## Layout

- [src](src) — the engine, split into focused `Puck.*` projects; see the
  [project map](docs/project-map.md)
- [experimental](experimental) — the GamingBrick emulator cores (GB/GBC, GBA)
  and the bare-metal runtime
- [docs](docs/README.md) — capability catalog, project map,
  [guide for contributors and agents](docs/agent-guide.md), design records
- [schema](schema) — the generated run-document JSON Schema
- [tools](tools) — formatting, validation, checked-in baselines

Standing on many shoulders — see [ACKNOWLEDGMENTS.md](ACKNOWLEDGMENTS.md).

## License

Puck is **source-available and dual-licensed** — not open source. Noncommercial
use (including by individuals, schools, universities, and government bodies) is free
under the [PolyForm Noncommercial License 1.0.0](LICENSE.md); commercial use requires a
paid license. See [LICENSING.md](LICENSING.md) for who needs what and how to obtain a
commercial license.
