# Blends, folds, scopes, and the budget

## Blends

In static emission, `shapes[].blend` selects how a shape composes with
everything accumulated before it. In the pooled/body path, ungrouped shapes
are emitted with default Union and zero smooth; put interacting shapes in
the same nonzero `group` to carry their authored blends and radii. Groups
also affect ordering and scope ownership; inspect both emission paths.

`shapes[].smooth` carries the radius for every family that takes one,
clamped to [0, 0.5].

| Blend | Formula | Radius lane |
|---|---|---|
| `Union` | `min(a, b)` | — |
| `SmoothUnion` | polynomial smooth min | `smooth` |
| `Subtraction` | `max(a, −b)` | — |
| `Intersection` | `max(a, b)` — **against the whole accumulator**, not the previous shape | — |
| `Xor` | symmetric difference | — |
| `SmoothIntersection` | filleted intersection | `smooth` |
| `SmoothSubtraction` | filleted carve | `smooth` |
| `ChamferUnion` / `ChamferIntersection` / `ChamferSubtraction` | 45° bevel seam | `smooth` |
| `GrooveUnion` | `max(min(a,b), r − √(a²+b²))` — a round seam carved along the intersection curve | `smooth` |
| `PipeUnion` | `min(a, b, √(a²+b²) − r)` — a round bead along that curve | `smooth` |
| `GrooveSubtraction` | `max(a, −b, r − √(a²+b²))` | `smooth` |
| `PipeSubtraction` | `min(max(a,−b), √(a²+b²) − r)` | `smooth` |

`Morph`, `StairsUnion`, and `StairsSubtraction` exist in the blend enum but
compose **only** at a field-scope pop. The document accepts them on a shape
because they are defined enum values. Static shape emission, and pooled
group emission that forwards the blend, throws
`Blend operation '<blend>' is supported only as a PopField composition.`
Use the shape-level families listed above. Text-free `creation stats` catches
these failures when its selected emission path forwards the blend. An animated
ungrouped shape can still exit 0 because pooled emission silently uses Union;
the histogram records the authored enum and does not prove it was emitted.

A groove follows the two fields' intersection and can replace a separate cutter.
It does **not** restore panels, trims, or cells: groove and pipe blends require
the same creation-wide scope as subtraction. Both blend families are supported
by deterministic field contact; panel and trim shorthand is presentation only.

An `Intersection` annihilates every earlier non-overlapping shape, including a
ground plane, and its instance can never be culled by a sphere.

## Domain folds

`shapes[].domain` is up to four folds applied in creation space, after the
creation and placement frame and **before** the shape's own transform. A fold is
one shape drawn many times, not many shapes.

| `$type` | Fields | Contact geometry |
|---|---|---|
| `symmetry` | `normal`, `offset` | expands |
| `repeat` | `spacing`, `limit`, `origin` | only when `limit` is a whole number inside the copy budget; an absent limit is unbounded and does not expand |
| `polar` | `count`, `axis`, `mirror`, `materialStride`, `origin` | expands, one rigid copy per sector |
| `wallpaper` | `group`, `cell`, `limit`, `plane`, `materialStride`, `lodDistance` | **never — render only**; a solid row carrying one is refused by name |

Mirror a part with a `symmetry` fold, never with a negative scale — negative
scale components are refused by name because emission reads magnitudes.

A symmetry plane satisfies `dot(p, normal) = -offset`: the shader tests
`dot(p, normal) + offset < 0`. For a +Y normal and author y = 0.99, use
`offset: -0.99`.

A bounded integer repeat spans `-limit..+limit`, giving `2*limit+1`
cells per repeated axis. A limit of 12 gives 25, not 12. Even counts need a
composition, such as two disjoint odd repeats, an odd repeat plus one endpoint,
or symmetry over a correctly positioned half-pattern. Preserve positions as
well as the count.

Cell centres lie at multiples of spacing from `origin`; a primitive displaced
half a cell from that lattice can be sliced. Centre the fold on the primitive
in the fold's coordinate frame and leave enough cell width for its extent.
The current `CreationFrame` converts symmetry normals but leaves repeat/polar
origins unchanged, so for an unrotated placement an author-space position
`[x,y,z]` needs origin `[-x,y,-z]` to coincide with its engine-space centre.
Do not blindly copy a nonzero author X/Z position into `origin`.

A shape carrying folds may not also carry a swing or a slide, may not carry a
`panel`, may not be the reference of a trim, and may not be a chain bone: a fold
rides its parent's frame, never a solve or a swing of its own, and its fold
already owns the point, so a panel has no scope left to nest into.

