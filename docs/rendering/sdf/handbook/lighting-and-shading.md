# Lighting and shading

After a march accepts a surface hit, the shading epilogue turns its position,
material id, and field data into a lit pixel. Puck computes the surface normal
from the shapes that decide the hit's field value, estimates soft
shadow penumbrae, and treats diegetic CRT screens as both pictures and lights.
Runtime switches let a user isolate these terms for measurement or restyle the
frame.

An authored light's `bounce` is a finite nonnegative gain for its diffuse
contribution to indirect transport. It defaults to one and accepts the same
state bindings and section keys as the light's weight. Zero keeps the direct
light while removing its indirect source. Rim highlights and attenuation-only
lights have no diffuse source to scale. The packed light record retains its
48-byte stride; its last word holds this gain.

## The epilogue and its field-unit discipline

The primary march ([SDF frame rendering](frame-rendering.md)) is a loop that answers one question: *where does this ray
first touch a surface?* Everything in this chapter runs **after** that loop
breaks with a hit. The primary pass records a hit position, the material id
captured at the accepting sample, and the accepted distance. It has **no
normal, no shadow, no occlusion yet**—those belong to the later passes: the
surface pass computes the normal and curvature (`sdfResolveSurface` in
`sdf-surface.hlsli`), the ambient pass computes occlusion (`sdfResolveAmbient`),
the shadow pass marches each selected light's soft shadow (`sdfShadowStage` in
`surface/sdf-shadow.hlsli`), and the views pass computes materials and
lighting from the record, read once as a surface sample (`sdfLightStage` in
`shade/sdf-light-stage.hlsli`).

Every technique here re-queries the same field function the march used, `map()`
(and its tile-masked twin `mapMasked()`). So there is one discipline that
threads through the whole chapter, and it's worth stating once as a rule:

> **The field is pre-scaled; de-scale it before any world-space comparison.**
> `map()` returns a distance already multiplied by the program's baked
> `stepScale = 1/L` (the Lipschitz clamp that keeps the march safe—see
> [SDF program model](program-model.md)). Any shading term that compares a returned distance against a
> real-world length—a shadow ray's clearance, an AO rung's height, a cone
> radius—must divide `stepScale` back out first. Skip it and the term
> silently tracks each program's Lipschitz bake instead of its geometry.

Each pass reads the same program `stepScale`; for shadows and AO it is
multiplied by the hit's local field gradient magnitude, which the surface pass
records. Each term de-scales exactly where its math meets world
units—and *only* there. (Coverage anti-aliasing is the deliberate exception:
its ratio lives in the march's own clamped units, so de-scaling it would be the
bug, not the fix.) Keep this rule in your pocket; it explains a comment on
nearly every function below.

## The surface normal follows the deciding shapes

Shading needs a surface normal—the field's gradient, normalized. The textbook
way to get it from an SDF is a **finite-difference tetrahedron**: evaluate the
field at four points slightly offset around the hit, subtract, and you have the
gradient direction. It works, and Puck keeps it compiled as an A/B reference.
But it is *not* the default, for two reasons that are both specific to Puck's
architecture.

Puck's field isn't a closed-form function—it's a **program** the shader
interprets, walking a `uint[]` tape op by op. The interpreter's real cost is
*memory traffic*: fetching and decoding each program word. A four-tap normal
re-walks that whole tape four times, four separate fetch streams for one normal.

The default first finds which shapes have a nonzero derivative weight at the
hit, following the existing interpreter's scalar and blend decisions. Hard
blends retain the deciding side; smooth blends retain both sides only inside
their blend band. A second walk evaluates just the selected shape derivatives.
Their `float3` tangents pass through the same hand-written transform and blend
rules as a full forward-mode dual walk. This replaces repeated work on losing
shapes with one scalar selection and a smaller derivative set.

```text
  Four-tap finite difference:           Selected analytic derivatives:
    walk tape → f(p+dx)                   walk tape → values and blend weights
    walk tape → f(p+dy)                   replay selected shape derivatives
    walk tape → f(p+dz)                   combine weighted tangents
    walk tape → f(p+dw)                   normalize
    subtract, normalize
```

