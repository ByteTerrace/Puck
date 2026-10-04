# SDF frame rendering

One world frame turns an SDF program and opaque meshes into pixels through a
fixed sequence of compute and graphics passes. The upload precedes culling; the mask pass builds per-tile instance
visibility before the beam and primary marches; surface, ambient, shadow and view passes shade each view's hits, and
the sky and composite passes finish its image. The sequence exposes
where the GPU work goes, why mask-first processing keeps beam cost tied to nearby
instances, how render-scale tiers trade resolution for frame budget, and how
frames stay in flight without stalling the whole device.

## One indirect render pipeline

A world frame has two halves. The scene's shared tables — the program, the
moving transforms, the screens, lights, volumes and mesh draws — live in one
**residency** (`SdfWorldResidency`) per frame source, and the frame first
submits one **upload** that brings those tables up to date. Then every view of
the scene is an instance of the render graph's `sdf.world` package, and its node
records the package's eleven native passes into its own submission, reading the tables
the upload wrote. The upload precedes culling; camera traversal, surface evaluation, AO,
the selected lights' shadows, lighting, the sky and the composite have separate dispatches. The
passes finish that view's own output image:

```text
   upload → mask → beam → cull-args → mesh → primary → surface → ambient → shadow → views → sky → composite
```

The render graph plans the view's passes like any other graph: the package
declares them as a fragment (`SdfWorldPackage.NativeFragment`) that the graph
compiler splices into the view's graph, and the planner decides every barrier
between them. `world.counters gpu` reports the upload under the residency
(`sdf:world` for the world's) and each view's passes under its instance, as
`sdf.world$mask` through `sdf.world$composite`. Here is what the culling and
rendering passes do; [the engine README](../../../../src/Puck.SdfVm/README.md)
describes the visibility records the five per-pixel passes share: one per pixel
of each view's render grid, 64 bytes. A view whose render-scale ceiling is below
native puts the full-output `resolve` pass described under
[render scale](#render-scale-tiers-trade-resolution-for-frame-time) between views
and the sky, and a view that reconstructs over time does so at any scale
([temporal reconstruction](#temporal-reconstruction)). The sky and the composite
are described under [the sky once, and a composite last](#the-sky-once-and-a-composite-last).

**mask** (`sdf-instance-cull.comp.hlsl`) computes, for every 16×16 screen tile, the
set of instances that could possibly matter to that tile—a bitmask, one bit per
instance. It reads a host-built uniform grid (instances binned by center into
world-space cells) and, for each tile, walks only the grid cells the tile's cone
overlaps, testing each binned instance's bound sphere against the cone. Dynamic
and unmaskable instances ride an always-tested list. The output is a compact
per-tile instance bitmask.

**beam** (`sdf-beam.comp.hlsl`) runs a coarse cone-march per tile over the
*mask-restricted* field. One representative cone per 16×16 tile marches until it
hits something or proves a stretch of space empty, recording where the fine march
should start and, when worthwhile, an empty gap it can teleport across. Programs
admitted to independent part tracing use a short entry search; their cheaper
per-part marches finish the work. Other programs also search gap and tail bounds.
Budget exhaustion leaves the unproven bounds disabled. Because the beam marches
`mapMasked`—the field with masked-out instances excluded—it never pays for
instances the mask already ruled out. Its cost is dominated by the VM evaluations
performed along the representative cone.

**cull-args** (`sdf-cull-args.comp.hlsl`) reads the beam's per-tile results and packs the
indirect-dispatch arguments for primary, surface, ambient, shadow and views. A parallel min/max reduction
finds the bounding rectangle of surviving tiles. Empty margins outside that
rectangle launch no threads; holes inside it remain in the dispatch.

**mesh** (`sdf-mesh.vert.hlsl`, `sdf-mesh.frag.hlsl`) rasterizes the frame's mesh draws, one draw call
each (and the impostor cards a view records, through a second pipeline with
`sdf-mesh-impostor.frag.hlsl`, after the meshes; a baked placement's mesh and card are alternatives, and the view records
one, as [the impostor](bricks-and-baking.md#prototype-bakes) describes), into the mesh visibility target at the view's extent: per pixel the ray parameter the march records,
the draw plus one, and the triangle, kept nearest by a reversed-Z depth test. Primary alone reads the
target: it resolves the triangle's material from the mesh region (the draw's, plus the triangle's palette entry
when its mesh carries one) and records the draw and the triangle in the pixel's visibility record, from which
surface resolves its normal at the hit point (the vertex normals interpolated when its mesh
carries them, carried to world space by the draw's inverse-transpose normal matrix so a nonuniform scale keeps
them perpendicular to the surface; the face normal otherwise), turned toward the camera and clamped so a grazing ray
never sees it lean away, and views a textured mesh's albedo, material and emission. The draw plus one and the triangle are stored as floats, so a region holds at most 2^24
draws and a mesh at most 2^24 triangles, each refused by name past that. A frame with no mesh draws skips the pass,
its target's and depth's barriers included, and the hit passes read nothing of the target. The target and its depth attachment, 20 bytes a pixel, are scratch the view's instance allocates with its graph.
Primary bounds its march by that ray parameter
and keeps an SDF surface only when it is strictly nearer, so a mesh pixel becomes a mesh visibility
record; while a mesh draws, cull-args covers the whole view, and a mesh pixel shades with neutral shadows
and ambient occlusion.

**primary** (`sdf-world-primary.comp.hlsl`) traces camera rays from their tile's entry
depth, raised to the mesh projection's near plane when necessary, and writes
each pixel's visibility record: ray parameter, identity, material and march data,
misses included. **surface** adds the geometric normal and curvature to it.
**ambient** evaluates contact occlusion along those normals into the record.
**shadow** gathers and marches each selected light from each lit surface,
packing four 8-bit stable visibilities into the record's K word. Each active
handoff adds one incoming march, bounded by K + F; its visibility uses a
policy-sized retained texture, one byte per pixel at F = 1 and two at F = 2,
absent at F = 0. **views** reads those visibilities and computes materials, lighting and
volumes. A view whose quality (`SdfViewSnapshot.Quality`) turns ambient occlusion or
soft shadows off skips that pass; quality is each view's, so views of one frame
render at different cost. Compare all five passes when measuring per-pixel field cost: moving
work between kernels can reduce register pressure but adds buffer traffic.

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
the map's threshold. A selected shadow direction adds `shadow`; geometry
or camera changes render every active pass. A sky layer that
samples a screen (a panorama, a textured disc) runs `sky` and `composite` every
frame, since the screen's image changes in place where no signature sees it;
the march passes still stand. `world.lighting` reports each keyed value's change class, including
fields keyed through a section.

Temporal sampling renders the geometry again while a sample is owed. Once
lit history converges, visual edits leave its sample count and ring standing.
`world.cadence off` forces active passes to render for measurement.

## What each pass costs

The passes scale with different things. `mask` and `beam` scale with how many
instances lie near each tile's cone. `primary`, `surface`, `ambient`, `shadow` and
`views` scale with on-screen content: how many pixels hit a surface and how
much of the program each field query walks. `cull-args` is small; `composite`
scales with the output's pixels and `sky` with the uncovered ones. **The five per-pixel passes are the scale lever for
on-screen content; `mask`+`beam` is the scale lever for instance count.**
Moving work between the per-pixel passes can relieve register pressure but
adds hit-buffer traffic, so compare their sum as well as each label.

## Why the mask pass flattens beam cost

Without a mask, every beam march step evaluates the whole program, and each
evaluation checks every instance segment's bound early-out. The beam's cost
then grows linearly with instance count even when the instances are nowhere
near the tile, because binning the instances is cheap while re-checking all of
them at every march step is not.

The set of instances a tile's cone actually needs is exactly what a spatial
cull computes, and it can be computed *once per tile* instead of re-derived at
every march step. So the pipeline puts the mask pass **first**: compute each
tile's relevant-instance bitmask up front, then let the cone march consume the
already-masked field. The march stops enumerating instances per sample; it
only ever touches the handful the mask kept.

The per-pixel passes do the same work with or without the mask, because the
masked field is bit-identical to the full field inside the tile's cone. With
the mask in place, a frame with many on-screen instances is bound by the
per-pixel passes rather than by `beam`, which is where the cost belongs: on
visible shading, not on culling.

Two design choices determine both correctness and occupancy. The cull is a
**separate pass, not fused into the beam**—a fused variant's per-thread mask
scratch lowers the co-resident cone march's occupancy
([SDF performance](performance.md) turns this into the general register-pressure
lesson). And the mask output uses **direct mask-buffer bit writes** (OR is
commutative, so insertion order doesn't matter), never a per-thread accumulation
array. Occupancy is part of the contract here, not a detail.

Correctness rides the exact-cull contract from [SDF program model](program-model.md): a masked-out
instance's bound excludes the tile's whole cone, and a far-neutral blend
(union/subtraction) returns the accumulator *to the bit* when its member is
skipped. So the masked march is bit-identical to the flat one. No runtime
switch disables the mask, and no automated check compares the two.

One nuance worth carrying: the cull raises the *total* instance ceiling, not the
*per-tile* one. Scattered content—a persistently damaged world, carves spread
across the map—costs almost nothing per frame because each tile's cone touches
only a few grid cells. But instances **densely stacked in one spot** overlap the
same tiles and are genuinely un-cullable there; their `views` cost is real and the
grid rightly doesn't touch it. The honest ceilings after the cull are (a)
dense per-tile stacking and (b) on-screen visible-instance shading—both
per-pixel `views` costs.

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

A view whose ceiling is native keeps the eleven-pass native fragment and writes
its output directly. It allocates no resolve resources and ignores the current
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
alpha, which is one for a solid hit, the silhouette's share on an edge beside the
sky, and zero for a miss. The color has already passed through the atmosphere
between the camera and the hit (see [surface transport](#surface-transport)). In a native
view a pixel outside the dispatch box is never written, so the passes after views
read it as uncovered; in a reduced or temporal view the resolve reconstructs the
lit image, coverage with color, and each pixel's surface transport at the output
extent.

The sky is an open stack of up to eight layers that compose in their authored
order, each by its blend (`over`, `add`, `multiply`, `screen`), a kind as often
as authored. A kind is a parameter record and one module under `Sdf/sky/kinds/`,
registered in the generated kind table the kernels switch on, so adding one
touches no pass. The stack is cut into **runs** without reordering it: a maximal
sequence of consecutive **field** layers (gradient, clouds, aurora, noise,
pattern, panorama) is one field run, and the **point** layers between them
(stars, a body's disc) are evaluated one by one. Every blend is affine in the
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

The host projects a changed lighting-visible candidate using the same packed
layers, half-float map and solid-angle weights as the kernels. It compares the
candidate with the last rendered coefficients: the largest absolute irradiance
difference over every normal and RGB channel must reach 1/255 before the map
and coefficients render again. The maximum is the extremum of each channel's
quadratic on the unit sphere (`SdfSkyEnvironment.IrradianceDifference`).
Sub-code changes accumulate against the rendered sky; camera-only changes and
a still sky perform no projection. A kernel reload invalidates the held result.
With ambient and reflection both zero, no sky-coloured fog and no haze, even
candidate projection is off. An authored fog colour does not read the map.

Each candidate counts `gpu.environment.projections` and 4,096
`gpu.environment.projection-texels`; a sub-code candidate also counts
`gpu.environment.skipped`. A rendered change dispatches the map once and its
reduction once: 4,096 evaluations per unmasked lit layer, 8,192 map texels and
nine coefficient texels written. No dispatch runs on a skipped candidate.
The first map texel is integrated analytically as a constant and subtracted
before quadrature, so a constant sky has exactly zero higher bands.

The views pass convolves the coefficients by π, 2π/3 and π/4 for bands zero,
one and two, then applies the existing normal-ladder AO and the
`render.environment.ambient` gain. A constant radiance C gives irradiance πC.
Reflections bilinearly sample the panel-free plane and apply the lighting
panels analytically in their authored order, under the
`render.environment.reflection` gain. Both gains default to one and zero
skips their work. Analytic panels use their layer frame, visibility, opacity,
mask and blend; roughness widens their angular rectangle and reduces its gain.
The map supplies the background beneath these reflection panels.

The composite filters the full plane for fog, including lighting panels.
Octahedral edge taps fold onto the adjoining texels. Queue barriers order all
views' reads before the next upload's writes; no view owns a second map.

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
reconstruction footprint (`reconstruction.hlsli`'s footprint and combine); the
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
extent, which only the resolve reads, inside the dispatch box: one where a screen
covers the pixel, and, since the material model cannot tell steady emission from
animated, the share of the pixel's color it emits after detail material
selection, material layers and mesh-atlas sampling. Coverage stays in the color's
alpha. The sky, the atmosphere's glow and the bounded media never enter the
history: they composite after the resolve. The atmosphere's transmittance does,
inside the lit color and the transport, as a property of each sample's surface.

The temporal resolve takes, for each output pixel, the 3x3 render samples
nearest its center, each weighted by a Gaussian of its distance in output
pixels. It reprojects the pixel through the nearest-depth sample's motion
(`frame/sdf-reprojection.hlsli`, the one reprojection implementation, through its
shape's or triangle's previous pose and the instance's previous view), or, where
the 3x3 saw no surface, through the camera's rotation alone. History at the moved
position is rejected when the history surface there names another identity or a
ray distance more than 5% from the reprojected one; the pixel then shows the
spatial path at this frame's sample grid and its history restarts. Surviving
history is clipped to the 3x3's YCoCg box, weighted down by the reactivity, and
joined by this frame's samples. The transport history is clamped to the 3x3's
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

The [`temporal-standing` canary](../../../../tests/Puck.World.Canaries/temporal-standing/canary.json)
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

The [`place-sharpen` canary](../../../../tests/Puck.World.Canaries/place-sharpen/canary.json)
captures the production Place kernel's output at equal extent. It checks exact
pixel codes derived from the kernel at zero, full, and partial strength, and
checks flat colors and saturated edges. Disabling `sharpen` at full strength
returns the source codes, making the sharpened-edge claims fail. Both canaries
require a GPU and run in the merge selection on Vulkan and Direct3D 12.

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
frame's copy. A rewrite of what every slot shares — a program or instance grid
that outgrew its region, a grown mesh region, a new glyph atlas, a reloaded
kernel — waits for the whole device to go idle instead, because the submissions
of other nodes read it too.

### What a frame uploads

Writing host-visible memory costs CPU copies and memory bandwidth, which
matters most on unified-memory devices such as the Steam Deck. So a frame
writes only what changed since the frame before it.

Every table the kernels read from the host is a `GpuRegion`: the program
words, dynamic transforms, the frame instance grid, screen surfaces, screen
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
  words. A word nobody changed stays as an earlier frame left it, so a
  transform changed on frame *N* is still correct on frame *N+1*, even though
  that frame stages in the other slot's buffer.
- **Ring**, on a device with a host-visible aperture or unified memory: each
  ring slot has its own buffer the kernels read directly, and each receives
  the words it is behind by when its turn comes. Nothing is copied. On a
  discrete adapter the buffers live in its aperture, so the kernels read them
  from device memory; on unified memory they are the one pool.

Every pass of every view reads the tables through one descriptor set, the
`sdf.world` interface's World group at set 1: each ring slot's buffers, the
brick pool, the glyph atlas, the samplers and the mesh and impostor atlases. The tables own
one such set per ring slot and write both once; a frame binds the slot its
upload wrote. They rewrite the sets only when what they bind moves (a region
grows, or the glyph, mesh or impostor atlases change), after the device is idle, since
every view's submission in flight binds them.

Dynamic transforms are never compared as a table: the residency packs only the
rows the frame's moved set (`SdfFrame.MovedTransforms`) owes since the frame it
last consumed, and the region owes the words of those rows that changed. The
program is written only by a program upload, into a region sized to the live
program and grown by half again when a larger one arrives. Mesh draws (`SdfFrame.MeshDraws`)
are packed into the mesh region (`SdfMeshRegion`: one 80-byte record a draw,
then each distinct mesh's positions and indices once) only when the frame hands
a different draw list. Each scene emitter states its draws, a static placement's
fixed at its rebuild and a stamp's posed at its root each frame, and returns the
same list while none moved, so a still scene packs nothing. The region starts with one draw record when the tables are built
and grows by half again when a list outgrows it. The mesh pass reads its triangles,
and primary reads the winning mesh's material.

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
them. The frame instance grid is rebuilt only on a frame whose
transforms moved. `world.counters gpu` reports the tables' written bytes as
`uploads.host-visible` on its `upload` line, and a brick's on its `bricks` line.

Nothing on the live path waits for its own submission: the upload and each
view's passes are submitted and the fences do the pacing. A capture reads a
view back through the render graph, which serves it from a frame it renders.

A view's device-local scratch (tile buffers, instance masks, indirect
arguments, visibility records, the mesh target) is *retained*: one allocation
per instance, shared by every frame slot rather than duplicated. The planner
orders each scratch resource's first use in a frame after the previous frame's
last use of it, which serializes that view's GPU frames against each other
while still overlapping CPU production with GPU execution. Only the view's
color output is kept per frame slot, so a consumer can read the previous
frame's image while the next one renders.

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
writes and copies; and `environment`, the sky environment map's refresh, which
an upload runs only when [the map](#the-environment-map) is owed. The `bricks` and `upload` passes follow each device's
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
marches run in floats and an indirect pass runs only the tiles culling leaves
it, so these counts are per backend deterministic. The clear, the copy and their
three barriers (after the clear, before the copy, and from the copy to the host)
count outside every pass. A pass the frame skips, such as the ambient and
shadow passes at a tier that turns them off, is reported as skipped, not as a
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
would require. `puck counters --check` holds a pinned workload's counts, pass by
pass, to the counted-cost ceilings recorded for it. When
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
- The uniform-grid cull rationale and why a per-frame BVH was rejected for it:
  [Hierarchical and instance acceleration](../reference/hierarchical-and-instance-acceleration.md).
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
The copy takes the record's V, C and L.x words (32 bytes) and, beside them, the
frame's 16-byte dispatch box. V supplies kind, source, ray distance and material.
The source is the SDF program instance ordinal plus one, or the mesh draw
ordinal. Static instances therefore remain distinguishable even with the same
material. A queued coordinate captures the program and immutable host identity
map when its pixel copy records, so a content revision before recording keeps the
request. A view change cancels it; after recording, a changed program or map
rejects the answer and the request records again. Pose-only mesh changes keep
that map.

A pixel outside the frame's dispatch box holds an earlier frame's record, so the
answer applies the rule the hit passes use, `SdfVisibility.IsCurrent`, to the box
its own frame wrote, and a pixel outside it answers nothing, as sky. The kernels
read the same rule through the generated `SDF_VISIBILITY_CURRENT`, and the
identity's kind and source fields through `SdfVisibility` too.

The 64-byte visibility record keeps the winning shape's exact transform slot in
L.x, or `SDF_TRANSFORM_SLOT_NONE` (`SdfProgram.NoDynamicTransformSlot`) for
static geometry. This slot can differ from an articulated instance's bound slot.
A pick carries it as `SdfPickResult.TransformSlot` and resolves it against the
transform table of the frame the record was rendered from, the rows that frame's
upload staged, captured when the copy records, as `SdfPickResult.Transform`.
Surface shading reads the four anonymous lanes from the existing dynamic
transform row; static hits read zero. The remaining L words are reserved. A slot
fits every lane that carries it: `SdfProgram.DynamicTransformSlotBits` is the
float data lane's exact range, and a program naming a larger slot is refused.

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
