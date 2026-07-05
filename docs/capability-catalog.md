# Puck capability catalog

The master inventory of everything Puck can do today. Puck is an
"everything-as-data" game engine — a dumb terminal with extra steps: one
versioned JSON document describes a run, and the engine's job is to render,
composite, validate, and replay it deterministically on whichever GPU backend
is available. There is no real gameplay yet beyond the overworld demo; the
value is the capability substrate below it, which this document catalogs.

Each entry says **what it is**, its **status**, **how to invoke it**, and
**where it lives**. Companion docs: [project-map.md](project-map.md) (which
project does what) and [agent-guide.md](agent-guide.md) (how to verify and
what will bite you).

**Status legend**

| Mark | Meaning |
|------|---------|
| ✅ | Shipped and verified on real hardware (RTX 4070 unless noted) |
| 🔶 | Shipped; partially verified, or verified only in one configuration |
| 🧪 | Designed-for: the seams exist, the end-to-end path is not built/tested |
| ⛔ | Known hard limit — documented, not a bug to fix |

---

## 1. Rendering core

| Capability | Status | Notes |
|---|---|---|
| SDF virtual machine | ✅ | Scenes are programs for a small instruction-stream VM, marched in compute shaders. 9 primitives (box, capsule, sphere, torus, cylinder, plane, ellipsoid, round cone, screen slab), 7 blends (union/intersection/subtraction + their smooth forms + xor, with per-op material ownership — a carve wears the carving shape's material), point transforms (translate/rotate/scale/repeat/symmetry), warps (twistY, bendX/Y/Z, elongate), field ops (onion, dilate), and derived constants baked host-side by the builder (programs build once; shapes evaluate millions of times per frame). C# half in `src/Puck.SdfVm` (`SdfProgram`, `SdfProgramBuilder`); GPU half in the `sdf-vm` HLSL — the two ISAs must stay in sync. Gates: Post `world`, `world-menagerie` (new shapes + materials), `world-warp` (warps/field ops/blends). |
| Wallpaper symmetry folds | ✅ | All 17 IUC wallpaper groups as one `WallpaperFold` op: square/rectangular lattices (P1–P4G) and the equilateral hex lattice (P3–P6M), every branch an exact isometry, with a parity-material stride (checker / hex 3-coloring recolors cells from the palette) and a per-sample symmetry LOD (distant lattices keep their copies, skip their in-cell folds). Proven by Post `world-wallpaper` (maxΔ1 cross-backend on both lattice families). |
| Material model | ✅ | Palette entries carry albedo + emissive (self-glow) + Blinn-Phong specular/shininess, two packed words per material with one shader decode point; all-zero new channels reproduce the v1 lambert image exactly. Proven by Post `world-menagerie`. |
| Diegetic screen glow + CRT | 🔶 | A booted stand's diegetic screen renders a CRT glass face (barrel curvature + rounded bezel + native-line scanlines + vignette + fresnel glint + bloom, in `sampleScreenSurface`) AND emits colored light into the room — its per-frame framebuffer average, via a binding-11 `sdfScreenLights` buffer summed with the sun in the world shade loop (up to 4 screen lights); a per-frame `AmbientScale`/`SunScale` dims the room so the glow dominates (the overworld mood). Additive: default (no bound screen, env 1.0) is the prior render. CRT-face cross-backend parity proven by Post `world-screen` (the binding-11 buffer is read every frame there); the CRT face + dimmed mood are confirmed in a live overworld capture. The lit-screen room glow is wired through the overworld but a fully-lit beauty shot needs game content (an external showcase ROM). |
| SDF engine host | ✅ | `SdfWorldEngine` (device-explicit core: beam cull → per-view render → split-screen composite over a viewport table of cameras + regions; submit-and-wait harness mode AND fire-and-forget node mode) + `SdfEngineNode` (the host-model `IRenderNode`), both in `src/Puck.SdfVm`, fully backend-neutral. Post drives the same engine its stages validate; the demo/overworld composites through the node. |
| Dynamic transforms | ✅ | `TransformDynamic` reads per-frame slots from a GPU buffer, so moving entities update every frame **without** re-uploading the static scene program. Proven by the Post `dynamic-transform` stage. |
| GPU-driven culling + indirect dispatch | ✅ | Beam pass → cull-args reduction → GPU-written indirect views dispatch; sky margin is never dispatched. Both backends. Indirect *draw* is deferred (engine seam unchanged). |
| Hardware ray tracing | ✅ | Ray-query-into-SDF-march (TLAS cull + RT shadows) on one neutral node, on **both** Vulkan (`VK_KHR_ray_query`) and Direct3D 12 (DXR 1.1). Proven by the Post `rt` parity gate (self-contained TLAS + kernel copies); the demo's live `--world-rt` producer was retired in the four-quad slim-down. |
| Text / font atlases | ✅ | Render-agnostic MTSDF/MSDF/SDF/mask atlas model + em-space layout in `src/Puck.Text`; content-addressed LRU atlas cache. No GPU code — the renderer consumes placements + sampling math. |
| Debug visualization | 🧪 | The monolith-era debug-view subsystem (mode catalog, GPU stats buffer, JSONL log) was **deleted with the monolith** — only the RT cull-parity debug host (`sdf-world-rt-debug.rq.comp.hlsl`) survives in the split engine. [debug-visualization.md](debug-visualization.md) is the design reference for rebuilding it. |

## 2. Dual backend: Vulkan + Direct3D 12

The whole showcase runs on **either** backend through one neutral seam
(`Puck.Abstractions`). See [feature-parity-summary.md](feature-parity-summary.md)
for the parity verdict and [feature-parity-table.md](feature-parity-table.md)
for per-row provenance.

| Capability | Status | Notes |
|---|---|---|
| Single-source shaders | ✅ | One HLSL source compiled at build time by DXC to **both** SPIR-V (Vulkan) and DXIL (D3D12) — including the inline-ray-tracing kernels. `src/Puck.SdfVm/Puck.SdfVm.csproj` is the recipe; `ShaderModuleLoader` sniffs SPIR-V vs DXBC/DXIL containers. |
| Cross-backend zero-copy surface sharing | ✅ | Content produced on one API, presented by the other, no CPU round-trip — **both directions** GPU-verified: Vulkan host + D3D12 producer and D3D12 host + Vulkan producer. Devices are LUID-matched. Described as data by a `world` run document (`graph.produce`), e.g. [examples/world-directx-vulkan-produce.json](examples/world-directx-vulkan-produce.json) — parse corpus: a mismatched `produce` is rejected with an attributed error (the shared world renderer runs host-backend only, pending the cross-backend re-host); same-backend `world` documents run live. Live zero-copy proof: Post C1/C2. |
| Runtime backend hot-switch | ✅ | `BackendSwitcher` toggles Vulkan ↔ D3D12 live; presenter persistence proven by Post D4. |
| Cross-backend differential fuzzing | ✅ | Process-isolated fuzzer renders generated scenes on both backends and diffs. The generator spans all 7 primitives, all 7 blends, and the twist/bend/elongate warps. Default posture is RELAXED (mean/spread guards only — real divergences explode both; FP-noise classes pass); `PUCK_PARITY_STRICT=1` opts into the pixel-perfect delta-mass calibrations (the long-term ideal — see `ParityThresholds`). Run via the Post `fuzz` stage (`--stage fuzz --fuzz-seed <n>`) or `tools fuzz` for isolated sweeps; the `fuzzing` document section ([examples/fuzz-sweep.json](examples/fuzz-sweep.json)) survives as parse corpus. |
| Mixed-backend split screen | 🔶 | What's shipped: per-viewport composition (up to 5 regions) where the *content* device and *host* device are different APIs, plus hosted child surfaces per viewport (a `world` run document's `child` graph field; the Post `world-child` stage proves it). A literal "top half D3D12, bottom half Vulkan in one window" is a composition of these verified pieces, not itself a standing demo. |
| GPU perf counters | ✅ | Cross-backend timestamp counters: per-pass GPU-ms + share on both backends. Enable with `PUCK_TIMING=1`; regression tripwire is Post D1. |