The second reason is **determinism**, and it's the stronger one. A finite
difference computes `f(p+h) − f(p−h)`: a subtraction of two nearly-equal
numbers whose *low bits* carry the answer. The two GPU backends are free to
contract and reorder fused multiply-adds differently, so those two evaluations
round slightly differently and their *cancelled difference* diverges more than
the raw distance ever does, producing ±1-LSB clusters along gradients that a
parity comparison would need a relaxed threshold to absorb. A dual
walk never subtracts: the tangent is *built up* by the same multiplies, adds,
and orthonormal transforms the distance channel already makes, so it is as
parity-stable as the distance itself.

Two design guardrails keep this cheap. The dual path is a **hit-only
specialization**—a separate interpreter function (`mapGradCore`) called from the
surface pass, so the primary march kernel's lean register footprint never
carries the 4×-wide accumulator (a normal is computed once per pixel; the march
evaluates the field dozens of times). Non-uniform scale needs
the same conservative distance treatment as the march and an inverse-transpose
gradient transform; paths that cannot provide that contract are refused.
Translations leave the gradient untouched; rotations multiply it by their
orthonormal matrix; folds apply their own reflection—each derivative
hand-derived once per op. Common primitives have analytic leaf gradients; the
remaining exotic primitives use a shape-local four-tap difference at the leaf,
and the transform and blend chain still carries that gradient analytically.
If the bounded contributor set fills, the interpreter takes the full dual
walk, retaining every contributor. The [gradient reference](../reference/gradients-and-normals.md)
describes the selection contract and the shape counters that include its cost.

## Soft shadows: the penumbra cone

Puck marches a separate shadow for each named light in the allocator's stable
slots and each active incoming handoff, bounded by the policy's K + F. From the lit
point the shadow pass marches the field toward the light and tracks, over the whole march,
the **closest approach** to any occluder—the ratio of how near the ray came to
something solid against how far it has travelled. A ray that sails clean stays
fully lit; one that grazes an edge gets a soft penumbra proportional to how
close it came. The ambient term still fills shadowed regions, so shadows read
soft and colored, never black.

Each light reads its own visibility. Stable visibilities pack into four 8-bit
lanes in the record's K word; active incoming visibilities occupy a texture
sized by the policy's fade capacity. A directional outside the slots is unshadowed,
scaled by the surface's ambient occlusion. During a handoff, an outgoing light
with marched visibility `v` reads
`1 - (1 - v) * (1 - progress)`, and an incoming light reads
`1 - (1 - v) * progress` using its own marched `v`. These scale each light's
occlusion deficit independently. Neither radiance nor the two visibilities
are crossfaded.

The closest-approach refinement has a deliberate limit. A chord between the
previous and current clearance spheres would read the previous sample too;
whether a pair straddles the close approach can then flip across neighboring
rays, producing stripes on a thin penumbra rim. It also collapses to zero for
a ray leaving its own surface along the normal. Puck therefore uses the
deterministic running minimum `k · c / t`, where `c` is the clearance and `t`
is the traveled distance, without that chord fold. `k` is the reciprocal of the
selected light's authored penumbra half-slope (`worldShadowPenumbraSlope(lightIndex)`). The field clearance is
de-scaled before this world-space comparison, while the march step still uses
the raw clamped sample.

## The shadow gather follows the sun ray

A shadow ray leaves the camera's cone, so it needs a *different* set of nearby
occluders than the primary march narrowed to. Getting this wrong is a
correctness bug, not just a speed one: reuse the camera's tile mask for the
shadow march and **off-frustum occluders never cast into frame**—the frame is
wrong-fast, and nothing about it looks broken until an occluder leaves the
frustum.

The current implementation is a **per-workgroup shadow gather**. Before the
shadow march, the 64 lanes in an 8×8 workgroup walk the world-space instance
grid along the current slot's light ray and cooperatively build one groupshared mask covering
the complete instance capacity. Each pixel then marches that mask. The mask
contains every occluder the flat all-instances march could hit, while its grid
walk is shared across the workgroup. An admitted instance a pixel's ray never
reaches composes as the accumulator to the bit, so by construction the masked
march equals the flat march exactly. No automated check compares them. The next
slot reuses the same gather and march sites after every lane finishes consuming
the shared mask.

