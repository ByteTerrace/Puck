# The idea

After this chapter you'll understand what a signed distance field actually
*is*, why it lets an entire 3D world be described as one function instead of
a pile of triangles, how a renderer can march a ray to a surface using
nothing but that function's output, and why Puck interprets a world as data
on the GPU rather than compiling a bespoke shader for every scene. By the
end you should be able to hold the whole system — program, engine, passes,
pixels — in your head as one small diagram.

## A function that tells you how far you are from something

Take any point in 3D space and ask it one question: *how far is the nearest
surface?* If you can answer that question for every point, you have a
signed distance field (an "SDF"). The "signed" part matters: the answer is
positive outside the surface, negative inside it, and exactly zero on it.
Nothing else about the surface needs to be stored — no vertices, no
triangles, no UVs. The surface is implicitly wherever the answer crosses
zero.

For a sphere of radius `r` centered at the origin, the field is one line of
math:

```
distance(p) = length(p) - r
```

Stand far away and it returns a big positive number. Stand exactly on the
sphere's skin and it returns zero. Stand at the center and it returns `-r`.
That one formula fully describes an infinitely-detailed sphere — there is no
polygon budget to spend, no level-of-detail to swap, no mesh to load.

## Why a whole world can be one function

The reason this scales past "one sphere" is that distance fields *compose*.
Given the fields for two shapes, `a` and `b`:

- `min(a, b)` is the field of their **union** — whichever shape is closer wins.
- `max(a, -b)` is `a` with `b` **carved out** of it (subtraction).
- `max(a, b)` is their **intersection** — only the region both shapes agree is solid.

Each of these is itself a valid distance field (an *estimate*, in some
cases — more on that in [chapter 2](02-the-program-model.md)), so it can feed
right back into another combinator. A floor, a wall, a stack of shaped
objects, a repeated colonnade, a carved doorway — a whole scene is built the
same way a spreadsheet is built: small formulas referencing the results of
other small formulas. There is never a moment where the world becomes a
mesh. It stays one function, evaluated fresh at whatever point you ask
about, for as long as it exists.

This is also why deformation is cheap conceptually: twisting, bending,
repeating, or warping space *before* evaluating a shape's formula bends the
shape without touching any stored geometry — there is no geometry to touch.

## Sphere tracing: walking a ray to the surface

A distance field only answers "how far to the nearest surface from *this*
point" — it doesn't hand you an intersection point directly the way a
ray/triangle test does. But that one answer is enough to render an image,
because of a simple guarantee: **the field never overestimates the true
distance.** So it is always safe to step a ray forward by exactly the
distance the field just reported — a step that size cannot skip past a
surface, because nothing is closer than that.

```
ray origin •───d0───▶ ●───d1──▶●──d2─▶●─▶◍  (hit: distance ≈ 0)
           step by d0   step by d1  step by d2 ...

     the circles are how far the field says the nearest
     surface is FROM THAT POINT — never a lie, never an
     overestimate, so stepping that far is always safe
```

Repeat — evaluate the field, step forward by the reported distance — and the
ray closes in on the surface geometrically, needing few steps in open space
and naturally slowing down as it approaches something close. Stop when the
distance drops below a small threshold (a hit) or after too many steps (the
ray escaped into the sky). This is **sphere tracing**, and it is the entire
rendering primitive underneath Puck's world: no rasterizer, no vertex
pipeline — every pixel independently sphere-traces its own ray against the
same field.

Two things make this fast and safe in practice, both covered in depth
elsewhere in this handbook and the wiki:

- A field that isn't a pure distance (some combinators and warps only
  *bound* it) needs a correction factor before you can trust "step by
  exactly what it says" — Puck bakes this once per program as a conservative
  step scale, so the march never overshoots.
- Most of a scene is empty space from any given camera; cheap coarse passes
  (a low-resolution cone march, a per-tile visibility mask) let the
  full-resolution march skip huge regions instead of stepping through them
  one small hop at a time.

## Why an interpreted program instead of a compiled shader

A conventional real-time renderer with procedural shapes usually compiles a
new shader per scene: the artist's shape graph becomes GLSL/HLSL source,
which becomes a compiled kernel specific to that one arrangement of shapes.
Changing the scene means recompiling.

Puck deliberately does not do this. A scene is a small flat **program** — a
stream of instructions (`ResetPoint`, `Translate`, `Sphere`, `Union`, ...) —
and the GPU runs **one fixed shader** that *interprets* that stream, the same
way a bytecode VM interprets bytecode instead of JIT-compiling a new native
binary per script. The renderer's shader code never changes; only the data
it walks does.

