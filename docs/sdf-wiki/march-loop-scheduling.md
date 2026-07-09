# March Loop Scheduling

Techniques that restructure the **kernel shape** of the march itself — one
big megakernel interpreter running every ray to convergence vs. splitting it
into stages, compacting alive work between them, or overlapping it with other
GPU work — as opposed to changing what a single ray does per step (that's
[marching-acceleration.md](marching-acceleration.md)) or how tiles/instances
get culled before the march launches (that's
[hierarchical-and-instance-acceleration.md](hierarchical-and-instance-acceleration.md)).
Puck's baseline is one HLSL compute kernel — the ~27-op SDF interpreter switch,
compiled to SPIR-V and DXIL — indirect-dispatched over live tiles behind a
16×16 beam prepass and cull-args reduction. The trigger condition for
revisiting kernel structure, per the deep review, is **per-step work ceasing to
be uniform across a wave** — per-tile specialized tapes or a heavy divergent
shade stage, neither of which exists today.

### Aila & Laine 2009 — Understanding the Efficiency of Ray Traversal on GPUs
- **Source:** Timo Aila, Samuli Laine, *Understanding the Efficiency of Ray
  Traversal on GPUs*, HPG 2009.
  https://research.nvidia.com/publication/2009-08_understanding-efficiency-ray-traversal-gpus
  ; PDF: https://research.nvidia.com/sites/default/files/pubs/2009-08_Understanding-the-Efficiency/aila2009hpg_paper.pdf
- **Digest:** Measures period GPU traversal kernels running 1.5–2.5x below a
  simulated throughput bound, and attributes the gap to hardware work
  distribution, not bandwidth: once a warp's rays finish at different depths,
  the warp holds a full slot while only a few lanes do useful work. The fix —
  "persistent threads": launch just enough warps to fill the machine, then
  have each warp pull fresh ray indices from a single global counter
  (`atomicAdd`) whenever occupancy drops, replacing finished lanes instead of
  idling to the tail. A secondary lever, speculative "while-while"/"if-if"
  traversal restructuring, gave smaller, vendor-specific gains.
- **Determinism / cross-backend fit:** The divergence shape transfers —
  intra-wave iteration-count variance is exactly our marcher's tail problem.
  But the mechanism does not survive as specified: a shared global counter
  driving `atomicAdd`-based dynamic fetch produces a nondeterministic
  ordering of compacted work across runs/vendors, which would leak into any
  reduction. It is usable only if turned into a deterministic prefix-sum/scan
  (stable, index-order preserving) with per-pixel results independent of
  ordering — the one general rule this whole page enforces. Persistent-threads
  *work-stealing* on a shared counter is disqualified outright, and doubly so
  because HLSL SM6 gives no portable cross-workgroup forward-progress
  guarantee — a spin loop that behaves on one vendor can starve or deadlock on
  another.
- **Puck verdict:** persistent-threads work-stealing is **incompatible** with
  the determinism contract and not portably expressible in SM6 — do not
  pursue. Cross-link:
  [negative-results-and-rejections.md](negative-results-and-rejections.md).
- **See also:** [negative-results-and-rejections.md](negative-results-and-rejections.md), [hierarchical-and-instance-acceleration.md](hierarchical-and-instance-acceleration.md), [verdict-index.md](verdict-index.md).

### Laine, Karras & Aila 2013 — Megakernels Considered Harmful: Wavefront Path Tracing
- **Source:** Samuli Laine, Tero Karras, Timo Aila, *Megakernels Considered
  Harmful: Wavefront Path Tracing on GPUs*, HPG 2013.
  https://research.nvidia.com/publication/2013-07_megakernels-considered-harmful-wavefront-path-tracing-gpus
  ; PDF: https://research.nvidia.com/sites/default/files/pubs/2013-07_Megakernels-Considered-Harmful/laine2013hpg_paper.pdf
- **Digest:** The canonical wavefront paper. Separates two costs of a
  megakernel: **register pressure** (one kernel inlining every material/BSDF
  plus the tracer is allocated the near-union register footprint over all code
  paths, which caps resident warps and throttles latency hiding — the paper's
  dominant argument) and **control-flow divergence** across material types.
  Fix: keep a large pool of paths alive, process them in stages (cast, then
  one kernel per material class) connected by compacted queues, refilling
  terminated paths from new samples to keep the pool full. Gains are
  scene-dependent, ~1.3–2x, and the authors are explicit that the win comes
  from **multiple complex materials** — homogeneous scenes, where the
  megakernel is already coherent, benefit little.
- **Determinism / cross-backend fit:** Queue-based stage separation is
  reproducible only if queue-fill order is deterministic — a stable
  prefix-sum/scan, never raw atomic append, and each output must depend
  solely on its own path so ordering is invisible to the result. This is the
  central rule the whole family must satisfy to be usable under Puck's
  bit-exact / calibrated-LSB parity gates.
- **Puck verdict:** the register-pressure argument transfers **weakly today**
  — Puck's per-step work is uniform (one interpreter switch, one scene field,
  no union of many materials), so there is no register blow-up to relieve.
  Per review-04: wavefront/compaction restructuring of the march loop proper
  is **deferred until per-tile specialized tapes land, or a heavy divergent
  shade stage lands** — i.e. until per-step work stops being uniform across a
  wave. Not now.
