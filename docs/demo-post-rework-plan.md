# Plan: rework Puck.Demo into an engine POST (power-on self-test)

This document is the agreed plan for reworking `Puck.Demo`. It travels with the branch. Status: **plan drafted, awaiting
author sign-off on the open decisions at the bottom.** Grounded in a full inventory of the current demo + root-causes of
the two known bugs (2026-06-22).

## Vision (author)

Rewrite `Puck.Demo` into an engine **POST** — a *unit-test singularity* where a **single run stresses nearly every core
subsystem** the final game will need, with one aggregated pass/fail. The ~13 `--validate-*` gates + determinism/parity/
fuzz checks **fold into** the POST. Cross-backend (D3D12↔Vulkan zero-copy) is **kept as a POST stage** but need not be used
in the action game (which runs same-device). "Completely rewritten without losing the essence." Fix two known bugs along
the way: (1) collision registers against the cube **center** not its **edges**; (2) split-screen **blanks half the screen**
when players enter certain regions.

## The POST concept

One process entry — a new `--post` flag mapped (via `DemoRunDocuments.Synthesize`) to a single `PuckRunDocument` with a
`Validation` section named `"post"`, which registers ONE root `PostBatteryNode`. It rides the existing data-driven spine
verbatim (`Program.cs` flags→document→`DemoRootNode.RegisterRunDocument`→`CreateValidationNode`) and reuses the
`ParityResult`/`ParityReport`/`ParityThresholds` substrate as the cross-stage carrier (default `ExitCode=2` already fails
loudly on a crashed/incomplete battery).

- **Single run, staged.** `PostBatteryNode` owns an ordered `IPostStage[]`, runs them on its first `ProduceFrame` (the
  canonical gate shape: run-check-write-`RequestExit`), folds verdicts (max → any infra=2 dominates, any fail=1) into the
  shared `ParityResult`, propagated at `Program.cs:268`.
- **Ordered fast→slow tiers.** A = pure CPU pre-flight; B = same-device GPU smoke (Vulkan host); C = cross-backend
  (Vulkan + LUID-matched D3D12); D = live-only subsystems (pacer/VRR, device-lost, backend hot-switch), time-boxed.
- **Headless + visual.** Default `--post` is headless (forces offscreen Vulkan host, writes `artifacts/post/`, exits 0/1/2).
  `--post --visual` additionally drives the live MiniAction + pacer against a real window, but the verdict stays headless
  (CI determinism).
- **Deterministic.** Tiers A–C are fixed-frame / fixed-seed / fixed-point with ±1-LSB tolerances. The fuzz stage uses a
  fixed seed list (a range still routes to `tools fuzz`). Tier D is time-boxed and asserts invariants, not exact pixels.

Essence preserved unchanged: the SDF VM + `WorldProducerNode` renderer, the MiniAction game, cross-backend zero-copy, and
the determinism/replay spine — just sequenced under one self-checking run.

## Coverage matrix (current entry → POST stage)

**Tier A — CPU pre-flight:** `--validate-determinism`→A1; `--validate-cli-determinism`→A2; `--validate-mini-action`→A3;
FixedPoint+WorldCoord3 self-tests→A4; `--check-run` scene oracle→A5; **NEW** collision-correctness→A6 (bug 1 gap); **NEW**
split-coverage→A7 (bug 2 gap).
**Tier B — GPU same-device (Vulkan):** `--validate-compute`→B1; `--validate-resample`→B2; `--validate-viewports`→B3;
`--validate-pixelate`→B4; `--validate-capture`→B5 (lenient/skip on headless desktop); `--capture` folds into B-tier readback.
**Tier C — cross-backend (Vulkan + LUID D3D12):** `--validate`(showcase parity)→C1; `--validate-export`→C2;
`--validate-reverse-share`→C3; `--validate-indirect`→C4; `--validate-world`→C5; `--validate-world-child`→C6; fixed-seed
`--fuzz-seed`+world→C7; **NEW** RT parity (ray-query/DXR, skip-with-note on non-RT adapter)→C8.
**Tier D — live-only, no gate today:** present/VRR cadence→D1; device-lost inject+recover (assert sim-tick preserved)→D2;
backend hot-switch + render-a-frame→D3; optional `PUCK_TIMING` GPU-ms budget→D4.
**Modifiers** (`--backend`/`--produce`/`--present-mode`/`--surface-format`) become battery config (e.g. run Tier C per
produce path); `--run`/`--emit-schema` stay standalone utilities. Net ≈ 21 stages subsume all 13 gates + utilities + fuzz +
every live producer's renderer + 3 untested live subsystems + 2 new bug-gap gates.

