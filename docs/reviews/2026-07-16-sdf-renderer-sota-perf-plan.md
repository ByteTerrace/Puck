# SDF renderer performance plan — SOTA-informed, measurement-gated

Date: 2026-07-16. Scope: `src/Puck.SdfVm` (engine contract, Post-gated).
Inputs: a fresh structural map of the renderer, the internal verdict ledgers
(`docs/sdf-backlog.md`, `docs/sdf-wiki/`, `docs/sdf-bench-notes.md`), and a
fact-checked external survey (105-agent deep-research pass; sources cited
inline).

**Standing constraint:** no major feature drops. Every lever below is a
refactor or an addition behind the existing observable-behavior gates (Post
battery, solidity, parity families, bit-identity culling contracts).

**Supergreen (owner order, 2026-07-16):** no decision in this plan is — or
may later be — justified by "might break existing consumers" or "might change
deterministic hashes." There are no external consumers; internal callers are
rewritten in the same change. Hash and golden-capture movement is an expected
outcome of any correction or codegen change: re-run the relevant tier to
prove determinism still holds, re-record what moved, and never shrink a
change to keep a hash stable. Where this plan demands bit-identity, it is a
*correctness proof* (a cull that must not drop geometry, an idle-lane exit
that must not change a hit) — never a stability promise.

**Standing caveat (owner ruling, 2026-07-16):** historical performance numbers
in this repo — the 2026-07-11 room-perf audit attributions, the bench ledger
figures, and the magnitudes behind "measured no-go" verdicts — were not
cleanly measured. This plan quotes them only as *hypotheses to re-verify*.
Structural reasoning from those verdicts is kept; their cost models are not.
Nothing in Phases 2+ is committed until Phase 0 re-attributes cost on clean
methodology.

---

## 1. Where the cost is (structural facts, verified in code 2026-07-16)

The per-frame pipeline is five compute passes: `mask` → `beam` → `cull-args`
→ `views` → `composite` (`SdfWorldEngine.Record`). The structural cost
centers, independent of any historical timing:

| # | Cost center | Where | Structural observation |
|---|---|---|---|
| C1 | Per-pixel field-eval count | `sdf-world.hlsli` `renderView` | A lit pixel pays: primary march (≤160 `mapMasked`, Bán auto-relaxed), soft-shadow march (≤48 `mapDistanceMasked` + a full grid gather traversal), 3 AO taps, 1 analytic-normal dual, 1 coverage probe — ~50–200+ full field evaluations |
| C2 | Interpreter register pressure | `sdf-vm.hlsli` `mapCore` | ~30-case op switch + ~17-case shape switch + nested double blend switch; live state spans position/scale/step-bound/material/blend/scope statics. The full-interpreter occupancy note (~38% CS-warp, ~72% RF) is a hypothesis to re-measure, but the live-state footprint is a code fact |
| C3 | Gradient dual pays full freight | `sdf-vm.hlsli` `mapGradCore` | The analytic-normal twin has **no rigid-leaf fast path** — rigid segments that bypass the op switch in `mapCore` walk the full switch in the gradient dual, per lit pixel |
| C4 | Fixed 32-iteration screen-light loop | `sdf-world.hlsli` `renderView` light loop | Loop bound is `SdfScreenLightEnv = 32` per lit pixel regardless of how many screens are actually bound; only an early-`continue` inside |
| C5 | Unconditional per-frame uploads | `SdfWorldEngine.PrepareFrame` | Screen-surface table and screen-light buffer re-upload every frame with no change detection (the decal buffer already has the dirty-flag pattern) |
| C6 | Beam lane utilization | `sdf-beam.comp.hlsl` | One tile per warp (`numthreads(1,1,1)`), a deliberate anti-lockstep trade — structurally 1/32 lanes active |
| C7 | Serial reduction between passes | `sdf-cull-args.comp.hlsl` | Single 256-thread workgroup strides the whole viewport×tile space; a fixed serialization point on the frame's critical path |
| C8 | Header decode reload tax | `sdf-vm.hlsli` header decode | DXC runs no GVN over `StructuredBuffer` loads (documented in-file); `sdfWords[0]` pointer-chasing re-derives offsets per evaluation |
| C9 | Offscreen views multiply everything | `Views/ViewStack.cs` | Each budgeted view resolve is a full 5-pass chain; budget 4/frame round-robin; no "is any screen actually sampling this view" gate at the ViewStack tier |

