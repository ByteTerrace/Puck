# Lighting and shading

After the mesh pass rasterizes a surface hit, the shading epilogue turns its
position, material id and normal into a lit pixel. Puck interpolates the baked
mesh's normal, estimates soft shadow penumbrae from cascades, and treats
diegetic CRT screens as both pictures and lights.
Runtime switches let a user isolate these terms for measurement or restyle the
frame.

An authored light's `bounce` is a finite nonnegative gain for its diffuse
contribution to indirect transport. It defaults to one and accepts the same
state bindings and section keys as the light's weight. Zero keeps the direct
light while removing its indirect source. Rim highlights and attenuation-only
lights have no diffuse source to scale. The packed light record retains its
48-byte stride; its last word holds this gain.

## The epilogue reads the visibility record

The mesh pass ([SDF frame rendering](frame-rendering.md)) answers one question
per pixel: *which baked triangle is nearest?* Everything in this chapter runs
**after** it. The surface pass writes each pixel's visibility record from the
mesh target: the ray parameter, the identity, the material, the triangle, the
draw's frame slot, the normal (the mesh's vertex normals interpolated, carried
to world space by the draw's inverse-transpose normal matrix, or the face
normal), a screen-space ambient occlusion and the cascade shadows of the stable
slots. The views pass computes materials and lighting from the record, read
once as a surface sample (`sdfLightStage` in `passes/sdf-light-stage.hlsli`).
No shading pass queries the field: the bake is the one GPU walk of the program,
and its gradients are the mesh's normals
([gradients and normals](../reference/gradients-and-normals.md)).

## Soft shadows: cascades and a penumbra filter

