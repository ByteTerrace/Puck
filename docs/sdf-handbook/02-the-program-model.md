# The program model

After this chapter you'll understand what a Puck scene actually is: a short
program the GPU interprets per pixel, built from four kinds of instruction over
one running accumulator. You'll know why intersection is dangerous where union
is free, what problem field scopes solve, how instances and materials ride the
same stream, and the two rules — the Lipschitz step clamp and the ISA admission
rule — that keep the instruction set both correct and small.

## An SDF is content, not a mesh

A signed distance field is a function: give it a point in space, it returns the
distance to the nearest surface — negative inside, positive outside, zero on the
skin. That single function defines a whole scene. There are no triangles, no
vertices, no meshes anywhere in the pipeline. The renderer finds surfaces by
*marching*: from each pixel's ray it repeatedly asks "how far to the nearest
surface?" and steps that far, safe in the knowledge that a correct distance can
never overshoot. A dozen or so steps later the ray has either converged on a
surface or escaped to the sky.

So the interesting question becomes: how do you *describe* that distance
function? In Puck the answer is a small **program**. A scene is a flat list of
instructions — `SdfProgram` on the C# side, a packed word buffer the shader
decodes — and evaluating the field means running that program once, at one
point, top to bottom. The GPU runs it millions of times per frame, once per
sample per pixel, but the program is the same tiny thing each time. This is why
the engine is "everything-as-data": the scene is not a loaded asset, it is
interpreted bytecode.

That framing pays off everywhere. A program is cheap to build, cheap to diff,
trivially serializable, and — because it is a pure function of position — it
renders identically on both GPU backends and can be re-evaluated on the CPU for
physics queries (see [chapter 7](07-queries-and-determinism.md)). The whole engine is an
interpreter and a compiler for this one little language.

## Four kinds of instruction

Every instruction in the stream is one of four kinds. Keeping them straight is
the key to reading and authoring programs.

```
        point ops            shape            field ops           blend
     (move the point)   (measure distance)  (reshape field)   (how it composes)
   ┌──────────────────┐  ┌──────────────┐   ┌──────────────┐  ┌──────────────┐
p →│ Translate Rotate │→ │ Sphere at p, │→  │ Onion Dilate │→ │ Union / Sub  │→ acc
   │ Scale Repeat …   │  │ distance d   │   │ Displace     │  │ Intersection │
   └──────────────────┘  └──────────────┘   └──────────────┘  └──────────────┘
```

**Point ops** transform the *evaluation point* before a shape measures it.
`Translate`, `Rotate`, and `Scale` are the familiar rigid/affine moves; `Repeat`
and `RepeatPolar` and `WallpaperFold` fold space so one authored prototype tiles
infinitely; `SymmetryPlane` mirrors it; `Bend`/`Twist`/`DomainWarp`/`LogSphere`
warp it. A point op never touches distance — it moves *where* the next shape
looks. Point ops compose in sequence, each building on the last, until a
`ResetPoint` snaps the working point back to the original world position to start
a fresh chain.

**Shapes** are the only instructions that actually measure distance: a sphere,
box, torus, capsule, cylinder, the lifted 2D family (rounded rectangle, polygon,
star, trapezoid, ellipse), a glyph sampled from a font atlas, a sampled brick
(see [chapter 9](09-bricks-and-baking.md)). A shape reads the current transformed point, computes its own
primitive distance, and hands that up as a *candidate*.

**Field ops** reshape the distance *already accumulated*, not the point.
`Onion` shells a solid into a hollow skin (`d = |d| − thickness`); `Dilate`
fattens it (`d −= radius`); `Displace` adds sinusoidal surface relief. These read
and rewrite the running distance directly.

**Blends** decide how a shape's candidate composes with everything before it:
`Union`, `Subtraction`, `Intersection`, their smooth and chamfered variants, and
`Xor`. The blend rides on the shape instruction — a shape is always emitted
*with* the blend that joins it to the scene.

## The one running accumulator — the rule that governs everything

Here is the single most important fact about the model, and the one that most
often surprises newcomers:

> **The whole program carries exactly ONE running nearest-surface distance —
> the accumulator. `ResetPoint` resets the evaluation POINT, never the
> accumulator.**

There is no tree. A blend does not compose "this shape with the previous shape";
it composes this shape's candidate against *the entire scene accumulated so far*.
When you emit a sphere with a `Union` blend, the interpreter computes
`min(accumulator, sphereDistance)`. The next shape unions against that, and so
on. The scene is a running fold, not a parse tree.

This one design choice makes some blends free and one blend hazardous:

- **Union is local.** `min(acc, candidate)` returns the accumulator wherever the
  candidate is farther. Adding a union member can only ever *bring a surface
  closer* somewhere; it can never disturb geometry it doesn't reach. You may emit
  a union anywhere in the program.
- **Subtraction is local.** Subtraction is `max(acc, −candidate)`. The negated
  candidate is positive (far) everywhere outside the subtrahend, so the `max`
  returns the accumulator untouched there — the carve only bites *inside* its own
  shape. Emit it anywhere too.
