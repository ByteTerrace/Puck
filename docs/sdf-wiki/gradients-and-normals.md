# Gradients and normals

Puck's default hit normal is evaluated analytically with the field. The SDF VM
propagates a forward-mode gradient through supported operations and shapes,
then normalizes the result at the accepted hit point.

## Analytic path

Analytic gradients avoid the additional field evaluations required by finite
differences and keep normal behavior tied to the same instruction semantics as
distance evaluation. New operations must define how they transform or combine
the gradient. Discontinuities, material ties, and smooth blends require an
explicit winner rule rather than an incidental backend result.

The gradient implementation is a C#↔HLSL contract. Update the instruction
analysis and every shader interpreter variant together.

## Comparison path

The renderer retains a four-tap tetrahedral finite-difference path for
comparison and diagnosis. It is useful when validating a new analytic rule or
isolating a shading regression, but it is not the production default.

Choose the probe epsilon in world units and account for `stepScale`. Too small
an epsilon amplifies floating-point noise; too large an epsilon rounds off
small features.

## Failure modes

- A domain transform changes the gradient basis as well as the sample point.
- Non-uniform scaling requires the inverse-transpose relationship and the same
  conservative distance scaling used by the marcher.
- Repetition and fold boundaries are intentionally non-differentiable. Do not
  infer a smooth normal across them.
- Smooth field blends and material blends are related but distinct operations.
- Sampled regions need a gradient rule consistent with their trilinear field.

Validate normal changes with the analytic/four-tap comparison view, hard and
smooth blends, transformed primitives, and cross-backend captures.
