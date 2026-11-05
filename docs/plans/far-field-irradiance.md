# Far-field irradiance: what a coarse probe level may answer

The design memo for the gallery's far third: why the 8 m probe level draws pale
triangles and dark wedges on a far wall, what shipped engines and the literature
do about coarse probes indoors, the Puck-native options weighed beside them, and
the one approach this memo recommends with its implementation brief. It extends
the [global illumination decision](../decisions/rendering.md#global-illumination)
and the [illumination plan](illumination.md); the
[mesh-GI reference](../../.claude/skills/rendering/references/mesh-gi.md) owns
the gather's current contract and the fixed poses.

## The problem

The mesh path's indirect light is a probe clipmap about the camera: the finest
level at the tier's spacing (`SdfMeshIrradiance.Layout.Of`: 1 m over 48×16×48
cells at High, 2 m over 32×12×32 at Medium), then two 32×12×32 levels of 8 m
and 32 m cells (`SdfMeshIrradiance.LevelSpacing`). A receiver reads the finest
level whose probes measured it, a finer level answering past its window as far
as its surface maps reach (`SdfMeshIrradiance.SurfaceReachOf`: 1.5 cell
diagonals plus the normal bias, 2.65 m at High), then the level whose window
holds it; where that level's measured probes answer nothing, the same probes
answer unbounded, then the finest level's boundary probes unbounded
(`SdfMeshIrradiance.Select`, `sdfMeshProbesAt`).

