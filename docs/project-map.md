# Puck project map

What each project is for, how they layer, and the dependency rules that keep
the architecture honest. Companion docs: [capability-catalog.md](capability-catalog.md)
(what the engine can do) and [agent-guide.md](agent-guide.md) (how to work on it).

## Ground rules

1. **No monolith.** `src/Puck` and `src/Puck.Avatars` are inspiration-only
   and live in git history, not the tree. Never reintroduce references to
   those paths; all real functionality lives in the split `Puck.*` projects
   below.
2. **Backends are leaves, not roots.** Nothing outside a backend pair may
   reference Vulkan or DirectX types; everything flows through the neutral
   seams in `Puck.Abstractions`.
3. **The launcher is generic.** `Puck.Launcher` imports nothing platform- or
   backend-specific; the composition root (today `Puck.Demo`) wires
   `AddPlatformWindowing` and named `SurfacePresenterDescriptor`s.
4. **Experimental stays decoupled.** Nothing under `experimental/` is
   referenced by `src/Puck.*` (and vice versa) until it graduates.

## Stability

Not everything here is equally settled. Treat these tiers as the honest
calcification map — stable things deserve caution before reshaping; fluid
things should not be treated as load-bearing precedent.

**Stable** — reshape only with strong cause:

- `Puck.Vulkan` and `Puck.DirectX` low-level bindings and internal layering
  (Bindings → Apis → Factories → Interop, mirrored across both). Zero open
  TODO markers; the parity table classifies every remaining backend delta as
  *by design*; hardened by Post Tiers B–D plus the 64-seed differential
  fuzzer. The hardware campaigns (device-lost, VRR, zero-copy) fit *within*
  this architecture without reshaping it.
- `Puck.Maths`, `Puck.Assets` — small, contract-heavy, consumer-proven.

**Mostly settled** — architecture fixed; remaining work is additive:

- `Puck.Input` — the transport seam, parsers, coalescer, and capture flow are
  hardware-proven across three controller families. What remains (Switch/Xbox
  Bluetooth handshakes, Linux hidraw transport, per-device calibration) adds
  transports and data, not architecture.
- `Puck.Abstractions` — namespaces and enum vocabularies are settled
  (project split rejected — do not relitigate), **but** a deliberate deferred
  backlog will still rename/reshape parts of the GPU seam (e.g. barrier-enum
  renames, a graphics-pipeline descriptor, `IGpuRenderTarget` is
  Vulkan-shaped). Backend *implementations* are stable; some seam
  *names* are not frozen yet.
- `Puck.Commands` — the deterministic snapshot path (`InputRouter`,
  `CommandSnapshot`, recording) is the settled direction, and it coexists
  with the older registry path (`GamepadInputSource` →
  `BindingCommandSource` → `CommandRegistry`), which the Input README labels
  *legacy*. Absorbing or blessing that duality is an open decision; the
  recording layer also self-describes as the seed of a future `Puck.Replay`
  project.

**Fluid** — expect churn; don't calcify:

