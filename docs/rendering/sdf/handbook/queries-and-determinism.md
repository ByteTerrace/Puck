# Queries and determinism

Simulation code asks an SDF world questions such as "what's beneath my feet"
and "can I see that" through one query interface. Puck supplies two
implementations with different confidence levels: the deterministic evaluator
for exact simulation and the bounded path for conservative answers. The
determinism contract separates those results from presentation checks, and
gravity remains a one-line field derivation owned by the caller. The
[prototype baker](bricks-and-baking.md#prototype-bakes) reads the same evaluator.

## Why a world needs a query interface

A world renderer's job is to turn a program into pixels. A simulation's job
is different: it needs to ask the world questions no pixel answers—is
there ground under this point, is that line of sight blocked, did this ray
hit anything. Reaching into the renderer for these answers would mean
simulation logic depends on GPU state, on float math that is allowed to
differ by a few bits between backends, and on whatever the render happens
to have resident that frame. None of that is acceptable for code that has to
produce the *same* answer on every machine, every time.

`IWorldQuery` is the seam that keeps those worlds apart. It is declared in
`Puck.Maths`, so a provider and a consumer can sit in sibling libraries that
never reference each other, and it is fully
fixed-point (`FixedQ4816`/`FixedVector3`/`FixedPosition`) end to end, and every
method is synchronous—both implementations that exist today are cheap
enough per call that no async plumbing is warranted.

## The five verbs

```csharp
public interface IWorldQuery {
    QueryCapabilities Capabilities { get; }

    bool Raycast(FixedPosition origin, FixedVector3 dir, FixedQ4816 maxDist, out RayHit hit);
    bool SphereCast(FixedPosition origin, FixedVector3 dir, FixedQ4816 radius, FixedQ4816 maxDist, out RayHit hit);
    bool Overlap(FixedPosition center, FixedQ4816 radius);
    bool TryGroundHeight(FixedPosition position, FixedQ4816 probeUp, FixedQ4816 probeDown, out FixedQ4816 groundY);
    bool LineOfSight(FixedPosition from, FixedPosition to);
}
```

- **`Raycast`**—the nearest hit along a ray, out to a max distance.
- **`SphereCast`**—the same question for a swept sphere instead of an
  infinitely thin ray (a character capsule probe, not just a hitscan).
- **`Overlap`**—does a sphere at this point intersect anything blocked?
  A placement/spawn/selection check, not a cast—it answers "is this spot
  free" without needing a direction.
- **`TryGroundHeight`**—the ground level directly above or below a point,
  searched within a bounded probe window. This is what a walking character
  snaps its feet to every tick.
- **`LineOfSight`**—is a straight line between two points unobstructed?
  The building block for "can this unit see that unit."

Every direction argument is normalized internally, so a caller never has to
remember to do it. `Capabilities`—a small struct of booleans
(`HasHeightfield`/`HasBlocked`/`HasOccupancy`)—is meant to be checked once
at startup, not per call: a provider that lacks a layer degrades gracefully
(a raycast without an occupancy grid falls back to the flat heightfield)
rather than throwing per query. A layer counts as present only when it
carries content—an allocated but entirely empty layer reports absent, so
"present" really does mean "this one can answer."

Every answer is tagged with a `WorldQueryConfidence`:

```csharp
public enum WorldQueryConfidence {
    Bounded = 0,  // a conservative baked answer, or a live march that could not complete a safe proof
    Exact = 1,    // a live evaluator whose query converged against the actual program
}
```

This is a **fidelity** signal, not a determinism one—both confidence
levels are bit-identical for the same inputs on the same provider. What
they answer is "how much should the caller trust the precision of this
particular number." An RTS unit snapping to the ground can live with
`Bounded`; a competitive hitscan probably wants `Exact`.

## Two providers, two philosophies

**`BakedWorldQuery`** wraps a `WorldQueryArtifact`—a heightfield plus a
blocked-cell bitmap, baked once, ahead of time, from float-authored
rectangles. The baking discipline is the same one the walk-grid system
uses: every rectangle edge snaps to a raw fixed-point value exactly once,
and every per-cell decision after that is pure integer arithmetic—the
float-to-fixed conversion happens at the edges of authoring, never inside
the per-tick query path. This provider is cheap, coarse by construction (a
cell's answer is only as precise as the cell), and never sub-cell-exact —
hence `Bounded`.

Coarse is not the same as sloppy, and the difference is worth being precise
about. `Bounded` means *quantized and conservatively dilated*, not
*approximate*: a cast enumerates every cell its swept volume can reach and
intersects the segment with that cell's box analytically, so "clear" means
no cell in the artifact can be reached—never that no probe happened to
land on one. Where the answer is deliberately loose it is loose in the safe
direction: a swept sphere is tested against each cell box dilated by the
radius on each axis, which contains the true rounded-rectangle sweep, so
contact can be reported slightly early at a box corner but never late.
`Overlap` uses the exact clamp-to-solid test and is the tighter of the two.
The two layers also resolve Y differently, because they carry different
information: a blocked cell is authored as a footprint with no height and
so blocks at every Y, while the heightfield blocks where the query volume's
lowest point reaches its authored ground. Both layers answer every verb,
which is why an artifact carrying only one layer still answers all five.

Two contracts sit at this provider's edges rather than inside its math. The
grid's origin is a world coordinate, so every position argument is rebased
against the world origin before it reaches a cell index—the same rebase
the evaluator applies, and for the same reason: a position's raw local
offset repeats once per hierarchy cell, so reading it would answer for
whichever copy of the grid the caller happened to be standing in. And a
radius is body-scale by contract: both radius-taking verbs walk every cell
the radius reaches, so a radius wider than `MaxRadiusCells` of the
artifact's own cells is refused by name rather than paid for quietly.
Nothing here indexes occupancy hierarchically, and the refusal is how a
consumer that genuinely needs one says so.

The bake itself has a separate allocation ceiling. The default is
`WorldQueryBaker.DefaultMaxCellCount` (4,194,304 cells), enough for a 512 by 512
world-unit square at the default quarter-unit resolution and about 32.5 MiB of
retained height/blocked storage. A larger bake must pass an explicit
`maxCellCount`; either way, the dimensions are checked before either per-cell
array is allocated. The artifact also rejects any origin, dimension, and cell
size combination whose far edge would leave signed Q48.16, so later cell-edge
arithmetic stays representable.

**`SdfFieldEvaluator`** is a second, independent interpreter of the *same*
instruction stream the GPU's `mapCore` walks—not a codegen of the shader,
a deliberate hand-written twin, the same relationship the program's own
host-side bounds/Lipschitz analysis passes already have to the shader. It
walks a live `SdfProgram` directly in `FixedQ4816`, so its answers reflect
whatever the program currently is, not a stale bake—hence `Exact`.

That exactness has a price: the evaluator is **warp-free**. Its constructor
walks the program once and throws immediately, naming the first
disqualifying instruction, if the program contains anything it cannot
interpret in fixed point—chiefly the ops whose exact math needs runtime
trigonometry it does not yet implement fixed-point (twists, bends,
log-spherical folds, cell jitter, polar repeats, and the two sinusoidal
warps, displacement and domain warp), plus the dynamic-transform op (its
per-frame pose buffer has no seam in this interface), the wallpaper fold
(isometric and therefore tractable, just not yet mirrored), and a small
number of shapes whose exact cores need runtime trig or texture sampling
(star and regular-polygon's `atan2`, the ellipse's cubic solve, and the
glyph shape's texture sample). Everything else—resets, translates,
rotations (a baked quaternion needs no runtime sin/cos), scales, repeats,
symmetry planes, elongation, onion/dilate, scoped field push/pop, and the
shape/blend core—is interpreted exactly, because every one of those
operations is either an isometry or has an exact, closed-form fixed-point
treatment. The constructor's fail-loud design is
deliberate: an evaluator that silently interpreted *part* of a program and
guessed at the rest would be worse than one that refuses outright, because
its wrong answers would look exactly like right ones.

`WorldQueryProviders.ForWorld` is the resolver a sim asks for the right
provider; a sim binds only `IWorldQuery` itself; nothing downstream needs to
know or care which provider answered.

A third reading sits between the two. `SdfDistanceGrid` keeps the live
evaluator's exact values at the corners of world-space cells, and because the
field cannot change faster than its Lipschitz bound, the nearest corner's value
less a slack is a lower bound anywhere in the cell. `SdfBandedFieldEvaluator`
answers the exact evaluator wherever that bound falls inside a band a body can
touch, and the bound everywhere else—so a sample in open air costs one corner
read instead of a walk over every solid, while a contact, a hit, or an overlap
is decided on the exact field.

One subtlety matters for sphere queries against a live program. `StepScale`
turns the field into a lower bound on Euclidean separation, so the safe sphere
advance is `field * StepScale - radius`, not `(field - radius) * StepScale`.
`Overlap` compares that same lower bound with the radius. When the lower bound
becomes too small to support another fixed-point step before the raw field has
converged, a cast returns a `Bounded` obstruction. It does not continue through
an unproven gap and later claim the sweep was clear. For the same reason,
`Overlap` reports occupied when a populated program cannot rebase an extreme
hierarchical position into Q48.16; failure to represent a sample is not proof
that a placement is clear.

## Certified bounds over a region

A sampled query answers about the points it sampled. `SdfFieldEvaluator` also
answers about a whole region, by proof. `TryDistanceBounds` takes a box and
returns a `FixedInterval` (`Puck.Maths`) that holds the field at every point of
the box. It holds both the exact field of the program's fixed-point constants
and every `TryDistance` answer there. It is a third reading of the same
compiled stream, rule for rule:
- each point step is a faithful rounding of an exact expression;
- each rule encloses that expression over the box with outward rounding;
- where the point code branches, the bounds take every branch the box reaches.

Every shape and op the point evaluator accepts has a rule. Two enclose rather
than mirror:
- A `Superellipsoid` at an exponent other than 2 raises through the interval
  power `FixedInterval.Pow`. Its ratios are clamped to [0, 1], and the largest
  axis's power is exactly one, so the sum is taken as at least one.
- A `Sweep` finds its closest parameter by a sample-and-refine search a box
  cannot follow, so its rule holds every parameter the search could pick. The
  lower bound is the distance to the curve's whole hull less the largest
  radius and the margin. The upper bound is the distance to the curve's start,
  the search's first candidate, which it leaves only for a nearer point. That
  is sound and loose: a sweep's bounds are wide, never wrong.

A shape without a rule would answer `FixedInterval.Entire`, never an
approximation, so it could only shrink the frame below.

No step of an evaluation leaves the carrier unseen. An interval whose exact
hull leaves the carrier is the unbounded `FixedInterval.Entire`, and every
interval operation given it answers it. So a bounded result proves that no step
overflowed. The evaluator uses that proof once, at construction, to find its
**frame** (`Frame`): the widest power-of-two cube about the origin over which
the whole program's bounds stay bounded.
- Inside the frame, every point step's value lies inside a bounded interval, so
  no point step can wrap.
- Outside it, `TryDistance` refuses the position rather than wrapping it into a
  wrong answer, and `TryDistanceBounds` refuses any box reaching past it.
- A program that overflows everywhere (dilated by the carrier's whole range,
  say) has a negative frame and answers nowhere.
- Typical programs reach 2⁶⁰ raws or more. A polygon's or trapezoid's squared
  distance leaves the carrier near 2³⁹ raws, so its frame is a few million
  units. A sweep's search compares squared distances too, so its frame is about
  2²² units.

Every point answer therefore lies inside its box's bounds, or both refuse. Every
instruction joins the proof, so adding an instruction to a program can only
shrink its frame.

A bounds query pays only for what its box can reach. The walk skips a hard-union
instance whose bound sphere lies, from every point of the box, at or beyond the
running interval's upper end: the box form of `TryDistance`'s own instance cull,
resting on the same sphere containment. A box near one object of many walks a
fraction of the program, and the overload taking `instructionsWalked` reports
the instructions it visited. A rotation reads each coordinate once per axis:
the exact rotation is linear in the point, so its enclosure is that matrix over
the box, widened by the four raws the point code's two rounded stages can miss
it by, and met with the step-by-step enclosure. A half turn, which the creation
emitter writes, then bounds a box as tightly as the unrotated program bounds the
box's image, where the step-by-step enclosure alone triples its width.

Two certified queries are built on it:
- **`TryCertifiedSweep`** moves a sphere along a displacement by conservative
  advancement. Each step is the certified clearance times the field's step
  scale (the inverse of its Lipschitz bound), and the step's whole segment is
  then proved clear by one bounds query over its box expanded by the radius,
  with a positive lower field bound throughout. The centre's field alone cannot
  prove a sphere clear when the field's gradient exceeds one. A sphere swept
  this way never passes through a surface, however thin the surface or however
  long the step. A step that only
  samples the field at its ends tunnels through such a surface.
  If the initial sphere cannot be proved clear, the result is `Contact` at zero
  travel with the original centre. A box whose bounds are unbounded (it reaches
  past the frame) proves nothing, so the sweep stops `Exhausted` there, never
  `Clear` and never `Contact`.
  A positive contact tolerance ends the sweep in `Contact` once the proved
  clearance is that small. Conservative advancement nears a face ever more
  slowly, so without one a body pressed against a wall spends its whole budget
  on the last sliver every tick.
- **`TryCertifiedLineOfSight`** splits a segment into boxes until each is proved
  clear, or a point of it is proved inside. Anything it cannot prove within its
  budget is `Undecided`, and so is a segment reaching past the frame.

Both take a bounds-query budget and report the queries they spent, each one
walk of the program over one box, so a caller counts its cost. A run under a
smaller budget is a prefix of the run under a larger one. A cut-short sweep keeps
the ground it proved, and a cut-short line of sight is `Undecided`. A line of
sight also has a ceiling whatever its budget,
`CertifiedLineOfSightMaximumBoundsQueries` (2·(2¹⁷ − 1)). The segment splits
on the Q16 grid of its own length, so no piece lies deeper than sixteen
halvings, and each costs at most two queries.

The sweep is written once, as `CertifiedFieldSweep`, over the `IFieldBounds`
seam in `Puck.Maths`: a step scale and a bounds query over a box.
`SdfFieldEvaluator` implements it, and so can any field that encloses its own
answers over a box. `FieldBoundsUnion` encloses the lesser of two fields and
refuses a box either part refuses, so a body is swept clear of a program and a
field lattice at once. The seam's `ICertifiedSweepQuery`, `CertifiedSweep` and
`CertifiedSweepOutcome` are what a consumer, such as the physics contact solver,
reads.

`SdfFieldBoundsLawTests` sweeps every op, shape and blend's point answers
through boxes against the bounds, and holds the bounds interpreter's rule sets
to the point interpreter's. `SdfCertifiedQueryLawTests` holds the sweep to a
thin wall at speeds up to 100,000 units a step, with the fixed-step stepper as
the red leg, and holds a contact tolerance to ending the approach in a few
queries just off the face. `SdfBoundsTightnessLawTests` holds a half turn's
bounds to the unrotated program's over the box's image, and the box cull to
walking under half of a twelve-object row while still enclosing every point
answer. `SdfFieldOverflowLawTests` sweeps programs whose constants and
positions reach the carrier's ends, and holds every point answer inside its
bounds or both refusing.

## What determinism means here

Puck's determinism contract is a single sentence: **display is a pure function
of data plus tick plus inputs.** Given the same world document, the same
sequence of per-tick input snapshots, and the same tick count, the simulation
produces the same state regardless of which presentation backend is attached
or how long the wall clock takes.

That contract only constrains one side of the engine. **Simulation state is
fixed-point; presentation is float, and always has been.** The distinction
is not "old code is fixed, new code is float"—it is a permanent boundary.
Anything that decides what happens in the world—physics, gameplay
outcomes, anything a query like `TryGroundHeight` feeds back into a
decision—must be `FixedQ4816`/`FixedVector3`/`FixedPosition`, with no
wall-clock reads and no unseeded randomness. Anything that only decides how
something is *shown*—a camera's eased transition, an anchor's published
position, a shading tweak—was never required to be fixed-point,
because nothing reads it back into a decision. An anchor
([Motion and views](motion-and-views.md)) is exactly
this: it is *produced from* an already-decided fixed-point pose, converted
to float once at the moment of publishing, and its only consumers are
camera math. The float never has anywhere to leak back into simulation
state, so it never threatens the contract.

**The contract and the gates are not the same thing.** The contract is what must
always be true. The gates—hash comparisons, golden replays, calibrated
performance ceilings—are the evidence that it currently *is* true, and
evidence gets re-measured, not treated as sacred. A design is never watered
down just to keep a gate green; if a change legitimately alters measured
behavior, the gate's expectation gets re-captured against the new reality,
not the other way around. This is why the query system's own verification
instrument—the drift check that compares the evaluator's answers against
an independent GPU render and against the baked provider—is a *measured*
tolerance, frozen at what it actually observed, rather than an aspirational
number tightened until something breaks.

The evaluator's own guarantee is itself evidence of the contract rather than a
substitute for it: three independently constructed evaluators over the same
program and the same points hash bit-identical. That is what "deterministic"
cashes out to at the code level—not "close enough," but the same bits,
every time, by construction.

## Derive gravity from the field

`IWorldQuery` answers geometric questions about a world. A narrower,
separate interface—declared in `Puck.Maths`, so a field's producer and its
consumers can sit in sibling libraries that never reference each other —
answers a question one level more abstract:

```csharp
public interface IFieldEvaluator {
    FieldEvaluatorCapabilities Capabilities { get; }
    bool TryDistance(FixedPosition position, out FixedQ4816 distance, out int material);
    bool TryFieldGradient(FixedPosition position, out FixedVector3 gradient);
}
```

`TryDistance` is the field itself: the signed distance to the nearest
surface, negative inside geometry. `TryFieldGradient` is that field's
gradient—the unit-length direction of steepest distance *increase*,
i.e., straight away from the nearest surface. `SdfFieldEvaluator` estimates
it with a six-tap central difference over `TryDistance`—one ± pair per
world axis. It replaced the original four-tap tetrahedron probe, which is
equivalent only where the field is locally linear: at an edge or blend seam
the tetrahedron aliases curvature into a spurious tangential component (its
fingerprint is a normal with two exactly-equal components), and the contact
solver consuming that normal converted commanded planar momentum into a
deterministic tangential drift along walls. Central differences are exact
about a mirror-symmetric probe point only when every transform between the
probe and the shape is exactly affine in fixed point—a reset and a
translate, nothing that rotates or scales the point first—so a mid-face
normal on that kind of geometry carries no off-axis component. A rotation in
the chain makes the two probe taps round independently (a quantized
quaternion is not bit-exact), leaving a residual tangential component of
about a thousandth of the normalized gradient—some 400 times smaller than
the tetrahedron's edge aliasing, small enough to be imperceptible during
play, and exactly zero on geometry that is not rotated.

The gradient is the entire primitive. The engine stops there on purpose —
nothing on this interface, or in its implementation, knows what a "planet"
or "down" or "gravity" is. The field only ever answers "which way is the
surface closer" and "which way is it farther." Everything gameplay-shaped
is the consumer's one line on top:

```csharp
// "down," for a walker standing anywhere on a field — a flat floor, a
// planetoid's far side, the inside of a hollow shell:
var down = -gradient; // (already unit length)

// a rocket's escape thrust, or a repulsor: just drop the sign.
var up = gradient;
```

That a walker crossing a planetoid's terminator can compute "down" the same
way a walker on a flat floor does—one negated gradient read, no special
case for curvature—is the payoff of keeping this seam this narrow. The
field never encodes "this is a planet"; the consumer decides that a
gradient pointing toward a spherical mass *means* gravity, the same field
primitive would equally mean wind, magnetism, or nothing gameplay-shaped at
all if a different consumer read it differently.

---

## Related resources

- [.claude/skills/rendering/SKILL.md](../../../../.claude/skills/rendering/SKILL.md)
  —the `SdfFieldEvaluator` sync-pair entry (excluded-ops reconciliation,
  measured tolerances) and the "Composition, anchors, views, and queries"
  section's query provider summary.
- [AGENTS.md](../../../../AGENTS.md)—the determinism contract (core rule 4). No
  automated gate covers this query contract; `puck parity` checks only the
  state hashes and pixels of its authored parity world.
- Source: `src/Puck.Maths/FixedPoint/IWorldQuery.cs`,
  `src/Puck.Maths/FixedPoint/IFieldEvaluator.cs`,
  `src/Puck.SignedDistance/Queries/SdfFieldEvaluator.cs`,
  `src/Puck.SignedDistance/Queries/BakedWorldQuery.cs`,
  `src/Puck.SignedDistance/Queries/WorldQueryProviders.cs`.