## 2. What the verified SOTA says (and how it maps here)

Seven findings survived 3-vote adversarial verification. Mapped onto this
renderer:

1. **MPR — GPU interval arithmetic + per-region tape shortening** (Keeter,
   SIGGRAPH 2020; mattkeeter.com/research/mpr). Interval evaluation over a
   shallow spatial hierarchy both culls empty regions and rewrites a
   *shortened per-region tape* (~100× expression reduction in one benchmark);
   needs only C0 continuity. Mapping: our uniform-grid mask **is** tape
   pruning at instance granularity — the internal refutation of per-tile tape
   pruning for flat room programs (284 single-segment instances) stands,
   because there is nothing to shorten when each instance is one segment. The
   unexploited axis is **within long multi-segment chains** (placed
   creations, forge bakes) — exactly backlog item 1.
2. **Synchronized tracing** (Aydinlilar & Zanni, ACM TOG 2025;
   dl.acm.org/doi/10.1145/3702227). 8×8-pixel tile buckets, a tile A-buffer
   of per-primitive entry/exit depth fragments, workgroup-shared depth
   ranges, subgroup-coordinated termination, on-the-fly tree pruning:
   ~10× over non-synchronized per-ray processing; beats MPR 62–89% on
   union-heavy/multiscale scenes. Mapping: our beam+mask already banks the
   coarse version of this win. The incremental, unbanked pieces are
   **per-tile depth intervals** (the mask is currently binary per instance,
   with one conservative march start + gap teleports per tile) and
   **subgroup-coherent march/termination** in views.
3. **Fidget — measured interpreter overhead** (Keeter 2024;
   mattkeeter.com/research/fidget-2024.pdf). JIT-compiling the tape beats the
   bytecode VM 2.4–2.6× on CPU rasterization; overhead attributed to opcode
   dispatch and value round-trips through memory. Verifier caveat: figures
   are CPU-side; on GPU the analogous costs are register pressure and switch
   footprint, not branch prediction. Mapping: this bounds the win available
   from **per-program kernel specialization** — the GPU analog of JIT — and
   says it is worth pursuing only if Phase 0 confirms the interpreter (not
   the eval count) is the limiter.
4. **Segment tracing — local directional Lipschitz bounds** (Galin et al.,
   CGF 2020; hal.science/hal-02507361). 1–3 orders of magnitude fewer field
   queries; ~6→67 Hz in the paper's GPU port. Two verified caveats that gate
   it here: benefit is *limited or negative* when the Lipschitz bound is
   uniform over the support (most of our content is factor-1), and it is
   **not composable with global over-relaxation** (our shipped default).
   Mapping: a conditional, per-warped-segment tier at most — after backlog
   item 1, per the existing backlog #2 ordering.
5. **Auto-relaxed sphere tracing** (Bán & Válasek, EG 2023 Short). Already
   shipped as our default primary march. No action.
6. **Keinert disjoint-sphere fallback** (Enhanced Sphere Tracing, 2014).
   Already shipped as the over-relaxation correctness contract. No action.
7. **In-tree proxy/LOD acceleration nodes** (Hubert-Brierre et al., CGF 2025;
   perso.liris.cnrs.fr/eric.galin/Articles/2025-lod.pdf). Cheap conservative
   stand-in nodes embedded *in the SDF construction tree itself* — no
   external octree/BVH — measured up to ×439 GPU sphere-tracing speedup on
   3889-node scenes (×2–×12 on small models; the big factors need big trees).
   Mapping: this is the strongest external validation of our doctrine (the
   grid + in-ISA structure, no external hierarchy) *and* of extending it: a
   **Proxy segment guard** in the ISA is the in-ISA generalization of what
   `SampledRegion` already does for carve sets, and subsumes backlog item 1's
   per-segment bounds.

Bottom line: the literature does not contradict a single shipped
architectural choice. It says the growth axes are (a) within-chain
specialization for big programs, (b) depth-interval + subgroup coherence in
the tile machinery, (c) kernel specialization to relieve the interpreter
ceiling — each contingent on clean measurement.

## 3. The plan

Phases are ordered by dependency, not strict sequence — Phase 1 items can
land while Phase 0 runs. Every phase names its gate. Structural refactors are
in scope everywhere (supergreen: no compat shims, callers updated in the same
change).

