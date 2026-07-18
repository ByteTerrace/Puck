# Queries and determinism

After this chapter you will understand the one interface simulation code
uses to ask an SDF world things like "what's beneath my feet" and "can I see
that," why there are two different implementations of it with two different
confidence levels, and how those pieces fit into what "determinism" actually
means in this engine — the difference between a rule that never bends (the
creed) and a suite of measurements that gets re-taken (the gates). Along the
way you'll see the entire derivation of gravity from a signed distance
field: it's one line, and it belongs to the caller, not the engine.

## Why a world needs a query interface at all

A world renderer's job is to turn a program into pixels. A simulation's job
is different: it needs to ask the world questions no pixel answers — is
there ground under this point, is that line of sight blocked, did this ray
hit anything. Reaching into the renderer for these answers would mean
simulation logic depends on GPU state, on float math that is allowed to
differ by a few bits between backends, and on whatever the render happens
to have resident that frame. None of that is acceptable for code that has to
produce the *same* answer on every machine, every time.

`IWorldQuery` is the seam that keeps those worlds apart. It is fully
fixed-point (`FixedQ4816`/`FixedVector3`/`WorldCoord3`) end to end, and every
method is synchronous — both implementations that exist today are cheap
enough per call that no async plumbing is warranted.

## The five verbs

```csharp
public interface IWorldQuery {
    QueryCapabilities Capabilities { get; }

    bool Raycast(WorldCoord3 origin, FixedVector3 dir, FixedQ4816 maxDist, out RayHit hit);
    bool SphereCast(WorldCoord3 origin, FixedVector3 dir, FixedQ4816 radius, FixedQ4816 maxDist, out RayHit hit);
    bool Overlap(WorldCoord3 center, FixedQ4816 radius);
    bool TryGroundHeight(WorldCoord3 position, FixedQ4816 probeUp, FixedQ4816 probeDown, out FixedQ4816 groundY);
    bool LineOfSight(WorldCoord3 from, WorldCoord3 to);
}
```

- **`Raycast`** — the nearest hit along a ray, out to a max distance.
- **`SphereCast`** — the same question for a swept sphere instead of an
  infinitely thin ray (a character capsule probe, not just a hitscan).
- **`Overlap`** — does a sphere at this point intersect anything blocked?
  A placement/spawn/selection check, not a cast — it answers "is this spot
  free" without needing a direction.
- **`TryGroundHeight`** — the ground level directly above or below a point,
  searched within a bounded probe window. This is what a walking character
  snaps its feet to every tick.
- **`LineOfSight`** — is a straight line between two points unobstructed?
  The building block for "can this unit see that unit."

Every direction argument is normalized internally, so a caller never has to
remember to do it. `Capabilities` — a small struct of booleans
(`HasHeightfield`/`HasBlocked`/`HasOccupancy`) — is meant to be checked once
at startup, not per call: a provider that lacks a layer degrades gracefully
(a raycast without an occupancy grid falls back to the flat heightfield)
rather than throwing per query.

Every answer is tagged with a `WorldQueryConfidence`:

```csharp
public enum WorldQueryConfidence {
    Bounded = 0,  // a baked, resolution-quantized artifact — sign-correct, conservatively dilated
    Exact = 1,    // a live evaluator against the actual program
}
```

This is a **fidelity** signal, not a determinism one — both confidence
levels are bit-identical for the same inputs on the same provider. What
they answer is "how much should the caller trust the precision of this
particular number." An RTS unit snapping to the ground can live with
`Bounded`; a competitive hitscan probably wants `Exact`.

## Two providers, two philosophies