## 3. Everything as data: the run document

| Capability | Status | Notes |
|---|---|---|
| `puck.run.v1` document | ✅ | One versioned JSON document (`PuckRunDocument` in `src/Puck.Scene`) is the source of truth for a run: `host` (window/backend/present mode), `scene` (materials + SDF objects), `viewports` (regions + content sources), `graph` (root node), `input` (controller routing), `validation`, `fuzzing`. Version discriminator is gated before anything binds; root carries forward-compat `Extensions`. |
| CLI ⇒ document collapse | ✅ | Every legacy `Puck.Demo --*` flag synthesizes a run document (`DemoRunDocuments.Synthesize`) — there is exactly ONE code path. Flag-synthesis determinism is Post A4. |
| JSON Schema export | ✅ | `schema/run.schema.json`, regenerated via `Puck.Demo --emit-schema <path>`. Every example document in [docs/examples/](examples/) validates against it (the Post `run-document` stage). |
| Graph node kinds | ✅ | `$type` on `graph`: `world` (the document's scene + viewports rendered live through the shared `SdfWorldRenderBuilder` on the HOST backend — re-enabled 2026-07-04; cross-backend `produce` and the retired `child` boolean are rejected with attributed errors) and `overworld` (the demo: a `consoles` list of gaming-brick sources seats the room's bootable stands; an optional `library` of cartridges stocks the shelf). Both graph kinds flow through the ONE shared render assembly. (The `showcase`/`rt`/`camera` kinds were retired in the four-quad slim-down; the `mini-action` kind and viewport source rolled up into `overworld`.) |
| `--rom` direct boot + fourth-wall exits | ✅ | `Puck.Demo --rom <path> [--rom-exit "0xDA22>=1"]` boots straight INTO the cartridge — the IMMERSED overworld: `DemoRunDocuments` synthesizes an `OverworldNode { immersed: true }` with one stand per potential player (the same ONE code path), and each connecting controller (up to 4) is auto-seated at (boots + takes over) its own machine, panes tiling the screen 1→2→3→4 as pads join. When ANY machine's `exit` condition holds (a `gaming-brick` source field: work-RAM address 0xC000–0xDFFF as an `"0x…"` string + op + value; polled after each stepped frame), the FOURTH WALL BREAKS: the panes ease away and the room is revealed — every active player standing at their stand, the games continuing on the diegetic screens (machines never reset). Without a handler (a plain `world`-document `exit`, e.g. [examples/pokemon-gold.json](examples/pokemon-gold.json)) the condition instead requests the same clean shutdown `host.exitAfterSeconds` uses. Gates: `--validate-overworld` (hash unchanged — seating/ownership is host-side routing), the Post `run-document` stage, the Humble `battery-save` stage. |

