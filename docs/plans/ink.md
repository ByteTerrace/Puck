# Ink

Outlines in Puck are edge signals the engine computes once per view and an ink
phase that authors write in the shade program
([the ruling](../decisions/rendering.md#a-light-is-shadowed-wherever-it-lights-and-cel-shading-is-a-style-per-material)).
This plan holds the research round, the design, what of it is built and the
order of what remains.

## What is built

The [handbook](../rendering/sdf/handbook/lighting-and-shading.md#ink) describes
the mechanism as it stands; this is the list a later step builds on.

- **The ink phase.** `SdfShadePhase.Ink` is a program's fourth phase, with the
  `color` and `coverage` outputs, `ink.under`, `ink.line` and `ink.exposure`,
  the `line` channel through the light walk and the compose phase, the gate
  (`sdfShadeInkPhase`), the phase's price in `SdfShadeProgram.PixelWork`, and
  the refusal of an ink phase that reads no line input.
- **One unit for width.** The pass block's `displayPixels` and the stamp's
  `surface.pixel`.
- **The edge search.** Occlusion, crease, object and material distances and
  the crease's cosine (`edge.depth`, `edge.crease`, `edge.object`,
  `edge.material`, `edge.creaseCosine`), from seeds between neighbouring
  pixels, in the views filter's group (`sdfViewsGatherFilter`,
  `sdfViewsEdgesWord`); the object key on every draw
  (`SdfMeshDraw.ObjectKey`); and the clip flag that keeps a portal's cut
  unlined.
- **The standard's ink.** `worlds/standard/ink.puck` (`ink`, `inkHatched`,
  `inked`, `hatched`, `standardInk`, `boldInk`), which `celMaterial` and
  `celHatched` lay over the band (`worlds/standard/styles.puck`). The gallery
  and the city carry their own inks, and the valley's rock and the glacier's
  ice are painted without one (`celPaint`).
- **The engine's outline is gone.** The fixed detector, its four style values
  and their pass members and material rows are deleted: a style row is the
  styled mark alone (`SdfToon`, `ToonStyleDocument`).

What authors still cannot do: ink the seam of a smooth blend or a part boundary
inside one bake group; draw a level line at a true constant width on a form
whose curvature differs along and across the view (the stamp holds the mean
alone); taper a silhouette by the size of its depth step, or read a stroke's
direction; ink glass or water; draw a line wider than the reach from a jump.

