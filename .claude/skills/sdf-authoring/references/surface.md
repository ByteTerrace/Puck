# Palette, weathering, inset, paints, lanes, and the render section

## The palette

`palette` holds at most 32 slots; `shapes[].material` is the index.

| Field | Range | Default | What it does |
|---|---|---|---|
| `color` | `#RRGGBB` or `state.<row>[.<key>]` | required | linear-RGB albedo; also the metallic reflectance tint |
| `emissive` | finite ≥ 0 | 0 | `albedo × emissive` added after shading, so it glows through shadow |
| `emissiveLane` | [0, 3] | absent | scales `emissive` by `saturate(lane)` at shading time, from each inhabited body's own lanes; absent keeps `emissive` static. A placement without lanes reads zero and stays dark |
| `specular` | finite ≥ 0 | 0 | dielectric reflectance at normal incidence. With `metal` also 0 the GGX term is skipped entirely |
| `roughness` | [0, 1] | 0.202 | 0 is a mirror-tight highlight, 1 the broadest lobe |
| `sheen` | [0, 1] | 0 | fresnel edge lift multiplied into the lit color — a painted catch along a bevel, never a halo |
| `metal` | [0, 1] | 0 | mixes reflectance toward albedo and scales diffuse by `1 − metal` |
| `coat` | [0, 1] | 0 | a second narrow lobe at fixed roughness 0.25 — lacquer over the primary specular |
| `wrap` | [0, 1] | 0 | widens the terminator; the skin and soft-surface control |
| `soften` | [0, 1] | 0 | blends the shading normal toward a wide-stencil gradient, smoothing pores and seams out of the lit normal without touching the silhouette |
| `fill` | `#RRGGBB` or a state binding | black | an artistic tint on the side the key light misses, scaled by ambient occlusion |
| `bleed` | `#RRGGBB` or a state binding | white | scales diffuse reflectance and emission sent into indirect lighting |
| `receive` | finite ≥ 0 | 1 | scales indirect light received by the surface |
| `weathering` | see below | absent | |
| `inset` | see below | absent | |
| `paints` | see below | absent | |

`roughness`, `sheen`, `metal`, `coat`, `wrap`, and `soften` are refused **by
name** outside [0, 1], and `emissiveLane` outside [0, 3]. There is no `shininess` member; it is refused as unmapped.
A palette slot has no `bounce` member either; it is refused as unmapped, and
`fill` is the artistic tint. A light's `bounce` is a separate transport gain.

Indirect transport uses the signed field gradient toward positive, free space.
The CPU reference, cache model and GPU march preserve that orientation at grazing
edges; facing it toward the incoming ray can point into the solid. A reflected
launch still needs a clear-interval certificate, and a failed certificate remains
unknown rather than becoming a black reference sample.

For painted metal with a clearcoat: a moderate `specular` around 0.35, a
`roughness` in the 0.25 to 0.45 band, `metal` near 0 for paint over metal or near
1 for bare metal, and `coat` around 0.3 to 0.5. None of it reads without
something to reflect — see the environment section below.

## Weathering

`palette[].weathering` reveals a substrate through the paint.

| Field | Range | Default |
|---|---|---|
| `edge` | [0, 1] | 0 | curvature-driven coverage |
| `lines` | [0, 1] | 0 | triplanar line-pattern coverage |
| `settle` | [0, 1] | 0 | coverage on upward-facing surfaces |
| `reach` | finite > 0 | 1 | curvature sensitivity of the edge mask |
| `seed` | uint | 0 | |
| `scale` | ≥ 0 | 1 | pattern frequency |
| `floor` | [0, 1] | 0 | coverage shown independent of the lane |
| `lane` | [0, 3] | 0 | which render lane drives the amount |
| `under` | 0 to 2 stages | null | what shows through |
| `deposit` | surface | null | the settle material |

A stage is `{ threshold, surface }` with thresholds strictly ascending in (0, 1];
a surface is `{ color, roughness, metal }`, all three required.

