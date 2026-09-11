# Palette, weathering, inset, lanes, and the render section

## The palette

`palette` holds at most 16 slots; `shapes[].material` is the index.

| Field | Range | Default | What it does |
|---|---|---|---|
| `color` | `#RRGGBB` or `state.<row>[.<key>]` | required | linear-RGB albedo; also the metallic reflectance tint |
| `emissive` | finite ≥ 0 | 0 | `albedo × emissive` added after shading, so it glows through shadow |
| `specular` | finite ≥ 0 | 0 | dielectric reflectance at normal incidence. With `metal` also 0 the GGX term is skipped entirely |
| `roughness` | [0, 1] | 0.202 | 0 is a mirror-tight highlight, 1 the broadest lobe |
| `sheen` | [0, 1] | 0 | fresnel edge lift multiplied into the lit color — a painted catch along a bevel, never a halo |
| `metal` | [0, 1] | 0 | mixes reflectance toward albedo and scales diffuse by `1 − metal` |
| `coat` | [0, 1] | 0 | a second narrow lobe at fixed roughness 0.25 — lacquer over the primary specular |
| `wrap` | [0, 1] | 0 | widens the terminator; the skin and soft-surface control |
| `soften` | [0, 1] | 0 | blends the shading normal toward a wide-stencil gradient, smoothing pores and seams out of the lit normal without touching the silhouette |
| `bounce` | `#RRGGBB` or a state binding | black | a tint added on the side the key light misses, scaled by ambient occlusion |
| `weathering` | see below | absent | |
| `inset` | see below | absent | |

`roughness`, `sheen`, `metal`, `coat`, `wrap`, and `soften` are refused **by
name** outside [0, 1]. There is no `shininess` member; it is refused as unmapped.

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

Two cross-rules the validator enforces together:

- `edge > 0` or `lines > 0` **requires** a non-empty `under` — there must be
  something to reveal.
- `settle > 0` **requires** `deposit`.

Both fail with one message: `Invalid inset or weathering: check frames, ordered
stops, surfaces, ranges, and lane [0, 3].`

The live amount is `max(saturate(lane), floor)`, and the edge mask comes from the
level-set mean curvature measured at the hit, positive side only, scaled by
`reach` and modulated by lattice noise. So chips appear where the geometry is
actually convex — no per-part authoring, and it rides joints because the pattern
follows the winning dynamic transform.

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

Three consumers address a lane by index, each validated to [0, 3]:

| Consumer | Spelling |
|---|---|
| shape erosion | `shapes[].erode = { lane, from, to, noise }` |
| material weathering | `palette[].weathering.lane` |
| volume intensity | `volumes[].intensityLane` |

Nothing in the renderer knows what the number means. Put the meaning in the
state row's own name and in the lane expression.

## Volumes

`volumes` is up to 8 emissive flow media per creation, presentation only — no
field, no collider.

| Field | Range | Default |
|---|---|---|
| `kind` | `"flow"` only | required |
| `position`, `rotation`, `halfExtent` | finite; half-extent positive on every axis | required |
| `ramp` | 1 to 4 `{ density, color }` stops, density strictly increasing in [0, 1], color `#RRGGBB` only | required |
| `parent` | the `name` of a shape in the same creation | null, the creation root |
| `axis` | > 0 | 2 × halfExtent.Y |
| `width` | > 0 | halfExtent.X |
| `speed` | finite | 1 |
| `seed` | uint | 0 |
| `steps` | [8, 64] | 32 |
| `intensity` | ≥ 0 | 1 |
| `extinction` | ≥ 0 | 1 |
| `pulseAmplitude` | [0, 1] | 0 |
| `pulseFrequency` | ≥ 0 | 0 |
| `intensityLane` | [0, 3] | null |

A volume attaches to a **shape by name**, and inherits that shape's live frame.

## The render section

`render` is a world-document section, not part of a creation, but a look does not
read without it.

### Lighting

At most 8 lights, and **at most one** may set `shadows: true`.

| `$type` | Fields |
|---|---|
| `directional` | `direction` (nonzero), `color`, `weight` ≥ 0, `angularRadius` in [0, atan(0.3)], `shadows` |
| `hemisphere` | `color`, `base` ≥ 0, `gradient` |
| `rim` | `color`, `weight` ≥ 0, `power` ≥ 0 |
| `point` | `position`, `radius` > 0, `color`, `weight` ≥ 0, `anchor` |
| `occluder` | `position`, `radius` > 0, `weight` in [0, 1], `anchor` |

A point light falls off as `weight / (1 + (distance/radius)²)` and casts no
shadow. An `anchor` must name an entity, an entity part, or a placement frame,
so a light can ride a body.

`render.lighting.curvature` adds `cavity`, `rim`, `ink`, `inkLow`, `inkHigh`, and
`inkColor`; `inkLow` must be strictly below `inkHigh`. All three gains at zero
closes the gate.

### Environment

`render.environment.softboxes` is up to 4 `{ direction, size, color, weight,
blur }`, with `direction` nonzero and both `size` axes strictly positive, plus
`horizon.low` and `horizon.high`. This is what a `coat` or a high `metal`
actually reflects — without softboxes an automotive material has nothing to
catch and reads flat. A softbox uses `length(size)` as its angular radius;
its footprint is isotropic. Unequal size axes do not produce a strip highlight.

`render.tonemap` is `None` or `Filmic`.

### Sky

`render.sky` layers, each kind at most once, composited gradient, stars, sun
disc, then clouds. A `gradient` needs 2 to 4 stops with `elevation` in [-1, 1]
strictly ascending. `fog` carries `density` per world unit. `sunDisc` indexes a
directional light slot. `stars` and `clouds` carry their own density, seed, and
motion terms.

### Everything else

| Field | Range | Default | Read-back |
|---|---|---|---|
| `shadows` | `Off`/`Low`/`Medium`/`High` | `Off` | `world.shadows` |
| `shadowCrowdRadius` | 0..100 | 0 | `world.shadows` |
| `ambientOcclusion` | bool | false | `world.ao` |
| `renderScale` | `Native`..`Eighth` | `Native` | `world.render-scale` |
| `upscaleSharpness` | [0, 1] | 0 | `world.upscale-sharpness` |
| `farDistance` | [1, 8192] | 40 | `world.budget` |
| `tonemap` | `None`/`Filmic` | `None` | `world.lighting` |
| `cycle` | a state row plus ≥ 2 keys at ascending `at` in [0, 1) | absent | `world.lighting` |

`world.lighting` echoes the whole lighting, curvature, sky, environment, tonemap,
and cycle state in one line each. An unauthored field reads `default`, meaning
the engine's pinned value for that kind — not zero.