The cel and toon vocabulary that remains in engine contracts
(`ToonStyleDocument`, `PaletteToonSelectDocument`, `render.toon`, `SdfToon`,
the pass member `ToonMode`, `sdf-toon.hlsli`, the `world.toon` verb and its
lever) is a leak under [the engine is generic](../decisions/engine-design.md#the-engine-is-generic):
what it names is a shade-case selection by state and a diagnostic over
authored phases. The engine's second built-in line, `applyCurvatureShading`
(`render.lighting.curvature`, `SdfCurvature`), still runs on every material
with no style, and no world authors it.

## The research round

Three lanes, each with its own focus and verified citations, then one
synthesis into this page's design.

| Lane | Memo | Focus | What it concluded |
|---|---|---|---|
| `ink-shipped` | [Line art in shipped games and offline tools](ink/research-shipped.md) | Which edges shipped stylized games and offline line art ink, how width, colour and breakup are authored, and what each costs | Every shipped look separates finding an edge from deciding how to draw it, and every one restricts, tints, tapers or fades its lines. Its last section lists what an ink phase must express |
| `ink-gpu` | [GPU edges and authored ink](ink/research-gpu.md) | Edge detection over a visibility buffer, distance to the edge at bounded cost, temporal stability, dynamic resolution, tile classification | Detect once per view, keep the edge's class and owner, and search a bounded neighbourhood. A flood across the whole image solves a larger problem than ordinary widths pose |
| `ink-field` | [Ink from the field](ink/research-field.md) | What a signed-distance pipeline can derive that others cannot | Lines come in three kinds with three carriers: the level line of a smooth quantity (one pixel, no neighbours), a jump between pixels (a neighbour comparison), and a line the program describes (the bake carries it) |

## Design

### Summary

The engine supplies edge signals in three ways, one for each kind of line the
field memo names, and a shade program's fourth phase turns them into ink.

- **Per-pixel scalars.** The world size of one display pixel (built) and the
  surface's curvature along any direction (to build). With those, the outline
  of a rounded form and the light terminator are level lines an author draws at
  a constant width with no neighbouring pixel.
- **A neighbour pass** (built). One bounded search per 8×8 tile, inside the
  pass that already filters each view's shadows, finds the jumps: an occlusion,
  a sharp crease, a boundary between two objects or two materials. It writes
  each pixel's distance to the nearest edge of each class, in display pixels.
- **What the bake carries** (to build). A shape declares a part. The bake's
  rule that already tracks which shape won each surface point tracks the part
  too, and how nearly the runner-up won, so the mesh holds part boundaries and
  the seam of a smooth blend.
- **The ink phase** (built). A program's `ink` block runs once a pixel after
  `compose`, reads the signals and the colour under it, and writes a colour and
  a coverage. The engine runs it only where one of its line inputs is live.

Width has one unit, the display pixel a viewer sees, in every view. Every look
value (which edges ink, width, taper, colour, breakup) is a parameter of a
program in the standard package.

### Where the memos disagreed, and what the code says

| Question | The memos | The code | Decided |
|---|---|---|---|
| What identity marks an object boundary? | The survey compares the visibility record's identity word. The GPU and field memos both say that word is the wrong key | The identity's source is the draw (`sdfVisibilitySource`). `SdfMeshDresser` gives a chunked group one draw per chunk, and a static creation bakes as one group whatever its shape count (`SdfInstanceRange.BakesWithPrevious`). A comparison of draws inks chunk seams and misses every part | An object key the dresser gives every draw of one group or copy, and a part the bake carries per triangle ([below](#what-the-bake-carries)) |
| Who draws the outline of a rounded form and the terminator: arithmetic at the pixel, or the neighbour pass? | The field memo draws both from scalars. The GPU memo brackets both between neighbours | Both lines are level lines of a cosine the stamp already holds. The field memo's template is admitted today at about a third of `cel`'s static cost | Arithmetic at the pixel, with the neighbour pass kept for what has no slope: sharp silhouettes and faceted forms. On a rounded form the engine reports the nearer of the two estimates as one distance |
| How is the distance to an edge found? | The GPU memo proposes sub-pixel seeds joined into segments, tile lists by indirect dispatch, and a radius-limited flood for wide strokes. The current code samples two rings | The views filter already loads each tile with an apron into group memory (`sdfViewsGatherFilter`), and its record already travels to the shading bins | A bounded search over seeds in that group's memory, with no flood, no tile list and no extra dispatch. Segments and a wide path are later steps a capture has to ask for |
| Where does curvature along a direction come from? | The field memo writes the view's curvature in the record and re-reads the triangle in the views pass for each light's | The surface pass already forms the change of the normal across the triangle for the mean (`sdfMeshMeanCurvature`). Views would re-read three vertices a pixel, and more for a skinned mesh | The surface pass writes the curvature's two remaining components once, and any direction is arithmetic from them ([below](#per-pixel-scalars)) |
| Which side of a boundary draws? | The survey asks for an authored priority between neighbouring materials. The GPU memo asks for priority, placement inside or outside, and a stable tie-break | A pixel runs its own material's program and can read no other material's rows | Each side draws its own half of a line, from its own program. A material that wants no line at a boundary draws none there. A stroke painted onto another surface's pixels is [not in this design](#what-this-design-does-not-do) |
| Is `surface.curvature` enough for creases? | The survey offers it for ridges and valleys | A sharp crease is split into two vertices with each face's normal (`SdfContour.SplitAtCreases`), so both of its sides read zero | Curvature draws lines on smooth forms only. Sharp creases are the neighbour pass's |

### Where the build departed from this design

| The design said | Built | Why |
|---|---|---|
| The surface pass writes the object key, the part and the clip flag into a second new visibility-record word | The search reads the key and the clip flag from the hit's draw record (`SdfMeshRecord.ObjectKeyShift`, `ClipFlag`) as it gathers its tile, and the visibility record keeps its eight words | No part exists yet to write, and a draw's record is one cached read a tile entry. The word returns with the bake's part ([what the bake carries](#what-the-bake-carries)), since a part is per triangle |
| Two more words in the filter record | One word: four distances in six bits each and the crease's cosine in eight (`sdfViewsEdgesPack`) | Nothing else is carried yet. A step that adds the depth gap or the direction widens it |
| Each pixel finds its nearest seed by a search over the tile | Seeds are bit masks, one a class for each tile row and column, and a pixel's distance is a bit scan down its column and along each row it reaches | A fixed handful of scans a pixel in place of a compare with every neighbour, and the scan stops at an occlusion or an object boundary for free, which is what keeps each side's line on its own surface |
| `edge.creaseAngle`, `edge.depthGap`, `edge.direction` | `edge.creaseCosine` alone | A program needed the crease's strength to choose and taper its folds, so the pass's own threshold is permissive (thirty degrees) and the look's is authored. The other two wait for a look that asks |
| On a rounded form `edge.depth` reports the nearer of the search's distance and the outline rule's | The search's alone | It needs `curvatureAlong` ([per-pixel scalars](#per-pixel-scalars)) |
| An `edges` debug view | Not built | The check world's far row shows each class under the standard ink; the view is still wanted for a critic |
| `world.shade phases` beside `world.toon` | Not built | It lands with the vocabulary's removal |
| The standard's styles carry their ink as template arguments | An ink is a record of the template's values (`standardInk`), laid on by `inked` or `hatched` | A world restyles one with `with`, and a style with no ink names none (`celPaint`) |

### The edge signals

Every signal reaches a shade program as a member of its stamp
(`SdfShadeOps.Inputs`) or as an op (`SdfShadeOps.Table`). A program has no
buffer address and cannot read a neighbouring pixel, so this list is all an ink
phase can know.

| Signal | Kind of line | Computed by | Reaches the stamp as | State |
|---|---|---|---|---|
| The world size of a display pixel | The unit of every width | The frame block, per view | `surface.pixel`, in metres | Built |
| Curvature along a direction | Outline of a rounded form, terminator, ridges and valleys | The surface pass, from the hit triangle | The op `curvatureAlong(direction)` and `surface.curvatureSpread`, beside today's `surface.curvature` | To build |
| The light's cosine and its line | Terminator, band edges, hatching in shade | The light phase | `light.cosine`, and the program's own `line` output | Built |
| Occlusion | Silhouettes, one form in front of another | The neighbour pass | `edge.depth` | Built; `edge.depthGap` to build |
| Crease | A sharp fold in one surface | The neighbour pass | `edge.crease`, with `edge.creaseCosine` | Built |
| Object boundary | Two creations, or two copies of one, touching | The neighbour pass, over the object key | `edge.object` | Built |
| Part boundary | Two declared parts of one creation meeting at a hard seam | The bake places it on mesh edges, and the neighbour pass finds it | `edge.part` | To build |
| Material boundary | Two materials on one surface | The neighbour pass | `edge.material` | Built |
| Blend seam | The centre line of a smooth blend between two parts | The bake, per vertex, and the surface pass | `edge.seam` | To build |
| Direction to the nearest edge | Width by stroke angle, hatching along an edge | The neighbour pass | `edge.direction`, a unit vector on the screen | To build |
| Contact behind glass | A shoreline | The glass pass | `surface.contact` as today, and `surface.contactSlope` | To build |

Every `edge.*` distance is in display pixels and reads far past the pass's
reach. Any phase may read these members. A surface phase that reads
`edge.crease` can wear the true edges of a box, which the mean curvature cannot
find.

#### Per-pixel scalars

**The problem.** The field memo's rule turns a smooth quantity into a line: the
distance to the line is the quantity's difference from its level, divided by
how fast it changes per metre, divided by the size of a pixel. The outline of a
rounded form needs the curvature in the plane of the view ray and the normal,
and the terminator needs the curvature along the light. The record holds only
the mean. On a cylinder seen from the side the mean is half the curvature
across it, so the line comes out at half width.

**What we propose.** The surface pass writes the two components of the
curvature the mean leaves out, in one new record word. In a tangent frame fixed
by the normal, the curvature along a unit direction at angle θ is the mean plus
`a·cos 2θ + b·sin 2θ`, and the word holds `a` and `b` as two halves. The mean
stays where its readers find it. The op `curvatureAlong(direction)` projects a
world direction onto the tangent plane and evaluates that sum, so one op serves
the view (`surface.rayDirection`), any light (`light.direction`) and any
direction an author chooses. `surface.curvatureSpread` is the length of
`(a, b)`: the mean plus and minus it are the two principal curvatures, which is
what a ridge or valley line thresholds.

**Why.** The surface pass has the triangle's three positions and normals in
hand and already forms the normal's change across it. Two more numbers there
cost arithmetic. The alternatives were weighed:

| Option | Cost | Fails at |
|---|---|---|
| Write the view's curvature beside the mean, and have the views pass re-read the triangle for each light's (the field memo) | Three vertex reads a pixel for every program that draws a terminator, more for a skinned mesh, and two mechanisms | The debug world styles nearly every pixel, so nearly every pixel pays the re-read |
| Recompute everything in the views pass on demand | No storage | The same re-read, on every pixel that inks |
| **Write the two components once (taken)** | One record word a pixel, about 15 MB for one view at 2560 by 1440, written every frame | A triangle's curvature is constant inside it, so a coarse level may step ([step 3](#build-order) reads that) |

The record layout is the same if a later step moves the source from the hit
triangle to a tensor stored per vertex. Only the surface pass would change.

**The outline and the terminator stay programs.** The engine supplies the
curvature and the pixel. The arithmetic that makes a line of them
(`inkContour`, `inkTerminator`) is a pair of functions in the standard package,
because a terminator's threshold is the author's band and a look value is never
engine code. One refinement is the engine's, because it is correctness and not
a look: on a rounded form `edge.depth` reports the nearer of the neighbour
pass's distance and the outline rule's, so one silhouette is one line, placed
more finely than a pixel and unchanged when the render grid moves.

**Where it fails.** A flat face has no slope to divide by, so its outline is
the neighbour pass's. The mesh's outline is not the smooth surface's, so a
width varies along a coarse silhouette by about the triangle's deviation, which
the level rule holds under a pixel except on the coarse terrain. A skinned
mesh's triangle curvature follows the pose, which a tensor per vertex would
not.

#### The neighbour pass

Built as [the handbook](../rendering/sdf/handbook/lighting-and-shading.md#ink)
describes. What a later step should know of its limits:

- A jump is placed to the render grid, at the midpoint of the pair of pixels it
  lies between, so its line moves by up to half a render pixel as the jitter
  moves and settles under the temporal resolve. On a diagonal the midpoints of
  the pairs along a row are up to 0.7 pixels apart along the line, so a line a
  pixel wide varies by up to a third of a pixel along it before the resolve
  ([the scallop question](#open-questions-and-who-owns-each)).
- A feature thinner than a render pixel that no sample hits is absent. Within
  one class a weak edge nearer than a strong one is the one reported.
- An occlusion is a point off the *nearer* surface's tangent plane by more than
  a few pixel footprints. At a fold seen at a grazing angle the far face's
  point can pass that test, and the fold then reads as an occlusion the nearer
  face owns. A look that weighs the two classes very differently shows it.
- The reach is `SdfShadeOps.EdgeReach` render pixels, so a line from a jump is
  at most that wide on each side. A wider one needs the wide path below.

#### What the bake carries

**The problem.** The line an artist draws where an arm joins a body is the seam
of a smooth blend. It has no crease, no change of material and no jump in
depth, so no comparison of pixels finds it. A hard seam between two shapes of
one creation is found only when its normals turn, because both shapes are one
draw.

**What we propose.** A shape row declares a `part`, a whole number whose
default is none. A creation that declares none bakes the triangles it bakes
now and gains no line.

- *The winner rule.* `sdfComposeCandidate` already keeps the material, lanes,
  instance and slot of whichever shape wins each composition. It keeps the part
  the same way, and the margin: how much nearer the winning part's surface is
  than the nearest other part's. The margin is zero on the centre line of a
  blend between two parts.
- *The sample.* The bake's material sampling leaves two of its five words empty
  (the mode that writes the slot writes zero gradients beside it). The part and
  the margin go there, so there is no new dispatch and no larger sample.
- *The mesh.* `SdfMeshMaterialCut` places a part boundary on mesh edges exactly
  as it places a material boundary, and each triangle carries its part in the
  spare bits of its material word. A vertex of a mesh that declares parts
  carries its margin.
- *The CPU twin.* `SdfFieldEvaluator` tracks the same winner, so
  `puck mesh audit --cpu` can check the mesh against the field.

The surface pass interpolates the margin at the hit, divides by its slope
across the triangle and the pixel's size, and writes the distance to the seam.

**What it costs.** The bake kernel's bytecode and the bake's C# both key every
stored mesh (`SdfMeshBakeIdentity`), so every stored mesh bakes again once.
The step lands with the kernel edits
[the terrain plan](terrain-pass.md#order-of-work) batches, so that happens one
time. A mesh that declares parts gains one word a vertex.

**Where it fails.** The margin is read by interpolation, so it is as good as
the tessellation. A fillet is curved and the bake refines it. A flat face is
merged into a few large triangles (`SdfMeshPlanarMerge`), and a triangle that
spans from one seam to another would interpolate a false line. So the margin
draws smooth blends, and a hard seam is a part boundary, which the neighbour
pass finds. An author who wants one exact seam today can paint one shape onto
the other's material (`sdfApplyPaints`).

### The ink phase

Built: the handbook holds the members, the gate and the `line` channel. What
the design decided that a later step must keep:

- **One pair of outputs covers the ways lines compose.** Dark ink that
  multiplies is `color = ink.under * tint`. An opaque line is a display colour
  over `ink.exposure`. A bright rim is `color = ink.under + glow`.
- **The gate is a definition, not a proof.** An author who wants ink somewhere
  raises `line` there with cheap arithmetic in the light or compose phase, and
  the costly part of a look runs only where a line is.
- **Composition.** A palette row's `shade [a, b]` list lowers its elements
  into one definition, a later output replacing an earlier one by name
  (`WorldShadeLowering.Program`), so `inked(celPaint(row, style), pen)` is one
  program: the row's own surface, the band, and the ink. Width, colour and
  breakup are parameters, which are data outside the identity, so every
  material that inks with one template shares one program, one kernel arm and
  one bin whatever its values. Only the surface phase chains, so one program
  holds one ink phase: two inks do not layer.

### Motion and dynamic resolution

Ink is applied in the views pass, in the frame's jittered samples, before the
temporal resolve, the air and the display transform, as the outline is now. A
line therefore recedes into haze, blooms and tones with the surface it is on.

**Lines fixed to the surface** (a crease, a part or material boundary, a seam,
a paint) are at the same surface point every frame. History follows the surface
point (`sdfPreviousPoint`), so such a line accumulates like any texture, and a
still view's window of jitter phases converges a line thinner than a render
pixel to its true coverage. Nothing is added for them.

**Lines that depend on the view or a light** (an outline, a terminator) slide
over the surface, so history holds no line where the line now is. The resolve
clips history to the pixel's neighbourhood, which removes a stale line, and may
soften a thin one while it moves. Whether it does is a question for a capture
([step 1](#build-order)). The mechanism, if the capture asks for it: the ink
phase gains an output, `slide`, the share of its coverage that came from a line
of this kind. The standard's contour and terminator functions set it. The
resolve lowers the history's weight by it only for a receiver that moved this
frame, so a still view keeps its full window. Marking every line reactive is
rejected: it would discard the sub-pixel samples that make a thin line stable.

**Dynamic resolution.** A level line's width is a division, so it does not step
when the render grid changes. A jump's distance is searched on the render grid
and reported in display pixels, so its width holds and its placement coarsens
with the grid. When the grid changes the resolve already reprojects through
both extents, and ink is part of the lit image it reprojects.

**Levels.** A group baked again at another cell, or a draw that changes level,
changes every value its vertices bear at once. A line placed by them moves when
the new mesh takes the draws. That is the bake's existing behaviour, and the
line inherits it.

### Glass, water and portals

**Ink behind glass is already right.** Ink on an opaque surface is in the lit
image before the glass pass reads it, so a line seen through water bends and
blurs with the refraction, and the glass pass already distrusts history where
the refracted read lands a pixel away.

**Ink on glass.** The views and glass kernels call one generated set of phase
doors (`SdfShadeHlsl`), so the ink phase runs on each shaded interface inside
the glass pass's far-then-near composition, with the same stamp. The glass
stage computes an interface's normal and curvature through the same
`sdfMeshSurfaceNormal`, so the per-pixel signals work unchanged: the rim of a
glass body by the outline rule, a second estimate of the rim from
`surface.thickness`, and ripples from `latticeGradient`. A shoreline is the
level line of `surface.contact` at zero. `surface.contactSlope` gives its
change per metre along the water, from the interface's normal, the ray and the
bed's normal in the opaque record at that pixel, so the same rule draws it at a
constant width. In the first build `edge.*` distances from the neighbour pass
read as far on glass. One pane in front of another, and a pane's edge against
what is behind it, are jumps in the glass target, and a later step searches
that target.

**Portals and screens.** A nested view runs its own edge pass at its own
extent, and only when one of its programs reads an edge. Taps outside the view
contribute nothing, as now. A screen's or an opening's picture never runs a
program, so it is never inked by the view that shows it, while the frame around
it is ordinary geometry and inks. A signal measured on the surface in metres is
the same through a portal as seen directly, and the display pixel makes the
width the same too. The clip flag keeps the cut of a body standing through a
portal free of a line.

### Cost

| Cost | Bound | What counts it |
|---|---|---|
| Record words | One more a pixel in the filter record (three for two), about 15 MB for one view at 2560 by 1440, allocated only in a binned graph. The visibility record is unchanged until the part and the curvature words land | `world.residency` and the memory report |
| The neighbour pass | A wider tile (the reach on every side in place of the filter's three pixels), two comparisons a tile entry to seed, and a fixed number of bit scans a pixel, set by the reach and nothing in the scene. No search in a tile with no seed. No extra dispatch | `world.counters gpu pass sdf.world$views-filter`, and `world.gpu-timing` for the pass |
| The ink phase | Its allocated instructions once a pixel, added to `SdfShadeProgram.PixelWork` and checked against `SdfShadeProgram.MaxWork` as if every pixel ran it. The gate only lowers what executes | `world.budget` prints the program's static cost. `sdfWorkSteps` counts executed instructions, so the gate's saving shows in the views pass's work counter |
| A view with no ink demand | Nothing beyond the filter record's third word: the tile is not widened, nothing is seeded and the phase is not run | The same counters |
| The bake (to build) | One rebake of every stored mesh, once. One word a vertex on a mesh that declares parts | `puck mesh identity --against`, `puck creation stats` |

[The frame budget](frame-budget.md#the-budget) holds ink's share of the frame
and the readings taken against it.

### What is deleted

No alias, no deprecation step and no read-side tolerance is kept
([rule 5](../../AGENTS.md#core-rules)). A document that still spells a removed
member is refused by name. The outline's four style values are already gone.
What remains:

| To remove | What replaces it |
|---|---|
| `ToonStyleDocument`, a palette row's `toon`, `render.toon`, `SdfToon`, `SdfMaterial.Toon`, `SdfFrame.Toon`, the mode `SdfAppearance.Pack` writes in row 22, the pass member `SdfWorldPackage.ToonMode`, `shade/sdf-toon.hlsli` | Nothing: there is no styled mark. A material inks when its program has an ink phase, and the comparison lever below turns phases off |
| `PaletteToonSelectDocument`, `PaletteToonCaseDocument`, a palette row's `toonSelect` | A shade-case selection, `shadeSelect`: a state cell and a list of programs, the case's program replacing the row's. It is the author-shaders plan's selection with the style row gone, and that plan's [style switch](author-shaders.md#the-remaining-steps) entry is rewritten with it. The Moth's `mothStyles` becomes two programs |
| `world.toon`, `WorldSessionLevers.Toon`, `WorldRenderSettings.Toon`, the `unstyled` mark in `sdfShadeBinding` | `world.shade phases <all or a list>`: which authored phases run, for every program. A phase that is off takes the engine's default, so `world.shade phases surface` is today's comparison of continuous against styled, and leaving out `ink` shows a view without its lines |
| `SdfCurvature`, `SdfLights.Curvature`, `applyCurvatureShading`, `worldCurvatureShadingEnabled`, `render.lighting.curvature` (`WorldRenderCurvature`), its pass members and its value fields | A standard program, below |

**The second built-in line folds into programs.** The curvature shading is a
capability no world calls, and "unreached is not unwanted" forbids deleting one
without asking. It is not deleted. Its three terms are per-pixel functions of
values a program already reads: cavity darkening and the ridge light are a
compose phase over `surface.curvature`, `compose.radiance`, `compose.indirect`,
`compose.adaptation` and `compose.exposure`, and its line by curvature is a
`line` raised in compose and drawn by the ink phase. The standard package gains
that as a template, `curvatureAccents`, in the step that removes the engine's
copy, and the gallery gains an exhibit under it, so the capability is wired for
the first time. Three things change for an author:

- The treatment is per material, or the world's default through `render.shade`,
  where it was every unstyled material at once. That is the ruling's shape: a
  style per material.
- A gain that followed a state row or a clock rides one of the four world lanes
  (`render.lanes`) into the program.
- `render.lighting.curvature` is refused by name, with no alias, as AGENTS.md's
  zero-legacy rule requires; the standard template replaces it in the same step.

The standing rule that a program omitting a phase takes the engine's default
means a material without the template shades exactly as an unauthored world
does today, where every gain is zero.

### The invariant

An edge signal is a function of the surface point, the view, the light and the
program. It is never a function of the render grid, of which draw or chunk a
triangle fell in, of which 8×8 tile holds the pixel, or of which of two views
is looking.

What enforces it:

- *The stamp is the only door.* A shade program reads the members
  `SdfShadeOps.Inputs` lists and nothing else, so an ink phase cannot depend on
  anything the engine did not hand it. The members are defined in metres and
  display pixels, so every program inherits the property.
- *The comparison cannot see a draw.* The search's tile holds each entry's
  point, normal, material and object key (`sdfViewsFilterKeys`) and no draw
  index, so a chunk seam has nothing to differ by.
- *One search.* `sdf-views-filter.hlsli` seeds and measures, and the filter
  pass and the unsplit views kernel are its two callers. The stamp's `edge.*`
  members are filled only from its record word (`sdfViewsEdgesUnpack`), which
  both callers read, so a binned view and an unsplit one hold one value.
- *One ink door.* `SdfShadeHlsl` generates `sdfShadeInkPhase`, with the gate
  and the composition of `color` by `coverage` inside it. No caller can run the
  phase ungated or compose its outputs another way.

Where the guarantee stops: a jump is found on the render grid, so its placement
(not its width) depends on the grid. Nothing but the search's own arithmetic
keeps a bound from reading the tile's edge: every bound in it is written as
the reach, and a change that reads the tile's extent draws a seam every eight
pixels, which a capture of a diagonal line shows and no check does. The object
key separates groups and copies, so two shapes an author bakes into one group
are one object until they declare parts. Nothing checks that a program's own
arithmetic keeps the property: an author who thresholds a raw cosine draws a
line whose width follows the radius, which is theirs to write.

### What this design does not do

- **A stroke outside its surface.** A pixel runs its own material's program, so
  a line is drawn on the pixels of the surface that owns it. An outline that
  grows outward over the background, or an overshoot into the sky, needs the
  owner's program run on another surface's pixel and a resolve that carries the
  owner's motion for it. It becomes a step when a look asks.
- **A coordinate along the line.** A per-pixel ink has no start or end of a
  stroke, so it cannot taper from end to end or lay a brush texture along a
  curve. Tapers are by strength. Chaining visible edges into strokes is the
  offline tools' method and is not planned.
- **Suggestive contours and apparent ridges.** Both need the derivative of
  curvature and are unstable under small camera moves, and the study the field
  memo cites finds most drawn lines without them.
- **Lines from luminance.** The GPU memo offers an edge of scene brightness. It
  would ink specular glints and its own neighbours' ink, and nothing the survey
  found needs it.

### At the scale of an MMO

Every zone, character and server is a world, and one frame can show several
through portals and screens. Three properties of this design are what that
scale asks of it.

- *Cost follows pixels, not worlds or objects.* The neighbour pass's work is
  fixed per tile and the ink phase's is fixed per pixel, whatever the number of
  creations, characters or parts in view. A nested view pays only when its own
  programs ink.
- *A look travels with its world.* Ink is a phase of a program in the world's
  `shades` table, named by content. Two worlds with different inks compose in
  one frame with no engine setting between them, and the display pixel keeps a
  body's line the same on both sides of the portal that joins them.
- *Ink is presentation.* It reads no simulation state and writes none, so it
  places no authority, crosses no trust boundary and leaves determinism alone.
  A part is authored in a creation and rides its baked mesh, a cache derived
  from the creation's identified bytes.

### Systems considered

| Capability | Existing mechanism | Verdict |
|---|---|---|
| A tile with an apron in group memory | `sdfViewsGatherFilter`, the binned filter pass and its record | Extended: the edge search runs in the same group and writes the same record |
| An outline from depth and normal jumps | `sdfToonOutline` and `sdfToonEdge` (`shade/sdf-toon-outline.hlsli`), up to sixteen taps a styled pixel inside the views kernel | Replaced and deleted: its two tests are the search's occlusion and crease seeds |
| Whole-tile classification and bounded jobs | The shade bins and `SdfShadeDispatch` | Used as they are. Ink adds no bin, since a pixel that inks already has a program |
| A cost only readers pay | `SdfShadeReads` and `SdfAppearance.ResolveShadeReads` | Extended with `SdfShadeReads.Edges`, which is a view's ink demand |
| A bound proved from the program | The wrap and tilt bounds behind `sdfMaterialShadowDemand` | Considered for the gate and set aside: the gate is a definition over `line`, which needs no proof |
| What an object is | `SdfMeshDraw.Identity` (the picking identity) and the visibility record's draw | Set aside: a chunked group's pieces each carry their own identity and their own draw. The dresser keys every draw of one copy or group alike (`SdfMeshDraw.ObjectKey`) |
| A draw's clip plane | `sdfMeshClipDistance`, the mesh stages' `SV_ClipDistance` | Reused: the search asks it for a hit beside the cut, behind a record flag so only a clipped draw's plane is read |
| Curvature at a pixel | `sdfMeshMeanCurvature` | To extend to the two remaining components, not duplicate in the views pass |
| Which shape won a point | `sdfComposeCandidate`, `SdfFieldEvaluator` | To extend with the part and the margin |
| Exact boundaries on the mesh | `SdfMeshMaterialCut` | To reuse for part boundaries |
| The share of each owner at a vertex | `SdfMeshSkin.Build`, `SdfMeshInfluence` | Already resident for skinned groups. Its per-vertex CPU walk is too costly for every static vertex, so the margin rides the bake's sample |
| The display size of a nested view | `SdfViewSnapshot.DemandHeight`, `SdfViewSnapshot.RenderGrid` | Reused as the width unit's source |
| A coarse shading rate that must not smear a line | `sdfRateCell` (`sdf-views-rate.hlsli`), which asked the old detector pixel by pixel | Kept: its one-surface test over the tile and a border is already what "no seed near" means, so the border widens to the reach in a view that searches |
| A look swapped by state | `PaletteToonSelectDocument` | Kept as a mechanism, to rename for what it is |
| Suppressing authored phases for comparison | `world.toon` and `sdfShadeHas` | To generalize to a mask over phases |
| A crisp authored line on a surface | `sdfApplyPaints`, the `shape` and `lines` ops | Used as they are, in any phase |
| Strokes that ride a surface | The `lines` op (`sdfLineCoverage`), the weathering's scratches | Set aside for hatching: it draws one randomly turned scratch a cell. The standard's hatching is stripe arithmetic over `surface.layerPosition` (`inkStroke`), which needs no op |

## Build order

Each step lands alone. The check world is `worlds/debug/ink-checks.puck`: a
near row that draws level lines from the stamp through the `line` channel, and
a far row that draws each class of searched edge under the standard ink.
Captures are settled stills with the exposure pinned and the camera fixed, as
[the authored-shading reference](../../.claude/skills/rendering/references/authored-shading.md)
describes, unless a step says a presented burst.

1. **Lines in motion.** A presented burst of eight frames inside a sustained
   pan of the check world and of the city's square, with `world.temporal on`,
   at render scales of one and one half.
   - *Decides:* whether a searched line and the hatching keep their settled
     contrast while they move, and whether a diagonal line a pixel wide
     scallops through the resolve. If a line loses contrast, the `slide`
     output and the resolve's use of it are built here
     ([motion](#motion-and-dynamic-resolution)). If it scallops, a step joins
     crossings into segments. The same burst sets the smallest width the
     standard's styles offer.
2. **The toon and curvature built-ins go.** Everything under
   [what is deleted](#what-is-deleted) is removed with its tests, schema rows
   and documents, `toonSelect` becomes `shadeSelect`, `world.shade phases`
   replaces `world.toon`, and `curvatureAccents` lands in the standard package
   with a gallery exhibit. The decision register, the handbook's curvature
   shading section, the authored-shading reference and the author-shaders
   plan's style switch entry are rewritten in the same change.
   - *Check:* `puck search` finds no `toon` identifier under `src/`, and the
     schema, the console verb table and the kernel sources name no member for
     cel or toon. `puck schema --check` and `puck shaders generate --check`
     pass. `puck compile --validate` refuses a
     row that spells `toon`, `toonSelect` or `render.lighting.curvature`, each
     by name. `moth.style Plain` then `Cel` swaps the Moth with
     `world.counters sdf.shade` showing no compile and the bake counters no
     bake. The exhibit's capture shows cavity, ridge and line as its plaque
     says.
3. **Curvature along a direction.** The surface pass writes the two components
   beside the mean, and the stamp gains `curvatureAlong` and
   `surface.curvatureSpread`. The check world's near row switches from the mean
   to the op, and `edge.depth` takes the nearer of the search's distance and
   the outline rule's on a rounded form.
   - *Check:* a device law in the manner of `SdfReprojectionDeviceLawTests`
     shades a triangle of a cylinder of known radius and reads one over the
     radius across the axis and zero along it, on both backends. In captures
     of the check world from three distances, before the change the capsule's
     line is half the sphere's width and after it the two match. `world.budget`
     prints the template's static cost.
   - *Decides:* the same captures under `world.detail 0`, `1` and `2` show
     whether a line whose width reads one triangle's curvature steps at
     triangle edges on coarser levels. If it does, a step is added that stores
     the tensor per vertex and interpolates it, with the bent skin as its known
     cost.
4. **The bake carries the part and the margin.** A shape row's `part`, the
   winner rule and its CPU twin, the material sampling's two words, part
   boundaries in the material cut, the triangle's part, the vertex's margin,
   the visibility record's key word, and `edge.part` and `edge.seam` in the
   stamp. It lands with the kernel edits
   [the terrain plan](terrain-pass.md#order-of-work) batches, so every stored
   mesh bakes again once.
   - *Check:* `puck mesh audit --cpu` on a chunked static union counts no part
     change across a chunk seam and one along each declared boundary. On the
     check world's blend, with its two spheres given parts, the audit reports
     a zero margin within one cell of the surface points where the two shapes'
     distances are equal, measured against the CPU field.
     `puck mesh identity --against` names the bake kernel and the changed bake
     members as what differs between the two builds. A world that declares no part bakes the
     triangle counts it baked before. In a capture, the blend shows a seam
     line of constant width and the stacked boxes a part line.
5. **Ink on glass.** `surface.contactSlope`, the ink door in the glass stage,
   and the ink phase's coverage kept apart from the interface's own in the
   far-then-near composition.
   - *Check:* a device law shades a glass plane over a bed at a known angle
     and reads the slope that angle gives. In
     `worlds/debug/shade-surface-checks.puck`, whose shallow glass copies
     already isolate contact, extended with a bed that slopes at two angles, a
     shoreline holds one width across both, and a rim on a glass sphere matches
     the same sphere opaque. At the garden's pond a submerged stone's line
     bends with the water, and the water's own line sits on its surface.

Later steps, each taken when a place or a look asks for it, with what it would
show:

- **The edge's attributes.** `edge.depthGap` and `edge.direction`, in a second
  filter-record word. Shown by a silhouette that thins where its step is small
  and a stroke whose width follows its angle.
- **The `edges` debug view.** Each class's distance as a colour, so a look
  critic can tell what the engine found from what a program drew.
- **The distance to a cast shadow's edge.** The filter's 4×4 window already
  holds each slot's visibility around the pixel, so its slope there is
  arithmetic. It needs one more filter word, and a light-phase member. Shown
  by a line of constant width along a cast shadow in the surface check world.
- **A wide path.** A radius-limited flood from the same seeds for a jump line
  wider than the reach. Shown by an eight-pixel silhouette whose owner and
  colour match the bounded search where the two overlap.
- **Jumps in the glass target.** The neighbour search over the nearest glass
  face's draw and triangle. Shown by a front pane's edge drawn over a rear
  pane.

## Open questions and who owns each

| Question | Owner | What would show it | What it decides |
|---|---|---|---|
| Do seeds at pair midpoints scallop a thin diagonal line? | Step 1's burst | The check world's turned box at one and two pixels, still and in a pan | Whether crossings are joined into segments |
| Does a thin line hold its contrast in motion? | Step 1's burst | Eight presented frames in a pan at two render scales | Whether `slide` is built, and the standard's smallest width |
| Does one triangle's curvature step on coarser levels? | Step 3's captures, in the lead's windowed pass | The check world under `world.detail 0`, `1` and `2` | Whether a per-vertex tensor step is added |
| What does the search cost on the benchmark route, against the old detector? | The lead's quiet window | `world.gpu-timing` for `views-filter` and `views` on this head and on the one before it, at the extent, backend and topology it names | Whether the reach or the seeding's shape changes ([the frame budget](frame-budget.md#the-budget)) |
