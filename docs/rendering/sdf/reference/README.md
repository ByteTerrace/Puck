# SDF technical reference

This section contains focused technical references for Puck's SDF engine: the
field the bake samples and the CPU queries walk, its correctness contracts,
shading, materials, text, and recorded decisions. The
[handbook](../handbook/README.md) provides the guided explanation; these pages
hold the deeper contracts and evidence.

## Field and bounds

- [Instance bounds and ownership](instance-bounds.md) covers what an instance
  bound must enclose and how the directory partitions segments.
- [Level of detail and bounds](lod-and-bounds.md) covers segment bounds, copy
  expansion and sampled carve regions.

## Correctness and field mathematics

- [Lipschitz and field correctness](lipschitz-and-field-correctness.md) covers
  distance bounds and operation norms.
- [Gradients and normals](gradients-and-normals.md) covers how the bake
  measures a gradient and where a view's normal comes from.
- [Antialiasing and filtering](antialiasing-and-filtering.md) covers coverage
  and sampled-content filtering.

## Materials, shading, and content

- [Shading, ambient occlusion, and shadows](shading-ao-shadows.md) covers lighting, ambient
  occlusion and the shadow cascades.
- [Materials and primitives](materials-and-primitives.md) covers composition,
  material ownership, lifted primitives, and domain distortion.
- [Text and glyphs](text-and-glyphs.md) covers glyph fields, MSDF atlases,
  and layout integration.

## Decisions and limits

- [SDF technique index](technique-index.md) records applicability and status.
- [Negative results and rejections](negative-results-and-rejections.md) records
  approaches that are outside the current engine and the evidence needed to
  reconsider them.

## Shared engine contracts

The SDF evaluator and the GPU bake share the packed instruction layout and field
semantics. The deterministic evaluator owns exact simulation and query results;
GPU execution is a presentation path whose output is checked with backend-
appropriate parity evidence. Conservative bounds and the scene's `stepScale`
remain required for safe marching on the CPU and for the bake's bound skips.
The authored instruction stream is authoritative, while baked meshes, sampled
regions and 3D textures are invalidatable render caches.

For shader manifests, connected pipelines, descriptor bindings, and configuration,
see the [`Puck.Shaders` README](../../../../src/Puck.Shaders/README.md).