The gather cone matters. It is **not** a bare ray: it is the *penumbra cone*,
wider than the ray itself, because the closest-approach estimate must include
occluders just beside the ray. A wider cone is always safe (a superset can't
drop a needed occluder); too narrow leaks light. The chord is three penumbra
half-slopes, `worldShadowPenumbraChord(lightIndex)` (`march/sdf-march-constants.hlsli`) `= 3 * worldShadowPenumbraSlope(lightIndex)`
(`frame/sdf-lights.hlsli`): every occluder that can lower the estimate lies inside that
cone with margin. `SdfLights.MaxPenumbraSlope` keeps the chord below one.

**When the gather wins and when it doesn't** is a clean story about density:

| Scene shape | What the gather does | Verdict |
|---|---|---|
| Spread content (a real room) | Narrows each shadow ray to a few local occluders | Correct shadows with fewer field evaluations than a flat march |
| Dense clustering (everything stacked in one spot) | Nothing to narrow; gather ≈ flat, plus the static-mask occupancy tax | Pays the price of correctness—not a regression against a *correct* baseline |

For dense scenes, performance work must accelerate the shadow march itself;
changing the occluder mask would make the clearance proof unsound.

## The indirect cache's depth-only light view

Each indirect residency has one light camera. It visits the finest and coarsest
allocated brick regions for each held and incoming directional light, publishing
at most one 512² region per frame. A region stores a 32-bit ray distance per texel;
infinity means empty and NaN means unresolved. The host retains exact light-owner
names, table-allocation identity and geometry revisions. Only a submitted depth
writer makes a region valid. Unchanged regions schedule no camera work, and light
motion accumulates against the retained direction using the direct shadow path's
one-eighth-penumbra refresh rule.

The orthographic camera starts just outside the finite caster volume. Its rays
run parallel to the light, with zero directional divergence. Each pixel has its
own nearby plane origin; shrinking the penumbra never moves a virtual eye farther
away or increases floating-point cancellation. Traversal starts at this camera's
positive near plane; the perspective camera's larger near-plane floor does not
clip the finite light volume. The primary marcher sweeps the texel's half-diagonal
using the existing certified indirect field walk. The tile
mask and beam enclose the tile's parallel columns plus that sweep radius. This
preserves thin casters while the slope-scaled comparison bias prevents a receiver
from shadowing itself. Positive penumbra remains an authoring requirement.

The fragment contains instance culling, beam culling, dispatch arguments, mesh,
primary traversal and depth publication. It omits tape evaluation, surface,
ambient, shadow, material lighting, sky and color reconstruction. Its allocation
includes visibility and mesh scratch, tile bounds and masks, argument buffers,
the retained depth bank and the frame/pass constant rings. The bank costs one MiB
per region; traversal scratch is additional and shared by the one camera.

A baked mesh carries a CPU-side certificate only when its emitter retains the
same SDF at the same pose. Raster depth can bound a search but cannot certify a
subtexel caster. The light camera uses full baked meshes and the retained field;
point-sampled impostor cards do not replace either. An independent mesh has no
such certificate, so it disables the finite map. The SDF fallback does not
establish visibility through independent mesh-only geometry; that geometry is
outside the light-view qualification.

Invalid regions, out-of-volume samples and unresolved texels require a bounded
per-hit SDF ray. The `indirect-light` diagnostic displays valid lit/shadowed regions
and marks unavailable maps blue. Applying these maps to indirect radiance is
part of the cache solve. The CPU and device laws share the existing subtexel-rod
fixture, no-false-light condition and two-texel widening bound; the rendering plan
tracks the outstanding device qualification.

The residency owns the cache's freeze and reset controls. Freeze admits no new
placement, partition, transport or lighting work after the already queued frame, while
views keep reading the retained cache. Reset waits for a renderable frame,
withdraws the old cache epoch and light-map validity, and changes no authoritative
world state. It retains the freeze setting across tier and table replacements.
Temporal history carries the exact cache allocation, geometry epoch and lighting
publication identity, so a changed cache cannot leave old light inside the
accumulated color merely because it passes the color-clipping test.