In the gallery hall (`worlds/debug/hall.puck`: storeys 4.5 m, slabs and walls
0.3 m), the far third seen from the east end at High reads the 8 m level
([open items](open-items.md#lighting-and-gi): `body.pose 64 4.57 28.52 273`,
`view.override camera look 68 6.8 28.52 10 6.0 28.52`, `world.debug-view
indirect`; `world.probe 10.6 6.5 28.5 1 0 0` reads `level=1`). Medium shows the
same past about 32 m. The brief's reading, that the 8 m cells span storeys and
both sides of walls, is right, and a bounded measurement says exactly how.

### The measurement

A scratch harness over `SdfMeshIrradiance` (the CPU trace, no GPU) traced a
four-storey hall like the gallery's at the High tier: storeys 4.5 m over 0.3 m
slabs, 12 m wide, 70 m long with the camera at one end, 2 m doorways in both
side walls on every storey, over a sunlit ground. It then read
`DescribeProbes` at 1,328 receivers in the far 32 m (floors, slab undersides,
side walls and the end wall on every storey) and classified every corner by its
weight, its probe's storey and the exact `reference-clear` segment test the
report carries. What it showed:

- **Storeys without a level-1 probe plane get no measured coarse answer.** The
  8 m level's probe planes stand at y = 4 and y = 12, inside storeys 0 and 2.
  Every floor and slab underside on storeys 1 and 3 (192 of 192), and 221 of
  256 wall receivers there, fell through the coarse level to the finest level's
  boundary probes (`level=0 measured=0`): the "measured room's light" read from
  25 m away, flat.
- **The coarse answers those storeys do get are leaks.** The 53 wall and
  end-wall receivers on storeys 1 and 3 that read `level=1 measured=1` read one
  corner in 41 cases and two in 10, probes in storey 0 or 2 across a slab, at
  total weights as low as 0.012 normalized to the whole answer. Of 1,542
  answering corners over all far receivers, 54 were exact-blocked, every one
  standing in another storey than its receiver: 32 at map visibilities from
  0.12 to 0.49, the soft half of the coverage ramp where a tap that saw the
  slab still reads partly visible, and 22 at visibility 1, where none of the
  four taps saw the slab at all. 22 corners of 1,948 exact-clear ones were
  over-occluded (weight zero at visibility zero).
- **Where the storey has a probe plane, the coarse level answers correctly but
  thinly.** Storeys 0 and 2 read 2.2 corners on average with a mean total
  weight 0.25 (365 of 717 measured reads below 0.25): the cage's other plane is
  rightly blocked, and a slab underside 0.2 m above the plane reads it at a
  facing weight of 0.007.

So the pale triangles are single leaking corners, from probes in the wrong
storey, most admitted at the soft end of the coverage ramp
(`SdfMeshIrradianceProbe.DepthCoverageStart`, `DepthCoverageScale`: a tap
half blocked still reads visible, which a 1 m probe needs for thin objects and
an 8 m probe cannot afford), the rest through taps metres wide at 8 m that
straddle the slab without a hit, and all normalized to 100 % because the
gather takes the first level with any weight. The dark wedges are the same cage's measured
corners where one blocked plane and one visible plane meet across a 1 m
receiver footprint 8 m wide. And the structural fact under both: an 8 m level
has a probe plane in every other storey, so for half the storeys no probe of
that level stands in the receiver's room at all.

## What shipped engines and the literature do

Every source below was opened and read (a PDF's extracted text, a page, or a
shader file); none is taken from a search summary.

| Source | What it does about coarse probes and thin geometry | What Puck takes from it |
|---|---|---|
| DDGI, Majercik, Guertin, Nowrouzezahrai and McGuire, JCGT 8(2) 2019, <https://jcgt.org/published/0008/02/01/paper-lowres.pdf>, §5.2 | Backface-culls probes below the receiver's tangent plane with a soft falloff; a perceptual weight crushes very low irradiance ("less than 5 % of the representable intensity range"); mean- and variance-biased Chebyshev visibility at 16×16 depth; a normal-and-view bias off the surface. Its GTC 2019 slides (Majercik, "Irradiance Fields", <https://developer.download.nvidia.com/video/gputechconf/gtc/2019/presentation/s9900-irradiance-fields-rtx-diffuse-global-illumination-for-local-and-cloud-graphics.pdf>, "Reading irradiance") show the gather's last line: the Chebyshev-weighted answer is blended toward the answer without visibility by `saturate(irradiance.a)`, the total visible weight, so seven blocked probes never normalize one leaking probe to the whole; and "Use fewer probes vertically ... two probes per room vertically" with "Self-shadow bias must be tuned to geometry thickness" under limitations. | The blend by visible share, with a better fallback than the unoccluded answer. The admission that probe density must match rooms. Puck already measured and rejected Chebyshev radial moments ([illumination plan](illumination.md#rejected-and-watched)). |
| Scaling DDGI, Majercik, Marrs, Spjut and McGuire, JCGT 10(2) 2021, <https://ar5iv.labs.arxiv.org/html/2009.10796>, §4.1, §5, §6, §7.3 | One self-shadow bias (0.75 × spacing × 0.3 along 0.2 n + 0.8 v); probes classified Off, Sleeping, Awake, Vigilant, "inside" at over 25 % backface hits, relocated through the nearest backface over five iterations; cascaded volumes whose weights fall linearly to zero over the last cell, accumulated densest first "After the weight total reaches 1.0, further volumes are skipped". Nothing on storeys or thin walls beyond "probes stuck in walls" and "The acute corner leads to shadow leaking". | Puck's placement and inside test already cover relocation and classification (`SdfMeshIrradiance.Place`, `InsideShare`). The cascade handoff, densest first until the weight fills, is the share-blend rule stated for level boundaries. |
| SDFDDGI, Hu, Yip and others, <https://ar5iv.labs.arxiv.org/html/2007.14394>, §3.1, §3.4, Table 1 | Replaces RTXGI's depth visibility with per-pixel sphere traces through a hand-authored analytic SDF (43 primitives for Sponza), one pixel per 2×2 block and duplicate tests removed, "from 8 to 4 on average" tests per thread; 1.67 ms total on an RTX 2080 Ti at 1080p. Claims leaking "completely solved". | Exact segment visibility per receiver removes angular aliasing. Its SDF is authored by hand and the frame walks it, which Puck's frame does not ([decision](../decisions/rendering.md#the-frame-does-not-interpret-sdf-programs)). |
| Lumen, Wright, Narkowicz and Kelly, SIGGRAPH 2022 Advances, <https://advances.realtimerendering.com/s2022/SIGGRAPH2022-Advances-Lumen-Wright%20et%20al.pdf> (notes) | Irradiance fields rejected for the final gather: "irradiance was calculated at the probe, not the pixel, which causes leaking and over-occlusion and makes the probe placement very difficult". Screen probes on visible surfaces trace short rays; the world-space radiance cache answers only where a screen probe's ray misses, as a clipmap with sparse coverage and an indirection. Surface-cache probe interpolation weights by plane and by the probe's own depth map. A mesh SDF cannot hold a wall thinner than a voxel: "even a single ray going through a wall can cause massive light leaks", fixed by a half-voxel expand that "causes over-occlusion", traded deliberately for diffuse rays. "Volumetric caches have issues with thin walls." | A coarse volumetric probe must not be asked for near visibility. Where it cannot resolve, over-occlusion is the safe side for diffuse light. |
| Godot SDFGI, <https://godotengine.org/article/godot-40-gets-sdf-based-real-time-global-illumination/> and `servers/rendering/renderer_rd/shaders/environment/{gi,sdfgi_preprocess}.glsl` on `master` | Cascades of voxel SDFs with a probe grid; "As long as walls are thicker than a voxel for a given cascade, light won't go through"; an optional per-probe occlusion of 8 octants, 4 bits each, flood-averaged through voxel solidity in `sdfgi_preprocess.glsl` and multiplied into the trilinear weight in `gi.glsl` (`weight *= max(occlusion, 0.01)`); the first cascade whose window holds the point answers, blended into the next over its last cells. | Occlusion as spatial connectivity per probe octant, a 4-bit cost; the wall-thickness rule stated as a condition on the cascade, not on the test. |
| Assassin's Creed Shadows, Leblanc and Conte, SIGGRAPH 2025 Advances, <https://advances.realtimerendering.com/s2025/content/Advances%202025%20-%20Raytracing%20the%20world%20of%20Assassin's%20Creed%20Shadows.pdf> | Five cascades of 16×16×8 probes from 2 m to 32 m "Following camera with forward 3d offset", a 20×20 octahedral G-buffer per probe relit without retracing, variance-shadow depth for interpolation weights, relocation by backface count, an indoor flag. Then, under "Light leaks": "Visibility test is not precise enough", "Outdoor probes are many factors brighter than indoor", so probes and secondary hits are classified against authored indoor volumes ("List of planes already available from baked GI") and outdoor probes are kept out of indoor receivers. | The strongest recent shipped evidence: 400 rays and variance depth at 2 to 32 m spacing still leak, and the fix was room segmentation. The forward window offset is a cheap mid-field gain. |
| The Last of Us Part II, Doghramachi, SIGGRAPH 2020 Talks, <https://s3.amazonaws.com/nd.images/research/2020_siggraph/LightingTechTlou2.pptx>, slides 39 to 47 | Per probe, closest-hit distances projected onto an oriented six-face cube (8 bytes), compared with the receiver's distance through a Hermite ramp; artists "Adjust probe locations" and "Tweak occlusion scale and bias" where the compressed occlusion fails. | A six-face occluder depth is the coarsest form of Puck's surface map; it too needed hand fixes. |
| Treyarch, Hooker, SIGGRAPH 2016 Advances, <https://www.activision.com/cdn/research/Volumetric_Global_Illumination_at_Treyarch.pdf> | "Light leaking is a problem"; the normal-weighted trilinear of Silvennoinen 2015 "needs to be more reliable"; planes and a signed distance field per voxel gave "Bad artifacts"; the shipped answer is artist-shaped convex volumes ("Click to size boxes"), voxels near walls, invalidation near geometry, "Careful lighting artistry". | Room segmentation by hand is the historical answer and the cost Puck refuses. |
| Call of Duty: Infinite Warfare, Iwanicki and Sloan, SIGGRAPH 2017 Advances, <https://advances.realtimerendering.com/s2017/Precomputed%20Lighting%20in%20CoD%20IW_20_PPS.pdf> | A tetrahedral light grid; "if we just interpolate the lighting between the probes the lighting will bleed through walls", so each probe stores a triangular depth map toward the opposite face of each tetrahedron, the barycentrics of the first hit. Probe generation reuses artists' manually placed volumes. | Visibility stored per probe per interpolation cell, in the cell's own frame. |
| Quantum Break, Silvennoinen and Timonen, SIGGRAPH 2015 Advances, <https://www.advances.realtimerendering.com/s2015/SIGGRAPH_2015_Remedy_Notes.pdf> | "we multiply the trilinear interpolation weight with a geometry term, which takes the surface normal into account"; automatic probe placement that maximises visible surface area and minimises distance to surfaces. | Puck's facing term. Surface-driven placement is the long-term shape of a sparse volume. |
| Local reconstruction from sparse radiance probes, Silvennoinen and Lehtinen, SIGGRAPH Asia 2017, <https://arisilvennoinen.github.io/Projects/RTGI/index.html> | Mutual visibility between receivers and a small, constant number of nearby probes is precomputed into each receiver's reconstruction filter; 3.9 ms with dynamic lights. | Receiver-side precomputed visibility is the exact form of what a coarse level lacks, at a precomputation Puck does not have. |
| Unity Adaptive Probe Volumes, <https://docs.unity3d.com/Packages/com.unity.render-pipelines.high-definition@17.0/manual/probevolumes-concept.html>, <https://docs.unity.com/en-us/engine/current/manual/lighting-overview/lighting/probevolumes/fixissues/light-probe-validity>, <https://docs.unity.com/en-us/engine/current/manual/lighting-overview/lighting/probevolumes/fixissues/troubleshoot-light-leaks>, <https://docs.unity.com/en-us/engine/6000.6/manual/lighting-overview/lighting/probevolumes/options-override-reference> | Bricks of 4×4×4 probes at 1, 3, 9 or 27 m, "small bricks with a short distance between Light Probes" where there is more geometry; a probe is invalid when its rays hit backfaces; Virtual Offset moves its capture point, Dilation copies valid neighbours into invalid probes; at runtime a Leak Reduction Mode warps the sample "to try to have invalid probe not contributing" (Performance) or samples "between 1 and 3 times" (Quality); Rendering Layer Masks "ensure interior objects will never sample lighting data from exterior probes"; Probe Adjustment Volumes invalidate probes by hand, which "solves most light leak issues, but often not all". | Geometry-driven density is the structural answer; the rest is a ladder of mitigations ending in authored masks. |
| Radiance cascades, Osborne and Sannikov, <https://arxiv.org/html/2408.14425v1>, §2.3 to §2.6; Sannikov's repository <https://github.com/Raikiri/RadianceCascadesPaper> | Spacing doubles and interval length grows with the cascade; angular resolution rises so a coarse cascade never integrates the near interval; the bilinear fix retraces each interval from the four coarser probes it merges into ("4× increase in the number of rays"), and "light leaking artefacts are typically an indication that the penumbra criterion is violated"; in 3D "eight samples of cascade i+1 are needed per sample of cascade i". | Why a coarse level must not answer near visibility; the register already sets dense 3D cascades aside. |
| Wright, SIGGRAPH 2015 Advances, <https://advances.realtimerendering.com/s2015/DynamicOcclusionWithSignedDistanceFields.pdf> | Cone traces through per-mesh and global distance fields; "Thin surfaces can only be represented with a negative texel inside"; the global SDF "is inaccurate near surfaces", so object SDFs serve the start of a cone; 3.44 ms at 1080p on a Radeon 7870 for sky occlusion, up to 7 ms of global SDF updates on a teleport. | A volume SDF at wall resolution over a 256 m window is not a baseline-path cost. |
| GI-1.0, AMD, <https://ar5iv.labs.arxiv.org/html/2310.19855>, §2.2, §2.4.1 | A screen cache and a world hash-grid cache keyed by position, direction and level; leaks across cells are cut by hashing whether the ray is shorter than the cell; screen probes farther than the cell size from the pixel's plane weigh zero. | A plane-distance rejection per probe is cheap and exact for flat receivers. |
| Frostbite, O'Donnell, GDC 2018, <https://media.contentapi.ea.com/content/dam/eacom/frostbite/files/gdc2018-precomputedgiobalilluminationinfrostbite.pdf> | Lightmaps and irradiance volumes path traced offline; invalid texels (inside geometry, "visible back faces") are filled from valid neighbours. Nothing on probe visibility at runtime. | Dilation of invalid probes, which Puck does not need: an invalid probe takes no part. |

Two sources the brief named have no primary text to cite: the Far Cry 3 and
The Division GDC pages (<https://www.gdcvault.com/play/1015326/>, the second
URL the Antigravity file gives resolves to a different talk) carry only
abstracts, which say nothing about probe spacing or leaks.

## The Antigravity synthesis, checked

An Antigravity lane researched the same question independently, and the lead
merged its synthesis into this lane's branch. Its own policy note is honest:
every external source is marked `summary-only`, and it says so. Each citation
was then opened for this memo:

- **Held, with the file's reading confirmed:** DDGI 2019 (the backface, crush
  and Chebyshev terms; the file's "weight-crushing threshold" is the GTC
  slides' `if (weight < threshold) weight *= square(weight) / square(threshold)`,
  threshold about 0.2, which the JCGT text describes as the perceptual
  low-irradiance weight); Scaling DDGI 2021 (relocation, classification,
  cascades; its "spring-like displacement" is the file's wording, the paper
  moves a probe through the closest backface); the Lumen 2022 deck (short
  screen-probe rays, world cache as fallback, plane and depth-map weighting;
  the file's "0 .. 2 m" matches the deck's "solving lighting further than two
  meters"); Wright 2015; Unity's brick and validity pages; Godot's article and
  docs; Sannikov's repository exists but its README holds none of the claims
  the file attributes to it, which come from the Osborne and Sannikov paper
  above; Split Radiance Cascades (the arXiv abstract confirms a sparse hashmap
  of world-space probes and ray splitting, not the "depth-aware bilateral
  merge" the file describes); GIBS 2024 (already cited in the illumination
  plan).
- **Held in part:** Assassin's Creed Shadows 2025. Real and important, but the
  file's numbers are wrong in detail: the deck says 5 cascades of 16×16×8
  probes at 2 to 32 m, a 20×20 texel G-buffer, G-buffer updates taking "~16 %
  of the remaining budget" and lighting 84 %; it says nothing about shoji or
  paper walls, and its conclusion is that the visibility test "is not precise
  enough" so authored indoor volumes were needed, which the file omits.
  Infinite Warfare 2017: the slides exist at the course site (the file cites
  the index), and the mechanism is a triangular depth map per probe toward
  each tetrahedron face, not the "separating plane or per-corner visibility
  bitmask" the file describes. Modern Warfare 2020: only a 73 MB PPTX exists on
  the course page; unopened, so its claims stand unverified.
- **Wrong or unreachable:** the Unity 6000.0 `probevolumes-leak-reduction`
  URL (404; the leak page is the troubleshooting page above, and its runtime
  modes are None, Performance and Quality, not "Validity Based"); the EA SEED
  URL for O'Donnell 2018 (404; the EA Frostbite PDF above is the source); the
  Division GDC URL (a Skylanders talk); the RTXGI page (it names SHaRC in one
  sentence and says nothing about hash keys); Cupisz 2012 (the abstract says
  nothing about walls or leaks); Far Cry 3 (the abstract says nothing about a
  4/8/16 m hierarchy).
- **Its readings of Puck's code:** the level table, reach, `LevelSpacing`,
  `MediumDims`, `Clearance`, `BehindShare` at coarse levels, `Select`'s
  first-level-wins loop, `world.probe`'s `reference-clear` and the hall's
  authored dimensions are right. Wrong: `ProbeSurfaceSide`, `FillSurfaceMaps`,
  `SurfacePushMax`, `MaxOffset = 0.45f * spacing`, `InsideLimit`, `KeyMask`,
  `sdfMeshProbeGatherLevel`, `sdfMeshProbeDepthOctUv`, `sdfMeshProbeDepthPatch`
  and `SdfInstanceRange` fields in `SdfMeshIrradianceScene` do not exist (the
  real names are `SdfMeshIrradianceProbe.DepthSize`, `SdfMeshProbeDepth.Surfaces`,
  `NormalBias`, `Place`'s `reach = 0.5f * spacing`, `InsideShare`, `KeyOf`,
  `sdfMeshProbesGather`, `sdfOctEncode` and `sdfMeshProbeDepthVisibility`);
  the line numbers it gives for the shader and `Select` are off by tens of
  lines; and the "12 unused bits in word 0" claim is wrong, since `KeyOf`
  packs three 10-bit coordinates into bits 0 to 29 and the two flags take 30
  and 31, so a new per-probe field needs a word of its own. Its ranking is
  reasonable where its facts hold and this memo reaches a close conclusion by
  its own route; nothing below rests on it.

## The options, weighed

The brief named five; the round added four. Each is judged by whether it fixes
the cause the measurement names, what it costs per frame on the RTX 2060 and
per bake, what it changes in the CPU/HLSL pair, and whether it is Puck's to
own.

1. **Blend the coarse answer toward the fallback by visible share.** Fixes the
   normalization of one leaking corner to the whole and the hard cliff between
   "one corner answers" and "none does". Does not stop the corner leaking in
   the first place (a 1.2 % share of outdoor light on an indoor wall is still a
   pale patch) and does not give storeys 1 and 3 any coarse answer. Per frame:
   one more gather on far-field pixels whose share is below one. **Adopted, as
   the gather's rule for every level boundary.**
2. **A storey-resolving level.** The measurement's structural finding: no
   gather rule lights a storey no coarse probe stands in. An isotropic 4 m
   level resolves 4.5 m storeys by coincidence (planes at 2, 6, 10, 14 m) and
   not 3 m ones; a 2 m vertical spacing holds one plane in every storey of
   2.4 m or more, which is what DDGI's "two probes per room vertically" and
   Godot's Y scale set by hand. Per bake: probes added to the level; per
   frame: nothing. **Adopted, as per-axis level spacing with the tier's table
   as data.**
3. **SDF-traced receiver-to-probe visibility for coarse levels, per pixel.**
   Exact where its field resolves the wall. The frame walks no field
   ([decision](../decisions/rendering.md#the-frame-does-not-interpret-sdf-programs)),
   so this means a volume SDF cache: at the 0.15 m voxels a 0.3 m slab needs,
   the 8 m level's 256×96×256 m window is about two billion voxels, and at voxels the
   memory allows the slab vanishes and Lumen's half-voxel expand over-occludes
   everything else. Eight marches per far-field pixel in the views pass, the
   hottest kernel, and a CPU twin marching the same volume for parity.
   **Rejected.** Exact segment visibility belongs at trace time, where the
   scene's BVH already answers it (`SdfMeshIrradianceScene.Unblocked`).
4. **Per-probe validity from the SDF.** A scalar "this coarse probe stands in a
   region larger than its cell" removes partitioned probes from indoor use and
   sends every far indoor receiver to the fallback: no leaks, no structure.
   Puck's hit buffer already classifies inside probes (`InsideShare`); a
   partition test from the same buffer is a heuristic between "a plane below"
   (outdoors on the ground) and "a room around" with no clean threshold.
   **Rejected as a scalar; adopted per direction** (option 6).
5. **Room or portal segmentation of probe volumes.** What Treyarch, Unity's
   layer masks, Call of Duty's volumes and Assassin's Creed Shadows all ended
   on. It works, and it is artist time per leak, which DDGI set out to remove
   and Puck's rule forbids ("a leak is a defect, not a style"). Puck could
   derive enclosure from the field instead of asking an author, but the
   receiver's class is not known per pixel at a far receiver without the
   finest level, which is the case at hand. **Rejected as the fix; left open as
   an authored override** (an `indirect` region an author may still want).
6. **Conservative coverage at coarse levels.** The measured leaks all sit in
   the soft half of the coverage ramp, where a tap half blocked reads visible.
   That ramp is the finest level's (a 1 m probe's taps are 0.5 m wide and must
   not let a railing shadow a floor); an 8 m probe's taps are 4 m wide and any
   measured hit before the receiver is a slab or a wall. Coverage thresholds
   per level, conservative past the finest, cost nothing and close the 32
   measured leaks in the soft half of the ramp; the 22 whose taps never saw
   the slab are option 1's to bound and option 2's to remove. **Adopted.** Its
   over-occlusion is bounded by the harness: 22 of 1,948 clear corners today
   at the soft ramp; the conservative ramp's figure is a check the
   implementation runs before choosing it.
7. **Higher angular resolution for coarse surface maps.** Assassin's Creed
   Shadows at 20×20 still needed indoor volumes; a 24×24 map on 24,576 coarse
   probes is 4× the rays and about 40 MiB more; the texel at 8 m is still
   2.7 m. **Rejected**: it lowers the leak rate without changing its kind.
8. **A per-probe visibility lattice** (a bit per finest-level cell in the
   coarse probe's footprint, exact at trace time). Exact where it is sampled,
   614 bytes and 4,913 rays per 8 m probe, 34× the hit buffer's rays.
   **Rejected** for cost while the trace is on the CPU; worth a look once the
   trace is on the GPU ([illumination plan step 3](illumination.md#the-build-order)).
9. **A sparse, geometry-driven finest level** (Unity's bricks, Lumen's
   indirection, Split RC's hashmap): spend the finest level's probes where
   surfaces are and none in open air. Indoors every cell is within reach of a
   surface, so it extends nothing in the hall; outdoors and underground it
   frees most of the window. **Left open** as the storage redesign the
   illumination plan's GPU trace step can carry.
10. **The window ahead of the camera** (Assassin's Creed Shadows' forward
    offset). At Medium a 64 m window centred 24 m ahead covers the whole hall;
    at High it covers 25 m of the far third. Turning the camera then moves
    the window by its offset and retraces what it uncovers. **Left open**, as a
    measured trade once the trace is budgeted on the GPU.

## The recommendation

Three changes, each one mechanism, landing in this order. Together they make
the far field read what its probes measured, in proportion to how much of the
receiver's support they measured, and give every storey a probe plane.

**Invariant.** A level's probes answer a receiver only in the share of the
receiver's front support they measured clear; the rest of the answer comes
from the next stage of the chain, never from normalizing the share up. The one
place that enforces it is the gather pair, `SdfMeshIrradiance.Select` and
`sdfMeshProbesAt`, which every read goes through (the views pass, the CPU
bounce reads, the meter and `world.probe`); the device law holds the pair to
one answer. The guarantee stops where the surface maps stop: a receiver a
coarse probe's tap cannot distinguish from the slab in front of it reads that
probe's answer scaled by a conservative coverage, so the error is darkening,
never a leak from another room.

### 1. Conservative coverage past the finest level

`SdfMeshProbeDepth.Visibility` and `sdfMeshProbeDepthVisibility` take the
coverage ramp (`DepthCoverageStart`, `DepthCoverageScale`) as arguments of the
level; the finest level keeps today's values, and every coarser level reads a
ramp that closes a tap on any measured hit before the receiver. Over-occlusion
at the coarse levels is then the safe error (Lumen's trade for diffuse rays),
and the fallback chain lights what it darkens. No record changes.

### 2. Share-blended level selection

`Select` and `sdfMeshProbesAt` stop choosing the first level with any weight.
The chain stays as it is (finer levels within reach, the holding level
measured, the holding level unbounded, the finest level's boundary unbounded),
and each stage contributes its **share**: the stage's total weight over the
same cage's weight before visibility (trilinear × facing), clamped to one, of
what the earlier stages left. The last stage takes all that remains. The
receiver's probe set becomes two cages, the chosen stage's and the remainder's,
whose weights are pre-scaled by their shares so `sdfMeshIrradianceAt`,
`sdfMeshReflection` and `sdfMeshLightVisibility` sum sixteen corners as they
sum eight today; `measured` becomes the chosen stage's share, so a traced
light's visibility scales with it. The GTC slides' crush of near-zero weights
is not needed on top: step 1 closes the soft band that produced them.

### 3. A storey-resolving coarse level

`Layout` carries a spacing per axis and dims per level, read from the tier's
table rather than two constants (`LevelSpacing`, `MediumDims`), and the pass
block carries three spacings per level. The default table keeps the finest
level and makes the 8 m level `(4, 2, 4)` m over `32 × 24 × 32` cells (a
128 × 48 × 128 m window, 24,576 probes), leaving the 32 m level as it is:
12,288 more probes than today, 11 MiB more table and 26 MiB more CPU cache
at either tier, a fifth more trace and relight work, and the band from 64 m
to 128 m outdoors read by the 32 m level instead of the 8 m one. The
alternative that keeps that band, `(8, 2, 8)` m over `32 × 48 × 32`, costs
36,864 probes (60 % more work) and is the table to try if the overlook's
mid-field shows the change. Which table a world uses belongs in authored data
with these as the engine's defaults (AGENTS.md: the standard is authored, with
a neutral engine default); the lane decides the home with the
`render.indirect` settings that already name the tier.

What each axis of a spacing means to the code: `Lattice`, `Beyond` and
`sdfMeshIrradianceBeyond` divide per axis; `SurfaceReachOf` becomes 1.5 cell
diagonals of the vector spacing (today's 1.5 √3 s is that for a cube); the
facing fade's half spacing becomes half the cell's extent along the receiver's
normal; `Place` searches a cell whose half extent is per axis; `Affected`'s
placement region and `WindowLow`'s centring take the vector.

### Cost

- **Per frame, RTX 2060 at 1080p.** Steps 1 and 3 add nothing to a pixel.
  Step 2 adds a second eight-probe gather on the far-field pixels whose chosen
  stage's share is below one (today those pixels already run the chain until a
  stage answers), and sixteen probe slots in the gather's live state instead
  of eight; the views pass's occupancy is the figure to read
  ([gpu cost](../../.claude/skills/rendering/references/gpu-cost.md)). Expected
  under a tenth of a millisecond at the hall's far-field share of the screen;
  measured by the lane.
- **Per bake and per window move.** Step 3's 12,288 probes trace 144 rays
  each, a fifth more than today's 61,440 at High; the relight budget covers a
  fifth more probes a sweep, so the age counter is the thing to watch
  (`world.budget`, `[sdf.mesh-gi] behind`). The Deck's four cores carry the
  same fifth; the GPU trace step is where that cost goes.
- **Memory.** 11 MiB of table and 26 MiB of CPU cache at both tiers for the
  default table; none for steps 1 and 2.

### The CPU/HLSL pair and the parity law

The pair stays a pair: `SdfMeshProbeDepth.Visibility` with
`sdfMeshProbeDepthVisibility` (step 1's ramp arguments), `Gather` with
`sdfMeshProbesGather` (unchanged but for the vector spacing), `Select` with
`sdfMeshProbesAt` (step 2's shares), `Layout` with the pass block's level rows
(step 3's spacings, a [sync pair](../../.claude/skills/rendering/references/sync-pairs.md)).
`SdfMeshIrradianceGatherDeviceLawTests` grows from eight corners to sixteen
and from a `measured` bit to the chosen stage's share: every receiver must read
the same sixteen slots with weights within its tolerance and the same share on
both backends; its hall gains a storey with no coarse probe plane under today's
table and a far receiver on it, so the law covers the share blend where it
matters, and a receiver whose coarse corner leaked under the soft ramp, so the
law covers step 1.

### How it proves itself in the debug world

Captures settled (`world.wait bakes`, `[sdf.mesh-gi] done`), `world.indirect
high` and `medium`, both backends, `world.debug-view indirect` and the lit
view:

- the brief's pose (`body.pose 64 4.57 28.52 273`,
  `view.override camera look 68 6.8 28.52 10 6.0 28.52`): the far third's
  floors, slab undersides and west wall continuous, no triangle brighter than
  its surround, no wedge darker, each storey's ceiling darker than its floor;
- `world.probe 10.6 6.5 28.5 1 0 0` and the same on each storey's floor at
  x = 12: `level=1`, two or more answering corners, every answering corner
  `reference-clear=1`, and no corner with a weight above zero at
  `reference-clear=0` anywhere along `x = 12 .. 36` at the hall's three
  storey heights;
- the fixed poses in the [mesh-GI reference](../../.claude/skills/rendering/references/mesh-gi.md#debug-world-corner-and-boundary-checks)
  and its gallery isolation checks compared tolerantly with today's stills, so
  the finest level's reads are untouched;
- the outdoor control and `debug.go overlook`: the valley's mid-field under
  the default table compared with today's, the `(8, 2, 8)` table tried if it
  darkens or flattens;
- `world.counters sdf.mesh-gi` and `world.budget`'s age line across the day
  cycle, no `behind` line that today's run lacks; `world.gpu-timing` of the
  views pass against today's on a quiet machine, and `host.frames` at the
  pose.

### What it leaves open

- A sparse finest level and the window's forward offset (options 9 and 10),
  both to be measured once the trace is on the GPU.
- An authored `indirect` region as an author's override (option 5), only if a
  world asks for it.
- The coarse levels' angular resolution (option 7) if a far atrium taller than
  its probe planes still shows a seam after step 3.
- The exact-visibility lattice (option 8) as the coarse levels' test once rays
  are a GPU budget.

## Implementation brief

For one lane, in `src/Puck.SdfVm`, `src/Puck.SdfVm.Model`, the irradiance
shader and the two test projects that hold the laws. Each step has the check
that shows it worked; the lane runs the scratch harness described above as a
law in `tests/Puck.SdfVm.Tests` (a four-storey hall, far receivers, counts of
exact-blocked answering corners and over-occluded clear corners) and keeps it.

1. **Coverage per level.** Add the coarse ramp beside the finest one in
   `SdfMeshIrradianceProbe` (generated into `sdf-render.hlsli`), thread the
   ramp through `Visibility`/`sdfMeshProbeDepthVisibility` from `Gather` and
   `sdfMeshProbesGather` by level. Check: the hall law's exact-blocked
   answering corners at the coarse level read zero and its over-occluded clear
   corners stay within a few percent; the existing laws in
   `SdfMeshIrradianceLawTests` (the sparse sphere, the thin slab) unchanged,
   since the finest ramp is untouched.
2. **Share-blended selection.** Rewrite `Select` and the rolled loop in
   `sdfMeshProbesAt` to accumulate stages by share into two cages;
   `SdfMeshProbes` and `Corners` carry sixteen; `measured` becomes a share.
   Check: the device law over sixteen slots and the share on both backends;
   `world.probe`'s first line prints the chosen stage's share beside `level=`.
3. **Per-axis level table.** `Layout` as a table of (spacing xyz, dims) per
   level per tier with today's values, then the default table above;
   `WriteMeshIrradiance` and the pass block's level rows carry the spacings;
   `Lattice`, `Beyond`, `SurfaceReachOf`, the facing fade, `Place`, `Affected`
   and `WindowLow` take the vector. Check: `puck mesh identity --against` shows
   the bake's bytecode unchanged (the irradiance shader is outside the bake's
   closure); the hall law reads `level=1` with answering corners on every
   storey; `world.budget` prints the new counts and bytes; the debug-world
   captures above.
4. **Documents.** The [mesh-GI reference](../../.claude/skills/rendering/references/mesh-gi.md)'s
   level and gather paragraphs, the [GI decision](../decisions/rendering.md#global-illumination)'s
   "Coarser levels supply reach" paragraph, the handbook's volume section and
   the far-field item in [open items](open-items.md#lighting-and-gi), each
   rewritten to the landed rule; then this memo's recommendation section is
   deleted and the decision cites the research table that stays here.

---

[Plans](README.md) · [Illumination](illumination.md) · [Rendering decisions](../decisions/rendering.md#global-illumination)
