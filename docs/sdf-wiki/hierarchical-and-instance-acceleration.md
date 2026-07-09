# Hierarchical and Instance Acceleration

Puck's `sdf-beam` kernel already runs a single-level 16×16 cone-march that
collapses each tile to an instance bitmask for the indirect-dispatch fine
march (`sdf-world-views`). Every technique on this page is a candidate
refinement of that one seam: deepen the beam into a coarse-to-fine pyramid,
teleport the fine march across the empty space the beam already sees, or
accelerate the O(instances) cost once instance counts make it hot.

**Discovery note (2026-07-09):** that O(instances) cost was framed below as
"the beam's inner loop" — a binning loop over instances per tile. Measurement
found otherwise: the hot cost was the cone march's own per-sample field
enumeration (~96 steps × 4000 tiles, each step bounding against every
per-instance candidate before early-out), not the tile-to-instance binning
loop itself. The landed fix (see the uniform-grid entry below) is therefore a
mask-first pre-pass: a new kernel computes the tile-instance mask *before* the
cone march runs, so the march consumes an already-masked field instead of
enumerating instances per sample.

### Hierarchical cone pre-pass / four-bound teleport

- **Source:** Jarl Larsson (Image & Form/Thunderful), "Raymarching The Gunk,"
  technical write-up, 2021. https://jarllarsson.github.io/gen/gunkraymarcher.html
- **Digest:** *The Gunk*'s Xbox Series X renderer runs a coarse cone-trace
  prepass at 1/8 resolution (each coarse texel a tile over 8×8 final pixels)
  that writes four values per tile along the representative cone: entry
  distance, first-exit distance, second-entry distance, and a far bound. The
  full-resolution march reads its tile's four bounds and teleports — starting
  at *entry*, and on failing to converge before *first exit*, jumping straight
  to *second entry* — skipping the empty gap between two occupied regions
  entirely. Measured 4K on Series X: 6.6 ms → 4.0 ms (~39%) from the prepass
  alone.
- **Determinism / cross-backend fit:** Excellent — the four-bound prepass is
  per-tile independent with no cross-thread communication, no atomics, no
  wave voting; both backends run the identical HLSL cone-march and the shared
  Lipschitz `stepScale`/step schedule make the four bounds bit-identical
  across SPIR-V and DXIL by the same argument that already makes the current
  bitmask parity-clean. The teleport itself is a branchless `t = max(t,
  entry)` / `if (t > exit) t = secondEntry` in the fine kernel — order-
  independent, numerically identical on both backends. One rule to pin:
  *second-entry* must be a total function (defined as `far` when there is no
  second region) so the fine kernel's teleport is well-defined for every tile,
  a constant-fold in the beam kernel rather than a data-dependent branch that
  could diverge between backends.
- **Puck verdict:** adopt-now, effort S–M. The review calls this "the
  cheapest item in the survey with the best-attested payoff" and "the single
  most defensible number in the whole survey: a shipped title, a real
  console, a concrete before/after on an SDF march." Puck's beam already pays
  the cone-march cost but discards the depth information it implicitly
  computes; extending the per-tile output from `{ instanceBitmask }` to `{
  instanceBitmask, entry, firstExit, secondEntry, far }` and teleporting in
  `sdf-world-views` is ~6 lines of HLSL consumer-side change with zero host
  packer changes. The town overview — sparse analytic instances across a
  large volume, mostly empty space between occupied bands — is exactly
  Larsson's regime, and should see *more* than his 39% because the
  empty-space fraction is larger. Promoted as the first D3 increment,
  independent of the instance-cull question (Leg 1 of the review).
- **Landed (2026-07-08, commit `ac373a8`).** The beam cone-march records ONE
  proven-empty gap `[firstExit, secondEntry]` past the tile entry and the fine
  march teleports across it — the gap-skip subset sized to the single-level
  16×16 beam (no 64×64 pre-level, deferred as Leg 3b). The tile cull buffer
  gains two planes for the gap; plane 0 (the classic `marchStart`) stays
  byte-identical, so `sdf-cull-args` and the compositor are untouched. The gap
  search is bounded (`TileGapSteps`) and reports only proven-empty spans; an
  unproven tile packs `firstExit = MaxDistance` so the teleport is a dead
  branch. DIST's 1.5× aggressive march (Leg 3a) rode along in the same commit.