`SdfIndirectCache.Snapshot()` copies CPU-owned admission, submitted stratum masks
and allocation bytes. It does not claim probe classes or proof results from those
counts: those are GPU records and require a fenced readback of the same
allocation and epoch.

Program uploads compare their packed field and binding words separately from
the contiguous material-value table. Recolouring an existing material restarts
the finite lighting solve while transport, receiver proofs, depth maps and
geometry passes stand. The held solve retains its captured palette until a new
complete publication replaces it. Changing a shape's material ID, palette size,
instance flags or any field/layout word withdraws transport and its proofs;
matching colour values do not make two material identities interchangeable.

Lighting changes conservatively revisit every allocated probe in the finite
solve. A positional light is evaluated at the stored hit, not the probe origin;
its radius is a falloff scale, not a hard influence cutoff. The shadow path from
that hit can cross a caster beyond every stored transport ray. Caster geometry
therefore advances the light-map geometry identity even when no transport brick
overlaps its changed bound. The CPU invalidation witnesses compare relit stored
hits with a cold solve for both cases; they do not stand in for GPU qualification.

The cache's geometry-change entry point coalesces old and new casting bounds while
an admitted batch finishes or admission is frozen. On resumption, the existing
schedule withdraws every trace stratum in each affected placement brick and
invalidates neighboring cell partitions. A validity bit in the existing brick
record keeps an old partition unavailable until the ordered classification pass
replaces it; placement lookup can still find the neighboring probes it needs.
The change also withdraws the lighting publication and receiver certificates.
This transport operation does not require replacing the cache allocation.

Program upload and a changed body policy collect a conservative influence bound for each casting dynamic slot
from the existing packed segment and instance bounds. Static unbounded geometry
does not taint unrelated dynamic slots; an unsupported dynamic dependency stays
explicitly unbounded. Before an actual transform-row overwrite, the tables queue
both the old and new bounds. Position, orientation and anonymous lanes participate,
so a rotation in place cannot retain stale transport. Direct-shadow suppression
is a separate policy and does not change this field. Identical packed geometry
and receiver-only motion queue nothing. The bounds are reused without a
per-motion program scan.

Each whole instance has an indirect participation policy. `Default` keeps static
geometry casting and receiving; moving bodies receive at medium and cast and
receive at high. `Receive` receives without casting, `Cast` does both, and `Off`
does neither. A placement's explicit policy wins over the frame's body default.
World authors set that default in `render.indirect.bodies` and the placement
override in `placements[].indirect`, using `default`, `cast`, `receive` or `off`.
Local and session bodies read their delivered placement identity; adjacent bodies
retain it in the same pinned snapshot as their pose. A body without an explicit
placement override uses its owning world's body default, including across an
adjacent border; only a remaining `Default` uses the consuming tier. A moving mesh
retains a dynamic flag so its policy is resolved at the consuming tier, just like its SDF.
Field queries, paired mesh draws, receiver shading and light views use the same
policy; direct visibility, collision and direct-shadow policy stay independent.
Nested indirect distance and gradient queries suspend the caller's ambient and
shadow masks for their own field scope, then restore both masks. A full-field
gradient uses the same caster field as a full-field distance query.
Changing the body policy withdraws transport through the existing geometry queue.
The policy occupies two existing instance/mesh flag bits and one common pass word.
The light map's mesh identity includes only actual casters, so moving a
receiver-only mesh does not schedule a new depth region.

Excluding an instance requires complete independent root operands. Internal CSG
inside an instance is supported; cross-instance field operations are refused
when a policy would omit an operand. Text-bearing moving creations keep their
host shapes and engraving inside one existing field scope and one root-bounded
instance; omitting the placement therefore removes every coupled operand.
The CPU irradiance reference currently names
filtered `Receive`/`Off` programs as unsupported rather than reporting ordinary
collision-field values as an indirect comparison.

## Finite indirect lighting sweeps

