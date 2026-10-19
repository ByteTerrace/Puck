# Primitives, profiles, and per-shape facets

Source: `src/Puck.SignedDistance/SdfSolidPrimitive.cs`, `SdfSolidGeometry.cs`,
`SdfPrismProfile.cs`, `SdfLift.cs`; document side
`src/Puck.World.Authoring/Authoring/CreationDocument.cs` and the
`CreationCanonicalizer*.cs` family.

Every primitive has a unit size; `scale` multiplies it. Every axis is clamped to
a magnitude of at least 0.0001.

## What `scale` means

| Type | `scale` reads as | Unit size |
|---|---|---|
| `Sphere` | uniform: radius. Non-uniform: baked as per-axis ellipsoid radii | r = 1 |
| `Box` | half-extents | (1,1,1): `scale` is the half-extent on each axis exactly, its corners filleted inside that box by 0.04 of the smallest axis |
| `Torus` | uniform multiplier only; non-uniform rides a generic scale | major 1, minor 0.4 — band width is 0.4 × major |
| `Cylinder` | x/z radius (x must equal z to bake), y half-height | r = 1, half-height 1 |
| `Capsule` | x/z radius (x must equal z), y the cylindrical section length | total height is 2r + length |
| `Ellipsoid` | per-axis radii | (1,1,1) |
| `RoundCone` | uniform multiplier | lower r 1, upper r 0.5, height 1 |
| `Plane` | **scale-invariant**; local +Y is the outward normal | — |
| `Cone` | x/z base radius (x must equal z), y half-height | r = 1, half-height 1, apex sharp |
| `Prism` | extrude: x bottom half-width, y half-height, z extrusion half-depth. **Revolve: z is a radial offset, not an extent** | all 1; top half-width is `taper` × x |
| `Superellipsoid` | per-axis radii | (1,1,1) |
| `Sweep` | **must be uniform**; multiplies every curve length | the curve carries its own units |

A `Plane`'s influence is unbounded, so an instance carrying one can never be
culled by a sphere. An `Intersection` blend is evaluated against the whole
accumulator, not just the preceding shape, and is likewise unmaskable.

## Which facets each primitive admits

| Type | taper | profile | lift | rounding | chamfer | exponent | curve |
|---|---|---|---|---|---|---|---|
| Box | | | | | ≤ min(scale x,y,z) | | |
| Cylinder | | | | ≤ min(radial, axial) | ≤ min(radial, axial) | | |
| Cone | | | | ceiling is **0** — the apex is sharp | | | |
| Prism | [0,1], default 0.5 | yes | yes | see below | extrude only | | |
| Superellipsoid | | | | | | [2, 8] | |
| Sweep | | | | | | | required, only here |
| Sphere, Torus, Capsule, Ellipsoid, RoundCone, Plane | | | | | | | |

`rounding` and `chamfer` may never both be nonzero on one shape.

Prism ceilings by profile, then additionally capped at `scale.z` for an extrude:

| Profile | rounding ceiling | chamfer ceiling |
|---|---|---|
| Trapezoid (null) | trapezoid inradius | min(inradius, z) |
| RoundedRectangle | min(x, y) | min(min(x,y), z) |
| Polygon | min(x,y)·cos(π/sides) | 0 |
| Ellipse | min(x,y) − 1e-4 | min(that, z) |
| ChamferedRectangle | chamfered-rect inradius | 0 — the profile carries its own |
| Convex | 0 — `cornerRadius` **is** the rounding | 0 |

## Profiles and lifts

`profile` is `{ kind, cornerRadius, sides, vertices }` and is admitted on `Prism`
only.

