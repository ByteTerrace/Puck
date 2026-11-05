# Water rendering: the round over transmissive surfaces, flow shading and presentation particles

A research round over the rendering capabilities the debug world's water
showed Puck lacks: a surface that transmits, colours and reflects what lies
behind and above it; a surface whose shading moves with a flow without the
geometry moving; and spray, mist and splash with nothing to draw them. The
round was driven by the water ([C3 as built](debug-world-composition.md#c3-as-built),
the [art pack](../game/art/caldera-water-pack/README.md)), and the owner's
guidance against spirals set its level: each gap is an engine mechanism the
water is one user of, never a water feature. One lane ran it (a Claude
`principal`); every source in the tables below was opened by this lane
unless its audit column says otherwise, and no other family's claims enter
it. A consensus step over the citations by another family is listed under
[owned questions](#owned-questions).

It extends the [graphics architecture](graphics-architecture.md), whose four
mechanisms (M1 geometry once, M2 background GPU work, M3 change sets, M4
residency as demand) it reuses, and it builds on two settled decisions:
[glass is a surface of its own after opaque visibility](../decisions/rendering.md#in-world-devices-and-screens)
and the weather decision's
[particles are shared presentation state](../decisions/engine-design.md#weather-is-a-regions-composition-of-generic-pieces).
The [water packet](open-items.md#water) owns the measured system work it
leaves; the [author shaders](open-items.md#author-shaders) memo owns the one
question this plan hands it.

## The measured starting point

Every claim here was read from the code on the branch this plan was written
against; the brief's premises that the code contradicts are corrected in
place.

**What the water is.** One conserved cycle on a one-row flow lattice hidden
90 m under the floor (`worlds/standard/hydrology.puck`: a `field` lattice of
24 cells with a `flow(field: hydroWater, rate, over: [hydroBed])` reaction;
`worlds/debug/water.puck` places it at `[-60, -90, 30]`). Its cells are
stores, not places: `FieldLattice.StepFlow` moves water between neighbours
through a per-step delta it never publishes, so **no spatial velocity field
exists anywhere in the world**. Everything visible binds to the chain's
scalar gauges (`lakeLevel`, `fallsFlow`, `riverDepth`, `aquiferFill`,
`meltStrength`, `poolLevel`) and is one of two kinds:

- *Field surfaces.* Four two-dimensional `waterViews` lattices (the pool in
  `worlds/debug/pool.puck`; the plunge basin, reservoir and passage in
  `worlds/debug/water-scenery.puck`) whose one row is set to a gauge each
  step and drawn by `WorldFieldEmitter`: a CPU-baked distance brick per
  height field (`Bake`, `Reach = 2`), emitted as a `SampledValues` instance
  with the row's `material` lowered through `WorldSurfaceMaterials.Resolve`,
  which carries the palette entry's `glass` (`Glass: entry?.Glass?.ToGlass`).
  The standard's `waterMaterial` declares `glass { depth: 2, ior: 1.333 }`,
  so these four surfaces bake into glass groups (`SdfMeshBakeGroup.Glass`
  from `SdfProgram.IsGlassRange`) and draw through the glass passes. **The
  brief's premise that field surfaces draw through an opaque path is wrong
  for every field surface the water has.**
- *Creations.* The stream (eight `streamSegment` legs of 24 m, each one
  `Box` sheet with two brush-edge paints and six foam-streak paints, slid
  along +Z by a `halfSine` driver on the `time` signal), the falls (a
  `fallsRibbon` of fourteen `Box` sheets, each swung into the wall by
  `meltStrength`), the lake (a `Cylinder` disc slid by `lakeLevel`) and the
  glacier (six `Superellipsoid` lumps scaled by `glacierLength`). All wear
  `waterPalette` or `icePalette`, **opaque** rows with no `glass`, so the
  stream, lake and falls are opaque slabs: no refraction, no depth colour, no
  transmission. This half of the premise holds. The falls' spray is one
  `impactSpray` creation: a `cloud` volume, ray-marched by the composite
  (`shadeVolumes`, `sdfCloudDensity`) with its drift host-integrated from the
  presented tick (`SdfVolumeMotion.At`, `PresentedTick.Integrate`), scaled by
  the `discharge` driver.

**How the water animates.** Entirely by geometry: a slide moves a whole
sheet mesh, a swing folds a sheet, a size driver shrinks the glacier. A
moving sheet is a dynamic transform (no rebake per frame), but every stream
leg is its own bake group because its paints and phase differ per leg, the
glacier rebakes at each settled size (the rig's settle rebake), and the
foam streaks are paints that ride the sheet, so they slide as a block and
restart with the half-sine. Nothing shades by flow: the material layers
(`shade-layers.hlsli`: `sdfApplyPaints`, `applyInset`;
`shade-weathering.hlsli`: `applyWeathering`) read a static position and
write albedo, roughness, metal and coat; none moves with time and none
perturbs the shading normal. No SDF pass reads a clock: the Frame group's
`time` and `timeDelta` (`isa/sdf-world.interface.hlsli`) are the render
graph's wall-clock frame block, which no `sdf-*` kernel reads, and the
volumes take a tick-integrated phase through their own per-frame table
instead (the volume region's cadence in `SdfWorldTables`), so a settled
capture repeats.

**What the glass path does today** (`passes/sdf-mesh-glass.frag.hlsl`,
`passes/sdf-glass.comp.hlsl`, `frame/sdf-glass.hlsli`;
[the handbook's limits](../rendering/sdf/handbook/frame-rendering.md#glass-after-the-opaque-image)).
The glass raster accumulates, per pixel, a signed distance sum, a face count
and three 64-bit integer optical-depth sums over every glass face before the
opaque hit, each material's absorption integrated separately and the words
independent of raster order (`sdfGlassAccumulate`). The shade pass takes the nearest front face,
refracts once by Snell, reads the lit image where the bent ray would leave
the glass and carry on to the opaque hit (`sdfGlassProject`), transmits by
Beer–Lambert over the integrated optical depth, frosts over up to eight taps
by roughness, reflects the **sky alone** by Schlick Fresnel (`sdfGlassSky`
reads the residency's environment map under `sdfSky[0].Reflection`) and adds
each light's GGX highlight (`sdfGlassHighlights`). It already holds, per
glass pixel, the quantity every shoreline-foam technique in the research
needs: the distance from the surface to the opaque hit (`opaque - t`) and
the distance through the medium (`thickness`). Its limits: one glass layer a
pixel; no in-scattering (a medium only absorbs, so a deep pool goes dark
rather than turquoise); the reflection sees no geometry (an opaque surface
reflects the probes' radiance along the mirror direction,
`sdfMeshReflection` in `shade/sdf-mesh-irradiance.hlsli`, but glass does
not); the frost is view-independent.

**Reflections.** Three things exist and nothing else: the sky environment
map eased toward the sky irradiance by roughness, then the sky's panel layers
(`worldEnvironmentReflection`, `shade/sdf-lighting.hlsli`); the irradiance
volume's radiance along the mirror direction, occluded by its visibility
(`sdfMeshReflection`), for opaque surfaces; and the glass pass's sky read.
The six "screen-space" mentions the brief counted are the word "reflection"
in these and in the bloom filter's cell-edge reflection; **there is no
screen-space reflection, no planar reflection and no reflection of geometry
on glass**.

**Particles.** None in `src/`: no pool, no emitter, no sprite or streak
draw. The decision exists and binds this plan
([weather](../decisions/engine-design.md#weather-is-a-regions-composition-of-generic-pieces):
region-owned GPU pools stepped once per presentation step for every view,
sized by machine byte and work budgets, drawn as instanced analytic streaks
and sprites after the opaque resolve, composed by depth with glass and with
media through shared depth segments) and the weather packet's slices 2 and 3
in [debug world content](open-items.md#debug-world-content) schedule it
behind the job-system memo. The bounded volumes are the one medium Puck
draws: at most 64 (`SdfProgramBuilder.MaxVolumes`), two kinds (a flow
column, a cloud), each costing `steps` noise evaluations per pixel of its
screen footprint per share it crosses, composited after the sky and clipped
at the opaque hit's distance, so a volume lying inside a glass solid still
paints unrefracted (the handbook: media composite at the opaque hit's
distance).

**Numbers.** The water follow-up merge recorded the mesh region at 1.9 GB
after cutting it from 2.7 GB and the compile peak at 2.25 GB from 5.2 GB
(its commit); the graphics plan's cold bake reads about 151 s with the water
on both backends against the one-minute bar
([starting point](graphics-architecture.md#the-measured-starting-point)).
No reading isolates the water's own share of the mesh region, the glass
passes' GPU time at the pool, or the cloud volume's composite cost; the
brief's "about 1 GB" is a lead this round could not confirm from a commit,
and the first step's checks take those readings.

## The general mechanisms

The step back first. Each gap is one instance of a mechanism Puck already
partly has, and the plan builds the mechanism once.

**W1. A transmissive medium surface is the glass pass with a material's
medium response.** Everything a water surface needs that a window pane does
not (depth colour by absorption, in-scatter that lightens a deep medium,
foam and a wet edge where the medium meets a solid, a normal that moves)
is a function of what the glass pass already measures per pixel (the
distance into the medium, the thickness, the surface normal, the opaque hit)
and the probes it does not yet read. A water surface is a palette row with
`glass` and a few medium parameters; ice, a tinted pane, a jelly and a fog
bank's wall are the same row with other values. The engine's default medium
response is neutral (absorb only, as today) and the standard package chooses
the water's. The water packet already rules it: "do not add a water-specific
shader". W1 is M1 on the shading side: one path for every transmissive
surface, field or creation.

**W2. Flow-driven shading is a material layer whose coordinates advect.**
The material layers are evaluated at the hit in the winning frame with a
pixel footprint; a flow layer is one more, whose lookup point is displaced
along an authored flow vector by a phase, in the two half-offset layers
every shipped flow map uses (Vlachos), with a per-cell noise against
pulsing, and whose outputs are a normal perturbation and a coverage (foam,
streaks). The flow comes from where any authored value comes from: a
constant per material (a stream leg's +Z), a render lane per instance, or a
field row the state system owns. The phase comes from where the volumes'
comes from: host-integrated from the presented tick and carried in a
per-frame table, never a wall clock, so a settled capture repeats and
lockstep holds. The geometry stops moving: the stream's eight legs become
one shared static geometry under M1, the falls fourteen static sheets, and
the foam a layer rather than a bake group. The meadow's wind, a lava flow,
a conveyor and a scrolling display band are the same layer with other
vectors.

**W3. Presentation particles are a region-owned pool on the frame graph
with authored emitters and authored motion.** The decision fixes the shape:
a GPU pool per region, stepped once per presentation step, sized by budget,
drawn as instanced analytic quads after the opaque resolve, composed by depth
with glass and media. What the research adds is where the pool's forces and
collisions come from in an SDF engine: the exact field is the collision
proxy (Godot bakes one per collider for exactly this; Niagara's two GPU
options are the depth buffer, blind off screen, and the mesh distance
field), and Puck's bake already produces the narrow-band lattices such a
brick derives from (the graphics plan's watched option 4, M1). The emitter
is an authored shape in the program's own vocabulary (a paint's shape, a
volume's bound); the motion is an authored program over generic primitives
(advect by a flow vector or field, gravity, drag, collide with a sampled
brick, age), parameterised by the author with neutral defaults, and the
per-particle expression program compiled to the GPU waits on the author
shaders memo rather than being a second VM here. Spray, mist, splash, then
rain, dust, leaves, embers.

**W4. Reflections for a surface are one gather, not one per surface
kind.** Opaque surfaces reflect the probes' radiance and the sky; glass
reflects the sky alone. The one change that closes most of the art pack's
"reflected warm cliffs and sky" is to make glass read the gather opaque
reads (`sdfMeshProbesAt` then `sdfMeshReflection`), one implementation. A
screen-space march for the near geometry a probe cannot resolve (the bridge
in the pool) is the research's standard enhancement and is measured before
it is adopted; a planar reflection is a second view per plane and is
rejected on the bar.

The round's Puck-native combinations, weighed beside the borrowed
techniques below: the glass sums give contact distance per pixel for free,
so shoreline foam needs no second depth pass; the field's exactness makes
the particle collision brick a derived product of the bake rather than a
second bake (Godot's separate SDF bake); the paints' shape dispatch is
already a GPU evaluation of an authored shape, so an emitter needs no new
shape vocabulary; and the lattice the hydrology runs on could publish a
derived flow row (the per-step transfer `StepFlow` already computes) as an
authored field for W2, so a flow field is state the author owns, not a
texture an artist paints.

## Research

The question the brief set: how shipped engines and recent work render
transparent water with refraction, absorption and depth colour; flowing
water and waterfalls (flow maps, advected normals and foam, sheets and
spray); GPU particle and volume systems of fixed or low per-frame cost on
the bar hardware; foam at contacts; and reflections for water on a
baseline that runs everywhere. A row is marked opened when this lane read
the page, the deck's extracted text or the shader; a summary row rests on
a search engine's digest and carries no verdict alone.

### Transparent surfaces: refraction, absorption, depth colour

| Source | What it does | What Puck takes | Audit |
|---|---|---|---|
| Single Layer Water, Epic, <https://dev.epicgames.com/documentation/en-us/unreal-engine/single-layer-water-shading-model-in-unreal-engine> | An opaque-queue shading model whose material output supplies scattering coefficients ("the rate at which light scatters on particles within a medium"), absorption coefficients ("how easily light penetrates the volume of water", per channel, in reciprocal metres), a phase `G`, and a "Color Scale Behind Water" that "multiplies the luminance of the surfaces below the water" without projecting caustics; "The fully lit scene and depth are used as input to the Single Layer Water pass"; refraction "reads the depth and color below the surface before distorting the samples"; the low-end path "reverts the material to a simple translucent material with no volume integration" and drops screen-space reflections. | The shipped surface model is exactly Puck's glass pass plus in-scatter: a single depth layer, the lit scene read behind it, absorption per channel per metre. The in-scatter term is what Puck's glass lacks, and the fallback shows the baseline shape (no SSR). | Opened. |
| Looking Through Water, Flick, <https://catlikecoding.com/unity/tutorials/flow/looking-through-water/> | "The underwater depth is found by subtracting the surface depth from the background depth"; exponential fog on that difference lerps the grabbed background toward the fog colour; refraction offsets the grab UV by the tangent-space normal's XY scaled by the saturated depth difference; a negative difference after the offset means "we've sampled a fragment that's in front of the water", so the UV resets to the unrefracted position; "An underwater camera requires a different approach". | The depth-difference shoreline input and the in-front guard. Puck's glass pass already has both: `opaque - t` and the nearer-surface check that resets `at` to `own`. | Opened. |
| The Technical Art of Sea of Thieves, Ang, Catling, Ciardi and Kozin, SIGGRAPH 2018 Talks, <https://history.siggraph.org/wp-content/uploads/2022/09/2018-Talks-Ang_The-Technical-Art-of-Sea-of-Thieves.pdf> | FFT ocean after Tessendorf 2001; colour "blend[s] between a deep water colour and a sub-surface water colour based on a combination of view angle, sun direction and a wave peak mask"; foam "at wave peaks" and "around objects that intersect the water surface within a camera centered window using depth buffer comparisons", progressively blurred "with feedback to simulate the foam dispersing"; "calm water will only show foam generation around intersecting objects"; an area specular from the closest point on a sphere; a Snell's window from below. Waterfalls and streams project "the depth buffer from the perspective of the camera onto the surface of a mesh" so a character "intersecting with a waterfall" occludes it and a character in a stream has "foam interacting with their feet". | A stylised surface keeps two authored colours (deep, sub-surface) chosen by view and a peak mask, not a physical scatter; contact foam is a depth comparison, dispersed by a feedback blur (a temporal product); the falls sheet is a mesh with a projected depth to cut and foam it. | Opened (the two-page talk's text extracted). |
| Horizon Forbidden West water, Malan, SIGGRAPH 2022 Advances, <https://advances.realtimerendering.com/s2022/SIGGRAPH2022-Advances-Water-Malan.pdf> | Breaking waves as baked offline simulations deformed along a shore; "The water shader also needed info about where to generate foam, the relative height, and deformation strength; these are passed through the vertex color", so foam "can't create features smaller than triangles", with "foam popping" from undersampling; surface foam named as "another area we'd like to improve". | A warning for Puck's own habit: foam carried by geometry (a paint on a sheet, a vertex attribute) is undersampled and pops; foam belongs at shade time. | Opened (text extracted; the deck is image-heavy, so only its notes were read). |
| Water capabilities, Unity HDRP 17.1, <https://docs.unity3d.com/Packages/com.unity.render-pipelines.high-definition%4017.1/manual/water-capabilities-of-the-water-system.html> | Three surface types (pool, river, ocean); "HDRP treats Water surfaces as refractive transparent objects"; "Foam and caustics are monochrome"; "You cannot bake lighting for a water surface"; "Although water surfaces can receive Ray-Traced Reflections, they cannot contribute to them"; "Screen Space Reflection does not fall back to Reflection Probes underwater"; water misbehaves behind other refractive objects and under transparent depth prepass; no breaking waves, no vertical or spherical bodies; temporal anti-aliasing ghosts because water writes no motion vectors. | The second shipped system also stacks one refractive layer and lists glass-through-glass and temporal history as its limits, the same two the handbook lists for Puck's glass. | Opened. |

### Flowing water and waterfalls

| Source | What it does | What Puck takes | Audit |
|---|---|---|---|
| Water Flow in Portal 2, Vlachos, SIGGRAPH 2010 Advances, <https://www.advances.realtimerendering.com/s2010/Vlachos-Waterflow(SIGGRAPH%202010%20Advanced%20RealTime%20Rendering%20Course).pdf> | "Pixel shader flow, not geometric flow"; an artist-authored flow map of 2D vectors at "~4 texels/meter", combed in Houdini; the UV "is offset by the 2D flow vector scaled by time", and after Max and Becker "distortion looks reasonable for the first 1/3 of uv space", so "Blend the short animated segment in two layers. Each layer is offset half a phase so we can hide the restart"; repetition "solved by offset", pulsing "solved by noise"; normal strength scaled down by flow speed; cost over two scrolling normal maps: "Additional texture fetches: 2 - flow & noise. Additional arithmetic pixel shader instructions: 21" on ps2.0b and the Xbox 360; colour (debris) flows with the interval "from –fraction to +fraction so the peak of the interval is at zero"; future work lists "Render dynamic flow vectors per-frame so animated objects cause flow changes" and using "flow map with our physics simulation". | The whole of W2's kernel: two half-phase layers, a per-cell noise against pulsing, a speed-scaled normal, a different interval for colour than for normals. The cost on 2010 hardware bounds the Deck's. Puck's flow source is an authored field rather than a painted texture, which is Vlachos's own future work. | Opened (deck text extracted). |
| River water body, Epic, <https://dev.epicgames.com/documentation/en-us/unreal-engine/water-body-actors-in-unreal-engine> | A river is an open spline; per point a velocity ("Higher values control the speed of the water between each spline point", sign is direction), width and depth; rivers "do not drive surface motion using waves" but "use velocity from the individual points along the spline to drive motion"; "The velocity is written to a flow map that visually drives the flow of water along the surface"; all bodies render as one tiled water mesh "for smooth and seamless transitions between them"; a river "act[s] as connections between other water bodies". | The flow field as a derived product of an authored spline, not a painted texture: Puck's stream is already eight bearings and lengths in `.puck`, so the per-leg flow vector is authored data today. One shared surface geometry across bodies is M1. | Opened. |
| Wave particles, Yuksel, House and Keyser, SIGGRAPH 2007, <https://cemyuksel.com/research/waveparticles/> | "a simple, fast, and unconditionally stable approach to wave simulation"; "graphics hardware can be used to convert wave particles to a height field surface"; "appropriate for most fluid simulation situations that do not involve significant global flow"; wave forces on objects and waves from object motion; 170 fps on a GeForce 7900 GTX. | A ripple from a swimmer or a dropped stone is a particle whose footprint writes a height field, a presentation product a field row could hold; it does not give a river its flow. Watched behind W2 and W3. | Opened. |
| Waterfall in Season, realtimevfx.com, <https://realtimevfx.com/t/how-we-made-waterfall-in-season-a-letter-to-the-future/23420> | Separate opaque or masked meshes for body, highlight and foam ("Highlight and water body ... don't need to add WPO on it, since we have a Foam layer to hide edges"); downward motion as a world-aligned pattern with "a power on the V axis of the UV, with a value lower than 1" to suggest acceleration; splash particles from two cell noises in a radial mask with erosion in Niagara, "without much overdraw because it's a masked particle"; base foam from a soft hand-painted round texture; WPO toned down with distance. | A falls is an opaque sheet whose shading flows, with an authored acceleration curve along the fall and masked (not blended) particles at the foot. All of it is W2 plus W3 with an authored look. | Opened. |
| Season's named inspiration: Stylized VFX in RiME, Trümpler, <https://simonschreibt.de/gat/stylized-vfx-in-rime/>, <https://80.lv/articles/rime-breaking-down-the-stylized-water/> | The talk is video-only ("I can only offer a video and no text-version"); the pages give the edge as "a linear vertex-color-transition" reshaped in the material, the cascade and ripples as named topics, and the material package for the first level's sea. A secondary paper says RiME imitates Fresnel over "a simple, otherwise invisible cube map" and reflects no environment. | A stylised water reads with an authored edge ramp, a cube reflection and no geometry reflection: the W4a baseline suffices for that look. The techniques themselves are unverified here. | Pages opened; the talk not watched; the cube-map claim is a search summary. |
| Rendering Rapids / The Technical Art of Uncharted 4, Naughty Dog, <https://advances.realtimerendering.com/other/2016/naughty_dog/index.html> | The page lists the tech-art deck (wetness shading, mip fog, particle-driven interaction) and nothing on water; the rapids talk's slides were not found. A SideFX interview (search digest) describes a flow-map texture with "flow direction & intensity in RG channels, wave height in B channel" feeding a water uber-shader. | Nothing beyond the flow-map convention, which Vlachos already gives. | Page opened; the rapids content is a summary. |
| Water rendering in Far Cry 5, Cutocheras and Grujic, GDC 2018, <https://80.lv/articles/the-gdc-talk-water-rendering-in-far-cry-5/> | The agenda names "Single Frame Rendering" and "Optimizing with Half Precision Math" for "realistic water materials such as waterfalls and lakes". | A lead on half-precision shading for the Deck; nothing opened beyond the agenda. | Summary only (the GDC Vault talk was not opened). |
| Breath of the Wild | No primary source on its water rendering was found this round (CEDEC 2017's eight sessions cover design, sound and debugging in the translations found). | Nothing. | Not found. |

### GPU particles and volumes at fixed or low cost

| Source | What it does | What Puck takes | Audit |
|---|---|---|---|
| GPUParticlesCollisionSDF3D, Godot, <https://docs.godotengine.org/en/latest/classes/class_gpuparticlescollisionsdf3d.html> | "A baked signed distance field 3D particle collision shape"; SDF shapes "allow for efficiently representing approximate collision shapes for convex and concave objects"; resolutions 16³ to 512³ ("Higher resolutions have a greater performance cost and take more time to bake"), editor-only baking with a pre-baked `Texture3D` loadable at runtime; "hollow on the inside", with a `thickness` raised "to prevent particles from tunneling"; needs the rigid or hide-on-contact collision mode. | The shipped engine with an SDF collision proxy for GPU particles. Puck's proxy is derived from the bake's own lattices (M1), never a second bake, and its interior is signed, so no thickness hack. | Opened. |
| GPUParticles3D, Godot, <https://docs.godotengine.org/en/stable/classes/class_gpuparticles3d.html> | A fixed `amount` per emission cycle ("Changing this value will cause the particle system to restart"); `fixed_fps` with interpolation; a `visibility_aabb` outside which the system is inactive and "particle collisions and attraction only occur within this area"; up to four draw passes each with its own mesh; sub-emitters; `use_fixed_seed` so results stay "consistent across replays". | A fixed capacity, a visibility box that gates work (M4), a fixed step rate and a fixed seed are the shape of a presentation-only system that still repeats a capture. | Opened. |
| Initialize context, Unity VFX Graph 16, <https://docs.unity3d.com/Packages/com.unity.visualeffectgraph@16.0/manual/Context-Initialize.html> | Capacity is "The fixed amount of elements in the simulation. This count scales the memory allocation of the particle system"; elements past capacity are discarded; bounds are manual, recorded or automatic and feed culling. | The same fixed-capacity pool; a budget sizes it, never a growing list. | Opened. |
| Niagara GPU simulation, Epic | No official page on GPU collision was retrieved (the overview fetch returned an empty table of contents). Forum digests agree that GPU collision defaults to the scene depth buffer ("limited to the camera's view"), that "GPU Distance Fields" collide off screen and only for static meshes, and that fixed bounds gate culling, not collision. | The two baseline collision sources shipped engines offer: a depth pyramid (view-bound) and a distance field (static, off-screen). Puck has the first already (`sdf-mesh-pyramid`) and derives the second from the bake. | Summary only. |
| Physically Based and Unified Volumetric Rendering in Frostbite, Hillaire, SIGGRAPH 2015 Advances, <https://advances.realtimerendering.com/s2015/index.html> | A frustum-aligned froxel volume (aligned to 16×16 light tiles) holding per-froxel scattering and extinction, integrated once per frame and applied to every surface and particle; the course index was opened and names the deck (a 158 MB PowerPoint). | The unified alternative to Puck's per-pixel volume march: one grid every medium writes into and every pass reads. Watched: a second integrator beside `shadeVolumes` until many overlapping media make the per-volume march the larger cost. | Index opened; the deck not opened; per-froxel cost figures not found. |
| Claybook, Aaltonen, GDC 2018 | Fluid simulated and rendered on the GPU against a signed distance field world; the graphics plan's Codex synthesis opened the media PDF (a 1024×1024×512 8-bit world field, sparse 8³ tile updates). | The proof that an SDF world is a fluid's natural collider and renderer; a volumetric fluid itself stays out of this plan. | Summary only here; opened in the graphics round. |

### Reflections on the baseline

| Source | What it does | What Puck takes | Audit |
|---|---|---|---|
| Stochastic Screen-Space Reflections, Stachowiak, SIGGRAPH 2015 Advances, <https://advances.realtimerendering.com/s2015/index.html> | Glossy screen-space reflections by importance-sampled rays at half resolution with neighbour ray reuse, a tile classification at 1/8 resolution, and spatial and temporal filtering; shipped in Mirror's Edge Catalyst. | The shape of a baseline-hardware SSR and its dependency on a temporal filter, which Puck's resolve supplies for stationary pixels but not for the reactive pixels the glass pass marks. | Index opened; the deck (a 58 MB PowerPoint) and the EA pages (404) not opened; details are search digests. |
| Single Layer Water and Unity HDRP (above) | UE's water uses SSR with a fallback that drops it; HDRP's water receives SSR and ray-traced reflections but cannot contribute to them. | Every shipped water reflects by probes or sky first and adds a screen-space march where the hardware allows; none reflects water in water. | Opened (rows above). |

### What the sources settle

- **Every shipped water surface is one refractive layer over the lit opaque
  scene, with absorption per channel and an in-scatter term that lightens
  the medium with depth** (Single Layer Water's scattering coefficients and
  phase; Sea of Thieves' deep-versus-sub-surface blend). Puck's glass pass is
  that layer without the in-scatter, and both shipped systems list the same
  limits the handbook lists (one layer, no water behind refractive glass,
  temporal history over refraction).
- **Contact foam is the depth difference between the surface and the opaque
  hit, dispersed over time** (Sea of Thieves, Flick); foam carried by
  geometry undersamples and pops (Malan). Puck's glass pass holds the
  difference per pixel already; the dispersal is a filter on a product the
  resolve's history can hold.
- **Flow is a pixel-shader distortion of a pattern in two half-phase layers
  with noise against pulsing, costing two fetches and about twenty
  instructions on 2010 hardware** (Vlachos), and the flow field is a product
  of authored data, a spline's per-point velocity (Epic), not a painting.
  A falls is the same with an acceleration curve along the fall (Season).
- **A GPU particle pool is fixed-capacity, budget-sized, gated by a
  visibility box, stepped at a fixed rate, seeded for replay, and collides
  with a distance field when it must work off screen** (Godot, Unity, the
  Niagara digests). Puck's weather decision already says the first four;
  the research adds the collider, which Puck derives rather than bakes.
- **Reflections on the baseline are probes and sky first, a screen-space
  march where it pays, and never a second view per plane** (every system
  above; Stachowiak for the march's cost shape). Puck has the probe
  reflection for opaque surfaces and withholds it from glass.

## The options, weighed

Named by the mechanism each instances. The criterion is the plans'
(authors rule; the worst frame at the highest quality; one implementation
per mechanism; derived products as caches), judged on the Deck as well as
the 2060.

### Transmissive surfaces

1. **The glass pass gains a medium response read from the material** (W1):
   an in-scatter colour and coefficient (the medium's own light, added by
   `1 - exp(-σ_s · thickness)` under the surface's irradiance, the Single
   Layer Water form) beside the absorption that exists, and a contact
   response (an authored ramp over `opaque - t`, the Sea of Thieves and
   Flick form, writing a coverage the layer's colour fills) so foam, a wet
   edge or a caustic-like brightening at the bank is a material's
   parameter. Material rows grow by the medium's words; `SdfMaterialData`
   gains them beside `absorption` and `ior`; the engine default is zero
   scatter and no contact response, which is today's glass exactly. The
   stream, the lake and the falls move from `waterPalette` to rows with
   `glass`, so every water surface shares one path. **Adopted, W1.** Cost:
   a few ALU per glass pixel and one probe gather per glass pixel (W4a
   below); no new pass.
2. **A water shading path beside glass.** The water packet rules it out
   ("do not add a water-specific shader"), and every input it would need
   the glass pass has. **Rejected.**
3. **Two glass layers a pixel** (water seen through the inspection pane,
   glass behind glass). The packet's open item; the sums already integrate
   absorption through every layer, and only the reflection and refraction
   use the nearest. A second front face per pixel in the glass target
   doubles its words and the shade's reads. **Watched**: taken only when
   the pavilion's pane shows the reservoir wrong in W1's capture, and then
   as a second record in the same target, never a second pass.
4. **Caustics.** Every shipped system fakes them (UE's colour scale, HDRP's
   monochrome projection). The irradiance volume could carry a medium's
   refracted sun as its probes' light, which is correct and costs nothing
   per pixel, but needs the probe trace to see through glass, which G1
   changes. **Watched behind G1**; until then the contact ramp's brightening
   stands in, authored.

### Flow-driven shading

1. **A flow layer in the material layers** (W2): a new layer kind evaluated
   where `applyWeathering` runs, taking a flow vector in the layer frame,
   a speed, a pattern scale, an acceleration exponent along the flow, and
   two outputs the author selects (a normal tilt from the pattern's gradient
   and a coverage colour for streaks or foam); the lookup point is the hit
   displaced by the flow scaled by two half-offset phases, each layer's
   weight a triangle over its phase, with the per-cell noise Vlachos adds
   against pulsing. Patterns are procedural (`sdfPeriodicNoise3`, the
   pattern lattices weathering already has), so there is no texture to
   fetch and the cost is two noise evaluations and their gradient per
   covered pixel. The phase is one float per material or instance,
   host-integrated from the presented tick exactly as `SdfVolumeMotion.At`
   integrates a volume's drift, carried in the pass block for a global
   phase and in a render lane for a per-instance one, so no kernel reads a
   clock and lockstep and settled captures hold. **Adopted, W2.** The
   stream's slides, the sheet-riding foam paints and the per-leg bake groups
   go with it.
2. **The flow vector as an authored field row.** A `field` lattice row the
   author writes (or a derived row the hydrology's `StepFlow` publishes as a
   per-cell transfer, which the lattice computes today and discards),
   sampled by the layer through the brick the field emitter already
   uploads. **Adopted as W2's second source**, after the constant and the
   lane: it is what makes a river bend around a rock without an artist's
   comb, and it is a state product rather than a texture. Its cost is one
   brick sample per covered pixel.
3. **Flow by moving geometry** (today's slides and swings). Each moving
   sheet is a bake group and a transform row, the foam restarts with the
   wave, and the falls' fourteen sheets exist only to fold. **Rejected** as
   the way water moves; the rig keeps its uses (the glacier's size, a
   gate's swing).
4. **Height-field waves** (wave particles, an FFT, a Gerstner sum). A pool
   with a swimmer wants ripples, which wave particles give as a presentation
   height row. **Watched** behind W2 and W3; the stream and falls do not
   need them, and the lake is still.
5. **Tessellated or displaced flow** (Vlachos's future work). Puck's meshes
   are baked and the frame walks no field; a displacement in the vertex
   stage would lose the draw's box (the rendering skill's rule). **Rejected.**

### Particles and volumes

1. **The decision's pool, built on the frame graph with the exact field as
   its collider** (W3): one `SdfGpuWork`-style ring is the wrong home,
   since a pool steps once per presentation step for every view and its
   step must land before the frame draws it; it is a compute pass recorded
   before `views` in the root view's chain, reading the frame's presented
   tick for its fixed step, its capacity from the residency's budget (M4),
   its emitters and forces from a per-frame table packed like the volumes',
   its collider from a sampled brick derived from the bake's lattices for
   the static draws its region holds (M1; the field emitter's `Bake` is the
   CPU form of this derivation today and the one `sdfSampledRegion` reads),
   and drawn as instanced quads through the mesh vertex stage's counted
   indirect draw with its own fragment, after `views` and split by the glass
   target's depth so a particle behind the front face is refracted and one
   before it composes after the reflection, as the decision states.
   **Adopted, W3.** Its first user is the plunge pool's splash and the
   falls' droplets; the mist stays a cloud volume.
2. **Emitters and motion as authored programs.** The emitter shape is a
   paint's shape (the existing `SdfPaintShape` packing, evaluated by
   `evaluateShape`); the motion is a fixed set of generic primitives with
   authored parameters (flow, gravity, drag, collision response, age, size
   and colour over age). **Adopted for the first step**; a per-particle
   authored expression compiled for the GPU is a second VM beside the field
   interpreter and the lane evaluator, so it is handed to the author shaders
   memo as that memo's second case (an authored program that runs per
   element on the GPU).
3. **Volumes stay the medium; a froxel grid is watched.** The composite's
   per-volume march is correct and bounded by `steps × footprint`; a unified
   froxel grid (Hillaire) pays one integration for every medium and lets
   particles read it, but it is a second integrator. **Watched** until the
   debug world has more than a handful of overlapping media in one view.
4. **CPU particles.** The Niagara digests' fallback for rain under roofs.
   Puck's blockers are a directional map the weather decision already
   names; nothing steps on the CPU. **Rejected.**

### Reflections

1. **Glass reads the probe gather** (W4a): `sdfMeshProbesAt` then
   `sdfMeshReflection` in the glass pass, blended with the sky as the opaque
   path blends, under the material's roughness. One implementation; the
   cost is one gather per glass pixel, which the opaque path pays per pixel
   today. **Adopted, inside W1.**
2. **A bounded screen-space march in the glass pass** (W4b): a few taps
   along the reflected ray against the visibility record's depth, for the
   near geometry a probe blurs (the bridge, the pavilion, a swimmer), read
   from the lit image, falling back to W4a where it misses, at a step count
   the material authors. The reactive pixels the glass pass marks cannot
   lean on the resolve's history, so its noise is bounded by its tap count
   rather than filtered. **Adopted as a measured enhancement after W1**:
   lands only when a capture at the pool shows the bridge's reflection
   worth its taps on the 4070 and the count holds on the Deck.
3. **Planar reflections.** A second view per water plane, each a full
   chain. **Rejected** on the frame bar.
4. **Hardware ray-traced reflections.** Upper hardware only, behind G2's
   acceleration structures. **Watched** there.

## The recommendation, ranked

Ranked by what each step gives the water and the mechanisms the next step
builds on, with its expected cost against the bars. Costs are reasoned from
the code and the sources; none is a measurement, and each step's checks take
its own.

| Rank | Step | Mechanism | Expected frame cost | Memory and bake | What it proves on the debug world |
|---|---|---|---|---|---|
| 1 | **W2** ([as built](#w2-as-built)). The flow layer, with the constant and lane sources, and the stream, falls and lake moved onto it as static geometry | W2; M1 (the eight stream legs share one geometry) | Two noise evaluations and a gradient per covered pixel, on water pixels only (Vlachos's 2 fetches and 21 ALU on 2010 hardware); the mesh pass loses 8 moving groups and their transform rows | The stream's bake groups go from eight to one shared; the falls' fourteen sheets and the foam paints stop being animated groups; `world.budget` and `[sdf.mesh-bake] done` read the cut | The stream flows and the falls fall in a presented burst with no draw record changing; a settled capture is still; lockstep stills repeat across two Worlds; the meadow's wind shimmer uses the same layer with a wind vector |
| 2 | **W1.** The medium response on the glass pass (in-scatter, contact ramp) and W4a (probes on glass), with every water surface on `glass` rows | W1, W4 | A probe gather and a few ALU per glass pixel; no new pass | None; the water's four field surfaces already bake as glass | The pool reads turquoise at depth and clear at the margin, with foam at the steps and the bridge piers; the falls sheet transmits the rock; the hall's windows and the lift's glass reflect their rooms; `world.counters gpu pass sdf.world$glass-shade` before and after |
| 3 | **W3.** The particle pool with an authored emitter and generic motion, collided against a brick derived from the bake, drawn as quads split by the glass target | W3; M1 (the brick), M4 (capacity and the visibility box) | A fixed step over the pool's capacity and a fill-rate bounded by sprite size; the Deck decides the capacity the standard chooses | A pool's buffers at its capacity; a brick per region at the bake's cell | Splash at the plunge pool and droplets at the falls' foot, refracted where they fall behind the surface; leaves in the forest from the same pool; a presented burst at `debug.go pool` with `host.frames` before and after on both backends |
| 4 | **W2's field source.** A flow row sampled through the field brick, and the hydrology's derived transfer row | W2 on M3 | One brick sample per covered pixel | A row per lattice | The stream bends around the bridge piers with no authored comb; `world.fields` shows the derived row |
| 5 | **W4b.** The bounded screen-space march on glass | W4 | The authored tap count per glass pixel; measured first | None | The bridge reflects in the pool at the 4070's bar; the Deck's count decides whether the standard enables it |

W2 is first because it removes work the water pays today (moving groups,
per-leg bakes, foam as geometry) while adding the mechanism the meadow and
the forest want next, and because it changes nothing in the glass path W1
then edits. W1 follows and is small once the surfaces are glass rows. W3 is
third because its decision is settled and its first users (splash, leaves)
are ready, but it is the only step that adds a pass and a pool, so it
follows the two that cost nothing structural. The field source and W4b are
enhancements each gated by a capture.

**Risks.** W2's phase must never come from a wall clock: a kernel reading
the Frame group's `time` breaks settled captures and lockstep, so the phase
is host-integrated and carried as the volumes' is, and the lockstep check is
the step's first. W2 on a sampled field surface reads a flat top's normal;
the layer's tilt is the only normal motion, and a too-large tilt on a still
pool will read as noise, so the tilt is scaled by the flow speed as Vlachos
scales it. W1's in-scatter under the surface's irradiance needs a light
value the glass pass does not shade today (it reads no probes): W4a supplies
it, so W4a is inside W1 and not after it. W1 moving the stream and falls to
glass rows takes them out of the cascades and the irradiance trace (glass
casts no shadow): a falls that no longer shadows its wall reads wrong under
a low sun, and the step's capture decides whether the sheet keeps an opaque
twin for the cascades or the transmission plan's peel takes it. W3's brick
derived from the bake's lattices is narrow-band: a particle far from any
surface reads the lattice's far bound, which is enough to fall through air
and collide on arrival, but a brick cell coarser than a splash's travel per
step tunnels, so the step length is bounded by the cell (Godot's thickness
rule, derived rather than authored). W3's pool runs on the frame thread's
recording like every pass, so its step cost is a frame cost; its capacity is
the budget's, never a cap that grows. W4b's taps on a reactive pixel cannot
be filtered by history; the count bounds the noise, and the capture decides.

## Implementation briefs

Each brief names the outcome, the invariant and what enforces it, the
mechanisms it reuses and may not duplicate, the order, and the mechanical
check that shows the step worked. A brief grants no GPU leg; the lead
schedules legs under `puck lease gpu`. Every look stays authored: the
standard package (`worlds/standard/water.puck`, `foliage.puck`) chooses the
water's and the meadow's values, and every engine default is the neutral one
that reproduces today's image.

### W2 as built

Steps (1) and (2) of the flow layer are in; the field source (3) remains,
briefed below.

**What landed.** A palette slot's `flow` (`PaletteFlowDocument`, lowered to
`SdfFlow` on `SdfMaterial.Flow`) packs material rows 27 to 30
(`SdfProgram.PackMaterials`; the stride is `SdfProgram.MaterialVectorsPerEntry`)
and runs `sdfApplyFlow` (`shade/shade-layers.hlsli`) in the views pass's light
stage after `applyWeathering`: the weathering's lattice noise with an analytic
gradient (`sdfFlowNoise`), advected along the velocity's tangential part in
two layers half a cycle apart with triangle weights, a low-frequency
per-place phase offset against pulsing, the blend renormalized so contrast
holds through the cross-fade, a stretch along the flow, a coverage that
reveals an authored surface through `sdfRevealSurface`, and a normal tilt
scaled by the flow's share along the surface. Unresolved pattern fades toward
its mean by the pixel footprint, as the weathering does. Sources: the material
constant, and a render lane scaling it (`lane`). An `Object` velocity rides a
slot's frame, or turns by the draw's matrix on a draw shading in world space
(a static copy); a `World` velocity keeps the pattern in world space too.

**The phase invariant and its door.** A flow layer's image at a presented
tick is a function of the document and that tick alone. The phase enters the
kernels only through the pass block's `flowClock` (`SdfWorldPackage.FlowClock`),
which `SdfFrameBlock` integrates from `SdfFrame.Clock` with
`PresentedTick.Integrate`, as `SdfVolumeMotion.At` does for volumes, reduced by
`SdfFlow.ClockPeriodSeconds`; the material carries a whole number of cycles a
clock period (`SdfFlow.CyclesPerClock`), so the layer joins where the clock
wraps. A program with no flow (`SdfProgram.HasFlow`) writes zero, so a still
world's views stand; the clock is a lighting member of the cadence signature
(`SdfWorldTables.Cadence.cs`), so the shading passes rerun as the flow moves
and the geometry, the cascades and the surface pass stand. No `sdf-*` kernel
reads the Frame group's `time` or `timeDelta`: `puck search -M 0
"frameGroup\.(time|timeDelta)" src/Puck.SdfVm/Assets/Shaders/Sdf` finds
nothing. Where it stops: a lane-driven flow is as deterministic as the lane's
expression, and a lane reads zero on any placement without lanes (static and
animated placements supply none), so the per-instance phase the brief
imagined for the stream legs has no carrier there; the world-space pattern
static copies shade in makes it unnecessary, since no two legs repeat.

**The water on it.** In the standard (`worlds/standard/water.puck`), the
stream segment is one still sheet wearing `streamFlowLayer`; its slide, its
brush-edge paints and its foam paints are gone (a paint on a static placement
is world-space, so they could not ride a shared copy). The debug world places
the one `stream` prototype eight times, alternate legs 2 cm higher so the
overlapping ends at each bend never share a plane, a shorter leg starting back
along the leg before it. The falls is fourteen still sheets wearing
`fallsFlowLayer`, one downward velocity that runs down every sheet at its own
slope; it lost the fold by `meltStrength` and keeps the visibility park. The
lake keeps its level slide, a state gauge's legitimate rig use, and wears
`lakeFlowLayer`, world-space breeze ripples. The meadow's grass
(`windMeadowTile` in `foliage.puck`) wears `meadowWind`, a world-space flow
whose paler gust patches cross every tile and blade together.

**Not yet.** The glass pass does not run the layer, so a water surface on a
glass row (W1) needs `sdfApplyFlow` in `sdf-glass.comp.hlsl` before the stream
moves to glass. The falls no longer narrows as discharge falls: a coverage or
tilt driven by the gauge needs a lane, and an animated placement supplies
none.

### W2's field source

**Outcome.** The flow vector read from a field row the state system owns: a
`field` lattice row the author writes, or a derived row the hydrology's
`FieldLattice.StepFlow` publishes as its per-cell transfer, sampled by the
layer through the brick the field emitter already uploads, after the constant
and the lane. **Invariant.** As W2's: the row is state, so the image stays a
function of the document and the tick. **Reuse.** The field emitter's brick,
`sdfSampledRegion`, `sdfApplyFlow`. **Check.** The stream bends around the
bridge piers with no authored comb in a presented burst; `world.fields` shows
the derived row.

### W1. The medium response on glass

**Family and model.** A Claude `specialist` or Codex Sol: the result is
specified and the pass is one file, but the material pair and the probe
gather's register cost in the glass kernel want reading on both backends.

**Outcome.** `SdfGlass` gains a medium: a scatter colour and coefficient
(the medium's in-scatter, `scatter · (1 - exp(-σ_s · thickness))` under the
irradiance the probe gather gives at the surface) and a contact response
(an authored ramp over the distance from the surface to the opaque hit,
writing a coverage the material's coverage colour fills before the
transmission). The glass pass reads the probe gather (`sdfMeshProbesAt`,
`sdfMeshReflection`) for its reflection as the opaque path does, blended
with the sky by roughness. Every water surface in the standard wears a
`glass` row: `waterPalette[0]` gains `waterMaterial`'s glass and the medium
the standard chooses, so the stream, lake and falls transmit. Defaults are
zero scatter, no contact response, and the sky-only reflection where no
probe answers, which is today's image.

**Invariant and enforcement.** A glass pixel's transmitted light is read
once, from the lit image at the refracted point, and absorbed over the sums'
thickness; the medium response adds to it and never reads a second image.
Enforced by the pass's structure: there is one `sdfGlassLitAt` read per
pixel (or the frost's disc), and the contact ramp and in-scatter are
functions of `thickness`, `opaque - t` and the gather alone. Where it stops:
glass behind glass still reads the opaque image behind the nearer pane (the
handbook's limit; option 3 above).

**Reuse, no second mechanism.** The glass passes as they stand; the probe
gather the views pass uses; the material packing and the sync pair; the
standard's palette. No water shader, no second glass layer, no new pass.

**Checks.** Settled captures at `debug.go pool` from the path's end and from
the bridge on both backends: turquoise depth, a clear margin, foam at the
steps and piers, the falls' rock showing through its sheet, the hall's
windows reflecting their rooms; the reservoir through the pavilion's pane
(decides option 3); `world.counters gpu pass sdf.world$glass-shade` texels
and `world.gpu-timing` for the glass passes before and after on the 4070;
the register count of `sdf-glass.comp.hlsl` read in RenderDoc or PIX on
both backends beside the views kernel's; the lockstep still.

### W3. The presentation particle pool

**Family and model.** Codex Astra at high effort or a Claude `specialist`,
with a principal-grade review of the invariant before the kernels are
written (an invariant-heavy implementation, and a new pass with GPU
lifetimes, which [hazards](../development/hazards.md#silent-invariants)
marks review-only).

**Outcome.** A region-owned GPU pool per the weather decision: a compute
step recorded before `views` in the root chain at the presented tick's fixed
rate over a budget-sized capacity, emitters as authored shapes (the
`SdfPaintShape` packing, a rate and a seed), motion as generic authored
primitives (a flow vector or the W2 field row, gravity, drag, a collision
response against a sampled brick, age, size and colour over age), and a draw
of instanced quads through the mesh vertex stage's counted indirect draw
and a fragment of its own, split by the glass target's front-face depth so
a particle behind the front face enters the lit image before `glass-shade`
and one before it composes after. The brick is derived from the bake's
lattices for the region's static draws (M1), never a second bake; the
step's length is bounded by the brick's cell. The standard authors
`impactSplash`; `impactSpray` stays the cloud volume. A forest leaf-fall
and a meadow pollen drift are the second users.

**Invariant and enforcement.** The pool's state is a function of the
document, the presented tick and the seed, and nothing in simulation reads
it. The first half is enforced by the step reading only the per-frame table
the host packs (emitters, forces, the tick) and the brick, with the seed
hashed per particle (`sdfPcg3d`) and no wall clock; the second by the pool
living in `Puck.SdfVm` with no path into the state mirror, as the volumes
do. Where it stops: a presented capture is still frame-order dependent in
the quads' blend, as any transparent draw is.

**Reuse, no second mechanism.** The mesh vertex stage and counted indirect
draw for the quads; the glass target for the split; `SdfPaintShape` and
`evaluateShape` for emitters; the volumes' per-frame table packing pattern
for the pool's table (`PackVolumes`); `sdfSampledRegion` for the brick's
read and the field emitter's `Bake` as the derivation's CPU form until a
GPU derivation exists; `GpuRegion` for the pool's buffers under every
policy; the residency's byte budget for capacity. No second scheduler, no
`SdfGpuWork` client, no froxel grid.

**Order.** (1) The pool, the table, the step with gravity and age, the quad
draw after `views`, no collision; the splash. (2) The brick derivation and
the collision response. (3) The glass-target split. (4) The W2 field row as
a force; the forest leaves.

**Checks.** A presented burst at `debug.go pool` shows the splash with
`gpu.draws` counting the one indirect draw and `world.counters gpu` the
pool's step as one dispatch; `host.frames` before and after on both backends
at the pool and at the forest; the pool's bytes in `world.residency`; a
particle dropped on the plunge basin's steps rests on them (the brick) and
one behind the pool's surface refracts (the split); the capture gate's taint
rules see the quads as any surface.

## Owned questions

- **The water's own share of the mesh region and the glass passes' time
  at the pool** are readings W2 and W1 take first, since no commit isolates
  them; the brief's "about 1 GB" stands as a lead until then.
- **Whether a falls sheet on a glass row needs an opaque twin for the
  cascades** is W1's capture under a low sun; the
  [transmission plan](transmission.md) owns the peel that would make it
  unnecessary.
- **The per-particle authored program on the GPU** is handed to the
  [author shaders](open-items.md#author-shaders) memo as its second case;
  W3's generic primitives are not a substitute for it, only the first step.
- **A consensus check of this round's citations by another family** (Codex,
  opening the Vlachos, Sea of Thieves and Epic sources again, and the two
  2015 decks this lane could not open) is the lead's to schedule before W2
  is briefed; nothing above rests on a summary row alone.
- **Whether the hydrology's per-step transfer should be a published row**
  is rank 4's question and a state-system change (`FieldLattice.StepFlow`,
  `m_flowDelta`); it stays a count of what the row would hold until W2's
  constant and lane sources are in.

---

[Plans](README.md) · [Graphics architecture](graphics-architecture.md) · [The debug world as one valley](debug-world-composition.md) · [Open items](open-items.md#water) · [Decisions](../decisions/rendering.md)
