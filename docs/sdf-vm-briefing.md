# The SDF VM — a briefing map

*Written 2026-07-08, at `main` = `53d16b2` (the alpha-prep squash). This is the
orientation document for the next SDF VM session: where things stand, what is
live, what is suspected, and what is deliberately weird. It synthesizes the
code, the two plan docs, the Post battery, every consumer, and the recent
commit history. Contract facts live in the `sdf-world` skill; plans of record
are [sdf-vm-evolution-plan.md](sdf-vm-evolution-plan.md) and
[sdf-accumulator-plan.md](sdf-accumulator-plan.md) — this map points at them,
it does not replace them.*

> **SESSION OUTCOME (2026-07-08, `features/sdf-fixes` through `6405cf5`,
> battery 49/49 green).** This map did its job and most of it is now history:
> the debug instrument (`sdf` mode, termination/slice views, `sdf.scope`,
> `sdf.bench`) exists; thread 2 (Xor) is settled EXEMPT; the ground-notch
> mechanism is settled (footprint/marchStart, not MaxSteps); the scoped
> accumulator (thread 1) is BUILT at depth 1 with SceneObject scoping; the D3
> trigger (thread 3) is MEASURED — the beam is O(instances), and the
> uniform-grid cull gates everything past ~256 live instances
> ([sdf-bench-notes.md](sdf-bench-notes.md)); shortlist wave 1 landed and was
> review-hardened; grid-locking shipped in both editors. The research record is
> [sdf-sota-survey.md](sdf-sota-survey.md) + [sdf-wiki/](sdf-wiki/README.md).
> Still open from this map: thread 4 (idle-ISA authoring catch-up), thread 5
> (UI convergence), the notch fix itself (now deterministically REPRODUCED —
> [examples/scripts/notch-repro.console](examples/scripts/notch-repro.console);
> the occluder attribution was refuted, see the Live-defects entry — the fix is
> a structural de-quantization call), and the uniform-grid cull arc.

## Session-start state (read first)

- `main` (`53d16b2`) carries the whole alpha-prep arc **unverified** — Post was
  not run after the squash merge. **First action of any engine session: run
  `dotnet run --project src/Puck.Post -c Release` and confirm the battery.**
  The evolution plan's last recorded count is 48/48.
- `origin/main` is one commit behind (`bfe8738`); nothing pushed.
- Parity posture is RELAXED by default (`mean≤0.35`; the fine ±1-LSB /
  isolation contracts bite only under `PUCK_PARITY_STRICT=1`,
  `ParityCheck.cs:197-213`). Never re-tighten unasked.

## The shape of the thing (60-second refresher)

One `uint[]` program: header → instruction headers → Data0/Data1 → materials →
per-shape bounds → **segment directory** (header `.y` lane = the per-program
Lipschitz `stepScale`) → **instance directory** (instances index *segments*,
never raw instructions) → world-segment list. Screen surfaces are a separate
fixed side table (always 8 entries).

Four compute kernels per frame: `sdf-beam` (tile cone-march + per-tile
instance bitmask) → `sdf-cull-args` (single-thread bbox reduction → indirect
args) → `sdf-world-views` (Keinert march, indirect, dithered store) →
`sdf-world-composite`. The fragment-shader path is deleted; compute is the
only path.

`SdfWorldEngine` owns three mutually exclusive submission models —
`RenderFrame` (wait, harness), `SubmitFrame` (fire-and-forget, live),
`SubmitFramePipelined`/`IsFramePixelsReady`/`AcquireFramePixels` (fenced,
bake preview) — guarded by a single-in-flight flag. Capacities freeze at
construction; `UploadProgram` rejects loudly.

Six consumers: Overworld (on-change rebuild, dirty-checked), World/sculptor
(`WorldSceneRenderer`, one instance per placement), Creator (parked-slot pool,
per-shape scoped Onion), Bake (persistent engine + pipelined readback),
Puck.Scene JSON (build-once), Town (build-time forge only — it authors
creations, never SDF directly).

## The live threads, ranked

### 1. The scoped accumulator — decided, costed, NOT implemented

