# The GPU frame's budget

The owner's ruling is that an RTX 4070 has no reason to strain on the debug
world, a few rooms, a valley and exhibits, and that the per-pixel pipeline is
redesigned to a budget, not shaved pass by pass, so there is headroom for a
bustling city of hundreds of lights and thousands of movers
([AGENTS.md](../../AGENTS.md#change-is-the-default)). This memo sets that budget,
says where the frame stands against it, names the design that meets it and
orders the work. It owns the [rendering plan](rendering.md)'s main-view item;
the [illumination plan](illumination.md) keeps the lighting decisions it cites.

Every reading here is attribution from one lane's windowed Worlds beside other
lanes' work on the same machine: it says where time goes, never whether a bar
is met. Bar readings are the lead's
([how the bars are read](streaming-and-memory.md#how-the-bars-are-read)).

## The budget

The target is a GPU frame of 8 ms at 2560 by 1440 on the RTX 4070, Direct3D 12,
with every pixel shaded (`renderScale: 1`), and under 1 GiB of per-pixel
storage for the main view. Half of the 60 Hz period is then free for content
the debug world does not hold.

| Stage | Passes | Now, ms | Budget, ms |
|---|---|---|---|
| Geometry | `mesh`, `pyramid` and the occlusion kernel between the mesh phases | 0.8 to 2.4 | 1.2 |
| Surface | `surface`: normals, ambient occlusion, cascade shadows | 1.9 | 1.0 |
| Indirect light | `views-probes` | 2.0 to 3.6 | 0.4 |
| Light selection | `views-filter`, `views-bins` | 0.95 | 0.4 |
| Shading | `views` | 1.9 standing, 2.5 moving | 1.6 |
| Ink | the edge search in `views-filter`, the ink phase in `views` | about 0.9 (scaled, below) | 0.4 |
| Glass | `glass-clear`, `glass`, `glass-shade` | 0 to 0.9 | 0.4 |
| Reconstruction | `resolve` | 1.9 moving, 2.5 standing | 0.7 |
| Sky and display | `sky`, `composite`, the bloom pyramid, `display` | 1.5 | 0.9 |
| Other views and the root | nested views, `main`, the environment | 1.5 to 4.1 | 0.7 |
| The world's shadows | the residency's upload | 0.5 | 0.3 |
| **Frame** | | **13 to 16** | **8.0** |

The main view's rows are `world.gpu-timing` at the stage with the camera
standing and the route's chapter means moving; the others are the route's.

Ink's share is taken from shading, where the outline it replaced read its
neighbours. Its row is the difference `SDF_VIEWS_OMIT_INK` makes
(`passes/sdf-views-split.hlsli`) outside the gallery, the Moth and the
gallery's walls inked, in a window of 1280 by 900: `views-filter` 0.56 ms
against 0.36 ms and `views` 0.95 ms against 0.87 ms, so about 0.2 ms of search
and 0.07 ms of phase over 1.15 million pixels, which is about 0.65 ms and
0.25 ms at 2560 by 1440 if both follow the pixel count. It is over its share,
and the search is where: it loads four tile entries a pixel where the filter
alone loads two, each with a read of its draw's record for the object key,
seeds two pairs an entry through group atomics, and scans up to sixteen mask
lines a pixel in a seeded tile. Decoding each entry's normal once did not move
the reading, so the arithmetic is not the cost; the next cut is the memory
traffic (the key in the visibility record with the bake's part word, or seeds
written by the surface pass, which already holds each hit).

Per-pixel storage of the main view at 2560 by 1440 (`host.memory families 0`):

| Storage | Now, MiB | Budget, MiB | How |
|---|---|---|---|
| Stationary window (`historyWindow`) | 369 | 250 | Three words a phase as built (`sdfPackWindowSample`); two needs the transport kept once a pixel, below |
| Probe records (`viewsProbes`) | 68 | 80 | A cell of four pixels a side with interpolation, as built |
| Probe histories (`viewsProbeHistory`, `glassProbeHistory`) | 118 each | 60 | One world-space cache a residency in place of a history a view |
| Glass sums, target, depth and lit image | 340 | 40 | Allocated for the tiles glass covers |
| History surface and colour | 227 | 120 | Motion and identity in the sample record |
| Visibility records, mesh target and depth | 186 | 150 | A draw and triangle in one word of the target |
| Lit images, sky runs, transport, reactivity, filter | 330, and by arithmetic 28 for the samples' air (two words a render pixel, unread) | 200 | Sky runs at half size; transport from the sample record |
| The root's frame (`main`) | 230 | 60 | The stages are gone with the one placement pass; what is left is one published image a frame in flight |
| **Total** | **about 2,200** | **under 1,000** | |

## Where the time goes

Read on the stage with the camera standing, by live levers and by the views
kernels' compile-out switches (`passes/sdf-views-split.hlsli`), one at a time
against the same frame:

- **`surface`**: about half is the cascade shadows (`world.shadows off`), a
  quarter the ambient occlusion (`world.ao off`), and a fifth the record's
  load, normal and store.
- **`views-probes`**: a third remains with `world.indirect off`, which is the
  classification of each cell's four pixels and the state it writes. The rest
  resolves each gather into a slot: sixteen corners of 25 words each, for the
  representative and for each pixel that gathers for itself. Moving through
  foliage, where many pixels gather for themselves, it is the largest pass
  after views.
- **`views-filter`**: two fifths is the tiles' light masks
  (`SDF_VIEWS_OMIT_LIGHTS`), the rest the occlusion and shadow filter, and in
  a view that inks the edge search beside them (`SDF_VIEWS_OMIT_INK`).
- **`views`**: a fifth with the shading compiled out
  (`SDF_VIEWS_OMIT_SHADING`: the load, the hit door, the store). Of the rest,
  the light walk (`SDF_VIEWS_OMIT_LIGHTS`) and the reflected environment
  (`SDF_VIEWS_OMIT_REFLECTION`) are the two large parts and the material layers
  (`SDF_VIEWS_OMIT_MATERIALS`) the small one.
- **`resolve`**: standing, every pixel sums its window; `world.temporal off`
  removes the pass and a third of the probe lookup with it.
- **`main`**: the root places every view and pane in one pass
  (`RenderGraphPackageCatalog.PlaceViews`, `WorldRootGraph`), which writes each
  output pixel once and draws nothing while one view stands for the world, so
  `world.counters gpu node main` reads no dispatch in single-seat play. Before
  it, each of the layout's eight views had a pass of its own that copied the
  whole image whether or not the view was shown: 0.8 ms.
- **Render scale**: at `world.render-scale 0.75` the main view costs about
  seven tenths of its full-rate time. Every pass before the resolve follows the
  samples; the resolve does not.

Two conclusions follow. The frame is already a visibility buffer with deferred
shading: the mesh target holds a draw and a triangle, the surface pass writes a
record a pixel, and views shades records (`SdfWorldPackage.NativeFragment`). No
candidate replaces that shape. What costs is that a shading kernel which does a
lighting input's work itself runs it for every wave that holds one pixel
needing it, at the occupancy its largest input leaves it
([gpu cost](../../.claude/skills/rendering/references/gpu-cost.md#registers-set-occupancy-occupancy-is-not-speed)),
and that each input keeps storage sized for its worst pixel.

## Where the memory goes

The route's worst reading was not a pass's cost. In the garden painting chapter
a nested view's `surface` pass read 21 ms, where the same kernel shades the
whole display in under 2 ms. The device had run out of local memory. The
process's ledger (`host.memory`'s `pool device`) peaked past what the operating
system budgets the process (`segment local`'s `budget`), and from then a pass
that touches memory the system has moved waits for it, whichever pass that is.
Over the route on both backends and both loops, only the chapter whose peak
crossed that budget read so; on a smaller window, or with the peak under the
budget, the same walk reads like every other chapter. The paging itself was not
captured, so the mechanism is an inference from that correspondence. A pass
that reads far above its kernel's cost is read beside `host.memory` first.

The RTX 4070's profile authors a device budget of 8 GiB (`deviceMemoryMiB`).
Storage that a view or a world cannot render without is granted past the limit
(`over-limit-grants`), so the budget did not hold: the families below
(`host.memory families 0`, MiB) held 5.8 GiB at boot and 8.3 GiB by the second
loop, under 10.4 GiB of device blocks, and a resize or a second large view on
top of that crossed what the system gives the process.

| Owner | Boot | Second loop | What it is |
|---|---|---|---|
| Mesh regions | 2,430 | 2,100 | The debug world's meshes, resident twice at about 1 GiB each |
| The main view | 2,020 | 2,020 | The storage table above |
| Irradiance volumes, traced scenes and programs | 620 | 1,370 | One set a residency |
| Nested views | 0 | 1,050 | A window once walked up to kept a display's worth |
| Other worlds' shadows and views | 0 | 950 | Three entries, spare pairs and glass extinction a residency |
| The world's shadows | 335 | 370 | |
| The root | 250 | 250 | |
| Room in device blocks no family names | 700 | 2,000 | Blocks the churn of resizes and shadow layers leaves part filled |

Four causes. This memo owns the per-pixel storage a view holds (the table
above and the first cause); [the memory plan](streaming-and-memory.md#the-build-order)
owns residency, the mesh budget and shadow allocation, which the other three
are evidence for.

1. **A view out of sight kept the storage of the extent it was last seen at.**
   A window fills the display as a body walks through it, and nothing shrank it
   afterwards, since a view no schedule reaches renders nothing. Such a view
   now renders at its least share until its node installs that extent
   (`WorldViewGraphHost.ReserveShrinks`).
2. **A world no window shows was resident at full detail.** A session view no
   fit measures demanded meshes at its whole image from wherever its camera
   stands, so the debug world's second residency, seen through a window inside
   a painting, held the same gibibyte as the one the seat stands in, and the
   one detail bias coarsened both to fit. Holding such a view's demand to the
   display pixels it is presented over did not shrink that region
   (`world.residency`'s `region-named` read the same, above its
   `region-budget`, with the bias at its ceiling), so the lever is one
   residency a world, not demand
   ([nested views](streaming-and-memory.md#nested-views-fitted-windows-authored-quality-and-standing-feeds)).
3. **The debug world is resident twice.** A session whose stream is not what a
   whole reader is handed (`WorldSceneShare.SharesAuthority`) renders a scene of
   its own, and every such session of one world shares one
   (`WorldSharedScene`). That scene is a residency with its own mesh region,
   shadows and irradiance volume, though nearly all its meshes are the first
   residency's: the library shares a mesh on the host (`SdfMeshLibrary`) and
   each residency places its own copy on the device (`SdfMeshRegionArena`).
4. **Resizes and shadow layers churn the blocks.** A window's extent moves in
   sixteen steps an octave (`RenderGraphExtent.StepsPerOctave`), each a whole
   new graph beside the old one until it retires, and a pressure trim gives
   back shadow layers the next frame takes again (`world.residency`'s `shadows`
   line counts the `trims`, and `host.memory`'s `over-limit-by` names the
   layers granted past the limit). The window's part is this memo's: an
   allocation at the octave with the render grid moving inside it, as the main
   view's does, once the storage table's rows shrink what an octave holds.

## The design

**Each lighting input is resolved once, by a lean kernel of its own, into a
compact record a shading sample reads whole. The shading kernels hold no
input's machinery.** A wave pays for every lane's path, so an input whose slow
case lives in the shading kernel taxes every pixel near one that takes it; an
input resolved ahead costs its own pixels, in a kernel whose registers are its
own. The same rule sizes storage: a record holds what a sample reads, and what
only some pixels need lies in a pool, not in every pixel's record.

| Input | Resolved by | Record |
|---|---|---|
| Indirect light and traced-light visibility | `views-probes` | A slot: blended cubes, the lights' agreement, the lights the corners disagree on (`sdf-views-probes.hlsli`) |
| Lights that reach a sample | `views-filter`, by cluster | A list of light indices a cluster, any count |
| Shadow visibility | `surface`, after a classification by each layer's block bounds, which the shadow upload reduces (`sdfMeshShadowClass`) | The record's shadow word |
| The reflected environment | The environment node | A prefiltered map, one lookup a roughness |
| Reconstruction's sample (motion, identity, air) | The views pass's epilogue | One record a render pixel; the air is built (`SdfWorldPackage.SampleAir`), motion and identity are step 5 |

Indirect light is the first input built this way. Before it, 1.6 ms of the
views pass at the stage was the code that read a gather's sixteen corners,
which only the pixels with a gather of their own and the lights the corners
disagree on needed.

### Options weighed

- **A visibility buffer with deferred material evaluation.** The frame's shape
  today. Kept.
- **A lean G-buffer.** The visibility record is 32 bytes and is the G-buffer.
  A material G-buffer between the surface phase and the light phase would let
  the two run as separate kernels, which is where the shading kernel's last
  registers go; it needs an authored program's state (`SdfShadeStamp`) to cross
  a pass, so it follows the light lists, when the light phase is the only large
  part left.
- **Indirect light from a world-space cache.** A gather is already valid for
  any receiver within the normal bias on the same plane (`sdf-probe-reuse.hlsli`),
  so the history entry is a world-space cache entry addressed by screen
  position. A hashed table a residency would serve every view and survive
  disocclusion, and removes a history a view. It does not shorten the pass: a
  cell still validates an entry and resolves a slot. It is the memory step for
  nested views, after the coarser lookup.
- **Indirect light at a coarser rate with interpolation.** A cell of four
  pixels a side, each pixel blending the four cells about it by the reuse
  test's weights, quarters the lookup and its storage, and replaces most extra
  slots, since a pixel across an edge from its own cell's representative is
  usually compatible with a neighbour's. A slot is 28 words to blend, where a
  gather was sixteen corners.
- **Merging passes.** The root's placement (eight passes for one view, now
  one) and the filter's light tiles are the two that pay. `views-bins` and the
  pyramid cost under 0.2 ms together.
- **Fewer samples.** Rendering at three quarters and reconstructing is how most
  shipped renderers spend a 1440p frame, and the mechanism exists
  (`renderScale`, the temporal resolve). It is an authored value of the quality
  table, and it does not make a city's lights cheaper. Ruled out for the bar:
  the frame bar is read at the highest quality at native resolution, so the
  RTX 4070's table keeps a scale of one and the budget is met at full rate.
- **A governor.** `WorldDynamicResolution` already moves the render grid inside
  the ceiling's allocation without a rebuild, and no table turns it on. With
  `world.render-scale auto on` at the lobby it held a grid of seven eighths and
  a median interval of one refresh. Its GPU signal holds the views' time to the
  whole display period within a tenth (`WorldDynamicResolution.Deadband`), so
  it rests above the period as readily as below. Ruled out as a pacing guard:
  the frame bar is met by the architecture, never by lowering quality, so no
  table of a bar device turns it on.

### Lights at a city's scale

The lights table holds `SdfLights.MaxLights` records, and every structure that
selects lights is a mask of that many bits: a view's (`SdfWorldPackage.LightMask`),
a tile's (`SdfWorldPackage.ShadeTileLightWords`), a probe's traced lights
(`SdfMeshIrradianceProbe.LightWords`) and a slot's agreement. Cost follows the
lights a tile keeps, which is the right shape, but nothing can name a light
past the table. The replacement, in the order that keeps each step runnable:

1. **Lists for the walk.** The filter pass writes each cluster's lights as
   indices into a list buffer (a count and an offset a cluster, the entries
   packed after a prefix sum, as `views-bins` does for tiles); the walk reads
   the list. Clusters split a tile by depth, as the tile's two boxes do
   (`sdfViewsTileLights`). The table grows to any size, and a cluster pays for
   its own lights.
2. **Selection ahead of the tiles.** At 500 lights a tile cannot test every
   light. The host bins lights into a world grid as it stages them
   (`SdfLightSelection`), and a tile tests the lights of the cells its boxes
   meet.
3. **Traced visibility without a bit a light.** A probe keeps the nearest
   traced lights by index with a visibility each, and a slot's agreement
   becomes the same list. A light past the list is unoccluded there and named
   in the counters, never dropped silently
   ([the light lists decision](illumination.md#5-light-lists-area-lights-and-budgeted-local-shadows)).
4. **Local shadows** for spot lights (headlights) from the budgeted atlas the
   same decision describes.

### Nested views by their footprint

A nested view costs its pixels plus a fixed part: a pass a part, each with its
barriers and binds, and the storage each part holds. Three things make the
fixed part larger than it should be. The display stage runs the whole bloom
pyramid for a view a screen films. A view keeps a probe history of its own, so
a view rated to a few frames a second gathers again each time. And the mesh
pass selects from the world's whole draw list. The steps: a nested view's
fragment takes only the parts its row enables; its gathers come from the
residency's cache; and the selection of a view that covers few pixels starts
from the cells its frustum meets.

## The steps

Each step lands with the check that shows it worked, read at the stage standing
and over the route, before and after, in one World session each.

The device fits its budget first, since a frame over it reads as a stall in
whichever pass touches moved memory ([where the memory goes](#where-the-memory-goes)).
The memory plan owns that; this memo's share of it is one step:

- **Windows allocate by octave.** The render grid moves inside the allocation,
  so a window grows without a second graph beside the first. Check: over the
  route the `session` family's `allocated` stays within twice its `peak`.

  The mechanism exists for the main view and does not carry over as it stands.
  A view's graph allocates at its ceiling and records each frame at a grid
  inside it (`IShaderPipelineRenderExtent.CeilingAt`, `FrameAt`), but a view
  whose grid is under its output runs the reconstructing fragment
  (`SdfViewSnapshot.Reconstructs`, `SdfWorldPackage.Fragment`), which upscales
  to the whole output. A window allocated at an octave that way would be
  resampled twice, once up to the octave and once down by its consumer, and
  its output-sized passes would run at up to four times the pixels its glass
  spans. Two shapes avoid that, and the step chooses between them by storage
  held and rebuilds counted over the route:

  1. The native fragment records at the grid into the corner of its
     allocation, and the consumer maps that part of the image, as it already
     maps the part of the glass a fitted image spans (`SdfViewSnapshot.ImageWindow`,
     which `SdfWorldPasses` writes for the sampling view). Every reader of a
     session image's whole extent takes the same mapping: the screen emission
     kernel, which sizes its grid by the image's dimensions, picking and the
     capture path.
  2. Fewer steps an octave for a window's allocation alone
     (`RenderGraphExtent.StepsPerOctave` divides every footprint in sixteen),
     with no grid inside. Two steps cut rebuilds eightfold with no kernel
     change, but a window then renders and holds nearly half as many pixels
     again as its glass spans on average, and over twice as many just past a
     step, which is the cost this memo exists to remove.

  The first is the step. The second is a fallback only if the mapping proves
  unworkable for one of those readers.

The route's report writes a chapter disturbed when the device's memory rose in
it to what the system budgets the process
([the benchmark](benchmark.md#it-plays-as-a-player-takes-it)), so a stall is not read as a
pass's cost. Then the time:

1. **The coarser lookup's time.** The lookup gathers a cell of four pixels a
   side and each pixel blends the four cells about it
   (`sdf-world-views-probes.comp.hlsl`, `sdfProbeBlend`); the records hold
   about 68 MiB at 2560 by 1440 by a reading of 21 MiB at 1280 by 900, and no
   cell shows in the hall or along the walk into it. Check, in the quiet
   window: `views-probes` under 0.8 ms at the stage and moving.
2. **Light lists.** As above. Check: the walk's visits a pixel are unchanged in
   the debug world; `SdfLights.MaxLights` is gone; a row of 512 point lights
   renders with each cluster's list holding only its own.
3. **The shadow classes' time.** A receiver's shadow is classified before it is
   filtered ([the reference](../rendering/sdf/reference/shading-ao-shadows.md#shadows),
   `sdfMeshShadowClass`): each layer pair keeps, for every block of 32 texels a
   side, the layer's fitted plane and the least and greatest depth off it over
   the block and the filter's reach about it, which
   `passes/sdf-shadow-blocks.comp.hlsl` reduces after each drawing into the
   pair, and the surface pass reads one record a receiver in place of the
   search's twelve taps and the filter's sixteen wherever the record proves the
   receiver lit or in umbra. The record is a plane's because open ground under
   a slanted sun changes depth across a block by more than any caster stands
   off it, so a plain depth bound never reads it lit. The bounds hold every
   texel within the filter's reach of the block, so one read serves a
   receiver; a whole layer's reduction reads each block's 62 by 62 texels.
   Check, in the quiet window: `surface` under 1.2 ms at the stage; a paused
   still under `world.debug-view shadow` unchanged against a reload with
   `SDF_SURFACE_OMIT_SHADOW_CLASSES` defined; and `world.residency`'s
   `shadows` line, whose `blocks…in…` counts say how many blocks each entry
   reduced in how many dispatches, beside the pass's measured time under a
   turning sun.
4. **A prefiltered environment.** The environment node writes the reflected
   sky, its panels included, at a few roughness levels; views takes one lookup.
   Check: `SDF_VIEWS_OMIT_REFLECTION` removes under 0.4 ms.
5. **A sample record for the resolve.** The air is built: views stores each
   render sample's air (`SdfWorldPackage.SampleAir`, two words) from the
   atmosphere it evaluates once a pixel for the transmittance, and the resolve
   scales it by the sample's coverage, where it evaluated the atmosphere again
   for each of the 169 samples a group of 64 pixels gathers. What is left:
   views writes each render pixel's motion and identity beside it, so the
   resolve reprojects through no mesh table (`sdfPreviousPoint`) and the
   history surface drops to four words. Check: `resolve` under 1.2 ms moving,
   and the history surface holds four words.
6. **One probe cache a residency.** The hashed table above. Check: no view
   holds a probe history; a nested view rated to five frames a second gathers
   only what entered it.
7. **The root's four-seat placement, read.** The placement pass is built and
   read with one seat, where it draws nothing. Its several-source path (four
   seats, a pane beside a view) has not run in a window: no console route
   joins a second seat without devices. Check: the measure lane's four-seat
   route shows each seat's view in its rect with `main$place$1` at one
   dispatch, on both backends.
8. **Glass storage by coverage**, and one published image a frame in flight.
    Check: the fixed class at the stage.
9. **The stationary window in two words a phase.** A phase's transport takes
   a word of its own because it differs by phase only where the footprint
   holds surfaces at two depths. Kept once a pixel as the transport per unit
   coverage, with a phase holding its coverage and weight beside its colour,
   it is exact everywhere else; at a depth edge a running mean would follow
   the jitter, which is the shimmer the window exists to remove, so such a
   pixel needs either the per-phase word or no window. Decide by a presented
   burst of a fogged silhouette. The standing resolve no longer sums every
   phase every frame: it
   [replaces one term](frame-production.md#3-the-main-view-costs-a-pixel-what-changed-in-it)
   of a mean it keeps.
10. **Read what the slots give up.** A slot lists at most eight lights its
    corners disagree on, and the pool holds one extra slot a cell. The lookup
    counts the lights rounded and the groups that kept no extra
    (`lights-rounded` and `groups-without-extras` on
    `world.counters gpu pass sdf.world$views-probes`). Check: both read zero
    over the route, or the list and the pool grow.

## The invariant the slots rest on

A views kernel reads only its pixel's slot, and a slot holds the answer the
corners would give: its cubes are `sdfMeshProbeCube`'s, its agreement
`sdfMeshLightAgreement`'s, and every light whose bit is in "any" and not in
"all" is in its list with `sdfMeshLightVisibility`'s share. One function writes
a slot (`sdfViewsProbesStoreSlot`) and the views kernels include no reader of a
corner, so a slot cannot disagree with a second path. The guarantee stops at
the two limits the last step counts: a light past the list is rounded to seen or
unseen, and a group of cells past the pool reads its representatives' slots.