- **See also:** [tape-pruning-and-inclusion.md](tape-pruning-and-inclusion.md) (per-tile specialized tapes — the trigger condition), [negative-results-and-rejections.md](negative-results-and-rejections.md), [verdict-index.md](verdict-index.md).

### Wald 2011 — Active Thread Compaction for GPU Path Tracing
- **Source:** Ingo Wald, *Active Thread Compaction for GPU Path Tracing*,
  HPG 2011. https://www.sci.utah.edu/~wald/Publications/2011/PathCompaction/compact.pdf
- **Digest:** After each bounce, alive rays are stream-compacted
  (prefix-sum/scan → scatter into a dense array) so the next launch has no
  dead lanes. The sobering measurement: compaction raises SIMD utilization on
  later bounces, but it is not free — it adds a scan pass plus a full
  read/scatter of per-ray state (origin, direction, throughput, RNG, payload)
  every bounce, and that memory traffic **frequently eats most or all of the
  utilization win**. Net result is modest and scene-dependent, and can go
  negative when per-bounce work is cheap relative to the state that must be
  moved. Compaction pays only when path-length variance is high *and* the
  per-step work saved is large relative to the state shuffled.
- **Determinism / cross-backend fit:** Compatible — this is the one piece of
  classic wavefront machinery that survives Puck's rules intact, provided the
  scan is a fixed-topology, index-order-stable scan (a plain multi-pass
  Blelloch scan rather than decoupled-lookback, for portability) and per-ray
  results stay independent of ordering. Prefix-sum compaction feeding an
  indirect dispatch is the **only** determinism-safe survivor mechanism from
  this whole literature family — never an atomic-append queue, never an
  output-touching wave intrinsic, never a cross-group work-stealing persistent
  loop.
- **Puck verdict:** Wald's own honest measurement is Puck's expected regime —
  shuffling per-ray march state (position, direction, t, packed VM PC/stack)
  every iteration to keep lanes dense would move a lot of bytes to save steps
  that are individually cheap, the losing side of Wald's inequality. Per
  review-04: prefix-sum compaction is medium-high effort and **largely wasted
  effort now** — only worth building once a divergence source (per-tile tapes,
  a heavy shade stage) exists to justify it.
- **See also:** [tape-pruning-and-inclusion.md](tape-pruning-and-inclusion.md), [verdict-index.md](verdict-index.md).

### Claybook (Aaltonen, GDC 2018) — shipped precedent: stay a megakernel, mask instead
- **Source:** Sebastian Aaltonen, *GPU-Based Clay Simulation and Ray-Tracing
  Tech in Claybook*, GDC 2018.
  https://media.gdcvault.com/gdc2018/presentations/Aaltonen_Sebastian_GPU_Based_Clay.pdf
  ; talk: https://www.gdcvault.com/play/1025316/ ; Switch-port thread:
  https://threadreaderapp.com/thread/1076765876148490240.html
- **Digest:** Claybook is a shipped game whose world is rendered entirely by
  sphere-tracing a global (grid-based) SDF at 60fps on console — the closest
  production analog to Puck's marcher. It notably did **not** adopt
  wavefront/compaction restructuring: it kept a single sphere-tracing kernel
  and fought divergence with per-ray dynamic step masks / early-out lane
  masking, plus coarse culling and adaptive step counts. On the Switch port,
  Aaltonen additionally interleaved fluid-grid and SDF-modification-grid
  passes to halve compute-barrier count, and added a per-tile early-out
  heuristic that detects "fully inside/outside" tiles in the SDF-generation
  shader without touching per-particle data (~30% faster SDF gen on Switch) —
  the same class of tile-classification early-out Puck's beam prepass already
  performs.
- **Determinism / cross-backend fit:** Structurally deterministic — per-ray
  masking is a pure per-lane function of state, with no queue reordering to
  reason about; the tile early-out is a static function of geometry per tile,
  directly analogous to Puck's own cull-args reduction.
- **Puck verdict:** direct shipped evidence for where the wavefront/compaction
  payoff line sits: a state-of-the-art production SDF renderer facing this
  exact workload chose masks-in-a-megakernel over wavefront restructuring,
  reinforcing review-04's deferral verdict on compaction. The tile early-out
  idea is already the shape of Puck's beam prepass, not a new adoption.
- **See also:** [hierarchical-and-instance-acceleration.md](hierarchical-and-instance-acceleration.md), [marching-acceleration.md](marching-acceleration.md), [verdict-index.md](verdict-index.md).

### March / shade split (the wedge)
- **Source:** Puck engineering design (review-04-wavefront §d), derived from
  the register-pressure argument in Laine, Karras & Aila 2013 (see entry
  above): https://research.nvidia.com/publication/2013-07_megakernels-considered-harmful-wavefront-path-tracing-gpus
  — no standalone external paper; this is the review's own proposed smallest
  increment for Puck's kernel-structure seam.