- **See also:** [marching-acceleration.md](marching-acceleration.md),
  [march-loop-scheduling.md](march-loop-scheduling.md),
  [verdict-index.md](verdict-index.md).

### Single-frame coarse-to-fine ray pyramid

- **Source:** Shaohui Liu, Yinda Zhang, Songyou Peng, Boxin Shi, Marc
  Pollefeys, Zhaopeng Cui, "DIST: Rendering Deep Implicit Signed Distance
  Function with Differentiable Sphere Tracing," CVPR 2020.
  https://arxiv.org/abs/1911.13225 ,
  https://openaccess.thecvf.com/content_CVPR_2020/html/Liu_DIST_Rendering_Deep_Implicit_Signed_Distance_Function_With_Differentiable_Sphere_CVPR_2020_paper.html
- **Digest:** DIST's forward renderer sphere-traces at a downsampled
  resolution first, then progressively increases resolution — the pyramid
  mechanic being that each finer ray *inherits the marched depth `t` of its
  coarse parent*, so finer rays start already advanced through the empty
  space the coarser level cleared. The far fewer expensive queries at coarse
  steps is DIST's headline win (its `map()` is a costly neural forward pass);
  the aggressive 1.5×-step marching detail that rides alongside this schedule
  is documented separately on [marching-acceleration.md](marching-acceleration.md)
  — this entry is about the pyramid *structure* only.
- **Determinism / cross-backend fit:** Good, but only for a *fixed* schedule.
  DIST, a differentiable renderer, can afford data-dependent adaptive ray
  refinement because it doesn't need cross-backend bit parity — Puck cannot,
  so only a fixed-level, fixed-dispatch-size pyramid is adoptable: each level
  (e.g. 64×64 tiles → 16×16 tiles → per-pixel) is a `Dispatch` whose thread
  count is a compile-time function of resolution, never a per-level count
  that depends on how many rays "survived" (which would require an atomic-
  append compaction with vendor-dependent ordering). Any inter-level pruning
  must be **prefix-sum compaction (count → scan → scatter), never
  `InterlockedAdd` append** — exactly what `sdf-cull-args` already is, one
  level up, so the review calls the pyramid deterministic "iff we keep the
  level structure static and express any inter-level pruning as prefix-sum
  compaction." No wave-vote should gate the aggressive step (per-lane, never
  a wave-min/max that would couple lanes and perturb output).
- **Puck verdict:** gated-on-measurement (the review's "Leg 3b"), effort M–L
  for a full third level with its own prefix-sum `cull-args` stage; the
  1.5×-march component alone is S and adopt-now (see
  [marching-acceleration.md](marching-acceleration.md)). The review's
  structural finding: Puck's existing "beam prepass → fine march" **already
  is** a 2-level pyramid (coarse tile cone inheriting into a full-res march),
  and DIST validates that shape rather than obsoleting it. "A 3-level
  pyramid does not categorically beat our 2-level beam." A third level (a
  64×64 pre-level feeding the 16×16 beam) helps only where the coarse level's
  conservative bound is too loose — the wide-cone town-overview regime — and
  only once the instance cull is itself accelerated (rides the uniform-grid
  leg below), because past ~3 levels the returns fall off: each extra level
  costs a full dispatch and a bound read.
- **Demoted / near-moot (2026-07-09).** The uniform grid landed
  (mask-first pre-pass, see below) and the beam no longer dominates —
  the O(instances) cost this entry's gating condition depended on turned out
  to be the cone march's per-sample enumeration, which the grid's pre-pass
  already fixes. A third pyramid level would only pay for itself if the beam
  became dominant again post-grid, which has not been shown.
- **See also:** [marching-acceleration.md](marching-acceleration.md),
  [march-loop-scheduling.md](march-loop-scheduling.md),
  [verdict-index.md](verdict-index.md).

### Two-level (TLAS/BLAS) instance acceleration

- **Source:** Vassillen Chizhov (Interplay of Light), "Adding support for
  two-level acceleration for raytracing," 2020.
  https://interplayoflight.wordpress.com/2020/11/01/adding-support-for-two-level-acceleration-for-raytracing/
