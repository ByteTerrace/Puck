# Tape pruning and inclusion functions

Two related ideas for cheapening per-pixel evaluation without changing what a
ray sees: **per-region program specialization** — prove a subtree of the
program is irrelevant to a spatial region and shorten the tape the region
evaluates — and **inclusion-function marching** — interval/affine bounds that
prove a ray sub-segment cannot contain the surface, or license a bigger safe
step than a scalar Lipschitz bound would. Both live at the compiler + beam
prepass seam: the compiler already bakes per-op Lipschitz norms
(`AnalyzeLipschitz`) and a per-program `stepScale = 1/L`, and both families of
technique below are ways to make that seam do more work before the per-pixel
march kernel ever runs.

### Keeter MPR (Massively Parallel Rendering of Complex Closed-Form Implicit Surfaces)
- **Source:** Matthew Keeter, *Massively Parallel Rendering of Complex
  Closed-Form Implicit Surfaces*, ACM TOG / SIGGRAPH 2020.
  https://www.mattkeeter.com/research/mpr/ (project page + code:
  https://github.com/mkeeter/mpr).
- **Digest:** Evaluates the expression tape with interval arithmetic over a
  shallow, high-branching-factor hierarchy of spatial regions; any `min`/`max`
  clause whose two operand intervals don't overlap collapses to the winning
  side, so the tape shrinks for that region before per-pixel evaluation.
  Reports up to ~100x expression-complexity reduction on a benchmark model,
  making thousand-op CSG trees render interactively. Targets JIT-compiled
  per-scene kernels, not a fixed program interpreter.
- **Determinism / cross-backend fit:** the interval min/max/add core is
  bit-stable in principle, but a full per-op interval kernel run on GPU is a
  *new* cross-backend numeric surface that must match SPIR-V/DXIL bit-for-bit —
  strictly more determinism work than the Lipschitz alternative for a
  comparable pruning result (see Barbier below).
- **Puck verdict:** borrow the concept, not the mechanism — effort **XL** for
  a full per-op interval kernel, poor marginal return once Puck's instance-tile
  cull and Barbier's Lipschitz criterion are accounted for. "Specialize the
  tape per region" is the right north star; Fidget's tape/trace/specialize
  decomposition (below) is the right mental model to borrow.
- **See also:** [hierarchical-and-instance-acceleration.md](hierarchical-and-instance-acceleration.md), [lipschitz-and-field-correctness.md](lipschitz-and-field-correctness.md), [verdict-index.md](verdict-index.md).

### Fidget (2024 VM-vs-JIT follow-up)
- **Source:** Matthew Keeter, *Fidget: Yet Another Implicit Kernel*
  (self-published research note, July 2024).
  https://www.mattkeeter.com/research/fidget-2024.pdf (project/demo:
  https://www.mattkeeter.com/projects/fidget/; code:
  https://github.com/mkeeter/fidget).
- **Digest:** Reworks MPR as a clean Rust kernel: a math tree compiles to an
  SSA tape/bytecode evaluated three ways (point, interval, forward-AD); a
  "trace" records one Choice (LHS/RHS/Both) per `min`/`max` clause during
  interval eval, and that trace drives tape simplification into a per-region
  specialized tape — "interval evaluation → tape simplification → per-pixel
  evaluation over active tiles & specialized tapes." At 1024³ the interval-
  driven VM interpreter runs ~61.7ms vs. 22.6ms for MPR and 23.6ms for the
  JIT — interpreted tape reduction is ~2.5–2.7x slower than the JIT-compiled
  form once the tape is short, because opcode-dispatch overhead dominates.
- **Determinism / cross-backend fit:** same interval-kernel caveat as MPR; the
  headline number that actually transfers to Puck is the interpreter-vs-JIT
  gap itself, not a determinism finding — it bounds how much of tape
  reduction's upside an interpreter (which is what Puck's fixed `uint[]` VM
  is, on both backends) can realize.
- **Puck verdict:** the tape/trace/specialize decomposition is the right
  mental model, but Puck can't JIT per-scene kernels, so the transferable
  prize is shorter per-tile tapes (fewer segments visited), not codegen —
  effort **XL** if built as a full per-op interval interpreter, same as MPR;
  kept as the fallback if Lipschitz bounds (Barbier) ever prune too loosely.
- **See also:** [march-loop-scheduling.md](march-loop-scheduling.md), [verdict-index.md](verdict-index.md).

