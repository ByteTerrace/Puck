# SDF technical reference

This section contains focused technical references for Puck's interpreted SDF
renderer: acceleration techniques, field correctness, shading, materials,
text, and recorded decisions. The [handbook](../handbook/README.md) provides
the guided explanation; these pages hold the deeper contracts and evidence.

## Performance and ray marching

- [Marching acceleration](marching-acceleration.md) covers conservative steps
  and over-relaxation.
- [Hierarchical and instance acceleration](hierarchical-and-instance-acceleration.md)
  covers cone prepasses, grids, and BVH techniques.
- [Tape pruning and inclusion](tape-pruning-and-inclusion.md) covers interval
  evaluation and region specialization.
- [Level of detail and bounds](lod-and-bounds.md) covers proxy nodes and segment bounds.
- [March-loop scheduling](march-loop-scheduling.md) covers wavefront scheduling,
  compaction, and persistent threads.

## Correctness and field mathematics

- [Lipschitz and field correctness](lipschitz-and-field-correctness.md) covers
  distance bounds and operation norms.
- [Gradients and normals](gradients-and-normals.md) covers analytic and sampled
  normal paths.
- [Antialiasing and filtering](antialiasing-and-filtering.md) covers coverage,
  beam footprints, and ray-differential filtering.

## Materials, shading, and content

- [Shading, ambient occlusion, and shadows](shading-ao-shadows.md) covers lighting, ambient
  occlusion, penumbra estimation, and clustered lights.
- [Materials and primitives](materials-and-primitives.md) covers composition,
  material ownership, lifted primitives, and domain distortion.
- [Text and glyphs](text-and-glyphs.md) covers marchable glyphs, MSDF atlases,
  and layout integration.

## Decisions and limits

- [SDF technique index](technique-index.md) records applicability and status.
- [Negative results and rejections](negative-results-and-rejections.md) records
  approaches that are outside the current renderer and the evidence needed to
  reconsider them.

## Shared renderer contracts

The SDF evaluator and GPU renderer share the packed instruction layout and field
semantics. The deterministic evaluator owns exact simulation and query results;
GPU execution is a presentation path whose output is checked with backend-
appropriate parity evidence. Conservative cull bounds and the scene's
`stepScale` remain required for safe marching. The authored instruction stream
is authoritative, while sampled regions and 3D textures are invalidatable
render caches.

For shader manifests, connected pipelines, descriptor bindings, and configuration,
see the [`Puck.Shaders` README](../../../../src/Puck.Shaders/README.md).