- **Digest:** Split the current `beam prepass → cull-args reduction →
  indirect march (interpreter switch) → composite` chain into two dispatch
  hops instead of one: a **march kernel** that runs the interpreter to
  convergence and writes a per-pixel hit buffer (hit position, `t`, packed
  material/primitive id, normal or the data to recompute it), with no shading
  registers live in that kernel at all; and a separate **shade/composite
  kernel** that reads the hit buffer and produces color. Today shading is
  cheap and uniform, so the split is close to break-even on its own — its
  value is **latent**: it is the seam where a future divergent shade stage
  (multi-material SDF brushes, textured fields, multi-sample lighting) lands
  without inflating the march kernel's register footprint, and it lets the
  two kernels be occupancy-tuned independently.
- **Determinism / cross-backend fit:** Trivially deterministic — pixel-local,
  no compaction, no reordering, no new nondeterminism class. Parity gates
  should hold bit-exactly modulo any intentional precision change.
- **Puck verdict:** **pursue** as a small, latent-value increment — worth
  doing near-term as an enabler, not urgent, effort **medium** ("mostly a
  hit-buffer schema, one new kernel, and re-plumbing the indirect chain to a
  two-hop dispatch... a strict prerequisite/enabler for any later shade
  divergence," per review-04). It buys little by itself until the shade stage
  diverges, but it is the cheapest way to keep that door open.
- **See also:** [tape-pruning-and-inclusion.md](tape-pruning-and-inclusion.md), [materials-and-primitives.md](materials-and-primitives.md), [verdict-index.md](verdict-index.md).

### Async-compute overlap
- **Source:** *Advanced API Performance: Async Compute and Overlap*, NVIDIA
  Developer Blog. https://developer.nvidia.com/blog/advanced-api-performance-async-compute-and-overlap/
  ; GPUOpen, *Leveraging Asynchronous Queues for Concurrent Execution*.
  https://gpuopen.com/learn/concurrent-execution-asynchronous-queues/
- **Digest:** Overlap independent compute workloads on an async queue against
  graphics/copy work to hide latency; reported up to ~13% frame-time
  reduction in favorable cases, at the cost of added pipeline latency and
  cross-queue barrier discipline.
- **Determinism / cross-backend fit:** Deterministic if scoped — overlapping
  non-data-dependent dispatches changes only *when* work runs, not the
  per-frame numeric result. Cross-queue races are a correctness bug
  regardless of determinism status and must be barrier-disciplined either way.
- **Puck verdict:** surveyed, not deep-reviewed (sweep-6 §6).
- **See also:** [verdict-index.md](verdict-index.md).

### Variable Rate Shading (VRS)
- **Source:** Microsoft, *Variable Rate Shading*, DirectX-Specs.
  https://microsoft.github.io/DirectX-Specs/d3d/VariableRateShading.html ;
  NVIDIA, *Advanced API Performance: Variable Rate Shading*.
  https://developer.nvidia.com/blog/advanced-api-performance-variable-rate-shading/
- **Digest:** Hardware coarse-pixel shading tied to the rasterizer stage,
  driven by per-primitive/tile/region shading-rate images. Does **not**
  extend to compute dispatch — there is no compute-shader equivalent hardware
  path. The transferable idea is the pattern, not the hardware feature: write
  a rate map, have compute passes read it and skip/broadcast work at coarse
  granularity — manual variable-rate tiling, which Puck's tile-bitmask
  prepass already approximates in spirit.
- **Determinism / cross-backend fit:** The manual rate-map pattern is a static
  function of a precomputed map, so it is deterministic; the hardware VRS
  feature itself is moot since it doesn't apply to Puck's compute-only march.
- **Puck verdict:** surveyed, not deep-reviewed (sweep-6 §2) — not directly
  applicable, but the manual rate-map idea is worth keeping in mind as a
  generalization of the existing tile prepass.
- **See also:** [hierarchical-and-instance-acceleration.md](hierarchical-and-instance-acceleration.md), [lod-and-bounds.md](lod-and-bounds.md), [verdict-index.md](verdict-index.md).

### Fixed foveated rendering
- **Source:** Arm, *Foveated Rendering: Current and Future Technologies for
  Virtual Reality*, whitepaper.
  https://developer.arm.com/-/media/developer/Graphics%20and%20Multimedia/White%20Papers/Foveated%20Rendering%20Whitepaper.pdf
- **Digest:** Gaze-contingent variable-resolution rendering, reducing shading
  rate outward from a gaze point by eccentricity. The paper covers both
  fixed-map and eye-tracked variants.
- **Determinism / cross-backend fit:** A **fixed** foveation map is a
  deterministic, replayable rate schedule — the same shape as the manual VRS
  rate-map pattern above. Eye-tracked gaze is a nondeterministic *external
  input*, and would need to be captured into the per-tick `CommandSnapshot`
  like any other input to stay replay-safe; it is not disqualified, just not
  free.
- **Puck verdict:** surveyed, not deep-reviewed (sweep-6 §2) — a fixed map is
  the determinism-safe variant if this is ever pursued; gaze-tracked
  foveation is a separate, input-capture-gated question.
- **See also:** [verdict-index.md](verdict-index.md).