### Barbier et al., Lipschitz Pruning
- **Source:** Wilhem Barbier, Mathieu Sanchez, Axel Paris, Élie Michel,
  Thibaud Lambert, Tamy Boubekeur, Mathias Paulin, Theo Thonat, *Lipschitz
  Pruning: Hierarchical Simplification of Primitive-Based SDFs*, Computer
  Graphics Forum 44(2) (Eurographics 2025, Best Paper Honorable Mention).
  https://onlinelibrary.wiley.com/doi/10.1111/cgf.70057 ; open preprint:
  https://wbrbr.org/publications/LipschitzPruning/documents/LipschitzPruning_submitted_to_EG25.pdf
  ; project + source: https://wbrbr.org/publications/LipschitzPruning/ ,
  https://github.com/wbrbr/LipschitzPruning.
- **Digest:** Prunes a smooth-CSG BlobTree per world-space region using only
  the 1-Lipschitz property — no interval arithmetic. For a cell of radius `R`
  centred at `p`, a binary operator with child fields `f1`,`f2` is skipped
  (replaced by one operand) if `|f1(p) − f2(p)| ≥ k + 2R`, where `k` is the
  operator's blend radius: one evaluation at the cell centre plus the
  Lipschitz bound proves the operands stay far enough apart across the whole
  cell. Two tree traversals per cell, run hierarchically over a dense grid
  (4³→256³ in their implementation), each level pruning from the parent
  cell's already-pruned tree; a complementary far-field culling substitutes a
  single conservative constant `sign(d)·(|d|−R)` when `|f(p)| > C·R`. Handles
  hard and smooth CSG exactly, treats primitives as black boxes, needs no
  preprocessing (fast enough to reprune every frame). Reported: down to ~1
  active node per cell, sphere-tracing speedups up to ~two orders of
  magnitude vs. naive tracing (×629 on a 6023-node scene), far-field culling
  worth up to another ×2. The authors explicitly compare to Keeter/MPR: same
  goal, but Lipschitz bounds prune "as well as" interval/affine arithmetic
  from a single cheaper evaluation, handle smooth booleans more efficiently,
  and need no per-primitive interval query (§5.2, Fig. 13).
- **Determinism / cross-backend fit:** the natural fit and the safest option.
  Its criterion uses the same per-op Lipschitz norms Puck already bakes
  deterministically in `AnalyzeLipschitz` — no new numeric machinery, no
  interval evaluator. Because pruning is view-independent (it partitions
  world space, not screen space), it computes CPU-side at `UploadProgram`/
  program-build time, deterministic by construction, with zero cross-backend
  parity risk — the GPU just reads a segment-subset list.
- **Puck verdict:** **ADOPT as the model** — quoting the review, "almost
  purpose-built for Puck." Staged effort: Stage 1 (`AnalyzeSegment` Push/Pop
  cases + shared Lipschitz region-bound helper) is **S–M**; Stage 2 (far-field
  constant substitution per instance/cell) is **S** and likely the best
  effort:payoff first increment; Stage 3 (per-instance/per-cell active-segment
  bitmask, the MVP pruner) is **M**. All three are gated on the scoped
  `PushField`/`PopField` accumulator landing first — on today's flat model,
  only Union-family chains are segment-eligible, and those are exactly the
  instances the beam prepass already masks well; the onion'd/intersection
  chains that dominate frame cost are `segmentEligible = false` and structurally
  invisible to the pruner until the accumulator restores their bounds. Net:
  a real, constant-factor win on the scenes that currently bite (town-scale,
  creator scenes) — not a 100x, because Puck's instance-level tile cull
  already banks the coarse speedup and Puck is an interpreter.
  **Status (2026-07-08): the gate is open** — the scoped accumulator shipped
  (commit `8fdabd4`, depth 1); Stages 1-3 above are unblocked, not yet built.
- **See also:** [hierarchical-and-instance-acceleration.md](hierarchical-and-instance-acceleration.md), [lipschitz-and-field-correctness.md](lipschitz-and-field-correctness.md), [lod-and-bounds.md](lod-and-bounds.md), [verdict-index.md](verdict-index.md).

### Zanni et al., Synchronized Tracing of Implicit Surfaces
- **Source:** Cédric Zanni et al., *Synchronized-Tracing of Implicit
  Surfaces*, ACM TOG 2024. https://arxiv.org/abs/2304.09673.