| Kind | Parameters | Deterministic contact? |
|---|---|---|
| `Trapezoid` | uses the shape's `taper` | yes |
| `RoundedRectangle` | `cornerRadius` as a fraction of the smaller half-extent, [0,1] | yes |
| `Polygon` | `sides` in [3, 32] | yes (the convex polygon of its scaled vertices) |
| `Ellipse` | none | yes |
| `ChamferedRectangle` | `cornerRadius` as a chamfer fraction, [0,1] | yes |
| `Convex` | `vertices`, 3 to 32 points; `cornerRadius` rounds them uniformly | yes |
| `Path` | `path` contours; `cornerRadius: 0`; bounded line/Bezier/arc flattening, optional stroke or filled-outline shear | no — renderable only |

Path uses extrusion only, up to 128 compiled edges, and explicit normalized-space
tolerance. Its X and Y scale place the compiled edges, so a fill keeps its exact
outline at any scale, and a stroke stays a run of round disks whose radii scale
by the geometric mean of X and Y: a stroke stretched in one axis keeps its
width round rather than flattening with the stretch. Panel, trims (including a trim referencing a Path), rounding, and
chamfer are refused. Curves compile once into a packed edge table. Consult
`docs/rendering/sdf/reference/materials-and-primitives.md#curved-path-profiles`
for fields and semantics; `experimental/tests/Puck.Parity/paths.puck` is the forcing fixture.

A convex hull's points must lie in [-1, 1] on both axes, be distinct and
non-collinear, and wind **strictly clockwise**.

`lift` is `Extrude` (the default, along local Z to `scale.z`) or `Revolve` (about
local Y, with `scale.z` as a radial offset). A revolve is refused on any solid or
body-collider row, because a revolve's radial offset is not a per-axis box.

The rounding and chamfer ceilings above are `SdfSolidGeometry.MaxRounding` and
`SdfSolidGeometry.MaxChamfer`; a value past either is refused by name. A
chamfered `Box` emits as an extruded ChamferedRectangle and a chamfered
`Cylinder` as a revolved one. A trapezoid Prism uses the Trapezoid/Extrude VM
path in static and animated emission and in the deterministic contact field, and
a `Convex` profile is the exact iq polygon SDF.

`Superellipsoid` is 1-Lipschitz over the whole admitted exponent range, so it
never oversteps: `SdfProgramBuilder.Superellipsoid`'s remarks carry the
derivation and `SuperellipsoidLawTests` the numeric proof. It is exact only at
exponent 2 under a uniform scale; any other exponent or a per-axis scale is a
bound, whose gain `creation shape` prints (`distance=bound gain=0.832` for
exponent 3). Every ellipsoid, and every non-uniformly scaled sphere, emits it
at exponent 2.

Verify profile admission and surfaces with `AuthoredShapeAdmissionLawTests` and
`SdfTrapezoidProfileLawTests`.

## Every per-shape field

### Placement

| Field | Meaning | Refused when |
|---|---|---|
| `id` | stable integer id | duplicated |
| `name` | the handle `parent` and a trim's `shape` reference | — |
| `type` | the primitive | unrecognized |
| `position`, `rotation`, `scale` | author-frame transform; scale defaults to 1 | non-finite; **any negative scale component** — mirror with a symmetry domain op, not a negative scale |
| `parent` | name of a shape declared **earlier** | names no shape, or names a later one |
| `joint` | hinge point used only when an effector bone declares no swings | non-finite |

### Primitive parameters

| Field | Range | Null default |
|---|---|---|
| `taper` | Prism only, [0,1] | 0.5 |
| `profile` | Prism only | trapezoid |
| `lift` | Prism only | extrude |
| `exponent` | Superellipsoid only, [2, 8] | 2 |
| `curve` | Sweep only, required there | — |

### Field operations

| Field | Range | Behavior |
|---|---|---|
| `onion` | clamped [0, 0.2] | hollow shell of that thickness |
| `dilate` | clamped [0, 0.2] | inflates the field |
| `rounding` | Prism, Cylinder, Cone; ≥ 0 | curved edge |
| `chamfer` | Box, Cylinder, Prism-extrude; ≥ 0 | 45° bevel |

### Warps

