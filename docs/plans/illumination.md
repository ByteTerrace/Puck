# Illumination

Puck lights a frame with cascaded shadow maps for the key light, a per-view
light loop, and a three-level probe volume traced and relit on the GPU for
indirect light
([the lighting handbook](../rendering/sdf/handbook/lighting-and-shading.md)).
The owner asked for a research round on global and local illumination before
any further graphical work, and three independent reports covered it: global
illumination, shadows and direct light, and the latest research and stylized
lighting. Their consensus lists agree on the same five items.
This plan turns that agreement into decisions, says how each decision serves
an author building a scene nobody here imagined, settles the gaps the round
found, records what was rejected and why, and orders the build as lanes
checked on the running debug world.

The criterion that decided every verdict is the owner's: authors will be more
creative than us, and Puck exists to serve them. A technique serves authors
when it handles arbitrary scenes (any geometry, dynamic and emissive objects,
portals and windows, interiors and exteriors), when it is a general mechanism
with looks layered on as authored styles, when it degrades gracefully with no
cost cliff, and when its cost is visible and budgetable. The bar is
[the frame bar](../decisions/rendering.md#the-pipeline-foundation), the worst
frame at the highest quality with no lower quality tier as a fix. Every number below that
came from a paper or a talk keeps its original hardware; none is a prediction
for Puck, and no estimate for the Deck is extrapolated from the 2060.

## Where the volume stands

The research round read the code, and the plan rests on these facts:

- A probe record (`SdfMeshIrradianceProbe.ProbeWords`) holds six sky shares,
  an 18-half radiance ambient cube, 128 static-light visibility bits, a
  relocation offset, an 18-half sky-light cube and a 12×12 octahedral first-hit
  map with curvature
  ([mesh GI reference](../../.claude/skills/rendering/references/mesh-gi.md)).
  The sky enters as the sky-light cube the relight integrates from the open
  rays (`shade/sdf-mesh-irradiance.hlsli`, `sdfMeshIndirect`). The sun, the static point and
  spot lights and every emissive surface are baked into the radiance cube at
  trace time, and later bounce sweeps bake the sky's colour too.
- A probe is a hit buffer and a relight: one full-reach closest hit per
  octahedral texel, from which its sky shares and surface map derive, traced
  once per geometry; and its radiance cube and light bits, relit from those
  hits when the lighting version advances (`SdfMeshIrradiance.RefreshLighting`,
  which hashes the sky too). A light change therefore costs a relight and never
  a trace.
- The two former ray sets (128 or 96 Fibonacci radiance rays and 144 bounded
  depth rays) are one set of 144 octahedral rays at full reach.
- Only static opaque draws cast into the volume (`SdfMeshIrradianceScene`
  admits `!IsDynamic && !Glass`). A moving body receives light and casts none;
  a lane-driven emissive reads zero in the trace.
- The trace and the relight run on the GPU through the background GPU service
  (`SdfMeshProbeGpuWork`). Nobody has measured either on the Deck.
- The views pass loops over every light the view's frustum selected
  (`passes/sdf-light-stage.hlsli`; `sdfLightNext` walks the set bits of the
  view's light mask and `sdfLightResponse` exits at zero reach). A bound screen lights a surface
  as an unshadowed centre-and-area diffuse term with no specular.
- The cascades cover 16 m near and 120 m far with casters 200 m toward the
  light, authored as `render.shadowCoverage` (`SdfShadowCoverage`) and printed
  by `world.budget` with each cascade's texel. The surface pass queries them
  wherever a light's diffuse response is non-zero, a wrapped material's side
  past the terminator included (`passes/sdf-surface-stage.hlsli`,
  `sdfMaterialShadowDemand`); a triangle turned from the light reads the cascade's exit
  layer, so only its own body lets the wrap's light through
  ([decision](../decisions/rendering.md#a-light-is-shadowed-wherever-it-lights-and-cel-shading-is-a-style-per-material)).

## The five decisions

### 1. Split each probe into cached hits and a budgeted GPU relight

**Puck builds:** one octahedral hit buffer per probe (distance, normal and
material per texel, at full reach), written by the geometry trace, and a GPU
compute relight that shades N probes a frame from those hits: sun Lambert under
sun visibility, static lights under their visibility bits, emission including
lane-driven emissives (the frame slots are on the GPU) and screens, and bounce
from the previous volume, folded into the ambient cube. A probe not relit this
frame is rescaled from per-probe metadata (sky visibility, mean sun and sky
luminance) so the volume stays balanced through a time-of-day transition. A
light change costs a relight; only a geometry change costs a trace. The two
ray sets merge into the one hit buffer. The patch visibility rule stays as it
is; the hit buffer is its storage, so no second visibility representation
appears.

**Why:** all three reports put this first or second, and a shipped engine
proves it. Assassin's Creed Shadows keeps a 20×20 octahedral G-buffer per
probe, spends about 84 % of its probe budget on relight-only updates, relights
a probe about three times a second and re-traces it about every two seconds,
and costs 0.97 ms for 512 probes on an RTX 4070 (Leblanc and Conte, "Ray
tracing the world of Assassin's Creed Shadows", SIGGRAPH 2025 Advances in
Real-Time Rendering,
<https://advances.realtimerendering.com/s2025/content/Advances%202025%20-%20Raytracing%20the%20world%20of%20Assassin's%20Creed%20Shadows.pdf>).
Lumen's world radiance cache states the pattern Puck needs as "fixed update
cost, variable lighting latency" (Wright, Narkowicz and Kelly, "Lumen",
SIGGRAPH 2022 Advances,
<https://advances.realtimerendering.com/s2022/SIGGRAPH2022-Advances-Lumen-Wright%20et%20al.pdf>).
DDGI's measured 2.6 ms for a 32×8×32 volume at 64 rays per probe, all probes
every frame on an RTX 2080 Ti at 1080p (Majercik, Guertin, Nowrouzezahrai and
McGuire, "Dynamic Diffuse Global Illumination with Ray-Traced Irradiance
Fields", JCGT 8(2), 2019, <https://jcgt.org/published/0008/02/01/>) bounds
what relighting every probe would cost and shows why a budget is needed.

The sky term joins the split as a transfer, not a scalar. Six sky shares make
a low western sky light an east-facing opening as if the sky were uniform about
that axis. Settled: since the probe holds its hits and relights on every
lighting change (the sky included), the relight integrates the sky itself, each
open ray carrying the sky's radiance about its own texel into a sky-light cube
beside the radiance cube (`SdfMeshProbeDepth.SkyRadiance`,
`SdfMeshIrradianceProbe.SkyLightWord`), which is the transfer evaluated exactly
at the hit buffer's angular resolution, with the cosine term, and with no SH
band limit at the horizon. A share times the hemisphere's SH irradiance had read
a window low in a wall at about half its light (the window-room harness: 0.49
of the path-traced floor) while the lawn bounce through the same window was
exact, which tinted window-lit rooms toward the lawn. The idea is precomputed radiance
transfer: Sloan, Kautz and Snyder, "Precomputed Radiance Transfer for
Real-Time Rendering in Dynamic, Low-Frequency Lighting Environments",
SIGGRAPH 2002, <https://www.cs.jhu.edu/~misha/ReadingSeminar/Papers/Sloan02.pdf>;
the all-frequency form the owner named is Ng, Ramamoorthi and Hanrahan,
"All-Frequency Shadows Using Non-linear Wavelet Lighting Approximation",
SIGGRAPH 2003, <https://cseweb.ucsd.edu/~ravir/papers/allfreq/index.html>,
whose wavelets are needed only for all-frequency lighting; diffuse sky is
low-order. Far Cry 3 shipped the probe form for a day cycle (Stefanov,
"Deferred Radiance Transfer Volumes", GDC 2012,
<https://gdcvault.com/play/1015326/Deferred-Radiance-Transfer-Volumes-Global>).

### 2. Make direct light correct at author scale

**Puck builds:** the wrapped-diffuse query fix (the surface pass queries the
cascades wherever the wrapped term is positive, without self-shadowing the wrap
away outdoors); cascade coverage as an explicit, measured, author-visible
resource rather than three constants (the valley's walls stand 205 to 430 m
from the origin, past the 120 m far split; the terrain pass's far cascade is
its step 7); a banded style that keeps rim, reflections and point-light
occlusion; a visibility-preserving banding option, `band(diffuse) × visibility`
beside `band(diffuse × visibility)`, as authored style data; and
shading lengths that follow a body's scale (cascade bias, AO and shadow tap
radii, `NormalBias`).

**Why:** these are demonstrated defects fixed with no new machinery, and they
come first because every later measurement reads through them. The PCSS
contract Puck implements is the nearest in-cone blocker, a deliberate sizing
heuristic over the textbook mean-blocker search (Myers, Fernando and Bavoil,
"Integrating Realistic Soft Shadows into Your Game Engine", NVIDIA 2008,
<https://developer.download.nvidia.com/assets/gamedev/docs/PCSS_Integration.pdf>);
the cascade design follows the stable-projection rules in Microsoft's
"Cascaded Shadow Maps"
(<https://learn.microsoft.com/en-us/windows/win32/dxtecharts/cascaded-shadow-maps>).
The warm/cool reasoning behind coloured shadows is Gooch, Gooch, Shirley and
Cohen, "A Non-Photorealistic Lighting Model for Automatic Technical
Illustration", SIGGRAPH 1998
(<https://users.cs.northwestern.edu/~ago820/SIG98/sig98_talk_web2/index.htm>),
and it is a style, not a shadow algorithm: tinting unoccluded sun into a
shadow is light through walls.

### 3. Move the probe geometry trace to the GPU behind one trace-hits interface

**Puck builds:** one interface that fills a probe's hit buffer, with two
implementations measured on all three targets before one is chosen: inline ray
queries over a static-mesh acceleration structure (DXR 1.1 `RayQuery`,
<https://devblogs.microsoft.com/directx/dxr-1-1/>; `VK_KHR_ray_query`,
<https://docs.vulkan.org/refpages/latest/refpages/source/VK_KHR_ray_query.html>),
and software tracing over a coarse representation derived from the bake. The
bake already samples every group's field to a lattice
(`SdfSampledLattice`), but those lattices are not ready-made Euclidean SDF
bricks: static unions refine sparsely and CSG seams hold interior distances,
so the software leg needs a conservative traversal and its own derived
product, kept as a cache under the
[derived-product decision](../decisions/engine-design.md#a-derived-product-is-a-cache-never-the-truth).
Either leg lets a moving rigid body cast into the volume and takes the CPU
trace off the frame's path. The software leg is built
([the build order](#the-build-order)); the hardware leg is built too and owes
its reading ([after the GPU trace](#after-the-gpu-trace)).

**Why:** Lumen kept software tracing over SDFs for "60 fps consoles" and for
heavily overlapping meshes that are slow with hardware RT, and documents the
thin-wall leaks that need a half-voxel expansion (Lumen 2022, above). Godot's
SDFGI traces probes against cascaded SDFs built from static geometry, with
dynamic objects receiving but not contributing
(<https://docs.godotengine.org/en/stable/tutorials/3d/global_illumination/using_sdfgi.html>).
DDGI's Table 1 measured 1.5 to 1.65 GRays/s of incoherent probe rays on a
2080 Ti; the Deck's RDNA2 ray accelerators do four box or one triangle test
per clock with traversal in shader code
(<https://chipsandcheese.com/p/amds-rdna-2-shooting-for-the-top>), so its
hardware leg is expected to be an order of magnitude slower and must be
measured, not assumed. Probe-only tracing is the one place hardware RT might
pay at the bar; per-pixel hardware RT stays out of the frame.

### 4. Visibility-bitmask AO with a bent normal steering the probe gather

**Puck builds:** a replacement for `sdfMeshAmbient`'s 8-tap obscurance that
marches the existing depth pyramid with sector bitmasks, lets light pass
behind thin surfaces (grass blades, railings), and writes a bent normal into
the visibility record so `sdfMeshProbesAt` reads the ambient cube along the
bent normal rather than the shading normal. It covers detail below the probe
spacing at a fixed per-pixel cost.

**Why:** all three reports agree. The horizon-based formulation is Jimenez,
Wu, Pesce and Jarabo, "Practical Real-Time Strategies for Accurate Indirect
Occlusion", SIGGRAPH 2016 courses
(<https://research.activision.com/publications/archives/practical-real-time-strategies-for-accurate-indirect-occlusion>);
XeGTAO measures about 0.56 ms at 1080p on an RTX 2060, with bent normals
about 25 % more (<https://github.com/GameTechDev/XeGTAO>). The bitmask
variant is Therrien, Levesque and Gilet, "Screen Space Indirect Lighting with
Visibility Bitmask", The Visual Computer 2023
(<https://arxiv.org/abs/2301.11376>); its abstract gives no timing, so the
bitmask's cost is Puck's to measure. The rendering decision's objection to
screen-space indirect light (per view, multiplied by portals) does not apply:
this modulates ambient and steers a world-space gather, and supplies no
radiance of its own.

### 5. Light lists, area lights and budgeted local shadows

**Puck builds:** a measured comparison of today's per-view frustum selection
against tiled and clustered lists, keeping whichever bounds the light loop at
the debug world's 68 fixtures and at ten times that; LTC area response for
screens, portal openings and any authored rectangle, replacing the
centre-and-area approximation and giving screens specular; and a residency for
local shadow casters (a budgeted atlas of point, spot and area shadows, cached
while a light and its casters hold still) whose overload is reported, never
silently dropped. A light that lost its shadow is named in the counters and in
a new `world.lights` listing, and the frame shows a defined fallback (probe
visibility, as today), not a flicker between the top K.

**Why:** authors will place more fixtures and screens than the debug world
does. Olsson, Billeter and Assarsson, "Clustered Deferred and Forward
Shading", HPG 2012
(<https://diglib.eg.org/bitstreams/237032bc-faae-48ce-812a-3fede4d80833/download>)
measured clustered 17 ms against tiled 26 ms at about 2,400 lights on a
GTX 480, and shows the method changes which lights are evaluated, not their
response. Heitz, Dupuy, Hill and Neubelt, "Real-Time Polygonal-Light Shading
with Linearly Transformed Cosines", SIGGRAPH 2016
(<https://eheitzresearch.wordpress.com/415-2/>) integrates polygonal emitters
analytically but unoccluded; visibility stays a separate mechanism. HypeHype's
stochastic tiled lighting for user-created scenes on low-end hardware
(Lempiäinen, SIGGRAPH 2025 Advances,
<https://advances.realtimerendering.com/s2025/content/s2025_stb_lighting_v1.1_notes.pdf>)
is the comparison for the overload case, and its own tile-cap flicker warning
is why overload here is reported rather than sampled away.

## What serves authors

Each decision has to hold for a scene an author builds, not the gallery alone.

| Decision | Dynamic geometry | Emissive and lane-driven surfaces | Portals, windows and screens | Interiors and exteriors |
|---|---|---|---|---|
| 1. Hits and relight | Static hits are cached; a moving body's influence waits on decision 3, and until then it receives light and casts none, as today | Emission is shaded at relight time from the live frame slots, so a screen that changes or a lamp a rule switches costs a relight, not a trace | A screen or opening is an area emitter in the relight ([gaps](#the-gaps-the-round-named)) | One mechanism; the budget is per probe, so a stairwell and a valley cost the same per probe relit |
| 2. Direct light | Cascades draw animated meshes through the shared vertex stage already | Unaffected | A portal view runs its own cascades and queries with its own depth | Coverage is an authored, visible resource; the terrain cascade covers the valley's walls |
| 3. GPU trace | A rigid body is an instance in the acceleration structure or a transformed proxy, so it casts bounce and occlusion | Hit material is recorded; emission is read at relight | Glass stays out of the trace until coloured transmission lands ([rendering plan](rendering.md#glass)) | Thin interior slabs are the software leg's known leak; the hardware leg traces the triangles exactly |
| 4. Bitmask AO | Screen space follows whatever the mesh pass drew | Unaffected | Each view, portal views included, computes its own | Fixed per-pixel cost indoors and out |
| 5. Lights | Casters invalidate only their own atlas regions | An emissive rectangle can be an LTC light | A screen is an area light with specular | Clustering pays indoors where depth separates rooms; the measurement decides |

**Looks are authored.** Painterly banding, coloured shadows, a macro-normal
terminator, a chroma floor in shadow, grouped foliage normals and translucency
are style data in `worlds/standard/` that the debug world imports, applied
through the existing receiver levers (`bleed`, `receive`, `wrap`,
`render.indirect.apply`, the toon tuning). None of it enters the volume, the
relight, the cascades or an engine default (AGENTS.md, "the engine is
generic"). A style that tints a shadow reads the local indirect chromaticity
the volume supplies; it never invents light the sun did not deliver.

**The budget surface nobody covered.** No report named what an author sees and
sets. Puck gives them one surface, in the world document and in the live
verbs:

- `render.indirect.budget` holds the probes relit a presented frame, the hits
  traced a frame, and the target lighting age in seconds; each quality table
  (`indirectBudget`, a device's own `table` among them) replaces it with that
  device's count, and the authored day length sizes the relight count (a
  24-minute day moves the sun 0.25° a second). The defaults live in the
  standard package as data, not in C#.
- `world.budget` prints the volume's cost sheet beside the program's: probes
  per level, bytes per copy, the relight and trace budgets, and the lighting
  age the current scene settles at.
- `world.counters sdf.mesh-gi` gains relit, traced, the age distribution (p50
  and max) and the number of lights whose shadow or probe influence was
  deferred this frame; `world.lights` names them. The sweep's signals carry
  the same counters so a regression shows in `puck sweep`.
- Overload degrades latency, not frame time, and says so: a scene with more
  changing light than the budget relights shows a rising age counter and an
  `[sdf.mesh-gi] behind` line naming the shortfall, never a dropped light or a
  slower frame. An author who wants the age down raises the budget and reads
  the frame time move in `host.frames`.

## Cel shading

Cel is the debug world's treatment (the owner's ruling,
[recorded in the decision](../decisions/rendering.md#a-light-is-shadowed-wherever-it-lights-and-cel-shading-is-a-style-per-material)):
every place opts in through its materials and is judged under it. The mechanism this plan built carries it:
a style per material with the world's default, `band(cosine) × visibility` with
the penumbra kept, and rim light, reflections and ambient occlusion surviving
the band
([the handbook](../rendering/sdf/handbook/lighting-and-shading.md#cel-shading)).

The cel-specific work that waits with it, kept here so it is not lost:

- **The band under indirect light.** Indoors a banded material is lit mostly
  by the traced bounce, which a style tints as its shadow band, so it reads as
  its shadow tone. The designed answer is that the band's colour comes from the
  probes' bounce and the sky tint along the bent normal, multiplied by the
  style's tint, through the authored `bleed`, `receive` and indirect-tint path
  and not a second lighting model. Whether the hall's band should warm under
  its white lamps is the style's authoring.
- **Outlines against the meadow and the portals.** The depth and normal edge
  test runs before the temporal resolve, so thousands of animated blades are
  the stress case for ink shimmer, and portal pictures for ink bleeding onto a
  picture the style must leave alone.
- **A dawn leg.** With the sun near the horizon the standard style's threshold
  puts every horizontal surface in the shadow band, so a penumbra check under a
  cel style needs a style whose threshold sits below the low sun's cosine.

## The gaps the round named

**Screens, openings and static emitting faces feeding light into the probes.**
Today a screen reaches a view only through the analytic room glow
([screen emission](../rendering/sdf/handbook/lighting-and-shading.md#screen-emission))
and the trace ignores it, while static emitting faces such as the hall's
ceiling strips light the floor in blotches. Decision: screens, portal openings,
windows onto other worlds and static emitting faces are area emitters in the
relight. They share one emitter store with the per-source-group storage of
[lighting proportional to change](#lighting-proportional-to-change) (build
step 2) and with screens as selected lights (build step 5), so the three are
designed together, with LTC response at the hit (decision 5). The standard LTC
technique for polygon lights does not include blocking, so how an emitting face
is shadowed is an open design question inside that work.

A portal is a display for lighting purposes (the owner's ruling): the other
world's image already carries that world's sun and sky, so the relight uses the
image, and no directional light crosses a portal. An author who wants the other
side's sun to cast through places a light. This keeps every residency's
lighting its own and costs a texture read per hit facing the opening.

Light carried across an opening refines what that image gives without breaking
the ruling. The goal: the garden painting's sunset reaches the gallery floor
even when the player faces away, and a cabinet in its way casts a shadow the
hall works out. Within the scope the destination's authority permits each
viewer, including whether the opening transmits light to that viewer at all
([disclosure](disclosure.md#presentation-and-derived-product-disclosure)),
the destination publishes the pre-exposure light leaving the opening, first as
a single average and then as a few samples by position and direction, each with
the time it was taken. The near world integrates it against its own surfaces
and blockers. Only paths crossing one boundary count, each once, because two
openings facing each other would otherwise feed back forever. The first step is
a one-process prototype on the garden painting: the camera, the far light and
a set of near-floor patches are fixed (some lit, one in the cabinet's shadow),
and the light arriving at each patch before exposure is compared against a
reference that samples the far scene in full, normalized by the reference's
brightest patch so an unlit patch has a denominator and the shadow is read on
its own. Incident light is read before exposure and the displayed floor
separately; raising the camera's exposure must never brighten the floor light.
A few directional samples are kept only if they beat the average against that
reference at well under the cost of a second view.

**Nested worlds sharing one budget and one probe pool.** Every residency today
owns its own probe volume (~72 MB table in up to four copies and ~90 MB hit
cache at High, rising to ~135 MB with transmission's three-word hits) and its
own fixed level table (`SdfMeshIrradiance.LevelsOf`). Decision: the relight and
trace are jobs of the job system's `Lighting` class, sharing one background
budget and one probe pool across residencies, priced in the one unit of
[background step 6](#background-gpu-work-and-headroom). Consumer minimums are
served first, and remaining work divides by each view's share of presented
pixels; a residency no view shows and no consumer is owed runs no GI work.
Without a minimum a small share starves, and a probe put to sleep lights a
passing body with older light, so a sleeping probe needs something that wakes
it when the scene inside its region changes. The pool also ends the dark period
while a newly visited area's whole probe grid is measured.
Probes in open air update at a coarse level's rate, moved regions publish slot
by slot, and the probe level table moves to authored data in `worlds/standard/`
once the pool is designed (until then every store reads `SdfMeshIrradiance.LevelsOf`
and is tested against a non-default table). A pooled volume is shared only among
viewers permitted the same delivered static rows
([streaming and memory](streaming-and-memory.md#decision-4-a-residency-is-staged-by-its-demand-not-by-a-render)).
Counters report age per residency, so a nested world that falls behind says so
without moving the main world's age.

Built of this: the order and the sizing, on the one budget. Each residency's
background service holds a turn weighted by its share of the display's pixels
(`SdfFrame.DisplayShare`, from `WorldViewGraphHost.DisplayAreaOf`), and two
services that both have work divide the budget by their weights
(`SdfGpuWorkBudget.Takes`, printed as `work-turns` on `world.residency`'s
selector line); a residency nothing read last frame takes the least weight
(`SdfGpuWorkBudget.LeastWeight`) rather than none, so a world being arrived at
still brings its meshes and light before its glass shows. The minimum is a
seen volume's first light: while a level the views read has not published,
the volume's jobs take the owed allowance an arrival's bake work takes
(`SdfMeshIrradiance.OwesFirstLight`, `SdfGpuWorkBudget.OwedAllowance`).
A glass's world measures only the levels the presenting world's nested-view
table keeps at the glass's presented height (`render.nested`'s
`indirectLevels`, `SdfFrame.IndirectLevels`); the standard keeps the storey and
far levels up to half the display's height and the far level alone below a
postcard. Still to build: the shared probe pool, the levels moved into authored
data, staleness weighed within the ordinary share, and per-residency age
counters.

**The exposure meters the image; the camera sample stays a volume read.** The
exposure meters each view's own image (the
[rendering decision](../decisions/rendering.md#lights-screens-and-the-camera-are-photometric)),
read back asynchronously so nothing on the frame path waits on it. The
volume's camera sample (`SdfMeshIrradiance.TrySampleCamera`) is no meter: it
gives the air light and lights a surface no probe answers, read on the GPU as
the eight-probe gather or, where the camera stands in an enclosure with no
lattice probe, one camera "probe" traced and relit like any other.

**The lockstep capture drain.** A lockstep frame traces the window to the end
before rendering so a still never depends on how far an asynchronous trace
had got (`SdfMeshIrradiance.Stage`, the `drain` path). Decision: the same
contract holds on the GPU. A tick-locked frame dispatches trace and relight
until the pending queue is empty, fenced before the frame's views record, and
the budget does not apply to it. Captures stay history-independent at the cost
of the capture's own frame, which is the cost lockstep already pays for bakes.

**Device measurements.** No one has timed the trace, the cascades or the views
pass on the RTX 2060 or the Steam Deck, and no figure for either device is
scaled from another GPU. Under the owner's device order, the RTX 4070 meets
both bars first on both backends and topologies; once it does, the RTX 2060
and Steam Deck legs run at High (the bar's quality). Until then each weaker
device's authored table runs on the RTX 4070 to verify what it selects and
submits.

## Rejected and watched

Each verdict names what would change it.

| Technique | Verdict | Reason | What would change it |
|---|---|---|---|
| ReSTIR GI, and its descendants (world-space reservoirs, Area ReSTIR, final gather, splatting, ReSTIR PT) | Reject | 8.9 ms against 8.0 ms for plain 1-spp path tracing on an RTX 3090 at 1080p (Ouyang, Liu, Kettunen, Pharr and Pantaleoni, "ReSTIR GI", HPG 2021, <https://research.nvidia.com/publication/2021-06_restir-gi-path-resampling-real-time-path-tracing>); per pixel and per view, so portals and screens multiply it; already set aside in the [decision register](../decisions/rendering.md#global-illumination) | A Deck-class GPU that path traces a frame in a few milliseconds |
| ReSTIR DI and stochastic lightcuts | Watch | Sample reuse for thousands of emitters; the debug world has 68 and deterministic lists remove most waste first (decision 5) | Authored scenes with thousands of important emitters where lists no longer bound the loop |
| Voxel cone tracing | Reject | Leaks through thin architecture; Lumen names it "leaky" and Wright 2015 says it "can't represent walls" at affordable voxel sizes (<https://advances.realtimerendering.com/s2015/DynamicOcclusionWithSignedDistanceFields.pdf>); Puck's walls are about 10 cm | Nothing: the failure is in the representation |
| Baked time-of-day keyframes | Reject | AC4 baked eight hours of bounce on PS3 (Wronski, GDC 2014, <https://bartwronski.com/wp-content/uploads/2014/03/ac4_gdc.pdf>); AC Shadows' table extrapolates keyframes to about 1.9 TB; an authored clock, live edits and point lights all break a keyframe bake | Nothing while worlds are edited live |
| Lightmaps and per-prototype bakes | Reject | Static, keyed per placement, and a bounce depends on a placement's neighbours; recorded in the register | Nothing while lights key on clocks |
| Neural caches (NRC, NIRC, the neural irradiance volume, Neural Light Grid, TransGI, vertex-feature GI) | Reject for now | Per-scene training or precomputation restricts edits; the neural irradiance volume (Coomans et al., <https://arxiv.org/abs/2602.12949>) is precomputed by its own abstract; the Deck has no tensor hardware and the brief forbids a quality tier | An online-trained cache measured under the bar on the Deck with live edits |
| Gaussian-splat light transport (PRTGS, GSCache, Gaussian Light Transport) | Reject | A trained scene representation (Gaussian Light Transport trains 7 to 12 minutes per scene on an RTX 4080 SUPER, <https://arxiv.org/abs/2609.11430>) over meshes that are already the truth | Nothing while creations are SDF programs |
| Dense 3D radiance cascades | Watch | World-space cascade 0 is "practically equivalent to voxelizing the entire scene" (Sannikov, <https://github.com/Raikiri/RadianceCascadesPaper>); screen space is per view | A sparse form measured on the Deck |
| Split radiance cascades and surface-sparse caches (GIBS, SHaRC) | Watch | Split RC (Freeman, Sannikov and Pitt-Francis, <https://arxiv.org/abs/2607.20384>) gives no performance numbers; surface-spawned caches populate from what views see, so a teleport or a portal reveals cold surfaces, the problem the packet already records; GIBS' 3.2 ms baseline is a Series X flythrough at 1440p and College Football freezes GI during 60 Hz play (Apers, SIGGRAPH 2024 Advances, <https://advances.realtimerendering.com/s2024/content/EA-GIBS2/Apers_Advances-s2024_Shipping-Dynamic-GI.pdf>) | A sparse cache whose cold-area behaviour is measured no worse than the probe volume's coarser levels |
| Lumen's per-pixel final gather and surface cache, idTech 8's screen gather | Reject at the bar | High is 2.8 to 4.3 ms on a PS5 at 1080p before anything else; idTech 8 gathers, denoises and upsamples per pixel (Sousa, SIGGRAPH 2025 Advances, <https://advances.realtimerendering.com/s2025/content/SOUSA_SIGGRAPH_2025_Final.pdf>) | A per-pixel gather measured under the Deck's budget beside the rest of the frame |
| Replacing patch visibility with DDGI Chebyshev moments | Reject | Puck measured radial-distance interpolation at 48 % visibility for a coplanar receiver and removed it; the plinth artefacts' cause is a hypothesis, not a texel-switching fact | A measurement showing the patch rule, not the sampling, causes a named artefact |
| Moment, variance and exponential shadow maps | Reject as default | Light bleeding through stacked storeys; three cascades already take 96 MiB today (144 MiB with per-body depth layers) | A constrained use that accepts the bound's error |
| Virtual shadow maps | Watch | Continuous sun motion and thousands of bending blades invalidate cached pages every frame; Puck lacks the page machinery | A measured coverage or detail limit the cascades cannot reach |
| Fourier-compressed horizon maps | Watch | Height-field only (Fritsch et al., HPG 2025, <https://www.cg.cs.tu-bs.de/upload/publications/fritsch2025fchorizonmaps/fritsch2025fchorizonmaps.pdf>): an author's arch, cave or overhang breaks it; 0.1 to 0.55 ms on an RTX 4090 and 1.3 to 8.8 ms on a notebook RTX 2050 | The terrain cascade's measured cost on the Deck failing the bar for far shadows alone |
| SH exponentials for glossy reflection | Watch | 33 coefficients per channel (Silvennoinen et al., HPG 2025, <https://arisilvennoinen.github.io/Publications/HPG_2025_CRC.pdf>), so a new probe payload; poor below roughness 0.25; the painterly direction makes glossy indirect low priority, though an author may want polished floors | The hit-and-relight volume landed, and an authored world that needs glossy interreflection |
| Per-pixel SDF cone shadows and sky cones | Reject | A field march in the frame, which the frame does not do; 3.4 ms on a Radeon 7870 at 1080p for sky cones alone | Nothing while the frame rasterizes baked meshes |
| Top-K per-tile local light selection | Reject | Silently dropping the fourth relevant light is the author-visible cliff the criterion forbids | Nothing; decision 5 reports overload instead |
| Path guiding of probe rays (vMF lobes, portal MIS) | Prototype later | Fits the stairwell enclosure finding; convergence claims are unmeasured | The hit buffer landed, so a guided ray has somewhere to be cached |
| Screen-space indirect light | Reject | Per view and view-dependent; recorded in the register | Nothing |

## Background GPU work and headroom

Before raising the lighting worker's throughput, background GPU work (`SdfGpuWork`,
shared across residencies through `SdfGpuWorkBudget`) must stop stalling frames
or waiting a frame per round trip.

**What the work waits on.** Background work is everything the GPU does that is
not drawing the frame: relighting probes, tracing their rays, sampling the field
for the bake and uploading finished meshes. The budget is a share of GPU time
counted against the wall clock (`SdfGpuWork.GpuShare`), over a fixed number of
job slots (`SdfGpuWork.SlotCount`). The GPU is not short of time; the work
waits on round trips.

- *The lighting.* One windowed run measured each probe job at about 25 ms out
  and back (step 5 names the cause since removed, a job answering from the
  frame's last signal). The service reported an occupancy of 0.05, which is GPU time
  divided by the time some job was in flight (`SdfGpuWork.Describe`): the GPU
  worked a twentieth of the time a job was out. It is not a share of the
  allowance, and nothing reports how much of the allowance is used. After a
  switch to night in that run the finest probe level's light was up to 44 s
  old, from three causes the run does not divide: the round trips; stale
  relights waiting behind owed traces (a second switch with no traces owed
  relit the finest level in about 90 ticks); and the lighting's version moving
  about 1,380 ticks after the day was set, 46 s before the age even starts to
  count. With step 3's built parts in, a switch moved the version on the first
  read in every run of the
  [lighting-switch convergence checks](../../.claude/skills/rendering/references/mesh-gi.md#lighting-switch-convergence-checks),
  so that delay did not recur. The relight's and the trace's allowances are
  granted for each second of wall time (step 3 below), so a world presenting
  20 frames a second is granted the same lighting a second as one presenting
  60.
- *The bake.* A group samples up to five lattices that narrow in on the
  surface, then its final slabs, then up to eleven rounds that search for the
  exact edge crossings, then gradients and materials, each read back to the
  CPU: about twenty round trips. A quiet cold bake has medians of 60.6 s on
  Direct3D 12 and 64.7 s on Vulkan with about 4 s of GPU work in a busy span
  of 68 s.
- *Constants that remain fixed.* Every client now cuts its budgeted jobs to
  the live frame allowance (steps 1 and 2 below). Mesh uploads still run at
  one byte rate (`SdfGpuWork.UploadBytesPerSecond`) that assumes a 60 Hz
  display, and a bake lattice, at most `SdfMeshPathBake.LatticeTaps`
  evaluations (about 4 ms at the bake's prior rate), is never split, so a
  batch whose first lattice is costlier than a small allowance still runs
  long; `world.residency`'s `over-allowance` counts those jobs.

**Why the headroom rule comes first.** Raising the share or the slot count does
nothing while the work waits on round trips. Shipped systems hold update cost
fixed and let lighting latency vary: Lumen describes "fixed update cost,
variable lighting latency", and Frostbite's probe system holds a fixed budget
of 100,000 rays and about 6,000 probes a frame. The main view alone uses about
25 ms of GPU time at the bar's size, more than the whole period
([the budget](rendering-research.md#the-bars-and-the-budget)), and the
background allowance at 60 Hz is another 4.2 ms. The lighting uses little of
that allowance today, so the first fix that makes the worker effective would
add all of it to a frame that is already over. The 4.2 ms is an allowance, not
a measurement: this is a scenario, and it is the one the next fix creates.

Six steps run in order:

1. **Make the budget follow the GPU time the frame leaves free.** Built: each
   frame `WorldBackgroundAllowance` hands the one budget (`SdfGpuWorkBudget`)
   the display's real period (`FrameContext.DisplayHertz`) and the GPU time
   every node of the render graph took, nested views among them, read from the
   frame-load source over every node (`WorldFrameLoadSource` with no view, each
   node's latest submission rather than a 32-frame mean). Background jobs are
   timed apart and never in the reading. Every `SdfGpuWorkBudget.FollowFrames`
   frames the share moves to what the mean leaves of the period short of
   `SdfGpuWorkBudget.HeadroomShare`, between `SdfGpuWorkBudget.LeastFrameSeconds`
   a period and `SdfGpuWork.GpuShare`: down at once, up by at most
   `SdfGpuWorkBudget.RiseShare` a step and not on the step after a fall. The
   timestamp demand stands whenever a display period is known (a pool and a
   readback a frame slot on every node); a World with no display period or no
   timed submission keeps the fixed share and names why (`follows:` on
   `world.residency`'s `work-budget` line, beside the share, one frame's
   allowance, the frame's GPU time and the GPU time granted against the time
   charged). Lockstep and held frames never meter their work, so a drain's
   result stays a function of its inputs.
   - *Deadline admission.* Built: the jobs queued ahead of one frame together
     fit one frame allowance. The service's one take hands every client what
     is left of the live allowance after the charged jobs still in flight on
     the presentation's queue (`SdfGpuWorkBudget.AllowanceOf`; no bound for a held
     frame), and while work is queued a client takes no job priced past it, so
     neither `SdfGpuWork.SlotCount` allowance-sized jobs nor credit banked over
     a long gap (`SdfGpuWorkBudget.MostBankSeconds` times the share) queue more
     than an allowance ahead of a frame. A job with nothing queued runs
     whatever its price. The probe coordinator still cuts queries to the whole
     allowance, so a query waits for an empty queue; cutting it to what is left
     needs the coordinator to know the queue when it cuts.
   - *Controller and overload order.* Dynamic resolution reads the root views'
     passes, which background work never inflates, and scales the render grid
     only when they alone overrun the period, while the background share falls
     as soon as they leave less than its share: optional background work gives
     way first, by construction. Still to build: under sustained overload,
     nested-view rates yield next (never below the author's floor), then
     lighting service. Consumer minimums
     ([streaming and memory](streaming-and-memory.md#decision-4-a-residency-is-staged-by-its-demand-not-by-a-render))
     stay served in one-frame jobs.
   - *Promise and reporting.* Lighting's authored work-per-second promise holds
     only while the frame's own passes leave its share free; past that limit
     the frame holds its bar and lighting age grows past its bound. Frames
     missed behind background work are counted apart from overdue lighting
     (`world.residency`'s `frames-behind`, `SdfGpuWorkBudget.FramesMissedBehind`),
     an upper bound read from the jobs' and frames' spans on the queue's clock;
     the reading on the benchmark route is owed. Admitting jobs against the
     deadline bounds what background work can cost a frame; it does not make
     the promise hold when the frame is over for long.
2. **Cut bake jobs to the live frame allowance.** Built: a budgeted batch's
   evaluations past its first lattice are cut to the allowance at the bake's
   measured rate (`SdfMeshBakeGpuWork`), which follows the mean
   (`SdfGpuWorkRate`'s `rise`), since a batch mixes groups whose evaluations
   differ in cost and a rate that rose to the costliest batch at once shrank
   every batch after it. A held frame's batch takes up to
   `SdfMeshPathBake.BatchTaps`. Still open: a lattice is never split, and the
   cold bake in play needs measuring against the bar
   ([bake proportional to change](open-items.md#bake-proportional-to-change)).
3. **Finish multi-job probe pipelining and per-frame grants.** Built: multiple
   probe jobs in flight with one commit per batch (`SdfMeshProbeGpuWork.Run`,
   `SdfMeshProbeGpuWork.RecordCommit`), stale relights sharing the worker with
   owed traces (`SdfMeshIrradiance.Work`), and a grant for each presented
   frame: `render.indirect.budget`'s (or the running quality table's
   `indirectBudget`) `relightPerFrame` and `tracePerFrame` bank once a frame the
   volume stages, held to `SdfMeshIrradiance.MostBankedFrames` frames of
   credit, so a frame that runs long owes no more probe work than a short one
   and a slow device does less lighting work a second. The GPU time those
   probes cost is charged by their jobs' timestamps to the one wall-clock
   budget, and a frame over its period (`SdfGpuWorkBudget.FrameOver`) holds
   even an unqueued job to the frame allowance until the share has paid for it
   (`SdfGpuWorkBudget.Hold`), so a higher count lowers the
   lighting's age only while the frame leaves time free. A drain ignores the
   grant, so its result does not depend on the play rate. Still to do: mesh
   upload time measured per device beside the bytes-per-second staging cap, and
   measuring root frame time, lighting age and lighting-version delay together
   on both backends at a fixed and a low frame rate; the version delay is
   reported on its own because the age cannot see it. Several jobs in flight
   change the order in which bounces are computed inside a batch, so every
   probe of a batch reads the bounce from before the batch and a drain
   publishes at stated points: batch size cannot change what a drain converges
   to. One point of admission makes a query that replaces shared probe state
   run alone (`SdfMeshProbeGpuWork.TryTake`), and a batch's last job commits
   only the rows its batch wrote.
4. **Move the probe work list onto the GPU** (lands before moving lighting's
   CPU loops onto the job pool, since both rewrite `SdfMeshIrradiance`). Keep
   probe state on device, pick next work deterministically, and read only
   counters on CPU. Cap each dispatch by *executed* light-hit tests rather than
   probe count (because `SdfMeshIrradiance.RelightRays` tests light range at
   the probe origin and misses hits on distant multi-lit walls: a probe outside
   every light's range can see a wall many lights reach, so its predicted cost
   is zero and its real cost large), suspending and resuming a probe that hits
   the cap. A recorded chain cannot be cut short, so the cap must be an input
   to the kernel: one dispatch of 4.4 million rays once ran past the device's
   timeout. Schedulers that keep their work list on the GPU have precedent;
   one that also drains to the same result whatever its history has none we
   found, and that half is Puck's own to prove.
5. **Hand background jobs to the GPU queue before frame passes record** (after
   the job system's C0 wait rework). Two parts exist: each residency's
   background work is pumped before any residency prepares, and a frame the
   graph withholds hands each job to the queue before it waits
   (`SdfGpuWork.Drive`). Extend that hand-off to ordinary frames in
   `SdfGpuWork.Pump`, so a wait names the exact queue position it needs and a
   job is answered at the next step, not the next frame; keep it only if
   measured to beat its extra submit cost. One cause of the slow answer is
   built away: a job submitted inside the frame's open batch used to answer
   from the frame's last signal, about 25 ms after its 1 to 2 ms of GPU work,
   and now the first frame submission deferred behind a run of background jobs
   hands the batch to the queue first (`GpuSubmissionBatch.Admit`), so the
   run's last job owns that hand-over's signal. `gpu.queue.jobs.late` counts
   job fences still answered behind frame work and should read zero; this
   step is judged against that reading and the probe round trip it leaves.
6. **Price every consumer in one unit**: nested views, uploads, lighting and
   bake. The scheduler already honours a ceiling on passes times pixels that
   no caller sets (`RenderGraphFrame.PassPixelBudget`), and that is where
   nested views join. This is a design round, held until the background share
   is actively used. Across residencies the budget is already ordered by what
   the player sees (`SdfGpuWorkBudget.Takes`, weighted by display share) and
   work whose absence a shown view displays (a hole, a waiting mesh, a volume's
   first light) takes the owed allowance; the round prices the frame's own
   nested views in the same unit.

Steps 4 and 5 sit in two classes of defect only review catches: a GPU job whose
inputs are freed while it runs, and a GPU read at a position another GPU job
wrote ([hazards](../development/hazards.md#silent-invariants)). Each gets an
invariant row and an adversarial review. One Vulkan device loss in this area is
still unexplained.

*Headroom and background checks:* on the night-switch reproduction (with and
without owed traces) and moving route chapters at full and half display rate,
with full slots and mid-batch lighting changes on both backends, every probe
level's oldest age stays within its bound without slowing the root frame beyond
its spread (using a forced GPU load if no stop is GPU-bound); an age that falls
only by taking frame time shows the claim false, and the lawn stops being
metered as day. Dynamic resolution (`world.render-scale auto`) and the headroom
rule do not fight; under full-period overload, missed frames behind background
jobs and overdue lighting are counted apart; no background job in play exceeds
one frame's allowance; a small-grant probe facing a multi-lit wall stays inside
its executed-test cap and resumes next dispatch, with the most work in one
dispatch reported; drains at different batch sizes match; early hand-off
answers a step sooner, with the extra submit's cost reported beside it and
validation clean on both backends.

**Options not taken, or held.** Raising the share or the slot count fixes
nothing while the round trips remain. The bake's sampling in one round trip per
stage is a candidate ranked by the cold bake's critical path
([bake proportional to change](open-items.md#bake-proportional-to-change)). A
second GPU queue for background compute would stop long jobs delaying the frame
behind them. A device without the capability never vetoes it, so it is a lane
of the rendering plan's second wave, judged by what it gives the RTX 4070, with
the frame's queue as the path where a device's table leaves it off
([the background queue](rendering-research.md#the-capability-lanes-of-the-second-wave)).
Steps 1 and 2 still hold without it: they are the rule on the frame's queue,
and they bound how much a background queue may take.

**What it rests on.** One implementation: `SdfGpuWork` stays the one service,
`SdfGpuWorkBudget` the one budget and `IWorldFrameLoadSource` the frame's one
load reading. The job system's `Lighting` class supplies the policy, and waits
stay on the host's primitives through `HostWaits`. The relight and trace
allowances and the lighting age are authored (`render.indirect`'s `budget`),
and their unit becomes work per second.

**Still to find out.** How much of its allowance the background work uses
today; how the 44 s divides between its three causes; whether the bake's round
trips are on its critical path. Not checked: the shape of a group's bake and
the 4.4 million-ray timeout (a research pass's map); about 4 s of GPU work in
68 s (measured once); twenty round trips, 32 ms for a full batch (reasoned).

## Direct light: roles, the probe fallback and one cascade set

The shadow a surface receives depends on which shadow map happens to answer
and on what kind of view asks. The outcome: a light is shadowed by the cascade
that contains the point, and by the probes where no cascade does, per pixel, in
every view and on glass; a soft edge's width depends only on the light and the
gap between blocker and receiver. Build step 1 below carries what remains of
it; the metre-sized filter is [transmission](transmission.md#implementation-steps)'s
`transmit-plane`, and caster culling is [visibility](visibility.md#v5-multi-view-and-shadow-work)'s.

**What the shadows lane fixed.** Three defects were the reason for roles and
the fallback, and the first wave's `shadows` lane has built the answer to
them; its checks in build step 1 are owed to a GPU leg.

- *Past the shadow maps everything was sunlit*, and *glass and nested views got
  no shadow maps* (one flag per view decided whether cascades were read). Now
  the surface pass writes each role's cascade visibility and its coverage,
  falling to zero over a band one widest filter wide at the edge of the role's
  outermost entry, and `sdfShadowRoleAnswer` blends the probes' answer in by
  coverage, so a point past the cascades, glass and a view with no cascade all
  take the probes' record of that light.
- *The wrong light held the maps.* `WorldShadowAllocator` compared the shadow
  mode before the brightness, so an unlit moon (`illuminance: 0lx, shadow:
  auto`) could hold the second slot by day and an unlit key light the near and
  far cascades at night, and `SdfMeshShadowCascades.Fit` returned no cascades
  when slot 0 held no directional light. Now a light that emits nothing
  competes for no slot (`WorldShadowAllocator.Admitted`), the slots keep light
  identities, and `SdfLights.RoleSlot` derives the roles from the slot owners
  and what each emits in the frame: primary is the brightest slotted
  directional light (near and far cascades, and the key light), secondary the
  next (one cascade). The roles are computed on read and stored nowhere, so no
  writer can leave them stale. An incoming handoff light takes the probes'
  answer and an outgoing one eases to ambient occlusion. The noon reading that
  shows the second slot empty is part of step 1's checks.

**What remains.**

- *Softness jumps at a cascade boundary.* The first cascade that contains a
  point answers, with no blend, and the blocker search and blur are sized in
  that cascade's texels (capped at `SdfMeshShadowMaxTexels`, 14). With a
  60-degree 16:9 view a near texel is 1.86 cm and a far one 13.9 cm, so a soft
  edge is capped at 26 cm on one side of the boundary and 1.95 m on the other.
- *Soft edges are cut short.* The search offsets lie on the shadow map's plane
  (`sdfMeshShadowGap`), so 26 cm is a sideways reach: a blocker whose edge lies
  further to the side is not found, which removes the outer part of every soft
  edge wider than that. Both are [transmission](transmission.md#implementation-steps)'s
  metre-sized filter.
- The cascades are a world's, eye-centred and cached, and every view reads them
  ([the decision](../decisions/rendering.md#a-worlds-shadows-are-drawn-once-and-cost-what-moved)),
  so a camera turn moves no cascade and a program with glass shadows holds its
  three optical-depth buffers once an anchor, not once a view.

**The design, and what each part still owes.**

1. *Roles.* Built as above. What the checks must show: two lit lights that swap
   which is brighter while each keeps its slot cross without a jump in either
   direction, a light that keeps its slot through a handover still takes the
   primary role when the other goes dark, and the exchange at the horizon does
   not flicker between lights.
2. *One indexed cascade set.* Built: `SdfMeshShadowCascades` is one list of
   entries (camera, role, slope), primary near, primary far, secondary, the
   primary role's distant map, then the far exit camera; `Holds` says which
   entries a view draws and reads. Step 4's memo adds
   radii to the entries rather than starting a second set.
3. *The probes' answer where no entry covers a point.* Built as above. The
   promise is limited: past the far cascade only the coarsest probe level
   answers, 32 m apart, and an unmeasured probe answers lit (never dark, or a
   camera cut would land in darkness until its probes fill). Where a device row
   draws no distant map, and past its reach, the valley's far view is shadowed in
   32 m steps, so the fallback is judged there by presented captures from the rim against the
   sunlit answer it replaced. A residency has one set of probe windows; they
   follow the first view when the views do not fit inside a level
   (`SdfMeshIrradiance.WindowLow`), so a second view far from the first is
   answered only by the coarsest level that covers it.
4. *Eye-centred cascades as one authored set of radii*, with entries beyond
   120 m held between refreshes, after visibility's caster culling and behind a
   memo that states the radii the indexed set's entries gain and what centring
   costs in texels at each field of view. Centring stops a camera turn from ending the far cascade's
   hold. The terrain's distant map is already an eye-centred entry of its own
   (a sphere about a grid cell, the stated lag an authored light tolerance per
   device row), so the memo's radii take it as it stands. Narrow views
   get coarser texels, and a far entry needs either a refresh spread over several
   frames or a stated lag at the authored day speed. A borrowed figure does not
   carry over: Cyberpunk refreshes distant shadows "every few seconds", but the
   debug world's day lasts 24 minutes and a held far map of 480 m radius
   tolerates about 0.4 milliradians of light movement, so at that day speed it
   would need about eleven refreshes a second (reasoned).
5. *The exit layer gives way to transmission's per-body depth layers*, on that
   plan's schedule.
6. *Glass shadow storage is cleared only where glass cast into it*, with
   visibility's glass list: the tiles the previous draw of that buffer wrote,
   kept as a list with the buffer so a caster that left is still cleared; first
   use clears all. That saves the whole-buffer clear, 48 MiB at the highest
   tier, and the stored sum stays independent of draw order. The sum has a
   limit: each shadow texel stores one optical-depth total down to its first
   opaque surface, right for a receiver behind all the glass, but a receiver
   standing between two panes is charged for both when the opaque surface lies
   beyond them. A receiver in front of, between and behind two panes decides
   whether glass needs storage by depth.

**Why.** Roles give a whole cascade back by day and the moon proper coverage at
night; a shipped game runs sun and moon as one global shadow system (Cyberpunk
2077). The register already rules that a view with no cascade takes the sun's
visibility from the probe volume, and that a light is shadowed wherever it
lights
([register](../decisions/rendering.md#a-light-is-shadowed-wherever-it-lights-and-cel-shading-is-a-style-per-material)).

**Options not taken.** Cached or sparse virtual shadow maps, refitting the
projection every frame, and moment shadow maps each fail on the moving sun or
the wind ([rejected and watched](#rejected-and-watched)). Sun shadows by ray
are the answer where no map reaches on a device with ray queries
([GPU capabilities](rendering-research.md#gpu-capabilities-puck-does-not-use-yet)).

**What it rests on.** A light's softness is its authored `angularRadius`; the
widest soft edge, the blend bands and a far entry's refresh are authored values
with neutral defaults
([where each look is authored](rendering-research/step-designs.md#where-each-look-is-authored)).
Point and spot lights keep the probes' answer until step 5 builds local shadow
maps. Not checked: the cascade spheres on the view axis and the 144 of 252 MB
(a research map); the probe windows' rule and the existing pyramid's one pixel
per block (a reviewer's reading); the texel sizes and the 26 cm and 1.95 m caps
(reasoned).

## Lighting proportional to change

Any scene change relights the whole probe volume from scratch, which is why the
lighting lags and why a night scene is metered as day after a switch. A probe's
*hits* (the surfaces its rays reach) depend only on geometry and are cached;
its *cubes* (the light arriving from those hits) are recomputed from the hits
and the current lights. The cubes are reused only when two global version
numbers match (`SdfMeshIrradiance.Gpu.cs`, the candidate's `Geometry` and
`LocalVersion` against the snapshot's), so any change, a camera-driven level
swap included, re-traces every probe's shadow rays to every point light in
range. Six facts make that large:

- The scheduler's cost is a prediction, not a count: about 13,800 rays for a
  fresh debug-world probe, from every light whose range reaches the probe's
  origin times its hits (`SdfMeshIrradiance.RelightRays`); the kernel is more
  selective, and rays actually issued are counted nowhere.
- Ranges are long: a light's range is the square root of its weight times the
  brightest exposure any authored key reaches, over
  `SdfPhotometry.RangeThreshold` (`SdfPhotometry.RangeOf`). The exposure term
  is the likely reason most point lights reach every probe.
- Each residency owns a whole volume and budget (about a 72 MB table in up to
  four copies and a 90 MB hit cache at High), with a level table fixed in engine
  code.
- Every relight shades every hit of every probe, though neighbours hit the same
  walls.
- The cubes store absolute light, so a colour or brightness change is a full
  relight and a fresh convergence.
- The sky map is rendered three ways: by the GPU kernel, by a CPU render that
  decides whether a refresh is due (`SdfSkyEnvironmentRefresh.Owes`), and by a
  second CPU render on the frame thread for each lighting capture.

The outcome: a geometry change costs a trace, a change of a light's direction a
relight, a change of brightness or colour a multiplication where the light still
reaches the same places, and a level swap only the ray segments it crosses.
Build step 2 lands the sky, the per-source store and the rescale; the shared
surface entries, visibility bits, stable trace mesh and coarse-to-fine order are
[after the GPU trace](#after-the-gpu-trace); the pool is in
[the gaps](#the-gaps-the-round-named). The round trips are fixed first
([background work](#background-gpu-work-and-headroom)) so that GPU time is what
a measurement reads.

**The per-source store.** The relight stores, for each source group, the light
per unit of that group, and publication combines the groups with the frame's
current source values.

- *A source group* is a set of sources that always scale together: each
  shadow-casting directional light, the sky under one gain, each local light on
  its own unless an author binds several to one value, every emitting material
  together, and each screen or opening that lights a room.
- *The bounce is stored per group.* The relight adds a material's own emission
  to a hit and reflects the whole previous volume off it; running that once per
  group would count emission once per group and mix every group's bounce into
  every other, so each group's next bounce reads that group's own previous
  volume and emission is a group of its own. That history is part of the memory
  price.
- *Multiply-only edits* keep a group's reach, attenuation, direction and
  materials: a directional light's brightness and colour and a local light's
  colour today. The sky's gain qualifies once the rounding grid
  (`SdfIndirectLightingGrid`) moves from the inputs to the combined result,
  because scaling a stored result equals rounding the scaled input only for a
  power-of-two gain; the reference drain changes in the same change. A local
  light's brightness qualifies only if its range is sized from the brightest
  value its authored brightness reaches, which changes its falloff at every
  lower brightness for direct and bounced light alike and amends the register's
  rule that a light "ends at a range derived from its intensity"; it is adopted,
  if at all, for both at once, after the ray and probe cost is measured.
- *Everything else invalidates* what it reaches: a moved light, a changed cone
  or range, a sky whose shape changed, a change of material or geometry.
- *Memory is priced first*: groups times probes times one cube and its bounce
  history, at the debug world's count of lights and screens and at a zone's.

At fixed geometry, materials and reach, the light a surface receives is linear
in a source's radiance, so the edit is exact, not an estimate. Its limits: two
lamps in one group cannot scale apart, a change of the sky's shape is not a
scale, and a source at zero must still be relit per unit or switching it on has
nothing to multiply. Nothing we fetched stores light per source in a dynamic
probe volume, so the check is Puck's own: after an edit the cubes equal a
from-scratch drain within `ConvergedShare` in the seven cases build step 2
lists. The nearest shipped relative is cruder: Assassin's Creed Shadows
rescales every probe it did not update when the sun or sky changes, from one
stored value per probe, keeping local light out of the rescale. That is the
rescale of unreached probes, a bridge a relight always replaces, because the
same kind of estimate measured about half the true light for a window low in a
wall.

**The one GPU sky.** A refresh decision lags a readback, and identical packed
inputs can be bound to new pixels, so the sky's identity carries each acquired
picture's identity, sampling transform and revision beside the packed
procedural inputs, and a picture that is cold or absent stays an explicit state.

**Still to find out.** The rays actually issued; how many probe hits share one
surface entry; the memory of per-source cubes with their bounce history;
whether floors pass the lighting check; whether moving the rounding grid
changes anything an author relied on. Indoors, a coarse-first start may look
poor. Not checked: the 13,800-ray charge (measured once); the 72 MB, 90 MB,
179 MB and 135 MB figures (reasoned); Assassin's Creed Shadows' rescale and
its halved probe budget at 60 frames a second (a reviewer read the deck).

## The build order

Each step is one lane with a check on the running debug world at `world.quality
high`, on the RTX 4070 (both backends) across the authored day cycle, with RTX
2060 and Steam Deck legs following once the RTX 4070 meets both bars. Stills
use the tolerant comparison from the
[job-system memo](engine-job-system.md#6-build-order): two captures of the
earlier build measure the run-to-run noise, and a difference beyond it is
flagged by region and looked at, never a stop. The frame bar is read by
[the one recipe](streaming-and-memory.md#how-the-bars-are-read), with counted
work (`world.counters gpu`, `world.counters sdf.mesh-gi`) beside it. No step
requires a byte-identical still.

Steps 1 to 3 are built but for what the table names: direct light is shadowed
wherever it lights, a probe is a hit buffer and a relight, change sets retrace
only the probes a geometry change reaches, and the trace, the relight, the work
selection and the camera meter run on the GPU through the background GPU
service (`SdfMeshProbeGpuWork` on `SdfGpuWork`;
[mesh GI reference](../../.claude/skills/rendering/references/mesh-gi.md)). The
CPU trace and relight stay as the diagnostic and query reference (the device
law, `puck mesh scene`, `world.probe`), off the frame's path; they are not
deleted. Where the job system meets these steps, the GI step keeps its unit
and the job system supplies the policy
([its build order](engine-job-system.md#6-build-order)).

| Step | Lane | Lands | Check on `worlds/debug` |
|---|---|---|---|
| 0. Background headroom and device leg | rendering | [Background GPU work and headroom](#background-gpu-work-and-headroom) steps 1 and 3–6 on the RTX 4070 (both backends); the RTX 2060 and Steam Deck High legs run once the RTX 4070 meets both bars | The headroom and background checks above on the RTX 4070; later, Deck `[sdf.mesh-gi] done … seconds=` at High and the frame bar at `debug.go overlook` alongside an unloaded resolve-pass repeat |
| 1. Direct light's remainder | rendering | [Direct light: roles, the probe fallback and one cascade set](#direct-light-roles-the-probe-fallback-and-one-cascade-set). Built: admission by contribution, roles derived from the slots (`SdfLights.RoleSlot`), the indexed cascade set (`SdfMeshShadowCascades`) and the probes' answer wherever no entry covers a point (`sdfShadowRoleAnswer`); the checks below are what that build still owes a GPU leg. Remaining: after [visibility's caster culling](visibility.md#v5-multi-view-and-shadow-work), the eye-centred cascade memo, whose set includes [the terrain pass](terrain-pass.md#distance)'s far shadow map as one entry of the primary role (stretching the far cascade over the valley costs more than an order of magnitude in draws, vertices and GPU time); the glass shadow clear by tile; scale-following shading lengths (cascade bias, AO and shadow tap radii, `NormalBias`). The metre-sized shadow filter and per-body depth layers are [transmission](transmission.md#implementation-steps)'s | At High: noon `world.counters gpu pass sdf.world$shadow-second` shows zero draws; through a continuous sun/moon exchange forward and reversed no frame loses cascades; two lit lights swapping brightness fade a far receiver without a jump and the brighter light ends with primary cascades; shadow beyond 40 m holds while slot 0 empties; both exchange ends match cold jumps; no hard line at 120 m; post-cut unfilled probes read lit and fade in; `valley-rim` captures read no worse than sunlit fallback; distant second views get an answer; glass, cascades-off views and beyond-cascade points each checked independently; receiver in front of, between and behind two glass panes checks the glass optical-depth sum limit; quarter-size Moth back-plate lines gone; once the terrain's far shadow map lands, `valley-rim` shows ridge shadows on the far walls and the frame bar holds there under a moving sun; the frame bar again with the sun moving; dawn leg on the `day` row with the sun near the horizon |
| 2. Lighting proportional to change | rendering | [Lighting proportional to change](#lighting-proportional-to-change), after background steps 1 and 3: **one GPU sky**, deleting the two CPU sky renderers (`SdfSkyEnvironmentRefresh.Owes` and the frame-thread capture render); **the per-source store**, combined at publication, with memory priced first (a world can bind up to `SdfProgramBuilder.MaxScreenSurfaces` screens), the rounding grid moved to the combined result with the reference drain, and a local light's brightness multiply-only only after the measured register change; **the rescale of unreached probes** by the mean sun and sky change while a non-multiply edit (a moving sun) relights the grid, a bridge a relight replaces (about 0.49 of the true light for a window low in a wall). Also the lockstep relight under a moving sun, which relights the whole window every tick ([open items](open-items.md#lighting-and-gi)) | A panoramic sky picture moving under unchanged parameters refreshes the sky; an unchanged procedural sky under camera motion does not. Per-source storage matches a from-scratch drain within `ConvergedShare` with zero probes relit across seven cases: colour-only directional change, one of two lamps changed, light from zero to on, receiver at old range boundary of a brightened lamp, non-power-of-two sky gain with a rounding grid, live screen changing its picture, and two coloured lamps plus an emitter and bounce-only wall with one lamp turned off after bounces. Day cycle at `debug.go gallery` and `overlook`: indirect light follows the sun with the age under its authored bound and no `[sdf.mesh-gi] behind`; continuous `world.exposure` across the atrium's arch; the frame bar across the cycle; a lockstep still of one tick repeats across two Worlds within the noise |
| 3. The GPU trace's acceptance | rendering, GPU leg on both backends | What [the GPU trace still owes](#after-the-gpu-trace) (shared surface lighting, geometry visibility bits, stable trace mesh, coarse-to-fine check, slot-by-slot window publish, ray query, and field-tracing comparison); moving bodies casting into the volume remains separate future work | Rays per second from `world.counters sdf.mesh-gi` against trace dispatch GPU time; moving-sun lockstep drain split into kernel time, round trips and host work; thin-wall stills (teal corner, stairwell flight B) compared tolerantly; executed shadow queries, hit-light pairs tested, distinct live pairs and max dispatch work reported; when bodies cast, a body posed under a lamp darkens the floor in `world.debug-view indirect` |
| 4. Bitmask AO and bent normal | rendering | The bitmask march over the depth pyramid; the bent normal in the visibility record and in the gather | `meadow.puck` blades without dark halos and thin crevices closed in `world.debug-view ao`; `world.gpu-timing` of `surface` against the build before it; stills at the gather stops compared tolerantly |
| 5. Lights | rendering | The list measurement; LTC for screens, portal openings and authored rectangles; **screens join the one light selection** (range from luminance and area, visibility from probes, waking an out-of-view screen that turns bright as a consumer minimum, respecting the 32-bit mask limit); local shadow residency with overload named | The device row with every screen on: specular on the floor in front of each screen and screen glow blocked by walls; `world.counters gpu` views texels at 68 fixtures and at a scratch world with 680; a scratch world past shadow residency names deferred lights with no flicker; the frame bar at `debug.go devices` |
| 6. Openings, area emitters and shared probe pool | rendering | Static emitting faces as area emitters sharing step 2's emitter store (fixing the hall's ceiling-strip blotches; resolve emitter shadowing as part of design); **light carried across an opening** (garden painting prototype: pre-exposure average vs directional samples, single-boundary limit, disclosure-gated via [disclosure](disclosure.md#presentation-and-derived-product-disclosure)); **one `Lighting` budget and one shared probe pool across residencies** (minimums first, pixel share second, open-air coarse update rate, slot-by-slot publication, authored probe level table in `worlds/standard/`, disclosure-safe volume sharing) | Hall floor under ceiling strips free of blotches; facing away from the garden painting, its sunset lights the floor, a moved cabinet shadows it, raising exposure never brightens it (incident light read before exposure, the displayed floor separately), directional samples kept only if beating average against full reference at far under a second view's cost, and shedding-disclosure check passes; nested world's screen reports its own age while an overloaded nested world leaves the main world's lighting age unchanged |
| 7. C10, light across the valley | the composition lane | The seam check the [composition plan](debug-world-composition.md#the-order) names, now over the cycle | A walk from `debug.go gallery` to the pool across a full day: no dark interval, no clipped rock, the rim's shadow on the floor, coloured shadows under the trees from authored style, `world.exposure` continuous |

**Engine gaps in this order.** Two gaps from the
[rendering plan's list](rendering-research.md#engine-gaps-filled-first) touch
these steps. Gap 3, that nothing casts a shadow past 120 m, is step 1's terrain
cascade, whose one home is the terrain pass's far-shadow step, and that step
builds on the shadow roles of the rendering plan's decision 6
(`SdfLights.RoleSlot`): the distant map is built as the indexed cascade set's
fourth entry of the primary role (`SdfMeshShadowCascades.DistantEntry`), and a
point past it takes the probes' answer (`sdfShadowRoleAnswer`).
Gap 12, that a frame holds at most 128 lights (`SdfLights.MaxLights`), blocks no
place today, so it keeps its place in step 5: step 5's list measurement runs
as soon as a place authors more lights than the cap.

Steps 4 and 5 share no files with the others and run beside them; step 6 waits
on steps 2 and 5; step 7 on the terrain pass's steps 1 and 7 and on step 6. What
the lighting demands and keeps resident, and which mesh tier it traces, is the
streaming plan's
[decision 9](streaming-and-memory.md#decision-9-lighting-reads-the-same-demand-and-pays-on-demand).
Cel-specific checks wait with [cel shading](#cel-shading).

The day cycle is standard data (`dayCycle` in `worlds/standard/skies.puck`,
expanded by `worlds/debug/day.puck`); writing `day` moves the time and its
advance holds the sun (`worlds/debug/debug.md`, "Time of day"). The hold is
`world.row.set`, which no authored command may run; a bindable hold needs a
rule effect that writes a row's advance.

## After the GPU trace

What the GPU trace opened, in the order the evidence favours. Each is a step of
this plan; none is scheduled by another page.

**The trace's own acceptance.** Live selection now compares a probe's last
light inputs with the current sun and the parts of the sky its rays saw.
`SdfMeshIrradiance.OwesRelight` spreads small changes across cell phases before
their age deadlines. An exact drain still starts bounce receivers from zero,
and later passes visit only those receivers. Read the moving-day backlog and
relight counts at the gallery and overlook, then compare dawn and dusk in the
valley. The moving-sun drain still needs a reading. GPU execution time alone is not the drain's cost: measure job and fence
round trips and the host's placement and selection beside kernel time before
choosing the next cut. The outdoor relight's difference from the CPU reference
near foliage needs attribution beyond the device law's transformed-box corpus.
Image comparisons keep the fixed day, pinned exposure and explicit unobstructed
cameras; a blocked or exposure-mismatched still is no parity reference. The
turn retrace of the window anchored ahead of the camera
(`SdfMeshIrradiance.WindowAnchor`) owes a quiet-window reading.

**Shared hit lighting and surface-to-light visibility bits.** Each relight sweep
shades every hit of the window, and each shading is a shadow walk plus a
sixteen-slot gather, though neighbouring probes see the same walls. This builds
after per-source-group storage (step 2) so entries are built once per source
group. The step lights each surface sample once and lets every probe ray read
it: the window's hits are deduplicated into surface entries keyed from exact
cached hits by position cell (a fraction of the level's spacing, finer along
the normal than across it, so a shelf over a floor stays two entries), facing,
material, level, and the surface's instance identity (defined against
transmission's three-word hit, sizing the hit cache at ~135 MB); an entry's
representative is its first member in (probe, texel) order, an exact hit, so
its light is a function of that one hit whatever order a GPU hash table fills
in; a relight shades entries and fills each probe's cubes by lookup, and a
table that fills falls back to per-hit shading and counts it. A cold arrival
exercises the miss path, so time until a newly visited place is lit correctly
is part of the check.

On top of shared surface entries (avoiding ~179 MB of per-texel storage), store
which lights reach each entry as geometry: one bit per light per entry, made
stale only when a changed bounding box intersects its ray segment. Size light
reach from the brightest exposure any authored key reaches (`SdfPhotometry.RangeOf`
and `SdfPhotometry.RangeThreshold`, a fixed value rather than momentary
exposure), and size the store by live distinct (surface, light) pairs under
motion and eviction rather than raw query traffic.

- *Invariant.* A probe's relit cubes are a function of its hits' entries'
  light, and an entry's light is a function of its representative hit and the
  lighting. Enforced by the relight kernel's input: it receives entry indices
  beside the hit words, written by the trace's compaction stage alone, and its
  shading function takes an entry, so no GPU path shades a hit outside its
  entry. It stops at the quantization: an entry's light stands for its members
  within the cell, bounded by measured error against the CPU's exact per-hit
  relight, not by proof.
- *Steps and checks.* (0) A measurement that touches no kernel: trace a High
  window on the CPU over the stored debug-world meshes (`puck mesh scene` or a
  scratch harness), count unique keys per level at candidate cells, and relight
  the four-storey hall harness both ways for per-probe cube error. Adopt if the
  finest level folds at least four to one with cube error inside
  `ConvergedShare` at the far-field fixed receivers. (1) Entries, lookup and
  surface-to-light visibility bits on the GPU behind the existing relight's
  output: the gather device law's slots and weights unchanged, per-probe cube
  error against the CPU oracle within step 0's bound, relit probes per GPU
  millisecond before and after, and cold-arrival convergence timed. (2) Delete
  per-hit GPU shading: the lockstep still at `debug.go gallery` repeats across
  two Worlds within the gather's residue, and a full relight's seconds under a
  moving sun are read, split by stage.

**Stable trace mesh and coarse-to-fine convergence.** Give lighting its own
demand class chosen by the spacing of the finest probe level reaching each
group (coordinating with
[streaming step 9](streaming-and-memory.md#step-9-lighting-on-the-same-demand)),
so camera motion and view-tier swaps never rebuild the trace scene. A group's
floor qualifies as its trace mesh only when a bake-time lighting check confirms
its recorded field deviation ([virtual geometry](virtual-geometry.md)) is small
against that probe spacing and it has lost no part, closed no hole and joined
no two surfaces (groups failing the check keep a finer trace mesh; the bake
reports passed, failed and unjudged counts). Built: the scene traces each
static group's floor (`SdfMeshDraw.Trace`), and `SdfMeshTraceJudge` holds a
kept floor to the group's first finer product at a quarter of the finest
probe spacing both ways, a declined floor's group tracing what it draws;
`world.residency`'s floor line reports the counts. Owed: the finer trace mesh a
declined group keeps, and a check against the field rather than a finer tier. Passing the floor's coverage test
does not qualify it: a floor can cover a wall and still erase a recess, or
join two surfaces on either side of a probe, and a group near a fine probe
level may need a third mesh. A check that only asks whether the light stays the
same as the eye moves cannot catch a mesh that leaks the same amount
everywhere. Check by comparing traced light against full-detail geometry within
tolerance at a lit exhibit, on both sides of a thin partition and in a lit
recess, confirming a deliberately broken
partition fails the lighting check, and confirming a wave of camera tier
changes triggers zero trace rebuilds (counting rebuilds separately from
lighting error). Once the trace mesh is camera-independent, price storing
cached probe hits on disk by their input identity as an optional follow-up.
Test coarse-to-fine convergence (far level, storey-sized level, then finest) in
a harness judged by time until near-player lighting is right, not pass count
alone. [The settle-time record](illumination/research-settle-time.md) measures
what the view-driven scene costs a boot today and ranks this step first among
the fixes for it.

**Tracing light with the field (comparison after stable trace mesh).** Where
triangles may be the wrong representation, compare four ways inside the one
tracer on a walk from the skylights toward moving foliage (with blinds falling
and casters arriving): the triangle hierarchy alone, the hierarchy with cheap
mesh-bounds culling, CPU certified empty boxes
(`SdfFieldEvaluator.TryDistanceBounds` and
`SdfFieldEvaluator.TryCertifiedLineOfSight`, skipping only segments proved
clear of current opaque blockers with an allowance for mesh-to-field offset and
withdrawing the proof when a blocker arrives), and GPU sparse distance bricks
sampled by the bake kernel (for visibility only, reading no materials). The
allowance matters because a field proof is about the field while the tracer
intersects triangles: a coarse mesh stands outward of the field and can bridge
an opening the field leaves open, and lowering a blind adds a blocker after a
box was proved. A distance volume was built once as the main picture and took
about 10.6 ms against the meshes' 9.2 ms on the RTX 2060 while losing the
scene's materials; a visibility trace reads no material, which is why bricks
return here only for visibility. A triangle node, an interval evaluation and a
distance step are different amounts of work, so measure correct rays completed
per second at equal quality (including build, invalidation and transfers),
reporting traversal steps saved, evaluator calls, bytes held and proofs
withdrawn. Judge binary visibility and closest-hit relighting apart, and test
one known ray crossing a proved-empty box before and after a blocker arrives,
because a picture of the whole scene can miss it: one false "clear" fails a
method. In the debug world, lowering the skylight blinds moves the shadow's
edge correctly. Keep a field method only where it beats triangles inside the
one tracer; otherwise the field stays the bake-time judge.

**A moved window published slot by slot.** `SdfMeshIrradiance.Publish` makes
completed cells available during a refill. The gather checks the slot's world
coordinates and tries a measured parent for the missing share. A view whose
camera sample is still pending after a cut reads its authored sky. Measure the
first published light and `world.iteration`'s `lit=` stage after a teleport.

**Hardware ray query, one kernel with two traversals (built; its reading is
owed).** The probe trace, the relight and the camera sample each have one body
(`passes/sdf-probe-trace-stage.hlsli` and its two siblings) that their
software build and their ray-query build (`sdf-probe-*-rays.comp.hlsl`, which
defines `SDF_PROBE_RAY_QUERY`) include. In the ray-query build
`sdfProbeWalk`, behind both entry points, traces an inline `RayQuery` through
the scene's top level instead of walking the uploaded hierarchy, and a hit's
normal, material and identity come from the same instance record and mesh
words through `sdfProbeSurface`, so it is never a second relight. As built:

- *Capability.* The variants depend on `GpuOptionalCapabilities.RayQuery`
  (`SdfKernelSet.RequiresOf`): a device reports it at Direct3D 12's raytracing
  tier 1.1 or with Vulkan's three ray-query extensions, the pipeline set leases
  the variants only there (`SdfWorldPipelines.Runs`), and `--withhold-gpu
  ray-query` creates the device without it.
- *Structures.* `SdfMeshProbeGpuWork.Rays` builds one bottom level a geometry
  of the published scene, read in place from the mesh region, compacts each,
  then builds one top level over the scene's draws, each draw an instance
  whose index is its record's (the scene's word one holds the first record's
  address, `SdfMeshProbeTrace.InstanceBaseWord`). Each is a background job cut
  to the frame's share by its measured rate, and a scene change rebuilds the
  top level and only the bottoms it lacks.
- *Lifetime.* A structure leaves service only through `Retire`, which holds it
  in a `GpuRetirementQueue` until every job admitted before it has landed (the
  client's landed job serials are the queue's completion points); that is how a
  bottom retired with its mesh tier outlives every queued trace of it.
- *Switch.* `render.rayQueries`, each quality preset's `rayQueries` row and
  `world.rays` turn it on, for every residency the session presents: a routed
  painting world follows the presenting session's lever
  (`WorldSessionSceneEmitter`), since the row is a device's cost, not a look
  its document authors. A query takes the variant only once the top level
  stands for the published scene, and the software walk otherwise. Turning it
  off, or publishing another scene, retires the structures the publication no
  longer uses, and a structure job the background service hands back (no
  room) backs off so the software walk's queries keep running.
- *Reading.* `sdf.mesh-gi.gpu-query-rays` and `gpu-query-trace-nanoseconds`
  are the hardware's share of `gpu-rays` and `gpu-trace-nanoseconds`, so rays
  per second read both ways from one run; `world.residency`'s `trace-structure-*`
  fields give the bytes the structures hold, retired ones included
  (`device-bytes`), and the bottoms' bytes beside the triangles they cover
  (`bottom-bytes`, `bottom-triangles`). Their ratio is the driver's, so it
  compares backends apart from the scene: two readings of the whole bytes at
  different scene states (a different resident tier, structures retired but
  not yet released) differ by the scene, not the backend.

What is owed: the same hits within the device law's tolerance on both
backends, no light through a thin wall at the teal corner or the stairwell,
and the structures' bytes at the hero spawn, the painting wing and the garden;
then the RTX 2060 and the Deck. On the RTX 4070, over structures whose bytes
agree geometry for geometry on both backends, Direct3D 12's ray-query trace
answers at about a quarter of Vulkan's rate, while the two software walks
agree; why is owed, from one trace job of each backend under Nsight Graphics
or PIX (the traversal's own time against the hit's reads), before the
Direct3D 12 rate is judged against the software walk's. If the hardware
walk does not beat the software walk for probe rays on a device, that device's
table turns the row off and the structures stay for shadows by ray.

**Coarse-level visibility.** The coarsest level still answers a few corners
across a slab at the four-storey hall's far end. Two candidates, one
experiment: a flood from connectivity built once per window and change set (a
bit per fine cell in a coarse probe's footprint), and a per-probe visibility
lattice exact at trace time, which was rejected on cost while the trace ran on
the CPU (about thirty times the hit buffer's rays) and is worth measuring now
that it does not. Check: the hall harness's `far-exact-blocked` corners close
and the hills stay clean.

**Watched, each with what would take it up:**

| Option | Why it waits | What would change it |
|---|---|---|
| A sparse, geometry-driven finest level (Unity's bricks, Lumen's indirection, Split RC's hashmap) | Indoors every cell is within reach of a surface, so it extends nothing in the hall; outdoors and underground it frees most of the window | The streaming plan's byte bound on the probe cache binding outdoors |
| Sun visibility from a light-space depth in place of shadow rays | None of the studied systems does it for hit lighting, and shared hit lighting shrinks the same rays without a second shadow mechanism | Shadow walks still dominating a relight after shared hit lighting's step 2 |
| A distance-brick trace target | At a 0.1 m finest voxel the finest cascade of 64³ voxels spans 6.4 m and the third cascade's voxel is thicker than a 0.3 m slab, so it cannot hold Puck's walls at reach | The GPU trace missing the bar on the Deck with no ray-query path there |
| An authored `indirect` region that overrides a room's probes | Room segmentation is artist time per leak, which the rule "a leak is a defect, not a style" forbids as the fix | An author who wants a region lit by hand as a look |

**Considered and not taken:**

- *A warm-started lockstep drain.* The bounce iteration contracts to one fixed
  point, so a drain started from the previous tick's light would converge in a
  few passes. It would land within `ConvergedShare` of the fixed point from a
  different side than a cold drain, which is more than the one-level residue
  the lockstep stills repeat to, and two runs started at different ticks must
  capture one still. The drain keeps its from-no-bounce start; the windowed
  path already is recurrent.
- *Filling the cache from the previous frame's screen.* Per view and stale off
  screen, the reasons screen-space indirect light is set aside.
- *Random ray rotation and stochastic light selection.* Cell-random rotations
  stamped spatial noise when measured, and a stochastic light pick is noise an
  exact convergence does not need.
- *Probe classification that switches probes off.* An inside probe is already
  rejected and the rest relocated; a probe in open air still lights bodies
  there.
- *A GPU rebuild of the whole hierarchy each frame.* Each geometry's hierarchy
  is built once and reused while the geometry stands, and only the top level
  over the draws is rebuilt per scene change.

## What the state-of-the-art audit adds

[The audit](state-of-the-art/audit.md) read Puck's lighting against published and
shipped practice. It found the structure ahead (probes relit rather than
retraced, cascades held under a moving sun) and these gaps in amortisation.
Where an item already exists above, the audit's evidence extends it; the rest
are new. All of them move work from every pixel or every frame to the moment
the field or a light changes, and each is judged on presented frames in motion.

- **Per-vertex probe visibility from the certified field**
  ([finding 2](state-of-the-art/audit.md#2-the-probe-gather-tests-16-probes-per-cell-per-frame)).
  The gather tests 16 probes against a 12 by 12 octahedral depth map for every
  shaded cell every frame. Precompute each static vertex's probe visibility
  weights with `SdfFieldEvaluator.TryCertifiedLineOfSight` when geometry or the
  probe placement changes, and keep the full gather for moving bodies. This
  is the field-for-triangles move of [tracing light with the field](#after-the-gpu-trace),
  applied to the lookup instead of the trace, and it shares that step's rule
  that a proof is withdrawn when a blocker arrives. Check: stills at the gather
  stops stay within the gather's tolerance, depth taps per pixel fall for static
  receivers, and a body crossing a proved pair withdraws its weights in the
  same frame.
- **Per-vertex baked occlusion and bent normal from the field**
  ([finding 8](state-of-the-art/audit.md#8-local-lights-ambient-occlusion-and-the-suns-cascades)).
  Decision 4's bitmask march covers detail below the probe spacing per pixel.
  A field-derived per-vertex occlusion and bent normal carry the part that does
  not move, so the march spends its taps on moving casters and thin surfaces.
  Visibility-bitmask AO itself is decision 4 and its build-order step 4, with
  XeGTAO's cost at about 0.56 ms at 1080p on an RTX 2060 as its reference.
  Check: `world.debug-view ao` on the gallery's crevices shows the baked term
  and the march agreeing where nothing moves.
- **A real shadow for a local light by a per-pixel ray.** Decision 5's budgeted
  shadow residency (an atlas of point, spot and area shadows) is one route.
  The other is a per-pixel shadow ray through the any-hit ray-query door
  already used by the probe trace (`shade/sdf-probe-trace.hlsli`), which
  needs no atlas and no residency but costs a ray a pixel per shadowed light.
  Choose between them by counted work at 68 fixtures and at ten times that,
  inside step 5's list measurement; the ray route is the 4070's path and
  falls back to the atlas where ray queries are absent. Check: a local light
  that lost its atlas slot still shadows in the ray path, with the same
  shadow edge within the still tolerance.
- **The sun's cascades at small penumbra.** One cascade spans 16 to 120 m at
  2048 texels, about 10 cm a texel. Weigh a third cascade, or rays at small
  penumbra, for the 4070 at 1440p, against the eye-centred cascade memo in
  build-order step 1. Contact shadows are a low-priority add. Check: a
  close-up of a thin shadow caster at the near edge of the span reads sharp at
  the owner's reference without a visible handover.
- **An exact visibility brick cache from the field.** The distant shadow's
  refresh, probe visibility, sky occlusion and local-light shadows all ask the
  same question of a static scene: what can this point see? Evaluate it once
  from the field at bake time into bricks and serve all four. This is the
  distance-brick option in [tracing light with the field](#after-the-gpu-trace),
  widened from the probe trace's visibility to the three other consumers. It
  is not a main picture: a distance volume was built once as one and lost the
  materials, and bricks here hold visibility only. A quick check comes first:
  does the distant pass draw the coarsest tier? If it draws a finer tier than
  the distance warrants, that is a defect to fix before any cache. Check: the
  distant map's refresh cost falls with no change in the shadows it casts, and
  one brick read answers a probe, a distant receiver and a local light's
  shadow test for the same static point.

---

[Plans](README.md) · [Rendering](rendering.md) · [The engine job system](engine-job-system.md) · [Decisions](../decisions/rendering.md)