- **Digest:** Attacks evaluation *coherence* rather than tape length. Builds,
  per screen tile/subfrustum, a low-resolution A-buffer holding the
  "primitives of interest" via a sparse bottom-up tree traversal using compact
  smooth-CSG operators (extended from standard bounded operators) that yield a
  tight volume of interest per primitive; then synchronizes the workgroup's
  threads so every thread in a tile evaluates the same coherent, pruned
  primitive set. Standard sphere tracing runs on the reduced set. Scales to
  thousands of primitives; needs no preprocessing when primitive parameters
  change, which suits animation.
- **Determinism / cross-backend fit:** an occupancy technique (workgroup
  barriers over a per-tile list); the list contents still need to be a
  deterministic function of the program. Its per-tile A-buffer is the closest
  analogue to Puck's existing per-tile instance bitmask, so its determinism
  story is the one the beam prepass already passes — but pushing it to
  sub-instance (segment) granularity re-opens that gate at finer resolution.
- **Puck verdict:** **adopt later, for coherence** — the right shape for a
  later stage (effort **L**) once the world-grid pruner (Barbier, Stage 3)
  proves its worth; only pursue if Stage 3 measurements show world-grid
  granularity still leaves meaningful cost on tiles, since it re-opens the
  parity gate at segment granularity. Also the closest existing pipeline to
  the tile-level partial inclusion evaluator recommended below — the
  "compact smooth operators → tight volume of interest" idea echoes Puck's
  existing halo derivations.
- **See also:** [hierarchical-and-instance-acceleration.md](hierarchical-and-instance-acceleration.md), [march-loop-scheduling.md](march-loop-scheduling.md), [verdict-index.md](verdict-index.md).

### Aydinlilar & Zanni, Forward Inclusion Functions
- **Source:** Melike Aydinlilar, Cédric Zanni, *Forward Inclusion Functions
  for Ray-Tracing Implicit Surfaces*, Computers & Graphics 114 (2023)
  190–200. DOI 10.1016/j.cag.2023.05.026. Open preprint:
  https://inria.hal.science/hal-04129922 (may sit behind a bot-wall to
  automated fetchers; abstract also mirrored at
  https://www.sciencedirect.com/science/article/abs/pii/S009784932300081X).
- **Digest:** Proposes asymmetrical forward inclusion functions, pinned exact
  at the ray's *current* query point `t` and growing outward, rather than
  classic interval/affine bounds that are symmetric about the interval and
  slack everywhere — a direct win for grazing rays, since the marcher's step
  decisions are dominated by the bound near where it currently stands. Two
  load-bearing claims for Puck: (1) **directional Lipschitz bounds are linear
  inclusion functions** — segment tracing (Galin 2020) is therefore the
  *linear member* of the same family, not a rival technique, and composes
  with it; (2) a quadratic version (bounding derivatives, or bottom-up
  composition) tightens thin-shell and multi-hit/transparency cases further.
- **Determinism / cross-backend fit:** the interval-arithmetic (IA) subset —
  add/sub/mul/min/max/abs — reuses the same IEEE-754 primitives the point
  evaluator already proves stable, so it is bit-stable across Vulkan/D3D12
  under the discipline the point path already requires: **no FMA
  contraction** (the interval multiply's four products + min/max must be
  emitted with contraction disabled, exactly like the point path) and **no
  vendor transcendentals or affine-arithmetic regression ops** in the gated
  path (Knoll independently reaches the same conclusion — see below). A
  few-ulp interval widening restores the "never miss" conservativeness lost
  to round-to-nearest (no directed rounding modes in HLSL/SPIR-V) without
  affecting determinism, since the widening constant is applied identically
  on both backends.
- **Puck verdict:** **adopt as a tile-level inclusion evaluator**, built
  jointly with tape pruning; defer per-step interval marching to a
  stall-triggered fallback; do not build a full 27-op interval interpreter.
  Effort **M** for the IA micro-op library + compiler emission (contraction
  off, few-ulp widening, parity test); **M–L (recommended)** for the partial
  interval evaluator (IA subset + blend get real transfers; fold/
  log-spherical/cell-jitter fall back to the existing Lipschitz `stepScale`
  for their subtree — legal because Lipschitz bounds *are* linear inclusion
  functions); **M** incremental for a per-pixel stall-triggered bisection
  fallback; **XL** (poor marginal return) for the full interpreter. The op
  mix caps the upside either way: domain-fold, log-spherical wrap, and
  cell-jitter all collapse to their full-period hull the moment a domain
  interval spans one period, which is most of the march — inclusion shines on
  the *smooth composed* field (blends, thin shells, transparency), which
  happens to be Puck's actual grazing-ray pain point.