- **Digest:** A software two-level structure: a BLAS per unique model in
  object space, built once and reused across instances, and a TLAS over the
  world-space bounding boxes of instances, each leaf carrying a BLAS
  reference plus a world-to-object matrix. Rays are transformed into each
  BLAS's object space rather than transforming geometry to world space; the
  TLAS is rebuilt every frame with fast-build settings while BLASes stay
  static. Reported on an HD4000 at 1280×720: two-level adds no meaningful GPU
  cost over a monolithic BVH (~31 ms GPU either way) while the CPU TLAS
  rebuild is only ~1.2 ms, enabling per-frame instance animation.
- **Determinism / cross-backend fit:** Riskier than the grid alternative. A
  GPU LBVH build uses a radix sort, and a deterministic radix sort across
  vendors is exactly the kind of cross-backend ordering hazard the engine's
  honor rules warn against; a CPU BVH build sidesteps vendor divergence but
  reintroduces float→Morton quantization pinning and costs more per frame
  than a grid scatter.
- **Puck verdict:** reject (in favor of the uniform grid below) for Puck's
  actual instance shape, effort L for a real BVH / L–XL for the two-level
  structure in full generality. The review's read: for ≤1024 dynamic
  analytic instances (the cap has since been raised to 16384 — the rejection
  verdict is unchanged), Puck's per-*segment* object-space bounds already ARE
  the "BLAS" (computed once at `UploadProgram`), but because each instance is
  a single analytic shape, "we have no deep BLAS to traverse — the 'BLAS hit
  test' is one sphere-vs-cone. That collapses the two-level structure's
  value: we need only the top level (the instance grid); there is no
  per-model sub-tree worth reusing." So the honest read is "TLAS-only," and
  "the cheapest correct TLAS for 1024 (now 16384) dynamic analytic instances
  is a uniform grid / spatial hash, not a BVH." The review states outright: "A real
  per-frame BVH is the one option this review recommends against" for
  Puck's instance counts — its BLAS-reuse value is near-zero and its
  per-frame deterministic sort cuts against the honor rules the grid
  satisfies for free.
- **See also:** [lod-and-bounds.md](lod-and-bounds.md),
  [verdict-index.md](verdict-index.md),
  [negative-results-and-rejections.md](negative-results-and-rejections.md).

### Uniform grid / spatial hash instance cull

- **Source:** Alexander Millane et al. (NVIDIA), "nvblox: GPU-Accelerated
  Incremental Signed Distance Field Mapping," arXiv:2311.00626, 2023.
  https://arxiv.org/abs/2311.00626 ; "Resolution Where It Counts: Hash-based
  GPU-Accelerated 3D Reconstruction via Variance-Adaptive Voxel Grids," ACM
  TOG 2025, https://arxiv.org/html/2511.21459 ; "VoxelCache: Accelerating
  Online Mapping…" (sparse voxel-hash mapping),
  https://arxiv.org/pdf/2210.08729 ; general GPU acceleration-structure
  comparison, https://www.ks.uiuc.edu/Research/vmd/projects/ece498/raytracing/GPU_BVHthesis.pdf
- **Digest:** Bucket instances into cells of a regular grid keyed by a flat
  GPU hash table; a query (for Puck, a tile-cone) visits only the cells it
  overlaps and tests only the instances bucketed there — constant-time cell
  access, no hierarchical traversal, and far cheaper and more regular to
  build than a BVH. The literature is consistent that spatial subdivision
  structures are faster to build than BVH-based approaches because they are
  regular, at the cost of wasted work when instance density is very
  non-uniform relative to cell size (the "teapot in a stadium" problem).
- **Determinism / cross-backend fit:** Good, with one rule. A grid bin is a
  deterministic integer scatter (`cell = floor((center - origin) *
  invCellSize)`); with a CSR/prefix-sum bucketing this is an
  order-independent, allocation-free CPU pass, trivially bit-reproducible
  frame to frame. The bitmask output is safe regardless of per-cell
  insertion order (OR is commutative), but the four-bound entry/exit values
  from the hierarchical-cone-prepass leg above *could* be perturbed by tie
  order between instances — so the grid must be **built CPU-side with a
  stable order** (instance-index order within each cell, via prefix-sum CSR)
  so both backends read identical, ordered cell lists. If ever built on GPU,
  it must use **prefix-sum compaction (count → scan → scatter), never
  `InterlockedAdd`-append** — the same rule the honor discipline applies
  everywhere else in the pipeline.