- **Intersection is NOT local.** `max(acc, candidate)` returns the *candidate*
  everywhere the candidate is farther — which is everywhere outside the
  candidate's own shape. That means an intersection **annihilates every earlier
  shape it does not overlap**, the ground plane included. Its region of influence
  is unbounded.

The rule that falls out of this is worth memorizing:

> **Author an intersection pair FIRST, against the empty accumulator.** An
> intersection annihilates everything it doesn't overlap, so intersecting two
> shapes means emitting both before any unrelated geometry exists to be deleted.

The failure mode is vivid: emit an intersection pair *last*, after a floor and
other clusters, and the scene renders as a lone wedge floating on empty sky —
the intersection quietly deletes everything else. That loudness is the one
mercy: a misplaced intersection destroys so much that it cannot hide.

**Why the field ops are the quiet cousins of this bug.** `Onion`, `Dilate`, and
`Displace` also read the whole accumulator. `Onion`'s `|d| − t = 0` means
`d = ±t`: the outer surface of *everything so far* moves outward by `t` and every
solid goes hollow. But unlike intersection this reads as "the object got a little
bigger," trips no gate, and hides. When you review a program, weight your
attention toward the accumulator-reading ops — intersection and the field ops —
because they are the ones that reach backward through the whole scene.

There is one graceful exception. `Xor` (symmetric difference) *looks* like it
should be unmaskable, but it reduces to plain union everywhere a first-hit march
ever samples, and the extra surface it carves lives strictly inside the union
hull. So `Xor` composes and culls exactly as safely as a union member. It is a
deliberate, settled exemption — not an oversight to "fix."

## Field scopes — a fence around the accumulator

The accumulator rule has a sharp edge. Suppose you build an elaborate scene, then
want to hollow out *one* object with `Onion`. You can't — a field op reads the
whole accumulator, so it shells the entire scene. Intersecting two shapes deep in
a populated program has the same problem: intersection deletes everything else.

**Field scopes** are the fix. `PushField` saves the running accumulator into a
one-deep slot and reseeds a *fresh* empty accumulator; `PopField` composes the
scope's result back into the saved parent as a single candidate, using a blend
you choose on the pop. Everything between the balanced push and pop — every
intersection, every `Onion`/`Dilate`/`Displace` — acts on the scope's own shapes
alone.

```
  … big scene …          acc = whole scene so far
  PushField              save parent; acc ← empty
    Sphere (Union)       acc = sphere
    Onion(0.2)           shells ONLY the sphere
  PopField (Union)       acc = min(saved parent, hollow sphere)
  … more scene …
```

A scope touches the field, never the point: `localPosition`, `distanceScale`, and
the material chain are untouched across a push, so cull bounds computed for shapes
after the push stay sound. A `Union` pop makes the whole scope far-neutral and
therefore *maskable* again (the culling payoff); an intersection-family pop stays
globally unmaskable, exactly like an unscoped intersection. There is one fine-print
rule — a scoped field op grows the surface *outward* past the authored geometry
bound, so the packer inflates the instance's cull bound by that reach (`Onion(t)`
moves out by `t`, `Dilate(r)` by `r`, `Displace(a)` by `a`) or the beam would
mask away tiles the grown shell reaches and the surface would hole at tile seams.
Scope depth is capped at one today, which covers "hollow this one object in a full
scene" without opening the door to arbitrary nesting.

The payoff is that a program stays a flat stream — no tree, no allocation, one
running value — while still expressing "operate on just this part."

## Material flow

Distance answers *where* the surface is; material answers *what it looks like*.
Material rides the same stream. Each shape carries a material id, and as blends
compose, the material of the *nearest* candidate wins — a strict compare that
runs alongside the distance blend (on an exact tie, the incumbent keeps its
material).
The point-folding ops can also stride material by cell: `WallpaperFold`,
`RepeatPolar`, and `CellJitter` optionally recolor per fold cell so a tiled
prototype alternates materials by checker parity, sector index, or hash. Because
the material winner is decided by the same nearest-surface logic as distance, you
never manage a separate material tree; it falls out of the accumulator fold for
free. The shading pass ([chapter 4](04-lighting-and-shading.md)) takes the
winning material and lights it.

## Instances — the same program, placed many times

A single instruction stream describes the *content*; **instances** place that
content into the world many times without re-uploading it. An instance is a range
of the program with a transform and a cull bound. Three lifecycles matter:

- **Static** instances are placed once and don't move — a forest of rocks, a
  field of carves. They bin into the spatial grid ([chapter 3](03-the-frame.md))
  by center and cost nothing to re-cull frame to frame.
- **Dynamic** instances read their transform from a per-frame buffer slot
  (`TransformDynamic`): a walking player, a moving screen. The static scene
  program never changes; only a small transform buffer updates each frame.