- **See also:** [lipschitz-and-field-correctness.md](lipschitz-and-field-correctness.md), [lod-and-bounds.md](lod-and-bounds.md), [march-loop-scheduling.md](march-loop-scheduling.md), [verdict-index.md](verdict-index.md).

### Knoll et al., Fast Ray Tracing of Arbitrary Implicit Surfaces with Interval and Affine Arithmetic
- **Source:** Aaron Knoll, Younis Hijazi, Charles Hansen, Ingo Wald, Hans
  Hagen, *Fast Ray Tracing of Arbitrary Implicit Surfaces with Interval and
  Affine Arithmetic*, Computer Graphics Forum 28(1) 2009, 26–40. DOI
  10.1111/j.1467-8659.2008.01189.x. Full text:
  https://www.sci.utah.edu/~knolla/cgrtia.pdf.
- **Digest:** Foundational precursor to the Keeter line of work. Establishes
  the inclusion property (Moore's fundamental theorem: an interval extension
  `F` of `f` is an inclusion function if `F(x) ⊇ {f(x) | x ∈ x}`) and the
  robust rejection test `0 ∈ Ft(t)` for a ray sub-interval — a guaranteed
  skip, unlike point-sampling which can silently miss thin non-monotonic
  features. Covers interval arithmetic (cheap, GPU-friendly, but overestimates
  under multiplication/cancellation because it forgets operand correlation)
  and affine arithmetic / reduced AA (RAA, a `float3`-sized fixed-width form
  that keeps correlated error symbols from accumulating, tighter but spawns
  new error symbols on non-affine ops). Introduces a stackless GPU bisection
  traversal (§5.5) for robust termination without a stack.
- **Determinism / cross-backend fit:** directly authoritative on the rounding
  question. Knoll deliberately omits the textbook per-op *outward* rounding
  step ("the typical precision ε is sufficiently large that rounding has
  negligible impact") and, on RAA's regression-line ops, states plainly they
  are "ill-suited for inaccurate GPU floating point arithmetic ... we
  therefore resort to interval arithmetic for functions that require
  regression-approximation AA operators" (§5.4) — a direct precedent for
  Puck's call to keep AA out of the parity-gated path and treat it as an
  unparityed perf-only experiment if ever revisited.
- **Puck verdict:** surveyed and reviewed as the foundational/determinism
  reference for the Aydinlilar forward-inclusion entry above, not adopted as
  a standalone technique in its own right; its stackless bisection traversal
  is folded into that entry's recommended stall-triggered march fallback (**M**,
  incremental).
- **See also:** [lipschitz-and-field-correctness.md](lipschitz-and-field-correctness.md), [verdict-index.md](verdict-index.md).

### Fryazinov & Pasko, revised affine arithmetic for FRep/R-functions
- **Source:** Oleg Fryazinov, Alexander Pasko, *Extending Revised Affine
  Arithmetic for Fast Reliable Ray-Tracing of Procedurally Defined Implicit
  Surfaces* (Technical Report TR-NCCA-2009-04, and the GPU follow-up
  "GPU-based real time FRep ray casting," Computers & Graphics ~2010–2011).
  https://eprints.bournemouth.ac.uk/11708/1/TR-NCCA-2009-04.pdf ;
  https://www.researchgate.net/publication/228577600_GPU-based_real_time_FRep_ray_casting
  ; ScienceDirect record S009784931000107X.
- **Digest:** Extends revised affine arithmetic (RAA) with explicit
  per-operator affine transfer functions for R-function CSG, bounded blends,
  and conditionals — the "weird operator" transfer-function pattern any
  affine treatment of Puck's non-standard ops (smooth blends, warps) would
  need. Yields tighter/faster function-range bounds than plain interval
  arithmetic for procedurally-combined implicit surfaces; the GPU follow-up
  moves ray-surface intersection, field evaluation, and normals entirely onto
  the GPU.
- **Determinism / cross-backend fit:** falls on the wrong side of Knoll's own
  determinism finding above — AA/RAA regression-line operators (sqrt,
  transcendentals, division fit to a regression line) are not bit-identical
  across NVIDIA/AMD/Intel, so this family stays out of the parity-gated path
  per the corpus's stated discipline; usable only as an unparityed perf-only
  experiment, mirroring Knoll's own IA-fallback decision.
- **Puck verdict:** surveyed, not deep-reviewed — cited by both phase-3
  reviews as precedent for per-operator affine transfer functions and as the
  concrete example of the AA family the determinism discipline excludes from
  the gated path, but not itself carried into a staged Puck implementation.
