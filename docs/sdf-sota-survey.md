# SDF Rendering SOTA Survey — the ranked decision shortlist

*Written 2026-07-08, against `main` = `53d16b2` (the alpha-prep squash). This
document is the ranked decision shortlist from a multi-agent survey of
signed-distance-field rendering: **which techniques not yet in the Puck SDF VM
could improve its visuals or performance**, judged against what the engine
actually ships, its determinism contract, and its two-backend (SPIR-V + DXIL)
parity discipline. It is a delta, not a tutorial — it assumes the reader knows
the pipeline and the settled contract from the `sdf-world` skill.*

*Method. Seven parallel web sweeps, each from a different angle (sphere-tracing
acceleration; acceleration structures & baked representations; iq/Shadertoy
practice; production engines; shading/AO/shadows; temporal & frame-level perf;
robustness & authoring) → a mechanical cross-sweep dedup into candidate clusters
→ a relevance triage against the shipped engine, dropping what Puck already does
and what only a representation change would buy → ten primary-source deep reviews
(one per surviving cluster, each ruling verdict + cost/benefit + determinism fit
+ implementation seam + effort) → this synthesis with a completeness critic over
the sweep coverage. URLs were fetch-verified where the venue allowed it;
paywalled venues (Wiley, ScienceDirect, EG DigLib, Inria HAL behind an anti-bot
wall) were confirmed through open-access mirrors and preprints, noted per
citation. Three empirical results from a concurrent correctness hunt postdate the
reviews and are folded in where they overturn a review's premise — flagged
inline.*

*Companion. [docs/sdf-wiki/](sdf-wiki/) is the comprehensive settled-knowledge
reference — one topic page per cluster, a full [verdict-index](sdf-wiki/verdict-index.md),
and [negative-results-and-rejections](sdf-wiki/negative-results-and-rejections.md).
The wiki forward-links here for the cross-survey priority ordering; this doc is
the ranked "what to build first," the wiki is the "everything we learned." Where
they disagree, the wiki's per-page derivation is the deeper source; this doc's
ranking is the decision.*

---

## 1. The shortlist

Ranked by effort-adjusted value, front-loading the cheap determinism-clean wins
the deep reviews most strongly promoted. Verdict vocabulary is fixed: **pursue
now** · **pursue when GPU-bound** · **pursue with the named prerequisite** ·
**incompatible**.

| # | Technique (review) | Seam | Expected win | Effort | Determinism fit | Verdict |
|---|---|---|---|---|---|---|
| 1 | iq 5-tap normal-ladder AO (R1) | shading epilogue | first-ever AO — grounds geometry; largest perceptual gain per effort | S | clean (de-scale `d` by `L`) | **LANDED** (wave 1) |
| 2 | Curvature shading + gradient outlines (R1) | shading epilogue, reuses normal taps | cavity/rim/ink legibility, ~free | S | clean | **LANDED** (wave 1; off by default) |
| 3 | Tier-0 coverage AA (R9) | march epilogue + composite | kills silhouette crawl dither can't hide | S | relaxed-clean; STRICT → quantize coverage | **LANDED** (wave 1, premise corrected — see R9 fold) |
| 4 | Four-bound teleport + 1.5× aggressive march (R3) | beam + march loop | skip empty space; 6.6→4.0 ms attested on a shipped title | S–M | clean, per-tile independent | **LANDED** (wave 1) |
| 5 | iq bounding-volume early-out (R8) | `mapCore` walk | per-segment cull under the tile cull; de-risks the far-field arc | S–M | bit-exact, strict-parity-safe | **LANDED** (pre-survey, 1a8330f) |
| 6 | Material blend factor at seams (R5) | shade funnel + `parityMaterialDelta` | removes the hard material edge inside every smooth blend | M | relaxed-clean, far-exact endpoints untouched | **pursue now** |
| 7 | Soft-shadow penumbra refinement (R5) | shadow march loop | removes step-frequency banding at sharp occluders | S | clean (unscaled `y`/`d`) | **LANDED** (wave 1) |
| 8 | Bán 2023 auto-relaxed marcher (R6) | march loop | fewer robustness fallbacks, no per-scene knob, +8–15% on smooth content | S–M | clean iff fp-contraction pinned; keep ω=1.2 as strict fallback | **LANDED** (wave 1) |
| 9 | Forward-mode analytic gradients (R7) | HLSL op-switch dual path (hit-only) | kills FD catastrophic cancellation (parity win), exact noise normals; **is** the planned gradient accumulator | L | *more* parity-stable than FD | **LANDED** (2026-07-09, ce36f80 — hit-only analytic normals; fBm/curvature consumers still open) |
| 10 | Per-region tape pruning / Barbier Lipschitz (R2) | beam + program encoding | shorter per-tile tapes on the expensive onion/intersection chains | M (staged) | CPU-baked at build → zero new parity surface | **UNBLOCKED — pursue now** (accumulator landed AND the grid cell lists exist; views is the new bottleneck) |
| 11 | Bound-preserving fBm ISA op (R6) | ISA (FBM op) + `AnalyzeLipschitz` + `distanceScale` | the correct realization of the roadmap fBm item | L | excellent (integer hash, no per-ray state) | **pursue with** the PCG3D noise-op basis |
| 12 | Ray-differential CRT texture filtering (R9) | screen-sampling path (`SampleGrad`) | kills CRT/screen-slab shimmer & moire on the hero asset | M | benign LOD-quantized | **pursue with** mip chains on screen-source textures |
| 13 | Tile-level IA inclusion evaluator (R10) | beam/tile kernel + compiler | tighter cull + provable per-tile `marchStart` + shared tape prune; grazing robustness | M–L | IA subset parity-safe (no FMA contraction, no transcendentals) | **pursue with** segment tracing (shares the prune evaluator with R2) |
| 14 | Proxy/LOD far-field nodes + Sphere Carving (R8) | compiler bounds channel + ISA | non-baking answer to the town render-range ceiling | M–L → XL | proxy relaxed-parity; LOD behind a run-doc knob | **pursue when GPU-bound — DEMOTED** (far field is views-bound post-grid) |
| 15 | Uniform-grid / spatial-hash instance cull (R3) | host packer + beam | O(instances)→cell-walk cull | M | deterministic CSR scatter, no atomic append | **LANDED** (2026-07-09, mask-first — f08add1/1931d3c; cap 16384) |
| 16 | Third pyramid level / 64×64 pre-beam (R3) | beam + second `cull-args` scan | wide-cone overview only | M–L | prefix-sum, static dispatch | **DEMOTED, near-moot** (beam no longer dominates post-grid) |
| 17 | March/shade split (R4) | kernel structure | latent enabler; protects march occupancy from a future divergent shade | M | pixel-local, deterministic | **pursue with** a divergent shade stage (or per-tile tapes) |
| 18 | Wavefront/compaction of the march loop (R4) | kernel structure | reclaim wave tails | M+ | prefix-sum compaction only | **pursue with** per-tile specialized tapes |
| 19 | Persistent-threads work-stealing (R4) | kernel structure | — | — | nondeterministic ordering; no SM6 cross-group forward-progress | **incompatible** |

**Open work is consolidated in [sdf-backlog.md](sdf-backlog.md)** — the table's
LANDED/DEMOTED annotations above are the per-row status of record.

