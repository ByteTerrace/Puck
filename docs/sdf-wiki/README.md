# SDF technique reference

This reference explains signed-distance-field rendering techniques in the
context of Puck's interpreted SDF VM. The pages summarize the algorithm,
correctness requirements, determinism implications, and current applicability.
Use [SDF rendering technique priorities](../sdf-sota-survey.md) for current
investment priorities and [the SDF backlog](../sdf-backlog.md) for open work.

## Performance and acceleration

- [Marching acceleration](marching-acceleration.md): relaxation, conservative
  step bounds, curvature stepping, and non-linear tracing.
- [Hierarchy and instance acceleration](hierarchical-and-instance-acceleration.md):
  cone prepasses, uniform grids, ray pyramids, BVHs, and work graphs.
- [Tape pruning and inclusion](tape-pruning-and-inclusion.md): per-region
  specialization, Lipschitz pruning, interval evaluation, and synchronized
  tracing.
- [LOD and bounds](lod-and-bounds.md): proxy nodes, segment bounds, segment
  tracing, and distance-dependent fidelity.
- [March-loop scheduling](march-loop-scheduling.md): wavefront scheduling,
  compaction, persistent threads, and march/shade separation.

## Correctness

- [Lipschitz and field correctness](lipschitz-and-field-correctness.md):
  distance estimates, operation norms, repetition boundaries, and
  bound-preserving procedural detail.
- [Gradients and normals](gradients-and-normals.md): analytic forward-mode
  gradients and finite-difference comparison paths.
- [Antialiasing and filtering](antialiasing-and-filtering.md): coverage,
  footprint termination, and ray-differential texture filtering.

## Shading and content

- [Shading, AO, and shadows](shading-ao-shadows.md): ambient occlusion,
  penumbra refinement, curvature shading, and light culling.
- [Materials and primitives](materials-and-primitives.md): smooth composition,
  material ownership, lifted primitives, and text fields.
- [Text and glyphs](text-and-glyphs.md): marchable glyph geometry, decal text,
  MTSDF field channels, and deterministic enrichment.

## Decision support

- [Technique index](verdict-index.md): compact current applicability by
  technique family.
- [Rejected and conditional techniques](negative-results-and-rejections.md):
  non-goals and the concrete triggers that justify reconsideration.

## Standing constraints

- The C# and HLSL interpreters form one packed contract.
- A skipped segment or instance must be conservative; exact-cull paths must
  return the accumulated field bit-for-bit when the candidate is irrelevant.
- `map()` applies the program's `stepScale`. Consumers comparing the result
  with world-space lengths must account for that scale as documented by the
  shader contract.
- Simulation state uses deterministic fixed-point data. Presentation shaders
  may use floating point but must satisfy the configured cross-backend parity
  gates.
- The analytic instruction stream and authored carve list are authoritative.
  `SampledRegion` bricks are bounded, invalidatable render caches, never the
  simulation or persistence representation.
- Shader features that should compile away use a static branch. Do not assume
  multiplying a contribution by zero removes its instructions in both DXC
  targets.

Paper publication years and source links are retained because they identify
the cited work. Implementation dates, commit hashes, rollout narratives, and
review-session provenance do not belong in these living references.