- **See also:** [lipschitz-and-field-correctness.md](lipschitz-and-field-correctness.md), [materials-and-primitives.md](materials-and-primitives.md), [verdict-index.md](verdict-index.md).

## The two load-bearing framings

**Tape pruning is gated on the scoped accumulator, and it's a constant-factor
win, not 100x.** *Status (2026-07-08): the gate is open — the accumulator is
built (`PushField`/`PopField`, depth 1, commit `8fdabd4`), not unbuilt as this
section originally framed it; the staged pruner itself remains to be built.*
On Puck's flat model today, `AnalyzeSegment` sets
`segmentEligible = false` for any non-Union blend or field op, so every
onion'd creator shape and every intersection chain is an always-evaluated,
unmaskable (`UnmaskableBoundRadius = 1e30`) instance with no segment or shape
bounds a pruner could shorten — exactly the geometry that dominates frame
cost, and the pruner is structurally blind to it. `PushField`/`PopField`
(the scoped accumulator) is what hands those chains back a far-neutral
compose, restoring maskability and segment eligibility; only then does
per-region pruning have high-value work to do. Once it lands, Barbier's
Lipschitz criterion is the model to build: it reuses the per-op Lipschitz
norms `AnalyzeLipschitz` already bakes, is view-independent so it computes
deterministically at program-build time with zero new parity surface, and the
authors' own comparison shows it prunes "as well as" Keeter's interval
arithmetic without needing an interval evaluator at all. The headline figures
from the papers (Keeter's ~100x, Barbier's ×629) are measured against naive
whole-tree sphere tracing with zero spatial culling — Puck already banks the
coarse speedup at instance granularity via the beam prepass's per-tile
instance bitmask, and Puck is an interpreter, so it loses most of tape
reduction's theoretical upside to opcode-dispatch overhead (Fidget's own
numbers: ~2.5–2.7x slower than JIT once the tape is short). What remains is
real but constant-factor: shorter per-tile tapes on the scenes that currently
bite, not a 100x scene-wide win.

> **Empirical status (Puck): Xor unmaskable-exemption CONFIRMED EXEMPT**
> (2026-07-08 in-demo correctness hunt, real-GPU slice comparison). Xor's
> exterior field is bit-identical to union — `max(min(acc,b),-max(acc,b))`
> reduces to `min(acc,b)` wherever the candidate is exterior, and a first-hit
> march never reaches `acc < -b` — and its extra carved surface lives strictly
> inside the union hull, hence inside any covering cull bound, hence never
> masked. `HasUnmaskableCompose` correctly omits Xor, `MaxSmoothBlendRadius`
> correctly gives it zero halo, and `AnalyzeSegment` already marks it
> always-evaluated, corroborating this framing. One residual (a documentation
> note, not a gate): Xor's cull-bound *sizing* is union-like — it needs an
> influence margin — not subtraction-like. Full record:
> [verdict-index.md](verdict-index.md#empirical-status-in-puck).

**Segment tracing is the linear member of the forward-inclusion family — they
compose, not compete.** Aydinlilar & Zanni's central result is that a
directional Lipschitz bound over a ray sub-interval *is* a first-order
(linear) forward inclusion function, so segment tracing (already on Puck's
roadmap) and interval/affine inclusion bounds are the same framework at
different polynomial orders, not rival techniques. The recommended shape for
Puck is therefore not "replace Lipschitz with intervals" but a **partial IA
evaluator at tile granularity, with Lipschitz fallback for the ops that don't
have a useful interval transfer** (domain-fold, log-spherical wrap,
cell-jitter — all of which collapse to a full-period hull once the interval
spans one period, under any algebra). Deployed at the beam/tile level, one
interval evaluation per tile-cone amortizes over thousands of pixels and
shares its evaluator with tape pruning: the same region-bound machinery
(Lipschitz-first, generalized to interval only if proven necessary) serves
cull, march-start distance, and per-tile tape pruning together, rather than
building three separate subsystems.

## See also
- [README.md](README.md) — wiki index and method.
- [hierarchical-and-instance-acceleration.md](hierarchical-and-instance-acceleration.md)
- [lod-and-bounds.md](lod-and-bounds.md)
- [lipschitz-and-field-correctness.md](lipschitz-and-field-correctness.md)
- [march-loop-scheduling.md](march-loop-scheduling.md)
- [verdict-index.md](verdict-index.md)
- [../sdf-sota-survey.md](../sdf-sota-survey.md)