The VM carries ONE running distance for the whole program; `ResetPoint` never
resets it. Intersection-family blends, `Xor`, and the FIELD ops
(`Onion`/`Dilate`/`Displace`) therefore compose against *everything emitted
before them*. The alpha-prep arc **mitigated** this (unmaskable instances,
`UnmaskableBoundRadius=1e30`, chamfer halo `1.70711×k`, guard gates in
`world-instanced`) but did not fix the model.
[sdf-accumulator-plan.md](sdf-accumulator-plan.md) records the decision:
**"Do it, at depth 1"** — `PushField(blendOp, smooth)`/`PopField` opcodes,
measured at **+7 DXIL instructions, 0 dx.op** at depth 1 (depth 4: +1.40%).
No such opcode exists yet in `sdf-vm.hlsli`'s `SDF_OP_*` table.

What it unblocks (all documented, all real):
- `SceneObject.Dilate`/`Onion` work only on the first object of a run document
  (`SceneObject.cs:38-51` says so explicitly).
- Creator's Intersection blend wipes the workbench (plan doc, "Open, filed
  elsewhere").
- Every creator shape with a non-zero onion is an always-evaluated unmaskable
  instance — the flat model's real running cost.
- Run-document nesting (stage 3 of the plan's staged landing).

The plan's own risk list is unusually candid — `AnalyzeLipschitz` becomes a
tree fold (chamfer POP needs `√2·max(L_parent, L_child)`; the doc says
"Expect the next bug here"), and nothing currently pins `blendSmoothUnion`'s
far/near endpoints, which a scope's first member depends on.

### 2. ✅ SETTLED: `Xor` is unmaskable-EXEMPT (verified 2026-07-08)

The suspicion (three readings converged on `Xor` as an unfixed accumulator-
reading sibling of the `d18d238`/`05d500b`/`449dc9b` class) is **refuted**,
both directions:

- **Algebra.** `max(min(acc,b), -max(acc,b))` reduces to `min(acc,b)` — the
  plain union — everywhere OUTSIDE the candidate (`b > 0`): the negated arm
  only wins when `acc + b < 0`, deeper inside a surface than a first-hit
  march ever samples. So a far candidate returns the accumulator exactly
  (union-grade far-exactness, no halo needed), and the extra surface Xor
  carves (the overlap hole) lives strictly INSIDE the union hull — inside any
  covering bound — so its tiles are never masked out.
- **Empirical (real GPU, the debug mode's slice view).** A box⊕sphere pair's
  exterior isolines are pixel-identical between `xor` and `union`; xor
  differs only inside the union hull. `debug.view.slice` + `sdf.blend
  xor`/`union` reproduces it in seconds.

So `HasUnmaskableCompose` omitting Xor and `MaxSmoothBlendRadius` giving it a
zero halo are **correct by design — do not "fix" Xor into the gate.** The one
real residue is a SIZING rule: an Xor member competes on the running `min`
wherever it is nearest, so its authored cull bound needs the UNION-style
generous influence margin, never the subtraction-style tight bound. Written
down in `SdfProgram.cs` (both gate methods' docs) and the `sdf-world` skill's
accumulator-rule note.

Related load-bearing exemption to keep honest: `DomainWarp` is deliberately
excluded (POINT op, never reads the accumulator — asserted three times
independently). Any *new* op must answer "does this read `result.distance`?"
before it ships.

### 3. The D3 trigger tension — the premise may already be stale

The evolution plan defers **D3 (hierarchical cone marching + instance BVH)**
"until complex scenes make it bite," citing the hero scene at ~1 ms
(CPU-sim-bound). But [game-studio-plan.md](game-studio-plan.md) and the
capability catalog both report the Puckton town **already** hitting the SDF
render-range ceiling — the reveal overview forces a COMPACT block *today*
(catalog: "Sculpted town 🔶 ⛔ far geometry exceeds the reveal overview's SDF
render range"). The consumer docs have outrun the producer doc. **Next
session should measure the town scene (`PUCK_TIMING=1` / Post `gpu-budget`)
and either promote D3 or record why the ceiling is a camera/content problem
instead.** D3's siblings ride it: segment tracing, per-tile Lipschitz
refinement, the compute shadow-cull.

### 4. The idle ISA — a renderer that outgrew its authors

Zero authored content uses: **LogSphere, CellJitter, RepeatPolar, DomainWarp,
Displace, SymmetryPlane (general form), Vesica, and the entire 2D family**
(RoundedRectangle/RegularPolygon/Star/Trapezoid/Ellipse). Only Post exercises
them. Not even the run-document JSON (`TransformOp.cs`, `SceneObject.cs`)
can express them — the accumulator plan and game-studio plan both name this
gap independently. Meanwhile the editor exposes *none* of the modifier verbs
("the renderer reserves two per-shape modifier slots — the editor exposes
none of them"). Three convergent asks, one thread: grow the run-document op
surface, grow `puck.creation.v1`, expose authoring verbs. This is where new
ISA work should pause and authoring work should catch up.

### 5. The UI convergence (deferred, but twice-asked)

Both [overworld-demo-plan.md](overworld-demo-plan.md) and
[game-studio-plan.md](game-studio-plan.md) (§W6) want the console/action bar —
eventually the whole editor — rendered *through* the SDF VM (screen-space
ortho, eventually an MSDF glyph op), retiring bespoke overlay shaders. Not
started; a natural successor to the 2D family. The deferred 2D-arc options
(flat-2D kernel, screen-space UI/glyph, gradient accumulator) feed this.

## Verification coverage (what Post proves, and the holes)

Gated well: every non-isometric warp has a **parity + Vulkan-only solidity
pair** (CellJitter, Displace, DomainWarp, TwistY, LogSphere) proving the
Lipschitz clamp's GPU consequence; `sdf-lipschitz` pins `stepScale` bits
CPU-side (hero == 1.0f exactly); instancing is gated at two scales with
instanced==flat bit-identical on Vulkan + the unmaskable-compose guard;
`world-swarm` double-renders for determinism.

**Named gaps (no stage anywhere):**
- `Intersection`/`SmoothIntersection` visual parity — Intersection appears
  only in the mask-correctness guard; SmoothIntersection appears **nowhere**
  in Post.
- `BendY`/`BendZ` (only BendX is gated), `Scale`, `RepeatLimited` (used only
  as scaffolding), `Dilate` (Onion is gated; Dilate is not).
- CellJitter's Gaussian flavor (covered by argument, not by a stage).
- Chamfer's √2 clamp has the CPU pin but no dedicated GPU solidity gate
  (small/pinned factor — probably fine, but it's an asymmetry).

The enclosed-hole caps vary per solidity stage (0.006–0.03), each individually
calibrated, no shared rule — fine, but don't "harmonize" them without
re-measuring.

## Known defects & deliberate weirdness (do not "fix" the deliberate ones)

**Live defects:**
- `SDF_WPG_P4G` renders as `p4` (no mirror classes survive;
  `sdf-vm.hlsli:466-478`) — recovering it is a redesign of the turn cocycle.
- Ground-plane tile-quantized notch — **REPRODUCED deterministically but the
  occluder attribution is REFUTED** (2026-07-08, deliberate-repro hunt;
  recipe: `docs/examples/scripts/notch-repro.console`, run with the new
  `sdf.cam <pitch|yaw|dist|target>` debug-camera pose verb). The `MaxSteps`-
  exhaustion hypothesis stays **REFUTED**: every ground pixel terminates cyan
  (footprint-adaptive hit) with ZERO red (steps-exhausted). What reproduces is
  a grazing-**horizon** tile-quantization: the far-ground silhouette against the
  sky steps in EXACT 16 px (one-tile) increments (measured top_y ∈ {16,32,48,64},
  vertical steps [16,16,16]) instead of a smooth perspective curve, and
  `debug.view.termination` shows the SAME tiles as a tile-quantized cyan
  (ground/footprint) vs dark-blue (escaped) boundary — the confirmed capture
  PAIR (`05-overworld-depth.png` + `06-overworld-termination.png`). But the
  "dips **behind foreground objects**" framing is WRONG: in a controlled debug
  scene (fullscreen `sdf` mode, tall box on a floor, grazing `sdf.cam`) a
  depth-diff against a no-occluder baseline is BYTE-IDENTICAL on the ground away
  from the occluder's own body (max diff 0) — an occluder does NOT dip the
  ground behind it, a grounded wall proves no empty gap for the four-bound
  teleport, and a floating slab (which DOES fire the teleport) still leaves the
  ground behind it unchanged (teleport lands at `secondEntry ≤ true re-entry`,
  provably not inflating the footprint). Open grazing ground and a steep-angle
  control both render SMOOTH depth — so footprint-adaptive termination is
  well-behaved; the artifact is the per-tile `marchStart`/beam-cull GRANULARITY
  leaking into the far horizon near `MaxDistance`, not an early-hit depth error
  and not occluder-amplified. Fix DEFERRED: no one-to-few-line footprint-
  threshold change addresses it (footprint is not the quantizer — the debug
  scene shows it produces smooth depth), and de-quantizing the horizon is a
  per-pixel-marchStart / sub-tile-beam restructure with hero-`world`-stage
  regression risk. Re-hunt from `notch-repro.console`.

**Deliberate — documented in-place, preserve on sight:**
- The rt-debug kernel diverges from the world path ON PURPOSE: `map()` not
  `mapMasked` (no tile cull), 6-tap normals ("migrating it would move every
  pixel"), no dither ("this kernel is a cross-backend PARITY probe"),
  brighter sky ("Not drift — do not 'reconcile'"), screens render flat black.
- `sdf-cull-args.comp.hlsl:10-14` re-declares `t0`/`t1` shadowing the VM's
  SRVs — a landmine if that kernel ever grows a `map()` call; the fix is to
  split the layout, never renumber.
- `BendZ` keys on `y`, not `z` (documented QUIRK, `SdfOp.cs:20-22`); the three
  Bends have distinct Lipschitz norms; using twist's norm for a bend
  under-clamps by up to 24% and holes the march.
- Op ids 13-15 and TwistY's old slot 4 are permanently retired, never reused.
- `Ellipsoid`'s SDF is approximate (can underestimate) — never earns a cull
  bound; prefer the exact 2D `Ellipse` revolved where a spheroid is meant.
- `SdfEngineNode.rayQueryEnabled` is threaded through but consulted by no
  render path (`SdfEngineNode.cs:140-146`) — vestigial, kept deliberately.

## Foot-guns for whoever touches it next

- **Host-baked constants**: builder methods bake reciprocals/norm factors that
  the HLSL decode assumes (Scale, Repeat, CellJitter, RepeatPolar, RoundCone,
  Capsule, Ellipsoid, LogSphere's `w=ln(shellRatio)`). A mismatched edit
  desyncs silently — no compile error. The contract pairs are enumerated in
  the `sdf-world` skill; keep both sides in one commit.
- **The step-clamp divide-back**: `stepScale` is applied ONCE at `mapCore`
  return; any consumer comparing the returned distance to world-space
  quantities must divide it back out (soft shadows already do —
  `sdf-world.hlsli:411-436`; the chamfer clamp once darkened shadows ~30%).
- **Capacity probes are manual**: every consumer that grows its emission must
  grow its worst-case probe (`EmitProbe`, `MeasureWorstCaseEnvelope`, etc.) —
  enforced only by comments. (This bit the world sculptor once already.)
- **Builder exception safety**: `Instance`/`DynamicInstance` leave the builder
  with an open instance if `emit` throws — discard the builder, don't reuse.
- **`ScreenSlab` has 3 overloads** with materially different `Material` id
  encoding; the wrong one silently loses screen sampling.
- **New soft-blend families need their own halo derivation** — the
  ChamferUnion `1.70711×` vs `1×` asymmetry is exactly the kind of margin a
  copy-paste would re-break.
- **Duplicated authoring code**: `CreatorSceneRenderer.EmitShape` and
  `CompanionRenderer.EmitShape` are admitted near-clones; per-consumer
  bound-margin constants are re-picked everywhere. A shared emission seam is
  cheap consolidation *when next in there* — not urgent.

## Screen-surface budget (for anything diegetic)

8 slots total. Cabinets own 0–3; Creator's easel **borrows slot 3** while
active (that cabinet degrades to flat lit material); 4–7 are unclaimed
headroom behind `ScreenSlotLedger` (`ScreenMuxHeadroomStart=4`). Camera feeds
cap at 4 (`CameraFeedPool`), excess narrates a drop. Densest real usage seen:
5 of 8.

## A sane next-session menu

1. **Verify the merge**: run Post on `53d16b2`; push if green.
2. ~~Settle the Xor question~~ — DONE 2026-07-08 (thread 2: exempt, proof
   written down in `SdfProgram.cs` + the `sdf-world` skill).
3. **Pick the arc**: (a) land the scoped accumulator (thread 1 — the engine
   stage is fully designed and costed, gates enumerated in the plan), or
   (b) measure the town and settle D3's trigger (thread 3), or (c) the
   authoring catch-up (thread 4 — run-doc ops + creator verbs, no new ISA).
   (a) and (b) are engine work with Post gates; (c) is demo-side and
   verify-by-running.
4. Cheap gap-closers if a Post session happens anyway: an
  Intersection/SmoothIntersection parity scene; BendY/BendZ/Dilate coverage.