- **`Puck.Demo` is GREENFIELD — the playground.** It is expected to churn and be
  rewritten; treat nothing in it as settled precedent. Demo changes are NOT
  engine changes: verify them by RUNNING the demo, never by gating them, and
  never promote a demo feature into Post unless the user explicitly asks
  ([agent-guide.md](agent-guide.md#anti-calcification-doctrine) rule 5).
- `Puck.Scene` document surface (additive-by-design via `Extensions`), the
  viewport/content-source typing (capture sources not yet in the document), and
  everything under `experimental/`.

## Layering

```
                        ┌─────────────────────────────┐
   composition root     │          Puck.Demo          │  CLI ⇒ run document ⇒ node graph
                        └──────┬───────────┬──────────┘
                               │           │
   validation           Puck.Post     Puck.Scene         puck.run.v1 documents
                               │           │
                        ┌──────┴───────────┴──────────┐
   engine services      │ Puck.Launcher   Puck.SdfVm  │  run loop · SDF VM · text
                        │ Puck.Text       Puck.Capture│
                        └──────┬───────────┬──────────┘
                               │           │
   presentation         Puck.Vulkan.Presentation   Puck.DirectX.Presentation
   backends                    │                            │
                          Puck.Vulkan                  Puck.DirectX
                               │                            │
                        ┌──────┴────────────────────────────┴──────┐
   shared substrate     │ Puck.Hosting  Puck.Compositing           │
                        │ Puck.Commands Puck.Input  Puck.Cameras   │
                        │ Puck.Shaders  Puck.Assets Puck.Platform  │
                        └──────────────────┬───────────────────────┘
                                           │
   leaves (no Puck deps)     Puck.Abstractions · Puck.Maths · Puck.Storage
```

(Arrows point downward: a project may depend on anything below its row.)

## `src/` projects

### Contracts and numerics (leaf layer — no Puck dependencies)

| Project | Purpose |
|---|---|
| `Puck.Abstractions` | The neutral seam every backend implements: GPU device/recorder/pipeline contracts, presentation lifecycle (`ISurfacePresenter`), windowing (`NativeSurfaceBinding`, `NativeDisplayKind`), capture contracts, allocator. Zero dependencies; no Windows-only types. |
| `Puck.Maths` | Deterministic numeric substrate: unsigned fixed-point (`UFixedQ4816`, `UFixedQ0016/32`), `WorldCoord3`/`FixedVector*`, branchless generic-integer routines. Culture/CPU-independent by contract. |
| `Puck.Storage` | Object blob storage (local file + Azure Blob behind one routing abstraction), JSON wrapper. |
| `Puck.Assets` | Content-addressed byte store, 64-bit content hashes, LRU cache. Moves and identifies bytes; never decodes. |

### Shared substrate

| Project | Purpose |
|---|---|
| `Puck.Hosting` | The recursive node tree: every level implements `IRenderNode` (produce a `Surface`, host children, `OnDeviceLost`). Dual capability model — *inherited* capabilities (device context) flow to all descendants, *held* capabilities (terminal control, input focus) go to one holder. Engine tick clocks. |
| `Puck.Commands` | The command system: every input modality (keyboard, gamepad, console text, AI, replay, network) becomes typed named commands. Deterministic per-tick `CommandSnapshot`s (interned ids, per-slot lanes) enable bit-identical replay. |
| `Puck.Input` | Controller input: HID protocols for Switch Pro / Xbox / DualSense, hotplug, IMU fusion, haptics, sub-frame timing. OS transports are injected from `Puck.Platform`. **Read [its README](../src/Puck.Input/README.md) before touching it** — it is the handoff doc. |
| `Puck.Cameras` | Tiny virtual-camera abstraction (`ICamera` → immutable `CameraSnapshot` basis+projection). Pure math. |
| `Puck.Shaders` | Compiled-bytecode loading/validation: sniffs SPIR-V vs DXBC/DXIL containers behind one `ShaderStageInfo` contract. |
| `Puck.Compositing` | Backend-neutral compositor: replays `GpuDrawCommand` lists through `IGpuCommandRecorder`; pipeline identity is the shader asset's content hash. |
| `Puck.Platform` | Platform implementations: native windowing (Win32 today; Wayland/Xcb/Vi stubs behind `NativeDisplayKind`), Media Foundation camera capture, Win32 HID/XInput/GameInput transports. CsWin32-generated interop, `[SupportedOSPlatform]`-guarded. |

### Backends (one pair per API)

| Project | Purpose |
|---|---|
| `Puck.Vulkan` | From-scratch, hand-mirrored Vulkan bindings (no generator, no wrapper library): Bindings → Apis → Factories → Interop layers. No windowing, no shader compilation. |
| `Puck.Vulkan.Presentation` | Wraps `Puck.Vulkan` into the `ISurfacePresenter`/compute-services contracts; compiles presentation HLSL to SPIR-V via DXC at build time. |
| `Puck.DirectX` | Direct3D 12 + DXGI via CsWin32 (AOT-friendly, unmarshaled COM). |
| `Puck.DirectX.Presentation` | The D3D12 mirror of Vulkan.Presentation: presenter, command-list recorder, compute services. |

Both backends are at functional parity across the showcase path — see
[feature-parity-summary.md](feature-parity-summary.md).

### Engine services

| Project | Purpose |
|---|---|
| `Puck.SdfVm` | The SDF engine, whole: the ISA (shapes/blends/warps/wallpaper folds), the program builder (with host-baked derived constants), frame sources, AND the GPU host that runs it — `SdfWorldEngine` (device-explicit pipeline core: beam cull → per-view render → split-screen composite; harness and live submission modes) and `SdfEngineNode` (the host-model `IRenderNode`). Backend-neutral by construction (only `Puck.Abstractions` seams). The C# half of a C#/HLSL pair — the HLSL (`sdf-vm` shaders, compiled to SPIR-V **and** DXIL) must stay in sync with the C# ISA. |
| `Puck.Scene` | Everything-as-data: `PuckRunDocument` (`puck.run.v1`) parse → validate → build; viewports and content sources; validation/fuzzing sections; JSON Schema export (`schema/run.schema.json`). Backend-neutral — resolving GPU services happens in the composition root. |
| `Puck.Text` | Font atlas model (MTSDF/MSDF/SDF/masks) + em-space text layout. Render-agnostic. |
| `Puck.Capture` | Frame capture sink: observer hooks (hashing) then dependency-free PNG encode. GPU readback happens upstream. |
| `Puck.Launcher` | The generic host: window run loop, terminal control (held-capability baton), command pump, `BackendSwitcher`, genlock registry. |

### Composition root and validation

| Project | Purpose |
|---|---|
| `Puck.Demo` | The game prototype / overworld composition root — Puck.Demo IS the overworld: a controller-driven player in a room with four bootable console cabinets (empty by default; North inserts/ejects, the Cycle bumper rotates the selected cart type); each boot lights a GamingBrick pane and the screen layout walks its staged split (fullscreen → side-by-side → big-top/two-bottom → 2×2 quad). The only composition root: parses CLI flags (or loads a `--run` document), synthesizes/loads a run document, resolves the node graph against real GPU services, wires platform windowing + presenters. `overworld` is the one game graph kind — the in-session experience per the unification contract ([overworld-demo-plan.md](overworld-demo-plan.md#the-unification-contract)); `--rom <path>` synthesizes an immersed `overworld` document (four cabinets, the booted ROM inserted) rather than a bare render target. The `world` graph kind (the document's scene + viewports run LIVE on the host backend, e.g. `--run docs/examples/world-*.json`) is a documented DEVELOPER/CI launch affordance for the example corpus — a document renderer, not a second product mode — sharing the same render assembly (`SdfWorldRenderBuilder`) for engineering reasons, not parity of intent. Deferred `world` affordances (cross-backend `produce`, the `child` boolean, `live-camera` sources) are pre-flighted rejections with attributed errors, exit 2. The demo is a game, not a test suite — verification lives in Puck.Post; the demo keeps exactly one self-gate, `--validate-overworld`, because Puck.Post cannot reference the demo. |
| `Puck.Post` | The engine's power-on self-test: fail-isolated stages across Tier A (CPU pre-flight) / B (same-device GPU) / C (cross-backend) / D (live subsystems) — run it for the current stage count in its own summary line, don't trust a hardcoded number. The canonical "is the engine healthy" answer — see [agent-guide.md](agent-guide.md). |

## `experimental/` projects

| Project | Purpose |
|---|---|
| `Puck.HumbleGamingBrick` (+ `.Post`) | GB/GBC emulator core (SM83, snapshot/restore/fork determinism) and its tiered POST battery. The base for the [ideal gaming brick plan](ideal-gaming-brick-plan.md). |
| `Puck.AdvancedGamingBrick` (+ `.Post`) | GBA emulator core (ARM7TDMI, per-cycle timing model) and its POST battery + deep diagnostics (mGBA lockstep co-sim, traces). |
| `Puck.BareMetal` | Freestanding C# (custom CoreLib + mimalloc, no GC/BCL): Windows samples, UEFI kernels, a freestanding Vulkan window, and the Steam Deck boot target. [README](../experimental/Puck.BareMetal/README.md). |

## Other top-level directories

| Path | Purpose |
|---|---|
| `schema/` | `run.schema.json`, generated by `Puck.Demo --emit-schema`. |
| `docs/examples/` | Annotated `puck.run.v1` example documents (all validated by the Post `run-document` stage). |
| `tools/` | Formatting/validation tooling and checked-in baselines. |

The monolithic `src/Puck` and `src/Puck.Avatars` projects (rule 1) live only
in git history, not the tree — as do the completed design records that
referenced their paths (see the retirement policy in [README.md](README.md)).
