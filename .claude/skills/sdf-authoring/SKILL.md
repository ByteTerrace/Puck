---
name: sdf-authoring
description: "Author and edit Puck creations inside world prototypes: characters, armor, props, and SDF geometry. Use for sculpting, primitive/blend/warp selection, materials, state-driven looks, shape budgets, and author-frame, scope, or contact mismatches. Use sdf-world instead for VM, ISA, kernel, or shader implementation."
---

# Authoring a Puck creation

A creation is a `puck.creation.v1` document: a palette, a list of shapes, and
optional rig, motion, text, volume, and noise sections. Shapes are signed
distance fields composed by blends, not meshes — so a "cut" is a subtraction, a
"fillet" is a blend radius, and repetition is a fold rather than copies.

**Where it lives.** On disk in a world document at `prototypes[].document`. The
console addresses those same rows as section **`creations`**, keyed by prototype
id. That mismatch is real: `world.row.set creations moth …` edits what the file
spells `prototypes[id=moth].document`. Read `references/loop.md` before driving
it live.

## The author frame — read this first

Every creation position, rotation, joint, pivot, axis, and camera offset is
authored in one frame that is a **180° yaw about +Y away from the engine's**:

| Author axis | Points |
|---|---|
| +Y | up |
| +Z | the front the subject faces — toward a front-facing camera |
| +X | screen-right as you look at that front, which is the subject's own **left** |

So a shape authored at +X appears on the viewer's right, on the character's left
side. Getting this backwards is the single most expensive mistake in this
surface, and it looks like a correct sculpt mirrored — not like a bug. The
conversion is exact axis negation, `(x, y, z) → (−x, y, −z)`, and it is its own
inverse (`src/Puck.World.Authoring/Authoring/CreationFrame.cs`).

Scalars, camera yaw and pitch, and chain goal and pole do **not** convert.
The conversion is field-specific: shape position, rotation, and joint convert;
`curve.a/b/c` remain shape-local and ride that converted pose. Palette
`inset.origin` and `inset.rotation` do not convert. Author them in the
shader's winning frame (world space for static geometry; the owning dynamic
slot's local frame otherwise). See [surface](references/surface.md#inset).

## The loop

Authoring is a live loop, not an edit-and-restart cycle. Five commands carry
almost all of it:

```
world.row.set creations <id> document.shapes[name=<shape>].<field> <json>
world.row     creations <id> document.shapes[name=<shape>]
world.wait    <ticks>
world.screenshot <abs-path.png>
world.reload
```

`world.screenshot` only **arms** a capture. Fence it with `world.wait` and
confirm the `[capture] … -> <path>` line on **stderr** before reading the file.
Read-backs land on stdout, refusals and capture confirmations on stderr, so
capture both streams. Full verb surface, the offscreen recipe, and the
sculpting library are in `references/loop.md`.

Offline, the one structural check is:

```bash
dotnet run --project src/Puck.Cli -- creation stats --world <path> --prototype <id>
```

It validates the whole world and checks the stamp budget, then emits text-free
prototypes at unit placement scale to inspect static and pooled rest-pose clamps.
It also constructs the whole world's deterministic contact field, including solid
placements and screens, even with a prototype filter. Exit 1 can mean validation,
render emission, or contact compilation failure; read the diagnostic.
Text requires a resolved font atlas and reports clamp inspection unavailable.
A successful check does not prove live motion, contact parity, or appearance.

## Choosing geometry

The vocabulary is much wider than sphere-box-cylinder, and reaching for boxes
when a profile or a warp would do is what makes a sculpt read as blocky. Start
from what you want the surface to *do*:

| You want | Reach for | Notes |
|---|---|---|
| A softly swelling mass, squarish or round on demand | `Superellipsoid` + `exponent` 2..8 | 2 is an ellipsoid, 8 is nearly a box; the sleek middle is 2.5–4 |
| A slab with a shaped outline | `Prism` + `profile` | rounded rect, chamfered rect, ellipse, regular polygon, or an arbitrary convex hull |
| A tapering limb or fin | `Prism` + `taper` | 0 is a triangle, 1 is a rectangle |
| A tube, cable, horn, braid, or lock of hair | `Sweep` + `curve` | quadratic Bézier, tapered radius, bulge, up to 4 helical strands |
| A part that swells or pinches along its length | `flare` | a scale profile along one axis with a mid-span bulge |
| A leaning or S-curved mass | `shear` | polynomial offset of one axis driven by another |
| A local dent or swelling | `bumps` | up to 4 gaussian pushes |
| Softened edges | `rounding` (curved) or `chamfer` (45° bevel) | never both on one shape |
| A hollow shell | `onion` | |
| Repetition — ribs, teeth, plates, spokes | a `domain` fold | repeat, polar, symmetry, wallpaper; one fold, not N shapes. Refuses a `panel` on the same shape |
| Cellular or faceted relief — scales, chitin, hammered metal | `cells` | needs its own scope; see the scope rules |
| Erosion driven by live state | `erode` | see "Binding to state" |

Exact scale conventions per primitive (a torus is not what you would guess), the
facet-admission matrix, every ceiling, and every refusal message are in
`references/shapes.md`. Check it before authoring a primitive you have not used
here — `scale` means something different for a torus, a capsule, and a revolved
prism.

## Composition, and the two scope rules that bite

Blends run from `Union` through the smooth and chamfer families to the round
seams — `GrooveUnion` carves a recessed seam along the intersection curve of two
solids, `PipeUnion` lays a bead along it, and both have subtraction twins. Reach
for a groove when the desired seam follows that intersection; it can remove a
separate cutter shape. It still forces the creation-wide scope.

Two rules decide whether facets are even available to you:

1. **Per-shape scope.** `dilate`, `onion`, `panel`, `flare`, `shear`, `bumps`,
   `erode`, `cells`, and a non-uniformly scaled sphere or ellipsoid each make
   the shape request its own field scope. When that depth is already occupied,
   a warp's bound is shared at the enclosing group or creation pop; only an
   unscoped chain taxes the program-wide step scale. A shared conservative
   bound can make siblings march short; check clamps before diagnosing a soft
   image from this symptom alone.
2. **The creation-wide scope.** If a creation has a `noise` facet, an engraved
   text run, **or any shape blend outside `Union` and `SmoothUnion`**, the whole
   static creation emits inside one scope. The admission rule applies across
   placement kinds: `panel`, `trims`, and `cells` are refused on **every**
   shape in it. Pooled emission instead uses per-shape/group assembly; see
   [composition](references/composition.md) before moving a CSG sculpt to a body.

That second rule surprises people: one `Subtraction` anywhere costs you panels,
trims, and cellular relief everywhere. Groove and pipe do too. Keep the
creation all-union and use `panel`, use a trim where its own admission allows,
or split prototypes. In an already scoped creation, explicit cutter shapes can
replace panel shorthand; each costs shape budget, but no extra scope depth.
Groove and pipe blends reach deterministic field contact; panels and trims do
not. See [composition](references/composition.md) for shared-clamp diagnostics.

In pooled emission, `group` collects interacting shapes into one instance and
forwards their authored blends; ungrouped shapes use Union defaults. A group
opens a shared scope when needed, and grouped shapes cannot carry panel, trims,
or cells. Static emission ignores `group` entirely — it walks the list in order —
so a group cannot be used to keep a subtraction off its siblings on a static
placement.

## The budget

`MaxShapesPerStamp` is 367. The count is not the length of the shapes list:

- each shape counts 1, or **2** if it carries a `panel`
- **+2 per trim**
- **+ one per non-whitespace glyph** in every text run