Applied in this order on the local point: twist, bend, flare, shear, bumps,
erode, then the primitive — but **only the pooled/body path emits twist and
bend**. The static stamper's chain runs flare, shear, bumps and never emits
either, so on a static placement both fields are inert: no refusal, and
`creation stats` still prints a nonzero `twist:`/`bend:` count. Get a static
curve from a `Sweep`, a profiled `Prism`, or `shear` instead.

| Field | Shape | Range |
|---|---|---|
| `twist` | scalar, radians per unit local Y; **body path only** | clamped ±3 |
| `bend` | scalar, radians per unit local Y; **body path only** | clamped ±1.5 |
| `flare` | `{ amount, bulge, span, top, axis, startScale }` | amount [−0.9, 4], bulge ±1, span > 0, axis 0..2 default 1 (Y), startScale (0, 5] |
| `shear` | `{ linear, quadratic, cubic, target, driver }` | coefficients ±4; target and driver must be **distinct** axes in 0..2 |
| `bumps` | up to 4 × `{ center, radii, push }` | push magnitude ≤ 2; radii floored at 0.001 |
| `erode` | `{ lane, from, to, noise }` | lane 0..3; from and to must differ; noise defaults to 1 cell per unit |
| `cells` | `{ frequency, amplitude, seed, mode, randomness }` | frequency (0, 8]; amplitude [0, 4]; mode `F1` or `F2MinusF1`; randomness ≤ 0.46 (F1) or ≤ 0.2 (F2MinusF1); the relief slope amplitude × frequency (× 2 for F2MinusF1) must stay under 1, past which the surface pinches, folds over itself and sheds islands |

An erosion's range is consumed against the shape's **bound radius**
(`SdfSolidGeometry.Reach`), not its thinnest dimension, so a flat plate is
already fully gone a fraction of the way into its authored `from`/`to` window —
roughly a quarter of it for a thin plate. Back-solve the window from a measured
capture rather than assuming `to` is where the shape disappears.

A strong `flare` bulge placed far toward one end can break the field enough to
punch holes through the baked mesh. On the moth's pods, `amount: 0.3` with
`bulge: 0.8` opened an oval hole through two stacked plates, and the same flare
at `bulge: 0.6` baked whole. The validator accepts it, so look at a capture
after raising either value. For a part that widens steadily toward one end, a
linear `flare` (`amount: 0`, `startScale` below 1) with a matching `shear` to
keep one edge straight is safe.

A hollow shell (an outer shape with a smaller one subtracted) needs walls at least
about 4 cm thick to survive the mesh bake: the Moth's pod plates at 2.2 cm baked
with their backs missing, the core showing through, while the same shapes at
4.4 cm baked whole. The validator accepts the thin version, so look at a capture.

Creation contact emission omits `flare`, `shear`, `bumps`, and `erode`.
This is an authoring-path omission, not a statement that their low-level VM
operations all lack fixed-point interpreters. `cells` also leaves contact at
the base primitive. These fields are not generally refused by a solid row.

A `cells` facet additionally requires a closed primitive (never a `Sweep`), its
own scope, and a shape that is not detail-only.

### Sweep curves

`curve` is `{ a, b, c, radiusStart, radiusEnd, bulge, strands, twist, strandOffset }`
— a quadratic Bézier controlled by three points, with a tapered radius.
It passes through endpoints a and c, generally not b. At t = 0.5 it is
`(a + 2*b + c)/4`: to sag by d from the endpoint midpoint, displace b by 2d.

The points remain in the shape's local frame; the converted shape pose carries
them into engine space. They accept `state.<row>[.<key>]` vector bindings,
so a control point can be tuned through a state row.

A Sweep refuses domain folds, panel, trims, flare, shear, bumps, and cells.
Its shape scale must be uniform. Erosion has its own admission rules.

| Field | Limit |
|---|---|
| `radiusStart`, `radiusEnd` | finite and strictly positive; the taper `|end − start|` may not exceed 4 × the smaller |
| `bulge` | magnitude ≤ 16 × the larger radius |
| `strands` | 1 to 4; more than 1 is render-only |
| `strandOffset` | ≥ 0 and ≤ 2 × the larger radius |
| `twist` | finite, in turns along the curve |