### Phase 0 — clean re-baseline (the gate for everything after Phase 1)

The owner has ruled historical numbers dirty, and (2026-07-16) that TRUE
timing measurements require a human in the loop: unattended agent-run bench
numbers are sanity signals only — they gate nothing and are never committed
as attribution. Phase 0's measured deliverables are executed by the owner
against the protocol below. Before any lever is sized or ordered:

- **Methodology pins:** immediate present, fixed `room.bench`/`sdf.bench`
  camera poses, validation layers off, within-session paired A/Bs only,
  locked clocks where admin allows (beam drifts with DVFS — treat beam as
  the clock canary), warm pipelines/uploads before sampling, sub-ms deltas
  are noise until repeated pairs agree (`docs/sdf-bench-notes.md` hygiene
  section is the checklist).
- **Nsight captures** per `docs/sdf-shader-profiling.md` (the
  `tools/prepare-sdf-nsight-shaders.ps1` overlay): flame graphs, register
  count, occupancy, stall reasons for `views` and `beam`, on the real
  Puck.World 128-avatar workload and on controlled `sdf.bench` postures
  (`rigs 128`, `carves` clustered+scattered, `storm`, the room).
- **Missing instrumentation to add first** (small, engine-side, dev-gated):
  a per-pixel eval-count/step-count debug heatmap (extend the existing
  termination debug view) and optional per-feature GPU counters via the
  existing bench-lever rows, so primary/shadow/AO/normal/light-loop cost can
  be attributed by *subtraction A/Bs* (the levers already exist:
  disable-soft-shadows / disable-AO / disable-screen-lights / fast marchers).
- **Deliverable:** an attribution table — per workload, per pass, and within
  views per feature — committed to `docs/sdf-bench-notes.md` as the new
  baseline chapter, replacing reliance on the 2026-07-11 audit numbers.
- **Hypotheses to confirm or kill** (from the dirty ledgers): views is
  register-pressure-limited and cache-resident; the scale-flat views floor is
  the shadow march; shadow cost scales with marched-set size at ~constant
  per-instance cost; the per-pixel gather already picks near-minimal sets.

### Phase 1 — no-regret structural fixes (land now, verify with Post + demo)

Each is cheap, feature-preserving, and correct regardless of what Phase 0
finds. Each still gets a paired A/B so the baseline chapter stays honest.

1. **Screen-light loop → live-mask loop** (C4). The bound-screen set is
   already in-shader as the `params.screenMask` push-constant uint: loop to
   `firstbithigh(screenMask) + 1` (or iterate set bits) instead of a fixed
   32. No new packing, no new KEEP-IN-SYNC lane — and bit-identical *always*
   (the skipped tail slots were already no-ops via the early-`continue`).
   Gate: `world-screen` family bit-identity.
2. **Dirty-flag the screen-SURFACE upload** (C5). Copy the decal buffer's
   dirty-gate pattern for the surface table, which is mutated only through
   `SetScreenSurface`. The screen-LIGHT buffer is deliberately excluded:
   `PackScreenLights` also carries per-frame env (ambient/sun scale), grid,
   bench, and shadow-proxy rows — the overworld genuinely dirties it most
   frames, and the buffer is ~624 bytes, so a change-gate there buys nothing
   and risks rendering stale env state. Gate: existing world-screen stages;
   a moving-screen scene must stay bit-identical.
3. **Rigid fast path for `mapGradCore`** (C3). The gradient of a rigid leaf
   is the shape-local gradient (`evaluateShapeGradient`) rotated to world by
   the leaf's rotation — for static leaves the baked quaternion, for
   `TransformDynamic` leaves the per-frame composition
   `dynamicOrientation ∘ leafQuat`. This is net-new dual code (the rigid-plan
   walk currently exists only in `mapCore`; the dual has no paired skeleton),
   so it must be built as a mirror of the primary rigid walk and added to the
   KEEP-IN-SYNC pair list. It makes analytic normals cheap exactly where the
   primary march already is — the avatar-fleet posture. Gate:
   `world-analytic-normal` + rigs bench A/B; expect ±1-LSB re-roll within the
   calibrated families.
