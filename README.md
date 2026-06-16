# Puck

A Vulkan engine written from scratch in C# (.NET 10): no engine framework, no
binding library, compute-driven rendering, and
signed distance fields all the way down.

![Avatar showcase capture](docs/images/sample-avatar.png)

## What it does

- **SDF-native rendering.** Avatars, terrain, and accessories are signed distance
  fields marched in compute shaders; avatars are data-driven from with rigged primitives and hot reload.
- **Hardware ray tracing with a verified fallback.** A `VK_KHR_ray_query` path
  shares its shader body with the tile-culling tier; a cull-parity debug mode
  flags any pixel where the two disagree.
- **Determinism as a regression net.** Capture runs are tick-exact and produce
  identical per-frame pixel hashes.
- **Self-validating shaders.** Toggleable debug view modes (depth, cost
  heatmaps, NaN/bounds/beam invariant checks), GPU-accumulated counters, CPU↔GPU
  math parity probes, and a telemetry log diffed against baselines.

## Quick start

Requires Windows, .NET 10, a Vulkan GPU, and `glslc` on `PATH`.

## Layout

- [src/Puck](src/Puck) — the engine library
- [tools](tools) — formatting, validation, capture-parity gates, checked-in baselines

Standing on many shoulders — see [ACKNOWLEDGMENTS.md](ACKNOWLEDGMENTS.md).

## License

Puck is **source-available and dual-licensed** — not open source. Noncommercial
use (including by individuals, schools, universities, and government bodies) is free
under the [PolyForm Noncommercial License 1.0.0](LICENSE.md); commercial use requires a
paid license. See [LICENSING.md](LICENSING.md) for who needs what and how to obtain a
commercial license.
