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
| Shading | `views` | 1.9 standing, 2.5 moving | 2.0 |
| Glass | `glass-clear`, `glass`, `glass-shade` | 0 to 0.9 | 0.4 |
| Reconstruction | `resolve` | 1.9 moving, 2.5 standing | 0.7 |
| Sky and display | `sky`, `composite`, the bloom pyramid, `display` | 1.5 | 0.9 |
| Other views and the root | nested views, `main`, the environment | 1.5 to 4.1 | 0.7 |
| The world's shadows | the residency's upload | 0.5 | 0.3 |
| **Frame** | | **13 to 16** | **8.0** |

The main view's rows are `world.gpu-timing` at the stage with the camera
standing and the route's chapter means moving; the others are the route's.

Per-pixel storage of the main view at 2560 by 1440 (`host.memory families 0`):

| Storage | Now, MiB | Budget, MiB | How |
|---|---|---|---|
| Stationary window (`historyWindow`) | 458 | 250 | A word of colour and a word of coverage, weight and packed transport a phase |
| Probe records (`viewsProbes`) | 150 | 80 | A cell of four pixels a side with interpolation |
| Probe histories (`viewsProbeHistory`, `glassProbeHistory`) | 118 each | 60 | One world-space cache a residency in place of a history a view |
| Glass sums, target, depth and lit image | 340 | 40 | Allocated for the tiles glass covers |
| History surface and colour | 227 | 120 | Motion and identity in the sample record |
| Visibility records, mesh target and depth | 186 | 150 | A draw and triangle in one word of the target |
| Lit images, sky runs, transport, reactivity, filter | 330 | 200 | Sky runs at half size; transport from the sample record |
| The root's frame and stages (`main`) | 249 | 60 | Placement in place; one published image a frame in flight |
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
  (`SDF_VIEWS_OMIT_LIGHTS`), the rest the occlusion and shadow filter.
- **`views`**: a fifth with the shading compiled out
  (`SDF_VIEWS_OMIT_SHADING`: the load, the hit door, the store). Of the rest,
  the light walk (`SDF_VIEWS_OMIT_LIGHTS`) and the reflected environment
  (`SDF_VIEWS_OMIT_REFLECTION`) are the two large parts and the material layers
  (`SDF_VIEWS_OMIT_MATERIALS`) the small one.
- **`resolve`**: standing, every pixel sums its window; `world.temporal off`
  removes the pass and a third of the probe lookup with it.
- **`main` 0.8 ms**: the root places each of the layout's eight views with a
  pass of its own, and a view the layout does not show still copies the whole
  image, since two stage images alternate and a hidden pass may not stand in
  (`WorldRootGraph`, `PlacePackage`).
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

Four causes, in the order they are fixed:

1. **A view out of sight kept the storage of the extent it was last seen at.**
   A window fills the display as a body walks through it, and nothing shrank it
   afterwards, since a view no schedule reaches renders nothing. Built: such a
   view renders at its least share until its node installs that extent
   (`WorldViewGraphHost.ReserveShrinks`).
2. **A world no window shows was resident at full detail.** A session view no
   fit measures demanded meshes at its whole image from wherever its camera
   stands, so the debug world's second residency, seen through a window inside
   a painting, held the same gibibyte as the one the seat stands in, and the
   one detail bias coarsened both to fit. Built: a session view demands at the
   display pixels it is presented over (`WorldRenderSettings.NestedDemandHeight`).
3. **The debug world is resident twice.** A session whose stream is not what a
   whole reader is handed (`WorldSceneShare.SharesAuthority`) renders a scene of
   its own, and every such session of one world shares one
   (`WorldSharedScene`). That scene is a residency with its own mesh region,
   shadows and irradiance volume, though nearly all its meshes are the first
   residency's: the library shares a mesh on the host (`SdfMeshLibrary`) and
   each residency places its own copy on the device (`SdfMeshRegionArena`).
   The step is one device mesh pool a process's residencies place in, keyed by
   mesh identity, with each residency keeping only its draw records.