4. **Hoist the header decode across the march** (C8). The decode is already
   hoisted to the top of each `mapCore` call; the residual tax is
   re-derivation once per call — i.e., per march *step*. The fix is threading
   the decoded offsets through as a parameter struct so a marcher derives
   them once per ray, not once per step. Do NOT reach for push constants:
   `sdf-vm.hlsli` is included by non-world kernels with their own push
   layouts (e.g. the rt-debug kernel), so a world-layer push read inside
   `mapCore` inverts the layering. Gate: full battery; expectations per
   parity families. (The old "mapCore preamble GVN" refutation was about
   compiler behavior, not hand-hoisting — and its magnitude is unverified
   either way; do it for code health, measure honestly, keep if
   non-negative.)
5. **ViewStack liveness predicate** (C9). `ViewStack.RenderFrame`
   *deliberately* has no intrinsic "is anybody watching" gate — views are
   resolved by NAME, and a wired-screen check cannot see by-name consumers
   (a `ViewTransition` sampling a view for its own layout would be silently
   starved; the rationale is documented in ViewStack.cs). So the gate must
   be **consumer-supplied**: the registrant attaches an optional liveness
   predicate at `Register` time (or simply calls `Release`/re-`Register`,
   which the overworld's wire-driven release already does). The engine never
   guesses. A skipped view serves its cached surface, never black. Gate:
   demo/World run including a name-only consumer (a transition sampling an
   unwired view must keep rendering); `world-screen` stages. Note this is
   NOT a bit-identity change — a gated view intentionally serves a stale
   image while unwatched.

### Phase 2 — the interpreter ceiling (gated on Phase 0 confirming views is
register/occupancy-limited)

The register-pressure hypothesis has the strongest structural support (C2)
but a dirty magnitude. If clean counters confirm it:

