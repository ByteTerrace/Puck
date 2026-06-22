# Plan: greenfield `Puck.Post` — an engine POST (power-on self-test)

This document travels with the branch. Status: **approved (2026-06-22); implementing.** It supersedes the earlier
"rework Puck.Demo in place" draft after that draft's claims were validated against the code and the author refined the
scope. The full design discussion lives alongside this; this file is the executable summary.

## Vision

A **POST** — one self-checking run that stresses nearly every core engine subsystem and emits a single aggregated
pass/fail. It is the engine's "does everything still work" button, to be in place *before* the game is built on top.

## Locked decisions

- **Engine first, game later.** The MiniAction game — the live `--mini-action` prototype, the collision-against-center
  physics bug, the game-specific `--validate-mini-action` gate, and a collision-correctness stage — is **deferred**. The
  POST proves the engine; the game is tacked on once the POST is solid. `Puck.Demo` keeps working as-is meanwhile.
- **Greenfield, reimplemented from scratch.** A new `Puck.Post` exe built against the `Puck.*` engine libraries. It does
  **not** reference `Puck.Demo`. Only the calibrated threshold *constants* are copied; `Puck.Demo`'s gate/renderer code
  is the **worked reference**, not a dependency.
- **`Puck.Demo` coexists.** It stays the live windowed showcase; converge/retire is a later decision. The standing
  cross-check is that the matching `Puck.Demo --validate-*` gates still pass, so the from-scratch reimplementation is
  known to agree with the reference.
- **Scope = Tiers A–C + de-risked Tier D (D2 device-lost, D3 hot-switch).** D1 (headless VRR cadence) and D4 (GPU-ms
  budget) are deferred — D1 has no headless seam today (the pacer is swapchain-bound) and would need a synthetic
  present-timing mock.
- **Split-screen is test-only.** Bug 2 (split blanks half the screen) is **already fixed in-tree**: `WorldProducerNode`
  rebuilds the composite rects every frame (`WorldProducerNode.cs:199`) and allocates full-size source textures
  (`:411-427`), with comments documenting exactly that fix. There is no fix to make — the POST adds a *test* that drives
  the compositor with **synthetic animated regions** (merged↔split↔merged), decoupled from the game's `CameraDirector`.

Outcome: `dotnet run --project src/Puck.Post` runs an ordered battery offscreen, writes `artifacts/post/`, and exits
`0` (pass) / `1` (a check failed) / `2` (infra/crash).

## Why from-scratch is tractable

The hard, GPU-verified assets are already in libraries; only the C# *wiring* is demo-local.

- `Puck.Maths` — `FixedQ4816`, `FixedVector2/3`, `WorldCoord3`, `Fnv1aHash` (Tier A).
- `Puck.Commands` — `CommandRegistry`, `CommandSnapshot`, `SnapshotRecording`, `InputRecorder`, `ReplaySnapshotSource`
  (Tier A determinism, driven by a **neutral** fixed-point sim, not `MiniActionWorld`).
- `Puck.SdfVm` — the SDF data model (`SdfProgram`, `SdfFrame`, `ISdfFrameSource`, `SdfProgramBuilder`) **and every render
  kernel** (`sdf-beam`, `sdf-cull-args`, `sdf-world-views`, `sdf-world-composite`, `sdf-child`, `composite`, `resample`,
  `pixelate`, `viewport-composite`, `sdf-world-rt-debug.rq`). Demo-local shaders are only `gradient.comp` and
  `cursor-overlay.frag`.