The residency's `shade` pass follows placement, partition and transport over the
same cache allocation. Once transport finishes, the shared `IrradianceSolveSchedule`
visits coarser levels before finer ones, within the tier's probe allowance. The
direct sweep starts from zero; medium then runs two feedback sweeps and high four.
A planned batch stays intact until successful submission, and only an entire
sweep publishes a generation. Transport completion does not count as a lighting
sweep. A completed unchanged solve schedules no further shade dispatch.

One solve pins its packed program, pose and lighting tables in existing GPU
regions and binds them through the ordinary World interface. Later source edits
wait for the finite solve to finish before starting another. The light camera
uses that same captured light table. A solve accepts its maps only when their
table owner and uploaded geometry revisions match the pinned source; maps for
later live poses fall back to bounded rays through the captured World tables.
Allocation accounting includes the pinned
regions, their rings and the separate shade-update region; resizing a pinned
region waits for its previous readers before replacing it.

When an environment producer is registered, the same source owner holds a
65,536-byte map, 144-byte coefficient snapshot and 8,704-byte screen reduction
snapshot in device-local memory. Each enabled source category admits its copies.
The indirect producer's `environment-pin` pass declares all three current exports
as transfer inputs and their snapshots as transfer outputs. CPU sources stage
before the residency upload; the pair copies after projection, and successful
copy submission admits the first shade batch. A newer environment publication
does not alter an active solve. Its exact owner and sequence participate in
desired-source matching, so readiness waits for a solve using that newer source.

Sky is enabled by default with direct light, feedback and emission. Only a ray
certified clear to the world's far boundary samples the pinned full environment
map, through four counted bilinear loads before artistic ambient or reflection
gains. Hits add no unoccluded sky term: light reflected from the previous complete
bank remains Feedback. Continuations keep their source categories; unresolved
rays supply no radiance and enter no cosine denominator. Turning Sky off skips
the map lookup and its four loads.

`LightingSource` describes the active solve, while `PublishedLightingSource`
describes the complete generation readers still see during a later solve.
Each retains immutable CPU tables and exact GPU source publications; `CopyFrame()` supplies independent mutable
light and sky tables for a CPU reference without consulting a newer live frame.
The cache snapshot reports admitted shade probes and submitted whole sweeps.
The independent CPU reference uses the same captured program and its immutable
material palette. `EstimateSources` follows the ordinary Halton paths while
attributing first-hit direct light, emission, sky and screens separately from
later feedback. `EstimateIncidentSources` instead fixes its first ray to the
captured, already-launched origin and finite nonzero direction. It normalizes
that direction, repeats it for one through 256 paths, then uses the same Halton
reflection samples and finite source fold for zero through nine later bounces.
It neither averages a new first hemisphere nor launches off the receiver again.
This is an independent physical incoming reference, with actual field query
counts, rather than a simulation of Near's twelve-query allowance.
The solve source's mask applies during transport; a rendered
receiver's captured mask selects the final categories. A required launch or ray
that fails remains unresolved and supplies no numerical divergence. Exactly zero
diffuse reflectance skips direct and screen queries and later reflections that
cannot contribute; every nonzero component retains those queries and the requested
depth. Point-specific reflectance includes attenuation on reflected light and feedback, leaving
emission independent. Explicit World explanations run this reference once for
a supported cache pick, at the visible publication's sweep depth. Uncaptured
screen/portal radiance, alternative rendered-frame sources, triangle meshes
and unsupported CPU field instructions report named refusals. Procedural sky
uses the existing quantized environment reference without artistic gain.
`CompletedSweeps` follows the active solve; `PublishedSweeps` follows the visible
bank and retains its previous depth until a whole new sweep replaces it.
The shared `sdfIndirectDiffuse` fold evaluates explicit lights in their table
order and applies the hit material's albedo, metallic exclusion and `bleed` once.
It returns direct and emission contributions plus reflected-light
attenuation; feedback and sky sampling remain the caller's separate operations.