That panel exclusion decides most budget questions, because folding repetition
and dropping panel copies are the two cheapest levers and they do not compose:
a panelled shape cannot be folded, so fold the unpanelled copies and leave the
panelled ones hand-placed.

## Panels and trims

Both are render-only detail that reuses a shape's own geometry.

`panel` is `{ inset, depth, material, face }`. It erodes a copy of the plate by
`inset`, then recesses it (positive `depth`, a subtraction) or raises it
(negative, a union) along `face`, which defaults to local +Z. `inset` may not
exceed the shape's smallest local half-extent; `|depth|` may not exceed the
eroded copy's full extent along the face.

`trims` is up to four `{ shape, width, material, inset }`. Each re-reads an
**earlier** shape's primitive geometry and pose and lays a band of `width` along
it. The referenced shape may carry neither domain folds nor a group, because a
trim's copy could not follow either. Both the host copy and reference copy
rebuild fresh primitive/pose chains without warp operations. On a warped host
or reference, the trim can paint an unwarped ghost shell. Use unwarped shapes
for both sides when the band must follow the rendered surface.

## The three scope rules

The builder allows exactly one level of field scope. Scopes may sequence, never
nest, and that single depth is what all three rules are competing for.

**1. A shape takes its own scope** when it carries any of `dilate`, `onion`,
`panel`, `flare`, `shear`, `bumps`, `erode`, `cells`, or is a non-uniformly
scaled sphere or ellipsoid. The scope clamps that shape's Lipschitz factor onto
its own candidate at the pop. If that shape is already inside a group or
creation scope, the enclosing pop owns the bound and its other members share
the clamp. Only an unscoped chain contributes to the program-wide step scale.
Shorter steps can affect convergence and the apparent hit threshold; the
symptom alone does not establish the cause.

**2. A group takes one scope for all its members** when any member wants one.
Because that consumes the depth, a grouped shape may not carry `panel`, `trims`,
or `cells`.

Only the pooled/body path reads `group` at all. Static emission walks the shape
list in declaration order and never looks at it, so on a static placement a group
isolates nothing and every `Subtraction` composes against the whole creation
accumulator — order each cut to be spatially local instead. `creation stats`
shows the difference directly: its `static:` line reports one scope over all
shapes where `pooled:` reports one per group.

**3. Static emission takes a creation scope** when it has a `noise` facet, an
engraved text run, **or any shape blend outside `Union` and `SmoothUnion`**.
The document admission rule uses that same predicate for all placements:
`panel`,
`trims`, and `cells` are refused on **every** shape in the creation.

A single `Subtraction`, groove, or pipe blend costs panels, trims, and cellular
relief throughout that creation. Keep `Union`/`SmoothUnion` and use `panel`,
use trims where admitted, or split prototypes. If already scoped, replace panel
shorthand with explicit cutters: they charge shapes, but require no extra depth.
Removing panel shorthand does not restore trims or cells while the outer
scope remains.

Warps (`flare`, `shear`, `bumps`, `erode`) accept the enclosing clamp;
`panel`, `trims`, and `cells` refuse by name when isolation is unavailable.
The silent part is admission, not inspection: `creation stats` reports
text-free static/pooled rest-pose scoped clamps, shared shape counts, and the
global step scale. `world.budget` reports the live program's scoped/shared
summary and worst clamp. Global scale 1 does not mean every scope is unit.
These are field bounds, not measured GPU timings; capture the running game to
judge appearance.

Erosion emits on both branches — the shape's own scope and the enclosing one —
so a shape inside a group or creation scope shares that pop's clamp rather than
losing the facet. What a static placement lacks is a lane to drive it: static,
animated, and attached registrations supply zero-valued lanes, so a static
erosion resolves at zero every frame. State-driven erosion belongs on an
inhabited placement for that reason, not because static emission drops it.

## The budget

`MaxShapesPerStamp` is 367. The count is not the list length:

| Contributor | Charge |
|---|---|
| a shape | 1 |
| a shape carrying a `panel` | 2 |
| each trim on a shape | +2 |
| each non-whitespace glyph in a text run | +1 |

Refusal: `<path> stamps <n> shapes, exceeding the 367-shape per-stamp budget.`

`puck creation stats` prints `shapes: N, stamp budget: M/367` plus histograms by
primitive and blend and counts by facet. When over budget, the cheapest wins in
order:

1. Replace hand-placed repetition with a `repeat` or `polar` fold.
2. Drop panel copies (+1) or trims (+2 each).
3. Replace a cutter with an appropriate seam blend, accepting its scope rule.
4. Shorten text runs.

`detail: true` excludes geometry from marches and contact, but does not reduce
the stamp count. It is a rendering-work lever, not a capacity lever.