**Wave 1 landed (2026-07-08).** Rows 1, 2, 4, 7, 8 landed as specified (see
`git log --oneline 7b23263..HEAD` for exact SHAs: iq 5-tap AO + curvature/ink
outlines, four-bound teleport + 1.5× aggressive march, Aaltonen closest-approach
soft-shadow penumbra, Bán 2023 auto-relaxed marcher). Row 3 (Tier-0 coverage AA)
landed *with* a premise correction — see the R9 empirical fold below. Row 5 (iq
bounding-volume early-out) was found **already implemented pre-survey** — see
the R8 empirical fold below. Row 10's prerequisite (the scoped
`PushField`/`PopField` field accumulator, depth 1) landed — per-region tape
pruning is unblocked, not itself built this wave. Serialized measurement (the
repo's own timing discipline — single process, quiet GPU): a room scene costs
**+~0.7ms mean** (the added AO/AA/shadow taps + bounded gap search), a heavy
warped scene is **flat** (the auto-relaxed marcher's reduced step count offsets
the added shading cost); the two ranges overlap heavily, so read sub-ms deltas
as noise-bound, not a verified regression or win.

**Landed since (2026-07-09).** Row 15 (uniform-grid instance cull) — landed
mask-first (the measured "beam slope" turned out to be the cone march's
per-sample enumeration, not the binning; see docs/sdf-bench-notes.md for the
collapse tables; MaxInstances now 16384). Row 9 (forward-mode gradients) —
landed hit-only as analytic normals (`mapGradCore`, hero parity 51→11 px,
gpu-budget 1.87→1.58 ms; one premise corrected, see below).

### Prerequisite graph — the interaction structure

The shortlist is not independent line items; several rows share a seam or a
prerequisite, and building the shared piece once is the whole economy.

- **Tape pruning (10) is designed against the scoped-accumulator ISA.** Today
  `AnalyzeSegment` sets `segmentEligible = false` for every non-Union blend and
  every FIELD op, so the expensive onion/intersection chains — the actual frame
  cost — are `UnmaskableBoundRadius = 1e30` always-on instances the pruner is
  structurally blind to. `PushField`/`PopField` (designed, costed, unbuilt) hands
  those chains a far-neutral compose, restoring maskability *and* eligibility;
  only then does per-region pruning have high-value work. The accumulator lands
  first or concurrently; pruning is its perf pay-off.
- **The shared region-bound / interval evaluator serves pruning (10) *and*
  inclusion marching (13).** Build it once, **Lipschitz-first** (Barbier's
  `|f1−f2| ≥ k + 2R` reuses the baked `AnalyzeLipschitz` norms — no interval
  arithmetic, no new parity surface), and generalize to a true IA evaluator only
  if a forward-inclusion marcher or a beam that needs tighter-than-Lipschitz
  bounds independently justifies it. Segment tracing (Galin 2020, roadmap) is the
  *linear* member of the same forward-inclusion family — ship it first and the
  quadratic/interval cases drop in without a second subsystem.
- **AO, soft shadows, and coverage AA share the march-epilogue closest-approach
  primitive** (the min de-scaled `k·h/t` ratio). Factor it out of the soft-shadow
  loop into one `closestApproach()` helper; cone-AO and coverage become callers.
  **All three hit the same `stepScale` divide-back foot-gun**: `map()` returns a
  distance pre-multiplied by `stepScale = 1/L`; any consumer comparing it to a
  world-space offset (normal-ladder step, cone radius, footprint) must multiply
  by `L` first. Soft shadows already do; AO/AA/coverage must too, or occlusion
  strength silently tracks each program's Lipschitz bake instead of geometry.
- **The gradient accumulator (9) feeds three consumers** — gradient-noise fBm
  displacement (11), analytic surface normals, and Moinet curvature stepping's
  first-derivative term (R6, deferred). Build the forward-mode dual once.
  ⚠CORRECTION (2026-07-09, learned by building it): the baked Lipschitz operator
  norms (Bend `1+a`, Twist `√((2+a²+a√(a²+4))/2)`, Displace `1+amp·max|freq|`)
  bound the STEP — they are scalar norms, not the propagation rule. Gradient
  propagation chain-rules through each op's runtime point-Jacobian (the dual
  carries the Jacobian COLUMNS through the walk; folds apply their actual
  reflection/rotation, DomainWarp its full matrix transpose, Displace its exact
  analytic derivative). Landed as `mapGradCore` (hit-only), commit ce36f80.
- **Prefix-sum, never atomic append.** Compaction (18), hierarchical dispatch
  (16), and the uniform-grid build (15) all restructure the same indirect
  pipeline. The discipline is invariant: count → scan → scatter (the existing
  `sdf-cull-args` shape), never `InterlockedAdd`-append, and no wave-vote that
  alters numeric output. Atomic append forks the cross-vendor ordering; a
  32-vs-64-lane wave reduction forks the numerics.

---

## 2. Per-technique reviews

Ordered by first shortlist appearance. Each compresses its deep review to the
load-bearing content; the full derivation lives on the linked wiki page.

### R1 — SDF ambient occlusion + curvature shading (ranks 1, 2)

Wiki: [shading-ao-shadows](sdf-wiki/shading-ao-shadows.md),
[gradients-and-normals](sdf-wiki/gradients-and-normals.md).

**What it is.** Two shading-epilogue enrichments the engine ships *none* of today.
(a) iq's 5-tap normal-ladder AO (`calcAO`): from the hit, step a short ladder
along the normal and accumulate the deficit `(h − map(pos + h·nor))` — where
geometry crowds the normal the field under-reports and the deficit reads as
contact darkening. Five `map()` calls, a geometric falloff, a gain/clamp. (b)
Curvature shading: a second finite-difference ring on the *same* tetrahedron taps
already fetched for the lazy normal gives a discrete Laplacian `≈ (Σ taps −
4·center)/h²` — for a metric SDF this approximates mean curvature (concave
creases negative, convex ridges positive), driving cavity darkening, rim light,
and ink-line outlines where |curvature| spikes or |∇d| collapses.

**Cost/benefit.** AO is the single largest visual uplift available: the engine
has no AO at all, and contact darkening is what reads as "grounded." 5 extra
`map()` evals per shaded pixel — same currency as the 4-tap normal — paid only on
hits. Curvature is *≈ free*: it reuses the four taps and the center hit distance
already in hand; outlines cost nothing extra since the gradient is the normal.
Higher tiers exist but are secondary: UE-style cone-traced DFAO (~12–30 map evals)
buys a genuine bent normal (direction of least occlusion) and specular/glow-light
occlusion — the quality tier, home of bent normals — but 1a captures most of the
perceptual win at a fraction of the cost. iq's analytic sphere/box occluders are
exact and cheap but only for designated non-overlapping hero occluders (a ground
sphere, one planet) — they double-count overlaps and miss smooth-union blending,
so they are not a general solution.

**Determinism / two-backend fit.** Clean. Pure `map()` + float arithmetic, no
RNG, no screen-space neighbor reads, no history — same numeric class as the
normals and soft shadows already passing parity. The one caveat is the shared
foot-gun: de-scale `d` by `L` before the AO subtract; curvature of the scaled
field is `L`-scaled uniformly (retune the gain for an artistic signal, or divide
`stepScale` back out for world-unit curvature). Reject the literal
multiresolution SSAO+shadowmap band — screen-space, view/resolution-dependent,
dither-seeded, fights the no-history posture; take only its combining *rule*
(multiply the occlusion bands, apply to **ambient only**, never to direct sun —
that ghosts).

**Seam / effort.** Shading epilogue only; no ISA, compiler, or march-loop change.
Add `calcAO()` and the in-place Laplacian beside the normal/shadow helpers, gate
behind per-run shading toggle bits. **S** for both (AO 5-tap; curvature cavity+rim);
**S–M** if outlines want a tuned edge signal and per-object ink params; **M** for
the Tier-1 cone-AO + bent normal, built on the shared `closestApproach()` helper.

**Verdict. Pursue now** (5-tap AO + curvature/outlines). Pursue when GPU-bound for
the cone-AO/bent-normal quality tier and the hero-occluder analytic accelerator.

**Citations.**
- Inigo Quilez, *Raymarching Signed Distance Functions* (`calcAO` 5-tap), n.d. — https://iquilezles.org/articles/raymarchingdf/
- Inigo Quilez, *Multiresolution Ambient Occlusion*, n.d. — https://iquilezles.org/articles/multiresaocc/
- Epic Games, *Distance Field Ambient Occlusion in Unreal Engine* (cone trace, bent normal, PS4 ~3.7 ms) — https://dev.epicgames.com/documentation/en-us/unreal-engine/distance-field-ambient-occlusion-in-unreal-engine
- Inigo Quilez, *Sphere Functions* / *Box Functions* (`sphOcclusion`, `boxOcclusion`) — https://iquilezles.org/articles/spherefunctions/ , https://iquilezles.org/articles/boxfunctions/
- shaderfun, *Signed Distance Fields Part 8: Gradients, bevels and noise*, 2018 — https://shaderfun.com/2018/07/23/signed-distance-fields-part-8-gradients-bevels-and-noise/

### R2 — Per-region tape pruning / program specialization (rank 10)

Wiki: [tape-pruning-and-inclusion](sdf-wiki/tape-pruning-and-inclusion.md).

**What it is.** Specialize the program per spatial region so a pixel evaluates a
shorter tape than the whole scene. Three converged sources: **Keeter MPR (TOG
2020)** + **Fidget 2024** — interval arithmetic over the expression tape proves
regions empty/full and collapses any `min`/`max` whose operand intervals don't
overlap, cutting expression complexity up to ~100×; Fidget's tape/trace/specialize
pipeline is the clean mental model. **Barbier Lipschitz Pruning (CGF/EG 2025)** —
prunes a smooth-CSG tree per world-space cell using *only* the 1-Lipschitz
property: a binary op with children `f1,f2` collapses to one operand over a cell
of radius `R` when `|f1(p)−f2(p)| ≥ k + 2R` (one center eval + the Lipschitz
bound), hierarchically over a dense grid, down to ~1 active node per cell, ×629
on a 6023-node scene, no preprocessing, GPU-per-frame-capable. **Zanni
Synchronized Tracing (TOG 2024)** — per-tile pruned primitive lists + workgroup
synchronization for evaluation coherence.