`puck creation stats` prints it as `shapes: N, stamp budget: M/367`. When you
are over, reduce emitted shapes: fold repetition, remove panel/trim copies,
replace an appropriate cutter with a blend, or shorten text runs.
`detail: true` saves march and contact work, **not stamp budget**; detail shapes
are still charged.

## Surface and look

The palette carries far more than a color: `specular`, `roughness`, `metal`,
`coat`, `sheen`, `emissive`, plus `wrap` and `soften` for skin, `bounce` for a
warm interior fill, `weathering` for chips and scratches over a revealed
substrate, and `inset` for a refracted layer beneath the surface — which is how
an eye is authored, as color stops at radii rather than a dark sphere. Ranges,
defaults, what each does visually, and the lighting and environment fields that
make them read at all are in `references/surface.md`.

## Binding to state

Nothing in the renderer knows what health, charge, or speed are. A look row
carries up to four anonymous **render lanes**, each an expression over world
state. Only body registration supplies those lanes: an inhabited placement
needs a look, kit, and body capacity. Static, animated, and attached placement
registrations supply zero lanes. Use bare `hp` or `airPose[$body]` in a lane;
`state.hp` is driver/binding spelling and reads the wrong row here.
Shapes and materials read a lane by index:

- `shapes[].erode` erodes a shape away over a lane range, with a ragged noise
  front, and skips it entirely past the range — the plate that crumbles and goes
- `palette[].weathering` scales its chips and scratches by a lane, with a
  static floor for props
- a volume's intensity can ride a lane — the jet that answers throttle

Live erosion uses the body path's lanes. Static placements supply zero-valued
lanes, so their erosion resolves at zero; the prefix is emitted both inside a
shared scope and when the shape owns its scope. See
[composition](references/composition.md#the-three-scope-rules).

Author the meaning in the document's own cell names and the lane expression; the
engine only ever sees a number. A reversed `from`/`to` range runs the fold
backwards, which is how something *appears* as a lane rises.

## Making it move

`parent` is the skeleton: it names an **earlier** shape whose animated motion
carries this one, which is why chains resolve in one pass and can never cycle.
On top of that, `swings` rotate about a pivot and `slides` translate, both driven
by a named `driver` reading a signal or a state row. `chains` plus `effectors`
give CCD inverse kinematics with plant windows for feet. `frames` snapshot base
poses. Details, caps, and every refusal are in `references/rig.md`.

A shape carrying `domain` folds may not also carry a swing or slide — a fold
rides its parent's frame.

## Render-only versus deterministic

A solid placement requiring deterministic field contact refuses non-detail
`Polygon` and `Ellipse` prism profiles. `Revolve` and domain folds that cannot
expand are also refused where contact is owed. In contrast, `flare`, `shear`,
`bumps`, `erode`, panels, trims, cells, sweeps, and creation noise may validate
but are omitted from creation contact geometry (cells keep the base primitive).
These authoring omissions are distinct from low-level VM interpreter support.
See [shapes](references/shapes.md#contact-admission).

## Verifying

Structure first, then pixels:

```bash
dotnet run --project src/Puck.Cli -- creation stats --world <path> --prototype <id>
dotnet run --project src/Puck.Cli -- schema --check
```

Then run the game and look — a claim about how something reads is not verified
until you have looked at a capture. The offscreen and windowed recipes, with the
stream and fencing rules that keep them honest, are in `references/loop.md`.

## References

| File | Read it for |
|---|---|
| `references/shapes.md` | Every primitive's scale convention, profiles and lifts, which facets each admits, every ceiling and refusal message |
| `references/composition.md` | Blends with formulas, domain folds, groups, scopes, trims, panels, the budget arithmetic |
| `references/surface.md` | Palette fields, weathering, inset, render lanes, lighting, environment, tonemap, volumes |
| `references/rig.md` | parent/joint/swings/slides, frames, chains, drivers, effectors, parts, cameras, text runs |
| `references/loop.md` | Console verbs, capture recipes, `puck creation` verbs, the sculpting library for C# authoring |