High views may replace one incoming cosine sample per four render pixels with a
local field ray of at most 0.5 m and 12 shared field queries. The same allowance
covers transport, gradients, secondary launch and connectivity proof; exhausted
or unsupported rays keep the whole cache fallback. A local clear endpoint reads
direction-matched radiance from the existing finest-level bank and never becomes
a world sky exit. A local hit uses explicit diffuse shading, excluding specular,
rim and fog. Its feedback reads only the exact preceding complete bank of the
same source. Near is off below high, for alternative comparison methods, while
frozen, and until the exact current source's complete solve is fenced. It reads
no temporal colour history; the resulting diffuse contribution joins the normal
P15 history and reactivity path. The reserved `indirect-near` row owns Near's
actual field queries, hashes, loads and outcomes; separately bounded directional
shadow queries remain in the ordinary indirect row. Fenced inspection reports
the actual Near outcome. The cache CPU estimate names a Near replacement as
unsupported instead of reporting a misleading cache divergence.

Primary traversal publishes a receiver approach in the existing visibility
record. It keeps a positive complete-field sample whose clear ball joins the
accepted sample, at most half the finest spacing away. The packed retreat and
clearance use one word: moving the point onto the half-float grid subtracts
the reconstruction error from its radius, and the radius rounds downward.
Misses, meshes and uncertified approaches publish zero. With indirect enabled,
primary evaluates its existing interpreter over the complete field instead of
independent parts and camera masks, whose exclusions cannot certify that ball.
This adds no launch query, but the primary shape count can increase.

The views pass applies the complete lighting bank through the same certified
component proof and irradiance weights as the solve. A missing approach uses
the bounded normal launch. All views share the tier's finite new-proof allowance;
its admission and deferred counters reset once through the residency's existing
trace pass, including frames with no new transport rays. Each receiver retains
its exact launch and completed result in the visibility record, independently
of shared proof-hash collisions. The complete allocation identity and transport
revision qualify this certificate; a new primary sample clears it. Completed
unresolved results also stand, while deferred results remain retryable.
The existing readback ring copies the actual deferred count and accepts it only
after its submission fence, for the same allocation, revision and primary write.
Pending receivers keep Views active without rerunning unchanged Primary; zero
completes that scope. Frozen caches read completed proofs and admit none.
The certificate adds 32 bytes per allocated render pixel, and each allocated
completion-ring slot owns four host-visible bytes, both reported in graph memory.
Material albedo, metallic diffuse exclusion, `receive` and AO apply once
after the selected algorithm returns its independent source contributions.
The `indirect` debug view shows the incident sum before those material factors.
`SdfFrame.IndirectSources` carries one shared category mask into both the solve
and receiver algorithms. Its bits follow the five stored categories: direct 1,
feedback 2, emission 4, sky 8 and screens 16. The current default enables the
first four; screen transport requires its captured publication path and explicit
enablement. A source-mask edit is lighting-visible and does not retrace geometry.
Disabled categories are zero in newly solved records and receiver results,
including an earlier complete bank retained while the new solve runs.

Screen radiance comes from the acquired GPU image. The residency's existing
environment producer reduces each admitted image into sixteen cell means and
one pixel-weighted whole-image mean. The 4×4 grid supplies bilinear physical
emission at an indirect screen-face hit; the whole mean supplies analytic room
glow only at the view's own surface. The indirect diffuse fold excludes that
analytic light, so the same screen never contributes through both paths.
Same-world camera feeds emit zero; independent sources and other-world views
can emit. Missing and excluded images write zero records. Every actual image
load and every written record is counted. The 8,704-byte residency buffer is
shared by all views, with one further immutable copy per finite solve.
Source owner, sequence and taint travel with that copy. The CPU explanation
continues to refuse screen transport because it has no captured GPU pixels.
Every capture resets radiance publication and view history, retaining reusable
geometry transport, then waits for a fresh finite solve of its capture-filled
sources and that solve's exact completed fence. An older tainted bank keeps its
taint even after clean image reductions arrive. A frozen indirect control
refuses the cold capture without changing its retained bank.
Finite portal-closure iterations remain implementation work; previous-frame
image dependencies alone do not provide that policy.