The solid is the union of the spheres of radius `r(t)` swept along each
strand, and the field is the least gap to them over `t`, so the visible radii
are the authored ones. Strands closer than twice their radius fuse.

### Composition

| Field | Range | Null default |
|---|---|---|
| `material` | palette slot, clamped to [0, 32) | 0 |
| `blend` | see `composition.md` | `Union` |
| `smooth` | blend radius, clamped [0, 0.5] | 0 |
| `group` | ≥ 0 | 0, ungrouped |
| `domain` | up to 4 folds | none |
| `panel`, `trims` | see `composition.md` | none |

### Flags

| Field | Default | Meaning |
|---|---|---|
| `detail` | false | visual only — excluded from the contact field and colliders; the bake samples it like any other shape. Cannot combine with a panel or trims |

### Motion

`swings` (up to 8) and `slides` (up to 4); see `rig.md`. A shape carrying
`domain` folds may not carry either.

## Contact admission

- **Refused by name:** non-detail Path prisms when the solid row requires
  deterministic field contact; Revolve where contact is owed;
  folds that cannot expand within the contact copy budget.
- **Accepted but omitted:** creation contact emission skips sweeps entirely
  (including single-strand sweeps) and detail shapes; it omits flare, shear,
  bumps, erode, panel, trims, cells, and creation noise. Base primitives remain
  for the omitted facets. Author a separate admitted contact shape if needed.

A low-level SDF operation's CPU support does not guarantee that a creation's
contact compiler emits it. Conversely, a supported primitive or profile name
does not prove the whole transform chain is admitted: `SdfFieldEvaluator`
refuses a residual **nonuniform `Scale`** op, because a nonuniform scale carries
no distance.

Whether one appears is decided by the primitive, not by the solid row. Arms that
bake the authored dimensions into the shape emit no `Scale`; everything else
rides a generic `Scale(scale)`, nonuniform exactly when the authored scale is.
A uniform scale is always admitted, so this only bites an anisotropic shape.

| Bakes its dimensions | Rides a generic `Scale` |
|---|---|
| Sphere, Ellipsoid, Superellipsoid, Box, Plane | RoundCone |
| Torus when `scale.x == scale.z` (an elliptic section revolved) | Torus when `scale.x != scale.z` |
| Prism with any profile but Path (a Path prism scales its edges natively, but no fixed-point evaluator answers it at any scale) | none |
| Capsule and Cone when `scale.x == scale.z`; Cylinder at any scale (elliptic when `scale.x != scale.z`) | Capsule and Cone when `scale.x != scale.z` |

None of the right column is refused by name — each validates, renders, and fails
only when the contact field is constructed. For an anisotropic solid, prefer a
left-column spelling: every prism profile but Path takes its half-extents
natively, a Convex or Polygon one by scaling its vertices into place.

`creation stats` constructs the world's deterministic contact field through the
same builder used at boot. A residual nonuniform Scale in a solid placement
therefore exits 1 with a contact-inspection diagnostic. This check covers the
whole world even with `--prototype`; it does not demand contact support from
unplaced prototypes or non-solid placements.

## Reading a refusal

Validator messages name the field and the reason, and geometry refusals arrive
as `scale authors <reason>` at `shapes[i].scale`. Common ones worth recognizing:

- `an edge-rounding radius R past the C this shape can carry (the profile inradius, and for an extrude its half-depth)`
- `chamfer and rounding cannot both be nonzero on one shape.`
- `scale has a negative component; emission reads scale magnitudes, so the sign mirrors nothing — author a mirror as a symmetry domain op.`
- `a non-uniform scale on a Sweep, whose control points already carry creation-unit dimensions directly`
- `a cone whose radial scale … under the 0.003 the deterministic fixed-point field evaluator can distinguish from a point`
