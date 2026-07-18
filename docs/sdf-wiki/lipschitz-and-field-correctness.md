# Lipschitz and field correctness

Sphere tracing is safe only when each step is bounded by the field's rate of
change. Puck computes a program-wide conservative `stepScale` from the authored
instruction stream and applies it in `map()`.

## Contract

For a field with Lipschitz bound `L`, the safe scale is at most `1 / L`.
Rigid transforms and exact primitives retain factor 1. Scaling, warps,
displacement, and some composition operators require an additional bound.
Host-side analysis and HLSL evaluation must agree on every instruction's
effect.

Consumers must distinguish scaled field distance from world-space length.
Hit thresholds, AO probes, shadow steps, and bound comparisons must apply the
conversion documented by the shader contract.

## Discontinuous folds

Repeat, polar repetition, wallpaper folds, and cell jitter can cross a domain
boundary between samples. A local Lipschitz factor alone cannot prove that a
raw step is safe across the discontinuity. The marcher therefore uses
fold-safe bounds where required.

Plain repetition is exact only when the prototype fits within its centered
cell. Cell jitter also requires conservative spacing; containment does not
guarantee that the folded cell contains the nearest displaced copy.

## Procedural detail

Bound-preserving noise must provide all of the following:

- a deterministic integer hash or sequence;
- a known output range;
- a conservative derivative bound;
- an explicit effect on `AnalyzeLipschitz`; and
- matching results across shader targets within the configured parity policy.

Visual plausibility is not evidence of a safe distance estimate. Validate new
field operations with grazing rays, fold boundaries, thin geometry, and a
strict/reference march comparison.