The first two stable shadow slots' directional lights cast shadows from the
mesh draws: `SdfMeshShadowCascades` fits two orthographic cascades along the
key light and one along the second slot's light, and the cascade passes draw
every mesh into them ([the shadow cascades](frame-rendering.md#the-shadow-cascades)).
The surface pass answers each light with a percentage-closer soft shadow from
the first cascade holding the point: a blocker search over a rotated disk finds
the mean depth of the occluders, the gap to them times the light's authored
penumbra half-slope (`worldShadowPenumbraSlope(lightIndex)`) sizes the
penumbra, and a filter of that width answers. Both disks turn and space their
taps by the pixel's cell of the same 4×4 tile the ambient occlusion uses, and
views averages each light's visibility over the same plane-aware 4×4 window
(`sdfViewsFilter`), so a penumbra carries no per-pixel grain with or without
temporal reconstruction ([ambient occlusion](#ambient-occlusion-screen-space-obscurance)).
`world.debug-view shadow` shows the key light's filtered visibility. The
ambient term still fills shadowed regions, so shadows read soft and colored,
never black.

A wide filter on a single receiver depth shadows the receiver by its own
surface: a floor seen at a slant from the light lies nearer the light on one
side of the disk, and a curved receiver such as a shin curls toward it. Each tap
therefore compares against the receiver's own plane, extended to that tap's
texel centre from the hit triangle's normal (`sdfMeshShadowGap`); the triangle
is what the cascade holds, while the smoothed shading normal tilts away from it
across an edge the bake rounded. A filter tap then counts a caster only inside
the light's penumbra cone over the receiver, since one outside it cannot shadow
the receiver, and within a texel and a half only one rising toward the light at
least 2.5 times as steeply as it lies aside, so the foot of a wall facing the
light does not darken the floor before it. The blocker search counts every
caster in front of the plane, so the penumbra estimate still sees them all.

Each light reads its own visibility from its K lane. A directional outside the
slots is unshadowed, scaled by the surface's ambient occlusion. During a
handoff, an outgoing light with cascade visibility `v` reads
`1 - (1 - v) * (1 - progress)`, scaling its own occlusion deficit, and the
incoming light, which holds no cascade until it takes the slot, lights
unshadowed. Neither radiance is crossfaded.

A cascade covers only the slice of the view's frustum it was fitted to and the
mesh draws inside its light camera's box, so an occluder outside it casts
nothing into frame, and a body thinner than a cascade texel can miss its own
shadow. The shadow tier sets the cascades' texels (2048 a side at `high`, 1024
at `medium`, 512 at `low`): the filter's widths are counted in texels, so a
lower tier casts softer, blunter shadows over the same reach.

## Indirect light controls

World boots at `render.indirect.tier`, Medium when absent; `off` and `high` are
explicit authored choices. `world.indirect` moves the live tier and `world.save`
retains it. The shared `quality.puck` presets select Off, Medium and High for
Low, Medium and High respectively. A preset's `indirect` member overrides that
mapping; applying a preset does not change the source gains or receiver controls.
The tier sizes the irradiance volume (below); Off reads no volume, and views
light with the authored fill instead.

`render.indirect.bounces` requests zero through four bounces after the direct
light, capped by the tier (one at Medium, two at High); absent uses that cap.

`SdfFrame.IndirectSources` carries one category mask. Its bits follow the five
categories: direct 1, feedback 2, emission 4, sky 8 and screens 16; the default
enables all five. World authors control `render.indirect.sources` with
`lights`, `emission`, `screens`, `sky` and `feedback`. Each is a bindable gain
in [0, 1], default one, using the ordinary scalar bindings and clock keys; an
explicit zero disables its category. A gain applies once where its source
enters the trace; feedback scales each reflected bounce. A gain or mask edit
retraces the volume's probes over the same scene.

`render.indirect.apply` holds bindable `intensity`, `tint` and `contact`, all
defaulting to one or white. These receiver-only values change no traced light.
Tint and intensity scale indirect diffuse after its source fold; contact blends
existing AO from no attenuation at zero to full attenuation at one. Enabled
physical Sky replaces harmonic ambient; disabling that source retains harmonic
ambient. Direct lighting, reflections, fog and the material's own emission are
independent. A material's `bleed` tints the light it sends into the volume, and
its `receive` scales the light it takes from it.

## Screen emission

Screen radiance comes from the acquired GPU image. The residency's environment
producer reduces each admitted image into one pixel-weighted whole-image mean,
summed over a fixed 4×4 grid of cells in source-pixel order, which supplies the
analytic room glow at the view's own surface. Same-world camera feeds emit zero;
independent sources and other-world views, a portal's window among them, can
emit. Missing and excluded images write zero records, and so does an image with
no known publication yet, such as a nested portal level whose glass is out of
view and has never rendered: it never holds the other screens' records. Every
actual image load and every written record is counted. The 512-byte residency
buffer, one record per screen slot, is shared by all views.

Worlds that show each other light each other through ordinary frame feedback:
each reduction reads the image the other world last completed, so the glow
settles over a few frames and follows a moving scene, and nothing holds a frame
for it. A capture sees the same lighting the display shows. Own-world cameras
remain outside lighting feedback. An authored zero screen-source gain stays
excluded.

## The mesh path's irradiance volume

The frame walks no field ([the bake](frame-rendering.md#the-bake)). Its
indirect light is an irradiance volume baked on the CPU from the
meshes the frame already draws (`SdfMeshIrradiance`, one per residency), because
worlds are mostly static and their evaluation belongs at edit time rather than
in the frame.

- **Probes.** A probe sits at the centre of a world cell: 1 m apart in a
  48×16×48 volume at High (128 rays a probe, two bounces), 2 m apart in a
  32×12×32 volume at Medium (96 rays, one bounce). The volume follows the first
  view's camera and is toroidal: a cell's probe lives in the slot its
  coordinates name modulo the volume's size, keyed by the cell, so a camera move
  rewrites only the slots of the cells it brings in, and views reject a slot
  holding another cell's probe.
- **The trace.** `SdfMeshIrradianceScene` gathers every static mesh draw (not
  dynamic) into a bounding volume hierarchy on the thread
  pool, once the mesh bake has settled. Probes trace on the pool, nearest the
  camera first, every probe's first sweep before any second. A probe stores two
  ambient cubes, one value per axis direction: the share of cosine-weighted sky
  its rays reach, and the cosine-weighted mean radiance the static geometry
  sends it. A hit's radiance is each directional
  light under a traced shadow ray, scaled by its `bounce`, its emission, and,
  from the second sweep, the light it reflects from the probes about it, its
  sky included, which is the Feedback source. The sources mask and
  gains of `render.indirect.sources` apply as described above. A probe
  whose rays mostly meet back faces lies inside geometry and is never read.
- **Reading it.** Views (`shade/sdf-mesh-irradiance.hlsli`) read the eight probes about the shaded point,
  pushed 0.35 of a cell along its normal, weighted trilinearly and by how far
  each lies in front of the surface. The sky term is the live sky's irradiance
  over π under the volume's sky visibility, so
  the sky can change without a retrace; the reflected term adds as stored. The
  sum replaces the harmonic sky ambient and the authored fill, under the
  `apply` intensity, tint and contact.
  Across the volume's last cell the light fades to the open sky.
- **What an edit costs.** Probes are cached by cell, so a static world traces
  each cell once per lighting, and a camera returning to a place reads its
  cached probes at once. A change of the static draws rebuilds the scene and
  retraces every probe; a change of a directional light, the gains, the
  sources or the bounces retraces them over the same scene. Old probes keep
  drawing until new ones land. On the debug world the scene is 7 draws and
  about 25,000 triangles, built in about 0.02 s, and the High volume's three
  sweeps (36,864 probes, 14.2 million rays) take about 5 s on four threads of
  the i5-8400.
- **Limits.** Moving bodies receive the volume's light and cast none into it.
  Point lights and screens contribute no bounced light. The sky's bounced share
  is captured when a generation of probes traces, so drifting clouds change
  only the direct sky term. Leaks are held back by the back-face weight and the
  inside-geometry test alone; there is no per-probe visibility.

## Ambient occlusion: screen-space obscurance

The surface pass estimates occlusion from the mesh target alone
(`sdfMeshAmbient` in `passes/sdf-surface-stage.hlsli`), within 0.75 metres or a
quarter of the hit's distance, whichever is smaller. Eight taps fall on a
golden-angle spiral over the disk that radius projects to on screen. Each tap
rebuilds the surface point its own pixel's ray reached and occludes by the
cosine of that point's elevation above the hit's tangent plane, past a small
bias and a lift of two pixel footprints, fading to nothing at the radius. A
point on the hit's own plane therefore never occludes it, however the disk falls
on the pixel grid, and a surface farther than the radius in front casts no halo.

The estimate is decorrelated per pixel and filtered. Each pixel turns and
spaces its taps by its cell of a 4×4 ordered-dither tile (`sdfMeshPattern`),
and a temporal view steps the pattern within the tile's strata by its sample
index. Views (`sdfViewsFilter` in `passes/sdf-views-stage.hlsli`) averages the 4×4 window
around each pixel, which holds all sixteen patterns once, keeping only
neighbors on the pixel's tangent plane, so no screen-locked pattern survives
with temporal reconstruction off, and a converged temporal view averages 128
patterns. `world.debug-view ao` shows the result.

Two rules give it its shape. It multiplies into **ambient light only, never
direct**—folding occlusion into the whole lighting equation causes ghosting;
the sun stays governed by the soft shadow above. And it sees only what the
camera sees: an occluder off screen or hidden behind a nearer surface occludes
nothing.

## Every light answers through one interface

The views pass lights a surface by walking one list of lights: the lights
table's (directional, point, rim and occluder, in authored order),
then every bound screen. Each is one `SdfLightSource`, and one function,
`sdfLightResponse` in `shade/sdf-light.hlsli`, answers what it adds at the
surface: a diffuse term that joins the radiance the material shade lights by, a
specular lobe (a point light's own), a rim brighten, and a factor on reflected
light (an occluder's dimming). The stage sums each kind of term over the walk
and adds each total once, so the order the terms combine in is the walk's. No other kernel source branches on a light's
kind, so a new kind is one branch of that function.

## Screen lights and the CRT treatment

The reference scene's diegetic screens—the console cabinets' CRTs—are the most
distinctive light source in the engine, because each screen is *both a picture
and a light*.

As a **picture**, a bound screen is emissive: it is its own light source, like a
real display, so no scene lighting dims or tints it. Its pixels are the
emulator's framebuffer, drawn from the screen's published mapping (the glass's
bezel inset, the layout, any letterbox and the crop) through a CRT glass-face
model—a soft bezel edge, soft cosine scanlines, an aperture-grille
phosphor-stripe tint, optional bloom on the bright regions, and (both off by
default) a vignette and a fresnel rim glint. The tuned look is a flat square tube: a hint
of CRT, not a heavy filter.

As a **light**, every bound screen is a colored area light illuminating the
room, one of the lights the views pass walks. Its position and orientation come from the screen-surface table; its
color is the per-frame average of what it's displaying—so a screen showing a
green field spills green onto the wall beside it. A `dot(screenNormal, −L)` gate
enforces "light through the glass": a screen only lights what sits in front of
its face, never behind it.

## Shading switches

Several shading terms are exposed as **live levers**—runtime settings a user
flips in `Puck.World` to isolate a term's cost or restyle the frame with no
restart. They ride reserved lanes in a per-frame parameter row, so an unset
frame uploads zero and every default is preserved.

| Verb | Effect | Why you'd use it |
|---|---|---|
| `world.shadows off\|low\|medium\|high` | `off` leaves every light unshadowed and renders or holds no cascade; the other tiers render the cascades at 512, 1024 or 2048 texels a side, a change of tier rebuilding each view's graph once | Isolates the shadow cascades and filter, and trades their memory and fill against sharpness |
| `world.ao on\|off` | `off` forces occlusion to 1; creases brighten | Isolates the occlusion's taps and its filter |

`SdfFrame` also carries a lane for disabling screen lights
(`DisableScreenLights`); no `Puck.World` verb sets it.

**Curvature shading** adds cavity darkening in creases, rim light on ridges,
and ink lines where curvature spikes, from the record's curvature. The surface
pass writes that curvature as zero, since the mesh carries none, so authored
`render.lighting.curvature` gains draw nothing.

---

## Related resources

- How the bake measures gradients and where a view's normal comes from:
  [../reference/gradients-and-normals.md](../reference/gradients-and-normals.md).
- The occlusion, the shadow cascades and the curvature limits:
  [../reference/shading-ao-shadows.md](../reference/shading-ao-shadows.md).
- The field's scale and bound contracts:
  [../reference/lipschitz-and-field-correctness.md](../reference/lipschitz-and-field-correctness.md).