An existing surface-picker request also copies a 288-byte receiver record and
the allocated probe-state range under the visibility copy's fence. The answer
retains its cache epoch and published lighting source across later resets. Its
eight corners carry actual classifications, stamps and normalized weights; its
five RGB contributions come from the GPU result, and its method identifies an
alternative replacement when selected. Corners then describe the cache fallback.
The whole-cache census counts only the captured brick inventory at that epoch.
Graph accounting includes the declared receiver record and every live or retiring
readback slot, including the census buffer.

An answered Near replacement also retains its sampled direction and exact
same-source predecessor stamp in that record. Its incoming source is admitted
only when the captured High cache method, current bank and predecessor agree.
The World reference follows this exact first ray, then independent finite paths;
missing provenance supplies no numerical divergence. Normal launches and
completed receiver connectivity are retained in the visibility record, separate
from the cache's finite proof buckets. Current GPU qualification must still
measure standing receivers' zero-new-proof-work behavior.

The counted comparison has a per-view SdfIndirectMethod selector: the ordinary
cache, current screen-space visibility, or one-bounce field cones. Its pass value
belongs to the lighting signature, so changing the method re-renders shading and
its consumers without changing geometry or shadow ownership. Quality restrictions
retain the consuming view's method. The alternatives interleave four render-pixel
parity classes through the existing temporal phase, with four equal-weight
stratified cosine samples. Screen-space rays take at most twelve projection
samples in this consumer's current visibility slice; missing, offscreen and
unsupported witnesses keep the cache fallback. Cones take at most twenty-four
full-field evaluations over four world units, including their surface gradient
and sign witness, including a gradient whose normal rejects the hit. Existing
directional shadow fallbacks retain their own bounds. Views attribute those
light queries and every field step once to their reserved indirect row; the
finite cache solve retains its separate map and fallback rows. A certified
secondary hit uses the shared explicit diffuse source fold; its result replaces
a sample rather than adding another bounce. A screen hit instead reads the same
pinned acquired-image emission as the cache and Near. It terminates the sample,
including a valid dark answer, without passing cached sky through the screen.
A local clear interval cannot declare a sky exit, and an ordinary hit's unproved
sky hemisphere retains the cache sky contribution. Both methods return independent categories
before the receiver's material response. Their device laws and measured
comparison remain open qualification work in the rendering plan.

## Ambient occlusion: three taps into the ambient fill

Puck's AO is the classic normal-ladder technique: from the hit, step fixed
rungs outward along the surface normal, and at each rung compare the distance
the field *should* read against what it *actually* reports; the deficit
accumulates as occlusion. The textbook ladder uses five rungs; Puck's uses
**three**, re-spaced to span the same reach at double pitch with the falloff
and gain re-tuned so the fully-occluded floor matches the five-tap look. The
rungs run in one rolled loop, so the ambient kernel carries a single inlined
copy of the interpreter. It reads convincingly as contact shadowing in
creases and under overhangs for the cost of three field evaluations, paid only
on lit hits in the ambient pass. `world.ao-quality fast` swaps the ladder for a
one-sample contact estimate, and `auto` does so at 16 or more simulated bodies.

Two rules give it its shape. It multiplies into **ambient light only, never
direct**—folding occlusion into the whole lighting equation causes ghosting;
the sun stays governed by the soft shadow above. And the `(h − d)` deficit mixes
a world-space rung height with a scaled field sample, so `d` is de-scaled first
(the chapter's recurring rule again).

There is *no* screen-space AO, no hemisphere sampling, and no AO history—all
of that fights the no-RNG posture that keeps the renderer deterministic across
backends, and every frame's occlusion comes from the field alone. (A temporal
view's [reconstruction](frame-rendering.md#temporal-reconstruction) filters the
shaded color afterwards and never feeds back into AO.) The three-tap ladder and its one-sample fast
path are the whole AO story: the cheapest technique with the largest perceptual
gain, and zero architectural disturbance.

## Every light answers through one interface

