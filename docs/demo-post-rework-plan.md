# `Puck.Post` — engine POST (power-on self-test): as-built

**Status: COMPLETE (2026-07-02).** `dotnet run --project src/Puck.Post` runs an ordered offscreen battery of
**25 stages**, writes `artifacts/post/`, and exits **0** (all pass) / **1** (a check failed) / **2** (infra/crash).
Full battery green on the RTX 4070; exit-code semantics are fault-injection-verified (a forced correctness break →
exit 1, a forced stage exception → exit 2, other stages unaffected in both cases).

This document is the as-built record; it travels with the branch. (It supersedes the original "rework Puck.Demo in
place" plan and the from-scratch `Puck.Post` plan those became — the changelog at the bottom records how it got here.)

## What it is

A **POST** — one self-checking run that stresses nearly every core engine subsystem and emits a single aggregated
pass/fail. It is the engine's "does everything still work" button, in place *before* the game is built on top. It is a
from-scratch exe built against the `Puck.*` libraries (it does **not** reference `Puck.Demo`), hosted offscreen by
`Puck.Launcher`, running its battery on the first frame and exiting via `ITerminalControl.RequestExit()`.

## Locked decisions

- **Engine first, game later.** The MiniAction game (the live `--mini-action` prototype, its collision physics, a
  collision-correctness stage) is **deferred** — the POST proves the engine; the game is tacked on once the POST is
  solid. Tier-A determinism is driven by a **neutral** fixed-point sim (`Replay/NeutralSim.cs`), not `MiniActionWorld`.
- **Greenfield, from scratch.** `Puck.Post` copies only the calibrated threshold *constants* from the demo; the demo's
  (now largely retired) gate code was the *worked reference*, never a dependency.
- **`Puck.Post` is the sole home of engine conformance.** The 11 `Puck.Demo --validate-*` gates the POST reimplemented
  (export, compute, cli-determinism, reverse-share, indirect, resample, viewports, pixelate, capture, camera, genlock)
  were **retired from `Puck.Demo` on 2026-07-02** once the POST was green and had been validated against them. `Puck.Demo`
  keeps only the gates with **no POST equivalent**: `--validate` (showcase debug-mode parity sweep),
  `--validate-determinism` (drives the real `MiniActionWorld` sim), `--validate-mini-action`, and the two hardware
  bring-up gates `--validate-camera-live` / `--validate-camera-gpu`. `Puck.Demo` is now purely the live windowed
  showcase (and rejects an unknown/retired flag with exit 1 instead of silently running the showcase).
- **All four Tier-D items ship.** D2 device-lost, D3 hot-switch, D4 GPU-ms budget, D1 present-cadence. The live-subsystem
  stages (D1/D2/D3) run as isolated `--probe <name>` child processes because the live loop runs above the battery's
  one-shot frame.
- **Split-screen is test-only.** The historic "split blanks half the screen" bug is already fixed in-tree
  (`WorldProducerNode` rebuilds composite rects every frame + allocates full-size source textures). The POST adds a
  *test* (B6) that drives the compositor with synthetic animated regions, decoupled from the game's `CameraDirector`.

## Engine gaps the POST discovered (2026-07-01) — both FIXED 2026-07-02 (commit 4d1a555)

1. **Runtime backend swap crashed (0xC0000005)** whenever the Vulkan presenter had ever presented real content. Root
   cause: `VulkanSurfacePresenter.Deactivate` disposed the whole renderer — device and instance included — under (a) the
   published device-context capability (the renderer IS that capability) and (b) every node resource, a use-after-free at
   their eventual release. Fix: `Deactivate` now calls `VulkanRenderer.ReleasePresentation()` (swapchain + surface only;
   device and instance survive for the renderer singleton's container-owned disposal), and `Initialize` reuses the live
   device on reactivation — mirroring the Direct3D 12 presenter, which always left its device-context singleton alive.
2. **No content-re-target seam existed across a switch.** With devices now surviving deactivation, the seam is simply
   node cooperation: poll `BackendSwitcher.ActiveBackendName` per frame and release/rebuild on change (a deactivated
   backend's device stays valid, so late release is safe; the neutral `IGpu*` services are per-call-device, so a node
   composes a bundle for the presenter's device). `PostProbeNode` is the worked reference; D3 proves live content across
   the swap end to end (real Vulkan frames → runtime swap → rebuild on the presenter's D3D12 device → real D3D12 frames,
   verified by a D3D12-side readback).

## Architecture

New project `src/Puck.Post/Puck.Post.csproj` (`OutputType=Exe`), referencing the engine libs it needs (no `Puck.Demo`);
it mirrors the demo's shader-compile MSBuild target only for the one local `gradient.comp`. The harness is small:

- `interface IPostStage { string Name; PostTier Tier; PostStageOutcome Run(PostContext ctx); }`
- `enum PostVerdict { Pass, Fail, Skip, Infra }`, `enum PostTier { A, B, C, D }`, `record struct PostStageOutcome`.
- `PostContext` — the Vulkan host `IGpuDeviceContext` (a host capability, carried as `RequireGpuDevice()`), a **lazily
  created** LUID-matched D3D12 device for Tier C (`RequireDirectXDevice()`; both devices `WaitIdle` per acquire — the
  reset seam), the `IServiceProvider`, and the artifacts dir.
- `PostBattery` — ordered `IPostStage[]`, each run in `try/catch` (a throw in stage N is recorded as `Infra` without
  aborting N+1); folds verdicts (**`Infra`→2 dominates, any `Fail`→1, else 0**; `Skip` neutral); writes a `PostReport`.
  `PostRunResult.ExitCode` defaults to **2** so a run that never reaches the battery fails loudly.
- `PostBatteryNode : IRenderNode` — on its first `ProduceFrame` runs the battery, sets the exit code, and requests exit.
- `PostProbeNode : IRenderNode` — hosted instead of the battery under `--probe <name>` (Tier D); presents real content
  and observes/drives a live subsystem. `PostProbeProcess` is the child-process runner (60 s hang timeout).
- `PostStages.Create()` — the ordered registry.

Shared substrate (value-copied from the demo, **never** code-shared, so the two implementations independently
cross-checked each other during bring-up): `ParityCheck.cs` (metrics + calibrated thresholds `Continuous`/`WorldComposite`/
`WorldFuzz` + the isolated-fraction heuristic), `PostWorldRenderer.cs` (a from-scratch port of `WorldProducerNode`'s
beam → cull-args → views (indirect) → composite chain, with opt-in GPU-timestamp bracketing for D4), `FuzzSdfProgram.cs`,
`RtWorldInstances.cs`, `PngImage.cs`, `GradientCheck.cs` (the compute/reverse-share oracle), and the `Replay/NeutralSim`
determinism vehicle.

## Stage catalog (25 stages)

Each stage returns Pass / **Fail** (→exit 1) / **Skip** (neutral, environmental absence) / **Infra** (→exit 2, or any
uncaught throw). `PostStages.cs` is the run order.

**Tier A — CPU pre-flight** (runs anywhere; a determinism gate cannot catch a wrong-but-deterministic op, so correctness
comes first).
- **A1 fixed-point** — `FixedQ4816` arithmetic, sqrt, CORDIC atan2, banker's rounding vs a `double` reference.
- **A2 worldcoord3** — `WorldCoord3` cell carry / cell-aware delta / translating add vs an absolute fixed-point
  reference, incl. cross-cell and far-cell (1e9) invariance, **on all three axes** (X/Y/Z).
- **A3 determinism + replay** — the neutral fixed-point sim is deterministic; record → binary round-trip → replay is
  bit-exact; every `CommandValueKind` round-trips.
- **A4 CLI/STDIN determinism** — a scripted `Submit`→inject→snapshot console session is deterministic, replays
  bit-for-bit, and measurably drives the sim.
- **A5 genlock** — the pacer's rhythm-follow math: a pure-CPU simulated external clock drives `GenlockPhaseAligner`;
  asserts PI-filtered convergence + the `ExternalClockRegistry` election rules, deterministically.
- **A6 run-document** — the `Puck.Scene` document funnel: every `docs/examples/*.json` parses + validates + round-trips
  bit-stable, malformed documents are rejected with attributed errors, and the committed schema is in sync.

**Tier B — GPU same-device** (offscreen Vulkan host).
- **B1 compute** — pipeline + descriptor + dispatch + storage-image write + readback; the `gradient.comp` output is
  checked **per-pixel** against the `GradientCheck` CPU oracle (R=x/w, G=y/h, B=0.5, within 1 LSB).
- **B2 resample** — sampled-image (combined-image-sampler) binding: nearest identity == source bit-for-bit; a 2× linear
  upscale differs from nearest (the filter mode is live).
- **B3 viewports** — the source-agnostic compositor: a heterogeneous layout (raw copy pane + resampled pane) matches a
  full CPU oracle bit-for-bit.
- **B4 pixelate** — the cell-snap + posterize decorator matches a kernel-replicating CPU oracle within 1 LSB.
- **B5 capture** — the native GDI capture pipeline (env-lenient): a captured desktop frame is asserted to carry real
  variety; a uniform frame (blank/secure/locked desktop) or no frame is a **Skip**, never a Fail.
- **B6 split-coverage** — synthetic animated merged↔split regions (counts 2/3/4): rects tile `[0,1]²` exactly at every
  ease and every active pane renders non-blank.
- **B7 dynamic-transform** — the per-frame entity-transform channel (`SdfOp.TransformDynamic`): the program is uploaded
  once and the entity moves/rotates by rewriting only the transform slot. Asserts the moved and **90°-rotated** frames
  each match their baked (`Translate` / `Translate+Rotate`) equivalents within `Continuous` thresholds and each visibly
  differ from the resting frame — so both the translation *and the quaternion* paths are proven.

**Tier C — cross-backend** (Vulkan host + the shared LUID-matched D3D12 device; debug layer OFF). Each `Skip`s
pre-Windows-10.0.10240 (RT needs 17763).
- **C2 export** — same-adapter export/import round trip on both backends (Vulkan OPAQUE_WIN32, D3D12 shared NT handle,
  exportable STORAGE images). Asserts the API plumbing; content survival is C3/C9's charter.
- **C3 reverse-share** — Vulkan writes `gradient.comp` INTO a D3D12-owned shared image (handle-type 0x40 import on
  NVIDIA), D3D12 reads it back; the readback is checked **per-pixel** against `GradientCheck`.
- **C4 indirect** — `vkCmdDispatchIndirect` / `ExecuteIndirect` is bit-identical to direct dispatch on both backends.
- **C5 world** — cross-backend SDF world parity (hero view) within `WorldComposite` thresholds (SPIR-V vs DXIL).
- **C6 world-child** — as C5 but a viewport is a hosted child-surface composite.
- **C7 fuzz** — a fixed deterministic seed list `{1,7,23,42,91}` is bit-equivalent (mod ±1-LSB) across backends; a
  seed diverging **fails and names the seed**; if *every* seed renders degenerate (a generator-flattening regression)
  the stage **fails** rather than passing green-with-a-note.
- **C8 RT** — hardware ray-query / DXR world parity; **Skip-with-note** when either device's
  `IGpuAccelerationStructure.IsSupported` is false.
- **C9 camera-share** (env-lenient) — the camera zero-copy import seam with synthetic frames: D3D12 produces `sdf-child`
  into a shared image, the Vulkan host imports it zero-copy and asserts real produced structure survived (distinct-color
  count + centre≠corner). **Skip** when the platform camera-capture service is the null implementation.

**Tier D — performance + live subsystems.**
- **D4 gpu-budget** — times the real hero world render via opt-in GPU-timestamp bracketing in `PostWorldRenderer`
  (~0.9 ms on the 4070), proving the per-pass timestamp counters are live and applying a loose sanity ceiling (a
  catastrophic-regression tripwire pending per-machine calibration). Runs in-process on the healthy Vulkan host, first
  in Tier D. **Skip** when the device reports no timestamp support.
- **D1 present-cadence** — a `--probe present-cadence` child: presents real content, polls `IPresentTimingFeedback`
  off the `BackendSwitcher`, and asserts the closed-loop present-timing path is live and its confirmed-present
  timestamps are monotonic (~31.8 ms mean = the dev box's fixed-32 Hz panel). Deliberately does **not** assert VRR
  phase-lock convergence (that needs a variable-refresh panel; A5 covers the aligner math on CPU), so a display with no
  closed-loop feedback yields a **Skip**, never a Fail.
- **D2 device-lost** — a `--probe device-loss` child: `PUCK_TEST_DEVICE_LOSS=1` injects a synthetic
  `DeviceLostException`; the launcher catches it, calls the node-release seam (`OnDeviceLost`), recovers the device in
  place, and resumes. Asserts survival well past the injection with the loop's tick accumulator strictly monotonic.
- **D3 hot-switch** — a `--probe hot-switch` child: presents on Vulkan, switches the backend at runtime, re-targets the
  render to the new backend, and verifies live content by a D3D12-side readback (see the discovered-gaps note above).

## Coverage rationale

The POST's charter is every key engine feature:

| Engine feature | POST stage(s) |
|---|---|
| Fixed-point math + world coordinates (`Puck.Maths`) | A1, A2 |
| Command determinism, record/replay (`Puck.Commands`) | A3, A4 |
| Genlock / external-clock phase alignment | A5 (deterministic math) + D1 (live closed-loop feedback) |
| Scene document funnel (`Puck.Scene`) | A6 |
| Compute dispatch + readback | B1 |
| Sampled-image binding / resample | B2 |
| Viewport compositor / pixelate decorator | B3, B4 |
| Native capture (`Puck.Capture`) | B5 (env-lenient) |
| SDF world render + split-screen compositor | B6, C5, C6 |
| Dynamic-transform channel incl. rotation (`SdfOp.TransformDynamic`) | B7 |
| Cross-backend zero-copy share (both directions) | C2, C3 |
| Indirect dispatch + GPU-driven cull | C4 |
| SDF VM cross-backend equivalence (fuzz) | C7 |
| Hardware ray tracing (ray-query / DXR) | C8 (skip-with-note) |
| Camera content source (synthetic zero-copy tier) | C9 (env-lenient) |
| GPU per-pass timing budget | D4 (loose ceiling, skip-with-note) |
| Present cadence / closed-loop present timing | D1 (skip-with-note) |
| Device-lost recovery | D2 |
| Backend hot-switch | D3 |
| HLSL→SPIR-V/DXIL shader parity | implicit in every Tier C parity stage |

*Considered and intentionally NOT stages:* `Puck.Input` (the HID report parsers are internal to the device classes —
no injectable seam, and everything past parsing needs physical controllers; revisit if a parse seam is exposed);
`Puck.Text` (a leaf library not wired into the engine render path). The live-webcam and DXVA-GPU camera tiers stay
demo-only hardware bring-up gates (`--validate-camera-live` / `--validate-camera-gpu`). VRR phase-lock *convergence*
stays out of D1 until a variable-refresh panel is available.

## Verification

- `dotnet run --project src/Puck.Post` exits **0** with every stage green; flip one threshold/constant → **1**; force a
  stage exception → **2** (other stages still run). Inspect `artifacts/post/` (per-stage PNGs + `post-report.txt`).
- **Tier A** runs anywhere (pure CPU). **Tiers B/C/D** need this RTX 4070 (Vulkan + D3D12).
- **Skip paths:** C8 (RT) via forcing `IsSupported=false`; C9 (camera) via `NullCameraCaptureService`; B5 (capture) on a
  headless/secure desktop; D1 on a display with no closed-loop present timing; D4 on a device without timestamps.
- **Demo cross-checks that remain** (the gates NOT retired): `Puck.Demo --validate` (showcase parity),
  `--validate-determinism`, `--validate-mini-action`, `--validate-camera-live`, `--validate-camera-gpu`. The retired
  gates' coverage now lives solely in the POST; there is no longer a second implementation to diff against for those 11.

## Changelog

- **M0–M1** (2026-06) — harness shell + Tier A (A1–A4), pure CPU; verdict folding + report aggregation.
- **M2** (2026-07-01) — Tier B same-device (B1–B5) + the audit-added CPU stages A5/A6.
- **M3** (2026-07-01) — the reusable `PostWorldRenderer` + B6 split-coverage + B7 dynamic-transform.
- **M4** (2026-07-01) — Tier C core (C2–C6) + C9, on the shared lazy LUID D3D12 device; parity substrate consolidated in
  `ParityCheck.cs`.
- **M5** (2026-07-01) — C7 fuzz + C8 RT (the codebase's first cross-backend RT parity diff, live on both backends).
- **M6** (2026-07-02) — Tier D as child-process probes; D2 device-loss (full recovery cycle) and D3 hot-switch. The two
  engine gaps above were discovered here and fixed.
- **M7** (2026-07-02) — verification complete: 23/23 exit 0; fault-injected exit-1/exit-2 paths confirmed with stage
  isolation intact.
- **2026-07-02 (closeout session)** — (a) retired the 11 superseded `Puck.Demo --validate-*` gates + the now-dead
  `ViewportCompositorNode`/`ResampleNode`/`PixelateNode` (~3.3k LOC), making the POST the sole conformance home;
  (b) hardened seven thin oracles (per-pixel gradient for B1/C3; all-three-axes A2; the quaternion path for B7; real
  structure for C9; all-degenerate fail for C7; captured-frame variety for B5); (c) shipped the two deferred Tier-D
  stages D4 gpu-budget and D1 present-cadence. Battery now **25/25, exit 0**.

## Known follow-ups (non-blocking)

- Hoist the duplicated `PngImage` to a shared imaging lib (the one value-copied file with no cross-check rationale).
- A demo↔POST sync-guard test for the remaining value-copied substrate files.
- Calibrate per-machine GPU-ms numbers to tighten D4's loose ceiling.
- C2 export still asserts only handle plumbing (content survival is C3/C9's charter by design).
- The deferred MiniAction game stages (collision correctness, a game-determinism gate) — tack on once the POST is deemed
  solid.
