# Lipschitz and field correctness

Sphere tracing is safe only when each step is bounded by the field's rate of
change. Puck computes a program-wide conservative `stepScale` from the authored
instruction stream and applies it in `map()`, and in every other marcher that
walks the same stream.

## Contract

For a field with Lipschitz bound `L`, the safe scale is at most `1 / L`.
Rigid transforms and exact primitives retain factor 1. Scaling, warps,
displacement, and some composition operators require an additional bound.
Host-side analysis and HLSL evaluation must agree on every instruction's
effect.

Consumers must distinguish scaled field distance from world-space length.
Hit thresholds, AO probes, shadow steps, and bound comparisons must apply the
conversion documented by the shader contract.

## Composition bounds

A blend's bound is a property of the composition, not of the operands' authored
chains, and the two differ whenever a blend can exceed both of its inputs.

Every min, max, and lerp arm carries `max(La, Lb)`. That is idempotent and
order-free, so folding it once per chain and taking a maximum computes it.

The chamfer family does not fit that shape. Its bevel arm is `(a ± b ± r)·√½`,
whose gradient is `(∇a ± ∇b)/√2`, so the composed bound is

    L = max(La, Lb, (La + Lb)/√2)

which grows with each chamfer composition and has fixed point `1 + √2`. A
factor applied once per chain or once per program therefore understates by up
to `(1 + √2)/√2 = 1.70711×`, which is enough to march through thin geometry:
three chamfer-unioned slabs carved to a plate a few hundredths of a unit thick
are a hole at the one-`√2` scale and a hit at `1/1.70711`.

Two properties follow from the accumulator rule and must be preserved by any
implementation of the recurrence:

- the running accumulator seeds at a constant, whose bound is zero, so the
  first chamfer composition is the identity and two chamfers reach exactly
  `√2`; growth begins at the third; and
- one accumulator crosses every point reset, so splitting the stream into
  segments is not a bound on how many chamfer compositions can nest. A scope
  pop composing with a chamfer is the same composition as a chamfer shape
  blend, not a separate program-wide factor.

## Query seams

A field evaluator behind a hierarchical world position evaluates the whole
position. Reading only the cell-local offset aliases the field with the cell
period and answers for the wrong copy, and a position constructor that
re-anchors past half a cell reaches that state without any caller asking for
it. A field-only seam that cannot rebase refuses the sample. An authoritative
obstruction verb resolves the undecidable point toward occupied; it may not
answer clear.

A CPU marcher over the same instruction stream is bound by the same step scale
as the shader. Restricting the interpreted subset to rigid ops does not make the
raw field value a safe advance: the chamfer family lives in the blend tail and
an eccentric ellipsoid in the shape body, so a program every one of whose ops is
an isometry can still overestimate distance and be tunnelled by a raw step.

For a swept sphere, scale the field before subtracting the radius:
`safeAdvance = field * stepScale - radius`. Scaling the clearance instead also
scales down the radius and can overstate the empty gap. `Overlap` uses the same
scaled field as a conservative separation test. If the safe advance falls below
the smallest reliable fixed-point step before the raw hit threshold converges,
the cast reports a bounded obstruction; replacing it with a larger step would
discard the Lipschitz proof and can tunnel the body through a thin surface.

The scale shortens every advance, so a fixed iteration budget shortens the
distance a march covers before it gives up, in proportion. The budget must
derive from the scale, or adding a chamfer silently converts resolving casts
into non-convergent ones.

A march that exhausts its iteration budget has proved nothing. Each verb must
resolve it toward the answer its own consumer can survive being wrong about, and
that direction is a property of what the verb's true half ASSERTS, not of the
provider:

- an obstruction verb — cast, sweep, visibility — asserts "something is there",
  so it folds exhaustion to a hit marked bounded rather than exact. Folding it
  into "clear" is a false negative that reaches authoritative simulation:
  contact resolution reads it as "no contact" and visibility reads it as a line
  through solid geometry.
- a surface verb — ground height — asserts "the terrain is at this Y". It
  returns a coordinate with no confidence channel to qualify, and a caller that
  grounds a body on a fabricated Y moves it somewhere the world does not have.
  It folds exhaustion to "not found", the same answer an empty column gives.

A grazing probe beside a vertical wall exhausts on the wall's own clearance and
is the discriminating case: it must read as blocked for a cast and as no-ground
for a height query, from the same march.

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

`NoiseDisplace` is the shipped instance: an integer-only PCG3D hash per
lattice corner, output host-normalized to `[-1, 1]`, a quintic-blend gradient
bound (`frequency·(15/4)·√3` per normalized octave sum) folded by
`AnalyzeLipschitz` into the step clamp, and cross-backend agreement inside the
relaxed parity envelope (isolated silhouette winner flips only). The
sine-product `Displace` remains the hash-free periodic sibling.

Visual plausibility is not evidence of a safe distance estimate. Validate new
field operations with grazing rays, fold boundaries, thin geometry, and a
strict/reference march comparison.
