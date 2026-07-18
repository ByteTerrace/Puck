# Antialiasing and filtering

Puck treats geometric coverage and sampled-content filtering as separate
problems. A ray marcher can terminate at the correct surface and still alias a
thin silhouette, while a correctly covered screen surface can still shimmer
when its texture is minified.

## Geometric coverage

The world renderer uses a pixel-footprint-aware termination rule. The accepted
distance shrinks with depth and projection so distant geometry does not require
the same absolute threshold as nearby geometry. The rule must remain
conservative with the program's `stepScale`; a threshold expressed in world
units cannot be compared directly with a scaled field value.

Coverage changes belong in the common march contract, not in a backend-specific
shader. Validate them with silhouette-heavy scenes, thin features, grazing
angles, and both GPU backends.

## Surface filtering

Diegetic screens and glyph decals sample textures after the geometry hit. Use
the surface parameterization to estimate texture footprint and choose an
appropriate filter or mip level. This is preferable to increasing march cost:
extra field samples cannot recover information lost by texture minification.

The current screen path uses explicit sampling contracts shared by Vulkan and
Direct3D 12. Any derivative-based extension must define equivalent behavior for
compute shaders and must not rely on implicit pixel-shader derivatives.

## Guidance

- Keep geometric hit tolerance independent from texture sharpness controls.
- Test temporal stability while the camera and screen surface move.
- Preserve the strict/reference march path when changing coverage behavior.
- Prefer deterministic, precomputed mip chains for authored content.
- Do not use stochastic jitter unless its seed, sequence, and accumulation
  policy are part of the documented render contract.

Ray-differential CRT filtering remains an open quality feature; see
[the SDF backlog](../sdf-backlog.md).
