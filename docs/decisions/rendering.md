# Rendering: decisions

The choices behind [the rendering programme](../plans/rendering.md), each with
the problem it answers and what follows from it. The implemented contract is
owned by [the shader guide](../reference/shaders.md#shader-pipelines-and-live-development)
and [multi-pass shader pipelines](../reference/shaders.md#multi-pass-shader-pipelines):
the vocabulary and its reserved terms, the JSON-authoritative model, the
compiler's ordering, liveness, and cycle diagnostics, the planner, allocation,
submission, and retirement contracts, the ownership seam between the shader
subsystem, the backends, and World, and the pause, reset, and reload semantics.
Their observable guarantees are preserved, not every current API shape.

## The frame does not interpret SDF programs

The frame does not evaluate SDF bytecode per pixel: every view rasterizes the
meshes the residency bakes the program into, and the interpreter's frame
passes, kernels and levers are deleted. The interpreter has a ceiling no tuning
reaches: even with the interpreter compiled away entirely (the upper bound of
per-world specialization) the primary and shadow passes stay several times over
the RTX 2060's frame, and cutting march steps cuts their time far less than in
proportion, because the remaining cost is the fixed price of each program walk.

Worlds are mostly static and sculpted in the editor, so evaluation belongs at
edit time, as in the shipped SDF engines that hold 60 FPS on weaker hardware.
SDF programs remain the authored representation, and the interpreter remains the
evaluator for baking, CPU queries and the editor. The frame reads what a bake
produced: baked meshes, rasterized with shadow maps. Two bake targets were
built and compared on the debug world. The baked-mesh path draws it well inside
the frame budget with the geometry intact. A sparse distance volume marched by
texture reads costs about the same, but loses the scene's materials and breaks
the avatar's composite. The frame-rate bar is read on a quiet machine
([below](#limits-price-the-mesh-pipeline-not-the-interpreter)). Interpreter
tuning stops; the specialization plan is withdrawn.
Curvature shading, the field's partial silhouette coverage and per-pixel
material blending at smooth seams do not survive the bake: the mesh carries no
curvature, a rasterized pixel is covered or not, and the bake point-samples a
quad's winning material.

## Limits price the mesh pipeline, not the interpreter

The scene ceilings sized for the per-pixel interpreter are retired. Limits
price what the mesh pipeline costs instead:

- **A rigid creation is one shared geometry asset.** A creation with no
  timeline, drivers, effectors, part dynamics, field-changing lanes, cameras or
  faces bakes once in its own space; each copy costs one root transform and one
  draw, never a stamp registration, program instances or a palette copy.
  Spawning, despawning or restyling such a body never rebuilds the program.
  An articulated creation takes a root transform plus one slot per part that
  moves on its own, not one per shape.
- **No scene ceiling is sized for a worst case.** There is no per-creation
  shape cap, no fixed registration pool and no transform reservation for
  shapes a world does not have; buffers are allocated for what the world holds
  and grow. Timings measured on the interpreter set no ceiling. Detail follows
  projected size (the one-pixel rule), never a body's index, and a body always
  wears its own look, never a substitute because a pool ran out.
- **What a creation's size costs** is bake work and resident bytes, counted
  and reported (`puck creation stats`, the bake counters), not refused by a
  shape count. The guards that remain protect representations: skin member
  indices, 24-bit slot, draw and triangle indices, exact-float ranges, the
  material sentinel and region addresses. A guard is never exceeded silently;
  past it a document is refused by name.
- **Kept as work bounds:** 64 volumes (still marched per pixel), the 32 screen
  bindings with the derived-face band following the authored screens, lights,
  sky layers and nested views, 4,096 bodies, and 32,768 solid placement
  colliders per world. `SDF_MAX_INSTANCES` bounds one bake program, not a
  world.
- **Field scope depth rises from 2 to 4.** Scopes are evaluated only by the
  bake and CPU queries.
- **Machine budgets are bytes.** Mesh residency and the geometry cache are
  sized from the machine and evicted by size and age, never cleared
  wholesale and never refused on a miss.
- **Performance is read, not ledgered.** The targets at the highest quality:
  the RTX 2060 and RTX 4070 at 60 FPS, every frame under 16.7 ms, and the Steam
  Deck at 45 FPS, every frame under 22.2 ms. Read the worst frame on a quiet machine,
  with GPU counts beside it. A lower tier, a coarser snap or a frame of latency
  is never the fix for a missed bar.

## Bloom is a camera's scatter, applied once before tonemapping

- **Where it runs.** Bloom is an optional generic camera effect on each optical
  endpoint (a seat's or a direct camera's output), after the view's HDR
  composite and exposure and before tonemapping. Glass, resolve, sky, air and
  volumes keep their order. Its final read fuses into the root's per-view
  placement after the direct image is sharpened, so sharpening never acts on
  scattered light. Intermediate transport images never bloom: portals,
  windows, mirror continuations, sky layers and raw machine or video textures.
  A camera used both as transport and as a direct output blooms only where it
  is shown directly. Panes, letterboxes and the overlay never bloom, and an
  engaged screen's scatter fades by its fidelity, so a fully framed screen
  shows its exact pixels.
- **How it filters.** A linear, normalized pyramid with no threshold and no
    luminance reweighting by default: a 13-position downfilter, a 3×3 tent upfilter,
  FP32 arithmetic and RGBA16F storage, six half-resolution levels at the high
  tier. Being linear, it commutes with exposure adaptation and needs no history
  of its own. Its radius is a fraction of the output's shorter side, so dynamic
  resolution never resizes its look. The combine is `(1 - s) L + s B(L)`; an
  explicitly authored soft cutoff is the one nonlinear option, and its exposure
  dependence is documented where it is authored.
    Cell-edge reflection and integrated destination-cell resampling preserve both
    constants and total HDR energy, including odd-sized levels and final placement.
    Adjacent contributions share linear samples; odd ratios can require extra
    samples per filter position. Fractional tent widths interpolate whole symmetric
    integer-width kernels, preserving energy while radius varies continuously.
- **Who owns it.** The engine default is off. A world authors `render.bloom`;
  a camera may override it; the quality tier gates its cost; each player has a
  scale from 0 to 1, where 0 is the accessibility off switch. Every diagnostic
  view bypasses it, collider and contact evidence included, and a bloom-only
  diagnostic view shows the scatter alone.
- **A screen material's one-pixel brightness knee is not bloom** and is deleted;
  the material's other treatment terms stay.
- **Its bar** is at most 0.3 ms of GPU on the RTX 4070 at 1280×900 high, with
  both backends matching, read from `world.gpu-timing` and `world.counters gpu`,
  and judged for flicker in presented bursts, never only in settled stills.

## A light is shadowed wherever it lights, and cel shading is a style per material

The [illumination plan](../plans/illumination.md#cel-shading) settled both; the
owner ruled the second (a style per material, not one look per world).

- **Shadow where the response is.** The surface pass queries a light's cascades
  wherever that light's diffuse response is non-zero, a wrapped material's side
  past the terminator included, and filters what it queried. A side it skips
  decodes as fully visible, so any response there is light through walls. Past
  the terminator the receiver's own body stands between it and the light, and
  that body, and only it, lets the wrap's light through. Each cascade holds two
  layers: its depth, the first face the light meets (where it enters the first
  solid it crosses), and an exit layer, the first face turned from the light
  (where it leaves that solid). A triangle facing the light compares against
  the depth; one turned from it compares against the exit layer, where its own
  face is the nearest exit unless another solid stands in front of its body.
  This is exact for a millimetre wing and a metre-thick plate alike and needs
  no thickness estimate. One from the surface's curvature fails flat geometry
  both ways (it lets a separate slab a few centimetres off a flat wing through,
  and shadows a thick flat plate by itself), and a per-vertex thickness cannot
  follow the light's angle. Reading
  the exit layer for every receiver (second-depth shadow mapping, Wang and
  Molnar 1994) would leak light along every lit floor beside a wall's foot,
  where the wall's far face meets the floor, so a receiver facing the light
  keeps the depth. Winding marks an exit only on geometry that encloses a
  solid: baked meshes, and authored meshes whose triangles close a volume,
  which are wound outward like the bake's. Everything else an author can make
  (an open or two-sided authored mesh, a body clipped at a portal's glass, a
  mirrored draw) enters the exit layer whole as a thin caster, so it still
  shadows a wrapped side behind it, and only its own wrapped side loses its
  transmission. A depth peel (the second-nearest surface) would need a
  closed-receiver flag anyway and a fragment-discard pass, and the field's own
  interior distance does not exist for authored meshes. The exit layer costs a
  second raster of each cascade's draws and one more depth image per cascade.
  The exit layer's reading of winding as facing rests on closure, consistent
  orientation, a rigid transform and no clip, none of which the raster path
  guarantees for an authored, clipped, mirrored or deformed mesh; and two depth
  layers cannot say which body a surface belongs to. The
  [transmission plan](../plans/transmission.md) therefore replaces the exit
  layer with a body identity per cascade texel beside the depth, a peel keyed
  by that identity, and the rule that any other body in front of a wrapped
  receiver shadows it while its own body shadows it only by a second chord;
  this entry is rewritten when it lands.
- **The band shapes the light, not the shadow.** A cel style bands a light's
  cosine and keeps its visibility as a separate factor,
  `band(cosine) × visibility`, so the filtered penumbra keeps its softness and
  colour. `band(cosine × visibility)` stays as an authored shape for the harder
  look. Rim lights, the environment reflection and ambient occlusion stay in the
  cel response as in the continuous one; a style never discards a term.
- **A style is material data.** One lighting path with a style response per
  material: a palette row's `toon` opts its material in (or out, with
  `enabled: false`), every other material inherits the world's default
  `render.toon`, and `world.toon` is the live lever over that default. One
  scene mixes cel-styled and continuous creations. The concrete styles are data
  in the standard package (`worlds/standard/styles.puck`), never an engine
  default, and the style rows live in the material record beside `wrap`,
  `fill` and `receive`, the material system there already is.

## A raster pass draws only what it can show

Every raster pass skips the mesh draws it cannot show any of, by a world box per
draw tested on the host (`SdfMeshBox`, `SdfMeshCull`): a view's mesh and glass
passes against the view's frustum before the frame's far distance, a shadow
cascade against its light's box and, swept along the light, against the
receivers the view shows. A skipped draw lies wholly outside what the pass's
rasterizer keeps, or casts only where no visible receiver reads, so the cull
changes no pixel and needs no authoring: every world gets it, and every nested
view culls by its own camera, which for a portal window is the frustum fitted
through its glass. On the debug world it removed 31% to 95% of a frame's
triangles across eight indoor, outdoor and through-portal views
([P20](../plans/rendering.md#p20--visibility-areas-and-residency)). The test
is conservative where exactness would risk a pixel: a pixel margin past the
temporal jitter, a thousandth of the near distance in front of the near plane,
the shadow filter's reach around receivers, and no box at all for a skinned
draw whose vertices the GPU places. Occlusion, levels of detail and residency
build on the same per-draw box; authored areas are not assumed, because the
debug world's levels are open plan and name no rooms. Hidden geometry is found
by a depth-based occlusion test that needs no authoring, not by authored rooms:
each view's mesh pass draws what its previous frame found visible, builds a
pyramid of the farthest depth it drew, tests every box the box cull keeps
against it on the GPU and draws the newly visible in a second phase of the same
frame, so the test is conservative and a disocclusion is never a frame late.
Both phases keep the box cull's draw order, so depth ties resolve alike. Glass
never occludes, the shadow cascades keep the box cull, and each nested view tests
against its own depth. On the debug world it removed a further 34% to 61% of
the mesh pass's triangles where walls and slabs hide exhibits
([P20](../plans/rendering.md#p20--visibility-areas-and-residency)).
`world.cull frustum` keeps the box cull alone for comparison.

Anything that trades a visible change for cost, such as a level of detail, an
unloaded residency ring or a culled small object, switches only beyond the
distance at which the switch is barely perceptible (the owner's ruling). The
switch distance is derived from the change's size on screen at the view's
resolution, not fixed in metres.

The owner's rulings for levels of detail:

- A coarser level of a baked mesh is drawn where its recorded geometric error
  projects to at most one display pixel. A view switches back to the finer
  level only above 1.1 pixels, so a level does not flicker at the boundary. A
  shadow cascade takes the coarsest level whose error is within one of its
  texels.
- The first level is lossless: coplanar lattice quads merged into the polygons
  they tile, changing no pixel.
- The irradiance trace reads that merged mesh, which never changes with the
  camera.
- A static union larger than one chunk (the hall) is split into chunks only once
  the merge's savings are measured.
- On-screen sizes for levels of detail and update rates are measured in display
  pixels, after upscaling, never in render-grid pixels.

The first level is part of the bake (`SdfMeshPlanarMerge`): the store keeps it,
every pass draws it and the irradiance trace reads it. It keeps every vertex on a
merged region's outline, a straight run two merged faces share included, and
removes only vertices inside a region, so every outline edge and every vertex
normal is the contour's own and the rasterizer covers each polygon as it covered
the lattice's quads. Dropping the shared run's vertices from both faces would keep
the geometry but have the rasterizer snap the shared edge from other vertices; on
the debug world it would save one part in a hundred of the triangles left, too
little to give up an exact outline for. The one pixel change a merged face can
make is where it meets a separately baked mesh, whose depth test the face's new
rounding can turn, as two coincident meshes' pixels turn.

The two coarser levels are part of the bake too (`SdfMeshCollapse`): half-edge
collapses of the merged mesh, so a level is a triangle list over the mesh's own
vertices with their own normals, and the region holds only its indices and
triangle materials, numbered after the merged triangles. A level's error is
measured, not estimated from the quadric that orders the collapses: every vertex
of the merged mesh stays within it of a level triangle of its own material, and
every vertex and edge midpoint of a crease or a material's edge stays within it
of that line as the level draws it, and every level triangle's centroid and edge
midpoints stay within it of the surface it replaces, so no triangle cuts through
a thin rod whose samples it passes between; a collapse is taken only where all
three hold (a quarter of the contour cell for the first level, two cells for the
second).
The lines are measured as lines because a surface measure alone misses an
outline that moves within its face's plane: a triangle filling a notch in a face's
outline holds the notch's vertices at no distance, and the face shows surface
where there was none. Shading needs a bound geometry alone does not give: a
coarse triangle can stay within a pixel of a bumpy surface while its interpolated
normal smooths the bumps away, so a collapse also keeps each vertex's normal within
2 degrees (first level) or 6 degrees (second) of the level's normal there. A crease
place slides only along its crease with each side keeping its own vertices, a
corner where three faces meet and a mesh's open edge never move, and no collapse
turns a triangle against its normals, which the back-face cull would show as a
hole. A level saving less than a quarter of the finer level's triangles is not
kept. The projection of the error is conservative: the error times the draw's
largest stretch, over the nearest forward depth of the draw's box, at the
frustum corner's sharpest projection of a displacement; a box reaching the eye's
plane draws the merged mesh. Glass draws its merged mesh, since its thickness is
the sums of its own triangles' distances, and a frame held for a settled capture
chooses without the previous frame's level, so its still owes nothing to earlier
frames.

Large static unions partition their contour triangles into spatial chunks before
the planar merge and collapse (`SdfMeshChunks`). A triangle stays whole in its
centroid's bin; the chunk's box encloses the whole surface it draws. Every chunk
keeps every vertex on its open boundary during merging and locks it during collapse,
so adjacent chunks keep identical seam edges at independently chosen levels, without
a stitch or a T-junction. The existing pixel and texel rules choose each chunk's
level. The store keeps the pieces; whole-group queries read their combined merged
surface, and the irradiance trace reads the pieces' merged surfaces.

The lights follow the same rule: each view shades only the lights that can
reach a surface it shows (`SdfLightSelection`), tested with the same
`CameraSnapshot.MaySee`, a light's sphere of range against the view's frustum.
Skipping one changes no pixel, since a point or spot light's falloff is exactly
zero at its range (below).

Work that changes every frame updates less often where it is far or small on
screen (the owner's request), by the same rule: a reduced rate is a visible
trade-off, so each switches only where a held update jumps by at most two
pixels on screen for the speed it is sized for (`SdfUpdateRateTiers`). A body's
pose evaluation updates at least 30 times a second at or below 30 pixels per
metre and at least 15 at or below 15, its root followed every frame, its pixels
taken in the view that shows it largest, a window's or a camera screen's view
counted by its share of the display and one the display shows none of not at
all; a nested
portal or session view renders at least 30 times a second at or below 120
display pixels high and at least 15 at or below 60; a screen's source uploads
at least 30 times a second once shown at or below its own height and at least
15 at or below half of it; the far shadow cascade refreshes at least 30 times a
second, its fit held between refreshes so a static shadow stays exact.

A tier is a floor held by presentation time, since the two-pixel bound holds
only if the content really updates that often: a rated item updates on the
first frame where waiting one more frame, estimated from the latest frame's
span, would pass its period (`PresentationRate.Holds`). Counting frames of the
pacing target instead lets the real rate fall with the frame rate: paced at
141 Hz on a variable-refresh display but rendering 70 frames a second, a
five-frame hold is 14 Hz and four-pixel steps. On a fixed signal the period is a
whole number of refreshes rounded down, so the cadence stays even and never
falls below the tier, and a steady 60 Hz holds the same frames as a two- or
four-frame divisor. Off a grid the comparison allows a millisecond for the
jitter of a sampled frame time, without which a steady 60 Hz on a
variable-refresh display refreshed the far cascade on three frames of every
five instead of every second one.
A tier above the display's rate holds nothing. Under
lockstep the period is whole ticks, so a capture's frames do not depend on the
display. A cap is the other meaning of a rate ("at most N Hz": an authored
refresh, or a machine's own 59.7 Hz) and stays counted in presented frames,
rounded up (`PresentationRate.FramesAtMost`); the two never share a rounding
rule. Everything is presentation: the simulation ticks every body and every
machine runs at its own rate, so game timing, audio and replay hashes do not
move. The levers extend the per-pass cadence and the render graph's refresh
(`RenderGraphRefresh.AtLeast`), never a second scheduler; `world.rates`
switches each for comparison and prints each lever's longest interval between
updates at each tier against the tier's period. Measured on the debug world
([P20-3](../plans/rendering.md#p20--visibility-areas-and-residency)).

## A diagnostic view never covers its evidence

A debug view that flags a suspect value never draws over the pixels it flags
(the owner's ruling). Flagged pixels stay identical to `world.debug-view off`:
diagnostic views keep temporal resolve, sky, glass, fog and exposure, and their
marks are composed after resolve or in the 2D overlay, never into temporal
history or lighting.

The untouched frame is the default. Marks sit outside the evidence:
- a pin per cause, beside the item, at most 16 on screen;
- contours outside a flagged region, with an untouched gap;
- for physics, the contradicting geometry drawn in the empty space around the
  drawn surface (a collider's corners, a support caliper).

Muting the unflagged context is an optional mode, because dimming changes the
perceived brightness that exposure and lighting checks depend on.

A red flag is a named contract violated on matching state, with a witness
outside an error budget built from the system's own precision. Amber is a
suspicion and is off by default. Unknown means not evaluated, so an incomplete
scan never reads as clean. There are no allowlists or recorded baselines: a
misfiring check is made precise, or the data is fixed.

A mesh comparison certifies surface correspondence before it can be red. A
normal ray that misses a nearby edge and reaches a distant surface proves no
local discrepancy. Diagnostics search the retained mesh hierarchy for the
nearest triangle point within a precision-derived neighbourhood, including
edges, vertices and zero-distance contact. The search radius bounds coverage,
never the error budget: the returned triangle supplies its own bake precision.
No local match means unknown, never clean or red. Collider boundary separation
is the unsigned Euclidean distance; a nearest point alone does not certify
which side of a closed solid contains the witness. Reports distinguish sampled
red, clean and unknown outcomes from clustered finding counts.

Every view has a console companion, `world.debug-view report`, which runs with
the view off and headless. Each finding carries its identity, the predicate and
its witness, coverage, lifetime and provenance back to the `.puck` source. Pins
are addressable by `world.view.pick` and `goto`, which moves only the camera.
A capture with a view on saves the untouched frame, the annotated frame and the
report from one frame.

## Lights, screens and the camera are photometric

**Every light, screen and emissive surface is authored in real units, and the
frame shades in luminance (the owner's request).** A directional light's
illuminance is in lux (a clear midday sun about 100,000), a point or spot
light's luminous intensity in candela or its luminous power in lumens (a 60 W
bulb about 800), with a colour or a colour temperature in kelvin; an emissive
surface's, a sky layer's and a display's luminance is in nits (a CRT about 150).
A light's colour is scaled to unit luminance, so it gives exactly its quantity.
The frame's radiometric unit is the nit: a light record carries its illuminance
(or intensity) over π, so a Lambertian albedo ρ under an illuminance E shows
ρE/π, the irradiance volume traces and stores nits, and a light's specular lobe
scales by its illuminance. `SdfPhotometry` owns the conversions, and the `.puck`
language takes `lx`, `lm`, `cd`, `nit` and `K` on the fields that carry them.
The sky is consistent with the sun because its colours are in units of a layer
luminance whose default is a clear day's sky (10,000 nits) beside the default
100,000-lux sun, so the sky lights the ground about a fifth as brightly as the
sun does.

**A camera exposure maps the luminance to the display, before the tonemap.**
`render.exposure` is manual (an `EV100`) or automatic. The chosen automatic
meter reads the log-average of each view's HDR output with a highlight clamp,
so dynamic meshes and anchored lights contribute to its exposure. Adaptation
eases toward that reading less compensation, within authored bounds, over
presentation time. The image meter is open work in
[Lighting and GI](../plans/open-items.md#lighting-and-gi); the current automatic
meter still reads the irradiance volume about the camera.

The probe meter remains for local adaptation and the enclosure case. When no
visible lattice probe answers, the tracer measures radiance at the camera
itself. This measures an enclosure smaller than a lattice cell without
importing light from another room or changing the volume. An unavailable
measurement holds the previous exposure; the authored EV initializes a view
before its first reading. `world.exposure` names the measurement source and
reports no meter reading while holding. The eye adapts across a scene
where a camera cannot, so an automatic exposure also adapts locally by an
authored share (`render.exposure.local`): the views stage moves each surface's
exposure from the view's toward the one its own illumination (the lights'
diffuse terms and the volume's indirect light, read as a scene of 0.4 albedo)
would meter, within six stops, so a room beside its sunlit windows reads as a
room and the sunlit ground through the window as ground, while a dark paint
under the same light stays dark; the composite scales the sky share so the
sky's brightest texel reaches display white rather than clipping, and a window
from a dim room shows the sky's gradient. The gain reads illumination, never a
surface's own luminance, so no dark albedo is lifted by it, and the volume's share of that illumination is smooth over a cell, so the
adaptation carries no halo across a depth edge and agrees between views of one
world.
Each view's composite multiplies its output by its exposure, so a dark lobby
lit by its screens and a sunlit garden both read. A window onto another world
and a sky layer's view carry the world beyond at an exposure between its own
and the viewer's: the view beyond the glass applies its exposure to the window
share (`SdfPhotometry.WindowAdaptation`, three quarters) and the view looking
through applies the rest of its own, so the window shows
`L · E_viewer · (E_window / E_viewer)^share`, a sunlit garden seen from a dim
room reading as a garden rather than white, and a night field seen from a lit
room as night rather than black; every level of windows within windows carries
the same share, so the result is the same whatever the nesting. A display shows
its picture at its peak luminance; terms that are display-referred by design
(the editor grid, an unbound screen's glass) are divided by the exposure. The
stylized curvature terms (the ridge light, the cavity and the ink line) apply
to the exposed colour at the strength of the surface's own exposed
illumination against the white the meter aims for (`SdfExposure.ReferenceWhite`),
so a lit room keeps its crisp edges and a dark room's edges stay as dark as its
walls rather than drawing light lines across them. Exposure is presentation only: it reads presentation time and the
volume, and nothing in simulation state depends on it.

**A point or spot light ends at a range derived from its intensity.** Its falloff is the windowed inverse square
`saturate(1 − (d/r)⁴)² / d²`, smooth and exactly zero at the range `r`, the
distance at which a white surface facing the light shows 1/256 of display white
at the darkest exposure the world adapts to. Past it the light darkens nothing a
view would show, and a view whose frustum misses its sphere skips it, which
lifts the old eight-light cap: a world's lights table holds 128, and each view's
pass block carries a mask of the ones it shades.

**A fixture that stands still is occluded and bounced by the irradiance
volume.** The frame has no shadow path for a positional light (the cascades are
the sun's), so a static point or spot light (one with no anchor,
`SdfLight.IsTraced`) is traced where the bounce already is: each probe records
which such lights its centre sees, one bit a table record, and lights its hits
by them under a traced shadow ray, so their light bounces about their room; the
views pass takes a light's visibility at a surface as the share of the probes
about it that see it, so a downlight lights nothing through the slab under it,
and a room's own fixtures light it indirectly. The occlusion has the volume's
resolution, so the wall, slab and doorway are what it resolves and a plinth's
shadow is the ambient occlusion's. An anchored light moves with its anchor,
which the volume cannot follow, so it lights through every wall and bounces
nothing.

**A matte surface reflects little of the sky.** The specular lobe's reflection
of the environment is its directional albedo, the split-sum environment BRDF
(`sdfMaterialEnvironmentBrdf`) over the environment blurred toward its
irradiance as the roughness widens the lobe, and a material with no specular
response reflects none of it; a Fresnel edge at grazing that ignored the
lobe's width made painted stairs and placards read as chrome.

**Considered, and set aside.** Scene-referred values left as stylized weights
with a global gain, since no gain makes a 150-nit screen and a 100,000-lux sun
read in one frame; a cutoff radius authored per light, which would make the
range a look decision for every light rather than one derived threshold; a
cube or dual-paraboloid shadow map per positional light, six raster passes of
the world a light a frame
where the hall holds 68 of them, for a sharper shadow than the rooms need; and
a window published unexposed for the viewer to expose, under which a sunlit
world seen from a dim room blows to white.

## The pipeline foundation

**Fixtures before features.** The first bounded deliverable is repeatable
pipeline verification and per-pass work counters, then shared opaque visibility with
one procedural mesh and one SDF wall. An importer, a material editor, and
skeletal animation do not precede that proof, and no package introduces an
alternate compiler, World driver, or verification script.

**A fixture's oracle is independent.** Expected pixels are derived
arithmetically or asserted by region and object, never taken from a captured
baseline or from two backends agreeing, because two backends can agree on the
same wrong image. Frame counts derive from pinned logical steps and reset
semantics, not the scheduler's cadence. Float presentation pixels are never
claimed as simulation-state determinism.

**Performance is judged by the worst frame, with counted work beside it.**
Quiet-machine timing reads the frame-rate bar; code, disassembly and live work
counts explain where its cost comes from and what a change moves. Read
`host.frames` and `world.gpu-timing` with no build, test or other GPU work
beside the debug world. Counts are diagnostic evidence, never a substitute
for that timing. Dynamic resolution's load signal and the editor's per-pass
readout ([E9](../plans/editor.md#e9--cost-per-object-and-gpu-pass-timing)) are its
runtime readers. Work counts extend one shared read
contract: a completed sample carries identity, labels, pass states, and counts
together, there is no parallel pipeline-only counting interface, and no label
is borrowed from the currently installed graph.

**The frame-rate bar is the slowest frame at the highest quality.** Until
initial development is done, every machine runs the highest quality settings,
and lower qualities wait (the owner's ruling). Those settings are the
`world.quality high` preset: high shadows from two shadowed lights, ambient
occlusion, temporal reconstruction, high indirect lighting and native render
scale. The bars, each the slowest frame and not the average:

- the RTX 2060, the lowest-powered desktop the programme targets: a steady
  60 FPS, every frame under 16.7 ms;
- the RTX 4070: a rock-steady 60 FPS (the owner's ruling);
- the Steam Deck: a rock-steady 45 FPS minimum, every frame under 22.2 ms (the
  owner's ruling).

Each machine still meets its bar with its own strengths
([each machine runs to its own
strengths](engine-design.md#each-machine-runs-to-its-own-strengths)), never by
dropping below the highest quality. An average hides the
hitch a player feels, so a periodic stall (a camera enumeration, a live
mutation's apply, a pipeline compile) misses the bar even when the mean is
fast. A lower tier, a coarser shadow snap, reduced contact detail or a frame
of latency is never the fix for a missed bar. Counted work explains a change;
the bar itself is read by timing the worst frame on a quiet machine.

**Worlds are mostly static, and sculpting happens in the editor.** Most of a
world's geometry never changes while it is played, and editor mode is where a
creation's field is sculpted. The cost of evaluating an authored field therefore
belongs to the moment it changes, not to every frame: a played world renders a
representation baked from its static geometry, which can ship with the compiled
world as an optional cache, and a sculpt edit rebakes only what it touches.
The field interpreter serves the bake and CPU queries; the frame draws meshes
already available while replacement geometry bakes. Dynamic bodies bake per
rigid part, since their parts move without changing shape.

**Counted work is diagnostic evidence.** Read dispatches, vertices, texels,
uploaded bytes and bake evaluations beside quiet-machine worst-frame timing
on the debug world at `high`. Nothing records those counts in the repository,
and no floor-tier ledger or march-step gate exists. A count identifies work
to remove at its cause; it never grants a frame over the machine's bar.

**Attachment ownership belongs to the pipeline plan.** A backend executes a
plan it does not own. Outputs are versioned, each version has one writer, and
forwarding transfers ownership rather than permitting multiple writers; a
`load` flag alone cannot express a depth prepass followed by an update.

**The first hybrid resolve uses the full valid per-view extent** and keeps
SDF culling for traversal only; a coverage-union optimization follows
correctness and measurement. A shared image is an agreement about extent,
format, color space, sampling, and lifetime; an opaque surface adds a shared
camera, depth, coverage, material interpretation, and coordinate conventions;
lighting or volume adds a composition contract; an authoritative field is
separately admitted deterministic data. An arbitrary image shader has no
meaningful depth, and compilation does not turn it into a surface or a
simulation.

**The persistence boundary is a per-instance override committed through a
document mutation.** An override is authored state like any other; session
state (pause, steps, preview time, captures, feedback history) stays out; time
scale is document data; shared files keep their defaults; headless validation
needs neither a GPU nor a local compilation.

**Distribution is a source closure plus a manifest** of logical paths, content
hashes, compiler, options, and adapter identity, and required capabilities,
with each pass's precompiled binaries per backend and variant versioned by its
interface hash, so loading a package needs no compiler. Packaging is not a
sandbox for arbitrary GPU code, and the trust boundary says so.

**Replacement memory has separate steady-state and replacement-peak limits**,
checked before candidate allocation, counting still-live old and retired
allocations; the installed graph is never released to satisfy admission. The
numerical release limits are the owner's decision.

**The release profile is the packaged candidate that ships the debug
world**, the same candidate every other programme's release evidence names;
its thresholds are measured against it, never inferred from one run. The
foundation releases independently of hybrid work when its gates pass.

**The debug world is the live qualification world.** `worlds/debug` shows the
features composing on both backends while the canaries under
`experimental/tests/Puck.World.Canaries` are quarantined. A workload names the
world it actually boots, and its observations establish only that world's
behaviour under the exercised conditions.

**Hardware- and environment-only checks run once, at the programme's close.**
A check that needs a particular device, driver state, operating system or
display is not run package by package. Each is listed in
[the programme's deferred checks](../plans/rendering.md#deferred-to-the-end),
and they run together when the programme ends, on the code it finally ships.
For the GPU checks each change runs, whether the two backends agree is judged
in one final review pass rather than change by change.

**Device-local memory is counted by role, not by memory type.** Images,
device-local buffers and imports count on both backends; host-visible,
staging, upload and readback memory never count. On a unified-memory device
every memory type can be device-local, so a memory-type test would count
staging and readback buffers there and not on a discrete card, and a
`peakDeviceLocalBytes` threshold would mean something different on a handheld
than on a desktop. Counting by memory type is therefore rejected, on
unified-memory devices included.

**Representations are chosen by measured cost**, never by replacing
"everything is an SDF" with "all environments are SDFs"; meshes, continuous
fields, and baked distant content may each fit a scene, and no broad shadow or
ambient-occlusion speedup is assumed without a scene and fidelity comparison.

## How worlds reach the GPU

**A world is runtime data, so no shader binary depends on a world.** A world is
authored as a document, loaded while the game runs, and visited through a
portal, and a device receives binaries and data rather than a toolchain. A
binary therefore depends on its source and its declared interface and on nothing
else, and a GPU layout is never derived from a world's rows: a layout a world
could change is a binary a world could invalidate. A shader-owned layout
recovered by reflection is rejected because two backends have two packing rules,
members match by convention, and nothing is shared between passes. A world-owned
layout with a header generated per world is rejected because it forces a shader
build per world, which is what a runtime-loaded world may not require. A
KERNEL-class probe kind's kernel therefore compiles at build, as do the camera
frame converter's kernels and the display encode every swapchain compositor draws, and a pipeline
package loads from its binaries.

**A world names its pipelines by source, and its package's compile stores their
packages by key.** `puck compile --tree` packages every source a package's
pipeline row names into a package store beside the package's root document
(`packages/`), each under a key over the
source's closure, its passes' interfaces, and the names it plans under. A row
naming a source loads the stored package with that key and compiles nothing;
only a source with no stored package compiles, through `ShaderPipelineLoader`,
which is the authoring path, and where nothing can compile it the row is refused
by name. The compile does not rewrite a document's rows to name
packages. The document an author edits is then the document that ships, a
compiled package booted from its output finds its packages beside the document,
a source boot compiles on first use, and an edited source misses its package
and compiles, so authoring keeps its loop without a second document.

**A pass declares an interface, and its declarations are generated from it.** A
pass names the scalars, vectors, arrays, and images it reads and writes, each
with a name and a GPU type, the way a function declares a signature. A shader
package is the unit that ships: the sources, the interface, the declarations
generated from the interface, and the precompiled binaries for each backend and
each quality variant, versioned by a hash of the interface. Generated
declarations carry explicit offsets and explicit binding slots, so the layout is
identical on every backend and no backend's packing rules are consulted; scalars
generate a named struct, so a GPU debugger shows members rather than bytes, and
arrays generate accessors that hide the element format. A world binds a row to
an interface member with data — `parameter water.tide = state.cisternLevel` —
and the shader cannot tell a literal from a bound row. A compiled world records
the interface hash it was validated against, and a mismatch refuses at load
naming the package. The pass chooses an array's element format, and the bound
row has to be able to fill it, so validation refuses a row whose kind or bounds
do not fit, naming the row and the member; the conversion runs once per tick on
the CPU.

**One binding contract serves every pass kind, grouped by how often the bound
data changes.**

| Group | Changes | Holds |
|---|---|---|
| Frame | every frame | resolution, presentation time, the deterministic tick, the pointer |
| World | every tick at most | the state mirror's regions, the field lattices |
| Pipeline instance | on install or resize | pass parameter blocks, persistent and history resources |
| Pass | every pass | transient inputs and outputs |

Each group is one descriptor set on Vulkan and one root-signature table on
Direct3D 12, plus a sampler table when the group holds a sampler, because a
Direct3D 12 descriptor table cannot mix samplers with other views. The group's
ordinal is its set and its register space on both backends. The binding kinds are closed and the same for every pass kind:
constant buffer, read-only buffer, read-write buffer, sampled image, storage
image, and sampler. Push constants carry at most an
index, so nothing authored or bound lives there and the portable push-constant
minimum stops being a design limit. Widening the current fullscreen contract by
one binding kind is rejected because it deepens the split between two unrelated
binding models; raising the push-constant block per adapter is rejected because
a layout that differs per adapter forks the interface and takes the
cross-backend pixel verdict with it. A pass's resource needs come from its
declared interface, so the frame graph's planner and the binding contract read
one statement.

**The closed set of binding kinds is one type, `GpuBindingKind`.** The pass
interface, both backends' layout planners, and `IGpuBindings.WriteBuffer` all
read it, and whether a buffer is read or written is part of its kind, so it is
the one statement of buffer access. Keeping another binding-kind type beside it
with translations between them is rejected, because each translation is a
second statement of the same access that can drift from the first. A
positional compute binding (`GpuComputeBinding`) states it too, and holds only
buffers and storage images: a sampled image is always read through a separate
sampler in a group, so no combined image sampler exists anywhere.

**A binding is visible to the pipeline's stages, never to its own.** A
pipeline's stages come from its pass kind, compute or vertex and fragment, and
every binding and the pushed index are visible to all of them. Direct3D 12
therefore gives each root parameter `ALL`, or the one stage of a single-stage
graphics pipeline, and Vulkan gives each binding and the push range the
pipeline's stage flags. Reflecting each stage's bytecode to narrow every
binding to the stages that read it was rejected: Direct3D 12 sets visibility
per root parameter, so a table holding a vertex-only and a fragment-only
binding would need `ALL` anyway or a split into one table per stage, which
breaks one view table and one sampler table per group and moves the root
indices. Narrowing returns only if a measured driver cost shows it pays.

**A device's descriptor heaps are the device's own size, and a candidate is
admitted into them or refused.** Direct3D 12 binds one shader-visible view heap
and one sampler heap per device, and every descriptor pool is a range of them.
The view heap takes the size the device reports,
`GpuDeviceCapabilities.ViewHeapSize`, and the sampler heap takes the smaller of
the reported `SamplerHeapSize` and `StaticSamplerHeapSize`, since root
signatures with static samplers are bound beside it. A runtime that does not
answer options 19 reports the sizes every binding tier guarantees, 1,000,000
views and 2,048 samplers for both sampler limits. The
heaps are created once with the device, and the device outlives every world it
presents, so no world's demand sizes them. What varies is admission. Each pool
owner states its pools' sizes statically, and its own pool creation reads the
same statement, so `GpuDescriptorHeapBudget.TryAdmit` checks a candidate's
demand against the free ranges before anything is allocated. A candidate that
does not fit is refused by name when it would install, as a pipeline over its
memory budget is refused with `SHADERPIPE_BUDGET`; the installed graph keeps
presenting and nothing grows. A heap's bytes are its size times the device's
descriptor increment, counted under `memory.directx`. At most
`GpuDescriptorHeapBudget.MaxLivePools` pools, 1,024, are live on one device,
which bounds the range allocator's bookkeeping; a pool past it is refused by
name, as a pipeline past `ShaderPipelineLimits.MaxPasses` is.

Two ways of sizing the heaps were rejected. A cap stated in a document has no
basis, because nothing an author writes knows how many pipelines, views and
previews a session will install, and a cap below the device's limit refuses work
the device could hold. Sizing the heaps from the first world's summed demand,
doubled so a reload can hold both graphs, ties the device's heaps to whichever
world booted first, so a larger world loaded later on the same device is
refused although the device has room.

**HLSL compiled by the pinned DXC is the one source language, and a generated
echo pass is what proves the layout.** One language, one compiler, one cache
key. A second language would need its own front end, a translation back into
HLSL for DXIL, a register remap between the two, and offset reconciliation
wherever their packing rules disagree, and none of that buys a pass anything,
so every pipeline source is HLSL. Because declarations are generated, the engine assigns every group,
binding, register, and offset itself, which is what a language-level parameter
block would otherwise buy and is a stronger cross-backend guarantee than any
allocator's. Slang is the better language and the wrong trade: DXIL still
comes out of DXC, so a Slang pipeline pins two toolchains; it compiles several
times slower on the largest include, which is already the slowest part of the
build; and the features that would justify it, precompiled modules and link-time
specialisation, are not yet stable. Revisit it when a precompiled-module path is
stable, when DXC stops being maintained, or when a generated declaration cannot
express a group identically on both backends. The contract is indifferent to the
language, so a revisit touches no world and no interface — an interface can
generate a module as easily as an include. What a single reflecting compiler
would have given is proof that both backends agree on the generated layout, and
that proof is taken by running instead: every package build generates an echo
pass per interface that reads each member through the generated declarations and
writes it to an output buffer, the members are filled with distinct sentinels,
and the read-back has to match exactly on both backends, so a layout the
generator got wrong fails the package on a real driver before a world binds to
it. Compiler reflection over both binaries is a second, cheaper build-time check
for hosts with no GPU.

**The SDF VM is the one GPU interpreter.** A world's signed-distance program runs
in `Puck.SdfVm`'s kernels, and any other GPU effect is a pipeline pass compiled
from HLSL. A second, general-purpose bytecode interpreter, the removed
the shader VM project, is rejected. It had no consumer but its own tests, no kernel
included its GPU half, and nothing compared its host and GPU interpreters. It
therefore added a C#/HLSL contract that no check held and a second copy of the
PCG3D hash. It also duplicated work each existing path already owns: an authored
effect that is not a signed-distance field is better expressed as a pass with a
declared interface than as a program interpreted per pixel. A future interpreter
is admitted only with a consumer on the render path and a check that holds its
two halves together.

**Presentation samples a state mirror at presentation time.** The compiler knows
statically which rows a presentation binding reads — HUD, camera, pipeline,
material, signed-distance program — and that deduplicated union is the
presentation manifest. Each client view holds a mirror compiled from it: a flat
table of slots carrying a row ordinal, a key, a target flag, and a conversion,
resolved to ordinals when the document is installed, so no string, dictionary,
or reflection sits on a per-tick or per-frame path and nothing allocates after
warm-up. The mirror refreshes at the settled tick boundary from the rows that
moved plus the slots still in motion, because a row version says when a stored
value changed and not when a read changes: an easing follower keeps chasing
after the write that kicked it, and an advancing or cycling cell moves with its
clock, so those slots refresh each tick until they rest. A moving slot keeps two
samples, the previous tick's and the current one's. Presentation time is the
tick plus the frame's interpolation fraction, and a cell carrying a
value-over-time trait is evaluated at that fractional time, so a parameter eased
by a 30 Hz simulation is smooth at 144 Hz. A plain cell steps, because a score
of 3.5 is wrong and an author who wants smoothness declares `dynamics`. An
offscreen capture pins the fraction to one, so a capture shows exactly one named
tick. Presentation time is a float and never re-enters state. On the GPU a
bound scalar is copied into its pass's parameter block at the offset the
interface fixes, and a bound row fills an array member in that pass's own World
group block; each is written only when its value moves. Arrays live in shared
regions keyed by row and element format, so two passes reading one row the same
way read one copy; a row bound once is indexed per instance, so a board of
sixty-four pieces is one binding; and the field lattice is a row the mirror
reads like any other, so a field has one truth on the GPU. The mirror reads
[the presentation view's](runtime-and-delivery.md#the-presentation-view) state
interface, not a document.

**A binding reads eased by default and the stored truth with `.$target`, for
every consumer.** A HUD gauge, a camera operand, and a pipeline parameter answer
one question, so they share one rule: the parsed binding carries its target
flag into its mirror slot, so no consumer can read truth where the token asked
for the eased value. Giving pipelines their own default is rejected
because it leaves two mechanisms for one decision. Changing the default later
moves camera and pipeline pixels and the parity contract, and moves no state
hash, because easing is presentation-side over the exported rows.

**A member is bound or overridden, never both.** A `views.graphs` row's
`parameters` bind a pass's config field to a literal or a state token, and its
`overrides` set the field's authored value, which a live `pipeline.set`
previews and `pipeline.commit` records. A row naming one field of one pass in
both is refused at validation, naming the row, the pass and the field. Letting
one win silently was rejected: an override that a binding overwrites every
frame is a commit that changes nothing on screen, and a binding an override
masks is state that never reaches the pass, and neither shows the author why.
An unbound field keeps the value its source's default and the row's override
give it; a bound field's fallback is its source's default, which it draws while
its binding does not resolve. A live `pipeline.set` of a bound field is refused
by the same rule.

**Residency is chosen from what the adapter reports.** The properties a policy
needs — whether device-local memory is host-visible and coherent, and how much of
it there is — are the memory profile `IGpuDeviceContext` reports, filled by each
backend at device creation. A pure function then chooses per region:
write in place on coherent unified memory, a per-frame ring in host-visible
device-local memory where it exists, and a staged copy otherwise. Nothing
selects on a platform, product, or driver name, nothing is authored, and every
policy produces byte-identical region contents. A handheld with unified memory
pays no copy for what its memory already shares, and a discrete adapter pays one
dispatch per dirty region; a design that serves one of those devices by starving
the other is wrong.

**A quality tier selects a variant of a package with the same interface.** A
tier may change how a pass looks and what it costs. It may not change what a
pass reads, what an array holds, or anything the simulation does, so the
manifest and the mirror are functions of the document alone, and two documents
differing only in tier compile identical manifests, identical mirrors, and
identical state hashes. A tier is named from the one quality vocabulary,
`QualityTiers` (`low`, `medium`, `high`), which also keys a world's
`world.quality` presets, rather than from a second one. Packages carry variants
behind that shared interface, and a pipeline's selected tier chooses its
variant alongside its bound rows. The `default` variant supplies a package's
default implementation. Tier selection changes presentation alone; it cannot
reduce the highest-quality qualification requirement.

**The offscreen host's time is its tick count.** Its frames are its only
output, so it steps one tick per rendered frame, never several ticks in one
iteration after a slow frame, and it steps the next tick only once the root
reports the current tick's frame rendered. A frame that is not yet renderable
(a cold build, a rebuild, an input with no output for the frame) holds the
tick, composing it again, so the tick-to-frame mapping is total and no frame
shows an older image for a newer tick; a refused frame releases the tick, since
holding cannot render it. A wall-clock
catch-up would make the tick a frame shows depend on how long the frame before
it took: a script that waited a few ticks could read a GPU result before it
landed, and a capture's frame could reproject across several ticks. Stepping
per frame makes the tick a frame shows a function of the script alone. The
wall clock only keeps
the host from outrunning the world's rate. The windowed host keeps catching up,
because a player's simulation keeps real time, and so does a headless
authority, whose remote peers do; a headless host renders nothing, so no frame
needs a tick of its own.

**Bound rows are priced, and a capture reports the tick it shows.**
`FixedStepPump.Advance` can run several steps in one call, so a frame composed
after one tick may show a later one. For the simulation tick a scheduled capture
already does both. It fences, because the pump ends its burst at the armed
tick through `IFixedStepSimulation.AwaitsFrame`, so the host composes that
tick's frame before stepping on; only frame interleaving changes, never the steps. The
windowed and offscreen hosts also hold their clock: they
step no tick past the armed one until the capture is served or refused, and
refuse it by name after a bounded hold. Repeated window resizes delay serving
without advancing its tick or blocking the next scheduled capture. A pending
capture presents bound state and poses at fraction one. The scheduler also
reports: `WorldCaptureScheduler` refuses a capture served by a frame showing
another tick as `stale`, naming both ticks, rather than leaving it out of the
manifest. Bindings are priced too: a world's bindings appear in the cost report
as bytes per tick and bytes per frame, beside and separate from the
simulation's cycle bound, and a document over its ceiling is refused at
validation naming the pipeline and the binding. A state-bound parameter adds a
second tick, the one its regions were refreshed at, pinned by the capture hold
along with the simulation. A capture carries it as its region tick, and
`puck parity`'s tick verdict holds it to the armed tick, ordered before the
pixel verdict, so a skewed capture fails as a skew. `puck parity` pins one
reference tier, `high`, and its `bound` station's pixels depend on a bound row
the world's own rules move. Hardware observations also run at `high` and meet
that device's worst-frame bar.

## The frame graph and nesting

**The frame graph is the centre of rendering, and the SDF engine is one pass
package in it.** The SDF engine began as the host. It composited panes in its
second stage, it owns the screen slots, it capped nested cameras through a
round-robin refresh budget, and post-processing and the overlay were nodes
chained after it. That
design grew from a prototype, and each capacity in it is a constant rather than
a planned cost. The replacement has to be better at every job the SDF engine
does. So composition, nesting, sources, and output belong to the graph, and
the SDF passes become a package the graph schedules like any other. The move
happens behind a capability matrix generated from the engine's code, so no
feature is lost by accident.

**The frame graph is a document, `puck.render.graph.v1`.** Worlds are runtime
data, and a world should be able to add a pass or a view without new C#. Shader
pipelines are already JSON documents planned by one planner, so a C# graph
declaration would be a second way to say the same thing and could not be
extended by a world. Engine passes ship as packages that the document names.

**A nested view is a graph instance, and a view that sees itself reads its
previous frame.** Every view, from the main camera to a pane to a camera on a
screen, is an instance of a graph, and nesting is one instance reading
another's output. Instances render on demand, at the extent their on-screen
footprint needs, at their own rate, and once per frame however many consumers
they have. A self-reference goes through the planner's previous-frame edge, so a
mirror shows the previous frame, and a same-frame cycle is refused because no
order of passes can satisfy it. The main view, the panes, the split-screen
seats, and every camera and session a screen shows run as graph instances this
way; a camera view is an `sdf.world` instance that reads every view, itself
included, at its previous frame, so a screen showing its own view samples the
output completed before this frame and never the one being written.

**Post passes are passes of the synthesized root graph.** A world names them in
`views.post`, each row written the way a graph document's `packages` row is,
less its ports: the root runs them in order over the composed frame, after
every view and pane is placed and before the overlay, and each reads the frame
the pass before it wrote. A `views.graphs` row per post pass was rejected. A
row is an instance that another instance reads, so a post pass written as one
would run over the world before placement rather than over the composed frame
with its panes, and each would cost an image and a node of its own. A
post-process package is declared once, in the render graph package catalog,
with its stages, members, config and interface, rather than as a manifest
beside a package, so it ships the way `place` and `overlay` do.

**Sources are classified by how an image arrives, and producers register by
id.** The three transports are uploaded, imported, and rendered, and
`Surface`'s CPU-pixel, shared-handle, and same-device kinds already describe
them. Naming a source kind after its producer, such as a brick or a desktop,
was rejected: a second emulator, a Linux capture API, or a video decoder would
each change the schema and the planner. Instead, a producer registers under an
id, and the graph names only the id and the transport.

**A source's content class decides its verification and privacy.** A
deterministic source, such as an emulator framebuffer, is exact, so it gets an
exact pixel verdict. An external source, such as a desktop or a camera, is
private and nondeterministic. It never reaches simulation state, replays, or
the state hash, and captures replace it with a fill or refuse. Presentation
sources follow the ordinary float rules.

**The pipeline owns the mapping from a hit back to a source's pixels.** The
pipeline draws the source, so it is the one component that knows the UV
layout, crop, letterbox, and warps involved, and it publishes that chain as
data. Three destinations consume the mapping:

- Simulation input inverts it in fixed point from document data, so a light
  gun aimed at an emulated game stays deterministic.
- Host passthrough sends pointer and keyboard events to an external window,
  such as an author's editor shown picture-in-picture, where floats are fine.
- Presentation hover can use GPU picking.

Passthrough exists only for a source the local user opened. A world document
arriving through a portal must never be able to type into the player's
desktop. Focus stays with the input system, which reads the mapping. The local
user opens a source in one of two ways, and nothing else counts: the
`source.passthrough open` verb typed at the host's own console, or a physical
press on the framed picture of a window capture's screen that a local seat
engaged in the world the host booted (never a world reached through a portal
or a session view), mapped through the screen's published mapping to the
window's own pixel. A physical press is a pointer press the window pump
delivers to the input filter, or a local pad's press: a binding's dispatch of a
known `gamepad.*` control that a local device reported
(`WorldPassthroughPad.IsLocalPad`). Engaging such a screen, by any principal
(a console `body.engage`, a peer, an addon, a replay), only shows the window;
the press opens it. The engage's own provenance is not what decides: a seat's
channels can be driven by a co-driving addon or a scripted signal, so the
engine cannot prove an engage was the local user's, while a press only the
local machine's own devices produce can only be. A pad counts beside the mouse
because its signals enter the input router only from the local gamepad
capture, stamped with the device that reported them; every scripted or remote
door into the router (`player.signal`, a wheel sector, an authored command, a
typed line) carries no device or no binding origin, and a probe's axis is no
gamepad source, so each is refused by name. A world document's binding page
chooses which pad control means which pointer act (move, scroll, a button, an
arrow key), and a route's `padPointer` its feel; neither makes an act happen.
The source closes when the seat no longer frames the screen, as when it leaves
(`WorldSourcePassthrough`), and the reserved chord, Control+Alt+Escape, leaves
in one step while a seat frames such a window. A document still never authors
a passthrough destination: `route.input: Passthrough` is refused by name.

**The engine's generated declarations are committed and checked against their
generator; compiled binaries are build outputs.** The includes the C# model
owns, `sdf-isa.hlsli`, `sdf-render.hlsli` and the `*.interface.hlsli` of each engine package and
SDF kernel interface, are written by `puck shaders generate` and tracked in
Git, because the engine's kernels include them from the source tree. CI runs
`puck shaders generate --check`, which regenerates each in memory and fails on
any difference, on a checked-in interface include no generator owns, and on a
package whose include is missing, so a committed include cannot fall behind
the model. A world's own pipeline passes commit nothing: the loader generates
their declarations in memory, and a package carries them. Compiled `.dxil` and
`.spv` files are ignored by Git and built by `build/Shaders.targets`, and CI
builds the packages that ship. Every build host already needs DXC. DXIL is built
on Windows alone: Direct3D 12 is its one reader, and DXC's DXIL is not
byte-stable across hosts, so a build elsewhere compiles SPIR-V only and the
cross-host check compares SPIR-V ([freshness](../reference/shaders.md#freshness)).

**A kernel nothing dispatches is deleted rather than kept for the sweep.**
`sdf-child`, `pixelate` and `viewport-composite` compiled into shipped
bytecode that no code loaded, so each build paid for them and each reader had
to discover that they did nothing. Keeping them until P14's final sweep was
rejected: dead source is not a reference anyone needs, and history keeps it.
The same rule removed the SDF-side `resample.comp.hlsl` once the graph's package
library held the one resample pass, now the `place` package. The pixelate
interface fixture under `tests/Puck.Shaders.Tests` is its own copy
and stays with the spike.

**Seats and camera views share one SDF residency, and each renders as its own
instance into its own output.** One `SdfWorldResidency` serves a world. Each
composed view is an `sdf.world` instance of the render graph that runs the
package's passes, mask through composite, over the residency's tables into its own
output. The graph places each output into its seat rect with the `place`
package. Everything a frame's views have in common is therefore shared by
construction: the program upload, the mesh bake and its meshes, the glyph atlas,
screen sources, lights, decals and volumes. Camera views carry their restricted
quality in their own pass blocks and sample screen images through their own
instance's reads. Separate residencies per seat have these costs:

- Each seat residency would upload its own program and tables, costing N times
  the table memory and N times the uploads.
- Each would plan its own mesh bake; the shared mesh library spares the
  re-bake, but every seat would still hold its own region of the same meshes.

N seats cost N instances' passes rather than one dispatch whose Z dimension is
N, and the counted work shows that cost. Layered views return only if the
counts call for them.

**The lights and the sky are generated records in World-group tables.** A
hand-numbered environment table is a second hand-kept layout beside the frame
data, and carried in every pass block it makes every dispatch bind constant data
that only the sky, shadow and views passes read. The lights table and the sky's
block and layers are C# records whose HLSL declarations are generated
from the types, written as regions that owe only the words that changed, and
referenced only by the kernels that read them, so a pass block holds the view
and frame values alone.

**Temporal reconstruction is Puck's own complete implementation.** The Steam
Deck floor needs render scale to be cheap without looking cheap, and SDF
shading is the dominant per-pixel cost. A vendor upscaler first was rejected. It is a
closed or platform-specific dependency, and it knows nothing about SDF
surfaces, nested sources, or screens showing live content, which all need their
own motion and reactive masks. The package produces the inputs vendor upscalers
expect, so one could still be added later as an alternative pass.

## Global illumination

These decisions shape [P6-GI](../plans/rendering.md#p6-gi-global-illumination),
the global illumination slices of P6.

**Indirect light is an irradiance volume traced on the GPU against the baked
meshes.** The frame walks no field, and worlds are mostly static, so bounce
lighting is cached rather than retraced per pixel. Each residency shares one
three-level nested volume (`SdfMeshIrradianceProbe.Levels == 3`) among its views.
The finest window uses the tier's spacing and dimensions; the storey level's
32×24×32 window uses 4 m × 2 m × 4 m cells and the coarsest 32×12×32 window
32 m cells (`SdfMeshIrradiance.LevelsOf`). Compute kernels traverse the CPU scene's
uploaded two-level hierarchy of static mesh draws; views and later bounce sweeps gather the eight probes around a
receiver through the same visibility rule. Moving bodies receive this light
and cast none into the static volume.

Each toroidal window reuses cell-keyed slots and publishes only once all its
slots are measured. Its centre encloses the residency's view cameras when
they fit within the window's middle, otherwise it follows the first camera.
The CPU probe cache is a cache of its window: each level keeps the probes of its
window grown by the band that holds the window still (an eighth of it a side),
and forgets the rest when the window moves, so the cache is bounded by
residency, not by where the camera has been. Full demand-point windowing and
scene scope are open work in
[residency step 2](../plans/open-items.md#residency-and-streaming).
A still cell reuses its measurement until the lighting or geometry changes.
[The handbook](../rendering/sdf/handbook/lighting-and-shading.md#the-mesh-paths-irradiance-volume)
owns its layout, costs and limits.

**Background work belongs to the engine job system.** Its budgeted jobs make
GI proportional to change: newly landed geometry re-measures only probes
whose measurements it can affect. This is the
[job-system and GI scheduling work](../plans/open-items.md#engine-job-system),
rather than a second executor.
[`SdfMeshIrradiance`](../../src/Puck.SdfVm/SdfMeshIrradiance.cs) builds scenes
on the thread pool and reuses unchanged geometry hierarchies. Its probe client
(`SdfMeshProbeGpuWork`) shares the bake's `SdfGpuWork` service: GPU selection
classifies the window's owed work, compute traces and relights it, and indirect
dispatch consumes the compacted relight list. Changed scene boxes retrace only
the probes they can reach. The GPU keeps hit words and publishes measured
records through fence-completed, scene/window-checked copies; old data remains
visible until its replacement is ready. Camera exposure queries use the same
service and shading function. CPU traversal and shading remain the diagnostic
reference, and CPU nearest-surface and short clearance queries still place probes.

Visibility is an any-hit query: GPU `sdfProbeOccluded` and CPU `Unblocked`
stop at the first accepted triangle, sharing their nearest-hit walk's node
and triangle tests. Shadows, light bits and camera visibility use this door.
The shared scene builder certifies each hierarchy at no more than
`SdfMeshProbeTrace.StackDepth - 2` edges, forcing median splits when necessary.
A hierarchy that cannot fit is refused by name. CPU and GPU walks count stack
overflow as an assertion failure rather than silently ending a walk.
Within each work priority and eight-cell distance band, probes run in Morton
cell order to keep neighbouring origins together without changing their rays.
The budget diagnostic samples median and oldest lighting age per level so a
coarse window cannot hide the finest window's response time.
Under changing lighting, the selection kernel interleaves coarse levels by a
stable cell phase. A level's age bound scales the authored target by its
interleave; reaching it forces eligibility and age relative to it takes
priority. Geometry and publication work bypass the gate. Unchanged lighting
takes whole bounce sweeps. A sweep retains its initial phase across listings,
even when later batches shade under newer lighting, and a lockstep drain always visits every cell from
no bounce. These bounds describe demand under the budget, not guaranteed wall
time when the device is overloaded.
Below its bound, a probe outside every local view's frustum gets less age
priority, and a probe whose last light changed gets more. Neither weight
removes work. Overdue probes ignore both weights. The budget diagnostic
reports ages inside the first frustum and outside it so the demand weighting
can be measured independently of whole-window ages.

Software compute is the baseline on both backends; no ray-tracing hardware is
required. The trace device law requires exact identities and discrete flags,
scene-scaled float tolerance and independently intersected near ties, with an
injected skipped subtree as its negative control. It qualifies its corpus,
not every possible authored geometry. A settled capture remains waiting while
its prepared lighting or exposure is pending; background work continues to
completion before that frame can certify a still. Throughput and the one-frame
lockstep target remain measurement obligations in the
[graphics plan](../plans/graphics-architecture.md#g1-the-gpu-probe-trace-and-relight).

**Visibility is measured as local first-hit surfaces.** Each probe holds a
12×12 octahedral surface map. One bounded closest-hit ray per texel stores an
FP16 distance and a packed surface normal; a negative distance marks a miss
and its measured reach. Neighbouring samples fit a cached normal-gradient
curvature per texel. Four reflected angular taps reconstruct local surface
patches and test the receiver against them, with a smooth surface-gap and
coverage fade. A finite blocker affects its sampled angular footprint rather
than extending an infinite plane through the scene.

[`SdfMeshProbeDepth`](../../src/Puck.SdfVm/SdfMeshProbeDepth.cs) and the views
shader's `sdfMeshProbeDepthVisibility` share that rule. Gather weight is the
trilinear share times a smooth distance fade from probes behind the receiver
to probes half a cell ahead of its surface, times depth visibility. The fade
uses the receiver normal and the relocated probe position. A fixed 5 cm normal
bias stays independent of cell spacing. The lookup clamps to boundary probes,
but visibility tests the actual biased receiver. Outside a window the facing
fade eases to one over the first cell, so normals turned away from the window
can still receive its measured light.

**Coarser levels supply reach without overriding fine visibility.** A receiver
uses the finest published window containing it within its boundary probes,
or the coarsest published level beyond all windows. A coarser level never
answers inside a finer window: its wider angular footprint could miss a thin
wall the fine level measures. If the selected level's bounded gather has no
support, the same probes extrapolate past measured reach: a measured surface
still blocks, while a missed ray no longer invents a far blocker. This read
reports `measured=0` and supplies no traced light visibility. It
estimates unmeasured light rather than painting every far surface black.

**A level answers only in the share it measured.** The chain's first stage
that answers contributes its share: its weight over the same cage's front
weight before visibility, clamped to one. The rest comes from the next stage
over another cage, and only the last stage that answers is normalized to what
remains; a receiver reads sixteen probe slots, pre-scaled by their stages'
shares. A finer level answering past its window keeps its whole share over
the inner half of its surface reach and eases to none at the reach, so the next
stage takes a receiver over across a band rather than at a line one probe row
tall. Levels are a table per tier with a spacing per axis: the middle level's
4 m × 2 m × 4 m cells put a probe plane in every storey 2.4 m or taller,
which no isotropic coarse level does (an 8 m level had one in every other
4.5 m storey, and its single corner across a slab lit a whole storey). The
[far-field memo](../plans/far-field-irradiance.md) holds the measurement and
the research behind this rule.

**A coarse tap on a sheet closes; a coarse tap on a solid's near side does
not.** One visibility rule holds at every level: the taps that block a receiver
on solids close it only as a majority, and the taps on sheets close it at
almost any share. The trace marks a coarse level's hit a sheet when the scene's
exact distance shows no solid thicker than 2 m behind it (a surface lies within
0.45 of that thickness of the point half of it inside): a slab, a wall or a
slab's edge is a sheet, with another space beyond it; a hill's near bulge, which
a coarse probe's metres-wide taps alias in front of the slope it sees, is a
solid. A conservative ramp at every coarse tap darkened one clear corner in six
on curved hills; a tolerant one let the 32 m level light a storey through its
slab. Measured along the normal alone, a slab seen edge-on reads twelve metres
thick, so the test is the solid's medial thickness, not its depth along a ray.
The finest level marks no sheets: its half-metre taps resolve slabs themselves,
and its minority tap on a thin finite object (a railing, a hanging sign) lies
beside the receiver's segment.

**A view with no cascade takes the sun's visibility from the volume.** A view
that draws no directional cascade (a session or screen camera's reduced quality,
or `world.shadows off`) reads each shadowing directional light's visibility from
the probes about the surface, as a traced point light's: every probe records
whether its own ray toward the light is clear, recomputed on each relight, and
the surface takes the weight of the measured probes that see it. Without it the
100 klx sun lit every interior face turned toward it.

**Relocation takes the nearest clear candidate.** An embedded or too-near
centre first tries an exit from the nearest surface, then quarter-cell grid
candidates in increasing distance order within half a cell on each axis. Each candidate
must clear nearby geometry and a 32-ray short-range back-face check before
its full radiance measurement is accepted. There is no openness score. The
surface map, sky and radiance cubes, and light masks use that same origin.
Only each probe's own measured cubes publish; neighbouring cubes are not
borrowed through a doorway. Sky visibility comes from the full-reach radiance
rays with their cosine weights, not from the bounded surface-map rays. The
exposure meter keeps its exact camera-to-probe scene tests.

The [mesh-GI reference](../../.claude/skills/rendering/references/mesh-gi.md)
owns packing, sampling and the CPU/shader sync contract. Surface samples have
finite angular resolution and do not certify geometry below it.

`world.probe` marks changing or unpublished reads unsettled and names the
published and replacement windows. Lighting comparisons use settled bakes and
irradiance, fixed matching cameras, and both Direct3D 12 and Vulkan.

**Considered, and set aside.**

- **A world-space radiance cache traced through the field.** Its probes and
  receivers proved every launch and every sealed boundary through the
  interpreter, so each ray paid the whole field walk on the GPU, and it needs
  the field in the frame, which the frame does not walk. It was
  replaced by the volume; its design is in the repository's history.
- **Screen-space indirect light over a probe fallback.** It is per view and
  view-dependent, so split screen, mirrors and portal windows multiply it and
  disagree, and it cannot see behind a wall from the camera.
- **Radiance cascades.** In screen space they are per view and per frame. In
  world space a dense volume of short intervals costs memory and updates in
  proportion to volume rather than surface area.
- **Per-pixel path tracing with reservoir reuse.** Several traces a pixel a
  frame, per view, is beyond the floor device's budget.
- **Indirect light baked per prototype or into lightmaps.** Either bake is
  static, while lights key on clocks, worlds are edited live and a
  bounce depends on a placement's neighbours, not its prototype.

**Off is the image without it.** With indirect light off a view takes the
authored fill, and no volume is traced.

---

[Decisions](README.md) · [The programme](../plans/rendering.md)

## In-world devices and screens

**An engaged screen is the in-world screen, framed until it passes for an
emulator (the owner's ruling).** Engaging eases the seat camera onto the
screen and mattes the window to the screen's picture; no separate 2D image
stands in for it. Framed, the picture is source-exact: whole window pixels per
source row, no CRT treatment, post or bezel on those pixels, and the matte's
fill around it, so the window is indistinguishable from a desktop emulator.
Easing out fades the room (and a screen material's treatment) back in, which
is the reveal that the player was in the world all along. One per-view value,
the screen fidelity the ease drives, turns the treatment and post off, and the
root's own placement of the seat's view shows only the picture's part of it, so
no pass draws the source a second time. A 2D emulator view that cut to the
source after the ease was rejected: it was a separate image that could disagree
with the screen (it cropped lines the screen showed).

**A device is modelled at the size a person who used one expects, and its
screen glass is exactly its picture (the owner's ruling).** A screen row's half
extents are the source's pixels times the device's pixel pitch, the pitch
carrying the pixel aspect (a 4:3 tube for a 256 by 240 console gives 8:7), and
the bezel lies outside the glass. Because the camera alone sets how large the
glass appears, no object is ever resized for a window. Every device is shown at
its real size (the owner's ruling), the pitch the real one, so the glass is
exactly the picture. A small device reads by how it is displayed, never by
being enlarged: the debug world stands its handhelds on low plinths near the
walkway with an angled name placard, the way a museum shows a small object.

**A screen's look is an authored screen material, and its picture always fills
the face.** The engine's screen shading carries one display treatment, the
display's own pixel structure drawn over the picture (scanlines, subpixel
stripes, a vignette, a glint and a soft rounded edge), whose every term a
document's `screenMaterials` row authors and a screen row or creation face
names by id; a screen naming none shows its picture untreated. A CRT is the
standard package's `crt` material (`worlds/standard/skin.puck`), never an engine
value. No material insets or warps the picture: a device's glass is exactly its
picture, so a material is presentation alone and never moves the mapping a
pointer hit takes, and a bezel is the device's geometry round the face.

**A screen lit by external content keeps lighting the room live (the owner's
ruling), and captures complete anyway.** A desktop or camera screen's glow
follows its pixels; every capture frame discards and re-derives each retained
state that holds external content, so no unfilled pixel or light reaches a
capture and no capture waits on it.

**Glass is a surface of its own after opaque visibility (the owner's ruling).**
A see-through shell, a window pane and a screen's front glass are drawn as
transparent surfaces over the opaque image, not painted fakes.