## The two bugs — root cause + fix

### Bug 1 — collision against the cube center, not the edge
**Cause:** `FixedRoom.From` (`src/Puck.Demo/MiniAction/PlatformerBody.cs:27-33`) builds the wall clamp planes folding in
ONLY the player half-extent (`MaxX = BoundsMax.X - halfX = 8 - 0.35 = 7.65`). But the visual wall
(`MiniActionFrameSource.cs:167`) is a box of half-extent `wallThickness=0.3` (a renderer-local, `:155`) centered on `maxX=8`,
so its inner face is at `7.7`. The player center stops at `7.65` → its right face reaches `8.0` = the wall *centerline*,
burying the edge `0.3` into the wall. The wall thickness is a renderer-local never shared with `MiniActionRoom`, and the
determinism gate only compares state-hashes between runs, never the physical resting position — so it slipped through.
**Fix:** (1) add `WallThickness` to `MiniActionRoom` as the single source of truth; (2) subtract `wall + half` on all four
clamp planes in `FixedRoom.From`; (3) replace the `MiniActionFrameSource` local with `m_room.WallThickness`; (4) **NEW POST
Stage A6**: drive a body into each wall at full speed, assert the resting face is flush with the wall inner face.

### Bug 2 — split-screen blanks half the screen
**Cause:** `WorldProducerNode.EnsureResources` runs ONCE and early-returns forever (`WorldProducerNode.cs:339`), baking the
Stage-2 composite rects (`BuildCompositePush`, only call site `:390`) AND the per-view source-texture sizes (`:410-414`) to
the FIRST frame's regions. But `CameraDirector` animates the layout every frame and snaps to its mode on frame 0. Stage 1
repacks the live region each frame, but Stage 2 reads the FROZEN rect from a texture allocated at the frozen size →
mismatch. Two triggers: (A) open split then merge → frozen half-rects composite stale/blank; (B) open merged then spread →
panes got 1×1 (zero-area first-frame) textures and "first-rect-wins" locks the second pane out → blank/OOB.
**Fix:** (1) call `BuildCompositePush(frame)` EVERY frame in `ProduceFrame` (right after `PackViewports`, ~`:188`);
(2) size each per-view source texture to the MAX extent a pane can reach (full `m_width,m_height` for the animated split);
(3) **NEW POST Stage A7**: sweep `CameraDirector` across the full hysteresis band both directions (spread crossing
`SplitEnterSpread=3.5` up, `SplitExitSpread=2.0` down) for counts 2/3/4, assert the rects fully tile `[0,1]²` with no
uncovered band at every ease, and an offscreen render shows every active pane non-blank. Layout is presentation-only (not
in `StateHash`), so the determinism gate stays green.

## Architecture

- **Entry:** add `--post` (Program.cs) + a `"post"` gate name; it auto-forces the offscreen Vulkan host and propagates
  the exit code through the existing funnel — no new imperative path.
- **Stage abstraction:** `interface IPostStage { string Name; PostStageResult Run(PostContext ctx); }` +
  `record PostStageResult(string Name, int Verdict, string? Detail, string? ArtifactPath)`. `PostContext` carries the GPU
  device(s), the LUID-matched D3D12 factory, the service provider, the artifacts dir, and the shared `ParityResult`.
- **Adapt, don't rewrite:** each existing `*ValidationNode`/`*ParityNode` already does run-check-write-RequestExit; factor
  its check body into `Run(PostContext)` callable both standalone (keep `--validate-X` as dev shortcuts) and from the
  battery. The 13-way `CreateValidationNode` switch becomes the stage registry.