## 4. Viewports, picture-in-picture, and content sources

| Capability | Status | Notes |
|---|---|---|
| Multi-viewport compositor | ✅ | 1–5 normalized regions per window (raised from 4 on 2026-07-04 so the overworld seats a room view + FOUR console panes), each with its own content source; a 2×2 split-screen with 4 independent cameras is described by a `world` run document ([examples/world-split.json](examples/world-split.json)) and RUNS live. Gate: Post B3; the live multi-view walk is also the overworld's boot layout. |
| Hosted child surfaces | ✅ | A viewport can host an independently animated child surface (picture-in-picture via the recursive node tree) — declared as the viewport's `source` (`gaming-brick` runs live; `live-camera` is rejected pending its child node's re-host). The legacy `world.child` boolean is retired (parses, rejected with a pointer at viewport sources). Parity gate: the Post `world-child` stage. |
| Diegetic screen sources | ✅ | An in-world screen as document data (2026-07-04): a scene `screenSlab` object takes an optional `screenIndex` + explicit `worldOrigin`/`worldRight`/`worldUp` frame, and the top-level `screenSources` table maps each screen index to a provider (`{"$type": "viewport", "slot": n}` = the gaming-brick child's NATIVE framebuffer) — a brick renders ON the device inside the world, not only as a 2D pane. Live example: [examples/world-screen.json](examples/world-screen.json). Gate: the Post `world-screen` stage, which also pins the builder's quaternion-authored `ScreenSlab` overload pixel-identical to the explicit frame. |
| Resample node | ✅ | Sampled-image compute binding; NEAREST is bit-for-bit identity, LINEAR/NEAREST upscale divergence proven. Post B2. |
| Pixelate node | ✅ | Post-process kernel (colorspace quantization + pixel-cell blocking). Post B4. |
| Live camera source | ✅ | First-class `live-camera` viewport source (Media Foundation): device selection, fit modes, requested format. GPU zero-copy tier: DXVA decode → D3D11 shared → Vulkan import, ARGB32, **no host memory** (killed the NV12/ycbcr path). Replug/re-open handled. Verified with a Logitech C920 incl. 1080p MJPEG. Zero-copy share gate: Post C6; live proof: the four-quad document's camera pane. (The `--validate-camera-*` bring-up gates were retired once the source shipped per-viewport.) Linux/macOS backends not built (camera M6). |
| GamingBrick pane source | ✅ | First-class `gaming-brick` viewport source: a HumbleGamingBrick machine (a `dmg`/`cgb`/`agb` costume + cartridge ROM) stepped from the engine's fixed-step clock through an exact integer tick→T-cycle accumulator (zero drift), its 160×144 framebuffer nearest-resampled into the pane. Controller routing via the document's `input` section (`auto`/`multicast`/`per-player`); the pad service is the run's sole gamepad drainer. Emulator-side gates: Humble Post `agb-costume` + `trio-lockstep`. Live proof: [examples/four-quad.json](examples/four-quad.json). |
| Overworld console panes | ✅ | The overworld graph hosts one dark GamingBrick pane per console stand: the player boots a stand in-world (interact = North) and the pane powers on with a FIXED full-frame image allocation, so the animated layout transition never reallocates GPU images. The room player's movement mirrors into every booted brick's joypad — walk the room, walk the games. Live proof: [examples/overworld.json](examples/overworld.json). |
| Desktop capture source | 🔶 | DXGI Desktop Duplication captures the live desktop as a content source; capture pipeline gate is Post B5. Remaining: the zero-copy D3D12-host import tier and run-document typing for capture sources. |
| Frame capture to disk | ✅ | `--capture out.png` grabs the first rendered frame from the GPU; `Puck.Capture` encodes PNG (dependency-free) with observer hooks (e.g. per-frame FNV-1a hash) for regression nets. |
| Genlock (external rhythm phase-align) | 🔶 | External rhythm producers (e.g. a camera) register as genlock sources; a grid-phase PI controller phase-aligns the frame pacer (arrival stamps in QPC at the publish site; deterministic control law proven by Post A5). Live convergence needs a true VRR panel — the dev display is fixed-32 Hz remote. Document field `host.genlock`; kill switch `PUCK_GENLOCK=0`. |

## 5. Presentation and timing

| Capability | Status | Notes |
|---|---|---|
| Present modes | ✅ | `vsync`, `mailbox`, `immediate`, `adaptive` (VRR), via `--present-mode` or `host.presentMode`; measured cadence gate Post D2. |
| Closed-loop present timing (VRR) | 🔶 | Vulkan `present_id`-based closed loop verified live. DX adaptive/tearing cannot phase-lock via `GetFrameStatistics` (API ceiling). Real-VRR cadence validation still open (no VRR panel on the dev box). `PUCK_PRESENT_TIMING` logs measured intervals. |
| Device-lost recovery | 🔶 | Real `VK_ERROR_DEVICE_LOST` detection + synthetic rebuild verified on both backends; a TDR/driver reset (Win+Ctrl+Shift+B) is absorbed with no loss; unrecoverable loss ends in a graceful clean shutdown (10 s budget, teardown tolerates a dead device). Gate: Post D3. Known deferred: `CrossBackendComputeWorldNode` lacks `OnDeviceLost` (leaks on loss under a live `world` run document). DX real-removal recovery unverified. |
| | ⛔ | In-process Vulkan **cannot** recover from a full GPU removal on NVIDIA — the ICD wedges and `vkCreateInstance` fails forever post-re-enable; only a new process recovers. |

## 6. Determinism and simulation

| Capability | Status | Notes |
|---|---|---|
| Deterministic numerics | ✅ | Allocation-free unsigned fixed-point (`UFixedQ4816` et al.) in `src/Puck.Maths`: culture/CPU-independent, hardware paths bit-identical to fallbacks. `WorldCoord3` fixed-point world coordinates with cell carry. Gates: Post A1/A2. |
| Command snapshots + replay | ✅ | All input converges into per-tick `CommandSnapshot`s (interned ushort command ids, per-slot lanes, capture-tick total order) — bit-identical across machines. `InputRecorder` / `ReplaySnapshotSource` / `ScriptedSnapshotSource` in `src/Puck.Commands`. Gate: Post A3 (600-tick record/replay). |
| Overworld demo | ✅ | THE demo (the no-flags default): a controller-driven deterministic fixed-step room (stick = walk, South = jump, North = interact/boot, West = contextual run/activate; binding pages + debug verbs per [overworld-demo-plan.md](overworld-demo-plan.md) controls) with three console stands — the showcase cartridge on the dmg/cgb/agb costumes of the one SM83 machine. Booting a stand is SIM STATE (interact edge near a stand; boot mask + boot order are hashed and replay), lights its screen, powers its pane, and eases the layout through fullscreen → side-by-side → big-top/two-bottom → 2×2 quad. Brick input rides one shared epoch-anchored timeline: a late-booted machine fast-forwards through the recorded stream until it converges, so same-costume machines are bit-identical regardless of boot order (capture-proven CGB≡AGB across a 4 s stagger; the DMG pane's in-game drift is the cartridge's authentic mono-path timing, not a defect). A source's `speed: "dmg"` pins the tick→cycle budget to the DMG rate regardless of KEY1 — the fairness mode for same-game-different-device play ([examples/overworld-fair.json](examples/overworld-fair.json)); `runAs: "dmg"` is the boot-time uniform-demote debuff — the stand keeps its costume but the machine boots as a DMG, so a dual-mode cartridge takes its mono code path and the whole fleet bit-locks, DMG pane included ([examples/overworld-mono.json](examples/overworld-mono.json), capture-proven three-way pixel-identical). Identical-machine consoles CHOIR once converged (a ContentEquals-verified park: one stepped machine, W presents — [examples/overworld-choir.json](examples/overworld-choir.json), pixel-proven), machines step task-per-machine off the serial GPU pass, and the debug pages exercise realtime buff/debuff (speed pin, live DMG/CGB/AGB mode migration via snapshot/restore, clear-save) with automatic choir unpark. Console mode is MULTIPLAYER (2026-07-04): pads beyond the first join as world players (pad index = slot, up to 4), each with its OWN binding bar in the overlay, dispatching debug verbs at their own nearest console; pad-count drops evict with leaver hygiene. Proximity TAKEOVER: any player's interact at an unbooted stand boots (and claims) it; at a booted, unowned stand it takes it over — that pad alone drives the brick, which leaves the shared timeline (a choir dissolves); a second interact releases it back to the timeline at the head. Ownership is HOST-SIDE input routing, never hashed sim state — the overworld determinism hash is unchanged (0x47DA634C1658D2CE). The bare room (an empty `consoles` list) keeps the 4-player pools, controller join/leave, and replay-through-churn machinery. Run: `Puck.Demo` or `--overworld` (Vulkan); self-check `--validate-overworld`; headless boot captures via `PUCK_OVERWORLD_DEBUG_BOOT`. |

## 7. Controller and window input

Full architecture, feature matrix, and debugging notes:
[src/Puck.Input/README.md](../src/Puck.Input/README.md) — that README is the
handoff document; this is the summary.

| Capability | Status | Notes |
|---|---|---|
| Switch Pro / Xbox Series / DualSense over USB | ✅ | Buttons, sticks, triggers, rumble, LEDs; Switch + DualSense also gyro/accel with fused orientation (IMU fusion runs on the **device clock**, not wall-clock — deterministic re-runs). |
| DualSense Bluetooth | ✅ | Input, motion, rumble, LED, adaptive triggers (CRC'd `0x31` reports). |
| Switch / Xbox Bluetooth | 🧪 | Deferred; parsers are transport-agnostic, only the BT handshakes are missing. |
| DualSense adaptive triggers | ✅ | Typed capability (`TriggerEffectSpec`: Feedback/Weapon/Vibration/ContinuousCurve). |
| Hotplug + player slots | ✅ | 1.5 s rescan, fault-on-disconnect pruning, slot reuse, content-addressed device ids. |
| Sub-frame timing | ✅ | Reports stamped on the I/O thread; per-button first-press edge times survive coalescing (rhythm-game grade). |
| Keyboard/mouse | ✅ | Raw-Input high-rate coalescing (1000–8000 Hz to per-frame), provider-neutral `WindowInputEvent` → `InputSignal` mapping. |
| Binding pages (modifier-chord profiles) | ✅ | Data-driven binding profiles (`BindingProfileDocument` → `CompiledBindingProfile` → `PagedInputBindings`): two trigger modifiers select order-sensitive pages with threshold hysteresis and press latching; the on-disk JSON profile is the source of truth (seeded + version-reseeded); an on-screen binding bar renders the active page. Rides BOTH input paths — the bare room's deterministic router and console mode's page adapter (`OverworldPageInput`, which replays the sole-drainer pad service's state as synthesized signals). Default profile (purified 2026-07-04 — the placeholder "Actions I/II" grids and their `demo.action`/`demo.target`/`demo.interact` vocabulary were DELETED): movement + **Debug: Engine** (RT→LT: SDF debug view modes depth/normals/raydir/material-id/iteration-count/off) + **Debug: Bricks** (LT→RT: speed pin, live mode DMG/CGB/AGB, clear save, state hash, fleet status, capture) as the LAST controller page. In multiplayer console mode every active player renders their own bar. Gate: Post's `BindingPageStage` (Tier A; bit-identical session hashing) — the old demo-resident `--validate-bindings` flag was retired into it. |
| Linux / Steam Deck hidraw transport | 🧪 | Deferred; `Puck.Input` core is OS-agnostic, only the Windows transport exists (`Puck.Platform`). |

## 8. Validation: the POST batteries and gates

| Capability | Status | Notes |
|---|---|---|
| `Puck.Post` engine battery | ✅ | 32 fail-isolated stages, green on RTX 4070: Tier A (7, CPU pre-flight: fixed-point, WorldCoord3, determinism, CLI synthesis, paged-binding resolver, genlock law, run documents), Tier B (7, same-device GPU), Tier C (14, cross-backend incl. world/RT parity, diegetic screen sampling (`world-screen`), instance-culling exactness (`world-instanced`), 300-instance swarm at scale (`world-swarm`), zero-copy both directions, camera share, fuzz), Tier D (4, live subsystems: GPU budget, present cadence, device-lost, hot-switch). On iGPU-only machines (e.g. the Surface) expect `rt` to SKIP (no ray query) and `reverse-share` to FAIL (import handle type) — hardware conditions, not regressions. Run: `dotnet run --project src/Puck.Post -c Release` with `--tier`/`--filter`/`--stage`/`--fuzz-seed`/`--artifacts`. |
| Demo-resident gates | ✅ | Puck.Demo is now the overworld prototype, and keeps exactly one self-gate: `--validate-overworld` (pure-CPU determinism + replay self-check). Everything else — engine determinism/replay, the paged-binding resolver, cross-backend world parity, differential fuzzing — was retired into Puck.Post (the webcam bring-up gates went when the camera shipped as a per-viewport source); Puck.Post cannot reference the demo, so `--validate-overworld` is the one gate that has to stay demo-resident. |
| Emulator POST batteries | ✅ | Each GamingBrick has a mirrored tiered battery (see §9): Tier A self-tests need no assets; Tier B reference-ROM checks skip when the corpus is absent; Tier C is reserved for cross-machine link determinism. |

## 9. Emulation: the GamingBricks (experimental)

Handheld emulators use the deadpan `<Tier>GamingBrick` naming: **Humble**
(GB/GBC), **Advanced** (GBA), Folding (DS, future). Both cores are standalone
experimental subsystems — no `src/Puck.*` references yet. The north star is
[ideal-gaming-brick-plan.md](ideal-gaming-brick-plan.md): a deterministic
cross-generation machine (three rules: deterministic; carry-forward; two
players on different generations stay bit-locked over a link cable).

| Capability | Status | Notes |
|---|---|---|
| Humble (GB/GBC) core | ✅ | SM83, unified 16-bit timer/DIV/serial counter, docboy-derived PPU, SameBoy-derived integer APU, full mapper set. **Whole-machine mid-frame snapshot/restore and `Fork()`** — the foundation for rewind, netplay, and link co-sim. Battery: `dotnet run --project experimental/Puck.HumbleGamingBrick.Post` (determinism / snapshot round-trip / battery-save / fork-determinism / blargg / mooneye). |
| Battery saves (persistent SRAM + RTC) | ✅ | A battery-backed cartridge's external RAM persists to `<romPath>.sav` — loaded at machine assembly (a power-on cartridge read, a deterministic config input), flushed debounced-on-dirty, forced on reboot/dispose — with the mapper's clock appended as a footer (MBC3: the standard 48-byte RTC layout; HuC3: a 16-byte minutes/days block, our own convention — no cross-emulator standard exists for it). The clock RESUMES where the last session left it (the footer's wall timestamp is stamped for foreign-emulator interop but ignored on load — time pauses while off, never goes backward, so Pokémon G/S's own elapsed-time bookkeeping stays consistent). The save TRAVELS with the cartridge across the debug demote/promote reboot (hardware cartridge-move semantics) and is deletable live via the Bricks debug page's "Clear save" verb. Seam gate: the Humble battery's `battery-save` stage. |
| Advanced (GBA) core | ✅ | ARM7TDMI + full bus/prefetch/DMA/timers/IRQ per-cycle model, full PPU + APU, Flash/SRAM/EEPROM + RTC. 38/38 AGS aging-cartridge cells with real BIOS; Pokémon Emerald boots. No snapshot API — determinism is register+framebuffer identity. Battery: `dotnet run --project experimental/Puck.AdvancedGamingBrick.Post` (+ a deep diagnostics suite: mGBA lockstep co-sim, cycle/state traces, render hashes). |
| Machine-fleet bench | ✅ | `--bench` in the Humble Post exe: fleet scaling in both shapes (independent streams / shared choir stream) 1T+MT to N=256, burst catch-up, `Create`/`Snapshot`/`Restore`/`Fork` latency+allocation, mailbox-check cycle, per-machine footprint — with built-in serial-vs-parallel and same-stream bit-lock guards (exit 1 on divergence). Measured plan: [docs/machine-fleet-plan.md](machine-fleet-plan.md). |
| Networked / multi-machine link play | 🧪 | The load-bearing seams exist — Humble: `ISerial` + fork-determinism gate; Advanced: `IGbaLink`/`IGbaLinkNode` with `NullGbaLink` default; both batteries reserve Tier C for link determinism — but **no multi-machine link layer is implemented yet** (Humble plan milestone M5). "Multiple emulators in a networked fashion" is theoretically possible by design, untested in practice. |
| Emulator-as-content-source | ✅ | The overworld IS the bridge: `GamingBrickChildNode` hosts the Humble machine as a live pane (fixed full-frame GPU allocation, per-frame upload, `OverworldBrickTimeline` input lockstep) — a machine is a first-class world citizen today. The GamingBricks are the deliberately chosen stand-ins for hosting a foreign engine: deterministic, self-verifying tenants whose alien clock, framebuffer format, input timing, and link-cable comms exercise the full hosting contract — with the tenant provably correct, every integration bug is attributable to the host. Still open: the GBA core as a pane (overworld panes run the SM83 costumes), zero-copy framebuffer upload (fleet-plan backlog). |

## 10. Bare metal (experimental)

| Capability | Status | Notes |
|---|---|---|
| Freestanding C# runtime | ✅ | `Puck.Runtime` replaces CoreLib under NativeAOT (`IlcSystemModule`): no GC, no BCL, heap via statically-linked mimalloc; ~105 KB self-contained exe; reflection-free DI substrate works. `experimental/Puck.BareMetal` ([README](../experimental/Puck.BareMetal/README.md)). |
| Freestanding Vulkan window | ✅ | Win32 window + full Vulkan bring-up (instance→swapchain→present loop) in ~120 KB, depending only on OS DLLs. |
| UEFI boot on real Steam Deck | ✅ | `EfiHello` boots on a Steam Deck LCD (Van Gogh) from USB, non-destructively: GOP-only console (no serial), portrait 800×1280 framebuffer at high PCIe BAR, `PUCK_FB_ROTATION=90`. The load-bearing fix: map the framebuffer **write-combining** and remove the global `wbinvd` (AMD DCN doesn't snoop the CPU cache). Six works-in-QEMU-dead-on-Deck bugs fixed; branch `features/efi-deck`. |
| "Fake Linux host at boot" | 🧪 | The eventual target (Puck as the machine's only program); entry point not built. Polymorphic interface dispatch under freestanding NativeAOT still pending (single-implementer devirtualization only). |

## 11. Supporting libraries

| Library | What it gives you |
|---|---|
| `Puck.Assets` | Content-addressed byte store (`IAssetSource`), 64-bit content hashes, fixed-capacity LRU cache — dedup identity, not tamper evidence. |
| `Puck.Storage` | Object blob store: local-file + Azure Blob backends behind one routing abstraction, JSON wrapper. |
| `Puck.Maths` | The deterministic numeric substrate (§6) plus branchless generic-integer routines (Morton/Szudzik pairing, Gray codes, primes, secure random). |
| `Puck.Compositing` | Backend-neutral draw-command recording; pipeline identity = shader content hash, so the same compositor runs unchanged on both backends. |
| `Puck.Hosting` | The recursive node tree (`IRenderNode`) + the dual capability model (inherited device context vs. held terminal/input focus) — the "dumb terminal" core. |
| `Puck.Launcher` | The generic host: run loop, command pump, backend switcher, genlock registry. Imports nothing platform- or backend-specific; the composition root (Demo) wires those in. |

---

*Maintenance note: when a capability ships or a status changes, update the
relevant row here and the pointer docs it references. Statuses reflect
hardware verification as of 2026-07-04 (RTX 4070, Windows 11; Steam Deck LCD
for §10).*
