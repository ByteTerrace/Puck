# SDF Rendering Techniques Wiki

A comprehensive reference for signed-distance-field rendering techniques
surveyed for the Puck SDF VM renderer — the data-driven `uint[]` interpreter
compiled to SPIR-V + DXIL, marched per-pixel under a deterministic,
cross-backend-parity contract.

This wiki is the **reference companion** to
[../sdf-sota-survey.md](../sdf-sota-survey.md). The survey is the *ranked
decision shortlist* — what to build next and why. This wiki is the *complete
catalog* — every technique the research touched gets an entry with a working
source link, a determinism note, and (where a deep review ruled) a Puck verdict.
When the two disagree on a ranking, the survey is authoritative on priority; the
wiki is authoritative on coverage.

## Method

- **Date:** 2026-07-08.
- **Corpus:** 7 web literature sweeps (sphere-tracing acceleration; acceleration
  structures & baked representations; the Inigo Quilez / Shadertoy corpus;
  production-engine talks; shading/AO/shadows; temporal & frame-level
  performance; robustness / Lipschitz / authoring theory) followed by 10
  adversarial Opus deep reviews that read the primary papers and mapped each
  technique onto Puck's actual VM, ISA, and parity discipline.
- **Verdicts** (`adopt-now`, `adopt-when-GPU-bound`, `gated-on-X`, `defer`,
  `reject`, each with an S/M/L/XL effort rating) come from the deep reviews.
  Entries only touched by a sweep are marked **surveyed, not deep-reviewed**.
  Provenance identifiers of the form `review-01` … `review-10` refer to those
  ten deep-review documents in the research corpus (not files in this repo);
  [verdict-index.md](verdict-index.md) maps each identifier to its cluster.
- **Empirical status:** a 2026-07-08 in-demo correctness hunt tested three
  suspects the corpus covers — the Xor unmaskable-exemption (confirmed exempt),
  the Repeat/CellJitter neighbor-cell discontinuity (confirmed real, bounded to
  the authoring constraint), and the ground-notch/MaxSteps hypothesis
  (refuted). The affected entries carry an "Empirical status (Puck)" line;
  [verdict-index.md](verdict-index.md#empirical-status-in-puck) holds the full
  record. All other verdicts are literature-vs-architecture rulings, untested
  in-engine.
- **Engine facts** cited here (the beam prepass, `stepScale = 1/L`,
  `AnalyzeLipschitz`, the scoped `PushField`/`PopField` accumulator, the
  exact-cull contract, relaxed-vs-strict parity) are taken from the reviews,
  which are authoritative on them; nothing here invents engine detail beyond the
  corpus.

## Pages

### Performance / acceleration
- [marching-acceleration.md](marching-acceleration.md) — step relaxation past
  Keinert (Bálint 2018, Bán 2023 auto-relaxed), aggressive 1.5× marching,
  curvature stepping, non-linear ODE tracing, quadric tracing.
- [hierarchical-and-instance-acceleration.md](hierarchical-and-instance-acceleration.md)
  — hierarchical cone pre-pass (The Gunk four-bound teleport), the ray pyramid
  (DIST), instance acceleration (TLAS/BLAS vs uniform grid / spatial hash),
  GPU work graphs.
- [tape-pruning-and-inclusion.md](tape-pruning-and-inclusion.md) — per-region
  program specialization (Keeter MPR/Fidget, Barbier Lipschitz Pruning, Zanni
  Synchronized Tracing) and interval / inclusion-function marching (Aydinlilar
  forward inclusion, Knoll, Fryazinov RAA).
- [lod-and-bounds.md](lod-and-bounds.md) — in-tree LOD & proxy nodes
  (Hubert-Brierre), Sphere Carving bounding volumes, per-segment early-out,
  segment tracing, and the far-field-proxy patterns from production engines.
- [march-loop-scheduling.md](march-loop-scheduling.md) — wavefront /
  compaction / persistent-threads restructuring, march/shade split, and the
  frame-level schedulers (async compute, VRS, foveation).

### Correctness / theory
- [lipschitz-and-field-correctness.md](lipschitz-and-field-correctness.md) —
  exactness taxonomies, operations-on-estimates error metrics, generalized
  Lipschitz tracing, domain-repetition correctness, KIFS fudge factors, and
  bound-preserving fBm detail.
- [gradients-and-normals.md](gradients-and-normals.md) — the forward-mode
  analytic-gradient dual walk replacing finite-difference normals.

### Shading / visual quality
- [shading-ao-shadows.md](shading-ao-shadows.md) — the ambient-occlusion family,
  soft-shadow penumbra refinement, curvature/NPR shading & outlines, interior
  thickness SSS, triplanar, light culling, volumetrics.
- [antialiasing-and-filtering.md](antialiasing-and-filtering.md) — analytic
  coverage AA and ray-differential texture filtering.
- [materials-and-primitives.md](materials-and-primitives.md) — material blend
  factor at seams, the smin taxonomy, the hg_sdf blend/domain op library, the
  2D-primitive catalog gaps, interior distance, and MSDF text.
- [text-and-glyphs.md](text-and-glyphs.md) — glyphs as world-geometry distance
  fields (march / CSG / engrave): the MTSDF-alpha field-source finding (C1/C2/C3),
  the coverage-family reject matrix (incl. Slug's 2026-03-17 public-domain flip),
  analytic-dual world-space AA, the bake recipe, and the text-enrichment arc
  (markup grammar, effect catalog, the determinism fix, the `sdfMsdfGlyph`
  prototype, and the delight doctrine).

### Decisions
- [verdict-index.md](verdict-index.md) — every deep-reviewed technique in one
  table: verdict, effort, gating dependency, review provenance.
- [negative-results-and-rejections.md](negative-results-and-rejections.md) —
  what was considered and rejected, and *why* (binary-search raycasting, the
  baked-volume family, the temporal-history family, GPU work graphs, wave-vote
  early-out, and more).

## Standing constraints referenced throughout

- **Determinism is a feature.** No wall-clock, RNG, or temporal history in the
  render path; both backends must agree bit-for-bit under
  `PUCK_PARITY_STRICT=1`, or within the relaxed `mean ≤ 0.35` gate by default.
- **`map()` returns a `stepScale = 1/L`-scaled distance.** Any consumer
  comparing it to a world-space length must divide `stepScale` back out first —
  the recurring foot-gun the reviews flag.
- **The exact-cull contract.** A culled/skipped segment must be bit-identical to
  an evaluated far segment; far-exact smooth-blend endpoints are what make it
  hold.
- **No baked volumes.** Puck is a pure-analytic program interpreter; the
  far-field answer stays analytic (proxy nodes, not voxel bricks).
- **DXIL does not dead-code-eliminate an arithmetic ×0 gate the way SPIR-V
  does** (found 2026-07-08, commit `44bfd88`, curvature/ink-outline shading).
  Multiplying a shading term's contribution by a compile-time-zero gain (the
  existing CRT-knob pattern) let SPIR-V fold the whole disabled chain away
  (~+20 bytes) but left DXC's DXIL backend keeping the dead arithmetic in full
  (~+13 KB) — including a divide and an extra `map()` tap that were supposed
  to cost nothing when off. The zero-cost-off seam for a chain DXIL won't
  fold is a compile-time `static const bool` dead-branch guard
  (`if (FeatureEnabled) { ... }`), which DXC strips on both backends; use the
  arithmetic-×0 gain pattern only for chains cheap enough that DXIL keeping
  them is a non-issue.