**Cost/benefit for Puck.** The ~100× / ×629 headlines are vs naive whole-tree
tracing with *zero* culling; Puck already banks the coarse slice via its 16×16
beam prepass instance bitmask. The residual splits by accumulator state:
pre-accumulator, the only segment-eligible chains are Union-family — already
well-masked — so pruning them is a low-single-digit win chasing the cheap part;
the cost hogs (onion'd creator shapes, intersection chains) are `1e30`
always-evaluated and `segmentEligible = false`, so **the pruner is structurally
blind to exactly the geometry that dominates the frame.** After
`PushField`/`PopField`, those chains become maskable and eligible and the pruner
sees them — a substantial win on the town-scale/creator scenes that currently
bite, though a constant factor, not 100×, because Puck is an *interpreter*
(Fidget's own numbers: interpreted tape reduction loses ~2.5× to opcode dispatch,
and Puck cannot JIT per-scene kernels on two backends — the transferable prize is
shorter per-tile tapes, not codegen).

**Empirical fold (2026-07-08).** The Xor unmaskable-exemption question is
**confirmed EXEMPT**: Xor's exterior field is bit-identical to union
(`max(min(acc,b),−max(acc,b))` reduces to `min(acc,b)` wherever the candidate is
exterior, and a first-hit march never reaches `acc < −b`); its carved surface
lives strictly inside the union hull, so it is never wrongly masked.
`HasUnmaskableCompose` correctly omits Xor and `MaxSmoothBlendRadius` correctly
gives it a zero halo — this corroborates the maskability gating this review
depends on. Residual (doc note, not a gate): Xor's cull-bound *sizing* is
union-like and needs an influence margin, not subtraction-like.

**Determinism / fit.** Prefer **Barbier's Lipschitz criterion computed CPU-side
at program build** — view-independent (partitions world space), reuses the norms
`AnalyzeLipschitz` already bakes, encodes as per-region active-segment subsets in
the `uint[]` directory → **zero new cross-backend parity surface**. Keeter's
per-op interval evaluator on GPU would be a second numeric kernel that must be
bit-identical across DXIL/SPIR-V — strictly more determinism work for a result
Barbier's §5.2 says is "as good."

**Seam / staged landing / effort.** Stage 0 (prereq): `PushField`/`PopField`
(separate arc, **M**). Stage 1: give `AnalyzeSegment` explicit Push/Pop cases +
the shared Lipschitz region-bound helper (**S–M**, also fixes the current
`chainBoundable` cull regression). Stage 2: far-field constant substitution per
instance/cell (**S**, cheapest proof). Stage 3: per-instance/per-cell
active-segment bitmask, hierarchical from the parent cell (**M**). Stage 4:
Zanni-style per-screen-tile pruning in the beam (**L**, re-opens the parity gate
at segment granularity). Stage 5: per-frame GPU reprune (**XL**, likely never —
Puck rebuilds on-change).

**Verdict. Pursue with** the scoped accumulator. Barbier is the model; borrow
Keeter's *concept* not its mechanism; Zanni is a later coherence rider.

**Citations.**
- Matthew Keeter, *Massively Parallel Rendering of Complex Closed-Form Implicit Surfaces*, ACM TOG (SIGGRAPH) 2020 — https://www.mattkeeter.com/research/mpr/
- Matthew Keeter, *Fidget: Yet Another Implicit Kernel*, 2024 (VM-vs-JIT benchmarks) — https://www.mattkeeter.com/research/fidget-2024.pdf
- Barbier et al., *Lipschitz Pruning: Hierarchical Simplification of Primitive-Based SDFs*, CGF 44(2) / Eurographics 2025 — https://onlinelibrary.wiley.com/doi/10.1111/cgf.70057 ; preprint https://wbrbr.org/publications/LipschitzPruning/
- Zanni et al., *Synchronized-Tracing of Implicit Surfaces*, ACM TOG 2024 — https://arxiv.org/abs/2304.09673

### R3 — Hierarchical pre-beam, instance acceleration, ray pyramid (ranks 4, 15, 16)

Wiki: [hierarchical-and-instance-acceleration](sdf-wiki/hierarchical-and-instance-acceleration.md),
[march-loop-scheduling](sdf-wiki/march-loop-scheduling.md).

**What it is.** Roadmap item D3, split into three separable legs.
**Leg 1 — The Gunk four-bound teleport (Larsson 2021).** A coarse cone prepass
stores per tile `{entry, firstExit, secondEntry, far}`; the fine march teleports
to `entry`, skips the `firstExit→secondEntry` empty gap, terminates at `far`.
Measured 6.6→4.0 ms (~39%) at 4K on Series X. Our beam already cone-marches these
depths — it discards them and keeps only the instance bitmask.
**Leg 2 — instance acceleration.** Chizhov TLAS/BLAS vs a uniform grid / spatial
hash. For ≤1024 dynamic single-shape analytic instances the two-level BVH's value
(BLAS reuse, density adaptivity) mostly doesn't apply and its per-frame
deterministic sort cuts against the discipline; the deterministic **uniform grid
/ CSR spatial hash** is the right structure (integer scatter, order-stable via
prefix-sum, uniform per-lane cell-walk).
**Leg 3 — DIST (CVPR 2020) coarse-to-fine schedule.** Our beam→fine-march *is*
already a 2-level pyramid; DIST validates the shape. Two transferable parts: a
3rd (64×64) level only where the coarse bound is too loose (wide-cone overview),
and the **1.5× aggressive march** (`t += 1.5·d` in clear space, reverting to `1.0×`
in a safe-convergence band) — a free empty-space accelerant orthogonal to pyramid
depth.

**Cost/benefit + the stale-premise resolution.** D3 was deferred "until
GPU-bound," but the town overview already exceeds the reveal camera's SDF render
range (compact-block forced today). The survey resolves the monolith into three
triggers: **Leg 1 + the 1.5× march promote now, unconditionally** — S-effort,
host-code-free, determinism-clean, attested by a shipped title and a published
renderer, gated only to prove they don't change output. **Leg 2 (grid) and Leg 3b
(3rd level) are measure-first**: run `PUCK_TIMING=1` on the full-extent town
overview and read the Post `gpu-budget` per-pass ms — if `sdf-world-views`
dominates, the ceiling is empty-space marching (Legs 1+3a fix it, don't build the
grid); if `sdf-beam` grows linearly toward the 1024 cap, promote the grid; if both,
grid then 3rd level.

**Empirical fold (2026-07-08) — the notch is NOT a D3 problem.** The ground-plane
notch was **REFUTED as `MaxSteps` exhaustion**. The termination view shows the
ground terminating uniformly on the footprint-adaptive threshold, never
steps-exhausted; the actual mechanism is **footprint-adaptive early termination
amplified behind occluders by the per-tile `marchStart` teleport**. Consequence:
D3's empty-space work (Leg 1 teleport, 1.5× march) remains valid *perf* work but
is **not the notch remedy** — the lever there is the footprint-epsilon /
`marchStart` derivation, not empty-space skipping. Any earlier framing that D3
fixes the notch is withdrawn.

**Determinism / fit.** Excellent across all legs. Leg 1: per-tile independent, no
atomics/wave-vote, GPU-derived from packed bounds, both backends run identical
HLSL cone-march (make `secondEntry` a total function). Leg 2: integer CSR scatter,
instance-index order within cells (OR is commutative for the bitmask; stable order
protects the four-bound ties), **no atomic append**. Leg 3: fixed level structure,
any inter-level compaction is prefix-sum (`cull-args` run twice), the 1.5× step is
per-lane with a position-only convergence guard.

**Seam / effort.** Leg 1: widen the beam tile-output struct + ~6-line consumer
teleport + one two-wall-corridor parity gate, zero host change (**S–M**). Leg 2:
one host count/scan/scatter build + beam inner-loop cell-walk + a
bit-identical-to-flat parity gate (**M**; BVH **L** for less benefit). Leg 3a:
one constant + guard band (**S**); Leg 3b: a second prefix-sum `cull-args` stage
(**M–L**).

**Verdict. Pursue now** (four-bound teleport + 1.5× aggressive march).
**Pursue when GPU-bound** (uniform grid, 3rd pyramid level) on the town-scene
measurement. **Incompatible** framing withdrawn only for the notch claim; the
two-level BVH is rejected for ≤1024 dynamic analytic instances (see §3).

**Citations.**
- Jarl Larsson, *Raymarching The Gunk*, 2021 — https://jarllarsson.github.io/gen/gunkraymarcher.html
- Vassillen Chizhov, *Adding support for two-level acceleration for raytracing*, 2020 — https://interplayoflight.wordpress.com/2020/11/01/adding-support-for-two-level-acceleration-for-raytracing/
- Liu et al., *DIST: Rendering Deep Implicit SDF with Differentiable Sphere Tracing*, CVPR 2020 (coarse-to-fine + 1.5× aggressive march) — https://arxiv.org/abs/1911.13225
- *A Comparison of Acceleration Structures for GPU Assisted Ray Tracing* (UIUC) — https://www.ks.uiuc.edu/Research/vmd/projects/ece498/raytracing/GPU_BVHthesis.pdf

### R4 — Wavefront / compaction / persistent-threads restructuring (ranks 17–19)

Wiki: [march-loop-scheduling](sdf-wiki/march-loop-scheduling.md).

**What it is.** Restructuring the per-pixel megakernel march into (a) staged
dispatches joined by compacted work queues (wavefront) or (b) a persistent-threads
kernel that refetches alive work. **Aila & Laine (HPG 2009)** — persistent warps
pull rays from a global counter to refill lanes finished early; the divergence it
attacks (intra-warp iteration-count variance) is structurally our sphere-marcher's.
**Laine/Karras/Aila (HPG 2013)** — megakernels pay the near-*union* register
footprint over all material paths, capping occupancy; fix is staged kernels + queues,
but the gain is "much better suited where **multiple complex materials** are
present." **Wald (2011)** — active-thread compaction's extra memory traffic
"frequently eats most or all of the utilization win"; net modest, scene-dependent,
can go **negative** when per-step work is cheap. **Claybook (GDC 2018)** — a shipped
SDF sphere-tracer that stayed a megakernel with per-ray step masks.

**Cost/benefit for Puck.** Mostly **no, until per-step work diverges.** Puck has
no material heterogeneity (one interpreter switch evaluating one field per step),
so the 2013 register argument transfers weakly; the tile beam prepass already
harvests the spatial coherence compaction chases (Aila-Laine's win was on
*incoherent* rays; our indirect-dispatched tiles are the coherent regime where
persistent threads bought little); and shuffling per-ray march state every
iteration to keep lanes dense is the losing side of Wald's inequality for cheap
uniform per-step work. The trigger to revisit is precisely **per-tile specialized
tapes** (R2) — different tiles run different shorter op-sequences, so lanes in a
wave run genuinely different code and the megakernel again carries a near-union
footprint — or a heavy divergent shade stage (multi-material SDF brushes, textured
fields) that today does not exist.

**Determinism / fit.** Atomic-append work queues and persistent-threads
work-stealing are **disqualified** — nondeterministic ordering across runs/vendors,
and SM6 gives no portable cross-workgroup forward-progress guarantee (a
spin-on-counter loop can deadlock/starve on another vendor). Output-feeding wave
intrinsics (`WaveActiveSum`) are disqualified (32-vs-64-lane divergence). The one
survivor: **prefix-sum stream compaction** feeding a dispatch whose per-pixel
output is ray-order-independent (true for a single-bounce marcher).

**Seam / effort.** The safe wedge is a **march/shade split** (hit buffer + separate
shade kernel) — trivially deterministic (no reordering), latent value: it becomes
the seam where a future divergent shade lands without inflating the march kernel's
registers. **M**. Compaction proper is **M+** and largely wasted today.

**Verdict.** March/shade split: **pursue with** a divergent shade stage (or per-tile
tapes) — worth doing near-term as a latent enabler but not urgent. Wavefront
compaction: **pursue with** per-tile specialized tapes. Persistent-threads
work-stealing: **incompatible**.

**Citations.**
- Laine, Karras, Aila, *Megakernels Considered Harmful: Wavefront Path Tracing on GPUs*, HPG 2013 — https://research.nvidia.com/publication/2013-07_megakernels-considered-harmful-wavefront-path-tracing-gpus
- Aila, Laine, *Understanding the Efficiency of Ray Traversal on GPUs*, HPG 2009 — https://research.nvidia.com/publication/2009-08_understanding-efficiency-ray-traversal-gpus
- Wald, *Active Thread Compaction for GPU Path Tracing*, 2011 — https://www.sci.utah.edu/~wald/Publications/2011/PathCompaction/compact.pdf
- Aaltonen, *GPU-based Clay Simulation and Ray-Tracing Tech in Claybook*, GDC 2018 (shipped SDF megakernel precedent).

### R5 — Material blending at seams + soft-shadow penumbra (ranks 6, 7)

Wiki: [materials-and-primitives](sdf-wiki/materials-and-primitives.md),
[shading-ao-shadows](sdf-wiki/shading-ao-shadows.md).

**Technique 1 — material blend factor at seams.** iq's 2024 smin rewrite splits
smins into DD (non-compact support — never reach a bit-exact endpoint,
**disqualified**: they break far-exact mask-cull) and CD (clamped, zero effect
beyond `k` — the Puck-compatible family Puck already uses). The key reuse: the
same clamped `h` that positions the distance blend is the correct convex weight
for interpolating a per-candidate material — iq's two-output form returns
`(distance, m)` with `m ∈ [0,1]` a material blend factor, `0`/`1` *exactly* at the
endpoints (so winner-take-all is the strict `m>0.5` subset and mask-cull stays
bit-exact). Removes the hard material edge that snaps at the geometric midpoint of
every smooth blend. **Determinism:** `m` is a polynomial in the LSB-scale
difference `(a−b)`; interior deviation stays LSB-scale inside relaxed thresholds;
near band edges the clamp can flip `m` to 0 vs ε in a one-pixel ring — route the
mixed material through the existing `parityMaterialDelta` channel so it inherits
that channel's relaxed tolerance rather than tightening the color path. **Seam:**
the **shade-funnel** variant (recommended) — march unchanged, re-evaluate the
top-of-tree blend at the confirmed hit, recover `h`/`m`, lerp the two operands'
palette albedos; the whole feature lives in the shade stage, `SdfResult` and
culling untouched. **M** (carried-weight `SdfResult` widening is **L**, deferred
until nested-blend bleeding is demanded). **Verdict: pursue now**, shade-funnel first.

**Technique 2 — soft-shadow penumbra refinement.** The classic `res =
min(res, k·h/t)` samples the miss only *at* discrete steps, so at a sharp
occluder the true closest approach falls between steps and is missed →
step-frequency banding in the penumbra. Aaltonen's improved form estimates the
inter-sample closest approach via a local parabola (`y = h²/(2·ph)`,
`d = √(h²−y²)`, `res = min(res, d/(w·max(0,t−y)))`) — no extra `map()` evals, ~two
ops + one `sqrt` per step. **Determinism:** `sqrt` and divide are correctly-rounded
on both backends; the grazing region `t≈y` is the parity hotspot but a smooth
gradient inside relaxed thresholds. **The one hard requirement:** the `stepScale`
divide-back must act on *unscaled* `y`/`d` — mixing scaled and unscaled here
resurrects the ~30%-shadow-darkening bug class. **Seam:** the shadow march loop
only. Because Puck's current shadow form is unverified, the first task is a
**read-and-classify**: if classic `k·h/t`, do the S-upgrade; if already the improved
form, confirm the three-point checklist (`ph` seeded large, `max(0,t−y)` guard,
unscaled divide-back) and bank it. **Verdict: pursue now** (verify-first).

**Citations.**
- Inigo Quilez, *Smooth Minimum* (2024 rewrite; DD vs CD families, two-output `m`) — https://iquilezles.org/articles/smin/
- Inigo Quilez, *Soft Shadows in Raymarched SDFs* (classic `k·h/t`; Aaltonen GDC 2017 closest-approach parabola) — https://iquilezles.org/articles/rmshadows/

### R6 — Step relaxation beyond Keinert + bound-preserving fBm (ranks 8, 11)

Wiki: [marching-acceleration](sdf-wiki/marching-acceleration.md),
[lipschitz-and-field-correctness](sdf-wiki/lipschitz-and-field-correctness.md).

**Technique 1 — step relaxation.** Past our fixed `ω=1.2` Keinert baseline:
**Bálint 2018 "enhanced"** infers the next radius from a planar-optimal
extrapolation of the last two samples (carries two live values; up to 50% better
on smooth scenes, ≈relaxed on the Mandelbulb). **Bán 2023 auto-relaxed** tracks
the along-ray slope with an EMA and steps `z = 2r/(1−m)` (one scalar of state;
beats relaxed ω=1.2 by ~8–15%, takes *significantly fewer* robustness fallbacks,
and is markedly less hyperparameter-sensitive — `β∈{0.2,0.3}` within 2% across
scenes vs relaxed's per-scene ω). **Moinet/Neyret 2025 curvature stepping** uses
1st+2nd derivatives for a larger safe step (largest win on volumetric noise, not
today's content). The published wins are *relative to relaxed sphere tracing =
our baseline*, so the marginal gain over ω=1.2 is the single-digit-to-mid-teens
band and collapses to ~zero on fractal/high-frequency geometry. **The value
concentrates in robustness**, not raw speed: auto-relaxed's lower fallback rate
means fewer data-dependent branch divergences in a parity-gated fixed-budget
marcher, and its knob-insensitivity fits the "no per-scene float tuning" ethos.
**Determinism:** the real risk is FMA-contraction divergence amplified by the
`2r/(1−m)` division near tangency, which can flip the disjoint-sphere fallback
comparison on boundary pixels (a hard parity failure, not a sub-threshold delta)
— mitigation is mandatory and known: mark the step and the fallback compare
`precise`/no-contract consistently on both backends, clamp `(1−m)` from 0, keep
ω=1.2 as the `PUCK_PARITY_STRICT` fallback. **Seam:** march loop only, one line +
a register, downstream of the Lipschitz machinery (no ISA/compiler change). **S–M.**
**Verdict:** auto-relaxed **pursue now**; enhanced **skip** (strictly dominated);
Moinet curvature stepping **defer** (rides the gradient accumulator).

**Technique 2 — bound-preserving fBm detail.** Arithmetic fBm displacement is not
an SDF (violates |∇f|=1, creates flyover surfaces the marcher overshoots). iq's
`fbmsdf` builds detail one octave at a time with the smin/smax already in the ISA:
`n = smax(n, d−0.1s, 0.3s); d = smin(n, d, 0.3s)` clips each octave into a shell
around the running surface, keeping a valid conservative bound. Moinet/Neyret adds
**fBm-as-nested-bounds LOD** — every partial sum bounds the full field, so octave
count is a footprint-driven level of detail. **Determinism:** excellent — a pure
function of position over the integer-hash **PCG3D** basis (bit-identical both
backends), no per-ray state. **Seam:** a dedicated **FBM ISA op** (not a
macro-expansion — LOD's runtime octave count needs a runtime-variable loop the
fixed-length program can't bake) carrying `(base_amplitude, lacunarity, gain,
max_octaves, basis, clip, smooth)`; a closed-form `AnalyzeLipschitz` norm (smin/smax
bounds `L` by the *max* octave contribution, not the sum — the top active octave
dominates: `L_fbm = C·base_amplitude·(gain·lacunarity)^(K−1)·(1+smooth_inflation)`);
and a footprint-driven octave-LOD `L`-correction routed through the **existing
`distanceScale` channel** (as `K` drops with footprint, steps grow automatically —
no new machinery). Prerequisite: the **PCG3D value-noise op** (the roadmap's
blocking integer-hash basis, **M**). FBM op + cascade + Lipschitz + LOD: **L**.
**Verdict: pursue with** the PCG3D noise-op basis — this *is* the correct
realization of the roadmap fBm item.

**Citations.**
- Bálint & Valasek, *Accelerating Sphere Tracing*, EG 2018 Short (Algorithm 3, cone-tracing SDFE) — https://people.inf.elte.hu/csabix/publications/articles/eurographics-2018-shortpaper.pdf
- Bán & Valasek, *Automatic Step Size Relaxation in Sphere Tracing*, EG 2023 Short (Algorithm 4; RX 5700 results) — https://diglib.eg.org/xmlui/handle/10.2312/egs20231014 ; code https://github.com/Bundas102/auto-relaxed-trace
- Moinet & Neyret, *Fast Sphere Tracing of Procedural Volumetric Noise…*, CGF 2025 (110→16 ms; abstract-level, internals lower-confidence behind an Inria HAL anti-bot wall) — https://inria.hal.science/hal-05046040v1
- Inigo Quilez, *fBM Detail in SDFs* (per-octave smax-clip/smin-blend) — https://iquilezles.org/articles/fbmsdf/

### R7 — Analytic gradients for the interpreter VM (rank 9)

Wiki: [gradients-and-normals](sdf-wiki/gradients-and-normals.md).

**What it is.** A forward-mode dual (value + float3 tangent) propagated through
the VM, replacing the lazy 4-tap tetrahedron finite-difference normal. iq's
distance+gradient primitives return `(f, ∇f)` with ∇f already unit-length for
exact primitives and shared subexpressions (sphere: `(length(p)−r, p/length(p))`).
The arXiv:2405.07124 domain-warp AD paper is the math we want (forward-mode,
Jacobian-of-the-warp, normal from tangents `n̂′ = normalize((u+Ju)×(v+Jv))`) but
its mechanism — build-time codegen from a per-scene DSL — is unavailable to an
interpreter walking `uint[]` data; the interpreter-native form is to hand-write
each op's derivative micro-code once in the HLSL switch and let the VM loop
compose the chain at runtime. iq's gradient-noise derivative re-bases cleanly onto
our PCG3D hash (the integer cell decision has zero derivative, so the entire
smooth derivative is the quintic `du` term — bit-defined by the same cell decision
the value makes).

**Cost/benefit.** Normals are needed only at the hit (lazy), so the comparison is
one extra evaluation there: a **dual walk fetches/decodes each program word once**
and updates all four accumulator components; **4-tap fetches/decodes the whole
program four times.** On a fetch-bound interpreter the single fetch stream wins
(up to ~4× fewer program reads on deep programs); the ALU is comparable. Analytic
wins hardest on noise-heavy fields (FD on high-frequency noise needs a tiny epsilon
and still aliases; analytic is exact and nearly free) and gives crisp one-sided
crease normals where 4-tap averages a rounded incidental bevel. The register trap:
the dual accumulator is 4 floats plus, at smooth blends, both branches' gradients
live at once — mitigate structurally by making the dual path a **separate hit-only
specialization** so the march kernel keeps its lean footprint.

**Determinism — the strongest Puck argument.** FD central differences are a
catastrophic-cancellation machine: `f(p+h·e)−f(p−h·e)` subtracts two nearly-equal
values whose low bits are the answer, and DXC's SPIR-V/DXIL backends contract FMA
and reassociate differently, so the cancelled difference diverges *more* than the
primal distance — this is the "±1-LSB clusters along gradients" signature the fuzz
work sees, and why lit scenes lean on relaxed parity. A dual walk has no such
subtraction: the gradient is built up by the same op composition, selecting on the
same comparisons the distance already makes. Provided it reuses the distance
channel's contraction discipline and the integer PCG3D hash, **the gradient is
more parity-stable than FD** — this alone tips adoption independent of frame time.

**Seam / effort.** Grow the HLSL op-switch dual path, compile-time-flagged; do NOT
emit a separate gradient program. Two `mapCore` specializations (`MAP_DISTANCE`
untouched, `MAP_DISTANCE_GRAD` hit-only); keep the gradient a **strictly parallel
channel** to the distance accumulator (never tangle the PushField/PopField
stacks); CPU mirror + a `world-normals` Post gate. **L** (no single piece XL; the
interpreter-wide reach + C#/HLSL/Post triple-sync sets the size). ⚠One premise
corrected in the build (see the prerequisite graph): propagation uses each op's
runtime point-Jacobian, NOT the baked Lipschitz scalars — those bound the step.
LANDED 2026-07-09 (`mapGradCore`/`calculateNormalAnalytic`, `world-analytic-normal`
gate, hero parity 51→11 px, gpu-budget 1.87→1.58 ms; commit ce36f80).

**Verdict. Pursue now** — and make it *the* design of the roadmap's reserved
gradient accumulator. It feeds gradient-noise fBm, analytic normals, and curvature
stepping (build once).

**Citations.**
- Inigo Quilez, *SDFs and Gradients (3D)* — https://iquilezles.org/articles/distgradfunctions3d/
- Corse et al., *Vertex Shader Domain Warping with Automatic Differentiation*, arXiv:2405.07124, 2024 — https://arxiv.org/abs/2405.07124
- Inigo Quilez, *Analytic Derivatives of Gradient Noise* — https://iquilezles.org/articles/gradientnoise/

### R8 — In-tree LOD & compile-time bounds (ranks 5, 14)

Wiki: [lod-and-bounds](sdf-wiki/lod-and-bounds.md).

**What it is.** Three compile-time / in-tree legs, none baking a volume, on one
shared bounds channel. **Leg 3 (do first) — iq bounding-volume early-out:** thread
the running `minDist` through `mapCore`'s walk and skip a segment whose bounding
volume already exceeds it (`if(dB>minDist) return minDist`) — exact and
conservative (the skip returns the identical global min), ≈8× on a multi-part
character. **Leg 1 (flagship) — Hubert-Brierre "Accelerating SDFs" (CGF 2025):**
acceleration nodes embedded in the tree. *Proxy `P`/`C` nodes* replace `f` outside
a volume `V` with a cheap 1-Lipschitz `b(p)=d(p,∂V)+δ` — surface exact (`O'=O`),
only empty-space step sizes get cheaper; ×439 GPU on Castle, ×382 on Wall (the
"big architecture from a distance" regime the town hits). *Continuous LOD `L`
nodes* interpolate high/low-res subtrees by viewer distance — *changes* the
surface, pop-free, ×100 on Temple. *Normal warping* recovers LOD detail cheaply,
shading-only. **Leg 2 — Sphere Carving (SIGGRAPH 2025):** a black-box convex
bounding-volume constructor (half-spaces + ellipsoids from conservative SDF
queries) that the CGF'25 paper uses to auto-generate proxy volumes `V`, and that
can give tight bounds to cases Puck bounds badly today — warped/blended chains,
and `Ellipsoid` (which earns no cull bound today because its analytic SDF
underestimates; carving only needs conservative queries).

**Cost/benefit.** This is the strongest **non-baking far-field answer** and maps
cleanly because Puck's program already *is* a construction tree lowered to
segments. Proxy nodes collapse a distant subtree to a Euclidean-to-`V` distance
for every ray that never enters `V` — most far rays — directly attacking the town
render-range ceiling analytically, at the cost of a modestly larger `uint[]`, not
an octree.

**Empirical fold (2026-07-08).** The town ground-notch that motivated the
far-field urgency was **refuted as `MaxSteps` exhaustion** (see R3): the mechanism
is footprint-adaptive termination amplified by the per-tile `marchStart` teleport.
This does not diminish the far-field *render-range* case (far geometry genuinely
exceeds the overview march range) but it means the notch specifically is a
footprint-epsilon problem, not a proxy-node one.

**Empirical fold (2026-07-08) — Row 5 / Leg 3 already shipped pre-survey.**
The segment- and shape-level `minDist` early-out this leg recommends building
first was found **already implemented** before the survey (commit `1a8330f`)
— a stale "do this first" premise. Wave 1 spent its Row 5 effort verifying
eligibility soundness for the shipped skip test instead (the skip returns the
identical minimum an evaluated far segment would, per the exact-cull
contract), which held.

**Determinism / fit, per leg.** Leg 3: **bit-identical, strict-parity-safe**, no
knob — the only risk is GPU wave divergence eating the saving (a perf question,
measure it). Leg 2: CPU-side at compile → identical both backends; conservative
bounds only relocate the cull test (surface untouched); caveat — re-carve
per-instance for non-uniform-scale / space-folding segments (which gains them a
bound they lack today). Leg 1 proxy `P`/`C`: surface exact but the changed march
path can differ in the last ULPs at silhouettes → **relaxed-parity-safe, not
strict** (same posture as `stepScale`/footprint-epsilon already occupy); LOD `L`:
`O'≠O`, an approximation that **must sit behind a run-doc coarseness knob, never
silent** — deterministic *because* the eye position is fixed-point sim state.

**Seam / effort.** Shared compiler bounds channel; prefer emitting cheap/full
segment variants over new ISA ops where possible; `AnalyzeLipschitz` becomes a
tree fold (`C`'s `λ=1+s'(2r+δ)/r`) — reuse the accumulator plan's fold. Leg 3:
**S–M**. Leg 1 proxy-only with existing subtree bounds, relaxed parity: **M–L**;
full paper (carved `V` + LOD + normal warp + placement): **XL**, staged. Leg 2:
**M–L**, off the interactive rebuild path (async/static-only — "a few seconds" per
model is too slow for live edit).

**Verdict.** Leg 3 early-out **pursue now** (do first — de-risks the arc, quantifies
the tile-vs-segment headroom on the actual town scene). Proxy/LOD far-field arc
**pursue when GPU-bound**; Sphere Carving **pursue with** the early-out proving the
channel pays + an async pipeline.

**Citations.**
- Hubert-Brierre, Guérin, Peytavie, Galin, *Accelerating Signed Distance Functions*, CGF 44(7) 2025, DOI 10.1111/cgf.70258 — https://perso.liris.cnrs.fr/eric.galin/Articles/2025-lod.pdf
- Schott, Thonat, Lambert, Guérin, Galin, Paris, *Sphere Carving: Bounding Volumes for SDFs*, SIGGRAPH 2025 — https://aparis69.github.io/SphereCarving/index.html
- Inigo Quilez, *SDF Bounding Volumes* (`if(dB>minDist)` early-out, ≈8×) — https://iquilezles.org/articles/sdfbounding/

### R9 — Analytic/filtered antialiasing + ray-differential filtering (ranks 3, 12)

Wiki: [antialiasing-and-filtering](sdf-wiki/antialiasing-and-filtering.md).

**What it is.** Two independent tracks. **Track A — coverage AA.** iq's "antialias,
sort of" + frost.kiwi's analytical AA are the same identity: *coverage = a
smoothstep of signed distance against the pixel footprint*. At the closest approach
of a ray that grazes a silhouette, `map()` is small but nonzero; compared against
`pixelRadius·traveled` (the pixel cone footprint Puck **already forms** for its
adaptive epsilon — the exact quantity frost.kiwi calls the hard part in 3D), a
small distance is partial coverage. Track over the march `minRatio = min(d_world /
(pixelRadius·traveled))` and derive coverage — one min + one divide per step, no
extra `map()` calls, no march extension. **Track B — ray-differential filtering.**
iq's `filteringrm`: shoot the two neighbor rays (pixel+1x, +1y), intersect each
with the tangent plane at the hit, get the world-space pixel footprint
(`dposdx/dposdy` closed forms), chain-rule to UV derivatives, feed `SampleGrad` —
correct anisotropic mip selection with **no hardware derivatives** (which Puck
lacks entirely).

**Cost/benefit.** Track A kills **silhouette edge crawl** — the dominant visible
aliasing dither provably cannot hide (dither trades edge aliasing for noise; it
does not reconstruct the sub-pixel edge). Tier 0 blends coverage against sky
(free, correct for most overworld silhouettes against an open skybox); Tier 1
gates a one-hit continuation to fractional-coverage pixels for silhouettes against
geometry (~1.2–1.5× on edge pixels only); Tier 2 (full front-to-back) is out of
proportion — do not pursue. Track B is the **highest-certainty win in the review**:
the diegetic CRTs are the hero asset and shimmer/moire at glancing angles; the math
is closed-form over the ray setup + normal Puck already computes. Its one
prerequisite is real: the sampled screen-source textures must actually carry mip
chains (a pipeline/upload change), or `SampleGrad` has nothing to select.

**Empirical fold (2026-07-08) — Row 3 premise corrected, rebuilt (commit
`37f8b65`).** "Free coverage from the march min-ratio" turned out **false**
under Puck's footprint-adaptive marcher: the min-ratio saturates to ~1 on every
solid hit — the termination criterion itself guarantees it — so the metric
carried no interior-vs-silhouette signal, and a naive along-ray forward probe
used to compensate misfired on grazing-but-solid surfaces (footprint-quantized
scaliness across the twisted torus's flanks and the floor gradient, the floor
washed to sky in a diagnostic build). The working form blends only where three
signals agree the pixel is a genuine silhouette edge: coverage from the
**terminal-step residual** in the same clamped/termination-consistent units
the footprint-adaptive march already terminated against (no de-scale — living
in the termination test's units is what makes the signal mean anything), a
normal-facing clamp (a camera-facing surface never blends), and a **relative**
rising-field forward gate (the field's rise from the terminal residual, not an
absolute tap). Deliberately subtle — corner-pixel softening only (~30 px in
the bisect scene, up to 125/255 coverage on the corner pixel); silhouette-vs-
geometry AA remains the Tier-1 continuation, not this landing.

**Determinism / fit.** Coverage is a pure function of `d`, `traveled`,
`pixelRadius`, `stepScale` + a smoothstep — a few-LSB `Δcov` across backends,
comfortably inside relaxed parity (perturbs only a thin edge band). Under STRICT a
few-LSB `Δcov` at a high-contrast edge can flip an 8-bit channel by ±1 — mitigate
by **quantizing coverage to N levels** before the blend (mirrors the engine's
existing quantize-the-divergent-quantity discipline). Ray differentials feed
inherently-quantized mip LOD selection, so ±1-LSB derivative noise rarely changes
the selected mip pair (snap the LOD under STRICT if it does). **Foot-guns pinned:**
divide `stepScale` back out (`d_world = d/stepScale`) before comparing to the
world-space footprint; run **dither AFTER** the coverage blend and filtered sample
(dithering the hard edge first, then blending, smears the pattern along the
silhouette). Pipeline order: march → coverage → composite/blend → SampleGrad →
tone → ordered dither → 8-bit.

**Seam / effort.** Track A: march-loop epilogue (min-ratio accumulator) + composite
mix — **S**, highest value/effort ratio in the review. Track B: screen-sampling
path — **M** kernel side + **M** mip-chain generation (on the critical path for the
benefit). Tier 1 continuation and STRICT quantization: **M**/**S–M** follow-ups.

**Verdict.** Tier-0 coverage AA **pursue now** (do first). Ray-differential CRT
filtering **pursue with** mip chains on the screen-source textures (do second).
Tier 1 later if silhouette-vs-geometry remains objectionable; Tier 2 **incompatible**
with the effort budget.

**Citations.**
- Inigo Quilez, *Raymarching Distance Fields* ("antialias, sort of" coverage) — https://iquilezles.org/articles/raymarchingdf/
- Inigo Quilez, *Ray Differentials and Textures* (`dposdx/dposdy`, `textureGrad`) — https://iquilezles.org/articles/filteringrm/
- frost.kiwi, *Analytical Anti-Aliasing*, 2024 (exact 2D coverage, 3D limits) — https://blog.frost.kiwi/analytical-anti-aliasing/

### R10 — Interval / inclusion-function marching (rank 13)

Wiki: [tape-pruning-and-inclusion](sdf-wiki/tape-pruning-and-inclusion.md).

**What it is.** Interval- and affine-arithmetic *inclusion functions* as a
robustness/perf tier beside the Lipschitz tracer. An inclusion function `F` of `f`
satisfies `F(x) ⊇ {f(x)|x∈x}`; the robust rejection test for a ray sub-interval is
`0 ∉ Ft(t)` — a *guaranteed* skip, unlike point-sampling which silently misses
thin non-monotonic features. **Aydinlilar & Zanni (C&G 2023)** add *forward*
inclusion functions — asymmetric, exact at the query point, tight for the *first*
step the marcher licenses (the grazing-ray win) — and prove the load-bearing
fact: **Lipschitz bounds ARE linear inclusion functions, so segment tracing
(Galin 2020) is the linear member of this family, not a rival** — they compose,
ship segment tracing first. **Knoll (CGF 2009)** is the IA/AA/reduced-AA foundation
and the stackless GPU bisection traversal.

**Cost/benefit for Puck.** An inclusion function per op is a *second* interpreter.
The op mix caps the upside: **domain repeat, log-spherical wrap, and cell-jitter
all collapse to their full-period hull the moment a domain interval spans one
period** — most of the march — so interval marching helps *least* on exactly the
ops that make Puck's field interesting. Where it shines is the smooth composed
field (blends, thin shells, grazing rays) — which happens to be Puck's actual
grazing-crawl pain point. The recommended shape is therefore **partial**: affine +
min/max + abs + blend + clamp/poly get real interval transfers; fold /
log-spherical / cell-jitter fall back to Lipschitz (legal precisely because
Lipschitz *is* linear inclusion) — **M–L**, capturing ~all the benefit; a full
27-op interpreter is **XL** for near-zero tightness on a third of the ops.

**Determinism / fit.** The **IA subset is parity-safe** under the conditions the
point path already meets: reuse only `add/sub/mul/min/max/abs` (correctly-rounded,
bit-identical), **forbid FMA contraction** (the single largest hazard — the
interval multiply is four products + min/max, emit with `NoContraction`), and
**keep AA/RAA and vendor transcendentals out of the gated path** (Knoll reached
the same conclusion: RAA regression ops are "ill-suited for inaccurate GPU floating
point," so he resorts to IA there). HLSL exposes no directed rounding, so restore
conservativeness with a fixed few-ulp relative widening — which does *not* affect
determinism (a constant applied identically on both backends).

**Seam / effort.** Compiler emits interval micro-ops for a **second interpreter
templated on the register type** (same single tape walked twice: scalar for the
march, interval for cull/prune/segment-bound); the payoff is at the **beam/tile
level** — one interval eval per tile-cone amortizes over thousands of pixels and
**shares its evaluator with R2's tape pruning** (cull + `marchStart` +
node-pruning from one pass). Keep Keinert/segment tracing as the primary marcher;
add a per-pixel Knoll stackless-bisection **fallback triggered only on a stall
detector** (step count > K, the grazing-crawl signature) so the 95% of pixels the
Lipschitz march handles never pay the second interpreter. Tile-level partial IA:
**M–L**; stall fallback: **M** follow-on.

**Verdict. Pursue with** segment tracing landed (ship it as the linear member),
built jointly with R2's tape pruning (shared evaluator); the per-pixel bisection
is a stall-triggered follow-on; the full 27-op interpreter is **incompatible** with
the effort budget (poor marginal return).

**Citations.**
- Aydinlilar & Zanni, *Forward inclusion functions for ray-tracing implicit surfaces*, Computers & Graphics 114 (2023) 190–200, DOI 10.1016/j.cag.2023.05.026 (abstract verbatim via Semantic Scholar; preprint https://inria.hal.science/hal-04129922 behind an anti-bot wall).
- Knoll et al., *Fast Ray Tracing of Arbitrary Implicit Surfaces with Interval and Affine Arithmetic*, CGF 28(1) 2009 (§5.4 GPU-FP posture, §5.5 stackless bisection) — https://www.sci.utah.edu/~knolla/cgrtia.pdf
- Fryazinov & Pasko, *Fast Reliable Ray-tracing … Revised Affine Arithmetic* (per-op affine transfers for R-function CSG) — ScienceDirect S009784931000107X.
- Galin et al., *Segment Tracing Using Local Lipschitz Bounds*, CGF 39(2) 2020 (the linear inclusion member) — https://onlinelibrary.wiley.com/doi/10.1111/cgf.13951

---

## 3. Considered and rejected

One line each, with the disqualifying category. Detailed reasoning:
[negative-results-and-rejections](sdf-wiki/negative-results-and-rejections.md).

- **Binary-search raycasting (iq).** *Negative result* — measured ~2× slower than
  sphere tracing; explicitly recorded here so nobody re-adds it. (iq, *Interior SDFs*/ray-march notes.)
- **Baked/discretized family** — ADF (Frisken 2000), GigaVoxels, ESVO, SVDAG/SSVDAG,
  Aokana, brickmaps, JCGT-2022 SDF grids, Claybook voxel grid, Godot SDFGI, UE Mesh
  Distance Fields. *Representation change* — trades the pure-analytic program for a
  cache with memory cost and edit-staleness; Puck has no meshes to bake and refuses
  baked volumes.
- **Quadric Tracing (Kiglics & Bálint, Acta Cybernetica 2021).** *Baked spatial
  index* — a precomputed per-cell grid of bounding/unbounding quadrics tested before
  the field; the "augment" mode preserves the exact SDF but still requires a
  GPU-resident precomputed grid, and the "replace" mode is a representation change.
  20–100% on static scenes, but the grid is exactly the baked volume Puck declines.
- **Dreams splat pipeline (Evans 2015).** *Representation change* (point splats, not
  per-pixel marching) — keep only its incremental re-bake invalidation discipline as
  background.
- **GPU Work Graphs (Kuth et al. HPG 2024).** *Wave-op / backend parity hazard* —
  D3D12-only, no first-class Vulkan equivalent.
- **Wave-intrinsic early-out.** *Wave-op parity hazard* — 32-vs-64-lane divergence
  changes iteration counts at wave boundaries across backends.
- **Temporal-history family** — Xor temporal-SDF (GM Shaders), checkerboard 2-frame
  reconstruction, TAA cleanup, DDGI / SDFGI propagation, Lumen Surface Cache, Dreams
  temporal indirect. *Temporal-history conflict* — value reuse across frames fights
  determinism/replay.
- **Reprojected-depth `marchStart` seeding (Q3).** *Temporal-history conflict* on the
  fault line — depth-only reuse perturbs only where marching *starts*, not what value
  is trusted, so it is the *most* determinism-adjacent of the temporal set; still
  breaks strict determinism if the seed uses last frame's GPU-order-dependent output.
  Recorded as the taxonomy boundary, not adopted.
- **Hardware VRS.** *Solves a problem we don't have* — raster-only, does not extend to
  compute dispatch (the tile-bitmask prepass already approximates manual variable-rate).
- **Non-linear sphere-tracing ODE (Seyb 2019).** *Solves a problem we don't have* —
  our closed-form Jacobian bounds already cover authored warps (twist/bend/domain-warp);
  ODE integration is materially more expensive per step.
- **RTSDF (Tan et al. 2022).** *Solves the inverse problem* — regenerates an SDF from
  triangle geometry per frame; our scene already *is* an analytic SDF.
- **InverseVis (Lawonn 2024); RBF sphere tracing.** *Niche* — visualization / narrow
  primitive class.
- **DIST as a neural renderer.** *Solves a problem we don't have* — only its
  single-frame coarse-to-fine pyramid schedule + 1.5× march survive into R3.
- **Womp / MagicaCSG.** *Tooling, not technique.*
- **Eye-tracked foveation (Arm).** *Nondeterministic input* — a *fixed* foveation map
  is deterministic and available but unreviewed; gaze would need capturing into the
  per-tick `CommandSnapshot`.
- **Higher-order algebraic SDFs (Valasek/Bán 2023); exact-polygon SDF (Bálint 2023).**
  *Representation / primitive future* — replace the field representation or need a
  polygon primitive Puck lacks.
- **Two-level BVH / TLAS-BLAS instance acceleration.** *Over-structured* — for ≤1024
  dynamic single-shape analytic instances the BLAS-reuse value is ~zero and the
  per-frame deterministic sort cuts against the discipline the uniform grid satisfies
  for free (R3 Leg 2).
- **DD-family (non-compact-support) smins.** *Breaks far-exact mask-cull* — influence
  extends past `k`, so no bit-exact endpoint (R5).
- **Persistent-threads work-stealing.** *Incompatible* — nondeterministic ordering + no
  SM6 cross-group forward-progress guarantee (R4).
- **Full 27-op interval interpreter.** *Poor marginal return* — most of the cost buys
  near-zero tightness on the fold/jitter/log-spherical ops (R10).
- **Tier-2 front-to-back transparency accumulation.** *Out of proportion* to opaque
  silhouette AA (R9).
- **Bálint 2018 "enhanced" marcher.** *Dominated* — strictly worse than Bán 2023
  auto-relaxed in the same authors' follow-up (more state, more fallbacks, needs ω
  tuning) (R6).
- **hg_sdf seam-profile blends (columns/stairs/pipe/engrave/groove).** *Premature until
  authoring catches up* — the idle-ISA finding; each new blend family needs the full
  halo + Lipschitz-norm + scoped-POP triple derivation before it ships.
- **Literal multiresolution SSAO + shadowmap pipeline.** *Solves a problem we don't
  have* — screen-space, view-dependent, needs a G-buffer/shadowmap Puck doesn't keep;
  take only the ambient-only multi-band combining rule (R1).

---

## 4. Background and validation

Not full reviews — provenance, folk-taxonomy formalization, honesty notes, and the
one transferable baked-world pattern.

- **Exactness taxonomies.** hg_sdf (Mercury 2016) and iq's `distfunctions` /
  `distfunctions2d` are the folk classification (nearly every combinator yields a
  distance *bound*, not an exact distance; invariant |∇f|≤1) that Puck's compiler
  already formalizes with exact per-op norms. Regression-test candidates: the
  elongation/rounding "flat core" caveat and the 2D→3D exactness-inheritance rule.
  hg_sdf: https://mercury.sexy/hg_sdf/ ; iq: https://iquilezles.org/articles/distfunctions/
- **The chamfer-halo claim — TEMPERED.** The `1.70711·k` cull-halo constant Puck bakes
  for ChamferUnion **was not found in the surveyed literature** — it was derived
  independently here for the maskable-instance culling bound in this renderer. This is
  a narrow claim about that one constant for that one purpose; **do not** generalize it
  to "ahead of published practice" broadly. Cross-check any *new* blend family's halo
  against iq's per-family band-width formulas (*Smooth Minimum*, 2024 rewrite —
  https://iquilezles.org/articles/smin/) rather than re-deriving from scratch.
- **KIFS / Mandelbox fudge-factor honesty.** For fractal folds, tracking a scalar
  running product of scaling-Jacobian magnitudes gives a usable DE but *not* a true
  metric — the scalar product misses the true Jacobian norm's discontinuities, so an
  empirical fudge factor is standard. Honest SOTA: no rigorous fix exists; this is the
  running-Lipschitz-product situation Puck's nested Scale/fold ops are in. (Mikael
  Hvidtfeldt Christensen, *Distance Estimated 3D Fractals (VI): The Mandelbox*,
  Syntopia blog, 2011 — blog.hvidtfeldts.net; site unreachable at survey time,
  bibliographic form retained.)
- **Domain-repetition correctness — cataloged, tempered (empirical fold 2026-07-08).**
  iq's 2023 "wrong neighbor" bug of `round()`-based repeat (the true nearest instance
  can live in a neighboring cell when instance size/offset varies) is **confirmed REAL**
  in our engine but **bounded by the existing in-cell authoring constraint**: slice
  captures show off-center/oversized prototypes creasing at cell walls (an
  overestimate — the hole-inducing class), while centered-within-half-spacing stays
  exact; CellJitter seams at boundaries even when in-cell containment holds (containment
  ≠ nearest-copy). The reviewed 3^k (and mirrored) neighbor-check fix is **cataloged but
  NOT warranted at current idle repeat usage** — the strengthened authoring rule plus
  builder-guard growth (Repeat has no extent guard today; CellJitter's omits the
  prototype radius) suffices. **Verdict: pursue with the named prerequisite — when
  repeat authoring grows**, not pursue now. (iq, *Domain Repetition*, 2023 —
  https://iquilezles.org/articles/sdfrepetition/)
- **Provenance cites.** Segment Tracing (Galin et al., CGF 39(2) 2020,
  https://onlinelibrary.wiley.com/doi/10.1111/cgf.13951) — the directional-Lipschitz
  baseline the roadmap already carries and R10's linear inclusion member. Enhanced
  Sphere Tracing (Keinert et al., EG Short 2014) — the over-relaxation baseline; check
  whether the final-candidate selection is implemented.
- **Active frontier Puck's closed-form design approximates.** Bálint et al.,
  *Operations on SDF Estimates* (CAD&A 2023) introduces set-contact smoothness — a
  principled *per-node* error metric beyond a global Lipschitz constant. Bán & Valasek,
  *Generalized Lipschitz Tracing* (CGF 2025) extends it to general implicits via a
  precomputed local-polynomial-proxy **bake pass** — interesting as a fallback for ops
  without a hand-derived norm, but it adds exactly the bake-time dependency Puck's
  closed-form per-op design deliberately avoids.
- **The one transferable baked-world pattern: far-field fidelity reduction by distance.**
  UE's Global Distance Field / Lumen near-far split (per-object SDF for ~2 m, coarse GDF
  beyond) and SDFGI/brickmap clipmap cascades all "switch fidelity by traveled distance,
  not by object." The *non-baking* version is exactly R8 Leg 1's proxy/LOD nodes —
  reduced-primitive-count once ray distance exceeds a threshold, no volume cached. This
  is the single idea worth lifting from the baked family; everything else in it is a
  representation change. UE GDF: https://dev.epicgames.com/documentation/en-us/unreal-engine/mesh-distance-fields-in-unreal-engine
- **MSDF / text (the one thin sweep angle).** Only Valve/Green 2007 (*Improved
  Alpha-Tested Magnification for Vector Textures and Special Effects*, SIGGRAPH 2007 —
  https://steamcdn-a.akamaihd.net/apps/valve/2007/SIGGRAPH2007_AlphaTestedMagnification.pdf)
  surfaced for the planned screen-space text/UI arc; Chlumský's msdfgen (the modern
  multi-channel SDF generator) did not surface. A targeted MSDF/msdfgen/2D-UI
  mini-sweep is warranted **only if the text/UI convergence arc is prioritized** (the
  briefing's thread 5 — routing the console/editor through the SDF VM as an MSDF glyph
  op); it is not a synthesis blocker.
- **Available but unreviewed.** Async-compute overlap + fixed foveation/rate maps
  (small, deterministic-compatible, low priority). iq's `distfunctions2d` extended
  primitive list (heart/cross/pie/arc/ring/horseshoe/moon/Bézier/parabola) — a
  mechanical extension of the lifted-2D family; catalog only, no review needed.
- **Interior distance & thickness effects (unreviewed feature-expansion).** iq's
  *Interior SDFs* (correct inside-a-union distance — `min()` unions break interior
  distance, which anything sampling the negative side inherits), Claybook-style
  negative-side thickness marches for cheap SSS/translucency, and SDF-shaped
  volumetric density (fog hugging geometry) are new *capabilities* rather than
  upgrades to current output; cataloged for the feature backlog, not reviewed.
  (iq, *Interior SDFs* — https://iquilezles.org/articles/interiordistance/)

### Open research gap

**Anisotropic / non-Euclidean metric step correction.** No dedicated literature
was found deriving anisotropic or non-Euclidean metric step corrections for sphere
tracing. Segment Tracing (Galin 2020) and Generalized Lipschitz Tracing (Bán/Valasek
2025) are the closest partial answers — they generalize *ray-direction* dependence,
not the underlying *metric*. This is a genuine field gap (not a sweep hole): if Puck
ever needs correct stepping under a warped/anisotropic domain metric, the closed-form
derivation would be original work, not an adoption. Recorded here so a future session
does not mistake the absence for an incomplete search.