- **Puck verdict:** gated-on-measurement (the review's "Leg 2"), effort M.
  This is the review's recommended structure over a BVH: "the deterministic
  uniform grid / CSR spatial hash is the right acceleration structure —
  cheaper to build per frame, order-stable via prefix-sum, uniform per-lane
  traversal." Implementation is one host build function (count/scan/scatter
  emitting `cellStart[]`/`cellInstances[]` into the program's side tables)
  plus a beam inner-loop rewrite from a flat `for (i in 0..N)` to a cell walk
  — no new kernel, no indirect-dispatch restructuring. But its value is
  proportional to how hot the O(instances) cull actually is, which nothing
  has yet measured: promote it only if `PUCK_TIMING=1` on the full-extent
  Puckton town overview shows `sdf-beam` (not `sdf-world-views`) dominating
  GPU-ms, rising roughly linearly with instance count toward the (then-)1024
  cap. If the fine march dominates instead, the review says the
  hierarchical-cone teleport and the 1.5× aggressive march (adopted
  unconditionally) likely lift the ceiling without needing this at all.
- **Landed, mask-first (2026-07-09, commits `f08add1`, `1931d3c`).** Shipped
  as a NEW pre-pass kernel (`sdf-instance-cull.comp`), not the beam
  inner-loop rewrite predicted above: a fused variant (cull folded into the
  cone march) was tried first and rejected — its 512 B/thread scratch cost the
  co-resident cone march ~+12% occupancy (measured). The mask-first pre-pass
  computes the tile-instance mask before the march runs, so the march
  consumes an already-masked field instead of enumerating instances per
  sample — see this page's discovery note above: the measured O(instances)
  cost was the cone march's per-sample enumeration, not the binning loop the
  original prediction assumed it would replace. `world-grid-cull` is the new
  Post gate; grid and flat paths are bit-identical. `MaxInstances` raised to
  16384, `MaxCarves` to 4096 (commit `1e389db`).
- **See also:** [lod-and-bounds.md](lod-and-bounds.md),
  [verdict-index.md](verdict-index.md).

### GPU Work Graphs

- **Source:** Kuth, Oberberger, et al. (AMD/academic collaboration),
  "Real-Time Procedural Generation with GPU Work Graphs," HPG 2024 (Best
  Paper), ACM PACMCGIT. https://dl.acm.org/doi/10.1145/3675376 ; preprint:
  https://gpuopen.com/download/publications/Real-Time_Procedural_Generation_with_GPU_Work_Graphs-GPUOpen_preprint.pdf
- **Digest:** Uses the D3D12 Work Graphs model — nodes are shaders that
  dynamically emit new work for downstream nodes, scheduled entirely on-GPU
  without CPU round-trips — to drive recursive procedural generation,
  sustaining 79,710 instances augmented in 3.74 ms on an RX 7900 XTX with no
  CPU-side bookkeeping beyond what a normal renderer already has. For a
  Puck-shaped pipeline the pattern could replace the fixed-dispatch
  tile-then-pixel two-pass with a graph where a tile node dynamically spawns
  exactly the per-pixel work its cull pass determined is needed.
- **Determinism / cross-backend fit:** Named as a parity hazard in the
  sweep: D3D12 Work Graphs has no first-class Vulkan equivalent yet, so
  adopting it would require a compute-indirect-dispatch fallback on Vulkan
  just to keep the two backends in the same architecture — the opposite of
  the shared-`sdf-vm.hlsli`, one-kernel-both-backends discipline every other
  technique on this page is held to. It is also a newer, less mature GPU
  feature (driver risk) than compute shaders proper.
- **Puck verdict:** surveyed, not deep-reviewed. Flagged in the sweep as
  "with caveats" for the cross-backend gap and driver immaturity; not one of
  the three legs the D3 deep review carried forward (hierarchical teleport,
  instance acceleration, ray pyramid), and separately listed as a
  cross-backend parity hazard on
  [negative-results-and-rejections.md](negative-results-and-rejections.md).
- **See also:** [march-loop-scheduling.md](march-loop-scheduling.md),
  [negative-results-and-rejections.md](negative-results-and-rejections.md),
  [verdict-index.md](verdict-index.md).

## The split-trigger synthesis

The D3 deep review (`review-03-d3-hierarchy-bvh.md`) resolves "promote D3 now
or wait for GPU-bound" into two different triggers rather than one verdict:

1. **Promote now, unconditionally:** the hierarchical cone pre-pass /
   four-bound teleport (Leg 1) and the 1.5× aggressive march (Leg 3a, detailed
   on [marching-acceleration.md](marching-acceleration.md)). Both are S-effort,
   host-code-free, determinism-clean, and attack the ceiling the Puckton
   town scene is *already* hitting — a march-range/step-budget ceiling that is
   symptomatically an empty-space-cost problem, which is exactly what these
   two fix. Evidence is a shipped console title and a published renderer, not
   a hunch.
   *Empirical status (Puck): the ground-notch/MaxSteps hypothesis is REFUTED*
   (2026-07-08 in-demo correctness hunt): the termination view shows ground
   terminating uniformly on the footprint-adaptive threshold, never
   steps-exhausted, with near-black iteration counts. This *narrows* the
   promote-now premise — the specific ground-notch artifact is
   footprint-adaptive early termination amplified behind occluders by the
   per-tile marchStart teleport, which the four-bound teleport and aggressive
   march would NOT fix (the lever there is the footprint-epsilon / marchStart
   derivation). The legs remain justified on their own evidence as empty-space
   perf work; they are no longer justified as the fix for that notch. (Caveat:
   the notch geometry was not isolated in a single frame; the refutation rests
   on ground-never-red across all captures.) Full record:
   [verdict-index.md](verdict-index.md#empirical-status-in-puck).
2. **Measure-first (resolved 2026-07-09):** the uniform-grid instance cull
   (Leg 2) and the third pyramid level (Leg 3b). Their value was proportional
   to how hot the O(instances) beam cull actually was — which nothing had yet
   shown when this was written; `sdf-bench-notes` (2026-07-09) has since shown
   it, and Leg 2 is built: the hot cost was the cone march's per-sample
   enumeration, and the mask-first grid pre-pass fixes it directly, which is
   why the third pyramid level (Leg 3b) is now demoted/near-moot rather than
   promoted alongside it. The decision instrument as originally specified was
   `PUCK_TIMING=1` on the Puckton town overview at its intended (non-compact)
   extent, read via the Post `gpu-budget` stage: if `sdf-world-views`
   dominates, Legs 1+3a alone likely suffice and the grid should wait; if
   `sdf-beam` is a large and growing fraction scaling with instance count,
   promote the grid; if both are large after Legs 1/3a, build the grid first
   (so the third pyramid level has cheap per-cell candidate sets), then the
   third level.

All four legs pass the discipline scorecard the review runs against them:
prefix-sum-not-atomic-append, no output-altering wave-vote, identical tile
classification on both backends, deterministic build. GPU Work Graphs is the
one item on this page that was never run through that scorecard at all — it
is surveyed-only, carried forward only as a flagged parity hazard.

## See also

- [marching-acceleration.md](marching-acceleration.md) — the 1.5× aggressive
  march detail (Leg 3a) that rides alongside the ray-pyramid structure
  covered here.
- [lod-and-bounds.md](lod-and-bounds.md) — bounding-volume and early-out
  techniques adjacent to instance acceleration.
- [march-loop-scheduling.md](march-loop-scheduling.md) — the
  prefix-sum-compaction discipline (`sdf-cull-args`) that every fixed-dispatch
  pyramid level and grid build here depends on.
- [verdict-index.md](verdict-index.md) — this page's verdicts in the
  all-techniques table.
- [negative-results-and-rejections.md](negative-results-and-rejections.md) —
  the per-frame BVH rejection and the GPU Work Graphs parity-hazard flag.
- [../sdf-sota-survey.md](../sdf-sota-survey.md) — the ranked decision
  shortlist this wiki is a reference companion to.
