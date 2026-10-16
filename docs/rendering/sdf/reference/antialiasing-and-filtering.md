# Antialiasing and filtering

Puck treats geometric coverage and sampled-content filtering as separate
problems. A rasterized surface can still alias a thin silhouette, while a
correctly covered screen surface can still shimmer when its texture is
minified.

## Geometric coverage

The mesh pass rasterizes one sample a pixel, so a pixel is wholly covered or
wholly sky: the lit image carries no partial silhouette coverage. A view that
reconstructs over time (`world.temporal`) jitters its sample each frame and
resolves the samples over its history, which antialiases silhouettes and thin
features ([temporal reconstruction](../handbook/frame-rendering.md#temporal-reconstruction)).
Features thinner than the bake's final cell (no finer than 1.5 cm) do not reach
the mesh at all.

## Surface filtering

Diegetic screens and glyph decals sample textures after the geometry hit. Use
the surface parameterization to estimate texture footprint and choose an
appropriate filter or mip level.

The current screen path uses explicit sampling contracts shared by Vulkan and
Direct3D 12. Any derivative-based extension must define equivalent behavior for
compute shaders and must not rely on implicit pixel-shader derivatives.

## Practical guidance

- Test temporal stability while the camera and screen surface move.
- Prefer deterministic, precomputed mip chains for authored content.
- Do not use stochastic jitter unless its seed, sequence, and accumulation
  policy are part of the documented render contract. A temporal view's
  per-frame sample offset and history weighting are such a contract; see
  [temporal reconstruction](../handbook/frame-rendering.md#temporal-reconstruction).

Ray-differential screen filtering remains an open quality feature. Nothing
tracks it and nothing is sequenced to build it.
