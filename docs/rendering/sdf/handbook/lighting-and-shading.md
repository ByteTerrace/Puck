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
the nearest occluder inside the light's cone over the point, the gap to it
times the light's authored penumbra half-slope
(`worldShadowPenumbraSlope(lightIndex)`) sizes the penumbra, and a filter of
that width answers. Both disks turn and space their
taps by the pixel's cell of the same 4×4 tile the ambient occlusion uses, and
views averages each light's visibility over the same plane-aware 4×4 window
(`sdfViewsFilter`), so a penumbra carries no per-pixel grain with or without
temporal reconstruction ([ambient occlusion](#ambient-occlusion-screen-space-obscurance)).
The window's shadow average also leaves out a neighbour whose normal turns more
than 60 degrees from the pixel's, because the surface pass leaves full
visibility on a side turned from the light, which it does not query: averaged
across the crease where a wall meets the ceiling, it would light a band along
the wall's top.
`world.debug-view shadow` shows the key light's filtered visibility. The
ambient term still fills shadowed regions, so shadows read soft and colored,
never black.

A wide filter on a single receiver depth shadows the receiver by its own
surface: a floor seen at a slant from the light lies nearer the light on one
side of the disk, and a curved receiver such as a shin curls toward it. Each tap
therefore compares against the receiver's own plane, extended to that tap's
texel centre from the hit triangle's normal (`sdfMeshShadowGap`); the triangle
is what the cascade holds, while the smoothed shading normal tilts away from it
across an edge the bake rounded. A filter tap sees the light when no caster
stands in front of that plane by 2.5 texels per texel it lies aside, so the
foot of a wall facing the light does not darken the floor before it; it hides
the light when its caster also lies inside the light's penumbra cone over the
receiver point itself; and a tap whose caster lies outside that cone says
nothing about the point and takes no part in the average. The cone is measured
from the point rather than the extended plane, which runs on past the corner
where a wall meets the slab over it, and the penumbra is sized by the nearest
blocker in the cone rather than the blockers' mean: under a soft sun, a slab's
top seen through a window beside higher floors would otherwise widen the
filter past the slab's own cone and light the wall under it.

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
light, capped by the tier (two at Medium, three at High); absent uses that cap.

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
  48×16×48 volume at High (128 rays a probe, three bounces), 2 m apart in a
  32×12×32 volume at Medium (96 rays, two bounces). The volume follows the first
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
  sends it, and which static point and spot lights its centre sees, one bit a
  record of the lights table. A hit's radiance is each directional light under
  a traced shadow ray and each static point or spot light under its windowed
  falloff, cone and a traced shadow ray, each scaled by its `bounce`, its
  emission, and, from the second sweep, the light it reflects from the probes
  about it, its sky included, which is the Feedback source. The sources mask
  and gains of `render.indirect.sources` apply as described above. A probe
  whose rays mostly meet back faces lies inside geometry and is never read.
  The volume a view reads holds each probe's cubes averaged with the
  neighbouring probes its centre sees (one half across a face, a quarter
  across an edge, an eighth across a corner, `SdfMeshIrradiance.FilterWeights`),
  so a sunlit patch a few rays catch does not show as a band; a neighbour
  behind a wall or inside geometry takes no part, and the light visibility bits
  are each probe's own. A probe also stores a depth cube, how far it sees
  along each axis before the first geometry. Rays escaping beside a blocker
  cannot extend its blocking depth. Its 26 neighbour rays retain their first
  hit planes too (normal and distance as four halves each); the plane along
  the receiver's nearest lattice direction rejects diagonal wall/roof corners
  that every axis ray misses. A plane tests the actual receiver, rather than
  comparing its distance to a ray travelling in a different direction.
- **A probe lights only what lies within its depth.** A read weights each of
  the eight probes about a point by whether the point, pushed off its surface,
  lies nearer along each axis than the probe's depth that way, fading over
  4 cm past it (`SdfMeshIrradianceProbe.DepthFade`,
  `sdfMeshProbeDepthVisibility`): a probe in the storey above a wall's top, the
  room beyond a partition or the void under a floor stops at the slab, wall or
  floor and lights nothing past it, which the preference for the probes in
  front of a surface cannot tell for a wall. The fade is thinner than the
  thinnest floor, because a point on a wall where it meets a floor or ceiling
  is pushed along the wall, level with the floor's surface, and a probe on the
  far side of that floor sees it only the floor's thickness past its depth; a
  softer fade would draw the far side's light along every junction. Where every probe about a point stands across a
  wall or slab from it, or no probe has traced, the point takes no indirect
  light. An axis ray is one line wide, so a
  window or doorway on a probe's axis lets its depth pass a wall the probe
  stands across from the point; each answering probe's weight therefore also
  fades out as the share of the other answering probes' weight it sees and is
  seen by along the lattice falls from three quarters to a quarter (the
  neighbour bits the trace records, `SdfMeshIrradianceProbe.NeighboursWord`,
  `MutualLow`, `MutualHigh`, `sdfMeshProbeMutualShares`), so a probe outside a
  room's corner, which sees in through a window above it and may hold the
  sunlit day, gives way entirely to the probes inside. The directional hit
  planes cover diagonal blockers, but architecture none of the sampled rays
  crosses still resolves at the lattice's coarseness.
- **Reading it.** Views (`shade/sdf-mesh-irradiance.hlsli`) gather the eight
  probes about the shaded point once (`sdfMeshProbesAt`), pushed 0.35 of a cell
  along its normal, weighted trilinearly, by how far each lies in front of the
  surface and by its depth. The sky term is the live sky's irradiance over π under the
  volume's sky visibility, so the sky can change without a retrace; the
  reflected term adds as stored. The sum replaces the harmonic sky ambient and
  the authored fill, under the `apply` intensity, tint and contact. A static
  point or spot light's visibility at the surface is the share of those probes
  whose centre sees it (`sdfMeshLightVisibility`), which scales its direct
  term, so a fixture lights nothing through the slab under it; an anchored
  light is never traced and goes unoccluded. Outside the volume, the lookup
  clamps to its boundary probes while their depth tests still use the actual
  receiver position. They extend only the light their visibility supports;
  the volume boundary adds no sky. `world.debug-view
  indirect` shows the volume's light alone. A window's view and a sky layer's
  view of another world read that world's own volume, at the tier its
  `render.indirect` authors, so its meter, fixtures and bounce are its own.
- **What an edit costs.** Probes are cached by cell, so a static world traces
  each cell once per lighting, and a camera returning to a place reads its
  cached probes at once. The scene is two-level: each distinct geometry (a
  mesh's positions and indices) holds its own hierarchy in the mesh's frame,
  and a top hierarchy indexes each draw's subtrees of at most 1,024 triangles,
  carrying the draw's transform and material base. A change of the static
  draws builds the scene from the one it replaces, so only geometry new to it
  builds (a moved, recoloured or renumbered draw builds none, and geometry a
  change set aside, such as a light switch's other slab, is kept for the next
  scene up to 8 million triangles), and retraces every probe; `world.counters
  sdf.mesh-gi` counts the builds and the geometry each built and reused. A
  change of a directional light, the gains, the
  sources, the bounces or what the materials reflect and emit (a bound
  color's move, a placement showing another creation over the same meshes, as
  the gallery hall's light switches do) retraces them over the same scene. Old probes keep
  drawing until new ones land. `puck mesh scene --world <file>` times the
  scene over a world's stored static meshes on the CPU: a full build, one
  sweep of a High window, and the update for one draw's new geometry, a move
  and a return. On the debug world's 250 stored static groups (16.9 million
  triangles) a full build takes about 1.2 s on the Ryzen 7 7800X3D and a
  one-draw change about 0.05 s. A change of a static point or spot light (a
  switch's power) retraces the probes, since their bounce and visibility bits
  carry it; an anchored light's move retraces nothing.
- **Limits.** Moving bodies receive the volume's light and cast none into it.
  Anchored lights and screens contribute no bounced light. The sky's bounced
  share is captured when a generation of probes traces, so drifting clouds
  change only the direct sky term. Leaks are held back by the back-face weight,
  the inside-geometry test and the read's preference for the probes in front
  of a surface (a probe more than a tenth of a cell behind its plane, past the
  wall or slab it belongs to, answers only where none lies in front); there is
  no per-probe visibility of the surfaces it lights, so a probe across an
  opening still sees both rooms. A fixture's occlusion has the probe spacing's
  resolution: a wall, a slab or a doorway resolves, a plinth's shadow does not.
- **Reflections.** A surface with a specular response reflects the
  lighting-visible sky about its mirror direction, blurred toward the sky's
  irradiance as the roughness widens the lobe, under the lobe's directional
  albedo (`sdfMaterialEnvironmentBrdf`, which keeps a rough dielectric near its
  `specular` at every angle); with the volume on, only as far as the probes
  about it see the sky open that way, and the light the surfaces about them
  send back where they do not (`sdfMeshReflection`), so a room reflects its
  walls rather than the sky outside it.
- **Exposure.** The volume is also the automatic exposure's meter: the
  luminance the probes about a view's camera see along their six axes, weighted
  toward where the camera looks. A scene ray from the camera to each probe
  excludes probes across a wall or slab. If no probe answers, the same tracer
  samples at the camera without writing that sample into the volume
  ([photometric units](#photometric-units-and-exposure)).

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
table's (directional, point and spot, rim and occluder, in authored order) that
the view shades, then every bound screen. Each is one `SdfLightSource`, and one
function, `sdfLightResponse` in `shade/sdf-light.hlsli`, answers what it adds at
the surface: a diffuse term that joins the radiance the material shade lights
by, a specular lobe (a directional's or a point's own, scaled by its
illuminance), a rim brighten, and a factor on reflected light (an occluder's
dimming). The stage sums each kind of term over the walk and adds each total
once, so the order the terms combine in is the walk's. No other kernel source
branches on a light's kind, so a new kind is one branch of that function.

A point or spot light falls off as the windowed inverse square
`saturate(1 − (d/r)⁴)² / max(d, 5 cm)²` (`sdfPointReach`), exactly zero at its
range `r`, and a spot's cone multiplies it by
`saturate(dot(−l, SpotAxis) + SpotOffset)²`; a static one (its record's
`Shadows`, `SdfLight.IsTraced`) is scaled by the irradiance volume's visibility
of it at the surface. The range is where the light shows
1/256 of display white on a white surface at the world's darkest exposure
(`SdfPhotometry.RangeOf`). Each view's pass block carries a mask of the table's
lights it shades (`SdfLightSelection`): a light whose sphere of range the view's
frustum cannot see changes none of its pixels and is skipped, so a world holds up
to 128 lights and a view pays only for those near what it shows.

## Photometric units and exposure

The frame shades luminance in nits. A light record carries its illuminance (a
directional's lux) or its luminous intensity (a point's or spot's candela) over
π, so an albedo ρ under an illuminance E shows ρE/π; an emissive material's
`emissive`, a sky layer's `luminance` and a display's `luminance` are nits; the
irradiance volume traces and stores nits. Each view's composite multiplies its
output by its exposure, the multiplier `1 / (1.2 · 2^EV100)` per nit
(`SdfPhotometry.ExposureOf`), before the root's tonemap. `render.exposure` holds
`EV100` fixed or adapts it: the meter reads the irradiance volume about the
camera, which carries every traced light's bounce, aims for
`log2(L · 100 / 12.5)` less the compensation within the bounds, and eases there
over the adaptation time of presentation time, snapping at a camera cut
(`SdfViewExposures`). A camera with no visible lattice probe measures its own
surroundings through the same radiance tracer. An unavailable scene or a camera
inside opaque geometry supplies no reading: exposure, local adaptation's meter
and sky visibility hold
their previous values, using the authored EV only before the first reading.
An automatic exposure's `local` share then adapts each
surface on its own in the views stage (`sdfLightStage`): the illumination
arriving at the surface, the lights' diffuse terms and the volume's indirect
light, is read against the view's meter reading taken as a scene of
`SdfExposure.LocalReflectance` albedo, and the surface's exposure moves that
share of the way from the view's toward the one that illumination would meter,
within `SdfExposure.LocalRange` stops, before the curvature and grid terms, so
a dark paint under the same light stays dark. The composite scales a display-referred view's sky share so
the sky's brightest texel (`skyPeakLuminance`, which the volume's lighting
measures over its environment map) reaches display white
(`SdfExposure.SkyPeakTarget`) rather than clipping; a view a window shows
leaves its sky to the viewer. The pass block carries the view's meter reading
(`meterLuminance`), the share (`localAdaptation`) and the sky's peak
(`skyPeakLuminance`).
`world.exposure` echoes the authored exposure and each view's `EV100`, multiplier
and meter reading, with `source=probes` or `source=camera-trace`. No measurement
reports `meter=none` and `source=held` (previous exposure) or `source=initial`
(no previous exposure). A view onto another world through a window, or a sky layer's
view, publishes its luminance raised by its own exposure to the window share
(`SdfViewSnapshot.Unexposed`, the pass block's `exposeShare`,
`SdfPhotometry.WindowAdaptation`), and the view looking through it applies the
rest of its own to the opening, so the window shows the world beyond at an
exposure between the two. The editor grid and an unbound screen's glass are
display-referred: they apply to the exposed colour. The curvature terms apply
to the exposed colour too, at the strength of the surface's exposed
illumination against a white surface lit as the meter aims for
(`SdfExposure.ReferenceWhite`, about a quarter of display white): the ridge
light and the ink colour scale by it, so a lit room keeps its edge highlights
and ink while a dim room's edges stay as dim as its walls. The lit
images store half floats, so a view holds its luminance below
`SdfPhotometry.LuminanceCeiling`.

## Glass

A palette slot's `glass` (`SdfGlass`) turns its shapes into a transmitting
surface the glass pass shades after the opaque image
([glass after the opaque image](frame-rendering.md#glass-after-the-opaque-image)).
Its vocabulary is the slot's own: `color` is the tint, the colour light takes
on through `depth` of the glass, `roughness` the frost, and `ior` the index of
refraction, which bends the read of the image behind and sets the Fresnel
reflectance (`((ior − 1) / (ior + 1))²`, 0.04 for window glass). The glass
pass lights the surface with the sky's environment map in the mirror direction
and every directional and point light's GGX highlight through that reflectance,
unshadowed; it reads none of the opaque epilogue's terms, no occlusion, no
cascade, no irradiance volume, no paint and no weathering. A glass shape in an
opaque group (a pooled creation's mixed `group`, which the document validator
refuses) shades as an opaque surface with the tint as its albedo.

## Screen lights and the CRT treatment

The reference scene's diegetic screens—the console cabinets' CRTs—are the most
distinctive light source in the engine, because each screen is *both a picture
and a light*.

As a **picture**, a bound screen is emissive: it is its own light source, like a
real display, so no scene lighting dims or tints it. A picture value of one
shows at the screen's peak luminance (`WorldScreen.Luminance`, nits; a window
onto another world shows that world's own luminance), and the view's screen
fidelity eases that to the value the exposure leaves as the picture itself. Its pixels are the
emulator's framebuffer, drawn from the screen's published mapping (the glass's
bezel inset, the layout, any letterbox and the crop) through a CRT glass-face
model—a soft bezel edge, soft cosine scanlines between the source's rows, an
aperture-grille phosphor-stripe tint one triad a source column, optional bloom
on the bright regions, and (both off by default) a vignette and a fresnel rim
glint. The scanlines and the grille fade out as a source row or column shrinks
toward two pixels, so they never alias against the pixel grid. The tuned look is a flat square tube: a hint
of CRT, not a heavy filter. A view's screen fidelity (`SdfViewSnapshot.ScreenFidelity`, 0 to 1) scales every
term of that treatment away and turns the soft bezel into a hard picture edge, and clears the atmosphere and
the tone map from the view, so a seat that has framed the screen it engaged sees its pixels exactly as the
source holds them; every other view carries 0.

As a **light**, every bound screen is a colored area light illuminating the
room, one of the lights the views pass walks. Its position and orientation come from the screen-surface table; its
color is the per-frame average luminance of what it's displaying—so a screen showing a
green field spills green onto the wall beside it. A face of luminance L and area
A casts `E = L · A · cos / (d² + A/π)` on a surface facing it, the exact
on-axis illuminance of a disc of that area. A `dot(screenNormal, −L)` gate
enforces "light through the glass": a screen only lights what sits in front of
its face, never behind it.

## Shading switches

### Cel shading

`world.toon on|off|toggle` selects a presentation-only cel response. With no
argument it echoes the choice. It uses the same render-section grant and
session-lever delivery as `world.ao`, and contributes no simulation state.
It starts off unless `render.toon.enabled` is true. Until the live lever is
used, reload follows the document default; afterwards the session choice
applies to the host, routed worlds and nested views, with each world's own
tuning. `world.save` folds the live choice into the render settings.

```text
render {
  toon {
    enabled: false
    threshold: 0.45
    softness: 0.08
    shadowTint [0.38, 0.48, 0.65]
    outlineWidth: 1.5
    outlineDistance: 12
    outlineNormal: 0.65
    outlineTint [0.08, 0.10, 0.16]
    specularSize: 0.025
    exposure: 0.5
    shoulder: 0.65
  }
}
```

The views pass bands each directional light's diffuse cosine and filtered
shadow visibility together. Point lights retain their range, cone and probe
visibility, with a banded diffuse cosine. A short soft terminator joins the
two tones; `threshold` places it and `softness` is its full cosine width.
Indirect light and sky ambient take the cooler linear RGB `shadowTint`,
with AO still affecting indirect light. This does not add unoccluded sunlight
inside a shadowed room. Specular lobes become bounded patches: `specularSize`
is one minus their half-vector cosine, with a narrow soft edge. Metals keep
their painted colour; continuous environment lobes and rim enrichment are
omitted in this mode.

Outlines compare the depth and normals of eight neighbouring visibility
records, on the foreground side of silhouettes and at sharp creases. The
tangent-plane depth comparison leaves sloping planes clear. `outlineWidth`
is the close radius in output pixels, scaled to the current render grid;
`outlineDistance` is the distance in metres at which it halves. Fractional
radii interpolate two rings, avoiding integer-width jumps. `outlineNormal`
is the crease cosine threshold; `outlineTint` multiplies reflected colour.
The ink is part of the lit surface before temporal reprojection and
atmosphere, so existing motion, disocclusion and history clipping apply.

`exposure` is reflected-light compensation in stops. `shoulder` compresses
the exposed RGB peak with one common multiplier, retaining paint chroma
before the world's authored tonemap. Emission is added after both this
compression and ink. Bound displays and portal pictures bypass the cel
response, and sky stays in its existing pass. A portal's destination shades
its own surfaces once; the consuming view does not posterise the picture.

There is no additional dispatch, image or history allocation: the work is
in `sdf.world$views` and reported with that pass's counters. An outlined
opaque hit reads at most eight neighbouring depths and normals at a radius
of one render pixel or less, sixteen above it. Width zero skips these reads;
mode off skips the cel response and all outline reads. The pass-block
parameters participate in the lighting cadence signature, so live edits
request fresh shading and temporal samples without rebuilding geometry.

### Lighting levers

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
pass estimates it from the interpolated vertex normals in world units
([surface pass](frame-rendering.md#one-render-pipeline)). Authored
`render.lighting.curvature` gains use that same estimate as edge weathering;
flat normals and meshes without vertex normals contribute zero. The rim light
and the ink colour follow the surface's exposed light
([photometric units](#photometric-units-and-exposure)), so they draw no light
lines in a dark room.

---

## Related resources

- How the bake measures gradients and where a view's normal comes from:
  [../reference/gradients-and-normals.md](../reference/gradients-and-normals.md).
- The occlusion, the shadow cascades and the curvature limits:
  [../reference/shading-ao-shadows.md](../reference/shading-ao-shadows.md).
- The field's scale and bound contracts:
  [../reference/lipschitz-and-field-correctness.md](../reference/lipschitz-and-field-correctness.md).