**`BakedWorldQuery`** wraps a `WorldQueryArtifact` — a heightfield plus a
blocked-cell bitmap, baked once, ahead of time, from float-authored
rectangles. The baking discipline is the same one the walk-grid system
uses: every rectangle edge snaps to a raw fixed-point value exactly once,
and every per-cell decision after that is pure integer arithmetic — the
float-to-fixed conversion happens at the edges of authoring, never inside
the per-tick query path. This provider is cheap, coarse by construction (a
cell's answer is only as precise as the cell), and never sub-cell-exact —
hence `Bounded`.

**`SdfFieldEvaluator`** is a second, independent interpreter of the *same*
instruction stream the GPU's `mapCore` walks — not a codegen of the shader,
a deliberate hand-written twin, the same relationship the program's own
host-side bounds/Lipschitz analysis passes already have to the shader. It
walks a live `SdfProgram` directly in `FixedQ4816`, so its answers reflect
whatever the program currently is, not a stale bake — hence `Exact`.

That exactness has a price: the evaluator is **warp-free**. Its constructor
walks the program once and throws immediately, naming the first
disqualifying instruction, if the program contains anything it cannot
interpret in fixed point — chiefly the ops whose exact math needs runtime
trigonometry it does not yet implement fixed-point (twists, bends,
log-spherical folds, cell jitter, polar repeats, and the two sinusoidal
warps, displacement and domain warp), plus the dynamic-transform op (its
per-frame pose buffer has no seam in this interface), the wallpaper fold
(isometric and therefore tractable, just not yet mirrored), and a small
number of shapes whose exact cores need runtime trig or texture sampling
(star and regular-polygon's `atan2`, the ellipse's cubic solve, and the
glyph shape's texture sample). Everything else — resets, translates,
rotations (a baked quaternion needs no runtime sin/cos), scales, repeats,
symmetry planes, elongation, onion/dilate, scoped field push/pop, and the
shape/blend core — is interpreted exactly, because every one of those
operations is either an isometry or has an exact, closed-form fixed-point
treatment. The constructor's fail-loud design is
deliberate: an evaluator that silently interpreted *part* of a program and
guessed at the rest would be worse than one that refuses outright, because
its wrong answers would look exactly like right ones.

`WorldQueryProviders.ForWorld` is the resolver a sim asks for the right
provider; a sim binds only `IWorldQuery` itself; nothing downstream needs to
know or care which provider answered.

## What "determinism" actually means here

Puck's determinism creed is a single sentence: **display is a pure function
of data plus tick plus inputs.** Given the same run document, the same
sequence of per-tick input snapshots, and the same tick count, the engine
produces the same simulation state — every time, on either GPU backend,
regardless of wall-clock timing.

That creed only constrains one side of the engine. **Simulation state is
fixed-point; presentation is float, and always has been.** The distinction
is not "old code is fixed, new code is float" — it is a permanent boundary.
Anything that decides what happens in the world — physics, gameplay
outcomes, anything a query like `TryGroundHeight` feeds back into a
decision — must be `FixedQ4816`/`FixedVector3`/`WorldCoord3`, with no
wall-clock reads and no unseeded randomness. Anything that only decides how
something is *shown* — a camera's eased transition, an anchor's published
position, a shading tweak — was never required to be fixed-point,
because nothing reads it back into a decision. An anchor
([chapter 6](06-motion-and-views.md)) is exactly
this: it is *produced from* an already-decided fixed-point pose, converted
to float once at the moment of publishing, and its only consumers are
camera math. The float never has anywhere to leak back into simulation
state, so it never threatens the creed.

**The creed and the gates are not the same thing.** The creed is what must
always be true. The gates — hash comparisons, golden replays, calibrated
performance ceilings — are the evidence that it currently *is* true, and
evidence gets re-measured, not treated as sacred. A design is never watered
down just to keep a gate green; if a change legitimately alters measured
behavior, the gate's expectation gets re-captured against the new reality,
not the other way around. This is why the query system's own verification
instrument — the drift check that compares the evaluator's answers against
an independent GPU render and against the baked provider — is a *measured*
tolerance, frozen at what it actually observed, rather than an aspirational
number tightened until something breaks.

The evaluator's own guarantee is itself evidence of the creed rather than a
substitute for it: three independently constructed evaluators over the same
program and the same points hash bit-identical. That is what "deterministic"
cashes out to at the code level — not "close enough," but the same bits,
every time, by construction.

## Gravity in one line

`IWorldQuery` answers geometric questions about a world. A narrower,
separate interface answers a question one level more abstract:

```csharp
public interface IFieldEvaluator {
    FieldEvaluatorCapabilities Capabilities { get; }
    bool TryDistance(WorldCoord3 position, out FixedQ4816 distance, out int material);
    bool TryFieldGradient(WorldCoord3 position, out FixedVector3 gradient);
}
```

`TryDistance` is the field itself: the signed distance to the nearest
surface, negative inside geometry. `TryFieldGradient` is that field's
gradient — the unit-length direction of steepest distance *increase*,
i.e., straight away from the nearest surface. `SdfFieldEvaluator` estimates
it with a four-tap tetrahedron central difference over `TryDistance`
(cheaper than the naive six-tap version, at a documented, measured error
bound against the analytic answer).

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
way a walker on a flat floor does — one negated gradient read, no special
case for curvature — is the payoff of keeping this seam this narrow. The
field never encodes "this is a planet"; the consumer decides that a
gradient pointing toward a spherical mass *means* gravity, the same field
primitive would equally mean wind, magnetism, or nothing gameplay-shaped at
all if a different consumer read it differently.

---

## Related resources

- [.agents/skills/sdf-world/SKILL.md](../../.agents/skills/sdf-world/SKILL.md)
  — the `SdfFieldEvaluator` sync-pair entry (excluded-ops reconciliation,
  measured tolerances) and the "Composition, anchors, views, and queries"
  section's query provider summary.
- [AGENTS.md](../../AGENTS.md) — the determinism and verification contract.
- Source: `src/Puck.SdfVm/Queries/IWorldQuery.cs`,
  `src/Puck.SdfVm/Queries/IFieldEvaluator.cs`,
  `src/Puck.SdfVm/Queries/SdfFieldEvaluator.cs`,
  `src/Puck.SdfVm/Queries/BakedWorldQuery.cs`,
  `src/Puck.SdfVm/Queries/WorldQueryProviders.cs`.
