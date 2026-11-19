# Ink

Outlines in Puck are edge signals the engine computes once per view and an ink
phase that authors write in the shade program
([the ruling](../decisions/rendering.md#a-light-is-shadowed-wherever-it-lights-and-cel-shading-is-a-style-per-material)).
This plan holds the research round, the design that follows from it, and the
build order.

## What exists

The outline today is a fixed edge detector in engine code
(`shade/sdf-toon-outline.hlsli`): a depth jump tested against the surface's
tangent plane, and a normal crease, read from the visibility buffer at up to 16
neighbour taps per styled pixel inside the views pass. Authors set four values
per material or as the world default (`ToonStyleDocument`: width up to four
output pixels, the distance at which width halves, the crease cosine, an ink
multiplier). The nearer surface owns a line; screens, portals and sky never ink.

The engine holds a second built-in line beside it. `applyCurvatureShading`
darkens cavities, lights ridges and draws a line of a fixed colour where the
surface's curvature passes a band, from six world values
(`render.lighting.curvature`, `SdfCurvature`). It runs on every material that
has no style. No world in the tree authors it.

What authors cannot do today: ink object or material boundaries, the light
terminator or inner lines by curvature; colour a line from the surface or the
light; vary width by curvature, light or distance in metres; taper or break a
line up in a way that holds still in world space; ink glass or water; choose
which material's ink wins at a boundary.

The cel and toon vocabulary in engine contracts (`ToonStyleDocument`,
`PaletteToonSelectDocument`, `render.toon`, `SdfToon`, the pass members
`ToonOutline` and `ToonInk`, `sdf-toon.hlsli`, the `world.toon` verb and its
lever) is a leak under [the engine is generic](../decisions/engine-design.md#the-engine-is-generic):
what it names is an edge pass, a shade-case selection by state, and a
diagnostic over authored phases.

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
field memo names, and a shade program gains a fourth phase that turns them into
ink.

- **Per-pixel scalars.** The stamp gains the world size of one display pixel
  and the surface's curvature along any direction. With those, the outline of a
  rounded form and the light terminator are level lines an author draws at a
  constant width with no neighbouring pixel.
- **A neighbour pass.** One bounded search per 8×8 tile, inside the pass that
  already filters each view's shadows, finds the jumps: an occlusion, a sharp
  crease, a boundary between two objects, two parts or two materials. It writes
  each pixel's distance to the nearest edge of each class, in display pixels.
- **What the bake carries.** A shape declares a part. The bake's rule that
  already tracks which shape won each surface point tracks the part too, and
  how nearly the runner-up won, so the mesh holds part boundaries and the seam
  of a smooth blend.
- **The ink phase.** A program's `ink` block runs once a pixel after `compose`,
  reads the signals and the colour under it, and writes a colour and a
  coverage. The engine runs it only where one of its line inputs is live.

Width has one unit, the display pixel a viewer sees, in every view. Every look
value (which edges ink, width, taper, colour, breakup) is a parameter of a
program in the standard package. The toon and cel names leave the engine with
no alias, and so does the built-in curvature line, whose three terms become a
standard program.

### Where the memos disagreed, and what the code says

| Question | The memos | The code | Decided |
|---|---|---|---|
| What identity marks an object boundary? | The survey compares the visibility record's identity word. The GPU and field memos both say that word is the wrong key | The identity's source is the draw (`sdfVisibilitySource`). `SdfMeshDresser` gives a chunked group one draw per chunk, and a static creation bakes as one group whatever its shape count (`SdfInstanceRange.BakesWithPrevious`). A comparison of draws inks chunk seams and misses every part | An object key the dresser gives every draw of one group or copy, and a part the bake carries per triangle ([below](#what-the-bake-carries)) |
| Who draws the outline of a rounded form and the terminator: arithmetic at the pixel, or the neighbour pass? | The field memo draws both from scalars. The GPU memo brackets both between neighbours | Both lines are level lines of a cosine the stamp already holds. The field memo's template is admitted today at about a third of `cel`'s static cost | Arithmetic at the pixel, with the neighbour pass kept for what has no slope: sharp silhouettes and faceted forms. On a rounded form the engine reports the nearer of the two estimates as one distance |
| How is the distance to an edge found? | The GPU memo proposes sub-pixel seeds joined into segments, tile lists by indirect dispatch, and a radius-limited flood for wide strokes. The current code samples two rings | The views filter already loads each tile with an apron into group memory (`sdfViewsGatherFilter`), and its record already travels to the shading bins | A bounded search over seeds in that group's memory, with no flood, no tile list and no extra dispatch. Segments and a wide path are later steps a capture has to ask for |
| Where does curvature along a direction come from? | The field memo writes the view's curvature in the record and re-reads the triangle in the views pass for each light's | The surface pass already forms the change of the normal across the triangle for the mean (`sdfMeshMeanCurvature`). Views would re-read three vertices a pixel, and more for a skinned mesh | The surface pass writes the curvature's two remaining components once, and any direction is arithmetic from them ([below](#per-pixel-scalars)) |
| Which side of a boundary draws? | The survey asks for an authored priority between neighbouring materials. The GPU memo asks for priority, placement inside or outside, and a stable tie-break | A pixel runs its own material's program and can read no other material's rows | Each side draws its own half of a line, from its own program. A material that wants no line at a boundary draws none there. A stroke painted onto another surface's pixels is [not in this design](#what-this-design-does-not-do) |
| Is `surface.curvature` enough for creases? | The survey offers it for ridges and valleys | A sharp crease is split into two vertices with each face's normal (`SdfContour.SplitAtCreases`), so both of its sides read zero | Curvature draws lines on smooth forms only. Sharp creases are the neighbour pass's |

### The edge signals

Every signal reaches a shade program as a member of its stamp
(`SdfShadeOps.Inputs`) or as an op (`SdfShadeOps.Table`). A program has no
buffer address and cannot read a neighbouring pixel, so this list is all an ink
phase can know.

| Signal | Kind of line | Computed by | Reaches the stamp as |
|---|---|---|---|
| The world size of a display pixel | The unit of every width | The frame block, per view | `surface.pixel`, in metres |
| Curvature along a direction | Outline of a rounded form, terminator, ridges and valleys | The surface pass, from the hit triangle | The op `curvatureAlong(direction)` and `surface.curvatureSpread`, beside today's `surface.curvature` |
| The light's cosine and its line | Terminator, band edges, hatching in shade | The light phase | `light.cosine` as today, and the program's own `line` output |
| Occlusion | Silhouettes, one form in front of another | The neighbour pass, refined at the pixel on rounded forms | `edge.depth`, with `edge.depthGap` |
| Crease | A sharp fold in one surface | The neighbour pass | `edge.crease`, with `edge.creaseAngle` |
| Object boundary | Two creations, or two copies of one, touching | The neighbour pass, over the object key | `edge.object` |
| Part boundary | Two declared parts of one creation meeting at a hard seam | The bake places it on mesh edges, and the neighbour pass finds it | `edge.part` |
| Material boundary | Two materials on one surface | The neighbour pass | `edge.material` |
| Blend seam | The centre line of a smooth blend between two parts | The bake, per vertex, and the surface pass | `edge.seam` |
| Direction to the nearest edge | Width by stroke angle, hatching along an edge | The neighbour pass | `edge.direction`, a unit vector on the screen |
| Contact behind glass | A shoreline | The glass pass | `surface.contact` as today, and `surface.contactSlope` |

Every `edge.*` distance is in display pixels and saturates at the pass's reach,
past which it reads as far. Any phase may read these members. A surface phase
that reads `edge.crease` can wear the true edges of a box, which the mean
curvature cannot find.

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
| **Write the two components once (taken)** | One record word a pixel, about 15 MB for one view at 2560 by 1440, written every frame | A triangle's curvature is constant inside it, so a coarse level may step ([step 2](#build-order) reads that) |

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

**The problem.** A sharp crease, a part boundary and an occlusion are jumps.
Today each styled pixel reads up to sixteen neighbours' depth and normal from
the record, inside the shading kernel, and gets back one blended response: no
distance, no class, and nothing for a line thinner than the gap between its two
rings of taps.

**What we propose.** The pass that filters a view's shadows already loads each
8×8 tile with an apron into group memory. A view with ink demand widens that
apron to the reach and does three things in the same group:

1. *Seeds.* It compares each pixel with its right and lower neighbours, once a
   pair, and marks a seed between them for each class that jumps there. An
   occlusion is the current test: the neighbour's point lies off this pixel's
   tangent plane by more than a few pixel footprints, and the nearer surface
   owns the seed. A crease is two hits at one depth whose normals turn by more
   than the turn the bake splits a crease at (`SdfContour.CreaseCosine`), with
   the sign that says ridge or valley. An object, part or material boundary is
   two hits at one depth whose keys differ.
2. *Search.* Each pixel finds its nearest seed of each class within the reach.
   A group with no seed skips the search, which is most of a typical image.
3. *Record.* It writes each pixel's distances and the nearest edge's
   attributes into the filter record beside the filtered shadows, where the
   shading bins already read.

The unsplit views kernel, which gathers its own filter tile, calls the same
module, so the search has one implementation and two call sites, as the filter
has now.

**The key.** The comparison reads a word that holds only what an author means
by a boundary. The dresser gives every draw of one bake group, each of its
chunks included, and every placed copy, one object key in the spare bits of the
draw record's flags word (`SdfMeshRecord.FlagsWord` uses four of its 32). The
surface pass writes that key, the triangle's part, the blend seam's distance
and one flag into a second new record word. The flag marks a hit beside its
draw's clip plane, and the pass makes no seed there, so a body cut by a
portal's glass draws no line along the cut.

**The reach** is a kernel constant, four render pixels in the first build,
which is the width the current style admits. Distances are reported in display
pixels and saturate at four, so a line is the same width at every render scale.
A line wider than that from a jump needs the wide path, a later step. A wide
line on a rounded form does not: an author can draw one from `curvatureAlong`
at any width.

**Why not the larger designs.** The GPU memo's arithmetic for a flood across
the whole view is up to 1.77 GB of buffer traffic a frame at 2560 by 1440, and
its own recommendation is the bounded search. Segments joined from sub-pixel
crossings fix a scalloped edge on diagonals at widths near one pixel. Whether
that scallop shows through the temporal resolve is a capture's question, so
[step 4](#build-order) reads it before segments are built. Tile lists and
indirect dispatch are what the shade bins already do for the kernels that shade.
The search does not need a second set, because a group with no seed returns
before it searches.

**Where it fails.** A jump is placed to the render grid, so its line moves by
up to half a render pixel as the jitter moves, and settles under the temporal
resolve. A feature thinner than a render pixel that no sample hits is absent.
Within one class a weak edge nearer than a strong one is the one reported.

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

**The problem.** The ruling asks for an authored phase that decides which edges
ink, how wide, what colour and how broken, and that runs only where an edge is
near. A shade program's phases are straight-line code that admission prices for
every pixel of a group (`SdfShadeProgram.PixelWork`), so "only where an edge is
near" has to be a test around the phase.

**What we propose.** `SdfShadePhase` gains a fourth member. `WorldShadeLowering`
reads a phase's block name from that enum, so the spelling is an `ink { }`
block beside `surface`, `light` and `compose`.

| | Members |
|---|---|
| Reads | Everything a compose phase reads from the stamp, the edge signals above, `ink.under` (the colour the compose phase produced) and `ink.line` |
| Writes | `color`, three linear values of scene light, whose default is `ink.under`; and `coverage`, whose default is zero |
| The engine then shows | `ink.under` moved toward `color` by `coverage` |

Dark ink that multiplies is `color = ink.under * tint`. An opaque line is
`color = inkColor`. A bright rim is `color = ink.under + glow`. One pair of
outputs covers the three ways the survey found lines composed.

**The `line` channel.** A terminator belongs to one light, and the ink phase
runs after the lights are folded. So the light phase gains one output, `line`,
a number from zero to one. The light walk keeps the largest value over the
lights it visits. The compose phase reads that as `compose.line` and may write
its own `line` (the default passes the fold through), which is how a line from
any other per-pixel quantity, such as a curvature ridge or a band of hatching,
enters. The ink phase reads the result as `ink.line`.

**The gate.** The ink phase runs at a pixel when one of its line inputs is
live: an `edge.*` distance it reads is within the reach, or `ink.line` is above
zero. Elsewhere the pixel keeps the compose colour and no ink instruction
executes. This is a definition, so it needs no proof about the program: an
author who wants ink somewhere raises `line` there, with cheap arithmetic in
compose, and the costly part of a look (a noise for breakup, a colour ramp)
runs only where a line is. An ink phase that reads neither an `edge.*` distance
nor `ink.line` could never run and is refused by name at admission.

**Composition.** A palette row's `shade [a, b]` list lowers its elements into
one definition, a later output replacing an earlier one
(`WorldShadeLowering.Program`). The ink phase follows that rule as the light
and compose phases do, and outputs replace by name, so
`shade [base.shade, cel(...), terminator(...), ink(...)]` is one program: the
row's own surface, the band, a `line` written beside the band's `diffuse`, and
the ink. The program is one row of the world's `shades` table under its
content identity, and a use names it by key, as
[authored shading](../decisions/rendering.md#authored-shading) holds. Width,
colour and breakup are parameters, which are data outside the identity, so
every material that inks with one template shares one program, one kernel arm
and one bin whatever its values. A material with no program inherits the
world's `render.shade` whole, its ink phase included, which is what replaces
`render.toon`. A program with no ink phase draws no ink.

**Where the looks live.** `worlds/standard/ink.puck` holds the functions
(`inkLine`, `inkContour`, `inkTerminator`, a breakup anchored to
`surface.layerPosition`) and the templates built from them. The values the
standard's styles carry today as a style row (`celStyle`'s width, halving
distance, crease cosine and tint in `worlds/standard/styles.puck`) become
arguments of those templates, and `celMaterial` composes the template into the
row's list.

**What authors gain, against the survey's list.** Per-class selection and
weight (each class is its own distance). Distance to the edge for any width
profile. Width in display pixels or, divided by `surface.pixel`, in metres, by
distance, view angle, light or curvature. Taper by strength
(`edge.creaseAngle`, `edge.depthGap`, the curvature). Colour from the surface,
the light or a fixed hue, in three composing modes. Breakup that rides the
creation (`surface.layerPosition`, `lattice`), or steps on a quantized `clock`.
Stroke direction (`edge.direction`). Opting out by having no ink phase.

### One unit for width

**The problem.** `surface.footprint` is the world size of one pixel of the grid
the views pass runs on. The current outline corrects its width for the render
grid (`SdfFrameBlock.Write` passes `SdfViewSnapshot.RenderGrid`) and for
nothing else. A window fitted to its glass renders an image that is then shown
at the size the glass spans, which follows the image's size only in quantized
steps. So a body standing half through a portal, which two views draw, can
change outline width at the glass.

**What we propose.** One unit, the display pixel: a pixel of the image the
viewer sees. Each view's frame block carries how many display pixels one of its
render pixels spans. That is the height its picture is shown at over its render
rows, where the shown height is `SdfViewSnapshot.DemandHeight` when the view
has one and its output height otherwise. `DemandHeight` is already the display
pixels a fitted window's glass spans from the viewer's eye, and a camera filmed
for a screen already sets it to its own image's height, so a screen's picture
keeps lines in its own pixels and a window keeps them in the viewer's.
`surface.pixel` is the footprint divided by that ratio. `surface.footprint`
stays the sample spacing, which is what an antialiasing ramp wants.

Every `edge.*` distance is in this unit. A width in metres is the same width
divided by `surface.pixel`, so the survey's two units are one member.

**Where it stops.** A line cannot be drawn thinner than a render pixel, only
fainter, so below one render pixel a line holds its width by coverage. A nested
view that renders more than one render pixel per display pixel reaches fewer
display pixels with the same search, so a jump line near the reach's limit
narrows there.

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
([step 9](#build-order)). The mechanism, if the capture asks for it: the ink
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
| Record words | Two more a pixel in the visibility record (ten for eight), about 30 MB for one view at 2560 by 1440. Two more in the filter record, the same again, allocated only in a graph that inks | `world.residency` and the memory report. The author-shaders plan's [planar record](author-shaders.md#image-history-split-and-measured-shading-cuts) item decides the layout |
| The neighbour pass | A fixed number of loads and comparisons a tile, set by the reach and nothing in the scene. Zero search in a tile with no seed. No extra dispatch | `world.counters gpu pass sdf.world$views-filter`, with detail rows for tiles searched and tiles skipped, and `world.gpu-timing` for the pass |
| The ink phase | Its allocated instructions once a pixel, added to `SdfShadeProgram.PixelWork` and checked against `SdfShadeProgram.MaxWork` as if every pixel ran it. The gate only lowers what executes | `world.budget` prints the phase's static cost and which edge classes the program reads. `sdfWorkSteps` counts executed instructions, so the gate's saving shows in the views pass's work counter. The `shade-cost` view includes the phase |
| A view with no ink demand | Nothing beyond the two record words: the pass, the filter words and the phase are not recorded | The same counters, reading zero |
| The bake | One rebake of every stored mesh, once. One word a vertex on a mesh that declares parts | `puck mesh identity --against`, `puck creation stats` |

The GPU memo's planning estimate for the new passes is 0.6 to 1.3 ms a view at
2560 by 1440 on the RTX 4070, before the current outline's taps are retired.
It is an estimate from assumed memory traffic, and
[step 5](#build-order) replaces it with a reading on the benchmark route.

A debug view, `edges`, shows each class's distance as a colour, so a look
critic can tell what the engine found from what a program drew.

### What is deleted

No alias, no deprecation step and no read-side tolerance is kept
([rule 5](../../AGENTS.md#core-rules)). A document that still spells a removed
member is refused by name.

| Removed | What replaces it |
|---|---|
| `ToonStyleDocument`, a palette row's `toon`, `render.toon`, `SdfToon`, `SdfMaterial.Toon`, `SdfFrame.Toon`, the style rows `SdfAppearance.Pack` writes (its row 25 keeps the glass shadow density it also holds), the pass members `SdfWorldPackage.ToonOutline` and `ToonInk`, `shade/sdf-toon.hlsli` and `shade/sdf-toon-outline.hlsli` | The ink phase of the row's program, and of `render.shade` for the world's default. There is no styled mark: a material inks when its program has an ink phase |
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
- `render.lighting.curvature` is refused by name.

The standing rule that a program omitting a phase takes the engine's default
means a material without the template shades exactly as an unauthored world
does today, where every gain is zero.

### The invariant

An edge signal is a function of the surface point, the view, the light and the
program. It is never a function of the render grid, of which draw or chunk a
triangle fell in, or of which of two views is looking.

What enforces it:

- *The stamp is the only door.* A shade program reads the members
  `SdfShadeOps.Inputs` lists and nothing else, so an ink phase cannot depend on
  anything the engine did not hand it. The members are defined in metres and
  display pixels, so every program inherits the property.
- *The comparison cannot see a draw.* The neighbour pass reads the record's
  depth, normal, material and key words. The key word holds the object, the
  part, the seam and the clip flag, and no draw index, so a chunk seam has
  nothing to differ by.
- *One search.* The module that seeds and searches is included by the filter
  pass and the unsplit views kernel and by nothing else. The stamp's `edge.*`
  members are filled only from its record.
- *One ink door.* `SdfShadeHlsl` generates the ink phase's door, with the gate
  and the composition of `color` by `coverage` inside it, for the views and
  glass kernels alike. No caller can run the phase ungated or compose its
  outputs another way.

Where the guarantee stops: a jump is found on the render grid, so its placement
(not its width) depends on the grid. Values the bake samples hold only to the
mesh's resolution. The object key separates groups and copies, so two shapes an
author bakes into one group are one object until they declare parts. Nothing
checks that a program's own arithmetic keeps the property: an author who
thresholds a raw cosine draws a line whose width follows the radius, which is
theirs to write.

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
| Whole-tile classification and bounded jobs | The shade bins and `SdfShadeDispatch` | Used as they are. Ink adds no bin, since a pixel that inks already has a program |
| A cost only readers pay | `SdfShadeReads` and `SdfAppearance.ResolveShadeReads` | Extended with the edge read, which is a view's ink demand |
| A bound proved from the program | The wrap and tilt bounds behind `sdfMaterialShadowDemand` | Considered for the gate and set aside: the gate is a definition over `line`, which needs no proof |
| Curvature at a pixel | `sdfMeshMeanCurvature` | Extended to the two remaining components, not duplicated in the views pass |
| Which shape won a point | `sdfComposeCandidate`, `SdfFieldEvaluator` | Extended with the part and the margin |
| Exact boundaries on the mesh | `SdfMeshMaterialCut` | Reused for part boundaries |
| The share of each owner at a vertex | `SdfMeshSkin.Build`, `SdfMeshInfluence` | Already resident for skinned groups. Its per-vertex CPU walk is too costly for every static vertex, so the margin rides the bake's sample |
| The display size of a nested view | `SdfViewSnapshot.DemandHeight`, `WorldWindowFrustumFit.ShownHeight` | Reused as the width unit's source |
| A look swapped by state | `PaletteToonSelectDocument` | Kept as a mechanism and renamed for what it is |
| Suppressing authored phases for comparison | `world.toon` and `sdfShadeHas` | Generalized to a mask over phases |
| A crisp authored line on a surface | `sdfApplyPaints`, the `shape` and `lines` ops | Used as they are, in any phase |

## Build order

Each step lands alone, and the debug world keeps its outline throughout: the
current detector runs until step 5 has moved the standard package off it, and
step 6 deletes it. Steps 1 to 3 touch the same generated tables
(`SdfShadeOps`, the frame block) and go in sequence. Step 7 waits for the
terrain plan's batch of bake kernel edits and can land any time after step 4.

The check world is `worlds/debug/ink-checks.puck`: a sphere and a capsule of
one radius, a box, a smooth blend of two spheres and a hard union of two boxes,
under the field memo's template, which draws a terminator and an inside outline
from the stamp as it stands. Captures are settled stills with the exposure
pinned and the camera fixed, as
[the authored-shading reference](../../.claude/skills/rendering/references/authored-shading.md)
describes, unless a step says a presented burst.

1. **One unit for width.** The frame block carries each view's display pixels
   per render pixel, the stamp gains `surface.pixel`, and the current outline
   scales its width by the new member in place of the render grid.
   - *Check:* `SdfFrameBlockLawTests` reads the member for a view in the
     output, a filmed screen and a fitted window. In a capture of the gallery,
     a body standing through a portal shows one outline width on both sides of
     the glass, and the outline's width in output pixels is the same at render
     scales of one and one half, each within a pixel. The same capture records
     whether a line is drawn along the cut at the glass today, which says
     whether step 4's clip flag fixes a defect or only prevents one.
2. **Curvature along a direction.** The surface pass writes the two components
   beside the mean, and the stamp gains `curvatureAlong` and
   `surface.curvatureSpread`. The check world's template switches from the mean
   to the op.
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
     cost. The reading of the surface pass before and after
     (`world.counters gpu pass sdf.world$surface`, `world.gpu-timing`) is a
     measurement request for the lead's quiet window.
3. **The ink phase.** `SdfShadePhase` gains its member with the `color` and
   `coverage` outputs, `ink.under`, the `line` channel through the light fold
   and compose, the gate on `ink.line`, and the phase's cost in `PixelWork` and
   `world.budget`. The generated doors serve the views and glass kernels.
   `world.shade phases` lands here beside `world.toon`.
   - *Check:* the `shade-*` spelling pairs and `ShadeLoweringTests` admit a
     four-phase program, and a program with no ink phase keeps its three
     instruction streams and its static cost. An ink phase that reads no line
     input is refused by name. In the check world, with the template's outline
     and terminator moved into an ink phase, the capture matches step 2's
     within the still tolerance, and the views pass's work counter is lower
     than with the gate forced open.
4. **The neighbour pass.** The dresser writes the object key, the surface pass
   writes the key word with the clip flag, the filter pass and the unsplit
   kernel seed and search, and the stamp gains `edge.depth`, `edge.crease`,
   `edge.object`, `edge.material`, their attributes and `edge.direction`. The
   gate reads them. The `edges` debug view lands here.
   - *Check:* a device law compares the kernel's distances with an exhaustive
     search over the same seeds on a synthetic record that holds a diagonal, a
     junction and a tile corner. In the `edges` view of the debug world: a
     sloping floor shows no occlusion edge, two copies of one creation that
     touch show an object edge, two paints on one face show a material edge, a
     chunked terrain sector shows no edge along a chunk seam, and a body
     standing through a portal shows none along the cut.
     `world.counters gpu pass sdf.world$views-filter` reports tiles searched
     and skipped, and a view with no edge-reading program records none.
   - *Decides:* a capture of the check world's box edge at 45 degrees, at
     widths of one and two pixels, shows whether seeds at pair midpoints
     scallop the line by more than a quarter of a pixel after the resolve. If
     they do, a step joins crossings into segments.
5. **The standard's ink, and the debug world on it.**
   `worlds/standard/ink.puck` gains the functions and templates. The standard's
   styles carry their ink as template arguments, `celMaterial` composes the
   template and stops writing a style row, and the debug world's places, the
   Moth's cases included, take it. This is the step that carries the look
   values.
   - *Check:* `puck compile --validate` on the debug world, and a boot.
     `world.budget` lists the ink templates once each however many rows use
     them. On [the benchmark](benchmark.md) route the lead's batch pass reads
     the views filter, each shading bin and the glass pass with the old
     detector, with the new graph and `world.shade phases` leaving out `ink`,
     and with ink on, at the render extent, backend and topology it names. A
     look critic judges each place's outline in motion against its reference:
     a slow pan, a moving actor, a changing render scale and a portal in view,
     looking for a second line at a frame, a width that jumps, and a line that
     trails onto revealed background.
6. **The toon and curvature built-ins go.** Everything under
   [what is deleted](#what-is-deleted) is removed with its tests, schema rows
   and documents, `toonSelect` becomes `shadeSelect`, and
   `curvatureAccents` lands in the standard package with a gallery exhibit. The
   decision register, the handbook's curvature shading section, the
   authored-shading reference and the author-shaders plan's style switch entry
   are rewritten in the same change.
   - *Check:* `puck search` finds no `toon` identifier under `src/`, and the
     schema, the console verb table and the kernel sources name no member for
     cel or toon. `puck schema --check` and `puck shaders generate --check`
     pass. `puck compile --validate` refuses a
     row that spells `toon`, `toonSelect` or `render.lighting.curvature`, each
     by name. `moth.style Plain` then `Cel` swaps the Moth with
     `world.counters sdf.shade` showing no compile and the bake counters no
     bake. The exhibit's capture shows cavity, ridge and line as its plaque
     says.
7. **The bake carries the part and the margin.** A shape row's `part`, the
   winner rule and its CPU twin, the material sampling's two words, part
   boundaries in the material cut, the triangle's part, the vertex's margin,
   and `edge.part` and `edge.seam` in the stamp.
   - *Check:* `puck mesh audit --cpu` on a chunked static union counts no part
     change across a chunk seam and one along each declared boundary. On the
     check world's blend, with its two spheres given parts, the audit reports
     a zero margin within one cell of the surface points where the two shapes'
     distances are equal, measured against the CPU field.
     `puck mesh identity --against` names the bake kernel and the changed bake
     members as what differs between the two builds. A world that declares no part bakes the
     triangle counts it baked before. In a capture, the blend shows a seam
     line of constant width and the stacked boxes a part line.
8. **Ink on glass.** `surface.contactSlope`, and the ink phase's coverage kept
   apart from the interface's own in the far-then-near composition.
   - *Check:* a device law shades a glass plane over a bed at a known angle
     and reads the slope that angle gives. In
     `worlds/debug/shade-surface-checks.puck`, whose shallow glass copies
     already isolate contact, extended with a bed that slopes at two angles, a
     shoreline holds one width across both, and a rim on a glass sphere matches
     the same sphere opaque. At the garden's pond a submerged stone's line
     bends with the water, and the water's own line sits on its surface.
9. **Lines in motion.** A presented burst of eight frames inside a sustained
   pan of the check world, with `world.temporal on`, at render scales of one
   and one half, at widths of one and two pixels.
   - *Decides:* whether the outline and the terminator keep their settled
     contrast while they move. If they lose it, the `slide` output and the
     resolve's use of it are built here, and the burst is taken again. The same
     burst sets the smallest width the standard's styles offer.

Later steps, each taken when a place or a look asks for it, with what it would
show:

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
| Does one triangle's curvature step on coarser levels? | Step 2's captures, in the lead's windowed pass | The check world under `world.detail 0`, `1` and `2` | Whether a per-vertex tensor step is added |
| Do seeds at pair midpoints scallop a thin diagonal line? | Step 4's capture | The box edge at 45 degrees at one and two pixels | Whether crossings are joined into segments |
| Does a thin view-dependent line hold its contrast in motion? | Step 9's burst | Eight presented frames in a pan at two render scales | Whether `slide` is built, and the standard's smallest width |
| What do two more record words cost the surface pass, and is a planar record cheaper? | The lead's quiet window, with the author-shaders plan's planar record item | The surface and views-filter passes before and after steps 2 and 4 on the benchmark route | Whether the words stay interleaved or move to planes |
| What does the new graph cost against the old detector? | Step 5's readings | The benchmark route in the three configurations the step names | Whether the reach or the search's shape changes before the old code is deleted |