- `Puck.Launcher` — the generic offscreen host + `BackendSwitcher` + the device-lost recovery loop (the launcher always
  opens a window and drives the root node per frame; "headless" here means the POST renders offscreen and exits on the
  first frame, exactly like the demo's `--validate-*` gates).
- `Puck.Abstractions` — `DeviceLostException`, `IDeviceLostRecoverable`, `IGpuExportableStorageImage`,
  `IGpuAccelerationStructure.IsSupported`, `Surface`.

## Architecture

New project **`src/Puck.Post/Puck.Post.csproj`** (`OutputType=Exe`), referencing the engine libs it needs (no
`Puck.Demo`). It mirrors the demo's shader-compile MSBuild target only for the one local `gradient.comp`.

Harness (all new, small):

- `interface IPostStage { string Name; PostTier Tier; PostStageResult Run(PostContext ctx); }`
- `enum PostVerdict { Pass, Fail, Infra, Skip }`, `enum PostTier { A, B, C, D }`
- `record PostStageResult(string Name, PostVerdict Verdict, string? Detail, string? ArtifactPath)`
- `PostContext` — the Vulkan host `IGpuDeviceContext` (resolved from the host capability), a **lazily-created**
  LUID-matched D3D12 device for Tier C, the `IServiceProvider`, the artifacts dir, and the aggregate accumulator.
- `PostBattery` — ordered `IPostStage[]`, each run in `try/catch` (an infra-fail in stage N is recorded as `Infra`
  without aborting N+1); folds verdicts (**`Infra`→exit 2 dominates, any `Fail`→1, else 0**; `Skip` neutral); writes a
  `PostReport` to `artifacts/post/`.
- `PostBatteryNode : IRenderNode` — on its first `ProduceFrame` it runs the battery, sets the exit code, and calls
  `ITerminalControl.RequestExit()` (the one-shot shape the demo gates use). Hosted offscreen by `Puck.Launcher`.
- `Program.cs` — minimal: parse `--artifacts <dir>`, `--tier A|B|C|D` / `--filter <substr>`; build the battery; launch
  the offscreen host with `PostBatteryNode` as root; return its exit code. (`--visual` is a deferred hook.)

Copied constants (values, not code): the ±1-LSB threshold sets from `Puck.Demo/ParityThresholds.cs` (`Continuous`,
`WorldComposite`, `WorldFuzz`, `Discrete`) and the metric shapes from `ParityMetrics.cs`/`ParityReport.cs`.

## Stage catalog (~19 stages; "Reference" = the `Puck.Demo` file to port from)

**Tier A — CPU pre-flight (self-tests run first; a determinism gate can't catch a wrong-but-deterministic op).**
- A1 fixed-point — `FixedQ4816` vs double ref. Ref `Replay/FixedPointSelfTest.cs`.
- A2 worldcoord3 — `WorldCoord3` correctness. Ref `Replay/WorldCoord3SelfTest.cs`.
- A3 determinism + replay — neutral fixed-point sim is deterministic; record→round-trip→replay bit-exact; every
  `CommandValueKind` round-trips. Ref `Replay/DeterminismGate.cs` (neutral sim, no `MiniActionWorld`).
- A4 CLI/STDIN determinism — scripted `Submit`→inject→snapshot session is deterministic + replays + measurably drives
  the sim. Ref `Replay/CliDeterminismGate.cs`.

**Tier B — GPU same-device (offscreen Vulkan host).**
- B1 compute — pipeline dispatch + readback. Local `gradient.comp`; ref `ComputeValidationNode.cs`.
- B2 resample — SAMPLED_IMAGE binding. `resample.comp`; ref `ResampleValidationNode.cs`.
- B3 viewports — generic compositor. `viewport-composite.comp`; ref `ViewportParityNode.cs`.
- B4 pixelate — pixelation decorator. `pixelate.comp`; ref `PixelateParityNode.cs`.
- B5 capture — native capture (environment-lenient: `Skip`, not `Fail`). `Puck.Capture`; ref `CaptureValidationNode.cs`.
- B6 split-coverage (**NEW**) — synthetic animated regions (merged↔split↔merged, counts 2/3/4); assert rects fully tile
  `[0,1]²` with no uncovered band at every ease and every active pane non-blank. World wiring from M3; ref
  `WorldProducerNode.cs:188-199,411-427`.

**Tier C — cross-backend (Vulkan host + LUID-matched D3D12, lazily created once, reset between stages; debug layer OFF).**
- C2 export — same-device export/import round-trip. `IGpuExportableStorageImage`; ref `ExportRoundTripNode.cs`.
- C3 reverse-share — D3D12→Vulkan share. Ref `CrossShareReverseNode.cs`.
- C4 indirect — GPU-driven indirect compute. `sdf-cull-args.comp`; ref `IndirectDispatchValidationNode.cs`.
- C5 world — cross-backend SDF world parity (hero view), ±1-LSB. Ref `WorldParityNode.cs` +
  `CrossBackendComputeWorldNode.cs` + `DirectXComputeWorldDevice.cs`.
- C6 world-child — as C5 but a viewport is a hosted child surface. `sdf-child.comp`; ref `WorldParityNode.cs` (withChild).
- C7 fuzz (fixed seeds) — a deterministic seed list of SDF programs is bit-equivalent (mod ±1-LSB) across backends. Ref
  `FuzzSdfProgram.cs`; `WorldFuzz` thresholds.
- C8 RT parity (**skip-with-note**) — ray-query/DXR world parity; explicit `Skip` when
  `IGpuAccelerationStructure.IsSupported` is false. `sdf-world-rt-debug.rq`; ref `RtWorldProducerNode.cs:150-157`.

*Dropped from pass 1:* C1 showcase parity (`--validate`, demo eye-candy) and A5 `--check-run` scene oracle (tests the
demo's JSON document funnel, which the POST does not use).

**Tier D — de-risked live subsystems.**
- D2 device-lost — inject loss, recover in-place, assert a simulated tick counter is preserved.
  `DeviceLostException`/`IDeviceLostRecoverable`; ref `Puck.Launcher/LauncherWindowHostedService.cs:251,279-292`.
- D3 hot-switch — switch backend at runtime and render a frame on the new backend. `Puck.Launcher/BackendSwitcher.cs`.

*Deferred:* D1 present/VRR cadence (no headless seam), D4 GPU-ms budget.

## Milestones (each shippable; coverage only grows)

- **M0** — `Puck.Post` exe + harness shell hosting a zero-stage battery offscreen via `Puck.Launcher`; exits 0, writes an
  empty report.
- **M1** — Tier A (A1–A4), pure CPU; proves verdict folding + report aggregation.
- **M2** — Tier B same-device (B1–B5); stands up the offscreen Vulkan host + readback + the copied `Parity*` substrate.
- **M3** — world render wiring (the heavy lift; `WorldProducerNode` is the reference) + B6.
- **M4** — Tier C cross-backend core (LUID D3D12 device; C5/C6/C2/C3/C4).
- **M5** — Tier C fuzz + RT (C7, C8 with the skip-with-note path).
- **M6** — Tier D de-risked (D2 device-lost, D3 hot-switch; register the DirectX presenter as a 2nd descriptor for D3).
- **M7** — reporting + polish (`PostReport` table, `artifacts/post/` layout, `--tier`/`--filter`).

## Risks & decisions (locked)

1. **Tier D process isolation** — D2 deliberately triggers device loss; run Tier D last and `try/catch` each stage. If
   in-process recovery proves flaky, fall back to a child-process sub-run for D2.
2. **Shared vs per-stage D3D12 device (C2–C8)** — shared + explicit reset between stages; per-stage fallback if
   shared-handle/descriptor-pool leaks appear. Debug layer stays OFF (it breaks `CreateDevice` on this box).
3. **Split-screen VRAM** — full-size per-view textures (the in-tree fix) quadruple source VRAM at 4 panes; fine at
   960×600.
4. **RT portability** — C8 must `Skip` (not `Fail`) when `IsSupported` is false.
5. **Reimplementation fidelity** — the standing cross-check is the matching `Puck.Demo --validate-*` gate; the POST's
   from-scratch stage must agree with its reference gate.

## Verification

- **Per milestone:** `dotnet run --project src/Puck.Post` exits **0** with every stage green; flip one
  threshold/constant → exits **1**; force a stage exception → exits **2** (other stages still run). Inspect
  `artifacts/post/`.
- **Tier A** runs anywhere (pure CPU). **Tiers B/C** require this RTX 4070 (Vulkan + D3D12); diff
  `artifacts/post/*-{vulkan,directx,diff}.png` against the calibrated thresholds.
- **C8**: RT is supported here, so it runs; verify the skip path by temporarily forcing `IsSupported=false`.
- **D2**: set `PUCK_TEST_DEVICE_LOSS`, confirm recovery + the preserved tick.
- **Cross-check**: the matching `Puck.Demo` gates still pass.