1. **Curated kernel-variant ladder** (extends the shipped Full/CoreOps pair;
   this is the queued ISA-profile arc's kernel-variant axis, and the
   backlog's "register-pressure reduction through curated kernel variants"
   priority). Generalize `SdfViewsKernelVariants.Select` from a 2-way enum to
   a capability-profile match over a small precompiled set — candidates:
   `rigid` (Reset/Translate/Rotate/TransformDynamic + core shapes only; the
   avatar-fleet posture), `core` (today's CoreOps), `core+folds`
   (Repeat/Symmetry/Polar/Wallpaper, no warps), `full`. All compiled at build
   time (no runtime DXC), selected at `UploadProgram` exactly like today.
   Keep the beam on the full kernel (the core-beam refutation's *reasoning* —
   the cone march is latency- not register-bound — is plausible; re-verify
   its magnitude in Phase 0). Gate: every variant must pass the battery on
   programs it claims; selection is a pure function of the instruction
   stream; `world-*` stages already exercise both existing variants.
2. **Per-program kernel specialization (the GPU JIT analog)** — only if the
   variant ladder still leaves a confirmed gap. Generate program-specific
   HLSL (inline the op sequence, constant-fold the data lanes, drop the
   switch entirely), compile with DXC at runtime asynchronously, render on
   the interpreter until the specialized pipeline is ready, swap
   atomically at a frame boundary. Fidget's 2.4–2.6× CPU figure bounds
   expectations; on GPU the win is occupancy, not dispatch. Real engineering
   costs: runtime DXC dependency on both backends, shader-cache lifecycle,
   and the stale-bytecode class of bugs. Parity/hash movement is NOT a cost:
   specialized codegen re-rolls the ±1-LSB distribution and may shift golden
   captures — that is an expected outcome; recalibrate the families and
   re-record goldens in the same change, exactly as every interpreter growth
   already does. The interpreter stays for functional reasons only — it is
   the render path while a specialization compiles and the diagnostic
   comparison when a specialized kernel misbehaves — not as a permanent
   compatibility anchor; if specialization proves universally better, the
   owner can retire the interpreter path outright. This is a big swing; it
   needs its own design review before implementation.
3. **March/shade split** (backlog #3, conditional). Split views into a
   distance-only march kernel (small live state) writing hit-t/instance/
   material, and a shade kernel doing normal/shadow/AO/lights. It reduces
   *register pressure per kernel*, not eval count — worth building only if
   Phase 0 shows the march phase (not the epilogue) is the occupancy victim
   and the variant ladder hasn't already recovered it. The shade kernel
   re-marching nothing is the design constraint (hit data must carry what
   shading needs).

### Phase 3 — the shading epilogue (gated on Phase 0 attribution of
shadow/AO/normal shares)

The eval-count math (C1) says the epilogue plausibly dominates lit pixels.
**Measured 2026-07-17 (the baseline chapter, Puck.World product posture,
124 crowd):** shadows off→medium = +3.62 ms, off→high = +4.68 ms; AO on =
+0.74 ms; the exact tiers ≈ +1.5 ms each; and — load-bearing for this
phase's ordering — **forcing the exact per-pixel gather vs the camera-tile
mask is NOISE-LEVEL**, meaning the mask/gather *mechanics* are not the cost;
the march itself (steps × field evals) is. Consequently lever 1 below
(register-resident gather list) is DEPRIORITIZED at World's posture — its
target machinery is already free there — while march-length work (step/reach
tiers, far-field termination, and Phase 5's depth intervals — see the
empty-plaza anomaly in the baseline chapter) carries the measured weight.
The levers, in re-verified priority order:

1. **Register-resident shadow set.** `sdfShadowGather` already builds a
   per-pixel local mask; emit it instead as a compact ≤N index list held in
   registers so each of the ≤48 shadow samples iterates the list directly
   rather than re-scanning mask words. (The 2026-07-11 redirect lever;
   set-size reasoning survives, magnitude re-measured.) Gate:
   `world-shadow-cull` bit-identity (same set, cheaper iteration).
2. **Shadow march quality tiers as data.** `ShadowSteps`/`ShadowMaxDistance`/
   penumbra sharpness become per-frame quality fields (they largely already
   ride bench rows) with shipped-default picks owned by the owner —
   presentation-side, re-capture goldens when defaults change. No structural
   risk; pure knob hygiene.
3. **Do not rebuild tile-granular shadow supersets.** The wave-3 no-go's
   *reasoning* (any tile-level superset inflates the per-pixel marched set;
   penumbra-cone gathers are near-minimal) is sound independent of its dirty
   magnitudes. Re-open only if Phase 0 shows the gather traversal itself
   (not the march) dominating — in which case the fix is a cheaper gather
   (e.g., coarser grid walk), not a superset mask.
4. **AO stays 3-tap** unless a quality arc asks for the cone/bent-normal tier
   (backlog #11). No perf work here.

### Phase 4 — big-program scaling: proxy guards + per-segment bounds
(the placed-creation / forge future; gated on a representative workload)

This is where MPR/synchronized-tracing/proxy-LOD all point, and where the
current renderer has no tier at all (the grid prunes *instances*; nothing
prunes *within* a long chain). Trigger: creator/forge content with
multi-hundred-instruction placements, or the rigs posture going
views-bound on chain length in Phase 0.

1. **Per-segment influence bounds at bake time** (backlog #1). When baking a
   placed creation, derive each segment's world-space influence sphere
   (bound + blend halo + scoped-field reach, the exact margin channels the
   instance packer already implements) and pack them into the segment
   directory. `mapCore`'s existing per-segment early-out then rejects on the
   *baked* bound instead of the running-min heuristic alone. This is pure
   in-ISA metadata — no new pass, no hierarchy.
2. **Proxy segment guards (in-ISA, the Hubert-Brierre shape).** A new segment
   header form: a cheap conservative under-estimator (sphere/capsule/box, or
   a coarse `SampledRegion` brick) that `mapCore` evaluates first, descending
   into the guarded range only when the query point is within the proxy
   shell + margin. Far queries cost O(1) per guarded creation. This subsumes
   and generalizes item 1; it satisfies the ISA admission rule (it is
   control flow the existing vocabulary cannot compose) and it keeps the
   no-external-hierarchy doctrine. Correctness contract: the proxy must be a
   conservative lower bound of the guarded range's true field minus margin —
   the same class of proof as `SampledRegion`'s `boundaryFloor`. Gates: a
   `world-proxy` stage proving proxy==full within epsilon at the surface and
   hole-free solidity; bit-identity when no guard is authored. Capacity
   note: guard metadata adds program words — hot-swapping consumers must
   grow their `ProgramWordCapacity` probes in the same change (the envelope
   pattern), or a live rebuild outgrows the frozen buffers.
3. **Distance-tiered LOD variants of guarded ranges** (optional extension):
   far tier renders the proxy itself as geometry. Render-only, relaxed-parity
   territory; needs an owner call on visual policy before building.
4. **Segment tracing within warped segments** (backlog #2) stays parked
   behind all of the above, per its verified caveats (uniform-Lipschitz
   content sees limited-or-negative benefit; incompatible with the global
   over-relaxation that is our shipped default). It becomes interesting only
   for heavily warped creations where the global `stepScale` clamp is the
   confirmed march-length culprit.

### Phase 5 — tile-machinery coherence (synchronized-tracing increments;
gated on Phase 0 divergence/utilization profiles)

1. **Per-tile depth intervals.** Upgrade the beam's per-tile output from
   {march start, gap list} + binary instance mask toward per-instance (or
   per-instance-cluster) entry/exit depth spans along the tile cone. Views
   then (a) starts each ray deeper, (b) skips known-empty spans without field
   evals, (c) can drop instances from the working set once the ray passes
   their exit span. This is the A-buffer idea recast onto our existing beam —
   no new pass, richer beam output. Watch the memory shape: spans must stay
   O(small) per tile (cap + conservative fallback to today's binary mask).
   Gate: bit-identity is NOT expected (march paths change) — solidity +
   calibrated parity families + paired perf A/B carry it.
2. **Group-cooperative termination in views.** Two distinct sub-levers with
   different proofs: (a) *all-terminated early-exit* — idle-lane exit only,
   must be bit-identical per pixel; (b) *shared next-span skip* — changes
   march paths, proved by solidity + calibrated parity families instead.
   Portability pin: an 8×8 views group is 64 threads = two waves on wave32
   hardware (NVIDIA) and one on wave64 (RDNA — see the repo's wave64 pin),
   so a "group vote" is NOT a single portable wave op; coordinate through
   groupshared (or per-wave votes composed via groupshared), and keep the
   per-thread path compiled as the portable reference per the
   backend-equivalence rule.
3. **Beam lane utilization** (C6): revisit only if clean numbers show beam
   as a top-3 cost at representative scale. The in-file rationale for
   one-tile-per-warp is that packing 32 tiles per warp makes the march loop
   run to the slowest lane with the op switch replaying per divergent path —
   ballot tricks within a warp do not remove run-to-slowest without full
   work redistribution, which is the persistent-thread scheduler this plan
   explicitly defers. So this stays parked behind the wavefront/persistent-
   thread verdict, not framed as an in-place fix. DVFS noise also makes beam
   the easiest pass to mis-measure.
4. **cull-args** (C7, demoted from an earlier draft's Phase 1): the kernel
   is already 256-way parallel and documents its work as tiny (≈80×50 tiles,
   once per frame). Touch it only if Phase 0's timestamps show it non-trivial
   on real workloads; note its bbox→group-count finalization needs all tiles
   complete, so it cannot be fully absorbed into the beam pass.

### Phase 6 — frame-graph and multi-view hygiene (demo/World-facing)

1. **Cadence gate with a pose-quantized change signature**: an unchanged
   world revision + quantized camera pose skips mask/beam/cull-args and
   re-runs views only when something it consumes changed (or skips the whole
   chain and re-composites). Must be built on change *signatures*, never
   wall-clock heuristics; camera eases count as changes. Presentation-only.
2. **Shared uploads across nested engines — NEGATIVE FINDING (2026-07-17,
   do not build).** Investigated and rejected: `NestedWorldView` renders its
   own frame source, not the host program (the host-program filmer is
   `SdfCameraView`); the shareable resources (program buffer ~tens of KB,
   invariant grid) are a rounding error, and the grid is per-frame-rebuilt —
   not shareable — exactly in the moving-avatar workload that matters;
   `UploadProgram` cost is per-revision, not per-frame; and a revision-safe
   sharing design requires per-revision allocation + ref-counting +
   cross-engine fence coordination — structural coupling for zero frame
   time. REPLACED BY the real term the investigation exposed: every
   offscreen view engine allocates the default 64 MB brick pool
   (`SdfWorldEngine` `DefaultBrickPoolVoxelCapacity`) that it never bakes
   into — ~4 GB of dead allocation at the 64-view cap. Right-size view-engine
   pools (capacity 0 + a sound pool-less `SampledRegion` render fallback, or
   read-only host-pool sharing if filmed carves must stay carved — the pool
   is construction-stable, so sharing it lacks the revision-churn trap that
   killed program sharing).

   **LANDED 2026-07-17 (the capacity-0 + fallback leg).** `SdfCameraView` and
   `NestedWorldView` now construct their engines with `BrickPoolVoxelCapacity
   0` (a 1-float filler, ~64 MB → 4 bytes each). Baking and rendering were
   split: `UploadProgram` no longer rejects a `SampledRegion` on a pool-less
   engine (only `RequestBrickBake` does), and `sdfSampledRegion` gained a
   `GetDimensions` gate — seeing the single-float filler it returns
   `SDF_FAR_DISTANCE`, the same conservative uncarved-hull fallback the
   pool-unbound kernels already use, so the Subtraction never bites.
   **Step-1 ground truth (why this is a FIX, not a neutral change):** brick
   distances are stored `/√3`, so a ZEROED read = stored distance 0 = the box
   interior sitting entirely on the carve surface ⟹ the Subtraction carves a
   box-shaped HOLE across the whole region. The old default-64 MB view engines
   never baked their pools, so a filmed carved world rendered the carve box as
   a hole (or driver-dependent garbage) — a live (if latent) defect. Capacity
   0 + the gate render it uncarved instead: correct conservative approximation,
   never a hole.

   **Quality FOLLOW-UP still open (host-pool sharing).** Uncarved is the sound
   fallback, not the faithful image: if a filmed view must show the carve
   actually carved, the view engine needs read access to the HOST engine's
   baked brick pool. The pool is construction-stable (frozen at construction,
   written only by the sliced bake, never per-frame), so read-only sharing
   lacks the per-revision allocation + ref-count + fence trap that killed
   program sharing above — a bounded cross-engine descriptor-binding job. Not
   built here (no consumer films a carved world yet); revisit when one does.

## 4. What this plan deliberately does not do

- **No global voxel/clipmap representation, no external BVH/TLAS for the
  live path, no temporal history in shading** — the internal conditional/
  rejected verdicts stand and the external survey does not overturn them
  (proxy/LOD-in-tree explicitly *avoids* external structures; synchronized
  tracing needs no BVH; MPR's hierarchy is transient per-frame, not a
  persistent representation).
- **No wavefront/persistent-thread scheduler** ahead of a confirmed
  divergence profile (march-loop-scheduling verdict unchanged; the
  synchronized-tracing findings favor *in-place* subgroup coherence over
  queue machinery for this workload class).
- **No re-tightening of parity posture, no new Post stages for demo-side
  knobs, no preservation of wrong results for hash stability** (regime
  STABLE rules apply; corrections re-golden).

## 5. Verification map

| Change class | Proof |
|---|---|
| Set/loop changes that must not alter output (1.1, 3.1, 4.1, 5.2a) | bit-identity gates (`world-grid-cull` pattern: culled == flat) |
| March-path changes (4.2, 5.1, 5.2b) | solidity stages + calibrated parity families + hero `world` canary |
| Codegen-shaped refactors (1.3, 1.4, 2.1, 2.2) | full battery; expect ±1-LSB re-roll within families |
| Host upload changes (1.2, 6.2) | battery + moving-screen/decal scenes bit-identical |
| Consumer-policy and presentation changes (1.5, 3.2, 6.1) | demo/World run — including a name-only view consumer for 1.5 — + re-captured goldens when defaults move; NOT bit-identity claims |
| Every perf claim | within-session paired A/B per `docs/sdf-bench-notes.md` hygiene; committed to the baseline chapter |

## 6. Sources

- Keeter, *Massively Parallel Rendering of Complex Closed-Form Implicit
  Surfaces* (MPR), SIGGRAPH 2020 — mattkeeter.com/research/mpr,
  github.com/mkeeter/mpr
- Aydinlilar & Zanni, *Synchronized tracing of primitive-based implicit
  volumes*, ACM TOG 2025 — dl.acm.org/doi/10.1145/3702227
- Keeter, *Fidget* talk, 2024 — mattkeeter.com/research/fidget-2024.pdf
- Galin, Guérin, Paris, Peytavie, *Segment Tracing Using Local Lipschitz
  Bounds*, CGF/EG 2020 — hal.science/hal-02507361
- Bán & Válasek, *Automatic Step Size Relaxation in Sphere Tracing*,
  EG 2023 Short — diglib.eg.org/handle/10.2312/egs20231014 (shipped)
- Keinert et al., *Enhanced Sphere Tracing*, 2014 (shipped)
- Hubert-Brierre, Galin et al., *Proxies and LOD in SDF construction trees*,
  CGF 2025 — perso.liris.cnrs.fr/eric.galin/Articles/2025-lod.pdf