That trade buys three things Puck cares about more than raw peak
throughput:

- **Content is data, not code.** A world is a JSON-shaped document plus a
  program buffer, so it can be authored, saved, diffed, sent over a wire, or
  generated by a tool without anyone touching a shader compiler.
- **Hot swap.** Editing a scene means uploading a new instruction buffer.
  There is no shader recompile in the loop, so a live editor can rebuild the
  world every frame if it wants to.
- **Determinism.** Display is a pure function of data, the current tick, and
  this tick's inputs. An interpreter walking the same words in the same
  order produces the same output on any conforming GPU — the property a
  compiled-per-scene shader would have to re-prove for every distinct
  shader, forever. One interpreter earns that proof once.

The cost is real and known: an interpreter pays dispatch overhead a
specialized compiled kernel wouldn't, and it cannot fold scene-specific
constants at compile time the way a generated shader could. Puck's answer to
that cost is architectural, not a refusal to pay it — cull passes that keep
the interpreter from running at all where it doesn't have to (see
[chapter 3](03-the-frame.md) and the wiki's acceleration pages), not a
retreat to per-scene shader generation.

## The map: program → engine → passes → pixels

Four layers, each one built from the layer below it:

```
 ┌─────────────────────────────────────────────────────────────┐
 │  AUTHOR                                                      │
 │  SdfProgramBuilder — a fluent C# API: Sphere(), Box(),       │
 │  Translate(), Repeat(), Union blends, material scopes...     │
 └───────────────────────────┬───────────────────────────────────┘
                              │ .Build()
 ┌───────────────────────────▼───────────────────────────────────┐
 │  DATA                                                         │
 │  SdfProgram — the packed instruction stream (a flat array of  │
 │  words), plus its materials, instances, and bounds — the ONE  │
 │  thing that gets uploaded, saved, diffed, or replayed         │
 └───────────────────────────┬───────────────────────────────────┘
                              │ UploadProgram
 ┌───────────────────────────▼───────────────────────────────────┐
 │  ENGINE                                                        │
 │  SdfWorldEngine — owns the GPU buffers, uploads the program    │
 │  and this frame's camera/lighting/dynamic transforms, and      │
 │  dispatches the fixed pipeline of compute passes below         │
 └───────────────────────────┬───────────────────────────────────┘
                              │ per frame
 ┌───────────────────────────▼───────────────────────────────────┐
 │  PASSES                                                        │
 │  mask → beam → cull-args → views → composite                  │
 │  (which tiles touch which objects → coarse cone march per      │
 │  tile → pack the fine-march workload → per-pixel sphere trace  │
 │  + shade → combine viewports into the final frame)             │
 └───────────────────────────┬───────────────────────────────────┘
                              │
                         ┌────▼────┐
                         │ PIXELS  │
                         └─────────┘
```

Every layer below "author" is the same fixed machinery for every scene the
engine ever renders; only the *program* — the data at the top — changes
between a mostly-empty test scene and the full game world. That is the
whole point of the architecture: the interpreter and its passes are built
once, verified once, and then reused unchanged for as long as the content
above them keeps being expressed as the same kind of program.

The next chapter, [02-the-program-model.md](02-the-program-model.md), is
about the language itself: the four kinds of instruction, the one running
accumulator that governs how shapes compose, and the two rules that keep the
instruction set correct and small. [Chapter 5](05-authoring.md) then turns
that model into practice with `SdfProgramBuilder`.

---

## Related resources

- [docs/sdf-wiki/README.md](../sdf-wiki/README.md) — the standing
  determinism/parity constraints referenced throughout this handbook.
- [docs/sdf-wiki/marching-acceleration.md](../sdf-wiki/marching-acceleration.md)
  and
  [docs/sdf-wiki/hierarchical-and-instance-acceleration.md](../sdf-wiki/hierarchical-and-instance-acceleration.md)
  — the coarse-march and tile-cull techniques the passes diagram above
  compresses into "beam" and "mask".
- [docs/sdf-wiki/lipschitz-and-field-correctness.md](../sdf-wiki/lipschitz-and-field-correctness.md)
  — the formal theory behind the step-scale correction mentioned above.
- [docs/sdf-bench-notes.md](../sdf-bench-notes.md) — measured per-pass GPU
  cost on the reference hardware, for readers who want the numbers behind
  "cheap coarse passes let the fine march skip regions."
- [.agents/skills/sdf-world/SKILL.md](../../.agents/skills/sdf-world/SKILL.md)
  — the living contract this chapter's map is a simplified, human-facing
  view of.