A threshold is compared against **coverage**, not against the lane, and coverage
is `saturate(amount × max(edge × edgeMask, lines × lineMask))` — so it can never
exceed `max(edge, lines)`. A stage above that ceiling never reveals: the document
validates, `creation stats` exits 0, and the frame is pixel-identical. With
`lines: 0.45` and no edge, a stage at 0.55 is dead. Keep thresholds inside the
reachable band, not merely inside [0, 1].

Two cross-rules the validator enforces together:

- `edge > 0` or `lines > 0` **requires** a non-empty `under` — there must be
  something to reveal.
- `settle > 0` **requires** `deposit`.

Both fail with one message: `Invalid inset, weathering or paint: check frames,
ordered stops, surfaces, ranges, lane [0, 3], a nonzero paint rotation, a positive
paint scale and a non-negative stroke.`

The live amount is `max(saturate(lane), floor)`. The mesh surface pass estimates
mean curvature from the world-space gradient of the triangle's interpolated
unit normals, before facing them toward the camera. A sphere of radius `r`
reads approximately `1/r`; flat triangles without vertex normals read zero.
The edge mask takes the positive side, scales it by `reach` and modulates it
with lattice noise. It follows the baked surface rather than sampling the
field, so unresolved bevels and split normals limit its accuracy. The pattern
rides joints through the winning dynamic transform.

For lightly worn armor, keep `reach` below the broad plate's radius so the
rounded body stays painted and its tighter edges chip. `lines` tiles a scratch
in every pattern cell; use a few parented paints for sparse, placed scuffs
instead of increasing that all-over pattern.

## Inset

`palette[].inset` puts a painted plane beneath a refracting surface. This is how
an eye is authored — as color stops at radii under a cornea, not as a dark
sphere.

| Field | Range |
|---|---|
| `origin` | finite point in world space for static geometry, or the winning dynamic slot's local frame |
| `rotation` | finite quaternion |
| `depth` | ≥ 0 — the plane sits at local `z = −depth` |
| `ior` | > 0; 1 samples the plane without bending the ray |
| `paint` | the radial ramp |

`paint` is `{ stops, softness, modulationAmplitude, modulationFrequency, seed }`
with 1 to 4 stops of `{ radius, color }`, radii non-negative and **strictly
increasing**. Softness feathers the boundaries; the modulation terms add radial
fibers.

The last stop extends to infinity; the base material colour does not reappear
outside the ramp. For a sclera, make it the last stop: pupil, iris, optional
limbus, then sclera, within the four-stop cap. A low modulation amplitude adds
fibres, and coat adds a corneal highlight.

Neither inset origin nor rotation is converted by `CreationFrame`. Work in
the shader's frame, not blindly in the shape's author frame. A useful eye
setup aims the plane's local +Z toward the viewer and places it behind the
visible front surface. The actual acceptance test after refraction is
`t = (-depth - p.z) / d.z >= 0`, with a nondegenerate ray and nonzero d.z.
There is **no universal depth < radius condition**: origin, orientation,
surface hit and viewing ray decide. Identity rotation can put the plane in
front of a front-view eye in world coordinates and cause negative t.

Each palette slot has one inset frame. A geometry symmetry fold does not
reflect that material origin. Mirrored eyes generally need separate slots
with separate frames; shared slots work only when their respective dynamic
frames make the same local inset correct.

## Paints

Surface detail that is colour rather than form (a stripe, a trim, a mouth line,
a panel) is a paint, never a sculpted copy proud of its plate: relief under the
bake's cell (5 mm refined, 15 mm on smooth parts) frays into jagged steps and
speckled material. `palette[].paints` holds up to eight paints, each drawn over
the one before on every surface of the slot. A paint is one primitive of the
shape vocabulary placed like a shape; the surface takes `color` inside it, or,
with a `stroke`, within the stroke of its surface, the edge antialiased over the
pixel's footprint. The views pass draws it at the hit alone, so it never changes
the field or the bake.

```puck
paints [
  {
    parent: "shin-left"          // rides that shape's frame, in its local frame
    type: "Prism"
    position [0, -0.29, 0]
    scale [0.174, 0.174, 0.45]
    profile { kind: "Ellipse" }
    stroke: 0.023                // absent or zero fills the primitive
    color: "#9D66C6"
  }
]
```