- **`PostBatteryNode : IRenderNode`** holds the staged `IPostStage[]`, runs + aggregates + writes a `PostReport` table to
  `artifacts/post/`, then `RequestExit`. Tier D stages run as time-boxed offscreen sub-runs reusing the launcher loop.
- **Keep:** SDF VM, `WorldProducerNode`, MiniAction sim, cross-backend services, `ParityResult` substrate, the live
  console/cursor shell. **Evolve:** the option surface conceptually collapses to `--post` (gates stay as shortcuts).
  **Delete:** nothing structural up front; thin the per-gate `ProduceFrame` wrappers to `Run(PostContext)` last.

## Staged migration (each milestone shippable, coverage never regresses)

- **M0 — scaffold:** `IPostStage`/`PostContext`/`PostBatteryNode` skeleton + `--post` wired to a zero-stage battery. Verify
  `--post` exits 0; all `--validate-*` unchanged.
- **M1 — CPU pre-flight (Tier A):** adapt the 3 determinism gates + 2 self-tests + `--check-run` → A1–A5.
- **M2 — fix bug 1 + Stage A6:** WallThickness fix; A6 fails before / passes after; `--validate-mini-action` stays green.
- **M3 — fix bug 2 + Stage A7:** per-frame layout + full-extent textures; A7 detects the uncovered band on old code; eyeball
  `--world-split`/`--mini-action` shows no blank half.
- **M4 — GPU smoke (Tier B):** adapt compute/resample/viewports/pixelate/capture → B1–B5 under one offscreen Vulkan host.
- **M5 — cross-backend (Tier C):** adapt parity/export/reverse/indirect/world/world-child → C1–C6; add fixed-seed fuzz C7
  + RT parity C8 (skip-with-note on non-RT). Reuse one LUID-matched D3D12 device.
- **M6 — live-subsystem self-checks (Tier D, the genuinely new coverage):** D1 pacer/VRR cadence, D2 device-lost
  inject+recover (the `DeviceLostException`/`IDeviceLostRecoverable` injection seam already exists — built this session via
  `PUCK_TEST_DEVICE_LOSS`), D3 backend hot-switch+render, optional D4 GPU-ms budget.
- **M7 — visual mode + reporting:** `--post --visual`; finalize the `PostReport` table + `artifacts/post/` layout.
- **M8 — consolidate (optional):** make `--post` THE showcase entry; thin gate nodes to their `Run` seam.

## Risks & open decisions (need author input)

1. **Process isolation for Tier D.** Running 21 stages in one process means a GPU crash/device-loss in stage N can poison
   N+1. Recommend: Tiers A–C in one process; run Tier D (which deliberately triggers device loss) LAST or in a child
   process; try/catch each stage so one infra-fail (verdict 2) is recorded without aborting the rest.
2. **Shared vs per-stage LUID D3D12 device** across C1–C8. Recommend shared + explicit reset between stages (perf), fall
   back to per-stage if descriptor-pool/shared-handle leaks appear. Keep the debug layer OFF (it breaks `CreateDevice` on
   this box).
3. **Tier D is genuinely new test code** (pacer/device-lost/hot-switch have no gate today) — highest uncertainty. The
   device-loss injection seam now EXISTS (this session), so D2 is de-risked; D1 (headless cadence assertion) is the open
   one. Recommend M6 be de-riskable/deferrable.
4. **Split-screen fix VRAM:** full-size per-view textures quadruple source-texture VRAM (4 panes). Fine at 960×600/4
   players; "recreate on growth" is more code for less memory. Recommend full-size.
5. **RT stage portability:** C8 must SKIP-WITH-NOTE on a non-RT adapter, not fail.
6. **"Essence" scope — THE big decision:** this plan EVOLVES the existing data-driven spine (the `PuckRunDocument` funnel,
   `ParityResult` substrate, gate nodes) rather than greenfield-rewriting, because that spine is already the right POST
   backbone and a from-scratch rewrite risks losing the ±1-LSB-calibrated thresholds and hard-won cross-backend plumbing.
   Confirm: evolution-in-place (recommended) vs a literal new project.
7. **Capture stage** stays environment-lenient (skip, not fail, on a headless/secure desktop).
