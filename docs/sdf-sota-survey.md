# SDF rendering technique priorities

This page maps established SDF rendering techniques to Puck's current
architecture. It is a decision aid, not a chronology. Detailed theory and
citations live in [the SDF technique reference](sdf-wiki/README.md); open work
lives in [the SDF backlog](sdf-backlog.md).

## Evaluation constraints

Any adopted technique must preserve these properties:

- one data-authored SDF instruction stream on both GPU backends;
- conservative marching and culling with no surface holes;
- deterministic simulation state and evidence-calibrated render parity;
- bounded resource use declared at engine construction;
- no demo-only policy in `Puck.SdfVm`;
- measurable benefit on a representative Puck workload.

## Current priorities

| Priority | Technique | Why it fits | Required proof |
|---|---|---|---|
| High | Within-instance segment bounds | Complements the shipped whole-instance grid for large placed creations | identical pixels with segment pruning on/off; lower views or beam time on multi-segment content |
| High | Register-pressure reduction through curated kernel variants | Targets the views interpreter's limiting resource without changing program semantics | program-driven variant selection, unreachable stripped cases, cross-backend parity, representative GPU counters |
| High | Fewer shading-epilogue field evaluations | Views cost includes shadow and AO re-evaluation | paired feature A/B with a defined visual envelope and no culling unsoundness |
| Medium | Per-region Lipschitz refinement | Can recover step length in locally rigid regions | conservative composition across warps, folds, scopes, and instances |
| Medium | Ray-differential texture filtering | Improves minified diegetic screens without changing geometry | deterministic explicit-LOD reconstruction and backend parity |
| Medium | Bound-preserving procedural displacement | Expands authored detail while retaining a computable step clamp | integer hash, analytic derivative bound, solidity and parity coverage |
| Conditional | Wavefront or persistent-thread scheduling | May help highly divergent long marches | GPU-specific evidence that queue and compaction overhead is below the recovered occupancy |
| Conditional | Interval or inclusion marching | Strong guarantees but substantial per-step state | a narrow workload where reduced steps outweigh interval arithmetic and register cost |

## Shipped architectural choices

Puck already uses the following techniques as its default path:

- program-wide Lipschitz analysis and a baked `stepScale`;
- fold-safe step bounds at domain discontinuities;
- over-relaxed sphere tracing with a strict reference path;
- a mask-first uniform-grid instance cull;
- a cone-march beam prepass and indirect views dispatch;
- forward-mode analytic normals with a four-tap comparison path;
- per-pixel penumbra-aware shadow gathering;
- three-tap normal-ladder ambient occlusion;
- integer-derived per-view render scale;
- sampled carve bricks as a render-only cache for settled dense clusters;
- core/full views-kernel specialization selected from program content.

Treat these as current implementation facts, not reasons to reject change.
Replace one only with correctness evidence and representative paired
measurements.

## Techniques that need a concrete trigger

The following families are not default priorities because they change the
representation, add persistent history, or solve a cost Puck is not currently
paying:

- global voxel, clipmap, sparse-voxel, and distance-volume representations;
- temporal reprojection or history-dependent shading;
- screen-space-only geometry substitutes;
- a per-frame BVH over already grid-friendly dynamic content;
- subgroup-size-dependent algorithms without an explicit cross-vendor policy;
- mesh extraction as the primary renderer.

They may still be appropriate for a distinct content source or tool. Reopen a
family when a named workload cannot be handled by the analytic VM plus its
bounded caches, and record the representation boundary explicitly.

## How to evaluate a proposal

1. Name the workload and the pass that dominates it.
2. State whether the technique changes the analytic program, adds a cache, or
   only changes scheduling.
3. Prove the conservative bound or fallback before measuring speed.
4. Implement the smallest switchable comparison path.
5. Compare in one process with identical camera, content, render scale, and
   shading state.
6. Run the verification tier selected by the `verifying-puck-changes` skill.
7. Put an unresolved implementation item in `sdf-backlog.md`; do not preserve
   the implementation diary in living documentation.