| Footprint | Paint |
|---|---|
| A stripe across a part | `Plane`, stroked at the half-width; local +Y is its normal |
| A ring, horseshoe or polygon outline on a curved surface | an extruded `Prism` profile (`Ellipse`, `Polygon`, `Convex`, rounded rectangle), stroked: the outline projected along local Z; `scale.z` is the half-depth and bounds which side of the part it reaches |
| A filled panel | the same prism, unstroked |
| A curved line (a mouth, a seam) | a `Sweep`, unstroked, its curve on the surface and its radius the line's half-width |

Fields: `type` (every primitive; a `Path` profile is refused), `color`
(`#RRGGBB` or a state binding), `position`, `rotation`, `scale` (absent: origin,
identity, one), `stroke` (finite ≥ 0), `parent`, and the shape fields
`taper`, `profile`, `lift`, `rounding`, `chamfer`, `exponent` and `curve` (a
`Sweep` requires a curve, and only a `Sweep` carries one). The primitive takes a
shape's own admission.

**Frames.** A paint with a `parent` is authored in that shape's local frame, the
frame a shape-riding volume uses (curve points and text runs ride it the same
way), and rides the shape's own dynamic slot, so it moves with the part whatever
slot the hit's mesh rides; the parent may not carry a domain fold. Because a
parented paint colours only the surface near it, two parts can share a slot
(the Moth's two shins share `shinCream`); a projected paint still colours every surface of
its slot the projection crosses, so a part takes its own slot when a neighbour of
the same colour lies in the projection (the shins' rings would paint the boots). A
paint without a parent is in the frame of
the slot that owns the hit: the slot its baked group rides, which sits at the
group's first shape's authored position with that shape's axes, or world space
for static geometry. A static placement shares one palette across its
placements and draws no parented paint. To find a frame, paint a test stripe and
look before trusting the numbers.

## Render lanes

`looks.rows[].motion.lanes` holds **up to four** expressions in x, y, z, w
order. Missing/null entries read zero. Only body registrations receive the
look's lanes; an inhabited placement needs a look selecting its creation, a
kit, a body motion program, and body capacity. Static, animated and attached
placement registrations do not supply lanes, even when they have a dynamic
slot.

```json
"motion": { "lanes": ["(100 - hp) / 100", "airPose[$body]"] }
```

Lane expressions read **bare row names**, such as `hp`; keyed reads can use
`airPose[$body]` or `airPose.$body`. The evaluator adds `state.` itself.
`bodyState($body, flightFuel)` reads one of the wearing body's action-state
slots (`state.body` or `state.identity`, the slots kit predicates and hold
`spend` use), and `bodyState(body, 0, flightFuel)` body 0's; the value is the
one the latest snapshot carried, a counter's value or a timer's remaining
seconds, never eased. A lane naming an undeclared slot is refused at
validation.
The `state.<row>[.<key>]` spelling belongs to driver signals and document
value bindings, not lane expressions.

The runtime lane evaluator supports constants, state reads, negation,
`abs`, `sign`, +, -, *, /, `min`, `max`, and `clamp`. The parser's
broader comparison/bit/state vocabulary is not implemented here and evaluates
to zero. Syntax errors refuse, but a syntactically valid missing row/key reads
zero; divide-by-zero or unsupported operations also return zero. Verify the
actual row and value, not just that the document validates. State reads are eased.

For staged damage, either use one damage lane with different erode ranges, or
stage separate lane expressions over a common range. Check healthy and depleted
endpoints and sample the intervening hp values: every plate must transition in
order, and no hp value should leave all three mid-erosion.

Four consumers address a lane by index, each validated to [0, 3]:

| Consumer | Spelling | Reads the lane |
|---|---|---|
| shape erosion | `shapes[].erode = { lane, from, to, noise }` | in the bake: frozen at the value the bake read |
| material weathering | `palette[].weathering.lane` | at shading time, every frame |
| material emission | `palette[].emissiveLane` | at shading time, every frame |
| volume intensity | `volumes[].intensityLane` | at shading time, every frame |

Every view draws the residency's baked meshes, and erosion is geometry: the
bake samples the lanes the frames hold while a new program bakes (at boot, or
after an edit that rebuilds the program), and the mesh it bakes keeps drawing
until the next program (`SdfMeshPathBake`). A lane change alone starts no bake, so **erode cannot show or
hide a part live**; at boot the lanes usually read 0, and a part that only
appears as a lane rises bakes empty (`no surface after 0 refinements` in the
bake log). Use the shading-time consumers for live state: `emissiveLane` turns a
glow on and off, `weathering.lane` drives wear.

Nothing in the renderer knows what the number means. Put the meaning in the
state row's own name and in the lane expression.

## Volumes

`volumes` is up to 64 bounded media per creation (`SdfProgramBuilder.MaxVolumes`):
emissive `flow` media and `cloud` density volumes. Presentation only — no field,
no collider.

| Field | Range | Default |
|---|---|---|
| `kind` | `"flow"` or `"cloud"` | required |
| `position`, `rotation`, `halfExtent` | finite; half-extent positive on every axis | required |
| `ramp` | 1 to 4 `{ density, color }` stops, density strictly increasing in [0, 1], color `#RRGGBB` only | required |
| `parent` | the `name` of a shape in the same creation | null, the creation root |
| `axis` | > 0; flow only | 2 × halfExtent.Y |
| `width` | > 0; a flow's radius at the mouth, a cloud's noise-cell size | halfExtent.X |
| `spread` | ≥ 0; flow only: the radius at the far end as a share of `width` (0 tapers to a point, 1 a straight column, > 1 a widening cone) | 0 |
| `fade` | ≥ 0; flow only: density falls by the remaining axial share to this power (0 holds, 1 linear, 2 faster) | 1 |
| `noiseScale` | > 0; flow only: the noise-cell size, so a narrow jet stays continuous | `width` |
| `noiseDepth` | [0, 1]; flow only: the share of the density the noise modulates; below 1 the axis at the mouth reaches density 1, so the ramp's top stop draws a core | 1 |
| `speed` | finite | 1 |
| `seed` | uint | 0 |
| `steps` | [8, 64] | 32 |
| `intensity` | ≥ 0 | 1 |
| `extinction` | ≥ 0 | 1 |
| `pulseAmplitude` | [0, 1] | 0 |
| `pulseFrequency` | ≥ 0 | 0 |
| `intensityLane` | [0, 3] | null |
| `enabled` | bool; a disabled volume is omitted from emission | true |
| `coverage` | [0, 1]; cloud only | 0.55 |
| `softness` | (0, 1]; cloud only | 0.18 |

A volume attaches to a **shape by name**, and inherits that shape's live frame.

## The render fields a look depends on

`render` is a world-document section, not part of a creation, and
[`puck-world`](../../puck-world/references/documents-render.md#render--the-render-defaults)
owns it: lights and their shadow slots, curvature ink, the sky stack,
atmosphere, quality levers and keyed timelines. A creation author needs these
few facts from it:

- Specular, `metal` and `coat` reflect the shared sky map. A studio look needs
  something bright to reflect: a sky `panel` layer is an analytic rectangular
  highlight (`direction`, angular half-extents `size` in radians, `color`,
  `intensity`, `blur`), lighting-only by default, so it shows in reflections
  without drawing in the sky. Unequal half-extents make strip highlights.
- `render.environment` carries nonnegative `ambient` and `reflection` gains,
  each defaulting to one; zero turns that contribution off.
- At most 8 lights. A point light falls off as
  `weight / (1 + (distance/radius)²)` and casts no shadow; an `anchor` lets a
  light ride a body or a placement.
- `render.tonemap` is `none` (the default) or `filmic`.
- `world.lighting` echoes the whole lighting, sky, environment and tonemap
  state in one line each; an unauthored field reads `default`, the engine's
  pinned value for that kind, not zero.