The views pass lights a surface by walking one list of lights: the lights
table's (directional, point, rim and occluder, in authored order),
then every bound screen. Each is one `SdfLightSource`, and one function,
`sdfLightResponse` in `shade/sdf-light.hlsli`, answers what it adds at the
surface: a diffuse term that joins the radiance the material shade lights by, a
specular lobe (a point light's own), a rim brighten, and a factor on reflected
light (an occluder's dimming). The stage sums each kind of term over the walk
and adds each total once, so the order the terms combine in is the walk's. No other kernel source branches on a light's
kind, which `SdfLightInterfaceLawTests` holds, so a new kind is one branch of
that function.

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

## The shadow-proxy switch uses the union hull

Destructible geometry creates a specific, expensive shape: a dense cluster of
subtraction ("carve") instances, each one a hole punched in the world. Shadow
rays through such a cluster re-march every carve, and the frame becomes
shadow-bound on damage the player caused.

The **shadow-proxy** switch trades a little visual fidelity for a lot of speed
here. When enabled, the shadow gather *omits* carve-family instances—it
evaluates the **pre-carve union hull** for shadowing purposes. On a dense carve
cluster this collapses the occluder set from many to few by construction. It is
*conservative*: skipping a pure subtraction can only make the field more solid,
so shadows go slightly darker or fill holes that light would otherwise thread —
they never leak the wrong way. The visual tradeoff is exactly that: light that
"should" pass through a freshly-carved gap is instead blocked by the hull the
gap was cut from. It is off by default and is set by `SdfFrame.EnableShadowProxy`;
no `Puck.World` verb sets it.

## Shading switches

Several shading terms are exposed as **live levers**—runtime settings a user
flips in `Puck.World` to isolate a term's cost or restyle the frame with no
restart. They ride reserved lanes in a per-frame parameter row, so an unset
frame uploads zero and every default is preserved.

| Verb | Effect | Why you'd use it |
|---|---|---|
| `world.shadows off\|low\|medium\|high\|0..1 [crowd-radius]` | `off` leaves the sun unshadowed; other values scale the shadow ray's reach *and* its gather cone together, and the optional radius bounds which avatars cast | Isolates the most expensive shading term, or measures shorter-reach shadows (both lengths must scale together or the gathered set is unsound) |
| `world.ao on\|off` | `off` forces occlusion to 1; creases brighten | Isolates the three AO field evaluations per lit pixel |
| `world.ao-quality auto\|exact\|fast` | Picks the three-rung ladder or the one-sample contact estimate | A/B of AO cost against look |
| `world.shadow-mask auto\|exact\|camera-tile` | Picks the per-workgroup shadow gather or the camera tile's mask | Measures the gather's cost; the camera-tile mask drops off-frustum occluders |
| `world.shadow-march auto\|exact\|fast` | Picks the full soft-shadow march or a shorter, coarser one | Bounds shadow cost in crowded sessions |

The `auto` settings switch to the fast side at 16 or more simulated bodies.
`SdfFrame` also carries lanes for disabling screen lights
(`DisableScreenLights`), the shadow proxy (`EnableShadowProxy`), and the
four-tap normal (`UseFiniteDifferenceNormals`); no `Puck.World` verb sets them.

**Curvature shading** adds cavity darkening in creases, rim light on ridges,
and ink lines where curvature spikes. The authored `render.lighting.curvature`
gains enable it at runtime; all three at zero disable it. It uses four nearby
field samples and a center distance to estimate a discrete Laplacian, since
the analytic normal alone provides no second derivative. These samples also
supply the surface normal. This path counts scalar shape evaluations and no
analytic shape gradients; the courtyard's inherited curvature gains select it.
Programs without
shading-only detail reuse the center distance the visibility record holds. See
the [renderer README](../../../../src/Puck.SdfVm/README.md) for that reuse.

---

## Related resources

- The forward-mode dual normal, its parity argument, and the four-tap comparison
  path: [../reference/gradients-and-normals.md](../reference/gradients-and-normals.md).
- Soft shadows (classic penumbra + closest-approach parabola), the normal-ladder
  AO, the shadow-proxy, curvature/NPR, and the surveyed-but-unbuilt occlusion
  family: [../reference/shading-ao-shadows.md](../reference/shading-ao-shadows.md).
- The de-scale invariant and why coverage AA is the exception:
  [../reference/lipschitz-and-field-correctness.md](../reference/lipschitz-and-field-correctness.md).
- Shadow culling wins or loses depending on occluder density; the boundary
  between the two is unmeasured.