- **Parked** instances are reserved-but-inactive slots. A pool (players,
  companions, the creator's authoring set) reserves its worst-case instance count
  up front so capacities can freeze at construction, then marks slots active or
  parked per rebuild. A parked slot packs a negative-radius bound sentinel that
  every cull test skips with one branch, so reserved-but-unused capacity costs
  essentially nothing per frame. (Placing an instance *without* clearing its
  active flag — hiding it below the floor instead of parking it — is a classic
  bug: it stays in every cull test and the cost tracks reserved capacity instead
  of live content.)

The guiding principle: **cost tracks live, on-screen content, not reserved
capacity.** Capacities freeze at construction (program words, instance-mask
width, dynamic slots); a frame source declares its envelope with a worst-case
*probe* program up front, and `UploadProgram` loudly rejects anything that
exceeds it. Below the envelope, rebuilds vary freely and cheaply.

## The Lipschitz story, told simply

Marching is only safe if the distance the program returns never *overshoots* the
true distance to the surface. For ordinary rigid shapes and their unions this is
automatic: a real distance is exactly 1-Lipschitz, meaning it changes by at most
one unit per unit of travel, so stepping by the returned distance can never skip a
surface.

But some point ops *warp space*. A `Bend` or `Twist` or `DomainWarp` or the
log-spherical Droste fold stretches the metric — after the warp, the field
measured in warped coordinates can *underestimate* how fast the surface
approaches in world space, and an unclamped march could tunnel straight through a
thin shell. The fix is a per-program **step clamp**. The compiler pass
`AnalyzeLipschitz` walks the program once and, for each warp, folds in a closed-form
factor for how much that op can stretch the metric — a `Bend`'s exact operator
norm, a `Twist`'s different one, `exp(w/2)` for the log-sphere,
`1 + amplitude · max|frequency component|` for a displacement or domain warp
(deliberately the infinity norm — the Euclidean length was merely conservative
and over-clamped by up to `√3`), `√2` for a chamfer seam. The product becomes a
single `stepScale` (= 1/L) baked into the program; the marcher multiplies its
final step by it once, so a warped program simply approaches surfaces more
cautiously and never holes.

The elegant part is the boundary case. **An isometric, warp-free, chamfer-free
program gets `stepScale == 1.0` exactly** — bit-identical, no tax. Rotations,
reflections, translations, plain repeats, symmetry folds are all isometries and
contribute nothing. You pay the step clamp only for the exotic ops that actually
bend space, and only in the programs that use them. (The per-candidate
`distanceScale`, which handles `Scale` and the log-sphere's radial correction, is
a *separate* channel — never merged with `stepScale`.)

The practical rule for authors: **isometries are free; warps cost a step clamp;
keep warp rates moderate.** The correctness is proven at compile time, not tuned
by hand.

## The ISA admission rule — why the instruction set stays small

The instruction set is deliberately lean, governed by one owner-ratified rule:

> **An op or shape earns its own switch case ONLY if it cannot be composed
> exactly from existing vocabulary. Otherwise it ships as a builder macro that
> emits existing ops.**

The reason is that every real opcode costs register pressure in the hottest
kernel on the GPU — the per-pixel interpreter runs the op switch millions of
times a frame, and each case is live cost whether or not a given program uses it.
So a capability that *can* be expressed with what already exists should be a
convenience method on the builder, not a new case in the interpreter. The current
ISA still includes `RegularPolygon` and `Star`; they remain candidates for exact
`RepeatPolar`-based builder macros. `Ellipse` needs an exact curve that no existing
composition provides and therefore earns a real case. The compiled core-ops views
variant strips unused exotic vocabulary for programs that do not reference it.

Enum numbering is non-sequential because wire values remain stable; values 13–15
are reserved after the three axis-aligned symmetry folds were represented by the
general `SymmetryPlane` operation. The instruction set grows only
when a capability is genuinely *uncomposable* — the discipline that keeps the
per-pixel interpreter fast.

---

## Related resources

- The accumulator rule, the intersection-annihilation bug, and the `Xor`
  exemption: [`.agents/skills/sdf-world/SKILL.md`](../../.agents/skills/sdf-world/SKILL.md)
  ("The accumulator rule"), and the doctrine on `SdfBlendOp`.
- Per-op Lipschitz norms and the `stepScale == 1.0` byte-identity contract:
  [`docs/sdf-wiki/lipschitz-and-field-correctness.md`](../sdf-wiki/lipschitz-and-field-correctness.md).
- Field scopes (`PushField`/`PopField`) and the margin rule:
  the `SdfOp.PushField`/`PopField` doc comments in
  [`src/Puck.SdfVm/SdfOp.cs`](../../src/Puck.SdfVm/SdfOp.cs).
- The ISA admission rule and current instruction inventory: the sync-pair
  preamble in [`.agents/skills/sdf-world/SKILL.md`](../../.agents/skills/sdf-world/SKILL.md).
- Parked-instance cost tracking and the capacity-probe envelope: the
  "Engine semantics" section of the same skill.
