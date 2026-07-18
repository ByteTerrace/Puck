# Authoring

After this chapter you'll be able to build a real scene with
`SdfProgramBuilder` — shapes, transforms, repeats, and carves composed into
one program — know when to split a scene into independent emitters instead
of one long method, and recognize the handful of authoring mistakes that
produce a scene which *looks* fine until a camera angle, a cull pass, or a
different GPU backend finds the seam. It assumes the model
[chapter 2](02-the-program-model.md) teaches: a program is a flat
instruction stream over one running "nearest surface so far" distance, and
shapes compose into it through blends like union, subtraction, and
intersection.

## The grammar: point ops and shape ops

`SdfProgramBuilder` instructions come in two families, and the distinction
matters for ordering:

- **Point ops** move or warp the coordinate frame that the *next* shapes
  evaluate against — `Translate`, `Rotate`, `Scale`, `Repeat`, `TwistY`,
  `RepeatPolar`, and friends. `ResetPoint` snaps the frame back to world
  space, clearing any transform (and any positional material recolor) built
  up since the last reset.
- **Shape ops** emit an actual primitive — `Sphere`, `Box`, `Plane`,
  `Torus`, `Capsule`, and the rest — and immediately compose it into the
  program's running field with a blend (`Union`, `SmoothUnion`,
  `Subtraction`, `Intersection`, ...).

A typical chain reads like a sentence: reset the frame, move it somewhere,
maybe rotate or fold it, then place a shape there.

```csharp
builder
    .ResetPoint()
    .Translate(new Vector3(2f, 0.5f, 0f))
    .Sphere(radius: 0.5f, material: stoneId);
```

Remember the accumulator rule from [chapter 2](02-the-program-model.md):
there is exactly one running field for the whole program — `ResetPoint`
resets the *point*, never the accumulated distance — which makes `Union` and
`Subtraction` safe to emit in any order and `Intersection` a scene-wide
hazard. In authoring terms:

> **Rule: author an intersection pair first, against the empty
> accumulator.** An intersection annihilates every earlier shape it doesn't
> overlap — floor included — so it can never safely follow unrelated
> geometry. If you need "the part of A that's inside B," emit `A` then `B`
> with an `Intersection` blend as the very first thing in the program (or
> inside a scope — see below) — never after a floor or a wall already
> exists.

A scoped field accumulator (`PushField`/`PopField`) exists precisely so an
intersection, or a field op like `Onion`/`Dilate`, can be sandboxed to a
handful of shapes without threatening the rest of the scene — open a scope,
emit the shapes it should affect, close it, and the scope composes back
into the parent as a single ordinary candidate.

## A worked example: building a plaza

Start with the one thing every scene needs: unbounded world geometry that's
always evaluated, wherever the camera looks.

### The floor

```csharp
var builder = new SdfProgramBuilder();
var stoneId = builder.AddMaterial(new SdfMaterial(Albedo: new Vector3(0.55f, 0.53f, 0.5f)));

builder
    .ResetPoint()
    .Plane(normal: Vector3.UnitY, offset: 0f, material: stoneId);
```

A `Plane` has no natural bounding sphere — it's infinite by construction —
so it belongs in the **world set**: instructions declared outside any
`Instance`, always evaluated, never masked out by the tile-cull passes.

### A shaped object

Most content isn't infinite, and the renderer's tile-cull pass wants a
bounding sphere it can test per screen tile instead of paying to evaluate
every object everywhere. `Instance` opens exactly that: a bounding sphere,
and everything emitted inside it belongs to that one culling unit.

```csharp
var bronzeId = builder.AddMaterial(new SdfMaterial(Albedo: new Vector3(0.4f, 0.3f, 0.1f), Specular: 0.6f));

builder.Instance(boundCenter: new Vector3(0f, 1f, 0f), boundRadius: 1.6f, emit: b => b
    .ResetPoint()
    .Translate(new Vector3(0f, 1f, 0f))
    .Box(halfExtents: new Vector3(0.4f, 0.9f, 0.4f), round: 0.05f, material: bronzeId)
    .Sphere(radius: 0.6f, material: bronzeId, blend: SdfBlendOp.SmoothUnion, smooth: 0.25f)
);
```

A pedestal (`Box`) with a rounded finial (`Sphere`) melted into it with
`SmoothUnion` — a shaped statue from two primitives and one blend. The
instance's bounding sphere (center, radius) must actually cover the finished
shape; the tile-cull pass trusts it completely; understating it clips the
statue at the tiles the pass wrongly decides it can skip.

### A repeated colonnade

A row of columns is one column's instructions, repeated — not twelve
`Instance` blocks. `RepeatLimited` folds the point onto a finite lattice
before the column shape evaluates, so one `Cylinder` call becomes a whole
colonnade:

```csharp
builder.Instance(boundCenter: new Vector3(0f, 1.5f, 6f), boundRadius: 14f, emit: b => b
    .ResetPoint()
    .Translate(new Vector3(0f, 1.5f, 6f))
    .RepeatLimited(spacing: new Vector3(3f, 0f, 0f), limit: new Vector3(5f, 0f, 0f))
    .Cylinder(radius: 0.35f, halfHeight: 1.5f, material: stoneId)
);
```

`limit.X = 5` caps the lattice at columns −5..+5 along X (eleven columns);
`spacing.Y/Z = 0` means the fold never moves the point on those axes, so
there's exactly one row. The whole colonnade is one shape instruction; the
instance's bounding sphere just needs to be wide enough to cover the
outermost columns.

> **Rule: keep repeated content inside its own cell.** `Repeat` and
> `RepeatLimited` return the *current cell's* copy only — the fold never
> checks a neighboring cell for a nearer copy. An on-center column within
> half the spacing per axis is exact. An off-center or oversized prototype
> creases the field at the cell wall with an **overestimate** — the nearest
> real surface is one cell over, and nothing catches this (it isn't a
> Lipschitz violation, it's a missing neighbor check) — so it can hole the
> march at grazing angles. Size the prototype to fit inside half its
> spacing on every axis it repeats along, the same rule `CellJitter`'s
> in-cell containment follows.

### A carved detail

Subtraction is local, so unlike the intersection example above, a carved
window can be added anywhere after the wall it cuts into:

```csharp
builder.Instance(boundCenter: new Vector3(-6f, 1.5f, 0f), boundRadius: 3.2f, emit: b => b
    .ResetPoint()
    .Translate(new Vector3(-6f, 1.5f, 0f))
    .Box(halfExtents: new Vector3(0.3f, 1.5f, 3f), round: 0f, material: stoneId)
    .ResetPoint()
    .Translate(new Vector3(-6f, 1.6f, 0f))
    .Box(halfExtents: new Vector3(0.5f, 0.8f, 0.6f), round: 0.05f, material: stoneId, blend: SdfBlendOp.Subtraction)
);
```

The second `ResetPoint` re-anchors the frame at world space before
positioning the window box — a carve's own translate is relative to the
same world origin as the wall's, not relative to the wall's local frame
(there is no such thing as a shape's local frame once it's emitted; only the
point ops between resets carry state).

## The composition layer: many emitters, one program

The plaza above was one method because it's small. A real world — a room
full of furniture, a creature pool, a diegetic screen, an editor's live
preview — is not one method; it's several independent concerns that all
need to land in the same program. `ISdfSceneEmitter` is the seam that lets
them:

```csharp
public interface ISdfSceneEmitter {
    void Emit(SdfProgramBuilder builder, in SdfEmitContext context);
    int DynamicSlotCount => 0;
    void PackDynamicTransforms(Span<DynamicTransform> slots, in SdfEmitContext context) { }
    int Revision => 0;
    bool OwnsMaterialScope => false;
}
```

Each emitter owns one concern — a room's fixed geometry, a sculpted scene, a
pool of live creatures — and contributes to a *shared* builder instead of
building its own program. `SdfCompositionFrameSource` holds a fixed list of
these, assigns each one a contiguous range of dynamic-transform slots, sums
their `Revision`s to know when to rebuild, and rebuilds by calling every
emitter's `Emit` in order against one builder.

Why bother, instead of one big hand-written `BuildProgram`? Three reasons
that only compound as a world grows:

- **Independent concerns stay independently testable and independently
  replaceable.** A debug takeover, a creator-mode preview, and the room's
  permanent furniture don't need to know about each other's instruction
  counts or material indices.
- **Dynamic-transform slots assign themselves.** Each emitter declares how
  many per-frame-movable slots it needs (`DynamicSlotCount`); the
  composition host sums them and hands out non-overlapping ranges — no
  emitter hand-counts where its slots start.
- **Positional material safety is structural, not a documentation
  convention** — see the next section.

An emitter that hard-codes a "takeover" mode (a full-scene replacement, like
a debugger view) doesn't express that inside `Emit` — the host swaps in an
entirely different emitter *list* for that mode instead. `Emit` always
means "add my content to the shared scene," never "replace it."

## Materials and scopes

A material is small and shading-only — it never affects distance:

```csharp
public readonly record struct SdfMaterial(
    Vector3 Albedo,
    float Emissive = 0f,
    float Specular = 0f,
    float Shininess = 32f
);
```

`AddMaterial` appends one and returns its index; shapes reference materials
by that index. Most of the time that's the whole story. It gets more
interesting when a fold recolors *positionally* — `WallpaperFold` and
`RepeatPolar` can take a `materialStride`, so each lattice cell or each
sector of a repeated ring picks a different row of the palette (a
checkerboard floor, alternating column colors around a rotunda) from one
instruction instead of one shape call per copy.

The hazard: a positional stride's reach is computed from the *shape's own*
material index forward — it has no way to know where one emitter's palette
ends and another's begins in a builder several emitters share. Left
unguarded, a stride tuned for one emitter's four materials could reach into
the next emitter's first material and silently recolor the wrong thing.

`BeginMaterialScope` closes that hole structurally:

```csharp
using (builder.BeginMaterialScope()) {
    var a = builder.AddMaterial(new SdfMaterial(Albedo: colorA));
    var b = builder.AddMaterial(new SdfMaterial(Albedo: colorB));

    builder
        .ResetPoint()
        .RepeatPolar(count: 8, materialStride: 1)
        .Sphere(radius: 0.3f, material: a);
}
```

While the scope is open, any positional stride is clamped so it can only
ever land on a material *this scope itself added* — never an outer scope's
material, never a different emitter's. `ISdfSceneEmitter.OwnsMaterialScope`
tells the composition host to wrap that emitter's whole `Emit` call in one
of these automatically.

> **Rule: add every material a positional fold will recolor through before
> emitting the fold and the shapes that use it.** Followed, the scope clamp
> never triggers — it's a safety net for the case where it isn't, not a new
> step you have to think about on the happy path.

## The capacity-probe doctrine

The engine's GPU buffers are sized once, at construction, and never grow —
`UploadProgram` rejects a program that exceeds them rather than silently
truncating it. That's a deliberate trade: a fixed envelope is one an editor
can hot-swap content inside of, all session long, without ever
re-allocating a GPU buffer mid-frame. The price is that the envelope has to
be *right* before the session starts.

> **Rule: declare your worst case up front.** Every emitter's `Emit` needs a
> branch — selected by `context.Probe` — that takes its single largest
> legal form: every optional shape present, every modifier at its worst
> magnitude, every dynamic slot in use. One construction-time call with
> `Probe: true` runs every registered emitter's worst-case branch into one
> combined program, and *that* program's word count, instance count, and
> dynamic-transform count become the frozen ceiling every live rebuild for
> the rest of the session is measured against.

The probe program itself is never rendered — it exists purely to be
measured. The rule this creates for you as an author: when you add a new
*optional* piece of content to an emitter (a toggleable decoration, an
occasionally-present dynamic entity), you must grow that emitter's probe
branch to match, in the same change. Skip it, and the first live session
that actually uses the new content can outgrow the buffers the probe
promised — caught loudly (`UploadProgram` throws), but only at the moment
someone hits it, not at build time.

## Pitfalls, as rules

- **DO** author an intersection pair first, against the empty accumulator
  (or inside a scope). **DON'T** place an intersection after unrelated
  geometry — it deletes everything it doesn't overlap, silently.
- **DO** keep a `Repeat`/`RepeatLimited`/`CellJitter` prototype within half
  its spacing per axis. **DON'T** let it overspill a cell — the fold has no
  neighbor check, so an oversized or off-center prototype creases the field
  at the cell wall with an overestimate that can hole the march at grazing
  angles.
- **DO** emboss (a proud union) or engrave (a subtraction) a text label
  against its backing surface. **DON'T** ever place a label's field exactly
  coplanar with a surface it sits on — two surfaces sharing the same
  zero-set speckle unpredictably where floating-point rounding decides
  which one "wins" a given sample.
- **DO** treat a builder as spent the moment an `Instance`/`DynamicInstance`
  callback throws. **DON'T** catch the exception and keep using the same
  builder — it's left with an instance open and partial state, and nothing
  rolls that back; discard it and start over.

---

## Related resources

- [docs/sdf-wiki/lipschitz-and-field-correctness.md](../sdf-wiki/lipschitz-and-field-correctness.md)
  — domain-repetition exactness and the neighbor-cell discontinuity this
  chapter's containment rule is built on.
- [docs/sdf-wiki/text-and-glyphs.md](../sdf-wiki/text-and-glyphs.md) — the
  full engrave/emboss correctness case (C1/C2/C3) behind the never-coplanar
  label rule.
- [docs/sdf-wiki/materials-and-primitives.md](../sdf-wiki/materials-and-primitives.md)
  — the material-blend-at-seams background behind the material-scope
  mechanism.
- [.agents/skills/sdf-world/SKILL.md](../../.agents/skills/sdf-world/SKILL.md)
  — the composition/anchor surface contract this chapter's emitter section
  summarizes, and the C#↔HLSL sync-pair table for every op named above.