4. **Resizes and shadow layers churn the blocks.** A window's extent moves in
   sixteen steps an octave (`RenderGraphExtent.StepsPerOctave`), each a whole
   new graph beside the old one until it retires, and a pressure trim gives
   back shadow layers the next frame takes again (`world.residency`'s `shadows`
   line counts the `trims`, and `host.memory`'s `over-limit-by` names the
   layers granted past the limit). The step is
   an allocation at the octave with the render grid moving inside it, as the
   main view's does, once the storage table's rows shrink what an octave
   holds, and a trim that gives back only what no view has read for the
   anchors' own keep time.

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
| Shadow visibility | `surface`, after a classification by depth bounds | The record's shadow word |
| The reflected environment | The environment node | A prefiltered map, one lookup a roughness |
| Reconstruction's sample (motion, identity, air) | The views pass's epilogue | One record a render pixel |

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
- **Merging passes.** The root's placement (eight passes for one view) and the
  filter's light tiles are the two that pay. `views-bins` and the pyramid cost
  under 0.2 ms together.
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
whichever pass touches moved memory ([where the memory goes](#where-the-memory-goes)):

- **One device mesh pool.** A process's residencies place meshes in one pool by
  mesh identity. Check: with the garden painting in view from the painting
  room, the mesh regions hold one copy of the debug world, and
  `world.residency`'s `detail-bias` of the world the seat stands in reads zero
  at the stage.
- **Windows allocate by octave, and a trim keeps what a view read.** Check: over
  the route the `session` and `routed` families' `allocated` stay within twice
  their `peak`, the room in device blocks no family names stays under half a
  gibibyte, and `pool device` reads `over-limit=0` at every chapter's end.
- **The benchmark names a frame over the device's budget.** A chapter whose
  device ledger peaked past the segment's budget is written disturbed with that
  reason (`WorldBenchmarkCommandModule`'s disturbances), so a stall is never
  read as a pass's cost. Check: the route's report at a display the budget
  cannot hold says so in the chapter that crossed it.

Then the time:

1. **A coarser lookup with interpolation.** A cell of four pixels a side; views
   blends the four cells about a pixel, weighing each by the reuse test, and a
   pixel no cell serves takes a pool slot. Check: `views-probes` under 0.8 ms
   and the records under 80 MiB, with no cell visible in a presented burst
   along the hall's floor.
2. **Light lists.** As above. Check: the walk's visits a pixel are unchanged in
   the debug world; `SdfLights.MaxLights` is gone; a row of 512 point lights
   renders with each cluster's list holding only its own.
3. **Classify a receiver's shadow before filtering it.** The
   [rendering plan](rendering.md#gpu-submission-and-routed-worlds)'s item: a
   depth bound for each block of a layer answers lit and umbra exactly from
   four reads. Check: `surface` under 1.2 ms at the stage.
4. **A prefiltered environment.** The environment node writes the reflected
   sky, its panels included, at a few roughness levels; views takes one lookup.
   Check: `SDF_VIEWS_OMIT_REFLECTION` removes under 0.4 ms.
5. **A sample record for the resolve.** Views writes each render pixel's luma
   and chroma, reactivity, identity, distance, motion and air; the resolve
   reads nine records and no mesh. Check: `resolve` under 1.2 ms moving, and
   the history surface holds four words.
6. **One probe cache a residency.** The hashed table above. Check: no view
   holds a probe history; a nested view rated to five frames a second gathers
   only what entered it.
7. **The root places in place.** One image, each shown view writing its own
   rect. A hidden view's pass cannot stand in today because the two stage
   images alternate, so its output would stand for an image a later pass
   overwrites (`ShaderPipelineRenderNode.AliasRefusalOf`). Check: `main` under
   0.2 ms with one view shown.
8. **Glass storage by coverage**, and one published image a frame in flight.
    Check: the fixed class at the stage.
9. **Count what the slots give up.** A slot lists at most eight lights its
    corners disagree on, and the pool holds an extra for one pixel in eight.
    Count the lights rounded and the groups that kept no extra, on
    `world.counters gpu pass sdf.world$views-probes`. Check: both read zero
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
