# Gradients and normals

Puck's default hit normal is evaluated analytically with the field. The SDF VM
finds the shapes that contribute to the accepted hit's gradient, evaluates
their derivatives through the same transform and composition rules, then
normalizes the result.

## Analytic normal path

Analytic gradients avoid the additional field evaluations required by finite
differences and keep normal behavior tied to the same instruction semantics as
distance evaluation. New operations must define how they transform or combine
the gradient. Discontinuities, material ties, and smooth blends require an
explicit winner rule rather than an incidental backend result.

The gradient implementation is a C#↔HLSL contract. Update the instruction
analysis and every shader interpreter variant together.

`mapGradCore` first follows the field's scalar decisions and records each
contributing shape's blend weight. A hard minimum or maximum keeps the deciding
side. A smooth blend outside its radius makes the same choice; inside the
smooth band it keeps every shape with a nonzero weight. Subtraction retains
the candidate's sign change, and later blends can remove an earlier
contributor. The selected derivatives then replay through the existing
interpreter. The selection uses the dual blend's own branch and weight rules,
so equal-distance ties have the same meaning in both walks.

The bounded contributor set falls back to the full dual walk when it cannot
represent a hit's contributors. A later winner can discard that overflow,
including across a field scope, and restore selected-gradient evaluation.
Common primitives have analytic gradients;
an exotic primitive without one keeps its local finite-difference derivative.
`gpu.shapes.gradients` counts analytic primitive derivatives. A primitive's
finite-difference fallback adds no analytic gradient count.
`gpu.shapes.evaluated` counts scalar selection,
one evaluation per analytic derivative and each finite-difference distance
tap. Both attempts count when contributor overflow takes the full walk.
Both counters belong to the pass that performs the work. Device laws
compare the selected gradients and counted analytic contributors with a compiled full
walk; the renderer has no environment switch for choosing that reference.
The selected scalar result is the decision walk's result bit for bit. The
weighted gradient can reassociate floating-point operations, and the device
law compares it with the full gradient at a tolerance of 0.003. Its Nexus
hit samples come from the presenter's composed program and posed transform
table at the dense workload's camera and 1440×810 extent; both variants use
those same samples. Synthetic blend fixtures require multiple simultaneous
contributors; a Nexus hit whose full walk selects one shape requires one
analytic evaluation when that shape has an analytic derivative.

## Finite-difference comparison path

The renderer retains a four-tap tetrahedral finite-difference path for
comparison and diagnosis. Authored curvature shading also uses four neighboring
samples to estimate the field's second derivative, together with a center
distance. Those same samples supply the normal, so this path performs no
analytic primitive derivatives. A curvature-enabled surface can therefore
report zero `gpu.shapes.gradients` while its scalar probes contribute to
`gpu.shapes.evaluated`. The courtyard inherits this curvature setting from
its moth world. Its sample loop shares one interpreter call site; see the
[renderer README](../../../../src/Puck.SdfVm/README.md) for center-distance reuse.

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
