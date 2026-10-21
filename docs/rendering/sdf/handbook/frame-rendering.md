# SDF frame rendering

One world frame turns an SDF program into pixels without walking the field per
pixel: the residency bakes the program into meshes, and every view rasterizes
those meshes and shades the rasterized surface. A frame runs a fixed sequence of
compute and graphics passes: the bake's mask and sampling passes while a bake is
pending, the shadow cascades, the mesh raster, the surface and views passes that
shade each view's hits, and the sky and composite passes that finish its image.
The sequence exposes where the GPU work goes, how render-scale tiers trade
resolution for frame budget, and how frames stay in flight without stalling the
whole device.

## One render pipeline

A world frame has two halves. The scene's shared tables — the program, the
moving transforms, the screens, lights, volumes and mesh draws — live in one
**residency** (`SdfWorldResidency`) per frame source, and the frame first
submits one **upload** that brings those tables up to date. Then every view of
the scene is an instance of the render graph's `sdf.world` package, and its node
records the package's eleven native passes into its own submission, reading the
tables the upload wrote. The passes finish that view's own output image:

```text
   upload → mask → bake → shadow-near → shadow-far → shadow-second → mesh → pyramid → surface → views → sky → composite
```

The render graph plans the view's passes like any other graph: the package
declares them as a fragment (`SdfWorldPackage.NativeFragment`) that the graph
compiler splices into the view's graph, and the planner decides every barrier
between them. `world.counters gpu` reports the upload under the residency
(`sdf:world` for the world's) and each view's passes under its instance, as
`sdf.world$mask` through `sdf.world$composite`. Here is what each pass does;
[the engine README](../../../../src/Puck.SdfVm/README.md) describes the
visibility records the surface pass writes and the later passes read: one per
pixel of each view's render grid, 32 bytes. A view whose render-scale ceiling is
below native puts the full-output `resolve` pass described under
[render scale](#render-scale-tiers-trade-resolution-for-frame-time) between
views and the sky, and a view that reconstructs over time does so at any scale
([temporal reconstruction](#temporal-reconstruction)). The sky and the composite
are described under [the sky once, and a composite last](#the-sky-once-and-a-composite-last).

**mask** and **bake** run only while the residency's bake is pending
([the bake](#the-bake)): `sdf-mesh-bake-mask` writes the instance selection of
each lattice the frame samples, and `sdf-mesh-bake` samples those lattices
through the interpreter (`mapCore`), the frame's one walk of the SDF program. A
frame with no pending bake skips both, so a settled scene evaluates no field on
the GPU at all.

**shadow-near**, **shadow-far** and **shadow-second** (`sdf-mesh-shadow.vert.hlsl`,
`sdf-mesh-shadow.frag.hlsl`) draw every mesh draw from the light cameras
`SdfMeshShadowCascades` fits ([the shadow cascades](#the-shadow-cascades)) into
their depth alone. A frame with no mesh draw, or whose view has no cascade,
skips them; `shadow-second` runs only while a second stable shadow slot holds a
directional light, and a view whose world holds one stable slot has no
`shadow-second` at all.

**mesh** (`sdf-mesh.vert.hlsl`, `sdf-mesh.frag.hlsl`) rasterizes the frame's mesh draws, one draw call
each, into the mesh visibility target at the view's extent: per pixel the ray parameter,
the draw plus one, and the triangle, kept nearest by a reversed-Z depth test. The draw plus one and the triangle are
stored as floats, so a region holds at most 2^24 draws and a mesh at most 2^24 triangles, each refused by name past
that. A frame with no mesh draws skips the pass, its target's and depth's barriers included. The target and its depth
attachment, 20 bytes a pixel, are scratch the view's instance allocates with its graph.

**pyramid** (`sdf-mesh-pyramid.comp.hlsl`) reduces the mesh target into the
depth pyramid the surface pass's occlusion reads (`frame/sdf-mesh-pyramid.hlsli`):
levels one to four, each texel standing for its block by one actual pixel of
it, whose ray parameter and coordinate it holds, the pixel chosen on a rotated
grid that turns with the occlusion's pattern step. One invocation a level-one
texel writes every level its pixel represents, so no level reads another. The
levels lie in 8×8 tiles in one buffer, two words a texel. A frame with no mesh
draws skips the pass.

**surface** (`sdf-world-surface.comp.hlsl`) calls no field query. It writes
every pixel's whole visibility record from the mesh target, misses included:
the ray parameter, identity, material, triangle and the draw's frame slot; the
normal at the hit point (the vertex normals interpolated when the mesh carries
them, carried to world space by the draw's inverse-transpose normal matrix so a
nonuniform scale keeps them perpendicular to the surface; the face normal
otherwise), turned toward the camera and clamped so a grazing ray never sees it
lean away; a screen-space ambient occlusion, eight taps over the disk the
occlusion radius projects to, each rebuilding the surface point its pixel's ray
reached, from the mesh target within sixteen pixels and past that from the
pyramid level whose blocks are an eighth of its reach across, and occluding by its elevation above the hit's
tangent plane, so a plane never occludes itself; and the stable shadow slots'
visibility into the K word, a percentage-closer soft shadow from the first
cascade holding the point: a blocker search sizes the penumbra from the light's
penumbra slope and the gap to the blockers, and a filter of that width answers,
each tap against the receiver's own plane from the hit triangle's normal
([soft shadows](lighting-and-shading.md#soft-shadows-cascades-and-a-penumbra-filter)).
The occlusion and each shadow are one tap pattern's estimate per pixel: each
pixel turns and spaces its taps (the occlusion's spiral, the shadow's blocker
search and filter disks) by its cell of a 4×4 ordered-dither tile
(`sdfMeshPattern`), stepped within the tile's strata by a temporal view's sample
index, and views filters them.

The surface record also carries mean curvature for edge weathering. Half the
surface divergence of the interpolated unit vertex normals estimates it in
inverse world units, using the triangle's world-space barycentric gradients.
This reuses the three normal reads, with no field query or adjacent pixel
sample. Its sign follows the authored outward normal, independent of the
camera-facing shading normal. Flat normals give zero; collapsed and nearly
collinear triangles also return zero. It describes the baked normal field,
so a bevel missing from those normals cannot contribute curvature.

**views** (`sdf-world-views.comp.hlsl`) calls no field query either. It reads
each record and computes materials (the draw's palette entry), lighting, the irradiance volume's
indirect light and the bounded volumes. Before shading, each 8×8 group gathers
its pixels' records with a two-pixel apron into group-shared memory, and each
pixel averages the surface pass's occlusion and K over the 4×4 window around it
(`sdfViewsFilter`), which holds each of the tile's sixteen patterns once, so the
per-pixel noise cancels to one smooth estimate with or without temporal
reconstruction. A neighbor joins the average only while its surface point lies
within two pixel footprints of the pixel's tangent plane, so neither crosses a
depth edge or a silhouette. `world.debug-view ao` shows the filtered occlusion
and `world.debug-view shadow` the key light's filtered visibility. A
view whose quality (`SdfViewSnapshot.Quality`) turns ambient occlusion or soft
shadows off reads none of the record's occlusion or K; quality is each view's,
so views of one frame render at different cost.

Every in-extent record is the frame's own: the surface and views passes
dispatch one 8×8 group per block of the render grid, with no culled region and
no indirect arguments.

No pass assembles views. Each view's output is its instance's own image, sized
to its output extent. Reduced views reconstruct their current color in `resolve`
before the render graph's `place` pass puts that output in its seat rect. In split screen each view is an instance of its own (`world`,
`world$2`, and so on) over the one residency, so the graph schedules and places
the seats the same way it places panes.

## A cadence per pass

Each pass has a signature for the inputs it reads. Private fragment resources
retain their last queued writes across frame slots. A pass stands only while
its signature, extent and retained inputs and outputs remain valid. A standing
pass records no dispatch or barrier and its counter row reads `standing`;
the next executing reader follows the last actual access through the existing
resource tracker. A disabled optional pass reads `skipped` instead.

Camera-only cloud drift, twinkle, gradient colour and moving bounded media change
only `sky` and `composite`. Fog density, light colour and the ambient/reflection
gains also change `views` and `resolve` when present. A lighting-visible sky edit
refreshes the shared environment at the irradiance threshold described in
[The environment map](#the-environment-map); the submitted map's
revision also changes those lighting passes. Lighting-visible panels affect
analytic reflections directly, so their edits change lighting passes even below
the map's threshold. A selected shadow direction adds the cascades and `surface`;
geometry or camera changes render every active pass. A sky layer that
samples a screen (a panorama, a textured disc) runs `sky` and `composite` every
frame, since the screen's image changes in place where no signature sees it;
the raster and surface passes still stand. The bake's passes record whenever
they run. `world.lighting` reports each keyed value's change class, including
fields keyed through a section.

Temporal sampling renders the geometry again while a sample is owed. Once
lit history converges, visual edits leave its sample count and ring standing.
`world.cadence off` forces active passes to render for measurement.

## What each pass costs

The passes scale with different things. `mask` and `bake` scale with the
field evaluations a frame's lattices take, which `SdfMeshPathBake.FrameBudget`
bounds, and run only while a bake is pending. The three cascades and `mesh`
scale with the triangles drawn and the fragments they cover. `surface` and
`views` scale with the render grid's pixels: every pixel runs the same fixed
occlusion and shadow filter taps, and a hit pixel's lighting scales with the
lights and volumes it reads. `composite` scales with the output's pixels and
`sky` with the uncovered ones.

Each pass exposes `gpu.shapes.evaluated` beside `gpu.march.steps`; on the
baked-mesh path only `bake` evaluates shapes, so a settled frame counts none.

## Render-scale tiers trade resolution for frame time

When the shading epilogue is the cost and you need the frame to fit a tighter
budget, the lever is to render a view at *reduced* resolution and upsample it
afterwards. Each view carries a `RenderScale` ceiling inside its output extent.
The graph keeps the view's footprint at its native rect size, and an authored
camera or session resolution stays exact even when its reader shrinks. The
camera aspect uses that authored width and height from the first frame.

The ceiling alone decides which passes a view runs and what it allocates.
Traversal and shading use the current render grid inside the ceiling's
allocation (`SdfViewSnapshot.ResolvedRenderScale`, bounded by the ceiling). A
smaller current grid changes dispatch dimensions and the visibility stride and
nothing else: no target is reallocated and no graph is rebuilt, so the grid can
move every frame. A layout transition's dip moves only this grid. The shaded
color at the render grid is one retained allocation that every frame slot
shares. The final `resolve` pass writes full-output color with the same
bilinear and clamped Catmull-Rom filter as `place`; coverage remains the
color's alpha. A spatial resolve writes nothing beside the color; the temporal
resolve keeps a per-pixel surface (ray distance and identity) at the output
extent as its history.

The view's output has the extent the render graph schedules for it, which is its
rect's native extent quantized to the scheduler's steps. `place` copies that
output where it equals the rect's pixel extent, as it does for a whole-display
view or one half of an even display. Otherwise `place` resamples it into the rect, so a reduced
view in such a rect is filtered twice: once by `resolve` and again by `place`. A
lone whole-display view can stand directly, including at a reduced internal
render scale.

A view's camera maps the image in normalized coordinates, so the aspect it
projects is the aspect of the rect it was composed for, never of the grid it is
traversed on. A reduced grid, a transition's dip or an output still at its
previous extent while a resize builds changes only how densely the view samples
that rect, and `place` stretches the output back into it. The one image shown at
its own extent is the root's, which the host presents as the display: a display
resize composes the cameras for the new extent at once, so until the root's graph
installs that extent the root presents its last image and a capture waits for
the first frame at it
([loading and installing](../../../reference/shaders.md#loading-and-installing)).

A view whose ceiling is native and whose quality does not ask for temporal
reconstruction keeps the ten-pass native fragment and writes its output
directly. It allocates no resolve resources and ignores the current
grid, so a layout transition does not dip it. A changed ceiling rebuilds the
view's graph beside the installed one, which presents its last image until the
replacement installs. A reduced view adds one output-sized dispatch; its memory
account includes the output beside the render ceiling, and its scheduling price
sums the passes' current grids.
Render scale is *presentation only*: it never touches simulation state, and which
tier a view uses is a host decision, not baked into the content. In `Puck.World`,
`world.render-scale [view]` sets a view's ceiling, with the world's default
applying to every player view and a camera or session view keeping its native
extent until a lever or a `views.quality` row names it, and
`world.upscale-sharpness` sets the reconstruction blend.

### Dynamic resolution

With dynamic resolution on (`world.render-scale [view] auto`), one policy,
`WorldDynamicResolution`, moves a view's render grid each frame between the
view's floor and its render-scale ceiling. Every view owns its own controller,
with its own history and a load source that reads only that view's submissions;
`auto` without a view applies to the world's own player views (`world`,
`world$2` on), and a camera or session view adapts only when a lever names it.
`world.render-scale [view] pin <scale>` holds a view's grid instead, as a
session pin that no load moves and that enters neither a save nor a replay;
`auto` releases it, and the policy resumes from the pinned grid within one step.
The grid is
`SdfViewSnapshot.ResolvedRenderScale`, the same grid a layout transition dips,
so the two compose; it moves inside the allocation the ceiling sized, so no
frame reallocates, rebuilds or resets history. A view at a native ceiling
reconstructs nothing, so while dynamic resolution is on a native tier
allocates its views at three-quarter.

Each fresh load sample moves the grid through one response: within 10% of its
budget the grid holds, and outside it the grid moves toward the scale whose
area meets the budget by at most a sixteenth of itself down or a thirty-second
up. The sample is the view's GPU frame time against the display period, from
the same pass timestamps `world.gpu-timing` reads; a present-paced swapchain
reports every kept present as exactly its period, so only the GPU's time shows
the headroom to raise the grid again. A device that times nothing falls back to
the present interval, and a host with neither, an offscreen one, to the view's
counted march steps against the floor tier's committed ceilings per output
pixel. Counted work per frame then scales with the grid, which
`world.counters gpu` shows as the sky pass's texels written.

A sample counts only at the grid the views render now. Each view's node
records the quantized grid of every submission it renders, and a reading,
summed over each view's renders not read before, names their common grid; a
reading from another grid, such as one read back after the grid moved, is not
a sample, and a view standing on a render already read adds nothing. While a
standing view still owes a readback, the runtime polls its completed counters
and timestamps each frame, so its final submission becomes readable when its
fence signals; a view that owes nothing is not polled at all. A present
interval names no frame, so each view's node also keeps a summary of every
render completed since it was last read, and an interval counts only when every
view's summary names the current grid. A view removed from the graph hands its
completed renders to the runtime first, so a render it finished before leaving
still counts in the next read; the runtime keeps them only for the views the
controller reads, never for a pane or a source. Render completion fences survive an install's
counter invalidation; device loss drops renders whose completion is
unobserved. When the budget lies between two
adjacent grids, the controller settles on the cheaper one rather than
alternating: a sample over the budget marks its grid, and the grid rises onto
the mark only once a sample, compared exactly against the budget, predicts it
within the budget.

## The sky once, and a composite last

Views shades only the pixels a surface covers. It writes the **lit image**: each
pixel's shaded color premultiplied by its **coverage**, with the coverage in the
alpha, which is one for a hit and zero for a miss; a rasterized silhouette has no
partial coverage. The color has already passed through the atmosphere
between the camera and the hit (see [surface transport](#surface-transport)). In a
reduced or temporal view the resolve reconstructs the
lit image, coverage with color, and each pixel's surface transport at the output
extent.

The sky is an open stack of up to eight layers that compose in their authored
order, each by its blend (`over`, `add`, `multiply`, `screen`), a kind as often
as authored. A kind is a parameter record and one module under `Sdf/sky/kinds/`,
registered in the generated kind table the kernels switch on, so adding one
touches no pass. The stack is cut into **runs** without reordering it: a maximal
sequence of consecutive **field** layers (gradient, clouds, aurora, noise,
pattern, panorama) is one field run, and the **point** layers between them
(stars, a body's disc, an infinity view) are evaluated one by one. An infinity view (`view`, another
world, and `far`, only named prototypes of this one) is a second `sdf.world` instance whose image the layer samples
at the tangent each pixel's direction has on the viewer's basis, inside the rectangle of the camera plane the layer's
cone covers; before the instance has an image the layer draws its fallback colour, and either way a pixel counts a
shown texel in the layer's detail row, which demands the instance's next frame.
A `far` instance publishes only geometry: premultiplied color and coverage in
alpha, with no background sky or air on uncovered rays. Its covered surface
still takes the normal atmosphere and bounded media. The consuming layer filters
color and coverage together, then unpremultiplies before the existing layer blend
applies coverage once. Another world's whole view remains opaque.
The binder reads the authority's existing named-layer observation and gives each
planned layer one scene and residency. Each consuming camera fits its own output
after its frame's cameras and sky quality resolve; a second camera shares the
scene and the original nesting/cap decision. The shown world's physical screens
use the existing nested-screen sources and session admission. An unavailable
observation keeps its fitted fallback without publishing an unresolved graph
dependency. Only a completed, executed composite changes visibility demand;
standing frames retain it. Each sky recorder binds eight fitted records
(40 `float4` values, 640 bytes) beside eight images, indexed by packed layer
ordinal rather than an authored screen number.
Each distinct layer label keeps its own counter row across live edits and reloads;
retired labels retain their identities until the composition is disposed. The
existing counter buffers grow after their frame slots complete, and their actual
allocation and readback bytes remain counted. Every blend is affine in the
color beneath it, so a field run is summarized exactly as one per-channel scale
and offset, and the runs compose as the stack does (`SdfSkyRuns`, held by
`SkyRunCompositionLawTests`). Each layer carries its own opacity, mask (an
elevation band or a cone), transform, clock, visibility (the camera, the lighting
or both) and the lowest quality tier it draws at; the sky frame turns every layer
but a disc.

- The `sky` pass (`passes/sdf-sky-runs.comp.hlsl`) evaluates the field runs on
  the render grid, and only where the pixel or one of its eight neighbours is not
  wholly covered by the color views wrote: the lowest field run's offset, then at
  most two upper field runs' scales and offsets packed six half floats a run,
  the base's alpha marking the texels it evaluated. A pixel covered with all its
  neighbours evaluates no field runs, and each layer evaluated counts one
  `gpu.sky.evaluations` in its own detail row. A reduced view's sky therefore
  costs its render grid's uncovered pixels, not its output's.
- The `composite` pass (`passes/sdf-composite.comp.hlsl`) writes the view's
  color. It adds each [atmosphere](#the-atmosphere) kind's glow, its in-scatter
  colour scaled by the surface transport's weight for it, reading the sky the
  fog and the haze in-scatter, the sky the lighting sees, from the residency's
  [environment map](#the-environment-map) rather than evaluating it, so the
  atmosphere counts no sky layer's evaluation; each kind it evaluates at a pixel
  counts one `gpu.sky.evaluations` in the `atmosphere` detail row, and a zero
  weight evaluates nothing. Where the coverage is below one it composes the
  stack beneath the lit image in its authored order: each field run's summary
  filtered from the texels the sky evaluated (or every field layer evaluated in
  place, and counted, where it evaluated none beside the pixel), and each point
  layer evaluated at the pixel so it stays sharp, passes them through the haze
  and the medium to the far distance, then puts the lit image over them by its
  coverage, so a silhouette blends toward the full sky at its pixel. The bounded
  media integrate last, over each share of the pixel separately: the surface
  share up to the surface transport's distance, and the sky share up to the far
  distance.

An unauthored world renders the default look: the two-stop gradient `SdfSky`
starts from and the default atmosphere's fog (`SdfAtmosphere.Default`), read like
any authored sky. A debug view's lit image is its whole picture, so it passes
through no atmosphere, the sky evaluates nothing and the composite passes it
through.

### The atmosphere

`render.atmosphere` is the air between the camera and what it sees, written into
the sky block's atmosphere lanes (`SdfAtmosphere`, packed by
`SdfSky.PackAtmosphere`). An absent section is the default look's fog; an
authored one is exactly the kinds it states, each off at zero:

- The **fog**, `density` per world unit, alike at every height or, with a
  `height` profile, falling by a factor of e over each `falloff` above its
  `base`. It in-scatters the sky in the pixel's direction, or its own `color`.
  It ends at the sky, which is its colour at infinity.
- The **haze**, aerial perspective, takes its `amount` of the light along a
  level ray the far distance long and in-scatters the sky and the light of every
  directional light, which is what a light-casting body binds, by a
  Henyey-Greenstein phase of its `anisotropy`, so it glows toward a low sun. It
  lies before the sky as well as before the surfaces.
- The **medium**, water below a level `surface`, has its own `extinction` and
  in-scatter `color`. The fog and the haze fill the air above it, so a ray
  crossing the surface passes through the air and the water in order, and a
  camera below the surface sees the world and the sky through the water.

Each kind's optical depth along a ray has a closed form: a level kind's is its
density times the length, a height profile's is
`density · e^(−(y₀ − base)/falloff) · length · (1 − e^−x)/x` with
`x = dy · length / falloff`, which a ray parallel to the base (`x = 0`) takes as
its series, and the medium's is its extinction times the length below the
surface. The ray's transmittance is the exact product of its segments'. Within
the air the fog and the haze share the air's in-scatter by their optical depths.
`SdfAir` is the CPU reference for `shade/sdf-atmosphere.hlsli`, held by
`SdfAtmosphereLawTests`.

The bounded media a creation authors scatter the same bodies' light: a volume's
`scatter` is the share of each sample's extinction that scatters the sky block's
air lights toward the eye, by a mildly forward phase, beside the emission its
ramp gives it. The air lights are the first four lit directional lights, baked
with their radiance into the sky block, so the composite branches on no light
kind.

### The environment map

A residency keeps a 64 × 64 octahedral environment and nine second-order
spherical-harmonic coefficients per colour channel. The first map plane holds
the lighting-visible stack for fog and projection; the second omits panels for
reflections, where analytic angular rectangles preserve sharp highlights.
The planes share one evaluation of each lit layer per texel. Discs never enter
the environment. The payload is 65,536 bytes of map and 144 of coefficients,
shared by every view and frame in flight (`SdfWorldTables.SkyEnvironment.cs`).

One `sdf.environment` graph instance per residency produces both buffers before
its lighting consumers. It acquires the canonical observed view's panorama
images through the existing graph reads. Independent image feeds remain current;
world-derived images, including postprocessing chains, use their previous
publication to break feedback through the environment. The usual view reads
still demand those producers. Map and coefficients are separate named exports.
The source identity is the producer's owner and completed sequence acquired
with its image lease and fence. A changed publication or sampling map refreshes
a lit panorama even when its packed sky parameters are unchanged. The CPU
reference refuses panoramas; it supplies no invented image projection.

For procedural layers, the host projects a changed lighting-visible candidate using the same packed
layers, half-float map and solid-angle weights as the kernels. It compares the
candidate with the last rendered coefficients: the largest absolute irradiance
difference over every normal and RGB channel must reach 1/255 before the map
and coefficients render again. The maximum is the extremum of each channel's
quadratic on the unit sphere (`SdfSkyEnvironment.IrradianceDifference`).
Sub-code changes accumulate against the rendered sky; camera-only changes and
a still sky perform no projection. A kernel reload invalidates the held result.
With ambient and reflection both zero, no sky-coloured fog, no haze and no
physical transport consumer, even candidate projection is off. An authored fog
colour does not read the map.

Each candidate counts `gpu.environment.projections` and 4,096
`gpu.environment.projection-texels`; a sub-code candidate also counts
`gpu.environment.skipped`. A rendered change dispatches the map once and its
reduction once. An active lit layer counts one evaluation per map direction inside its
mask and directional domain: stars and clouds exclude the horizon and lower
hemisphere, and the aurora excludes directions below its lowest possible
curtain. Every rendered map writes 8,192 map texels and nine coefficient texels.
No dispatch runs on a skipped candidate.
The first map texel is integrated analytically as a constant and subtracted
before quadrature, so a constant sky has exactly zero higher bands.

The views pass convolves the coefficients by π, 2π/3 and π/4 for bands zero,
one and two, then applies the existing normal-ladder AO and the
`render.environment.ambient` gain. A constant radiance C gives irradiance πC.
The shared `sdfSkyPhysicalIrradiance` helper returns that irradiance before the
gain; physical diffuse transport divides it by π. `sdfSkyPhysicalRadiance`
filters the first, full map plane without the reflection gain. Both helpers
read the same environment and count their own lookup work once.
Reflections bilinearly sample the panel-free plane and apply the lighting
panels analytically in their authored order, under the
`render.environment.reflection` gain. Both gains default to one and zero
skips their work. Analytic panels use their layer frame, visibility, opacity,
mask and blend; roughness widens their angular rectangle and reduces its gain.
The map supplies the background beneath these reflection panels.

The composite filters the full plane for fog, including lighting panels.
Octahedral edge taps fold onto the adjoining texels. Queue barriers order all
views' reads before the next environment write; no view owns a second map.
The producer publishes its submission sequence with the actual reduction fence,
and completion is latched only when that fence signals. Displaying a view does
not establish environment completion.

### Surface transport

The atmosphere and the media depend on how far away a surface is, and a reduced or temporal
view's output pixel is a weighted blend of several render samples, each at its
own distance. A single distance per output pixel cannot describe that blend: at
an edge where a quarter of the pixel is a wall 100 units away and the rest is
sky, the sample under the pixel's center may be the sky's, and the wall's quarter
would then receive no atmosphere at all. Instead each render sample carries its own
transport, in the same premultiplied form as its color, so that blending samples
blends their transport by exactly the same weights.

For a sample with coverage `a`, color `C` and ray distance `t`, the atmosphere
lets through `T` of the surface's light and adds each kind `k`'s in-scatter
colour `Gₖ` by its weight `Wₖ`, where `T + Σ Wₖ = 1`. Its contribution to the
pixel is `a · (T · C + Σ Wₖ Gₖ)`, and the uncovered share `1 − a` shows the sky
`S`. Each `Gₖ` is a function of the pixel's direction alone, so for a weighted
blend of samples `i` with weights `wᵢ`:

```text
Σ wᵢ · [aᵢ (Tᵢ Cᵢ + Σ Wᵢₖ Gₖ) + (1 − aᵢ) S]
  = Σ wᵢ aᵢ Tᵢ Cᵢ  +  Σₖ Gₖ · Σ wᵢ aᵢ Wᵢₖ  +  (1 − Σ wᵢ aᵢ) · S
```

Each sum is linear in the samples, so each can be filtered like color. Views
writes the first, `aᵢ Tᵢ Cᵢ`, as the lit image's color; the alpha holds `aᵢ`.
The resolve computes each kind's **in-scatter weight** `aᵢ Wᵢₖ`, the fog's, the
haze's and the medium's, from each sample's own visibility record along the
output pixel's ray, and reads them beside the color at every tap of the same
reconstruction footprint (`reconstruction.hlsli`'s footprint and running sum); the
temporal path sums them with the same Gaussian weights and reprojects them with
the same history weights. The composite then adds each `Gₖ` times its resolved
weight and the sky runs times one minus the resolved coverage, so a pixel's
atmosphere is its samples' atmosphere, exactly, whatever the footprint. The
colours are read once at the output pixel's own direction, the sky from the
environment map, as the sky's runs are read from their images. A kind carried
together with another would take the other's colour, which is why each is
carried apart.

A bounded medium does not blend the same way, because whether it lies in front of
a surface depends on that surface's distance. The composite therefore splits the
pixel into its surface share, of the resolved coverage, and its sky share, the
rest, and clips each share's media at its own end: the sky share at the far
distance, and the surface share at the harmonic mean of its samples' distances,
which the transport carries beside the weights, `aᵢ / tᵢ` (scaled to stay
precise as a half float). Where a footprint holds one surface, that mean is the
surface's own distance, so a medium behind an edge reaches only the sky share
and never paints over the surface. Where a footprint spans a step between two
surfaces, the harmonic mean lies toward the nearer one; a medium lying between
the two is the one case the clip does not reproduce sample by sample.

The transport is two words of four half floats an output pixel, and the history
surface fits it in its four words by holding the distance and the weight as half
floats. A pixel the resolve copies whole from one render sample, which every
pixel is when the output has the render grid's extent and no jitter, as on the
first frame of a temporal epoch at native scale, carries that sample's ray
distance in the first word instead, marked by its top bit. The composite then derives the sample's transport at the
same line of code, with the same `precise` arithmetic, that a native view's
composite runs on the same sample, so that first frame equals the spatial frame
to the bit on any GPU rather than within the rounding of two half floats. `SdfSurfaceTransport` is the CPU reference, and
`SdfSurfaceTransportLawTests` hold the blend to the per-sample result. Because
each hit's transmittance is in the lit image, a change of an atmosphere density
reaches views and the resolve, while a change of a kind's colour, the fog's, the
medium's or the gradient the fog and the haze in-scatter, reaches only the
composite.

## Temporal reconstruction

A view whose quality asks for it (`SdfViewQuality.Temporal`) reconstructs over
time: it renders a jittered sample each frame and resolves the samples it has
gathered into its output. It runs `SdfWorldPackage.TemporalFragment`, the
reduced fragment's passes at its render ceiling, native or reduced, so a native
view gains a `resolve` pass too. In `Puck.World`, `world.temporal` turns it on
for the world's own views; the quality presets carry it, off at `low` and on at
`medium` and `high`. A camera view never reconstructs (its quality restriction,
`WorldScreenBinder.CameraViewQuality`, does not ask), and neither does a session
view.

Each view's history is its own fragment's: two versions at the output extent,
one allocation a frame slot each, that the next frame reads through
`ResourceReference.PreviousFrame`. A resize carries a history buffer only when
its resolved byte capacity and element size still match. A history version advances only when its
writer records and its submission succeeds: a failed or skipped write keeps the
last successful history and the sample count that went with it, reading history
demands nothing of its writer, and device loss starts from none.

- The **history color** holds, per output pixel, the weighted mean of every sample
  the pixel has gathered: the lit color in its RGB and the coverage in its alpha,
  premultiplied as the lit image is.
- The **history surface** holds four words per output pixel: the visibility
  identity of the nearest of the render samples the resolve read; that sample's
  ray distance and the samples' summed weight, capped at one jitter period of
  full-weight samples, as two half floats; and the weighted mean of the samples'
  surface transport, two words.

The views pass also writes a one-channel **reactivity** buffer at the render
extent, which only the resolve reads: one where a display screen's picture covers the
pixel (its bezel band and an opening, a portal's window, are zero and keep their
history like any surface, so the face's jittered edge resolves like any edge),
and, since the material model cannot tell steady emission from animated, the
share of the pixel's color it emits after material layers. Under a temporal view
a screen reads its image along the pixel's unjittered ray, so the image holds
still while the jitter moves.
Coverage stays in the color's
alpha. The sky, the atmosphere's glow and the bounded media never enter the
history: they composite after the resolve. The atmosphere's transmittance does,
inside the lit color and the transport, as a property of each sample's surface.

The temporal resolve takes, for each output pixel, the 3x3 render samples
nearest its center, each weighted by a Gaussian of its distance in output
pixels. It reprojects the pixel through the nearest-depth sample's motion
(`frame/sdf-reprojection.hlsli`, the one reprojection implementation, through its
draw's previous pose and the instance's previous view), or, where
the 3x3 saw no surface, through the camera's rotation alone. History at the moved
position is rejected when the history surface there names another identity or a
ray distance more than 5% from the reprojected one; the pixel then shows the
spatial path at this frame's sample grid and its history restarts. Surviving
history is clamped component by component to the 3x3's YCoCg box, so correcting
chroma leaves an already valid luminance alone, including when a chroma interval
has zero width. It is weighted down by the reactivity and joined by this frame's
samples. The reactivity the resolve reads is the pixel's own sample's, the tap
nearest its center, so a steady pixel beside a screen keeps its history. A
reactive pixel shows the spatial path by its reactivity, on the
unjittered grid, whatever weight it gathered: this frame's samples alone gather a
weight that swings about one sample's as the jitter moves, and blending by it
would switch a whole screen between the two filters frame by frame. The transport history is clamped to the 3x3's
range, as coverage is, and joined with the same weights.
Non-finite history colors or transports are rejected before clipping. History
writes stay within the half-float range; a non-finite accumulation stores zero
weight, so a bright transient cannot contaminate later history after its source
recovers.

History epochs are free: a reset sets the instance's frame count to zero, and the
resolve then reads no history, so the first frame after a cut, a follow or a
portal crossing, a view or extent change, a parked view shown again, a debug view turned on or off, or
reconstruction turned on is the spatial path's frame exactly, at the sequence's
first sample, the pixel center. Under `world.cadence on`, a still temporal view
renders one jitter period after its inputs last change and then stands, its
output converged (`SdfTemporalHistory.Stands`). A render-grid dip or recovery
restarts this settling period, including a grid change that takes effect only
when a replacement graph installs. A view the display stopped showing is parked:
the render graph counts the frames its schedule leaves an instance unread, which
means nothing the display shows reaches it, held consumer outputs included.
Cadence gaps in a consumer do not park its nested views. Every recording carries the count
(`RenderGraphPackageRecording.UnreadFrames`), and `IsUnchanged` receives it too.
The count is part of the epoch, so a temporal view shown again starts
a new epoch while a spatial view's still output stands without a render.

A plain capture (`converge: 0`) observes the next normal temporal sample and
keeps clean color history. An explicit converging capture starts its
sample sequence at zero; any capture also discards retained tainted history
before recording filled inputs. A presented capture, which holds no frame,
discards it on its first frame and skips the period of renders the restarted
history takes to converge, so its frames show the steady image the display
settles back to.

The [`temporal-standing` canary](../../../../experimental/tests/Puck.World.Canaries/temporal-standing/canary.json)
checks this through the real World's GPU counters. A camera pan starts another
convergence period, during which the World submits new shading and resolve
work. After settling, its completed submission stays unchanged across further
frames while the root's submission advances. The counters retain the last
completed sample's counts; an unchanged submission means no additional work,
rather than a new sample reporting zero. The control pans again and observes
another submission.

A temporal view that follows a portal crossing into another world keeps
reconstructing there. The other world's residency builds its resolve pipeline
only on request, so when a followed view changes residency `SdfWorldPasses`
requests that residency's resolve pipeline from the build source the departed
one used; until it is ready the view holds the departed world's image, as any
follow it cannot yet make does.

`place` sharpens what a temporal view resolves: where its source has its rect's
own extent, it applies a contrast-adaptive sharpen of `world.upscale-sharpness`'s
strength instead of its exact copy (`RenderGraphPlacement.Sharpen`), adding no
pass and no texel written; at sharpness 0 the copy stays exact. A lone
whole-display view that sharpens is placed by the root for that pass, as a
tonemapped one is.

The [`place-sharpen` canary](../../../../experimental/tests/Puck.World.Canaries/place-sharpen/canary.json)
captures the production Place kernel's output at equal extent. It checks exact
pixel codes derived from the kernel at zero, full, and partial strength, and
checks flat colors and saturated edges. Disabling `sharpen` at full strength
returns the source codes, making the sharpened-edge claims fail. Both canaries
require a GPU and run in the merge selection on Vulkan and Direct3D 12.

## The bake

`SdfMeshPathBake`, one per residency, bakes the residency's program into meshes
while a bake is pending, and the mask and bake passes run only then. A group is
the instances one rigid frame moves: every active instance riding one
dynamic-transform slot, or one static copy, the run of a placement copy's shape
instances that bake with the first (`SdfInstanceRange.BakesWithPrevious`, which
the static stamper sets on every shape of a copy after its first); the segments
no instance owns form one more group. The mask pass runs `sdf-mesh-bake-mask`,
which writes each lattice's group selection into its instance mask words (a run
selected as `-(first + 1)`, its length in the high half of the lattice's mode
word), and the bake pass runs
`sdf-mesh-bake`, which samples up to sixteen lattices a frame through the
interpreter, one call site, with only the group's instances in the walk
(`sdfMapOmitWorldSegments` drops the unowned segments from it). A frame's
lattices stay within its share of field evaluations (`SdfMeshPathBake.FrameTaps`,
a gradient sample counting five), so a frame that bakes keeps its budget. The
share is of a 60 Hz frame: a frame presented over longer takes as many shares as
whole 60 Hz frames it spans, up to eight (`SdfMeshPathBake.FrameBudget`), so a
slow display, a remote desktop's ten frames a second among them, bakes at the
rate a 60 Hz one does, while a display at 60 Hz or faster takes one share a frame.
Every lattice is sized by one share, so the meshes do not depend on the frame
rate. A frame
takes its lattices in the groups' order, not round robin, so the groups finish in
order and each lands in the shared mesh library (`SdfMeshLibrary`) as it finishes: a
body that crosses into another world mid-bake leaves the groups it finished there,
and the world it reaches, or returns to, takes them rather than baking them again. On
the debug world, a crossing into the garden five seconds into the bake and straight
back leaves 36 of the body's 177 groups to bake again on return, where round robin,
which finished none before the end, rebaked all 177. A
group refines from a coarse lattice over its bounding sphere to the box its
surface can cross, then samples a final lattice of at most 65 samples a side, no
finer than 1.5 cm, in slabs of whole planes, values alone. A static copy of
several shapes bakes as their union, so plates that share a face (the test
room's walls on its floor and under its ceiling) make one surface with a crease
where they meet rather than two meshes fighting over the face. Its final lattice
keeps each shape at the resolution its own lattice would give it: a cubic lattice
past the per-axis cap at the finest shape's spacing, coarsened until it holds at
most about a million samples (never coarser than the capped lattice over the
union's box), and every shape whose own spacing (its bounding sphere's box at 65
a side, no finer than 1.5 cm) stays finer than that by half again is a region the
refinement measures at that spacing whatever its samples show
(`SdfMeshPathBake.UnionFinal`, `SdfSampledLattice.Refine`'s regions). On the
debug world the room bakes at 5.5 cm with its smaller block refined to 2.7 cm,
and the groups drop from 193 to 187. The samples are read
back and contoured on the thread pool by `SdfSampledLattice`, manifold dual
contouring (`SdfDualContouring`) over a lattice whose spacing may differ per
axis. A crossed cell holds one vertex for each patch of the surface inside it:
its patches are the connected pieces of the marching-cubes surface, found from
the eight corner signs alone by joining the sign-changing cube edges along each
face's marching-squares segments, so a cell two sheets cross holds a vertex for
each and no edge of the mesh is shared by more than two quads. A face whose
diagonal corners agree is cut to separate its inside corners whichever cell
reads it, so the two cells that share a face agree and the mesh has no crack. The
walk a group samples through answers that group's instances alone, compiled
parts and rigid plans included, and each sample is the group's own field: the
kernel divides out the program-wide step scale another instance's warps lower
(`SdfFieldDeviceLawTests.Instances` holds both to the CPU evaluator over the
instance alone, on Vulkan and Direct3D 12). A shape under a non-uniform `Scale`
still samples a lower bound of its distance.

Static shapes also declare feature spacing through `SdfInstanceRange.Detail`:
an eighth of the smaller sweep radius, a quarter of shell thickness or rounded
profile radius, a thirty-second of the smallest half-extent for a warped shape, or a thirty-second of
the wavelength for cellular relief. `WorldPlacementStamper` takes the tightest
of these at placement scale; a scoped creation takes its tightest shape's
spacing. These regions refine surface cells even when their corner signs need
no topological repair, up to eight subdivisions per coarse axis. This gives thin
strands smooth surfaces and warped corners enough samples to meet cleanly.

A 1.5 cm cell cannot hold what the Moth's armour is made of: shells that thin to
a knife edge (the top of a shin, the helmet's rim), paint layers a millimetre
proud of the plate beneath, and shells resting on the joints under them. Its
samples step over a sheet thinner than a cell, so the mesh frays into fragments
with the joint showing through, and a material boundary inside a cell lands
wherever the cell's corners put it. So the final lattice refines where its
samples cannot resolve the surface (`SdfSampledLattice.Refine`):

- a cell next to a lattice edge that a sheet or gap thinner than a cell crosses:
  a run of one sample between two of the other sign whose crossings lie less
  than a cell apart, or two samples of one sign whose values fall steeply toward
  each other from both sides and meet below the surface, a sheet that passed
  between them. "Steeply" is measured against the field's own change over a
  step there, since a scoped, flared or non-uniformly scaled shape's field reads
  well under the distance (a fifth of it on the Moth's shins), and a test in
  world units misses every sheet such a field holds. A solid's edge or corner
  that a line crosses near its tip, or grazes, is as thin along the line, but
  the lines beside it either cross its body two samples thick or more, which a
  sheet thinner than a cell cannot, or see it along one direction alone, where
  a sheet spans both directions across the line that caught it
  (`ThinBeside`: a line beside it on each cross axis must cross the same sheet
  or gap thinly within the window): such a cell does not refine, and the
  contour's tangent planes keep the edge sharp at the lattice's own spacing. An
  acute rim (a cone's foot), a cut tilted against the lattice or a box turned
  off every axis would otherwise refine in patches along its edges and at its
  corners, and the merged cells around each patch cut the crease at the coarse
  spacing or fold its fine vertices into slivers along it;
- a crossed cell whose corner signs hold more than one surface patch, two sheets
  in one cell.

A cell whose outside corners report different materials is not refined for
that: the material cut searches each boundary along the mesh's own edges, and
a seam between two materials refined along a concave crease merges its fine
cells into collinear vertices whose quads fold.

Each such cell is measured again as a points lattice at a fine spacing: the cell
split into as many parts as bring it to 5 mm (`FineCell`), at most four along
each axis. Every cell touching one is contoured at the fine spacing too, from
its coarse corners' interpolation, and merges each connected piece of its fine
surface into one vertex, so it adds no triangles and the mesh has no crack where
the resolution changes (`SdfDualContouring.ExtractRefined`). A sheet thinner
than the fine cell is closed before it contours (`SdfSampledLattice.CloseSheets`),
in the cells the coarse samples saw a thin sheet or gap cross: a fine sample
inside one reads as inside by half the field's change over a fine step; where
one passed between two fine samples of a line, the nearer reads as far inside as
it lay outside, unless a neighbour lies deeper inside than half a step, as beside
a solid's edge the line grazes, or a line up to three fine steps beside it lies
inside for two steps running, as beside a wedge too acute for the neighbours to
reach deep (a window cut through a thin shell, whose rim otherwise closes into
lumps a centimetre proud); and a sheet one sample thick stepping diagonally
across the lattice is joined along the faces its steps share. Its samples alone
leave it holed, its two faces closer than a vertex's placement can tell apart,
and its thinning end frayed into slivers and specks: the white fringe and the
hole along the top of the Moth's shins where the shell thins against the knee. Every other cell
contours whole. The gradients are then measured at the samples the contour reads
one at: the corners of every crossed cell that contours whole, the coarse
corners of every refined cell, whose unmeasured fine samples interpolate them,
and the measured corners of every crossed fine cell. On the debug world about
8,000 cells refine, and the bake makes about 700,000 triangles from about
21 M field evaluations.

A cell's vertex is the point its crossings' tangent planes meet at. Before
measuring those planes, the bake brackets each zero along its sign-changing
lattice edge, using value-only point batches. The contour and the gradient
requests use the same refined crossing (`SdfSampledLattice.CrossingPoints` and
`AnswerCrossings`). A linear estimate projected along its nearest normal can
land on a different face at a corner, giving the contour the wrong plane.
Synthetic edges in a refinement's transition ring and samples displaced to
close a thin sheet keep their lattice interpolation, because their surface
intentionally differs from the original field. Where every
plane passes through one point and two of them turn apart by more than thirty
degrees, the point is a crease or corner (a feature): it is solved again with a
bias only the planes' common null space feels, so it lies on the crease rather
than drawn a few hundredths of a cell toward the crossings' mean
(`SdfDualContouring.Place`). A crease that steps across the lattice
diagonally, a curved rim's or a tilted cut's, often lies just past the cell
that sees both its faces, in a neighbour whose own crossings see only one of
them: the planes meet outside every cell that sees them, and no vertex lies on
the crease there. A vertex clamped into its cell sits off both faces, and the
mesh steps from a base vertex millimetres inside a cone's rim straight up to a
side vertex, cutting the corner in a regular row of notches that shade as dark
teeth. So a convex crease up to half a cell past the cell is handed to the
neighbour that holds it (`HandOff`): that cell's vertex moves onto the crease,
which bounds the patch it holds, and the cell that found it keeps its vertex
inside itself (`BesideCrease`), on the side of the crease its crossings lie on:
clamped across to the other face, past the neighbour's crease vertex, it folds
the quads they share (the corners of boxes turned about an axis). Where that
point already lies on both faces, it moves onto the face its crossings lie
nearer and takes that face's normal, so the strip to the crease vertex lies flat
on the face rather than a millimetre off it, and a crease within a twentieth of
a cell stays its own, since handed over it would drag the neighbour's vertex
along the crease (`TouchedCrease`). A vertex taken past its cell instead lands
beside the neighbour's and folds the quads they share. A straight crease, whose planes the
mean-biased solution meets too, keeps that point past the cell, as does a
concave one. A point that is no feature and lies past the cell is pulled back
along the line from the crossings' mean, not clamped axis by axis, which would
pin it to the cell's faces and step the vertices along an unresolved edge into
a staircase aligned with the lattice. A merged piece of a refined cell that
measured nothing is placed the same way from its fine crossings' planes, solved
in its coarse cell (`MergedFeature`), so an edge keeps its line through the
cells around a refined run (a seam between two materials). Each quad splits
along the diagonal whose triangles turn least from their corners' normals,
leaving crease vertices out (`SdfContour`), and chooses again after the final
field correction moves vertices, so a quad beside a crease
puts its crease vertex in both triangles rather than joining the two faces'
vertices across the crease. A feature vertex takes its
normal from the crossings whose planes pass through it, as does a vertex whose
crossings all face one way (one flat face); any other reads the gradients the
lattice interpolates at it.

The planes meet on the surface only at a sharp crease. Where an edge is rounded
by a fillet, a chamfer or a rounding near the cell's size (a smooth blend's), a
cell whose crossings all lie on the two faces puts its vertex at the faces'
meeting line, proud of the rounding by up to a third of the rounding's radius,
while a neighbour with a crossing on the rounding puts its own on it, so a rim
steps in and out from cell to cell. So the bake checks every vertex no crossing
pins to the surface (`SdfContour.Solved`: one its planes drew more than a
twentieth of a cell from their crossings' mean, and every merged vertex of a
refined cell) against the field: a second points round measures the field's
value and gradient there, and a vertex more than a fiftieth of the contour's
finest cell off the surface moves onto it along the gradient, takes the gradient
for its normal and is no crease (`SdfMeshPathBake`, `softened=` on its line; on
the debug world about 100,000 vertices, 190 more dispatches and 0.8 M more
taps). The movement is bounded before any incident triangle reverses its
original facing (`ContourProjection`), including a check of the rounded float
position. An incomplete correction retains its original shading normal and
feature flag. Degenerate triangles have no original orientation to constrain.
A vertex the contour kept inside its cell beside a sharp convex crease
past it (`SdfContour.Beside`), which the field puts inside the solid rather than
proud of a rounding, moves onto the surface but stays a crease vertex: its quads
turn across the crease, and with its own face's normal the other face's quads
beside the edge shade up to 90 degrees off. A vertex whose gradient reads
shorter than a tenth of the field's typical slope across its surface stays where
the contour put it
(`KinkGradient`): the gradient's taps straddle a crease the vertex lies on, and
the value over that length puts the surface a centimetre or more off, at a
shell window's rim. The contour then splits each vertex whose quads face apart by
more than 25 degrees into one
vertex per side, each with the area-weighted facing of its side
(`SdfContour.SplitAtCreases`). Incident geometry determines this split even
when the field correction cleared a vertex's placement feature flag. A lifted
chamfer's corner meets its neighboring bevel at 31.4 degrees; the threshold
stays below that smaller turn so the corner does not blend across its rim.
A quad whose two triangles face apart past the split angle
splits into its triangles first, so each takes its own side's normal rather
than the whole quad taking one side's and its other triangle shading as a spike
of that side (the star points round the Moth's ankle bezels). A sharp edge
shades as the two faces meeting at it rather than as a band of blended normals a
cell wide, the band that serrated the test room's plates and frayed the Moth's
plate rims into pale and dark fringes. Pieces of the surface apart from the rest,
under two lattice cells across and a hundredth of the largest piece's quads, are
dropped: the specks a shell leaves where it thins to a knife edge (the top of the
Moth's shins). The inner band of the Moth's face opening still shows jagged
slivers at the fine spacing ([open item](../../../plans/open-items.md#rendering)).

The material is then sampled where a field hit would read it, at points lifted
half a millimetre off the surface, in rounds the bake writes into the tables'
point table (`SdfWorldTables.WriteMeshBakePoints`, World member
`sdfMeshBakePoints`) ahead of the frame's upload and `sdf-mesh-bake` samples as
points lattices (`SdfMeshMaterialCut`). The first round samples each quad's four
quarter centres and each vertex. A quad whose vertices agree keeps one material
when its quarters agree too, and splits into its quarters otherwise, so a
feature thinner than a cell that no vertex sees, a painted iris or pupil,
survives at half a cell. A quad whose vertices disagree is cut: each mesh edge
of its two triangles whose ends disagree is bisected over four more rounds for
the point its material changes, and each triangle splits along the line between
its edges' points, so a boundary between two shapes of one group (a nozzle's
glow disc in its bore) follows its true place to a thirty-second of an edge,
unbroken across triangles, rather than stepping in half cells. Where the
boundary runs along a crease (a raised bezel's foot or rim), each piece takes
its own edge end's normal at the boundary point, so the pieces shade as their
own faces rather than with a normal blended across the crease. On the debug
world the cut searches about 14,000 edges, adding about 480,000 points, 60
dispatches and 20,000 triangles to the bake. A boundary between two groups is
the meshes' crossing and no cut reaches it.

A dynamic group bakes in its slot's frame: its box, lattices and points are in
that frame, and `sdf-mesh-bake` reads every transform through the poses the
group's geometry hashed (`SdfMeshGeometryBuilder.Poses`: each slot it reads,
relative to its own and rounded like the geometry's key, after the turn that
takes a dynamic bound's world-axis offset into its frame), which the batch
stages after its points and each lattice's fourth row names. No sample reads the frame
the lattice dispatches in, so a body that moves while it bakes bakes whole, and
one geometry bakes one mesh on every boot of a device. Its mesh stays in
that frame and draws at the slot's live transform, and its draw names that slot
(`SdfMeshDraw.FrameSlot`, the draw record's word 31), which the surface pass
writes into the visibility record so views places material layers in the body's
frame and reads its lanes; a static group's mesh is in world space.

A group whose shapes ride slots of their own beside the group's (a stamp-pool
blend group, each shape on its own slot) follows them as they move apart: once
its mesh is built, one more points round asks which slot wins at each vertex,
and the mesh keeps each such slot as a member, its offset from the group's slot
and the pose it was baked at, with each vertex's member (`SdfMesh.Members`,
`SdfMesh.VertexMembers`; the draw record's word 32 names them). Every read of a
mesh vertex carries a member's vertex through the inverse of that baked pose and
out through the slot's transform in the frame, the previous frame's for motion,
so a hinged part blended into a group swings with its slot. A cut or
intersection face rides the shape it shapes rather than the cutter. A seam
between two members that move far apart stretches, since each vertex follows one
slot rigidly, until the members hold still: once their poses in the group's frame
have drifted past a centimetre or two degrees from the baked pose and then held
for half a second, the group is planned again at that pose, snapped to 7.8 mm
steps so poses that settle near one another share one stored mesh, and its new
mesh replaces the skinned one when it lands (`SdfMeshSettle`). A pose still
changing never rebakes, and a residency starts at most two such rebakes at once
and four a second; `world.counters sdf.mesh-settle` counts them.

A body straddling a portal whose counterpart stands in the same world is drawn
on both sides of it (`WorldPortalStraddle`, `SdfSlotClip`). Each draw riding
the body's slots is clipped to the front of the glass it straddles, and gains a
copy carried through the pair's rigid motion and clipped to the front of the
counterpart's glass. The draw record ends with that carry, a rotation
quaternion and a translation every resolved world position and normal goes
through last (words 33 to 39), and the world-space clip plane (words 40 to 43,
`dot(n, x) + d`, all zeros for none), which the mesh and shadow vertex stages
write to `SV_ClipDistance0`. Vulkan devices are created with
`shaderClipDistance` for it. Only stamp-pool bodies are covered: a catalog
avatar or a prototype mesh draw is still drawn whole on one side. The frame
that carries a body through such a door reseats its slots' history, so
reprojection never reads the 6 m jump as motion. A chase camera whose boom
passes through a same-world door's glass stands past the counterpart's glass
instead (`WorldPortalStraddle.CarryBoom`), looking back through it at the body.

The tables keep each mesh at the words it was first placed at
(`SdfMeshRegionArena`: a reserve of draw records, then each mesh whole), so a
frame whose bodies moved packs only its draw records (`SdfMeshRegion.WriteDraws`),
and a frame where a group's mesh lands writes the records and that mesh alone.
A mesh no draw names any more stays where it lies, so a body that leaves the
world and returns finds its meshes placed and writes nothing for them; the arena
packs again only once those words outnumber the named meshes' words and 32 MB
(`SdfMeshRegionArena.RetainedSlackWords`).
Meshes a list newly places land within a frame's share of words
(`SdfWorldTables.MeshLandingWords`, 2 MB), in the list's order; a draw whose
mesh is still owed waits out of the staged records until it lands, so a body
whose meshes all arrive at once, as when it crosses into a world, lands over a
few frames. Every mesh is written again at once only when the arena outgrows
its draw reserve or packs; a region that grows, to
twice its need, takes the words it held without waiting for the device: the
next upload clears the new buffer and copies the old one into it on the GPU,
and the old buffer is disposed once that upload has completed. The bake keys each group by its
geometry (`SdfMeshGeometryBuilder`), a 128-bit hash of what determines its
surface independent of where its program places it: its instructions, a convex
polygon's, sweep's or path's side-table values in place of the offset its
program packed them at, its shapes' materials by their place among the group's
materials, a static group's bound, a dynamic group's bound radius, its
transform into its own slot by that role rather than the slot's number, a
transform into any other slot by that slot's pose in the group's frame (rounded
to a millimetre and to a 16384th of each quaternion component), and the lanes a
lane erosion reads. The other slots' poses matter because the stamp pool emits
a blend group as one instance on its first shape's slot (or the creation's
root) while each of its shapes rides its own slot, so the frame's transforms,
not the instructions, place and turn every shape after the first. A world
group is keyed by every instruction outside an instance, the transforms ahead
of its first shape included, and by the frame's reach, the box it bakes over;
a group whose bound passes the reach is keyed by the reach too. A dynamic group's bound center is left out, since
it is offset along world axes by the body's pose when the program was emitted.
The key is taken when a program arrives, from that frame's transforms, so a
group whose shapes move against one another afterwards (a part follower) keeps
the mesh of the pose it was keyed at. A program rebuild keeps every group whose geometry held, and a dynamic
group whose form held (`SdfMeshGeometryBuilder.Form`: its geometry without the other slots' poses), so a body
standing in another pose when something else in the world changes keeps its skinned mesh and bakes again only
through the settle rebake. A geometry whose bake found no surface is recorded as such and never baked again in the
run. A responding static placement registers the palette of every creation it can show
(`WorldPlacementStamper.EmitStatic`), so a response changes no other group's material ids. Every
meshed group is stored by its geometry in an `SdfMeshLibrary` every residency of
a presentation shares (`WorldScreenBinder.MeshLibrary`), so geometry that comes
back (a body that left the world and returned, an instance at another index) or
arrives from another world (a body crossing between the debug world and the
garden) takes its mesh rather than baking again, its triangle materials
renumbered by place when the program numbers the group's materials otherwise.
The World's library also keeps every mesh it stores between runs, in the
`meshes` device cache (`SdfMeshStore`, `WorldCacheRoots.Meshes`, held to a gibibyte by
the caches' one retention, `CacheRetention`), keyed by the group's geometry and
the bake's identity: the bake kernels' bytecode, `Puck.SignedDistance`'s build
and the hash of the bake's own sources in `Puck.SdfVm` (their list is
`PuckMeshBakeSource` in its project). A program's group the store holds loads
from disk on the thread pool instead of baking, so an unchanged world reboots
without baking and a changed creation bakes only its own groups; an entry that
fails its checksum, names another key or is no mesh is refused by name
(`[sdf.mesh-bake] group … store refused: …`), deleted, and its group bakes.
The store is presentation's alone: no simulation state reads it.
Only new geometry bakes. The bake reports its residency, groups,
dispatches, samples, groups loaded, triangles, vertices and wall time on stderr
when it starts and finishes (`[sdf.mesh-bake] start residency=… baking=…
loading=…`, `done residency=… loaded=…`).
It counts a batch as work when a bake pass records it: a frame that takes a
batch no pass records, as while the boot's pipelines build, dispatches nothing,
and its lattices go to the next frame. A batch remembers the bake that took it,
and the package polls every bake pass's readbacks at the start of each frame,
so a batch dispatched by a view that then leaves the frame, or follows another
residency, still answers its own bake. A recorded batch whose readback never
arrives, as when the view's graph rebuilds mid-bake, is dispatched again, and the line reports those samples as
`discarded`, so `samples` less `discarded` is the same on every run of one
build. Until a group's first mesh lands, the group draws nothing.

The walk merges the world-segment list with the selected instances' ranges and
has no second path: a program with no instance lists every segment as a world
segment. A second walk beside it, a linear one over every segment chosen by a
program-uniform flag, is a hazard: NVIDIA's Direct3D 12 driver on Turing (an
RTX 2060) takes it for instanced programs too, while Vulkan on the same card
does not, so every group samples the whole program. A group lying under a
larger neighbour (an axis line on the floor, a wall of the test room) then bakes
empty, and quads take their neighbours' materials.

## The shadow cascades

`SdfMeshShadowCascades` fits two orthographic cameras along the key light (the
frame's first stable shadow slot): the near one around the view frustum's first
16 metres, the far one to 120 metres or the far distance, each a bounding sphere
of its slice snapped to whole texels. The `shadow-near` and `shadow-far` passes
draw every mesh draw from their camera through the shadow pipeline
(`SdfMeshRasterPass.ShadowKey`): the mesh pass's layout and triangle pull, an
orthographic vertex stage along the light (`sdf-mesh-shadow.vert.hlsl`) and a
fragment stage that writes no colour and only counts its fragments, in a render
pass with the cascade's `D32Float` depth alone, cleared to zero and stored. The
depth is reversed and linear, one at the cascade's near plane and zero at its
far plane, so the surface pass samples it (`sdfMeshShadowDepth`) and decodes
the caster's light-space depth as `far - depth · (far - near)`; a texel nothing
was drawn into reads as the far plane. The second shadow slot's light, when one
holds it, gets one cascade over the first 40 metres, drawn by `shadow-second`
and written into K's second slot.

Every cascade is 2048 texels a side at the `high` shadow tier, 1024 at
`medium` and 512 at `low` (`SdfViewQuality.ShadowResolution`,
`WorldFramePresenter.ShadowResolutionOf`), 16 MB, 4 MB and 1 MB of depth each;
the pass block's `meshShadowTexels` carries the size to the surface pass, and
the fit snaps to that size's texels. The tier and whether the world holds a
second stable slot choose the view's fragment (`SdfWorldPackage.WithShadows`),
so changing either rebuilds the view's graph once; the slot count is the
world's configured policy, so a handoff between lights allocates nothing.

A shadow handoff (`SdfShadowHandoff`) is a pass value: while one runs, the
outgoing light fades from its stable slot's cascade by the handoff's weight, and
the incoming light, which holds no cascade until the handoff retires and it
takes the slot, lights unshadowed. Neither the fade policy nor a live handoff
plans or allocates anything.

Indirect light is the irradiance volume
([the irradiance volume](lighting-and-shading.md#the-mesh-paths-irradiance-volume)),
which views reads at the tier `world.indirect` and the quality preset ask for
(`SdfWorldResidency.IndirectTier`).

## Frames in flight

The host keeps producing frames while the GPU finishes earlier ones. Each
view's node has its own frame slots, as every render-graph node does, and the
residency's tables keep a **two-deep upload ring** (`FrameRingSize = 2`). Each
upload slot owns its own command pool, its host-visible buffer of every
host-written table (a staging buffer or the table itself, as the next section
describes), and a submission fence. The first pass of frame *N* to record
submits the frame's upload into slot *N mod 2*, ahead of every view's
submission, which reads what it wrote. Before it rewrites a slot, an upload
waits on the previous upload's fence; that fence signals once every submission
queued before it has finished, so the views that read the slot two uploads
earlier are done with it. This is what lets a moving screen or a walking player
update its transform in place each frame without racing the GPU reading last
frame's copy. A rewrite of what every slot shares — a program
that outgrew its region, a new glyph atlas, a reloaded kernel — waits for the
whole device to go idle instead, because the submissions of other nodes read it
too. A grown mesh region and its previous-transform table do not: the buffers
they replace are kept until the upload after the growth completes, which one
queue signals only after every submission that could still read them.

### What a frame uploads

Writing host-visible memory costs CPU copies and memory bandwidth, which
matters most on unified-memory devices such as the Steam Deck. So a frame
writes only what changed since the frame before it.

Every table the kernels read from the host is a `GpuRegion`: the program
words, dynamic transforms, screen surfaces, screen
mappings, screen lights, bounded volumes, glyph decals and mesh draws, which the
residency's tables hold. What is not a table rides each pass's block instead:
the view's camera, far distance and debug view mode, the frame's shading levers
and its environment's rows, which every pass of a view writes into its pass
block (`SdfFrameBlock`) at the offsets the generated `sdf-world` interface
declares. A region keeps a host copy of its table, and a write owes only the
words that differ from that copy, one run for each stretch of changed words.
Where each region lives is the device's choice, made by `GpuResidency.Select`
from its memory profile and the table's size, with a reader always in flight:

- **Staged**, on a device the host cannot write in its own memory (a discrete
  adapter without an aperture): each ring slot has a staging buffer, and the
  `upload` pass copies the owed words into one device-local buffer the kernels
  read, one dispatch per region however scattered the changes are. The staging
  buffer states the copy: a 16-byte header, 8 bytes for each run, then the
  runs' words packed one after another, so it holds only what its slot owes and
  grows when a slot owes more. A region's host copy lives in native memory. A
  word nobody changed stays as an earlier frame left it, so a
  transform changed on frame *N* is still correct on frame *N+1*, even though
  that frame stages in the other slot's buffer.
- **Ring**, on a device with a host-visible aperture or unified memory: each
  ring slot has its own buffer the kernels read directly, and each receives
  the words it is behind by when its turn comes. Nothing is copied. On a
  discrete adapter the buffers live in its aperture, so the kernels read them
  from device memory; on unified memory they are the one pool.

Every pass of every view reads the tables through one descriptor set, the
`sdf.world` interface's World group at set 1: each ring slot's buffers, the
brick pool, the glyph atlas and the samplers. The tables own
one such set per ring slot; a frame binds the slot its upload wrote. A slot's
set is rewritten the first time a view binds it after what it binds moved (a
region grows, or the glyph atlas changes), without waiting
for the device: the upload that took the slot waited the fence that signals
after every view that bound the slot's set before.

Dynamic transforms are never compared as a table: the residency packs only the
rows the frame's moved set (`SdfFrame.MovedTransforms`) owes since the frame it
last consumed, and the region owes the words of those rows that changed. The
program is written only by a program upload, into a region sized to the live
program and grown by half again when a larger one arrives. A program built on
the thread pool carries the words it differs in from the one held before it
(`SdfFrame.ProgramDelta`), which its build measured, so tables holding that
program copy only those words (`GpuRegion.WriteRuns`) and compare nothing on the
frame thread; any other upload compares the program with the live one. Mesh draws (`SdfFrame.MeshDraws`)
are packed into the mesh region (`SdfMeshRegion`: one 80-byte record a draw,
then each distinct mesh's positions and indices once) only when the frame hands
a different draw list. Each scene emitter states its draws, a static placement's
fixed at its rebuild and a stamp's posed at its root each frame, and returns the
same list while none moved, so a still scene packs nothing. The region starts with one draw record when the tables are built
and grows to twice the need when a list outgrows it. The mesh pass reads its triangles,
and the surface pass reads the winning draw's material.

A host-baked brick reaches the brick pool through a staged region whose
destination is the pool itself. Since the carve bake also writes the pool, each
brick is copied whole.

Every staged copy records `region-copy.comp`, the one region-copy pipeline each
device has, which `Puck.Shaders` ships and every owner leases from the
pass-pipeline cache (`GpuRegionCopyPass`). An owner records a frame's owed copies through
`GpuRegionCopyRecording`: a barrier ordering the earlier reads of every staged
destination before the first copy, then the copies, then one transition per
copied buffer for its readers.

A still frame therefore writes no table. The sky and the bounded media animate
on the frame's presented tick (`SdfFrame.Clock`, the tick the state mirror
presented the frame's bound state at), reduced on the host so no pass reads a
clock: the twinkle's phase and the clouds' drift, which the World's environment
resolver integrates from each rate, keyed or literal, ride the pass blocks, which the
view's node writes whole each frame it renders, and each medium's advection and
pulse ride the volume table, which a frame writes only when the tick moves
them. `world.counters gpu` reports the tables' written bytes as
`uploads.host-visible` on its `upload` line, and a brick's on its `bricks` line.

Nothing on the live path waits for its own submission: the upload and each
view's passes are submitted and the fences do the pacing. A capture reads a
view back through the render graph, which serves it from a frame it renders.

A view's device-local scratch (the bake's instance masks and samples, the
cascade depths, visibility records, the mesh target) is *retained*: one allocation
per instance, shared by every frame slot rather than duplicated. The planner
orders each scratch resource's first use in a frame after the previous frame's
last use of it, which serializes that view's GPU frames against each other
while still overlapping CPU production with GPU execution. Only the view's
color output is kept per frame slot, so a consumer can read the previous
frame's image while the next one renders. A view holds only the cascades it
draws: two at the debug world's `high` tier and one stable slot, 34 MB of its
295 MB at 1280×900 temporal, and a view whose quality skips shadows (a portal
window, a camera view, `world.shadows off`) runs its fragment without them
(`SdfWorldPackage.WithoutShadows`): no cascade pass, no cascade depth, and a
surface pass that binds the tables' filler in their place.

## Reading per-pass GPU cost

Performance is judged by code, disassembly, and deterministic work counters —
never by wall-clock or GPU timestamps. The residency counts the work of its
upload's passes, and each view's node counts the work each of its passes
(`sdf.world$mask` through `sdf.world$composite`) records, with no arming and no
effect on the image: dispatches, indirect dispatches, barriers, pipeline and
descriptor-set binds, push-constant bytes, descriptor writes and host-visible
upload bytes. The upload has four passes: `fillers`, the fillers' first
transitions and clears, which only the first upload runs; `bricks`, a queued
brick's staging copy and the carve bake's slices with the pool's barriers,
which an upload runs only when it writes the pool; `upload`, the regions'
writes and copies; and `environment`, the CPU candidate projection that decides
whether a procedural [map](#the-environment-map) needs refreshing. The separate
`sdf.environment` graph node counts its map and coefficient dispatches, image
bindings, and kernel counter copies. Its named layer rows grow through the same
completed-slot retirement and memory accounting as other graph passes.
The `bricks` and `upload` passes follow each device's
residency policy, so they are per-backend deterministic, and `puck counters`
does not hold the two backends to them.

Each view's passes also count their own work on the GPU: the march steps they
take, one for each field evaluation of a march or a query and for each sample of
a bounded volume, and the texels they write, one for each pixel whose output
they write (an image texel, or any word of a pixel's visibility record, so a
stage that returns before it stores counts nothing) and, in the mesh pass, one
for each fragment. The root graph's `place` passes, the overlay, the source
conversions and the post passes count the texels they write the same way. Each
wave sums its lanes' counts, a fragment stage's over the lanes that are not
helper lanes, and adds them with one atomic into the pass's row of the node's
kernel counters, which the node clears ahead of the view's first pass and
copies to the frame slot's readback behind its last; the counts join the pass's
line once the submission completes, as `march.steps` and `texels.written`. The
bake samples in floats, so these counts are per backend deterministic. The clear, the copy and their
three barriers (after the clear, before the copy, and from the copy to the host)
count outside every pass. A pass the frame skips, such as a
cascade in a frame that draws no mesh, is reported as skipped, not as a
pass that ran and counted nothing. A view the cadence gate finds unchanged is not rendered at
all: the render graph keeps its latest output, and its passes record nothing.
Counts are published only once the GPU has finished the submission, so
`world.counters gpu` shows the newest completed frame.

In `Puck.World`, read the previous frame's passes with `world.counters gpu`. A still
scene keeps each view's retained output instead of rendering, so run
`world.cadence off` before measuring one. Hold the camera at a fixed pose
while comparing runs; [SDF performance](performance.md) turns this into the
general rule: frame-index a measurement camera, never wall-clock it. The
counts are exact: the calls a pass records are the same on every backend for
the same inputs, and its march steps and texels written the same on every run
of one backend, so no banding or averaging is needed the way a timestamp sample
would require. `puck counters compare` holds a pinned workload's counts from
before a change to those from after it, pass by pass. When
a counted-work comparison alone cannot answer the question, read the kernel
disassembly or trace the code path instead.

---

## Related resources

- The pass order and what each pass must respect when edited:
  [the rendering skill's kernel reference](../../../../.claude/skills/rendering/references/kernels.md)
  and the fragment in
  [`src/Puck.Shaders.Model/Graph/SdfWorldPackage.cs`](../../../../src/Puck.Shaders.Model/Graph/SdfWorldPackage.cs).
- Measurement method and the register-pressure lesson:
  [SDF performance](performance.md).
- The two-deep upload ring and its per-slot fences: `FrameRingSize` in
  [`src/Puck.SdfVm/SdfWorldTables.cs`](../../../../src/Puck.SdfVm/SdfWorldTables.cs)
  and `SubmitUpload` in
  [`src/Puck.SdfVm/SdfWorldTables.Upload.cs`](../../../../src/Puck.SdfVm/SdfWorldTables.Upload.cs).
- How a view records its passes and binds the residency's tables:
  [`src/Puck.SdfVm/SdfWorldPasses.cs`](../../../../src/Puck.SdfVm/SdfWorldPasses.cs)
  and [`src/Puck.SdfVm/SdfWorldPassRecorder.cs`](../../../../src/Puck.SdfVm/SdfWorldPassRecorder.cs),
  [`src/Puck.SdfVm/SdfFrameBlock.cs`](../../../../src/Puck.SdfVm/SdfFrameBlock.cs), which writes each
  pass's block, and the pass block in the [rendering skill's sync pairs](../../../../.claude/skills/rendering/references/sync-pairs.md).

## Presentation picking

A rendered view exposes `SdfWorldPicker` through its `SdfWorldPasses`. A request
uses normalized view coordinates and reads one visibility pixel asynchronously.
The copy takes the record's first four words (16 bytes): the ray distance, the
identity, the material and the triangle; an inspector's copy takes six (24
bytes), through the normal. The identity supplies kind and source: the mesh draw
ordinal, which the host's pick map resolves to its placement. Every in-extent
record is the frame's own, so the answer needs no currency check. A queued
coordinate captures the program and immutable host identity map when its pixel
copy records, so a content revision before recording keeps the request. A view
change cancels it; after recording, a changed program or map rejects the answer
and the request records again. Pose-only mesh changes keep that map. The
identity's kind and source fields come from `SdfVisibility`, which the kernels
read through their generated spellings.

Build mode and locally opened passthrough panes demand hover from the same
picker. At most one copy is in flight: while it is, a moved pointer's latest
coordinate waits and records when the copy completes, so a moving pointer gets
an answer every round trip without forcing a render per frame. An answer for an
earlier coordinate is published with its own pixel (`SdfPickResult.X` and `Y`),
and the hover label (`WorldPickLabel`) names a placement only with the pixel it
was answered at. After an answer, continuing hover samples the next rendered
frame so geometry moving beneath a stationary pointer stays current. No demand
records no copy and forces no render. A one-shot request survives a reinstall of
the view's passes: a copy the retired recorder still held records again through
the one that replaces it. `world.view.pick <instance>
[<x> <y>]` exposes the same request/result seam to presentation automation.
`world.view.pointer <client-x> <client-y>` supplies an in-bounds console-only
presentation cursor override; `clear` restores the real pointer feed. It does
not move the OS cursor or send simulation input. The argument-free pointer
query keeps its existing readout.
