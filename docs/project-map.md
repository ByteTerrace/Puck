# Puck project map

This map describes the current responsibility and dependency boundary of each
project. See [capability-catalog.md](capability-catalog.md) for the feature
inventory and [agent-guide.md](agent-guide.md) for verification procedures.

## Dependency rules

1. All production code lives in split `Puck.*` projects. The former
   `src/Puck` and `src/Puck.Avatars` monoliths are not part of the repository.
2. GPU API types remain inside their backend projects. Shared code depends on
   the neutral contracts in `Puck.Abstractions`.
3. `Puck.Launcher` is backend- and platform-neutral. A composition root
   registers windowing, presenters, and content.
4. `Puck.HumbleGamingBrick` and `Puck.AdvancedGamingBrick` split internally: the
   core emulator depends only on leaf contract/data projects (`Puck.Maths` for
   deterministic numerics, `Puck.Snapshots` for the shared state-serialization
   substrate) — never on shared substrate, backends, or composition roots. Each
   project's `Hosting/` folder carries its screen-machine engine adapter
   (`GamingBrickEngine`/`AdvancedGamingBrickEngine`,
   `MachineHost`/`AdvancedMachineHost`) — the one place the project touches
   shared substrate, bridging to `Puck.Hosting`'s `QueuedMachineWorker` and the
   neutral screen-machine contracts in `Puck.Abstractions`. `Puck.Demo` and
   `Puck.World` consume both cores through their `Hosting/` adapters or through
   composition-root debug hosts.
5. `Puck.Demo` and `Puck.World` are composition roots. Neither defines shared
   engine contracts.

## Stability

- **Stable:** the Vulkan and Direct3D 12 backend implementations,
  `Puck.Maths`, and `Puck.Assets`.
- **Mostly settled:** the neutral GPU and hosting seams, command snapshots,
  input transport architecture, the SDF instruction contract, and the run
  document validation funnel. Changes require contract-level verification.
- **Fluid:** `Puck.Demo`, `Puck.World`, document additions, authoring tools,
  and emulator integration. Treat these as consumers, not architectural
  precedent.

## Layering

```text
Composition roots     Puck.Demo                 Puck.World
Validation            Puck.Post                 emulator .Post projects
Engine services       Puck.Launcher  Puck.Scene  Puck.SdfVm  Puck.Bench
                      Puck.Text      Puck.Capture Puck.Recording
                      Puck.HumbleGamingBrick    Puck.AdvancedGamingBrick
Presentation          Puck.Vulkan.Presentation  Puck.DirectX.Presentation
Backends              Puck.Vulkan               Puck.DirectX
Shared substrate      Puck.Hosting  Puck.Commands  Puck.Input  Puck.Platform
                      Puck.Compositing  Puck.Cameras  Puck.Shaders
                      Puck.Scripting
Leaf contracts/data   Puck.Abstractions  Puck.Maths  Puck.Assets  Puck.Storage
                      Puck.Snapshots
```

Dependencies normally point downward. A same-row dependency is acceptable
when it does not introduce a backend or composition-root dependency. Each
GamingBrick project's `Hosting/` folder is the deliberate internal exception:
it is the one place the core emulator's project touches shared substrate,
bridging to `Puck.Hosting` and the neutral screen-machine contracts in
`Puck.Abstractions`.

## Contracts and data

| Project | Responsibility |
|---|---|
| `Puck.Abstractions` | Backend-neutral GPU, presentation, capture, timing, machine, lighting, and window contracts. It has no Puck dependencies and exposes no platform-native types. |
| `Puck.Maths` | Deterministic fixed-point numerics, world coordinates, vectors, and integer algorithms used by authoritative simulation. |
| `Puck.Assets` | Content-addressed byte sources, hashes, and a fixed-capacity LRU cache. It identifies and moves bytes; it does not decode them. |
| `Puck.Storage` | Local and Azure object-blob stores, JSON and profile-document helpers, and a version-token seam (opaque read token + optional if-match write) for conditional overwrites. |
| `Puck.Snapshots` | The shared deterministic state-serialization substrate both GamingBrick cores build snapshots on: `StateWriter`/`StateReader` (little-endian widths + `WriteBlock<T>` memcpy + `Reset` reuse), `SnapshotSection`, the FNV-1a `StateFingerprint`, `ISnapshotable`, the flat `SnapshotImage`, and the `SnapshotDivergence` localizer. Per-core snapshot identity fields and component orders stay in each core. |

## Shared substrate

