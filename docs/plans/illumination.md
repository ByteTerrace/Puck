# Illumination

Puck lights a frame with cascaded shadow maps for the key light, a per-view
light loop, and a CPU-traced three-level probe volume for indirect light
([the lighting handbook](../rendering/sdf/handbook/lighting-and-shading.md)).
The owner asked for a research round on global and local illumination before
any further graphical work, and three families researched it independently:
global illumination, shadows and direct light, and the latest research and
stylized lighting. Their three consensus lists agree on the same five items.
This plan turns that agreement into decisions, says how each decision serves
an author building a scene nobody here imagined, settles the gaps the round
found, records what was rejected and why, and orders the build as lanes
checked on the running debug world.

The criterion that decided every verdict is the owner's: authors will be more
creative than us, and Puck exists to serve them. A technique serves authors
when it handles arbitrary scenes (any geometry, dynamic and emissive objects,
portals and windows, interiors and exteriors), when it is a general mechanism
with looks layered on as authored styles, when it degrades gracefully with no
cost cliff, and when its cost is visible and budgetable. The bar is the worst
frame at the highest quality: 60 FPS on the RTX 2060 and RTX 4070, 45 FPS on
the Steam Deck, with no lower quality tier as a fix
([rendering plan](rendering.md#the-forcing-artifact)). Every number below that
came from a paper or a talk keeps its original hardware; none is a prediction
for Puck, and no estimate for the Deck is extrapolated from the 2060.

## Where the volume stands

The research round read the code, and the plan rests on these facts:

- A probe record is 235 words: six sky shares, an 18-half radiance ambient
  cube, 128 static-light visibility bits, a relocation offset and a 12×12
  octahedral first-hit map with curvature
  ([mesh GI reference](../../.claude/skills/rendering/references/mesh-gi.md)).
  The sky enters as a visibility share the views pass multiplies by the live
  sky SH (`shade/sdf-mesh-irradiance.hlsli`, `sdfMeshIndirect`), so the sky
  term already relights for free at zero bounces. The sun, the static point and
  spot lights and every emissive surface are baked into the radiance cube at
  trace time, and later bounce sweeps bake the sky's colour too.
- `RefreshLighting` hashes every directional light record and every traced
  light; any change advances the generation and restarts the trace
  (`src/Puck.SdfVm/SdfMeshIrradiance.cs`, `RefreshLighting`), and a batch
  that lands after a generation change is thrown away (the `generation !=
  m_generation` test after the parallel trace). The debug world's key light is
  static (`worlds/debug/hall.puck`, `directional(name: "key", …)`), so no run
  has exercised a sun that moves every frame. This is the [high] moving-sun
  finding in the [Lighting and GI packet](open-items.md#lighting-and-gi).
- Each probe traces two ray sets from one origin: 128 full-reach radiance rays
  and 144 bounded depth rays. Both record first hits.
- Only static opaque draws cast into the volume (`SdfMeshIrradianceScene`
  admits `!IsDynamic && !Glass`). A moving body receives light and casts none;
  a lane-driven emissive reads zero in the trace.
- A full High window takes 8.35 s of wall time and about 90 to 98 s of
  process CPU per High-and-Medium pair on the lead machine. Nobody has
  measured the trace on the Deck's four-core CPU.
- The views pass loops over every light the view's frustum selected
  (`passes/sdf-light-stage.hlsli`; `sdfLightAt` applies `worldLightSelected`
  and `sdfLightResponse` exits at zero reach). A bound screen lights a surface
  as an unshadowed centre-and-area diffuse term with no specular.
- The cascades cover 16 m near and 120 m far with casters 200 m toward the
  light, authored as `render.shadowCoverage` (`SdfShadowCoverage`) and printed
  by `world.budget` with each cascade's texel. The surface pass queries them
  wherever a light's diffuse response is non-zero, a wrapped material's side
  past the terminator included (`passes/sdf-surface-stage.hlsli`,
  `sdfMaterialWrap` and `sdfMeshShadowTransmission`).

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
that axis. Each probe stores a normal-conditioned sky transfer (a transfer
vector per ambient-cube axis, or the SH product followed by the cosine
convolution) so that the live sky SH9 (`SdfSkyEnvironment` already projects
nine coefficients) relights it correctly for every receiver normal. A single
visibility vector dotted with the sky radiance omits the cosine term and is
not enough; the Codex consensus caught this. The idea is precomputed radiance
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
its step 7); toon mode that keeps rim, reflections and point-light occlusion
(today `sdf-light-stage.hlsli` drops rim under `!toon`, skips
`sdfMeshReflection`, and passes 1.0 for ambient occlusion into point-light
banding); a visibility-preserving banding option, `band(diffuse) × visibility`
beside today's `band(diffuse × visibility)`, as authored style data; and
shading lengths that follow a body's scale (cascade bias, AO and shadow tap
radii, `NormalBias`).

**Why:** these are demonstrated defects fixed with no new machinery, and they
come first because every later measurement reads through them. Codex ranked
them first and Antigravity second; the Claude consensus agreed. The PCSS
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
Either leg lets a moving rigid body cast into the volume, removes the 8.35 s
CPU trace, and makes the job system's change-set closure
([B1](engine-job-system.md#gi-as-jobs-and-only-the-probes-whose-light-can-change))
cheap to apply.

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

- `render.indirect.budget` holds the probes relit per frame, the hits traced
  per frame, and the target lighting age in seconds; the authored day length
  sizes the relight rate (a 24-minute day moves the sun 0.25° a second). The
  defaults live in the standard package as data, not in C#.
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

Cel shading is a style per material since step 1
([the handbook](../rendering/sdf/handbook/lighting-and-shading.md#cel-shading)):
a palette row's `toon` (`ToonStyleDocument`, `SdfMaterial.Toon`) shades its
material, every other material inherits the world's default `render.toon`, and
`world.toon` is the live lever over that default. A style holds a band
threshold and softness, a `shadowTint` that recolours indirect light and sky
ambient, distance-faded outlines, a bounded toon specular, an exposure
shoulder and its band shape; the standard authors `celStyle`, `celHardStyle`
and `continuousStyle` in `worlds/standard/styles.puck`, and the gallery's
level 3 STYLES row shows them beside continuous materials. Before step 1 it was
one per-world setting that banded `saturate(cosine) × saturate(visibility)` in
one `smoothstep`, collapsing the penumbra into the band's edge, and dropped rim
light, the environment reflection and point-light ambient occlusion; the
research round's shadows report (Codex, item 1) and stylized-lighting report
(Antigravity, item 2) both flagged it. Four decisions place cel shading in
this plan:

1. **Banding preserves visibility.** The band shapes the lighting response
   and the shadow stays a separate factor: `band(cosine) × visibility`, so the
   penumbra's softness and the visibility's colour survive under the band, and
   `band(cosine × visibility)` remains available as a second authored shape
   for a harder look. Rim light, the environment reflection and ambient
   occlusion stay in the toon response as terms a style may scale, never as
   terms the mode discards. This is part of step 1, direct-light correctness,
   because the same contract (query where the response is positive, filter
   what was queried) decides both.
2. **Cel shading is an authored style layer, not a world toggle.** The
   mechanism is one lighting path (decisions 1 to 5) with a style response
   applied per material or per style: a material's palette row or an authored
   style in `worlds/standard/` opts into banding, its threshold and softness,
   its shadow tint, its outline and its specular shape, and the world document
   supplies the default style a material inherits (the owner's ruling: a style per material, not one look per world). One scene can then mix
   painterly foliage, cel-shaded characters and continuous screens, as the
   Wind Waker direction asks. `render.toon` and `world.toon` become the
   world's default style and the live lever that overrides it, with the same
   fields, so a document that sets `render.toon` today keeps its look. The
   pass-block tuning joins the lighting cadence signature as it does now and
   never enters the bake closure.
3. **GI feeds the style.** The band reads the lit surface after the volume,
   the bent-normal AO and the sky transfer have supplied their light: the
   shadow band's colour comes from the probes' indirect bounce and the sky
   tint along the bent normal, multiplied by the style's tint, rather than from
   a flat `ShadowTint` alone. A room's shadow band warms from its walls and a
   lawn's cools from the sky because the volume says so, which is the look
   the art direction describes (coloured shadows, never black) and which flat
   bands cannot give. This lands with step 2, when the relight makes that
   light live under a moving sun, and is the authored `bleed`, `receive` and
   indirect-tint path reading through the style, not a second lighting model.
4. **Outlines are checked against the meadow and the portals.** The depth and
   normal edge test runs before temporal resolve, so thousands of animated
   blades are the stress case for ink shimmer and portal pictures for ink
   bleeding onto a picture the style must leave alone. The meadow's blades and
   the painting wing's portals are step 4's outline check, beside the AO's.

## The gaps the round named

**Screens and portals feeding light into the probes.** Today a screen reaches
a view only through the analytic room glow
([screen emission](../rendering/sdf/handbook/lighting-and-shading.md#screen-emission))
and the trace ignores it. Decision: a screen, a portal opening and a window
onto another world are area emitters in the relight, shaded from the same
reduced image the room glow reads, with LTC response at the hit (decision 5).
A portal is a display for lighting purposes (the owner's ruling): the other world's image already
carries that world's sun and sky, so the relight uses the image, and no
directional light crosses a portal. An author who wants the other side's sun
to cast through places a light. This keeps every residency's lighting its own
and costs a texture read per hit facing the opening.

**Nested worlds each running their own trace.** Every residency, one per world
shown, runs its own volume and its own trace. Decision: the relight and the
trace are jobs of the job system's `Lighting` class, and one budget covers
every residency a frame renders, split by each one's share of presented
pixels. A residency no view shows runs no GI work (the scheduling stage
already holds unseen bodies). The counters report age per residency, so a
screen showing a nested world that falls behind says so without stealing the
main world's budget.

**The exposure meter's readback when probes move to the GPU.** The meter reads
the probes on the CPU (`SdfMeshIrradiance.TryMeter`) and traces at the camera
when no probe answers. Decision: the terrain pass's image meter
([exposure at its cause](terrain-pass.md#exposure-at-its-cause)) becomes the
exposure's source, with no readback, and the probe meter stays for local
adaptation and the air light as a GPU read: the relight writes each view's
eight-probe gather and, where the camera stands in an enclosure with no
lattice probe, one camera "probe" traced and relit like any other, into the
same one-element buffer the image meter and the composite share.
`world.exposure` reads that buffer back for diagnosis only. Nothing on the
frame path waits on a readback.

**The lockstep capture drain.** A lockstep frame traces the window to the end
before rendering so a still never depends on how far an asynchronous trace
had got (`SdfMeshIrradiance.Stage`, the `drain` path). Decision: the same
contract holds on the GPU. A tick-locked frame dispatches trace and relight
until the pending queue is empty, fenced before the frame's views record, and
the budget does not apply to it. Captures stay history-independent at the cost
of the capture's own frame, which is the cost lockstep already pays for bakes.

**The Deck's measurements.** No one has timed the CPU trace, the cascades or
the views pass on the Deck, and the Codex consensus found that one report
budgeted a Medium window there, which the bar forbids. Decision: step 0
measures today's CPU trace and frame on the Deck at High, every later step has
a Deck leg at High, and no Deck figure in this plan or its lanes is scaled
from another GPU.

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
| Moment, variance and exponential shadow maps | Reject as default | Light bleeding through stacked storeys; three RGBA16 maps are 96 MiB against 48 MiB today | A constrained use that accepts the bound's error |
| Virtual shadow maps | Watch | Continuous sun motion and thousands of bending blades invalidate cached pages every frame; Puck lacks the page machinery | A measured coverage or detail limit the cascades cannot reach |
| Fourier-compressed horizon maps | Watch | Height-field only (Fritsch et al., HPG 2025, <https://www.cg.cs.tu-bs.de/upload/publications/fritsch2025fchorizonmaps/fritsch2025fchorizonmaps.pdf>): an author's arch, cave or overhang breaks it; 0.1 to 0.55 ms on an RTX 4090 and 1.3 to 8.8 ms on a notebook RTX 2050 | The terrain cascade's measured cost on the Deck failing the bar for far shadows alone |
| SH exponentials for glossy reflection | Watch | 33 coefficients per channel (Silvennoinen et al., HPG 2025, <https://arisilvennoinen.github.io/Publications/HPG_2025_CRC.pdf>), so a new probe payload; poor below roughness 0.25; the painterly direction makes glossy indirect low priority, though an author may want polished floors | The hit-and-relight volume landed, and an authored world that needs glossy interreflection |
| Per-pixel SDF cone shadows and sky cones | Reject | A field march in the frame, which the frame does not do; 3.4 ms on a Radeon 7870 at 1080p for sky cones alone | Nothing while the frame rasterizes baked meshes |
| Top-K per-tile local light selection | Reject | Silently dropping the fourth relevant light is the author-visible cliff the criterion forbids | Nothing; decision 5 reports overload instead |
| Path guiding of probe rays (vMF lobes, portal MIS) | Prototype later | Fits the stairwell enclosure finding; convergence claims are unmeasured | The hit buffer landed, so a guided ray has somewhere to be cached |
| Screen-space indirect light | Reject | Per view and view-dependent; recorded in the register | Nothing |

## The build order

Each step is one lane with a check on the running debug world at `world.quality
high`, on both backends and the Deck, across the authored day cycle. Stills use
the tolerant comparison from the
[job-system memo](engine-job-system.md#6-build-order): two captures of the
earlier build measure the run-to-run noise, and a difference beyond it is
flagged by region and looked at, never a stop. Frame time is `host.frames`
worst iteration under the bar; counted work (`world.counters gpu`,
`world.counters sdf.mesh-gi`) stands beside it. No step requires a
byte-identical still.

The job system's steps that touch GI are J1 (the trace's units become
`Lighting` jobs) and B1 (the change-set closure). Steps 1, 4 and 5 below touch
neither and run now. Step 2 lands before B1 so the closure is written once,
over hits: after step 2 a light change costs a relight and B1's "a light
change still invalidates the window" becomes untrue for lights. Step 2 keeps
one probe as its unit, so J1 can adopt it before or after. Step 3 is GPU work
the frame thread records, which J1 does not reorder.

| Step | Lane | Lands | Check on `worlds/debug` |
|---|---|---|---|
| 0. Confirm and baseline | rendering, with a Deck leg | The authored day cycle as standard data (a `dayCycle` template in `worlds/standard/skies.puck` driving the key light's direction, colour and the sky from one clock; the debug world imports it, with a lever to hold the sun); the moving-sun run; today's CPU trace and frame timed on the Deck at High; the [corrections](#corrections) below | Headless, key light bound to the clock: whether `[sdf.mesh-gi] done` lines ever print and whether `world.counters sdf.mesh-gi` probes settle or restart each frame; `world.probe` at the teal corner reports `unsettled` for the run's length if the finding holds. Deck: `[sdf.mesh-gi] done … seconds=` at High and `host.frames` at `debug.go overlook` |
| 1. Direct light correct (landed but for the scale-following lengths, the back-plate lines and the terrain cascade; see [step 1's status](#step-1-status)) | rendering | The wrapped-diffuse query; `band(cosine) × visibility` with the penumbra kept, and toon keeping rim, reflections and AO ([cel shading](#cel-shading), decisions 1 and 2: the style layer per material with the world default); scale-following shading lengths; cascade coverage printed in `world.budget`; the terrain cascade with the terrain pass's step 7 | Indoors under the 100 klx sun at `debug.go gallery` with the Moth's wrapped skin past the terminator: no sunlit patch under a roof (`world.debug-view shadow`); the quarter-size Moth's back-plate lines gone; under a cel style the Moth's terminator shows the cascades' penumbra in `shadow` and her rim in the lit view while the floor beside her stays continuous; `valley-rim` shows ridge shadows on the far walls; stills at the sweep's hall stops compared tolerantly; the frame bar at every stop with the sun moving |
| 2. Hits and relight | rendering, principal-grade review | The merged hit buffer written by today's CPU trace; the GPU relight under `render.indirect.budget`; per-probe rescale; the sky transfer with its cosine term; the budget surface in `world.budget`, the counters and `world.lights`; the lockstep drain on the GPU; the exposure path's GPU read; the cel style's shadow band reading the volume's bounce and sky tint ([cel shading](#cel-shading), decision 3) | The day cycle at `debug.go gallery` and `overlook`: indirect light follows the sun with the age counter under the authored target and no `[sdf.mesh-gi] behind` line; a lamp switched by `gallery.lights.all` relights without a trace (`traced` unchanged, `relit` rises); under a cel style the hall's shadow band warms when the lamps come on and the lawn's cools under the open sky; `world.exposure` continuous across the atrium's arch; the frame bar across the cycle on all three; a lockstep still of one tick repeats across two Worlds within the noise |
| 3. GPU trace hits | rendering, GPU leg on both backends and the Deck | The trace-hits interface with both legs; rigid bodies cast; the CPU trace deleted once a leg holds on all three | A body posed under a lamp darkens the floor in `world.debug-view indirect`; a `world.reload` that removes a wall re-traces its closure (B1's check) in tens of milliseconds amortized (`[sdf.mesh-gi]` traced against the window); the cold boot's GI seconds against today's 8.35; thin-wall stills (teal corner, stairwell flight B) compared tolerantly with step 2's; the Deck decides the leg |
| 4. Bitmask AO and bent normal | rendering | The bitmask march over the depth pyramid; the bent normal in the visibility record and in the gather; the cel outline's check ([cel shading](#cel-shading), decision 4) | `meadow.puck` blades without dark halos and thin crevices closed in `world.debug-view ao`; under a cel style with outlines on, a presented burst of eight frames at `debug.go meadow` shows no ink shimmer between frames beyond the blades' own motion, and at `debug.go portals` no ink on the paintings' pictures; `world.gpu-timing` of `surface` against today's on all three; stills at the gather stops compared tolerantly |
| 5. Lights | rendering | The list measurement; LTC for screens, portal openings and authored rectangles; the local shadow residency with overload named | The device row with every screen on: specular on the floor in front of each screen; `world.counters gpu` views texels at 68 fixtures and at a scratch world with 680; a scratch world past the shadow residency names the deferred lights and shows no flicker; the frame bar at `debug.go devices` |
| 6. Openings and residencies | rendering | Screens, portals and windows as emitters in the relight; one `Lighting` budget across residencies by pixel share | The garden portal's light on the hall floor in `indirect` follows the garden's day; a nested world's screen reports its own age; the main world's age unchanged when a nested world falls behind |
| 7. C10, light across the valley | the composition lane | The seam check the [composition plan](debug-world-composition.md#the-order) names, now over the cycle | A walk from `debug.go gallery` to the pool across a full day: no dark interval, no clipped rock, the rim's shadow on the floor, coloured shadows under the trees from authored style, `world.exposure` continuous |

Steps 1, 4 and 5 run in parallel with step 0 and with the job system's J1 and
M-steps; they share no files with step 2. Step 2 waits on step 0's run; step 3
on step 2; step 6 on steps 2 and 5; step 7 on the terrain pass's steps 1 and 7
and on step 6. Where a step and the job system meet (J1 moving the trace's
scheduling, B1 narrowing what a scene change re-traces), the GI step keeps its
unit and the job system supplies the policy, so neither waits on the other
needlessly.

### Step 1 status

Landed and checked on the debug world on Direct3D 12, and by eye on Vulkan:

- The wrapped-diffuse query, with casters within the receiver's own depth
  transmitting. Indoors under the slab beside the PRIMITIVES plinths, the
  Moth's cheek past the terminator no longer takes the 100 klx sun: one World,
  the surface kernel hot-swapped between the old and new query, differs only
  in the two 32-pixel tiles over her cheek; outdoors at the stage no tile
  differs, and the meadow's blades in the open keep their wrapped light.
- `band(cosine) × visibility` as the default band shape (`band: Visibility`)
  and `band(cosine × visibility)` as `band: Combined`; toon keeps rim lights,
  the environment reflection and ambient occlusion (point lights' banding
  included). At `debug.go stage` with `world.toon on` the Moth's shadow keeps
  its soft penumbra where it was a hard cutout.
- Cel shading as a style per material (decisions 1 and 2 of
  [cel shading](#cel-shading)), with the gallery's STYLES row mixing banded and
  continuous materials.
- Cascade coverage as an authored resource (`render.shadowCoverage`) printed by
  `world.budget`.
- No measurable frame cost: `surface` and `views` GPU time at the plinth stop
  stayed inside the run-to-run spread (surface 0.75 to 1.19 ms before, 0.72 to
  0.99 ms after; views 8.1 to 10.5 ms before, 7.9 to 9.4 ms after) on a GPU whose
  timings swung 50 % between identical readings.

Open in step 1:

- **The terrain cascade.** Stretching the far cascade over the valley is not
  the answer, measured at `valley-rim` with the sun at noon:
  `render.shadowCoverage { far: 480, casters: 600 }` raised `shadow-far` from
  208 draws and 0.57 M vertices to 7,170 draws and 14.3 M vertices, and from
  about 0.24 ms to 8.4 ms, and coarsened its texel from 13.9 cm to 55.3 cm. The
  far walls need the terrain pass's step 7: a third key-light cascade from the
  far split to the far distance, drawing static draws at their coarsest level
  and held while the sun and the static draws hold still.
- **Scale-following shading lengths** (cascade bias, AO and shadow tap radii,
  `NormalBias`) and the quarter-size Moth's back-plate lines.
- **The cel band under indirect light.** Indoors the hall is lit mostly by the
  traced bounce, which a style tints as its shadow band, so a banded material
  there reads as its shadow tone; decision 3 (step 2) is what makes that band
  read the volume's colour.
- **A dawn leg.** A windowed Vulkan boot showed the same three results (no sun
  on the cheek under the slab, the soft penumbra under `world.toon on`, the
  STYLES row's banded sphere beside its continuous twin). With the sun at about
  10 degrees the default style's 0.45 threshold puts every horizontal surface in
  the shadow band, so a dawn penumbra check needs the authored day cycle and a
  style tuned for it.

## Corrections

Errors the research found in Puck's own documents and comments, for the first
lane (step 0 or step 1) to fix:

- Fixed in step 1: the comment above `sdfMeshShadow` now states the
  implemented rule, the nearest blocker inside the light's cone and the nearest
  outside it when none is in the cone.
- The [mesh GI reference](../../.claude/skills/rendering/references/mesh-gi.md)
  says depth is traced on the first sweep and reused for later bounces. A
  lighting change resets a probe's sweeps to zero, so it also re-traces the
  geometry-only depth rays; confirm from `TraceProbe`'s sweep test and either
  make the code match the reference or the reference match the code. Step 2
  removes the question by merging the ray sets.
- `RefreshLighting` hashes directional and traced lights, the gains, the
  sources and the materials, but not the sky, so a sky-only change (an
  authored sky clock) leaves the bounced sky baked into the radiance cube
  stale while the zero-bounce sky term relights. Confirm in step 0's run with
  the sun held and the sky turning; step 2's relight removes the defect.
- The [job-system memo](engine-job-system.md#gi-as-jobs-and-only-the-probes-whose-light-can-change)
  says "a light, layout or window change still invalidates the window". After
  step 2 a light change invalidates nothing; update the memo when step 2
  lands.
- The research round's own report misnamed the Assassin's Creed Shadows
  presenters; they are Luc Leblanc and Melino Conte, as cited above.

---

[Plans](README.md) · [Rendering](rendering.md) · [The engine job system](engine-job-system.md) · [Decisions](../decisions/rendering.md)
