# Marching acceleration

The world marcher combines conservative sphere-tracing steps with an
auto-relaxed production path. The strict path remains the reference for
correctness and parity diagnosis.

## Auto-relaxation

Relaxation uses recent progress to enlarge steps when the field behaves
smoothly. It must fall back when the evidence is weak, near a surface, or near
a discontinuous fold. Relaxation never changes the accepted hit contract.

The implementation records enough local state to detect overshoot and retreat.
Do not treat a single successful frame as proof: test grazing rays, thin
features, high-curvature surfaces, repeated domains, and displaced fields.

## Conservative bounds

Domain folds require a bound on the next boundary crossing. Instance and
segment bounds can also provide a safe distance to potentially relevant work.
The step is the minimum of all applicable conservative limits after converting
them to a common distance scale.

## Candidate techniques

Curvature-guided stepping and non-linear root refinement may reduce work near
smooth surfaces, but they require stable derivative information and a measured
benefit over the current relaxed path. They must not weaken the strict
reference result.

When changing the march loop, report image parity, miss/overshoot diagnostics,
step-count distribution, and GPU time. Average step count alone can hide rare
holes and expensive fallback behavior.
