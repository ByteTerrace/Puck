# Verdict Index

Master cross-reference of every deep-reviewed SDF-rendering technique from Phase 3 of the SOTA survey — one row per sub-technique, with its verdict, effort, gating dependency, and which review ruled on it.

## Legend

**Verdict vocabulary** (paraphrased from each review's own ruling):

| Term | Meaning |
|---|---|
| adopt-now | Build immediately; no blocking dependency, determinism-clean as reviewed. |
| adopt-when-GPU-bound | Correct technique, but wait for headroom/measurement before spending the budget. |
| gated-on-\<X\> | Real value, but must not land (or must land behind a knob/flag) until \<X\> is true. |
| measure-gate | Value is proportional to a cost that hasn't been measured yet; instrument first, decide after. |
| defer | Not now; revisit when its trigger condition (a feature landing, content type appearing) occurs. |
| reject | Ruled out for Puck outright — breaks a hard invariant or is a poor fit by design. |
| confirm-and-bank | Likely already correct; verify the three-point checklist and close it out, no new code assumed. |
| take-the-principle-only | Adopt the idea/combining-rule; explicitly do not build the literal mechanism described in the source. |

**Effort scale:** S = hours–a day (a few lines, no new seam) · M = a focused sprint (new kernel/pass or plumbing across a couple of seams) · L = a subsystem-sized change (ISA growth, new compiler pass, C#↔HLSL/Post triple-sync) · XL = multi-arc effort, usually recommended against or deferred indefinitely.

## Master table

| Technique | Verdict | Effort | Gating dependency | Review |
|---|---|---|---|---|
| iq 5-tap normal-ladder AO (`calcAO`) | pursue now — cheapest technique surveyed, largest perceptual gain | S | — | review-01 (1a) |
| Multiresolution AO combining rule (ambient-only, multi-band multiply) | already-effectively-have-it as a principle; incompatible as a literal SSAO pipeline | S (principle) / XL (literal SSAO+shadowmap) | rides 1a + 1c as its two bands | review-01 (1b) |
| UE-style cone-traced distance-field AO (closest-approach cones) | pursue when GPU-bound headroom exists / when the 8 glow lights need directional occlusion | M | shared `closestApproach()` helper factored out of the soft-shadow march | review-01 (1c) |
| Bent normals from the AO cone bundle | pursue as part of 1c, never standalone | S (bundled with 1c) | rides 1c's cone loop | review-01 (1d) |
| iq analytic sphere/box occluders | pursue when GPU-bound, and only for designated hero occluders | M–L | analytic occluder param plumbing through the instance directory | review-01 (1e) |
| Curvature shading + field-gradient outlines (tetrahedron-tap Laplacian) | pursue now (cavity + outlines), cheaply | S (cavity+rim) / S–M (tuned outline edge signal) | — | review-01 (Technique 2) |
| Barbier Lipschitz Pruning (per-region active-segment tape pruning) | adopt as the model | S–M (Stage 1) → S (Stage 2 far-field constant) → M (Stage 3 world-grid pruner) → L (Stage 4 per-tile) → XL (Stage 5, likely never) | scoped `PushField`/`PopField` accumulator (Stage 0 prerequisite — without it there is almost nothing to prune) | review-02 |
| Keeter/Fidget interval tape reduction (per-op interval → specialized tape) | borrow the concept, not the mechanism | XL as specified — interpreted tape reduction loses ~2.5× to dispatch (Fidget's own numbers) | only reach for this if Barbier's Lipschitz bounds prune too loosely | review-02 |
| Zanni Synchronized Tracing (per-tile primitive lists + workgroup sync) | adopt later, for coherence (Stage 4) | L | world-grid pruner (Stage 3) proving its worth first | review-02 |
| The Gunk four-bound teleport (entry/firstExit/secondEntry/far per tile) | adopt now, independent of the O(n) cull question | S–M | — | review-03 (Leg 1) |
| DIST 1.5× aggressive march + safe-convergence band | take now — cheap, orthogonal | S | — | review-03 (Leg 3a) |
| Uniform grid / spatial hash instance cull | **LANDED** — mask-first pre-pass kernel (`sdf-instance-cull.comp`) | M | cap now 16384 | review-03 (Leg 2) |
| Two-level BVH / TLAS-BLAS instance acceleration | recommended against for ≤16384 dynamic analytic instances | L | — | review-03 (Leg 2) |
| Third pyramid level (64×64 pre-beam, DIST coarse-to-fine schedule) | **demoted/near-moot** — the grid landed and the beam no longer dominates | M–L | uniform grid (Leg 2) landed first; only if beam-ms still dominates after Legs 1+3a+Leg 2 | review-03 (Leg 3b) |
| March/shade split (hit buffer + separate shade kernel) | pursue as a small, latent-value increment — not urgent | M | — | review-04 |
| Wavefront/compaction restructuring of the march loop | when per-tile specialized tapes land (or a heavy divergent shade stage lands) — not now | M–high, and largely wasted effort today (Wald's uniform-work regime predicts net-zero-or-negative) | per-tile specialized tapes (tape pruning) or a genuinely divergent shade stage | review-04 |
| Persistent-threads work-stealing (shared global counter) | incompatible — do not pursue | high effort, high portability risk | disqualified by determinism (nondeterministic ordering) and no HLSL SM6 cross-group forward-progress guarantee | review-04 |
| Material blend factor at seams (CD-family smin `h`/`m`, shade-funnel) | adopt — shade-funnel (option iii) first | M (shade-funnel) / L (carried-weight `SdfResult` variant, deferred) | route mixed material through the existing `parityMaterialDelta` channel | review-05 (Technique 1) |
| DD-family (non-compact-support) smin material blending | reject outright | — | breaks far-exact mask-cull endpoints | review-05 (Technique 1) |
| Soft-shadow penumbra refinement (Aaltonen closest-approach parabola) | adopt if classic `k*h/t` is shipped; confirm-and-bank if the improved form is already shipped | S | read-and-classify the current shadow loop first; stepScale divide-back must act on unscaled `y`/`d` | review-05 (Technique 2) |
| Bálint 2018 "enhanced" planar-optimal marcher | optional/skip — strictly dominated by auto-relaxed | S | — | review-06 (T1) |
| Bán 2023 auto-relaxed marcher (EMA slope, one scalar of state) | build — the pick of the family | S–M | pinned fp-contraction + denominator clamp on `1-m`; keep omega=1.2 as the `PUCK_PARITY_STRICT` fallback | review-06 (T1) |
| Moinet/Neyret curvature stepping (2nd-derivative quadratic step) | defer | L | forward-mode gradient accumulator (1st + 2nd directional derivative) | review-06 (T1) |
| iq fbmsdf cascade + Moinet fBm-as-nested-bounds LOD (dedicated FBM ISA op) | build — the correct realization of the roadmap fBm item | L | PCG3D noise-op basis; footprint-driven octave-LOD routed through the existing `distanceScale` channel | review-06 (T2) |
| PCG3D integer-hash noise op (value basis) | build — the roadmap's blocking "integer-hash basis" | M | — | review-06 |
| Forward-mode analytic gradient dual (per-op micro-code, hit-only specialization) | **LANDED** (`ce36f80`) — and made the design of the gradient accumulator | L | keep as a strictly parallel channel to the distance accumulator; hit-only second `mapCore` specialization to protect march occupancy | review-07 |
| Proxy nodes P/C (conservative + continuous Lipschitz proxy, Hubert-Brierre) | adopt as the far-field arc — stage 1, always-on | M–L (proxy only, existing subtree bounds) / XL (full paper) | ships under relaxed parity by default; `PUCK_PARITY_STRICT=1` needs a gate or a bit-identical-convergence proof | review-08 (Leg 1) |
| Continuous distance-LOD nodes (viewer-dependent surface simplification) | adopt, behind an explicit run-doc coarseness knob — never silent | part of the XL full-paper effort | deterministic camera (eye position is fixed-point sim state); a `puck.run.v1` coarseness field, not a per-frame env var | review-08 (Leg 1) |
| Normal warping (shading-only detail recovery atop LOD) | adopt as stage 3, rides atop the LOD nodes | part of the XL full-paper effort | gated together with the L nodes | review-08 (Leg 1) |
| Sphere Carving bounding-volume preprocess (black-box convex carve) | adopt as a compiler subroutine, not a standalone feature | M–L | must run async / off the live creator-rebuild path (or static segments only — "a few seconds" per model is too slow for interactive edit) | review-08 (Leg 2) |
| iq bounding-volume early-out (`if(dB>minDist) return minDist`) | do this first — cheap experiment that de-risks the whole arc | S–M | — | review-08 (Leg 3) |
| Tier-0 single-hit coverage AA (min-ratio smoothstep, blended vs. sky) | adopt — do first | S | — | review-09 |
| Ray-differential texture filtering (CRT/screen slabs, `textureGrad`) | adopt — do second, highest-certainty win in the review | M | mip chains generated on screen-source textures (prerequisite pipeline change) | review-09 |
| Tier-1 one-hit continuation (silhouette blended against farther geometry) | reasonable later refinement, only if silhouette-vs-geometry aliasing is still objectionable after Tier 0 | M | Tier-0 landed first; gated to fractional-coverage pixels only | review-09 |
| Tier-2 full front-to-back transparency accumulation | do NOT pursue — out of proportion to opaque silhouette AA | — | — | review-09 |
| Partial tile-level IA inclusion evaluator (cull + marchStart + shared tape prune) | adopt — recommended shape | M–L | schedule right after segment tracing lands; shares its evaluator with tape pruning (review-02) | review-10 |
| Per-pixel interval-bisection stall fallback (stall-triggered, Knoll stackless) | adopt as a follow-on, if grazing stalls remain costly | M | the tile-level partial IA evaluator landing first | review-10 |
| Full 27-op interval interpreter (tight fold/jitter hulls, AA/RAA option) | do not build — poor marginal return | XL | — | review-10 |

38 rows.

## Empirical status in Puck

Verdicts above are literature-vs-architecture rulings; this section tracks which
of them have been *empirically exercised* against the running engine. Default
for every row is **untested**. When an in-demo instrument run confirms or
refutes a row's premise, annotate it here (technique → status → evidence)
rather than rewriting the row.

| Suspect | Related wiki entries | Empirical status |
|---|---|---|
| Xor unmaskable-exemption (does the Xor blend op's mask-cull exemption hold?) | [tape-pruning-and-inclusion.md](tape-pruning-and-inclusion.md) (segment eligibility / unmaskable instances), [materials-and-primitives.md](materials-and-primitives.md) (blend families & far-exact mask-cull) | **EXEMPT — confirmed 2026-07-08** by real-GPU slice comparison. Xor's exterior field is bit-identical to union (`max(min(acc,b),-max(acc,b))` reduces to `min(acc,b)` wherever the candidate is exterior, and a first-hit march never reaches `acc < -b`); its extra carved surface lives strictly inside the union hull, hence inside any covering cull bound, hence never masked. `HasUnmaskableCompose` correctly omits Xor, `MaxSmoothBlendRadius` correctly gives it zero halo, `AnalyzeSegment` already marks it always-evaluated. Corroborates review-02's gating framing. Residual (documentation note, not a gate): Xor's cull-bound *sizing* is union-like — it needs an influence margin — not subtraction-like. |
| Repeat/CellJitter neighbor-cell discontinuity (iq 2023 wrong-neighbor bug) | [lipschitz-and-field-correctness.md](lipschitz-and-field-correctness.md) (Domain Repetition entry) | **REAL — confirmed 2026-07-08**, bounded to the authoring constraint. Slice captures show Repeat with an off-center/oversized prototype creasing at cell walls — an *over*estimate, the hole-inducing class neither `stepScale` nor the omega step-back can fix — while centered-within-half-spacing stays exact; CellJitter seams at cell boundaries even with the in-cell containment rule satisfied (containment ≠ nearest-copy). The reviewed 3^k neighbor-check is NOT warranted at current idle usage: the strengthened authoring rule suffices, plus builder-guard growth (Repeat has no extent guard today; CellJitter's guard omits the prototype radius). Actionable-if-authored, not urgent. |
| Ground-notch / MaxSteps march-termination hypothesis | [hierarchical-and-instance-acceleration.md](hierarchical-and-instance-acceleration.md) (four-bound teleport / 1.5× march), [lod-and-bounds.md](lod-and-bounds.md) (per-segment early-out, proxy nodes) | **REFUTED — 2026-07-08.** The termination view shows overworld ground plus a controlled floor terminating uniformly on the footprint-adaptive threshold (cyan), never steps-exhausted (red), with near-black iteration counts. The evidence-supported mechanism is footprint-adaptive early termination amplified behind occluders by the per-tile marchStart teleport — not step exhaustion. This *narrows* review-03's premise: the promote-now empty-space fixes (Larsson four-bound teleport, aggressive march) would not fix this notch; the lever is the footprint-epsilon / marchStart derivation. Caveat: the specific notch geometry was not isolated in a single frame (pad-driven camera); the refutation rests on ground-never-red across all captures. |
| iq 5-tap AO + curvature/ink outlines (Rows 1–2) | [shading-ao-shadows.md](shading-ao-shadows.md) | **LANDED — 2026-07-08** (commits `a36cb6b`, `44bfd88`). Both shipped as specified; curvature shading is off by default behind a compile-time `static const bool` guard, not the arithmetic-×0 CRT-knob pattern (see the DXC dead-code gotcha noted on [README.md](README.md)). |
| Four-bound teleport + 1.5× aggressive march (Row 4) | [hierarchical-and-instance-acceleration.md](hierarchical-and-instance-acceleration.md), [marching-acceleration.md](marching-acceleration.md) | **LANDED — 2026-07-08** (commit `ac373a8`). Gap-skip subset sized to the single-level 16×16 beam (no 64×64 pre-level); tile-buffer plane 0 (`marchStart`) stays byte-identical. |
| Soft-shadow penumbra refinement (Row 7) | [shading-ao-shadows.md](shading-ao-shadows.md) | **LANDED — 2026-07-08** (commit `706788c`). Aaltonen closest-approach parabola upgrade shipped. |
| Bán 2023 auto-relaxed marcher (Row 8) | [marching-acceleration.md](marching-acceleration.md) | **LANDED — 2026-07-08** (commit `b6747e6`). fp-contraction pinned on the divided step/slope quotient/fallback operand, `(1-m)` clamped via `SlopeCap`, `SDF_STRICT_MARCH` compile-time `#define` keeps `omega=1.2` reachable as the strict fallback. |
| Tier-0 coverage AA (Row 3) | [antialiasing-and-filtering.md](antialiasing-and-filtering.md) | **LANDED WITH CORRECTION — 2026-07-08** (commits `1a941dd`, `37f8b65`). The min-ratio-accumulator premise was false under the footprint-adaptive marcher (saturates to ~1 on every solid hit); rebuilt on the terminal-step residual, a normal-facing clamp, and a relative forward gate. |
| iq bounding-volume early-out (Row 5) | [lod-and-bounds.md](lod-and-bounds.md) | **ALREADY SHIPPED — found 2026-07-08** (commit `1a8330f`, predates the survey). Stale "do this first" premise; Wave 1 verified eligibility soundness for the shipped skip test instead, which held. |
| Scoped `PushField`/`PopField` field accumulator (Row 10 prerequisite) | [tape-pruning-and-inclusion.md](tape-pruning-and-inclusion.md), [lipschitz-and-field-correctness.md](lipschitz-and-field-correctness.md) | **GATE OPEN — 2026-07-08** (commit `8fdabd4`, depth 1). Restores maskability/segment-eligibility for onion'd and intersection chains; per-region tape pruning (Barbier) is unblocked, not itself built this wave. |
| Uniform grid / spatial hash instance cull (Leg 2) | [hierarchical-and-instance-acceleration.md](hierarchical-and-instance-acceleration.md) | **LANDED, mask-first — 2026-07-09** (commits `f08add1`, `1931d3c`; `world-grid-cull` Post gate, grid==flat bit-identical). Shipped as a NEW pre-pass kernel (`sdf-instance-cull.comp`), not a beam inner-loop rewrite: the measure-gate's O(instances) premise is refined, not confirmed as originally framed — the hot cost turned out to be the cone march's per-sample field enumeration, not the binning loop. `MaxInstances` raised to 16384, `MaxCarves` to 4096 (commit `1e389db`). |
| Forward-mode analytic gradient dual (Row 51) | [gradients-and-normals.md](gradients-and-normals.md) | **LANDED — 2026-07-09** (commit `ce36f80`). Shipped as `mapGradCore`, a hit-only second `mapCore` specialization; analytic normals are now the default, gated by the new `world-analytic-normal` Post stage. Hero parity improved 51→11 px. The gradient propagates via each op's runtime point-Jacobian (folds' actual reflection/rotation, DomainWarp's matrix transpose, Displace's exact analytic derivative), not the baked Lipschitz operator-norm scalars — those still bound the march step only. |

All other rows: untested.

## Where to go next

Each row above is a one-line ruling; the full reasoning, cost/benefit derivation, determinism analysis, and implementation seam for every technique lives on its topic page — [gradients-and-normals.md](gradients-and-normals.md), [shading-ao-shadows.md](shading-ao-shadows.md), [materials-and-primitives.md](materials-and-primitives.md), [tape-pruning-and-inclusion.md](tape-pruning-and-inclusion.md), [hierarchical-and-instance-acceleration.md](hierarchical-and-instance-acceleration.md), [march-loop-scheduling.md](march-loop-scheduling.md), [lod-and-bounds.md](lod-and-bounds.md), [antialiasing-and-filtering.md](antialiasing-and-filtering.md), and [lipschitz-and-field-correctness.md](lipschitz-and-field-correctness.md) — grouped by cluster rather than by source review. For the techniques ruled out entirely (DD-family smins, persistent-threads work-stealing, the two-level BVH, Tier-2 transparency accumulation, the full 27-op interval interpreter, and the baked/discretized/temporal families triaged out before Phase 3), see `negative-results-and-rejections.md`. For the ranked priority shortlist across the whole survey — what to build first given everything in this index — see [../sdf-sota-survey.md](../sdf-sota-survey.md).
