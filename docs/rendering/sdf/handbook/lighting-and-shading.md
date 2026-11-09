# Lighting and shading

After the mesh pass rasterizes a surface hit, the shading epilogue turns its
position, material id and normal into a lit pixel. Puck interpolates the baked
mesh's normal, estimates soft shadow penumbrae from cascades, and treats
diegetic screens as both pictures and lights.
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
visibility on a side the light's response does not reach, which it does not
query: averaged across the crease where a wall meets the ceiling, it would light
a band along the wall's top.

The surface pass queries a light wherever its diffuse response is non-zero. For
a plain material that is the side facing the light; a wrapped material
(`wrap`, `max((n·l + wrap) / (1 + wrap), 0)`) takes light past the terminator
until the cosine reaches `-wrap`, so the pass reads the material's wrap
(`sdfMaterialShadowDemand`) and queries that side too: an unqueried slot decodes as
fully visible, which would light a skin's far side with the sun under a roof. Past the
terminator the receiver's own lit side stands in front of it toward the light,
so each shadow cascade holds two layers: its depth, the first face the light
meets, and its exit layer, the first face where the light leaves a solid. A
triangle facing the light compares against the depth; one turned from it
compares against the exit layer, where its own face is the first exit unless
another solid stands in front of its body. The exit layer draws a draw that
encloses a solid (`SdfMeshDraw.EnclosesSolid`: a baked mesh, or an authored
mesh whose triangles close a volume, unclipped and unmirrored) with its faces
toward the light culled, and every other draw (an authored sheet, a body
clipped at a portal's glass, a mirrored draw) whole, as a thin caster. The
wrap's light survives on a body in the open however thin or thick it is, and a
roof, a wall or a single sheet farther off still shadows it; a thin caster's
own wrapped side is shadowed by its own lit side.
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

`render.indirect.bounces` zero switches reflected bounce off; any other value (or
none) leaves it on, and bounce converges over successive relights of the
volume rather than stopping at a fixed depth.

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
  48×16×48 window at High, 2 m apart in a
  32×12×32 window at Medium, each probe 144 rays. Two coarser levels nest
  about it at every tier (`SdfMeshIrradiance.LevelsOf`): a storey level of
  4 m × 2 m × 4 m cells over 32×24×32, a 128 m × 48 m × 128 m window whose 2 m
  vertical spacing stands a probe plane in every storey 2.4 m or taller, and a
  32×12×32 window of 32 m cells reaching 1,024 m. Each level's window centres on the cameras of every view
  the residency renders when they fit in its middle, and on the first view's
  alone when they do not, and is toroidal: a cell's probe lives in the slot its
  coordinates name modulo the window's size, keyed by the cell, so a camera move
  rewrites only the slots of the cells it brings in, and views reject a slot
  holding another cell's probe.
- **The trace.** `SdfMeshIrradianceScene` gathers every opaque static mesh draw
  into a bounding volume hierarchy on the thread pool. While the bake still
  lands opaque static groups (`SdfMeshPathBake.IrradiancePending`), the scene is
  built from those landed so far once the previous scene's trace has finished,
  at most every few seconds, so the volume and the meter answer during a cold
  bake; a lockstep frame waits for the whole bake. Dynamic groups, glass and
  diagnostic slices do not enter that scene. A probe's measurement is a hit
  buffer, one closest hit per octahedral texel at full reach, traced once per
  geometry; its light is a relight of those hits under the frame's lighting.
  Probes are worked on the pool, untraced ones first (finest level and nearest
  the camera first), then those whose lighting is oldest. A probe stores two
  ambient cubes, one value per axis direction: the share of cosine-weighted sky
  its misses see, and the cosine-weighted mean radiance the static geometry
  sends it, and which static point and spot lights its centre sees, one bit a
  record of the lights table. A hit's radiance is each directional light under
  a traced shadow ray and each static point or spot light under its windowed
  falloff, cone and a traced shadow ray, each scaled by its `bounce`, its
  emission, and the light it reflects from the probes about it, its sky
  included, which is the Feedback source; that bounce converges over
  successive relights. The sources mask and gains of `render.indirect.sources`
  apply as described above. A probe
  stands at its cell's centre unless that lies inside geometry or within
  `SdfMeshIrradianceProbe.Clearance` (0.1 m) of a surface: then the trace moves
  it to the nearest point of its cell that stands clear, carries the offset in
  its record, and every read (direction, distance, depth tests) uses where it
  stands while its trilinear share stays the cell's. A probe no point of its
  cell frees (its rays mostly meet back faces wherever they start) is invalid and
  never read; its share goes to the corners that answer. `world.probe <x> <y>
  <z> [<nx> <ny> <nz>]` reads the eight probes about a point: where each stands,
  its inside share, validity, cubes and the terms of its weight.
  The published volume holds each probe's own measured sky and radiance cubes;
  neighbours never supply its sky or radiance. Sky, surface radiance and the
  surface map come from the same hits, sharing one quadrature (each texel
  weighted by its solid angle) across cells so a uniform field gains no lattice
  noise. Each probe stores a 12×12 octahedral first-hit surface map, one word
  per texel: ray distance and compact normal, the hits within the receiver
  support radius. Negative distances mark misses to that radius.
- **A probe lights receivers its surface samples support.** Four reflected
  angular taps reconstruct compatible local patches and test the signed gap from
  their surfaces. This keeps flat and curved receivers visible, including grazing angles,
  where interpolating radial distances incorrectly shadowed it. Misses never
  inflate a blocker. Blocked angular coverage closes smoothly; a minority hit
  tap cannot extend a finite block over the opening. There are no whole-sphere
  lattice-ray planes, front/back fallback or neighbour separation vetoes.
  Gather multiplies visibility by trilinear share and a smooth distance fade from
  zero on/behind the receiver's tangent plane to one half the cell's extent along
  the normal in front (half the spacing for a cube). A squared
  cosine concentrated near-floor probes into repeated spots. Normal bias is 5 cm;
  surface fade is 2 cm, independent of spacing.
  Support ends at 1.5 cell diagonals plus normal bias, while sky still traces to
  lighting reach. Subtexel geometry remains a sampling limit. Blocked taps close
  by one rule at every level: taps on solids only as a majority, taps on sheets
  at almost any share (from zero, complete at an eighth). A coarse level's trace
  marks a hit a sheet when the scene holds no solid thicker than 2 m behind it:
  a slab or a wall, a partition with another room beyond, which a tap metres
  wide must not see through. A hill's near bulge, which such a tap aliases in
  front of the slope it sees, reads as a solid. The finest level marks none: its
  half-metre taps resolve slabs, and a minority tap of theirs on a railing lies
  beside the receiver's segment.
- **Every surface reads a level, in the share its probes measured.** A
  receiver's stages run finest first: each finer level whose boundary probes'
  support reaches it past their window, the level whose window holds it (the
  coarsest past every window), the same probes past their measured reach,
  blocked only by the surfaces they measured, and the finest level's boundary
  probes the same way. A coarser level never answers inside a finer window: at
  that distance its angular support is metres wide and would see through a thin
  wall a finer probe measured. The first stage that answers is chosen in its
  share, its weight over the same eight probes' weight before visibility; the
  rest comes from the next stage over other probes, so a single corner measured
  across a slab never stands for a whole receiver, and only the last stage that
  answers is normalized to what remains. A surface the camera sees never reads
  the absence of a measurement as black, while a wall a probe saw still hides
  what lies behind it. A finer level answering past its window eases its share
  out over the outer half of its reach. A traced static light's visibility
  scales with the share read within measured reach, and so does the sun's in a
  view that draws no shadow cascade (a session or screen camera, or
  `world.shadows off`): each probe records whether its own ray toward the sun is
  clear. Probes behind the receiver's plane keep a small
  share at the finest level alone; a coarser level's depth test cannot resolve
  the receiver's own wall.
  [Mesh GI](../../../../.claude/skills/rendering/references/mesh-gi.md) owns the
  exact CPU/HLSL rule and record. `world.probe` marks changing or pending
  publication reads unsettled, naming the GPU and live windows separately.
- **Reading it.** Views (`shade/sdf-mesh-irradiance.hlsli`) gather the eight
  probes about the shaded point once (`sdfMeshProbesAt`), pushed 5 cm along
  its normal, weighted trilinearly, by how far each lies in front of the
  surface and by its depth visibility. The sky term is each probe's sky-light
  cube, which the relight integrates from the rays that missed every surface,
  each carrying the sky's radiance about its own direction, so a window low in
  a wall passes the horizon's light rather than the hemisphere's mean, and a
  sky change relights without a retrace; the reflected term adds as stored. The sum replaces the harmonic sky ambient and
  the authored fill, under the `apply` intensity, tint and contact. A static
  point or spot light's visibility at the surface is the share of those probes
  whose centre sees it (`sdfMeshLightVisibility`), which scales its direct
  term, so a fixture lights nothing through the slab under it; an anchored
  light is never traced and goes unoccluded. Past the coarsest window, the
  lookup clamps to its boundary probes while their depth tests still use the
  pushed receiver position. They extend only the light their visibility
  supports; the volume boundary adds no sky. A surface no published probe
  answers, every surface until a level first publishes and those past every
  published probe just after a camera cut, is lit by the exposure meter's sample
  at the view's camera, taken the moment the view arrives, as one probe: the sky
  over the share it sees open and its metered luminance over the rest, for its
  indirect light and its reflection, with its traced lights unoccluded. It reads
  neither black nor lit by the unoccluded sky, and agrees with the exposure the
  same sample sets; the authored fill stays the indirect-off look alone. `world.debug-view
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
  scene up to 8 million triangles), and retraces only the probes the added or
  removed draws reach: their placement region, or a ray before its recorded
  hit. The rest keep their hits and are relit. `world.counters sdf.mesh-gi`
  counts the builds, the geometry each built and reused, and the probes traced
  and relit. A change of a directional light, a static point or spot light, the
  gains, the sources, the sky or what the static scene's materials reflect and
  emit traces nothing: it relights the cached hits under the frame's budget
  (`render.indirect.budget`: probes relit and traced a frame, and the lighting
  age past which the volume prints `[sdf.mesh-gi] behind`), so a day's moving
  sun keeps the relight cycling over the window and overload lengthens the
  lighting's age, never the frame. `world.budget` prints the age's median and
  oldest. The part of each probe's light the point and spot lights and emission
  send is kept while they hold still, so a moving sun relights only the
  directional light and the bounce. `render.indirect.lightingGrid` optionally
  snaps indirect captures: `directionDegrees` steps directional azimuth and
  elevation; `radiance` is a fractional step per colour channel's power-of-two
  range for directional radiometry and the sky radiance each probe ray carries. Each zero or absent step
  leaves that component continuous. The standard chooses its precision in
  `worlds/standard/quality.puck`. Within a grid cell, a moving sun reuses the
  capture; crossing a boundary relights under the new values. Converged frames
  still drain from no bounce, and compare sky values exactly, so their light
  depends on their own frame rather than earlier captures. Direct lights stay
  continuous. A body's palette coming or going, which no
  probe ray meets, relights nothing (`SdfMeshIrradianceScene.Materials` names
  the ids the key reads). Old probes keep drawing, and remain available to the
  exposure meter and viewers, until their replacement lands. A layout change clears the
  staging volume, but the GPU keeps its previous table and coordinates until
  every slot in the new window has a first measurement; a moving window follows
  the same rule, and the thread pool refills its slots from the cache, so a
  window that jumps far costs the frame nothing. The window follows the first
  view's camera, and holds where it stood while another residency renders that
  view (`SdfViewSnapshot.Elsewhere`: its seat crossed into another world), so a
  crossing neither moves nor retraces the world it left. Unwritten cells never become an open-sky fallback. Before the
  initial scene supplies a volume, indirect light has no measured value.
  `puck mesh scene --world <file>` times the
  scene over a world's stored static meshes on the CPU: a full build, one
  sweep of a High window, and the update for one draw's new geometry, a move
  and a return. On the debug world a full build takes
  seconds and a one-draw change a small fraction of that, so the scene is
  updated incrementally rather than rebuilt. A change of a static point or spot light (a
  switch's power) relights the probes, since their bounce and visibility bits
  carry it; an anchored light's move relights nothing.
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
index. Views (`sdfViewsFilter` in `passes/sdf-views-filter.hlsli`) averages the 4×4 window
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
over the adaptation time of presentation time, snapping to the first reading
after a camera cut (`SdfViewExposures`); a cut the meter cannot read yet stays
owed until it can. The meter reads the probes as last relit, so it follows a
moving sun into the night. A camera with no visible lattice probe traces a hit
buffer of its own, cached by camera cell and geometry with storage bounded by
the view count, and relights it from that origin when the lighting changes or the volume
settles. Turning the camera reweights it; movement within the cell reuses it
only across an unblocked segment from its actual origin, and never at a cut,
whose reading is taken where the camera stands. The `camera-traces` counter
under `world.counters sdf.mesh-gi` stays constant on steady reads, and
`camera-relights` counts the relights.
An unavailable scene or a camera
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
cascade, no irradiance volume, no paint, no weathering and no flow. A glass
shape in an opaque group (a pooled creation's mixed `group`, which the
document validator refuses) shades as an opaque surface with the tint as its
albedo.

## Flow

A palette slot's `flow` moves its shading along an authored velocity while the
geometry stands still. The light stage runs it after the weathering
(`sdfApplyFlow`): two layers of the weathering's lattice noise, half a cycle
apart, advect along the velocity's part that runs along the surface and
cross-fade so neither restart shows, revealing an authored surface over the
flow's coverage and tilting the shading normal by the pattern's slope, so
the lights, the probes' reflection and the specular all see the moving
normal. The phase is the pass block's `flowClock`, which the host integrates
from the presented tick and writes only for a program that carries a flow,
so a settled capture holds still and a still world's views keep standing.
Authoring and frames are in
[the surface reference](../../../../.claude/skills/sdf-authoring/references/surface.md#flow).

## Screen lights and the CRT material

A diegetic screen is the most distinctive light source in the engine, because
each screen is *both a picture and a light*.

As a **picture**, a bound screen is emissive: it is its own light source, like a
real display, so no scene lighting dims or tints it. A picture value of one
shows at the screen's peak luminance (`WorldScreen.Luminance`, nits; a window
onto another world shows that world's own luminance), and the view's screen
fidelity eases that to the value the exposure leaves as the picture itself. Its pixels are the
source's image, drawn from the screen's published mapping (the layout, any letterbox and the crop) and,
by default, shown untreated, filling the screen's authored face. A screen whose row names a screen
material (`WorldScreen.Material`, an id in the document's `screenMaterials`; a placement face's
`material` the same) draws its picture, still filling the face, through the display treatment that
material authors: soft cosine scanlines between the source's rows, red, green and blue subpixel stripes
one triad a source column, a vignette, a rim glint at grazing angles, and a soft rounded edge. The
scanlines and the stripes fade out as a source row or column shrinks toward two pixels, so they never
alias against the pixel grid. The standard's `crt` material (`worlds/standard/skin.puck`) is a flat
square tube, a hint of CRT, not a heavy filter. The face kind reaches the kernels in the mapping record's
state row (`SdfScreenFace`: an opening or a display) and the treatment in its two treatment rows
(`SdfScreenTreatment`). A view's screen fidelity (`SdfViewSnapshot.ScreenFidelity`, 0 to 1) scales
every term of the treatment away and turns its soft edge into a hard picture edge, and clears the atmosphere and the tone map from the view, so a seat that
has framed the screen it engaged sees its pixels exactly as the source holds them; every other view
carries 0.

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

Cel shading is a style per material, not one look per world. A style is one
`toon` value with the fields below. A palette row that carries one shades its
material with it; every other material inherits the world's default style,
`render.toon`. A style is on unless it says `enabled: false`, so a row with
`toon { enabled: false }` stays continuous under a banded world, and one scene
mixes cel-styled creations with continuous ones (the gallery's `shape-cel` and
`shape-cel-hard` exhibits on level 3). The standard package authors styles as
data in `worlds/standard/styles.puck` (`celStyle`, `celInkStyle`,
`celHardStyle`, `continuousStyle`): a row names one with `toon: celInkStyle`
and a world with `render { toon: celStyle }`, as the debug world does, and a
variant is `celStyle with { softness: 0.2 }`. The engine holds no look of its own: a style that
bands authors every value below, and validation names each one it leaves out;
a style that says `enabled: false` needs none.

A palette row can also choose its style live from a state cell, the way a
camera case or a binding layout is chosen: `toonSelect { state:
"state.<row>[.<key>]", cases [ ... ] }` shades the row with the case the cell's
whole value names (an enum member's ordinal, an integer, or a boolean as zero or
one), and with its `toon` while the cell names none. A command that writes the
cell swaps the look; only the material's style rows change, so nothing bakes.
The debug world's Moth reads her `mothStyle` row this way, and `moth.style
Plain|Cel` swaps her between continuous shading and the standard's
`celStyle`, the world's own, which she boots with.

`world.toon on|off|toggle` is the live lever over the world's default style.
With no argument it echoes the choice. It uses the same render-section grant and
session-lever delivery as `world.ao`, and contributes no simulation state. A
material with a style of its own keeps it whichever way the lever is set. The
default starts on when `render.toon` is present and not `enabled: false`. A
world that authors no `render.toon`, or one without every value, has no default
style for the lever to switch on. Until
the live lever is used, reload follows the document default; afterwards the
session choice applies to the host, routed worlds and nested views, with each
world's own style. `world.save` folds the live choice into the render settings.

```text
import "../standard/styles"
render {
  toon: celStyle with { softness: 0.2 }
}
```

The views pass steps each direct light's diffuse cosine about `threshold`,
and the light reaches the surface as
`light × (band + (1 − band) × region × shadowTint)`: the lit side takes the
light whole, and the shade side, form and cast shadow alike, keeps
`shadowTint`'s share of it, the painted shadow colour, as far as the light
reaches the surface's surroundings. For a directional that region is whether
the surface stands in the open air: the share of the sky above it the
irradiance volume's probes see (`sdfToonOpen`), so a form or cast shadow
outdoors, under a tree or beside a building, keeps the tint, and a room the
sun never enters does not. The cascades cannot answer it, since the inner face
of a building's outer wall is that wall's own exit from the light; without the
volume their visibility stands in. A point light's reach already holds its
traced visibility. The surface pass queries a cel material's shadows on every
side (`sdfMaterialShadowDemand`), because the band's neighbourhood normal can
face the light where the pixel's own normal does not. Sky and bounced light add as they do in the continuous
response, untinted, so a room lit only by bounce keeps its exposure and the
lit-to-shade step outdoors is the tint the author chose rather than the
sky-to-sun ratio. The local adaptation meters each light without its band or
shadow step (`SdfLightResponse.metered`), so it stays continuous across the
terminator and a cast shadow's edge and never flattens the step.

`band: Visibility` (the default) multiplies the band by the shadow
visibility the cascades' filter left, keeping its penumbra; `band: Combined`
steps that visibility about one half too, so a cast shadow is a held shape.
The shadow step is never sharper than the style's softness, a pixel, or what
the cascade resolves: once a cascade texel, stretched by the receiver's slope
to the light, spans many pixels (`SdfToonShadowBlockPixels`), the step keeps
the whole filtered penumbra, so a far cascade on ground the light grazes
does not print its texel grid as blocks.

The band's cosine reads the surface's normal averaged over the pixel and its
eight neighbours on the same surface, so relief finer than a pixel or two
(a rock's lumps, a baked surface's detail) does not speckle the band and is
left to the paint and the continuous terms. `softness` is the band's full
cosine width, widened at each pixel to what the surface resolves: how far the
normals of that neighbourhood part, the cosine's change across the pixel
(mean curvature times footprint), and how far the hit triangle's vertex
normals part (`sdfMeshNormalSpread`), between which the cosine is only
interpolated, so a terminator never snaps along a coarse mesh's triangle
edges. Specular lobes become bounded patches: `specularSize` is one minus
their half-vector cosine, with a narrow soft edge, banded by the same step.
The banded lights shade the paint flat, metals included; the rim lights, the
environment reflection and ambient occlusion stay in the cel response as they
are in the continuous one.

Ink marks silhouettes and creases, each by a measure that grows smoothly with
the edge rather than a threshold test, so jittered samples move the line a
little instead of switching it and the temporal resolve converges it to a
coverage. A silhouette is a neighbour behind the pixel's tangent plane by
one to three percent of the viewing distance, so a raised plate a few
millimetres proud of its part stays part of that surface at any distance;
only the near side inks. A crease is the two neighbours on opposite sides of
the pixel turned apart by more than `outlineNormal` while both continue the
pixel's surface, so a corner between two faces inks and a step between two
parallel faces does not. A paint boundary changes neither depth nor normal
and draws no line. `outlineWidth` is the close radius in output pixels,
scaled to the current render grid; `outlineDistance` is the distance in
metres at which it halves. Fractional radii interpolate two rings, avoiding
integer-width jumps. `outlineTint` multiplies reflected colour. The ink is
part of the lit surface before temporal reprojection and atmosphere, so
existing motion, disocclusion and history clipping apply.

`exposure` is reflected-light compensation in stops. `shoulder` compresses
the exposed RGB peak with one common multiplier, retaining paint chroma
before the world's authored tonemap. Emission is added after both this
compression and ink. Bound displays and portal pictures bypass the cel
response, and sky stays in its existing pass. A portal's destination shades
its own surfaces once; the consuming view does not posterise the picture.

There is no additional dispatch, image or history allocation: the work is
in `sdf.world$views` and reported with that pass's counters. A cel-styled
opaque hit reads its eight neighbouring depths and normals once for the band's
normal and an ink of up to one render pixel, eight more for each further ring
an inked radius above one needs, and its triangle's three vertex normals. A
material shading continuously skips the cel response and all of these reads.
A material's own style is four rows of its material record (rows 22 to 25,
laid out as the pass block's `toonBands`, `toonShadow`, `toonOutline` and
`toonInk`; `SdfToon.WriteRows` writes both), all zero to inherit the world's
default, so a palette edit restyles one material and changes no mesh. The
pass-block parameters participate in the lighting cadence signature, so live
edits request fresh shading and temporal samples without rebuilding geometry.

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