| Project | Responsibility |
|---|---|
| `Puck.Hosting` | Recursive `IRenderNode` hosting, capability propagation, terminal ownership, fixed-step simulation context, frame timing, cross-thread publish buffers, and the machine-neutral queued-host substrate (`QueuedMachineWorker` + `IQueuedMachineCore` adapter: worker thread, bounded FIFO with backpressure, triple-buffer publication with the upload lease, native-frame-keyed save-flush debounce, and the vectorized framebuffer repack; `QueuedHostContractProbe` proves its observable contract for both cores' batteries). |
| `Puck.Commands` | Typed commands, deterministic per-tick `CommandSnapshot`s, recordings, console dispatch, binding profiles and sessions, feature switches, intent sources, and determinism helpers. |
| `Puck.Input` | Controller discovery and protocols, hotplug, routing arbitration, HID parsing, IMU fusion, haptics, and LampArray bind legends. Platform transports are injected. |
| `Puck.Platform` | Win32 windowing, HID and controller transports, Media Foundation camera capture, Windows Graphics Capture feeds, the Media Foundation hardware video-encoder ladder (AV1→H.264) and WASAPI loopback/microphone capture sources behind `AddRecordingPlatform` (`Puck.Recording`'s Windows backend), and generated native interop. |
| `Puck.Cameras` | Immutable virtual-camera snapshots and projection math. |
| `Puck.Shaders` | Compiled shader-bytecode loading, format detection, and validation. |
| `Puck.Compositing` | Backend-neutral draw-command replay and shader-asset pipeline identity. |
| `Puck.Scripting` | Deterministic, fuel-metered WASM addons that convert fixed-point tick input into neutral virtual-pad commands. |

## GPU backends and presentation

| Project | Responsibility |
|---|---|
| `Puck.Vulkan` | Vulkan bindings, device and resource factories, command recording, sharing, and synchronization. It contains no windowing or shader compiler. |
| `Puck.Vulkan.Presentation` | Vulkan presenter and compute-service adapters for the neutral hosting contracts. |
| `Puck.DirectX` | Direct3D 12 and DXGI device, resource, command, sharing, and synchronization implementation. |
| `Puck.DirectX.Presentation` | Direct3D 12 presenter and compute-service adapters. |

Backend parity is summarized in
[feature-parity-summary.md](feature-parity-summary.md) and detailed in
[feature-parity-table.md](feature-parity-table.md).

## Engine services

| Project | Responsibility |
|---|---|
| `Puck.Launcher` | Generic application host: window loop, command pump, fixed-step accumulator, terminal control, genlock, and backend switching. Composition roots register platform and backend services. |
| `Puck.Scene` | The `puck.run.v1` object model, parser, validator, graph documents, fuzzing and validation roots, and JSON Schema export. |
| `Puck.SdfVm` | SDF program model and builder, C#↔HLSL instruction contract, world renderer, frame sources, render assembly, debug tools, composition and anchor seams, camera views, and deterministic world queries. |
| `Puck.Text` | Font-atlas models, text layout, and deterministic coverage-to-distance atlas generation. It is render-agnostic. |
| `Puck.Capture` | Frame observers, hashing, and dependency-free PNG encoding. GPU readback occurs upstream. |
| `Puck.Recording` | The `puck.recording.v1` capture graph: frame source → data-defined overlay compositor → encoder ladder → hand-rolled Matroska/WebM muxer, plus the managed-Opus (Concentus) audio lane and the `RecordingSession` that implements the engine's `ICaptureSink`. It defines the recording document, muxer, overlays, and session; the Media Foundation video-encoder ladder and WASAPI audio sources are the platform backend. Depends only on `Puck.Abstractions`. |
| `Puck.Bench` | Content-blind benchmark orchestration, timing collection, feature-switch sweeps, scoring, and versioned reports. Content is registered through attach seams. |
| `Puck.HumbleGamingBrick` | Deterministic shared GB/GBC/AGB-costume SM83 machine (snapshots, forks, cartridges, link cable, PPU, APU, peripherals). Its `Hosting/` folder carries the thin adapter from the neutral screen-machine contract to the core over `Puck.Hosting`'s `QueuedMachineWorker`: an `IQueuedMachineCore` (pad mapping, KEY1-aware tick conversion, framebuffer, save persistence) plus the host shell and work-RAM peek. Inherits the substrate's queued/backpressure behavior. |
| `Puck.AdvancedGamingBrick` | Deterministic GBA-native ARM7TDMI machine (cycle-level bus, DMA, timers, PPU, APU, cartridges, snapshots, link cable). Its `Hosting/` folder carries the thin adapter from the neutral screen-machine contract to the core over `Puck.Hosting`'s `QueuedMachineWorker`: an `IQueuedMachineCore` (KEYINPUT mapping, exact tick conversion, framebuffer, save persistence, direct boot) plus optional explicit BIOS images and the host shell. |

## Composition roots and validation

| Project | Responsibility |
|---|---|
| `Puck.Demo` | Greenfield overworld and creator experience, run-document composition, console control plane, ROM forge, and developer proof surfaces. Verify changes by running the demo. |
| `Puck.World` | Document-driven (`puck.world.def.v1`, three checked-in worlds, `--world`) network-shaped local multiplayer game host: fixed-point player state, a runtime mutation/journal/undo protocol vocabulary, principals + capability grants (addons included), a per-user player document (`puck.world.player.v1`) with bindings layered onto the `Puck.Commands` stack, cloud-ready storage (local-proven, cloud wiring deferred), session write-back, native self-recording (`puck.recording.v1`, `--recording`, `capture.*` verbs), and SDF world rendering. Verify game behavior by running it. |
| `Puck.Post` | Fail-isolated engine power-on self-test across CPU, same-device GPU, cross-backend, and live-subsystem tiers. It does not gate greenfield game behavior. |
| `Puck.HumbleGamingBrick.Post` | Humble core conformance, determinism, reference-ROM, save, and cross-generation link battery. |
| `Puck.AdvancedGamingBrick.Post` | Advanced core conformance, determinism, commercial-ROM, link, co-simulation, and diagnostic tooling. |

## Experimental projects

`experimental/` holds only `Puck.BareMetal`; the GamingBrick cores live in
`src/` alongside the rest of the split projects.

| Project | Responsibility |
|---|---|
| `Puck.BareMetal` | Freestanding Native AOT runtime, UEFI kernels, native experiments, and direct hardware bring-up. |

## Repository data and tools

| Path | Purpose |
|---|---|
| `schema/run.schema.json` | Generated JSON Schema for `puck.run.v1`. |
| `docs/examples/` | Valid and negative document examples plus console scripts. |
| `tools/` | Formatting, batteries, generation, and analysis utilities. |
| `.agents/skills/` | Current factual and procedural agent references for repository-specific work. |
