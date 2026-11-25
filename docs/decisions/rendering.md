# Rendering: decisions

The choices behind [the rendering programme](../plans/rendering.md), each with
the problem it answers and what follows from it. The implemented contract is
owned by [the shader guide](../reference/shaders.md#shader-pipelines-and-live-development)
and [multi-pass shader pipelines](../reference/shaders.md#multi-pass-shader-pipelines):
the vocabulary and its reserved terms, the JSON-authoritative model, the
compiler's ordering, liveness, and cycle diagnostics, the planner, allocation,
submission, and retirement contracts, the ownership seam between the shader
subsystem, the backends, and World, and the pause, reset, and reload semantics.
Their observable guarantees are preserved, not every current API shape.

## The frame does not interpret SDF programs

The frame does not evaluate SDF bytecode per pixel: every view rasterizes the
meshes the residency bakes the program into, and the interpreter's frame
passes, kernels and levers are deleted. The interpreter has a ceiling no tuning
reaches: even with the interpreter compiled away entirely (the upper bound of
per-world specialization) the primary and shadow passes stay several times over
the RTX 2060's frame, and cutting march steps cuts their time far less than in
proportion, because the remaining cost is the fixed price of each program walk.

Worlds are mostly static and sculpted in the editor, so evaluation belongs at
edit time, as in the shipped SDF engines that hold 60 FPS on weaker hardware.
SDF programs remain the authored representation, and the interpreter remains the
evaluator for baking, CPU queries and the editor. The frame reads what a bake
produced: baked meshes, rasterized with shadow maps. Two bake targets were
built and compared on the debug world. The baked-mesh path draws it well inside
the frame budget with the geometry intact. A sparse distance volume marched by
texture reads costs about the same, but loses the scene's materials and breaks
the avatar's composite. The frame-rate bar is read on a quiet machine
([below](#limits-price-the-mesh-pipeline-not-the-interpreter)). Interpreter
tuning stops; the specialization plan is withdrawn.
The field's partial silhouette coverage and per-pixel material blending at
smooth seams do not survive the bake: a rasterized pixel is covered or not, and
the bake point-samples a quad's winning material. Curvature survives as an
estimate: the surface pass derives mean curvature from the baked vertex normals
(`sdfMeshMeanCurvature`) into the visibility record, where curvature shading,
edge weathering and an authored program's `surface.curvature` read it. It
describes the baked normal field, so a feature finer than the bake's cell
contributes none.

## Limits price the mesh pipeline, not the interpreter

The scene ceilings sized for the per-pixel interpreter are retired. Limits
price what the mesh pipeline costs instead:

- **A rigid creation is one shared geometry asset.** A creation with no
  timeline, drivers, effectors, part dynamics, field-changing lanes, cameras or
  faces bakes once in its own space; each copy costs one root transform and one
  draw, never a stamp registration, program instances or a palette copy.
  Spawning, despawning or restyling such a body never rebuilds the program.
  An articulated creation takes a root transform plus one slot per part that
  moves on its own, not one per shape.
- **No scene ceiling is sized for a worst case.** There is no per-creation
  shape cap, no fixed registration pool and no transform reservation for
  shapes a world does not have; buffers are allocated for what the world holds
  and grow. Timings measured on the interpreter set no ceiling. Detail follows
  projected size (the one-pixel rule), never a body's index, and a body always
  wears its own look, never a substitute because a pool ran out.
- **What a creation's size costs** is bake work and resident bytes, counted
  and reported (`puck creation stats`, the bake counters), not refused by a
  shape count. The guards that remain protect representations: skin member
  indices, 24-bit slot, draw and triangle indices, exact-float ranges, the
  material sentinel and region addresses. A guard is never exceeded silently;
  past it a document is refused by name.
- **Kept as work bounds:** 64 volumes (still marched per pixel), the resident
  screen slots (`SdfProgramBuilder.MaxScreenSurfaces`, one `UInt128` mask; a
  world declares any number of screens) with the derived-face band following
  the authored screens, lights,
  sky layers and nested views, 4,096 bodies, and 32,768 solid placement
  colliders per world. `SDF_MAX_INSTANCES` bounds one bake program, not a
  world.
- **Field scope depth rises from 2 to 4.** Scopes are evaluated only by the
  bake and CPU queries.
- **Machine budgets are bytes.** Mesh residency and the geometry cache are
  sized from the machine and evicted by size and age, never cleared
  wholesale and never refused on a miss.
- **Performance is read, not ledgered.** [The frame-rate
  bar](#the-pipeline-foundation) is the worst frame at the highest quality,
  read on a quiet machine with GPU counts beside it.

## Scene light is converted to display values once, at the end of each view's graph

- **What a value means is correctness, not look.** Shading runs in linear scene
  light: twice the value is twice the light. A display expects values encoded on
  the sRGB curve. Each view whose consumer is a display (a seat's view, a filmed
  camera's, a session camera's) ends its own graph with a display pass
  (`SdfWorldPackage.WithDisplay`) that sharpens the exposed scene light at its own
  size, adds the camera's scatter, puts it through the world's tone curve, fades
  those three toward the unconverted light by the view's screen fidelity, and
  encodes on the extended sRGB curve, so headroom above white survives for an HDR
  display. The view's published image is then a working image, and no consumer
  can read it unconverted. The root's `place` pass only places, and the display
  encode decodes a working image once for an HDR output and writes it as it is for
  an SDR one.
- **Three kinds of image.** Scene light is the composite's exposed linear output
  inside a converting view. A window's light is what a view published for a window
  or a sky layer carries: scene light under the window share of its own exposure
  (`SdfViewSnapshot.Unexposed`, or `GeometryOnly` for a sky layer); such a view
  takes no display stage, and its consumer exposes the rest. A working image is
  encoded for the display. One predicate on each side decides the kind: a view
  converts unless it publishes a window's light (`SdfWorldPasses` `Entry.Converts`),
  and a screen reads its picture by its face (`SdfScreenFace`): an opening shows a
  window's light as it is, and a display decodes its working image to the linear
  light it emits, in the screen shading and in the screen emission's reduction
  alike. No runtime check yet refuses a graph that joins two kinds without the
  conversion; the open item is in [the rendering plan](../plans/rendering.md#colour-conversion--scene-light-to-display-values-once).
- **Authored colour has two types.** A hex literal spells a display colour
  (`DisplayColor`, CSS semantics); shading reads only a scene colour (`SceneColor`),
  which decoding a display colour makes. Surface colours resolve through
  `WorldBakedColors.Resolve`, environment colours (lights, sky, fog, media) through
  `WorldEnvironmentResolve`, a shading program's colour literal through its
  lowering, and an identity's body colour where it becomes a material; each
  decodes there. A letterbox, a matte fill, a pane, a HUD colour and a `source.color`
  stay display colours.
- **The encode is the engine's and the curve is the author's.** The encode has no
  parameter. The tone curve (`render.tone`, Uchimura's toe, linear section and
  shoulder) is authored data whose neutral default is no curve; the standard
  package names one (`worlds/standard/tones.puck`).

## Bloom is a camera's scatter, applied once before tonemapping

- **Where it runs.** Bloom is an optional generic camera effect on each optical
  endpoint (a seat's or a direct camera's output), after the view's HDR
  composite and exposure and before tonemapping. Glass, resolve, sky, air and
  volumes keep their order. Its final read fuses into the view's own display
  pass ([below](#scene-light-is-converted-to-display-values-once-at-the-end-of-each-views-graph))
  after the direct image is sharpened, so sharpening never acts on scattered
  light, and every optical endpoint, a filmed camera included, blooms by that
  one path. Intermediate transport images never bloom: portals,
  windows, mirror continuations, sky layers and raw machine or video textures.
  A camera used both as transport and as a direct output blooms only where it
  is shown directly. Panes, letterboxes and the overlay never bloom, and an
  engaged screen's scatter fades by its fidelity, so a fully framed screen
  shows its exact pixels.
- **How it filters.** A linear, normalized pyramid with no threshold and no
    luminance reweighting by default: a 13-position downfilter, a 3×3 tent upfilter,
  FP32 arithmetic and RGBA16F storage, six half-resolution levels at the high
  tier. Being linear, it commutes with exposure adaptation and needs no history
  of its own. Its radius is a fraction of the output's shorter side, so dynamic
  resolution never resizes its look. The combine is `(1 - s) L + s B(L)`; an
  explicitly authored soft cutoff is the one nonlinear option, and its exposure
  dependence is documented where it is authored.
    Cell-edge reflection and integrated destination-cell resampling preserve both
    constants and total HDR energy, including odd-sized levels and final placement.
    Adjacent contributions share linear samples; odd ratios can require extra
    samples per filter position. Fractional tent widths interpolate whole symmetric
    integer-width kernels, preserving energy while radius varies continuously.
- **Who owns it.** The engine default is off. A world authors `render.bloom`;
  a camera may override it; the quality tier gates its cost; each player has a
  scale from 0 to 1, where 0 is the accessibility off switch. Every diagnostic
  view bypasses it, collider and contact evidence included, and a bloom-only
  diagnostic view shows the scatter alone.
- **A screen material's one-pixel brightness knee is not bloom** and is deleted;
  the material's other treatment terms stay.
- **Its bar** is at most 0.3 ms of GPU on the RTX 4070 at 1280×900 high, with
  both backends matching, read from `world.gpu-timing` and `world.counters gpu`,
  and judged for flicker in presented bursts, never only in settled stills.

## A light is shadowed wherever it lights, and cel shading is a style per material

The illumination research round settled both; the owner ruled the second (a
style per material, not one look per world).

**Cel is the debug world's treatment (the owner's ruling, lifting the
earlier park).** The visual bar requires every place to carry a stylized
treatment, and the standard package's cel style is how the debug world meets
it: every place opts in through its materials, and the direction reviews judge
each place under it. Cel stays a style per material, never an engine default:
the engine keeps the mechanism, and the standard package chooses the values. A
cel pixel passes one tone curve (the display pass's `render.tone`), never one
inside the material as well. The cel work the
[illumination plan](../plans/illumination.md#cel-shading) had scheduled resumes.

**Ink is edge signals the engine computes and an ink phase authors write (the
owner's ruling).** Outlines are meant to be performant and expressive. The
engine computes a view's edge signals once (depth discontinuity, normal crease,
object and material boundaries, and the distance to the nearest edge) and keeps
them correct in motion, across dynamic resolution and temporal reprojection.
What an edge looks like (which edges ink, width, taper, colour, breakup) is an
authored ink phase of the shade program in the standard package, run only where
an edge is near. No look's vocabulary names the mechanism: the cel and toon
names in engine schemas, kernels, levers and verbs are a leak, removed by the
[ink plan](../plans/ink.md). The search and the phase are built
([handbook](../rendering/sdf/handbook/lighting-and-shading.md#ink)); the
engine draws no line of its own.

- **Shadow where the response is.** The surface pass queries a light's cascades
  wherever that light's diffuse response is non-zero, a wrapped material's side
  past the terminator included, and filters what it queried. A side it skips
  decodes as fully visible, so any response there is light through walls. Past
  the terminator the receiver's own body stands between it and the light, and
  that body, and only it, lets the wrap's light through. Each cascade holds two
  layers: its depth, the first face the light meets (where it enters the first
  solid it crosses), and an exit layer, the first face turned from the light
  (where it leaves that solid). A triangle facing the light compares against
  the depth; one turned from it compares against the exit layer, where its own
  face is the nearest exit unless another solid stands in front of its body.
  This is exact for a millimetre wing and a metre-thick plate alike and needs
  no thickness estimate. One from the surface's curvature fails flat geometry
  both ways (it lets a separate slab a few centimetres off a flat wing through,
  and shadows a thick flat plate by itself), and a per-vertex thickness cannot
  follow the light's angle. Reading
  the exit layer for every receiver (second-depth shadow mapping, Wang and
  Molnar 1994) would leak light along every lit floor beside a wall's foot,
  where the wall's far face meets the floor, so a receiver facing the light
  keeps the depth. Winding marks an exit only on geometry that encloses a
  solid: baked meshes, and authored meshes whose triangles close a volume,
  which are wound outward like the bake's. Everything else an author can make
  (an open or two-sided authored mesh, a body clipped at a portal's glass, a
  mirrored draw) enters the exit layer whole as a thin caster, so it still
  shadows a wrapped side behind it, and only its own wrapped side loses its
  transmission. A depth peel (the second-nearest surface) would need a
  closed-receiver flag anyway and a fragment-discard pass, and the field's own
  interior distance does not exist for authored meshes. The exit layer costs a
  second raster of each cascade's draws and one more depth image per cascade.
  The exit layer's reading of winding as facing rests on closure, consistent
  orientation, a rigid transform and no clip, none of which the raster path
  guarantees for an authored, clipped, mirrored or deformed mesh; and two depth
  layers cannot say which body a surface belongs to. The
  [transmission plan](../plans/transmission.md) therefore replaces the exit
  layer with a body identity per cascade texel beside the depth, a peel keyed
  by that identity, and the rule that any other body in front of a wrapped
  receiver shadows it while its own body shadows it only by a second chord;
  this entry is rewritten when it lands.
- **The band shapes the light, not the shadow.** A cel style bands a light's
  cosine and keeps its visibility as a separate factor,
  `band(cosine) × visibility`, so the filtered penumbra keeps its softness and
  colour. `band(cosine × visibility)` stays as an authored shape for the harder
  look. Rim lights, the environment reflection and ambient occlusion stay in the
  cel response as in the continuous one; a style never discards a term.
- **A style is material data, and its banding is a program.** Under
  [authored shading](#authored-shading) the banding is the standard's `cel`
  shade program, the one implementation: no kernel bands. Its ink is the
  program's ink phase, over the edges the view searches. The engine keeps one
  mark per material, whether it is styled: a palette row's `toon` opts its
  material in (or out, with `enabled: false`) and every other material
  inherits the world's default `render.toon`, whose program is `render.shade`.
  The standard's `celMaterial` writes the mark, the band and the ink. One scene mixes cel-styled
  and continuous creations. The concrete styles are data in the standard
  package (`worlds/standard/styles.puck`), never an engine default.
- **One lever suppresses every style.** `world.toon off` shades every styled
  material continuously and uninked, a material's own style included, so one
  view compares continuous against cel; a styled material's program keeps its
  surface phase and loses its light and compose phases, rather than any row
  being rewritten.
- **A style can follow state.** A palette row's `toonSelect` binds its look, a
  style mark and a program per case, to a state cell, so a command that writes
  the cell swaps the look with no rebake and no compile (every case's program
  is declared). The Moth supports both cel and plain looks this way, swappable
  live for playtesting as binding layouts are (the owner's ruling): `moth.style
  Cel|Plain` writes the cell her palette rows read.
- **A program that omits the surface phase keeps the material's layers.**
  `cel` only lights and composes, so a banded material keeps its weathering,
  inset and flow until the layers themselves migrate
  ([the plan's step 6](../plans/author-shaders.md#the-remaining-steps)); a
  program with a surface phase replaces them. Programs run by default
  (`world.shade programs`), since the cel treatment is one.


## Authored shading

Authors bring their own shading as **authored shading programs**, never as
shader source (the owner's ruling puts author shaders in scope: AGENTS.md,
"Authors rule"). An author writes a `shade` program on a palette slot in
`.puck` against a fixed surface interface. `SdfShadeProgram.Compile` lowers it
to one canonical op stream, which the shading kernels interpret at once and
the engine specializes in the background into a kernel of its own, a derived
product identified from its inputs.
[The authored-shading reference](../../.claude/skills/rendering/references/authored-shading.md)
owns the implemented contract and [the plan](../plans/author-shaders.md) the
remaining steps; the research round's sources and its two citation checks are
[beside the plan](../plans/author-shaders/README.md) while it is open.

**A program, not source.** Every surveyed engine takes shader source somewhere
(Unreal's custom node, Unity's custom function node, Godot's shading language,
Bevy's per-material WGSL); a few lower a program of their own (Destiny's TFX
expressions, MaterialX) or expose parameters alone (Dreams). Puck takes the
program, for reasons that are its own rulings:

- *Cost is attributed before the GPU sees it.* A program the engine lowers has
  a static cost the compiler counts and `world.budget` prints. Raw source has
  none, and the spelling-equivalence guarantee cannot extend over text an
  external compiler sees.
- *Termination and memory safety come from the language, not the driver.* No
  runtime protects the frame: an operating system resets a hung adapter after
  seconds, and the bar is 16.7 ms. So the bound is static. Every loop unrolls
  at compile time, every call inlines, every operand is relative to the
  program's own slice, and a program whose worst case exceeds
  `SdfShadeProgram.MaxWork` is refused by name.
- *One lowering owns both backends' gaps*, which raw source would hand to
  every author.
- *No simulation state, by construction.* A program reads only the stamp
  (`SdfShadeOps.Inputs`). There is no spelling for a buffer, a descriptor, a
  neighbour pixel, a world cell or another world, so a program is a
  presentation-only float path outside the determinism contract.

What a program cannot do follows from those bounds: bring a resource, add a
pass, loop a count the compiler cannot see, or read another pixel. Expression
grows by adding ops to the one table (`SdfShadeOps.Table`), each with a
declared cost, never by opening the bounds.

**A surface function the engine lights, in three phases.** A program has a
`surface` phase before the light walk (the material's final response and
shading normal), a `light` phase once per light the walk visits and a
`compose` phase after the fold. The walk leaves out lights that cannot reach
the view or the 8×8 tile (past a light's range or outside a spot's cone), and
traced lights the surface's probes do not see, so a light phase cannot rely on
running where its light's `reach` is zero or where a traced light's
`probeShare` is zero
([the rule](#a-pixel-walks-the-lights-that-reach-it-and-that-its-probes-see)).
A phase it omits takes the standard's default. The engine keeps what
a per-pixel program cannot express: the shadow filter, the irradiance gather,
the edge search (it reads neighbouring depth, normal, object and material, and
hands a program the distances), local adaptation and the atmosphere. One generated invocation serves opaque and glass shading. The
surface pass, the bake, the cascades and the irradiance trace never run a
program.

**The probes and the meter see the base response.** A palette slot's base
`color`, `metal`, `emissive` and `bleed` are the lighting-representative values
the irradiance relight and the exposure meter read; a colour or an emission a
program computes does not bounce and does not meter. The standard's templates
derive their look from those base values, and `world.budget` names a program
whose emission is computed.

**Parameters are data, and structure is identity.** A template's parameters
are runtime slots in the material's rows unless the lowering finds one in a
structural position (a loop range, a shape's type), where it must be a
compile-time constant marked `structural`. A program's identity is its
canonical stream, its parameter layout and the op table's fingerprint, never
its parameter values, so two materials that differ by a seed or a colour share
one program, one kernel and one bin, and sweeping a colour compiles nothing.
Every spelling that lowers to the same stream costs the same.

**A world holds each program once, and a use names it by content.** A style
used on a hundred creations is one program and a hundred references, in the
compilation, the world definition, its projection and the renderer alike
(AGENTS.md, "Ergonomic is optimal"). Copied into every palette row, the
standard's `cel` alone would be thousands of expression nodes a row, and the
gallery's plinths and labels together would pass one compilation's work limit.
Two shapes were weighed: a deduplicating compact form of each inline
copy, and a table the uses name. A compact form still repeats the program in
every creation document and in every reader's memory, so the table was taken:
the world's `shades` table holds each program under its identity, the
SHA-256 of its canonical definition, and a palette row, a look case and
`render.shade` each write that key. The identity is computed from the program,
never authored or recorded, so equal programs from any document are one row,
composition adopts rows whole, and a creation's canonical hash, which covers
its keys, covers what it shades with. The table a definition writes is derived
from its uses, so it never holds a program nothing names. Every door that turns
bytes into a live world resolves the keys against the table, and a key the
table does not hold, or a row whose key is not its program's identity, is
refused there. Nothing changes for the renderer: it already compiled programs
by content and specialized by identity. The bake never reads a program.

**Specialize behind, and keep frames smooth while compiling.** A specialized
kernel replaces the running one at a frame boundary when its background compile
arrives. Measured on the RTX 4070, the interpreter costs 60 to 420 ms a frame on
the debug world, which breaks the rapid-iteration ruling more than a few seconds
of an older look does, so while a compile runs the frame keeps the last
specialized kernel for the programs it already had and draws plain shading for
a program no specialization has seen yet; the interpreter runs only when asked
(`world.shaders.tier interpreted`). Following from the owner's rapid-iteration
ruling (AGENTS.md). Compiling a views-sized kernel takes
seconds per target, which is why specialization is never on the edit path. A
world's programs are declared in its documents, so `world prepare` compiles
them for a player build and the interpreter covers only edits and misses. Both
tiers are generated from the one op table under one stated semantics (32-bit
values, `precise` results, folding only of exactly rounded operations,
sanitized outputs), and equivalence is claimed only for the program classes a
threshold-sensitive corpus holds on both backends.

**Specialization is by program set, with no interpreter arm.** A specialized
kernel holds every program the frame's set names and selects a pixel's arm
once. An interpreter default arm was set aside: registers are allocated for
the whole kernel, so it would keep the interpreter's register count in every
specialized kernel. A frame whose active programs the published set holds only
in part records with that set while the new one compiles: a program with no arm
binds none and shades as a material with no program, so the default arm is the
plain shading, which costs no registers. A frame with no program, or with no
published set yet, records plain kernels that hold no program state.

**Pixels shade in bins by whole tiles.** A frame that runs programs classifies
8×8 tiles after the tile-coherent filter and shades the tiles with no program
on a plain kernel, so a pixel that runs no program does not pay a program
kernel's registers. The filtered occlusion and shadows go to a record of their
own, because a group writing them into the visibility record would race a
neighbouring group's gather, and the filter pass writes each tile's light masks
beside its bin, so a binned views kernel reads everything it shades a pixel by
from records and holds no group state. A tile is still the unit a job shades:
a list of shading samples in a tile's place is the
[rendering plan](../plans/rendering.md#gpu-submission-and-routed-worlds)'s next
step for this pass.

**Pipeline state stays engine-owned.** A program changes compute bytecode and
nothing else: no authored blend, depth, format, binding or workgroup size. The
pipelines a world's shading needs are therefore its declared program sets,
listed by `world.budget` and compiled by `world prepare`, and nothing an author
writes makes a driver specialize a pipeline the engine did not enumerate.

**The invariant.** Every instruction the GPU executes on an authored program's
behalf is either the interpreter kernel, whose per-pixel work is bounded by the
program's static cost and which reads only the program's own slice, or a
kernel the engine emitted from the same canonical stream through the same op
table; every shading dispatch that may run a program is a job sized to at most
`SdfShadeProgram.MaxWork` of predicted work; and no shadow slot a program's
response can reach is left unqueried. Five doors enforce it, and no caller has
a way around one:

1. *The compiler is the only writer of program words.* `SdfShadeProgram` has a
   private constructor and `Compile` is its only admission door; a program
   reaches the appearance words only through `SdfAppearance.Pack`.
2. *One op table, one generator, one semantics* (`SdfShadeOps.Table`,
   `SdfShadeHlsl`). `puck shaders generate --check` fails on drift, and the
   table's fingerprint joins the kernel stamp and every specialization's
   identity, so a kernel built against another table is refused.
3. *One slice reader.* Both tiers and the shape helpers read program words
   through the reader that takes the slice from the material row.
4. *One planner.* `SdfShadeDispatch` turns every shading extent or bin into
   bounded jobs and records their program identities; its `Record` and
   `RecordBin` are the only shading dispatch calls.
5. *One surface-pass decision.* The surface pass queries shadow slots by the
   material's packed demand (`sdfMaterialShadowDemand`), which only the
   lowering writes from the program's proven wrap and tilt bounds, and an
   authored light phase reads zero visibility on a slot that was not queried.

Where the guarantee stops: the static cost bounds work, not time, so the bound
is a measured margin and not a proof against a driver timeout, and a submission
of many jobs is not bounded. The interpreter's register allocation is bounded
by the op table, not by any one program. The emitted kernel's correctness
rests on the shader compiler and the driver, as every kernel's does.

**The owner's rulings.**

- The **light hook** ships: an author may change how a material responds to
  each light. The standard package keeps consistent lighting by default; a
  world that replaces it does so as its author's choice.
- **Image data** a program samples comes after specialization, so a texture's
  cost is charged in `world.budget` like every other op.
- The **screen effect hook** (a program over the composite's output) is
  deferred.

## A raster pass draws only what it can show

Every raster pass skips the mesh draws it cannot show any of, by a world box per
draw tested on the GPU by the selection kernel (`SdfMeshOcclusion`,
`sdf-mesh-selection.hlsli`, with `SdfMeshBox` and `SdfMeshCull` as the host-side
twin that glass visibility and update rates still use): a view's mesh and glass
passes against the view's frustum before the frame's far distance, a shadow
cascade against its light's box and, swept along the light, against the
receivers the view shows. A skipped draw lies wholly outside what the pass's
rasterizer keeps, or casts only where no visible receiver reads, so the cull
changes no pixel and needs no authoring: every world gets it, and every nested
view culls by its own camera, which for a portal window is the frustum fitted
through its glass. On the debug world it removed 31% to 95% of a frame's
triangles across eight indoor, outdoor and through-portal views
([P20](../plans/rendering.md#p20--visibility-areas-and-residency)). The test
is conservative where exactness would risk a pixel: a pixel margin past the
temporal jitter, a thousandth of the near distance in front of the near plane,
the shadow filter's reach around receivers, and no box at all for a skinned
draw whose vertices the GPU places. Occlusion, levels of detail and residency
build on the same per-draw box; authored areas are not assumed, because the
debug world's levels are open plan and name no rooms. Hidden geometry is found
by a depth-based occlusion test that needs no authoring, not by authored rooms:
each view's mesh pass draws what its previous frame found visible, builds a
pyramid of the farthest depth it drew, tests every box the box cull keeps
against it on the GPU and draws the newly visible in a second phase of the same
frame, so the test is conservative and a disocclusion is never a frame late.
Each phase keeps staged draw order. Exact coplanar depth ties across the two phases
can resolve according to the preceding visible set, including in a settled still. Glass
never occludes, and each nested view tests
against its own depth. On the debug world it removed a further 34% to 61% of
the mesh pass's triangles where walls and slabs hide exhibits
([P20](../plans/rendering.md#p20--visibility-areas-and-residency)).
The far cascade also uses current camera-depth receiver coverage and two-phase
light-space entry depth; exit and optical lists remain independent of entry
visibility. `world.cull camera` isolates cascade culling while keeping camera
occlusion, and `world.cull frustum` keeps the box cull alone for comparison.

## A pass stands on what it can show

A pass's signature folds the changes its own draws can show, not the
residency's. Each raster pass of a view holds a motion scope
(`SdfMotionScope`): the cull the pass draws under and a revision that moves when
a draw that cull keeps changes. The passes that shade what a raster pass drew
sign with its scope.

- **What it replaced.** Every pass folded two residency-wide revisions, one for
  the transform table and one for the draw records. On the debug world a few
  thousand records move every frame (grass, the standing body's breath), all
  presentation-clock motion that a paused simulation does not stop, so no pass of
  any view stood on any frame.
- **Options weighed.** Keep the residency-wide revisions and reduce the motion
  (it is authored, and real); hash each view's visible records (a cost on every
  frame for every view, and it cannot see a draw leaving); or judge each change
  once, when it is staged, against the culls the passes already draw under. The
  third costs nothing when nothing moves, and one box test a scope while
  something does.
- **What keeps it honest.** A scope's cull is the cull the pass's latest
  recording drew under, built by the same function for both
  (`SdfWorldPasses.CullOf`), and a draw is judged by the box in its record, which
  is the box every pass selects it by. A change is judged by the box before it
  and the box after it, so a draw leaving a view moves the scope as a draw
  entering does. A cull other than the one the scope held moves the revision,
  and no revision is ever reused, so a camera that returns to an earlier pose
  never returns to an earlier signature. What no draw's box bounds is named in
  one place (`SdfWorldTables`' motion scopes): a skinned draw takes a host bound
  under the shared rows and every view's pose overlay, written into its record so
  every pass selects it by that bound; a light, a medium and a paint riding a
  slot take the box they reach; a screen's emission takes the half sphere its
  range reaches in front of its face; and a whole-table seed moves every scope.
  A screen's light is eased to exactly zero at its range, sized from its peak
  luminance as a point light's is, so a view past the range shows none of it.
  Emission moves a scope's lighting revision, not its revision, since no pose
  changed. The whole view signs with its scopes' revisions in place of the
  residency's.
- **Where the guarantee stops.** A kernel that starts reading a transform slot
  for something that list does not name leaves a pass standing on a stale image,
  and nothing but that list catches it. An opening (a portal's window) shows
  luminance no peak bounds, so its emission reaches every scope that shades.
  The skinned bound is checked against the CPU twin of the pose
  (`SdfMeshSkinBoundLawTests`), not against the kernel.
- **At scale.** A zone is a world with its own residency, so a view pays for the
  movers some view of its own zone can show: a body no view or cascade can show
  already holds its pose (`SdfUpdateRates.Bodies`), and each one that moves is
  judged against each scope until the scope's first change of the frame.

Anything that trades a visible change for cost, such as a level of detail, an
unloaded residency ring or a culled small object, switches only beyond the
distance at which the switch is barely perceptible (the owner's ruling). The
switch distance is derived from the change's size on screen at the view's
resolution, not fixed in metres.

The owner's rulings for levels of detail:

- A coarser level of a baked mesh is drawn where its recorded geometric error
  projects to at most one display pixel. A view switches back to the finer
  level only above 1.1 pixels, so a level does not flicker at the boundary. A
  shadow cascade takes the coarsest level whose error is within one of its
  texels.
- The first level is lossless: coplanar lattice quads merged into the polygons
  they tile, changing no pixel.
- The irradiance trace reads that merged mesh, which never changes with the
  camera.
- A static union larger than one chunk (the hall) is split into chunks only once
  the merge's savings are measured.
- On-screen sizes for levels of detail and update rates are measured in display
  pixels, after upscaling, never in render-grid pixels.

The first level is part of the bake (`SdfMeshPlanarMerge`): the store keeps it,
every pass draws it and the irradiance trace reads it. It keeps every vertex on a
merged region's outline, a straight run two merged faces share included, and
removes only vertices inside a region, so every outline edge and every vertex
normal is the contour's own and the rasterizer covers each polygon as it covered
the lattice's quads. Dropping the shared run's vertices from both faces would keep
the geometry but have the rasterizer snap the shared edge from other vertices; on
the debug world it would save one part in a hundred of the triangles left, too
little to give up an exact outline for. The one pixel change a merged face can
make is where it meets a separately baked mesh, whose depth test the face's new
rounding can turn, as two coincident meshes' pixels turn.

The two coarser levels are part of the bake too (`SdfMeshCollapse`): half-edge
collapses of the merged mesh, so a level is a triangle list over the mesh's own
vertices with their own normals, and the region holds only its indices and
triangle materials, numbered after the merged triangles. A level's error is
measured, not estimated from the quadric that orders the collapses: every vertex
of the merged mesh stays within it of a level triangle of its own material, and
every vertex and edge midpoint of a crease or a material's edge stays within it
of that line as the level draws it, and every level triangle's centroid and edge
midpoints stay within it of the surface it replaces, so no triangle cuts through
a thin rod whose samples it passes between; a collapse is taken only where all
three hold (a quarter of the contour cell for the first level, two cells for the
second).
The lines are measured as lines because a surface measure alone misses an
outline that moves within its face's plane: a triangle filling a notch in a face's
outline holds the notch's vertices at no distance, and the face shows surface
where there was none. Shading needs a bound geometry alone does not give: a
coarse triangle can stay within a pixel of a bumpy surface while its interpolated
normal smooths the bumps away, so a collapse also keeps each vertex's normal within
2 degrees (first level) or 6 degrees (second) of the level's normal there. A crease
place slides only along its crease with each side keeping its own vertices, a
corner where three faces meet and a mesh's open edge never move, and no collapse
turns a triangle against its normals, which the back-face cull would show as a
hole. A level saving less than a quarter of the finer level's triangles is not
kept. The projection of the error is conservative: the error times the draw's
largest stretch, over the nearest forward depth of the draw's box, at the
frustum corner's sharpest projection of a displacement; a box reaching the eye's
plane draws the merged mesh. Glass draws its merged mesh, since its thickness is
the sums of its own triangles' distances, and a frame held for a settled capture
chooses without the previous frame's level, so its still owes nothing to earlier
frames.

Large static unions partition their contour triangles into spatial chunks before
the planar merge and collapse (`SdfMeshChunks`). A triangle stays whole in its
centroid's bin; the chunk's box encloses the whole surface it draws. Every chunk
keeps every vertex on its open boundary during merging and locks it during collapse,
so adjacent chunks keep identical seam edges at independently chosen levels, without
a stitch or a T-junction. The existing pixel and texel rules choose each chunk's
level. The store keeps the pieces; whole-group queries read their combined merged
surface, and the irradiance trace reads the pieces' merged surfaces.

The lights follow the same rule: each view shades only the lights that can
reach a surface it shows (`SdfLightSelection`), tested with the same
`CameraSnapshot.MaySee`, a light's sphere of range against the view's frustum.
Skipping one changes no pixel, since a point or spot light's falloff is exactly
zero at its range (below). The views pass narrows that mask again for each 8×8
tile it shades (`sdfViewsTileLights`): a light is kept where its sphere of range
meets a box about the tile's hit points, and a spot where its cone also meets
them, and a screen where some of those points lie in front of its face
and its picture is not black, so a pixel walks only the lights that reach near
it. The boxes hold the
very points the light stage shades, so a tile's mask changes no pixel either,
and no light can pop at a tile edge. A tile also leaves out the traced lights
its pixels' probes do not see
([below](#a-pixel-walks-the-lights-that-reach-it-and-that-its-probes-see)).

Work that changes every frame updates less often where it is far or small on
screen (the owner's request), by the same rule: a reduced rate is a visible
trade-off, so each switches only where a held update jumps by at most two
pixels on screen for the speed it is sized for (`SdfUpdateRateTiers`). A body's
pose evaluation updates at least 30 times a second at or below 30 pixels per
metre, and likewise at least 15, 10 or 5 times a second at or below that many
pixels per metre (a far body would otherwise pose on every frame slower than
its floor), its root followed every frame, its pixels
taken in the view that shows it largest, a window's or a camera screen's view
counted by its share of the display and one the display shows none of not at
all; a nested
portal or session view renders at least 30 times a second at or below 120
display pixels high, at least 15 at or below 60, 10 at or below 40 and 5 at or below 20; a screen's source uploads
at least 30 times a second once shown at or below its own height and at least
15 at or below half of it. The far shadow entry draws its moving casters at the
rate its world authors (`SdfFarShadows`), every frame where none is authored
([a world's shadows](#a-worlds-shadows-are-drawn-once-and-cost-what-moved)).

A tier is a floor held by presentation time, since the two-pixel bound holds
only if the content really updates that often: a rated item updates on the
first frame where waiting one more frame, estimated from the latest frame's
span, would pass its period (`PresentationRate.Holds`). Counting frames of the
pacing target instead lets the real rate fall with the frame rate: paced at
141 Hz on a variable-refresh display but rendering 70 frames a second, a
five-frame hold is 14 Hz and four-pixel steps. On a fixed signal the period is a
whole number of refreshes rounded down, so the cadence stays even and never
falls below the tier, and a steady 60 Hz holds the same frames as a two- or
four-frame divisor. Off a grid the comparison allows a millisecond for the
jitter of a sampled frame time, without which a steady 60 Hz on a
variable-refresh display refreshed the far cascade on three frames of every
five instead of every second one. Frames that land within a quarter of a step of
the pacing cadence (`PresentationRate.PacedFrameLow`) are counted in whole
pacing steps as a fixed signal's are, since summing their jittered spans at an
integer ratio of the period (60 Hz frames, a 15 Hz tier) flips an interval
between n and n - 1 frames and walks two items that took a tier half a period
apart onto one frame; a frame far from the pacing step (a hitch, a display
running off its target) is counted by its time.
A tier above the display's rate holds nothing. Under
lockstep the period is whole ticks, so a capture's frames do not depend on the
display. A cap is the other meaning of a rate ("at most N Hz": an authored
refresh, or a machine's own 59.7 Hz) and stays counted in presented frames,
rounded up (`PresentationRate.FramesAtMost`); the two never share a rounding
rule. Everything is presentation: the simulation ticks every body and every
machine runs at its own rate, so game timing, audio and replay hashes do not
move. The levers extend the per-pass cadence and the render graph's refresh
(`RenderGraphRefresh.AtLeast`), never a second scheduler; `world.rates`
switches each for comparison and prints each lever's longest interval between
updates at each tier against the tier's period. Rated items spread over a
tier's period by a lead on the same floor (`PresentationPhases`), which only
moves one update earlier and never onto a step's frame, so the spread costs no
rate and needs no clock of its own. Measured on the debug world
([P20-3](../plans/rendering.md#p20--visibility-areas-and-residency)).

## A world's shadows are drawn once, and cost what moved

**The problem.** Cascades a view draws for itself, fitted to the slice of the
frustum in front of its camera, cost every view a full set and end their hold
whenever the camera turns or walks. Measured that way on the RTX 4070 at 2560
by 1440, the shadow passes summed over a frame's views cost 3 to 15 ms of the
debug world's route (9.1 + 4.7 + 1.3 ms at the garden painting), the main view
alone 3 to 6 ms, and one distant redraw about 7 ms per secondary view. The
debug world is a light scene; that cost names an architecture, not a tuning.

**The ruling.** A world has one shadow solution (`SdfWorldShadows`), owned by
its residency's tables and drawn by the residency's per-frame upload, ahead of
every view's submission, never by a view's graph. Every view binds the same
layers and reads the same cameras. The entries stay the cascade set the
surface pass already reads (near, far, secondary, distant: `SdfMeshShadowCascades`),
so the receiver side, its PCSS filter, its exit layer and its glass extinction
are unchanged; what changed is who draws them, from where, and when.

- **Anchored to the world, not to a camera's facing.** Each entry is an
  eye-centred square snapped to a grid of an eighth of its reach
  (`SdfMeshShadowCascades.CellOf`, `HalfExtentOf`), so a turn moves nothing and
  a walk moves an entry only at a cell crossing. At the views' fields of view
  the eye-centred square is no larger than the frustum slice's bounding sphere
  it replaces (18 m against 23 m for a 16 m near reach at 70 degrees and
  16:9), so nothing is given up in texels.
- **Anchors serve every view.** A world shown on many screens is a world
  looked at from many places. Views whose eyes share a near cell share an
  anchor (near, far and secondary entries); a view elsewhere takes an anchor of
  its own, up to `SdfWorldShadows.MaxAnchors`, past which it reads the nearest.
  The distant entry is one for the world. An anchor no view reads keeps its
  layers for a while and then gives them back. A view whose quality skips
  shadows takes no anchor, and an anchor's texels follow its view's quality.
- **Cached, and redrawn only for what changed the whole.** An entry's layers
  are drawn whole only when the light turns past the entry's tolerance, its eye
  crosses a cell or its texels change; the distant entry, which follows no
  motion, also when a static draw changes (`SdfWorldTables.StaticDrawRevision`),
  while the others redraw a changed static caster by its rectangles.
- **Scheduled for the world, never in one hitch.** Every whole redraw is drawn
  in shares, each a part of the draw list (`SdfMeshOcclusion`'s slices), and
  the world admits every entry's shares together: each upload draws the shares
  that fit its vertex budget (`SdfWorldShadows.UploadVertices`), redraws under
  way first, then entries no view has seen drawn, then by anchor and entry
  (far, near, secondary, distant). An entry's shares follow the vertices its
  last redraw measured (the selection's vertex words, read back with every
  shadow drawing), never fewer than the authored far period or distant
  `frames`. A world first shown therefore fills its shadows over a few frames,
  the place the player stands in first. A redraw of an entry no view reads
  draws into its own layers; one of a standing entry draws into a pair lent
  from the world's spares, which takes the entry's place when its last share
  lands, the old pair returning to the spares, so no copy and no per-entry
  back layer exists.
- **Moving casters cost the rectangles they sweep.** Every draw record a
  staging changes reports the box it held and holds (`SdfWorldTables.NoteMotion`,
  the motion scopes' one door). Each entry that follows motion judges the caster
  against where it last drew it, in its own texels; once it has moved a texel
  (`SdfWorldShadows.MoveTexels`) across the plane or along the light, the entry
  owes the rectangles where it was and where it is, merged into at most
  `MaxRectangles`. The upload clears each rectangle and draws every caster in
  its box again through a viewport of its own, from the camera of that
  rectangle of the plane, so texels land where the whole camera puts them. The
  debug world's staging reports thousands of changes a frame (most of its draws
  carry the dynamic flag), which is why capability (`SdfMeshDraw.IsStatic`)
  could not be the split; so a change first meets one comparison against the
  box the entries last judged its row by, and one that stays within the finest
  standing entry's texel of it reaches no entry. A grass blade swaying less
  than a texel costs that comparison. A redraw under way owes its own layers
  the places a caster was and is, which become the entry's owed rectangles when
  it lands.

**Authored, with neutral defaults.** The light turn an entry tolerates is the
author's (`farShadows.turnDegrees`, `distantShadows.turnDegrees`); where none
is authored it is the turn that moves the shadow of a caster as tall as the
entry's reach by a share of the penumbra the surface pass draws for it
(`SdfWorldShadows.PenumbraShare` of the filter's radius, which follows the
light's size up to `SdfKernelInterfaces.MeshShadowFilterTexels`) and a texel
besides. A held layer's step is then a small part of the softest edge it
shows, so a light with size holds for a second or more of a turning day where a
one-texel rule redrew a few times a second; a light with no size holds through
a texel. A far moving caster's shadow lags by at most the authored far period.

**Options weighed.**

| Option | What it gives | Why not, or not yet |
|---|---|---|
| Per-view cascades held between refreshes over a static layer | Little change | Fitted to each camera: every view pays, and a walking camera ends the hold |
| Shadows straight from the distance field: cone or soft traces against a global field or its bricks (Wright 2015, <https://advances.realtimerendering.com/s2015/DynamicOcclusionWithSignedDistanceFields.pdf>; Epic, distance field soft shadows, <https://dev.epicgames.com/documentation/unreal-engine/distance-field-soft-shadows-in-unreal-engine>) | Light motion is free; softness is the cone | The frame rasterizes baked meshes and keeps no world-space field ([the frame does not interpret SDF programs](#the-frame-does-not-interpret-sdf-programs)); a clipmap of bricks fine enough for 10 cm walls is a new content source, leaks through thin architecture at affordable voxel sizes ([illumination's rejected list](../plans/illumination.md#rejected-and-watched)), misses authored meshes and skinned bodies, and costs every pixel of every view every frame |
| Hardware ray-traced shadows through the probe trace's acceleration structures | No light-direction cache at all | Cost follows pixels, not motion; soft shadows need a denoiser; the structures are the probe scene's, not rebuilt for moving bodies every frame; a fallback is still a shadow map. Kept as the answer where no map reaches ([GPU capabilities](../plans/rendering-research.md#gpu-capabilities-puck-does-not-use-yet)) |
| Virtual shadow maps with cached pages (Epic, <https://dev.epicgames.com/documentation/unreal-engine/virtual-shadow-maps-in-unreal-engine>) | Resolution where receivers are; pages cached | Page tables and a physical pool are a large build; a moving sun invalidates every page as it does here. The rectangles are the page invalidation of that design at the granularity Puck can afford now; a paged pool is the next step if anchor memory binds |
| **Cached world-anchored maps, shared by every view, updated by moved rectangles (chosen)** | Costs what moved and what the light's turn forces, once per world | Light turns still redraw whole layers, held through a share of the penumbra and spread over frames by the scheduler |

**What enforces it.** `SdfWorldShadows.Decide` is the one door: it chooses each
entry's work for the upload about to be submitted and the cameras receivers
read after it; `Record` draws exactly that work; `Submitted` commits it, so a
camera no submitted upload drew is never read, and the decision freezes for
the rest of the frame once its upload is submitted. The layers a view binds
come from the same decision (`ViewOf`): a lent pair is bound only from the
upload whose share completes its redraw, and the views' binding revision moves
whenever the bound pair changes. No view's fragment holds a
shadow pass or storage (`SdfWorldPackage.NativeFragment`), so nothing else can
draw one. A view's surface pass folds `ContentRevision` into its signature, so
it renders again exactly when a layer it reads changes. Where the guarantee
stops: a caster no box bounds (a seeded motion history, an unboxed draw) owes
the whole layer, and a far moving caster's rectangles wait for the far period.

## A pixel walks the lights that reach it and that its probes see

**The problem.** The views pass visits lights a pixel at a time: each visit
loads the light, evaluates its reach and its visibility, and runs the
material's light response, an authored light phase included. Reach alone does
not bound that walk indoors. A fixture's range is sized by the darkest exposure
the world takes, so in the hall it crosses the walls and floors about its room,
and a mask of the lights whose range and cone meet a tile's hit points holds
the fixtures of the rooms beside, above and below. At the lift lobby that is
15.9 lights a pixel, of which the irradiance volume's probes see 4.7 from the
pixel; every other visit would end in a probe share of exactly zero, after the
whole of its work.

**What already held each light's visibility.** Nothing in the frame traces a
light. The key light's shadow is the world's cascades, drawn once per world and
filtered in the surface pass
([above](#a-worlds-shadows-are-drawn-once-and-cost-what-moved)). A static point
or spot light's occlusion is the volume's: each probe holds a bit for every
traced light it sees, traced in the background, and the probe lookup already
writes, for each cell of pixels, the lights any corner of its gather sees
([global illumination](#global-illumination)). A walk that reads those bits
one light at a time has already paid for the visit.

**What was weighed.**

| Option | What it would give | Decision |
|---|---|---|
| Cull each tile's lights by the probes' bits as well as by reach | The walk visits the lights that reach the tile and that some pixel of it can see. The data exists, per world, cached, and costs the tile a few words of the probe record | Built |
| A shadow map or cached visibility image per fixture | An exact shadow edge for every fixture | Not built: memory and redraws for every fixture (the hall holds a hundred), to answer what the probes' bits already answer at the volume's resolution. A fixture that needs a sharp shadow takes a shadow slot, as a second directional does |
| A world-space grid of light lists (froxels or a hashed grid) in place of the tile's boxes | The same reach cull, built once for a view | Not built: it holds no visibility unless each cell stores light bits, which is the probe volume. The tile's boxes already bound the exact points shaded |
| A per-pixel early-out only | No visit's light phase runs for an unseen light | Built as the rule's enforcement (below), but not enough alone: a wave runs the walk's steps for every light any of its lanes keeps, so the saving comes from the tile leaving the light out |

**The rule.** A traced point or spot light adds nothing at a surface where
its share of the probes about the surface is exactly zero. The default response
already gave no light there (its visibility is the share times the ambient
occlusion); the light phase's door now answers no light before it runs an
authored program (`SdfLightInputs.unseen`, tested in the generated
`sdfShadeLightPhase`), so a program's light phase cannot rely on running for a
traced light its surface's probes do not see, as it cannot for a light whose
`reach` is zero. On that rule a tile leaves out every traced light the probes
about none of its shaded pixels see (`sdfViewsTileLights`,
`sdfViewsCellLightsSeen`, `sdfViewsPixelLightsSeen`). A directional, an untraced light (one that rides an
anchor) and a screen are never left out this way: a directional's visibility is
its cascades' wherever they hold the point, and the others have no bits.

**Where a tile is decided.** One workgroup decides a tile's masks: the unsplit
views kernels in their own group and, for a binned view, the filter pass, which
writes them into the tile's words of the shade bins. The binned views kernels
read them there, so they hold no barrier and no tile state and shade any pixel
from its records alone.

**What enforces it, and where that stops.** A pixel's seen lights are a
superset of the lights whose share is above zero at it: every light where no
probe answers the pixel (its lights then go unoccluded), the `any` bits of
every cell about its tile, whose representatives its pixels blend, and the
`any` bits of the extra a pixel reads (`sdfMeshLightAgreement`, the one loop
every slot's agreement comes from). A light the tile left out is therefore unseen at every
pixel of it, and the door gives it no response, so the mask changes no pixel:
a paused still of the painting room with the cull on and off differs by at most
one level in a tenth of a percent of pixels, inside the difference between two
captures of one mode. The guarantee rests on the probes' bits as every other
reader does, so the bits answer geometry alone. A probe's bit for a traced
light says whether a ray from the probe reaches the light's position, at any
distance and whatever the light's power, range or colour, and the volume traces
every traced light, a dark one included (`SdfMeshIrradiance.Relight`,
`sdf-probe-relight-stage.hlsli`): a lamp a schedule switches on, a dimmer
brightens or a rule recolours shows in the next frame, and only its bounce waits
for the relight. The ray is blocked only where it leaves a solid between the
probe and the light, or meets an open sheet
(`SdfMeshIrradianceScene.ReachesLight`, `sdfProbeBlocksLight`): the solid that
holds a light, its lantern or housing, is entered and never left short of the
light, so a light set inside its fixture lights the street as its author meant;
a two-sided test would let the lantern hide it from every probe. A static fixture has no authored
way to opt out of the volume's occlusion
([open item](../plans/rendering.md#gpu-submission-and-routed-worlds)).

**What it costs and saves.** A tile that keeps no traced light reads no probe
record, so the open valley pays nothing for the rule. In the hall the walk's
visits fall to the lights that are seen: at the lift lobby on Direct3D 12 at
1280 by 900, 5.4 million visits a frame over 1.15 million pixels where reach
alone keeps 18.3 million. A visit costs the same whichever kernel makes it, so
the views pass's time follows the visits it no longer makes.

## A diagnostic view never covers its evidence

A debug view that flags a suspect value never draws over the pixels it flags
(the owner's ruling). Flagged pixels stay identical to `world.debug-view off`:
diagnostic views keep temporal resolve, sky, glass, fog and exposure, and their
marks are composed after resolve or in the 2D overlay, never into temporal
history or lighting.

The untouched frame is the default. Marks sit outside the evidence:
- a pin per cause, beside the item, at most 16 on screen;
- contours outside a flagged region, with an untouched gap;
- for physics, the contradicting geometry drawn in the empty space around the
  drawn surface (a collider's corners, a support caliper).

Muting the unflagged context is an optional mode, because dimming changes the
perceived brightness that exposure and lighting checks depend on.

A red flag is a named contract violated on matching state, with a witness
outside an error budget built from the system's own precision. Amber is a
suspicion and is off by default. Unknown means not evaluated, so an incomplete
scan never reads as clean. There are no allowlists or recorded baselines: a
misfiring check is made precise, or the data is fixed.

A mesh comparison certifies surface correspondence before it can be red. A
normal ray that misses a nearby edge and reaches a distant surface proves no
local discrepancy. Diagnostics search the retained mesh hierarchy for the
nearest triangle point within a precision-derived neighbourhood, including
edges, vertices and zero-distance contact. The search radius bounds coverage,
never the error budget: the returned triangle supplies its own bake precision.
No local match means unknown, never clean or red. Collider boundary separation
is the unsigned Euclidean distance; a nearest point alone does not certify
which side of a closed solid contains the witness. Reports distinguish sampled
red, clean and unknown outcomes from clustered finding counts.

Every view has a console companion, `world.debug-view report`, which runs with
the view off and headless. Each finding carries its identity, the predicate and
its witness, coverage, lifetime and provenance back to the `.puck` source. Pins
are addressable by `world.view.pick` and `goto`, which moves only the camera.
A capture with a view on saves the untouched frame, the annotated frame and the
report from one frame.

## Lights, screens and the camera are photometric

**Every light, screen and emissive surface is authored in real units, and the
frame shades in luminance (the owner's request).** A directional light's
illuminance is in lux (a clear midday sun about 100,000), a point or spot
light's luminous intensity in candela or its luminous power in lumens (a 60 W
bulb about 800), with a colour or a colour temperature in kelvin; an emissive
surface's, a sky layer's and a display's luminance is in nits (a CRT about 150).
A light's colour is scaled to unit luminance, so it gives exactly its quantity.
The frame's radiometric unit is the nit: a light record carries its illuminance
(or intensity) over π, so a Lambertian albedo ρ under an illuminance E shows
ρE/π, the irradiance volume traces and stores nits, and a light's specular lobe
scales by its illuminance. `SdfPhotometry` owns the conversions, and the `.puck`
language takes `lx`, `lm`, `cd`, `nit` and `K` on the fields that carry them.
The sky is consistent with the sun because its colours are in units of a layer
luminance whose default is a clear day's sky (10,000 nits) beside the default
100,000-lux sun, so the sky lights the ground about a fifth as brightly as the
sun does.

**A camera exposure maps the luminance to the display, before the tone curve and the encode.**
`render.exposure` is manual (an `EV100`) or automatic. Exposed light of one is SDR
white, so at an exposure that maps a white surface's light to one, an 18% grey card
encodes to the sRGB code 118.

**An automatic exposure meters the view's own image.** Each view's composite
bins every output pixel's luminance, at the view's whole exposure, into a log
histogram (`SdfExposure.MeterBins` bins over `SdfExposure.MeterStops` stops
from `SdfExposure.MeterLowestStop` below display white), weighting each pixel
toward the centre by `render.exposure.center`. The recorder reads the
histogram back once the frame's slot completes (`SdfSkyRecorder`,
`SdfViewExposures.Publish`), and `SdfExposure.Meter` averages its bins
between the `low` and `high` percentiles into a scene luminance, which aims
the exposure less compensation within the bounds. The exposure holds while
the reading stays within `render.exposure.band` stops of the one it settled
on; a reading past the band starts it following the meter, easing over the
adaptation time until it settles again, so a walk through an evenly lit room
does not move it and walking into a dark room or out under the sky does. A
camera cut holds the exposure until a reading rendered after the cut lands,
then snaps to it. The meter reads what the view shows: dynamic meshes,
anchored lights, screens, emissive fittings and the sky. Each view meters
its own image, a window's or a sky layer's view included, and a portal's
picture enters its viewer's image at the brightness the window shares give
it, so nested views compose with no special case. The neutral engine default
averages the whole picture alike with no band (`SdfExposure.Default`); the
debug world's basis authors its band, percentiles and centre weight.

**How night reads is the author's (the owner's ruling).** Night exposure is
the lead's call for the debug world, provided authors can style it as their
vision demands: the exposure's floor and curve stay authored data, keyable on
the day clock, and the engine fixes no night look. The debug world authors a
readable deep-blue night.

The readback rather than a GPU-resident exposure was chosen because the
exposure is read on the host in the same frame by every consumer that divides
by it (display-referred terms in views and glass, the window shares, the
curvature strength, the point lights' ranges, `world.exposure`): one value
owned in C#, a few frames late, against a time constant of most of a second.
Its cost is that a cut shows the previous exposure for those frames. A
rejected alternative was keeping the irradiance probes about the camera as the
meter: they measured light arriving at the camera position, which stepped by
two stops between neighbouring lattice cells along an evenly lit hall, and
could not see dynamic meshes or anchored lights. A
Puck-native option is still open for the owner: exposure authored per place
or world and blended across seams, so a world's author sets each room's key
directly instead of a meter inferring it.

The view's camera sample of the volume (`SdfMeshIrradiance.TrySampleCamera`)
is no meter: it gives the air about the camera its open sky (`airLight`) and
lights a surface no probe answers. When no visible lattice probe answers, the
tracer measures radiance at the camera itself, which measures an enclosure
smaller than a lattice cell without importing light from another room. A
missing reading holds the previous exposure; the authored EV initializes a
view before its first reading. `world.exposure` names each view's meter
reading, its target, whether the band holds or follows, and its camera
sample's source. The eye adapts across a scene
where a camera cannot, so an automatic exposure also adapts locally by an
authored share (`render.exposure.local`): the views stage moves each surface's
exposure from the view's toward the one its own illumination (the lights'
diffuse terms and the volume's indirect light, against the image meter's
reading taken as a scene of 0.4 albedo) would meter, within six stops; the
reference is the meter's held and eased reading, so it moves only when the
exposure does. The view meters the adapted image, whose reading follows that
reference by the local share of its covered weight (the composite sums the
coverage beside the bins); `SdfExposure.Settled` solves each reading for the
reference that reads itself, so the band holds as wide as authored instead of
widening by 1 / (1 − local) and holding wherever the meter arrived from. So a room beside its sunlit windows reads as a
room and the sunlit ground through the window as ground, while a dark paint
under the same light stays dark; the composite scales the sky share so the
sky's brightest texel reaches display white rather than clipping, and a window
from a dim room shows the sky's gradient. The gain reads illumination, never a
surface's own luminance, so no dark albedo is lifted by it, and the volume's share of that illumination is smooth over a cell, so the
adaptation carries no halo across a depth edge and agrees between views of one
world.
Each view's composite multiplies its output by its exposure, so a dark lobby
lit by its screens and a sunlit garden both read. A window onto another world
and a sky layer's view carry the world beyond at an exposure between its own
and the viewer's: the view beyond the glass applies its exposure to the window
share (`SdfPhotometry.WindowAdaptation`, three quarters) and the view looking
through applies the rest of its own, so the window shows
`L · E_viewer · (E_window / E_viewer)^share`, a sunlit garden seen from a dim
room reading as a garden rather than white, and a night field seen from a lit
room as night rather than black; every level of windows within windows carries
the same share, so the result is the same whatever the nesting. A display shows
its picture, decoded from its working image, at its peak luminance; terms that are display-referred by design
(the editor grid, an unbound screen's glass) are divided by the exposure. The
stylized curvature terms (the ridge light, the cavity and the ink line) apply
to the exposed colour at the strength of the surface's own exposed
illumination against the white the meter aims for (`SdfExposure.ReferenceWhite`),
so a lit room keeps its crisp edges and a dark room's edges stay as dark as its
walls rather than drawing light lines across them. Exposure is presentation only: it reads presentation time and the
rendered image, and nothing in simulation state depends on it.

**A point or spot light ends at a range derived from its intensity.** Its falloff is the windowed inverse square
`saturate(1 − (d/r)⁴)² / d²`, smooth and exactly zero at the range `r`, the
distance at which a white surface facing the light shows 1/256 of display white
at the darkest exposure the world adapts to. Past it the light darkens nothing a
view would show, and a view whose frustum misses its sphere skips it, which
lifts the old eight-light cap: a world's lights table holds 128, and each view's
pass block carries a mask of the ones it shades.

**A fixture that stands still is occluded and bounced by the irradiance
volume.** The frame has no shadow path for a positional light (the cascades are
the sun's), so a static point or spot light (one with no anchor,
`SdfLight.IsTraced`) is traced where the bounce already is: each probe records
which such lights its centre sees, one bit a table record, and lights its hits
by them under a traced shadow ray, so their light bounces about their room; the
views pass takes a light's visibility at a surface as the share of the probes
about it that see it, so a downlight lights nothing through the slab under it,
and a room's own fixtures light it indirectly. The occlusion has the volume's
resolution, so the wall, slab and doorway are what it resolves and a plinth's
shadow is the ambient occlusion's. An anchored light moves with its anchor,
which the volume cannot follow, so it lights through every wall and bounces
nothing.

**A matte surface reflects little of the sky.** The specular lobe's reflection
of the environment is its directional albedo, the split-sum environment BRDF
(`sdfMaterialEnvironmentBrdf`) over the environment blurred toward its
irradiance as the roughness widens the lobe, and a material with no specular
response reflects none of it; a Fresnel edge at grazing that ignored the
lobe's width made painted stairs and placards read as chrome.

**Considered, and set aside.** Scene-referred values left as stylized weights
with a global gain, since no gain makes a 150-nit screen and a 100,000-lux sun
read in one frame; a cutoff radius authored per light, which would make the
range a look decision for every light rather than one derived threshold; a
cube or dual-paraboloid shadow map per positional light, six raster passes of
the world a light a frame
where the hall holds 68 of them, for a sharper shadow than the rooms need; and
a window published unexposed for the viewer to expose, under which a sunlit
world seen from a dim room blows to white.

## The pipeline foundation

**Fixtures before features.** The first bounded deliverable is repeatable
pipeline verification and per-pass work counters, then shared opaque visibility with
one procedural mesh and one SDF wall. An importer, a material editor, and
skeletal animation do not precede that proof, and no package introduces an
alternate compiler, World driver, or verification script.

**A fixture's oracle is independent.** Expected pixels are derived
arithmetically or asserted by region and object, never taken from a captured
baseline or from two backends agreeing, because two backends can agree on the
same wrong image. Frame counts derive from pinned logical steps and reset
semantics, not the scheduler's cadence. Float presentation pixels are never
claimed as simulation-state determinism.

**Performance is judged by the worst frame, with counted work beside it.**
Quiet-machine timing reads the frame-rate bar; code, disassembly and live work
counts explain where its cost comes from and what a change moves. Read
`host.frames` and `world.gpu-timing` with no build, test or other GPU work
beside the debug world. Counts are diagnostic evidence, never a substitute
for that timing. Dynamic resolution's load signal and the editor's per-pass
readout ([E9](../plans/editor.md#e9--cost-per-object-and-gpu-pass-timing)) are its
runtime readers. Work counts extend one shared read
contract: a completed sample carries identity, labels, pass states, and counts
together, there is no parallel pipeline-only counting interface, and no label
is borrowed from the currently installed graph.

**The frame-rate bar is the slowest frame at the highest quality.** Until
initial development is done, every machine runs the highest quality settings,
and lower qualities wait (the owner's ruling). Those settings are the
`world.quality high` preset: high shadows from two shadowed lights, ambient
occlusion, temporal reconstruction, high indirect lighting and native render
scale. The bars, each the slowest frame and not the average:

- the RTX 2060, the lowest-powered desktop the programme targets: a steady
  60 FPS, every frame under 16.7 ms;
- the RTX 4070: a rock-steady 60 FPS (the owner's ruling);
- the Steam Deck: a rock-steady 45 FPS minimum, every frame under 22.2 ms (the
  owner's ruling).

Each machine still meets its bar with its own strengths
([each machine runs to its own
strengths](engine-design.md#each-machine-runs-to-its-own-strengths)), never by
dropping below the highest quality. An average hides the
hitch a player feels, so a periodic stall (a camera enumeration, a live
mutation's apply, a pipeline compile) misses the bar even when the mean is
fast. A lower tier, a coarser shadow snap or reduced contact detail is never
the fix for a missed bar, and neither is latency that hides a slow frame
(generated frames, a deeper GPU queue). Pipelining the simulation beside the
frame is a different thing and is allowed (the lead's decision, delegated by
the owner): engines commonly run their game thread a frame ahead of rendering,
and a fixed tick rendered by interpolation already sits up to a tick behind.
The frame presents the latest completed tick, look, camera and cursor read
input at present time so the view answers the hand at once, and the time from
input to display is measured on the benchmark route and kept within a budget
the design states. Counted work explains a change;
the bar itself is read by timing the worst frame on a quiet machine.

**A frame is pipelined across frames only once its camera is latched.**
Capturing frame N+1 while frame N's recordings run moves N's presentation into
the next host iteration, so world state reaches the display two frames after
it is captured, which the paragraph above allows, and so would look, which it
does not. The latency once the pipeline is built is one frame from look input
to display and two from world state. Look is not read at present time today:
the seat steps its live look once a fixed tick and presents it at the frame's
interpolation fraction, where the body it frames is drawn
(`WorldSeatViewState.PresentedLook`). Keeping a pipelined frame one frame from
look therefore takes a latch: just before frame N's recordings open, the
seat's camera is evaluated again from the newer look sample against the inputs
frame N's capture gave the evaluator, which is a pure function of them
(`SdfCameraProgramEvaluator`), while the stateful parts (the chase boom's ease
in `WorldSeatViewState`, the clearance) carry frame N's results rather than
stepping again. Until that latch exists the pipeline is not built, since
without it look would gain a frame; the frame's own chain is shortened inside
its interval instead ([frame production](../plans/frame-production.md#the-steps)).

**A frame costs what changed and what the display shows.** The bars are met
by the frame's architecture, never by a lower quality, so the architecture
has to make each kind of work follow its own rate of change. Four rules do,
and [the frame's plan](../plans/frame-production.md#the-whole-frame-as-one-architecture)
orders their build:

- The scene is resident on the device and changes by deltas. A transform slot
  has one writer, chosen when its creation is registered: the host for a
  still or simulated slot, and for authored presentation motion a kernel that
  runs the compiled motion program on the device. The program stays authored
  Puck; the kernel is machinery, as the shading interpreter is
  ([authored shading](#authored-shading)). Rejected: a hand-written kernel per
  kind of motion (grass, banners), which is a feature in engine code.
- A raster pass's commands are its draws: the selection kernel compacts its
  survivors in slot order (built, `SdfMeshOcclusion`).
- A pixel's lighting inputs are refreshed when the receivers they were built
  for change and otherwise over the frames of one jitter period, by tile and
  as a dispatched list, since a kernel saves time by wave and never by lane.
  Visibility is taken every frame. Rejected for the bar devices: fewer shaded
  samples (a render scale under one, a coarser shading rate), which lowers
  what every pixel shows, where a held lighting input changes only how late a
  change of light arrives, inside the window the temporal resolve already
  spreads it over.
- The device's caches of derived products are never state. Compiled
  pipelines, like meshes and compiles, live in the device cache root
  (`WorldCacheRoots`), keyed by their inputs, so a boot with a new state
  directory still starts warm.

**Worlds are mostly static, and sculpting happens in the editor.** Most of a
world's geometry never changes while it is played, and editor mode is where a
creation's field is sculpted. The cost of evaluating an authored field therefore
belongs to the moment it changes, not to every frame: a played world renders a
representation baked from its static geometry, which can ship with the compiled
world as an optional cache, and a sculpt edit rebakes only what it touches.
The field interpreter serves the bake and CPU queries; the frame draws meshes
already available while replacement geometry bakes. Dynamic bodies bake per
rigid part, since their parts move without changing shape.

**Counted work is diagnostic evidence.** Read dispatches, vertices, texels,
uploaded bytes and bake evaluations beside quiet-machine worst-frame timing
on the debug world at `high`. Nothing records those counts in the repository,
and no floor-tier ledger or march-step gate exists. A count identifies work
to remove at its cause; it never grants a frame over the machine's bar.

**Attachment ownership belongs to the pipeline plan.** A backend executes a
plan it does not own. Outputs are versioned, each version has one writer, and
forwarding transfers ownership rather than permitting multiple writers; a
`load` flag alone cannot express a depth prepass followed by an update.

**The first hybrid resolve uses the full valid per-view extent** and keeps
SDF culling for traversal only; a coverage-union optimization follows
correctness and measurement. A shared image is an agreement about extent,
format, color space, sampling, and lifetime; an opaque surface adds a shared
camera, depth, coverage, material interpretation, and coordinate conventions;
lighting or volume adds a composition contract; an authoritative field is
separately admitted deterministic data. An arbitrary image shader has no
meaningful depth, and compilation does not turn it into a surface or a
simulation.

**The persistence boundary is a per-instance override committed through a
document mutation.** An override is authored state like any other; session
state (pause, steps, preview time, captures, feedback history) stays out; time
scale is document data; shared files keep their defaults; headless validation
needs neither a GPU nor a local compilation.

**Distribution is a source closure plus a manifest** of logical paths, content
hashes, compiler, options, and adapter identity, and required capabilities,
with each pass's precompiled binaries per backend and variant versioned by its
interface hash, so loading a package needs no compiler. Packaging is not a
sandbox for arbitrary GPU code, and the trust boundary says so.

**Replacement memory has separate steady-state and replacement-peak limits**,
checked before candidate allocation, counting still-live old and retired
allocations; the installed graph is never released to satisfy admission. The
numerical release limits are the owner's decision.

**The release profile is the packaged candidate that ships the debug
world**, the same candidate every other programme's release evidence names;
its thresholds are measured against it, never inferred from one run. The
foundation releases independently of hybrid work when its gates pass.

**The debug world is the live qualification world.** `worlds/debug` shows the
features composing on both backends while the canaries under
`experimental/tests/Puck.World.Canaries` are quarantined. A workload names the
world it actually boots, and its observations establish only that world's
behaviour under the exercised conditions.

**Hardware- and environment-only checks run once, at the programme's close.**
A check that needs a particular device, driver state, operating system or
display is not run package by package. Each is listed in
[the programme's deferred checks](../plans/rendering.md#deferred-to-the-end),
and they run together when the programme ends, on the code it finally ships.
For the GPU checks each change runs, whether the two backends agree is judged
in one final review pass rather than change by change.

**Device-local memory is counted by role, not by memory type.** Images,
device-local buffers and imports count on both backends; host-visible,
staging, upload and readback memory never count. On a unified-memory device
every memory type can be device-local, so a memory-type test would count
staging and readback buffers there and not on a discrete card, and a
`peakDeviceLocalBytes` threshold would mean something different on a handheld
than on a desktop. Counting by memory type is therefore rejected, on
unified-memory devices included.

**Representations are chosen by measured cost**, never by replacing
"everything is an SDF" with "all environments are SDFs"; meshes, continuous
fields, and baked distant content may each fit a scene, and no broad shadow or
ambient-occlusion speedup is assumed without a scene and fidelity comparison.

## How worlds reach the GPU

**A world is runtime data, so no shader binary depends on a world.** A world is
authored as a document, loaded while the game runs, and visited through a
portal, and a device receives binaries and data rather than a toolchain. A
binary therefore depends on its source and its declared interface and on nothing
else, and a GPU layout is never derived from a world's rows: a layout a world
could change is a binary a world could invalidate. A shader-owned layout
recovered by reflection is rejected because two backends have two packing rules,
members match by convention, and nothing is shared between passes. A world-owned
layout with a header generated per world is rejected because it forces a shader
build per world, which is what a runtime-loaded world may not require. A
KERNEL-class probe kind's kernel therefore compiles at build, as do the camera
frame converter's kernels and the display encode every swapchain compositor draws, and a pipeline
package loads from its binaries.

The rule is about interfaces and layouts, and [authored shading](#authored-shading)
stays inside it. An authored `shade` program is runtime data too: its op stream
and parameters are words in the appearance tables, read through the same
engine-declared interface by a fixed interpreter kernel, so a world with
programs needs no compile to draw correctly. A kernel specialized to a set of
programs is an optional derived product keyed by its inputs, compiled in the
background or prewarmed by `world prepare`; a device with no compiler or a
cache miss interprets, and no layout, binding or interface ever differs by
world. A world-derived layout stays rejected.

**A world names its pipelines by source, and its package's compile stores their
packages by key.** `puck compile --tree` packages every source a package's
pipeline row names into a package store beside the package's root document
(`packages/`), each under a key over the
source's closure, its passes' interfaces, and the names it plans under. A row
naming a source loads the stored package with that key and compiles nothing;
only a source with no stored package compiles, through `ShaderPipelineLoader`,
which is the authoring path, and where nothing can compile it the row is refused
by name. The compile does not rewrite a document's rows to name
packages. The document an author edits is then the document that ships, a
compiled package booted from its output finds its packages beside the document,
a source boot compiles on first use, and an edited source misses its package
and compiles, so authoring keeps its loop without a second document.

**A pass declares an interface, and its declarations are generated from it.** A
pass names the scalars, vectors, arrays, and images it reads and writes, each
with a name and a GPU type, the way a function declares a signature. A shader
package is the unit that ships: the sources, the interface, the declarations
generated from the interface, and the precompiled binaries for each backend and
each quality variant, versioned by a hash of the interface. Generated
declarations carry explicit offsets and explicit binding slots, so the layout is
identical on every backend and no backend's packing rules are consulted; scalars
generate a named struct, so a GPU debugger shows members rather than bytes, and
arrays generate accessors that hide the element format. A world binds a row to
an interface member with data — `parameter water.tide = state.cisternLevel` —
and the shader cannot tell a literal from a bound row. A compiled world records
the interface hash it was validated against, and a mismatch refuses at load
naming the package. The pass chooses an array's element format, and the bound
row has to be able to fill it, so validation refuses a row whose kind or bounds
do not fit, naming the row and the member; the conversion runs once per tick on
the CPU.

**One binding contract serves every pass kind, grouped by how often the bound
data changes.**

| Group | Changes | Holds |
|---|---|---|
| Frame | every frame | resolution, presentation time, the deterministic tick, the pointer |
| World | every tick at most | the state mirror's regions, the field lattices |
| Pipeline instance | on install or resize | pass parameter blocks, persistent and history resources |
| Pass | every pass | transient inputs and outputs |

Each group is one descriptor set on Vulkan and one root-signature table on
Direct3D 12, plus a sampler table when the group holds a sampler, because a
Direct3D 12 descriptor table cannot mix samplers with other views. The group's
ordinal is its set and its register space on both backends. The binding kinds are closed and the same for every pass kind:
constant buffer, read-only buffer, read-write buffer, sampled image, storage
image, and sampler. Push constants carry at most an
index, so nothing authored or bound lives there and the portable push-constant
minimum stops being a design limit. Widening the current fullscreen contract by
one binding kind is rejected because it deepens the split between two unrelated
binding models; raising the push-constant block per adapter is rejected because
a layout that differs per adapter forks the interface and takes the
cross-backend pixel verdict with it. A pass's resource needs come from its
declared interface, so the frame graph's planner and the binding contract read
one statement.

**The closed set of binding kinds is one type, `GpuBindingKind`.** The pass
interface, both backends' layout planners, and `IGpuBindings.WriteBuffer` all
read it, and whether a buffer is read or written is part of its kind, so it is
the one statement of buffer access. Keeping another binding-kind type beside it
with translations between them is rejected, because each translation is a
second statement of the same access that can drift from the first. A
positional compute binding (`GpuComputeBinding`) states it too, and holds only
buffers and storage images: a sampled image is always read through a separate
sampler in a group, so no combined image sampler exists anywhere.

**A binding is visible to the pipeline's stages, never to its own.** A
pipeline's stages come from its pass kind, compute or vertex and fragment, and
every binding and the pushed index are visible to all of them. Direct3D 12
therefore gives each root parameter `ALL`, or the one stage of a single-stage
graphics pipeline, and Vulkan gives each binding and the push range the
pipeline's stage flags. Reflecting each stage's bytecode to narrow every
binding to the stages that read it was rejected: Direct3D 12 sets visibility
per root parameter, so a table holding a vertex-only and a fragment-only
binding would need `ALL` anyway or a split into one table per stage, which
breaks one view table and one sampler table per group and moves the root
indices. Narrowing returns only if a measured driver cost shows it pays.

**A device's descriptor heaps are the device's own size, and a candidate is
admitted into them or refused.** Direct3D 12 binds one shader-visible view heap
and one sampler heap per device, and every descriptor pool is a range of them.
The view heap takes the size the device reports,
`GpuDeviceCapabilities.ViewHeapSize`, and the sampler heap takes the smaller of
the reported `SamplerHeapSize` and `StaticSamplerHeapSize`, since root
signatures with static samplers are bound beside it. A runtime that does not
answer options 19 reports the sizes every binding tier guarantees, 1,000,000
views and 2,048 samplers for both sampler limits. The
heaps are created once with the device, and the device outlives every world it
presents, so no world's demand sizes them. What varies is admission. Each pool
owner states its pools' sizes statically, and its own pool creation reads the
same statement, so `GpuDescriptorHeapBudget.TryAdmit` checks a candidate's
demand against the free ranges before anything is allocated. A candidate that
does not fit is refused by name when it would install, as a pipeline over its
memory budget is refused with `SHADERPIPE_BUDGET`; the installed graph keeps
presenting and nothing grows. A heap's bytes are its size times the device's
descriptor increment, counted under `memory.directx`. At most
`GpuDescriptorHeapBudget.MaxLivePools` pools, 1,024, are live on one device,
which bounds the range allocator's bookkeeping; a pool past it is refused by
name, as a pipeline past `ShaderPipelineLimits.MaxPasses` is.

Two ways of sizing the heaps were rejected. A cap stated in a document has no
basis, because nothing an author writes knows how many pipelines, views and
previews a session will install, and a cap below the device's limit refuses work
the device could hold. Sizing the heaps from the first world's summed demand,
doubled so a reload can hold both graphs, ties the device's heaps to whichever
world booted first, so a larger world loaded later on the same device is
refused although the device has room.

**HLSL compiled by the pinned DXC is the one source language, and a generated
echo pass is what proves the layout.** One language, one compiler, one cache
key. A second language would need its own front end, a translation back into
HLSL for DXIL, a register remap between the two, and offset reconciliation
wherever their packing rules disagree, and none of that buys a pass anything,
so every pipeline source is HLSL. Because declarations are generated, the engine assigns every group,
binding, register, and offset itself, which is what a language-level parameter
block would otherwise buy and is a stronger cross-backend guarantee than any
allocator's. Slang is the better language and the wrong trade: DXIL still
comes out of DXC, so a Slang pipeline pins two toolchains; it compiles several
times slower on the largest include, which is already the slowest part of the
build; and the features that would justify it, precompiled modules and link-time
specialisation, are not yet stable. Revisit it when a precompiled-module path is
stable, when DXC stops being maintained, or when a generated declaration cannot
express a group identically on both backends. The contract is indifferent to the
language, so a revisit touches no world and no interface — an interface can
generate a module as easily as an include. What a single reflecting compiler
would have given is proof that both backends agree on the generated layout, and
that proof is taken by running instead: every package build generates an echo
pass per interface that reads each member through the generated declarations and
writes it to an output buffer, the members are filled with distinct sentinels,
and the read-back has to match exactly on both backends, so a layout the
generator got wrong fails the package on a real driver before a world binds to
it. Compiler reflection over both binaries is a second, cheaper build-time check
for hosts with no GPU.

**The SDF VM holds the GPU interpreters.** A world's signed-distance program runs
in `Puck.SdfVm`'s kernels, as does a material's authored shading program
([authored shading](#authored-shading)), admitted under the rule this entry
ends with: its consumer is the views and glass passes, and one op table
generates both of its tiers. Any other GPU effect is a pipeline pass compiled
from HLSL. A second, general-purpose bytecode interpreter, the removed
the shader VM project, is rejected. It had no consumer but its own tests, no kernel
included its GPU half, and nothing compared its host and GPU interpreters. It
therefore added a C#/HLSL contract that no check held and a second copy of the
PCG3D hash. It also duplicated work each existing path already owns: an authored
effect that is not a signed-distance field is better expressed as a pass with a
declared interface than as a program interpreted per pixel. A future interpreter
is admitted only with a consumer on the render path and a check that holds its
two halves together.

**Presentation samples a state mirror at presentation time.** The compiler knows
statically which rows a presentation binding reads — HUD, camera, pipeline,
material, signed-distance program — and that deduplicated union is the
presentation manifest. Each client view holds a mirror compiled from it: a flat
table of slots carrying a row ordinal, a key, a target flag, and a conversion,
resolved to ordinals when the document is installed, so no string, dictionary,
or reflection sits on a per-tick or per-frame path and nothing allocates after
warm-up. The mirror refreshes at the settled tick boundary from the rows that
moved plus the slots still in motion, because a row version says when a stored
value changed and not when a read changes: an easing follower keeps chasing
after the write that kicked it, and an advancing or cycling cell moves with its
clock, so those slots refresh each tick until they rest. A moving slot keeps two
samples, the previous tick's and the current one's. Presentation time is the
tick plus the frame's interpolation fraction, and a cell carrying a
value-over-time trait is evaluated at that fractional time, so a parameter eased
by a 30 Hz simulation is smooth at 144 Hz. A plain cell steps, because a score
of 3.5 is wrong and an author who wants smoothness declares `dynamics`. An
offscreen capture pins the fraction to one, so a capture shows exactly one named
tick. Presentation time is a float and never re-enters state. On the GPU a
bound scalar is copied into its pass's parameter block at the offset the
interface fixes, and a bound row fills an array member in that pass's own World
group block; each is written only when its value moves. Arrays live in shared
regions keyed by row and element format, so two passes reading one row the same
way read one copy; a row bound once is indexed per instance, so a board of
sixty-four pieces is one binding; and the field lattice is a row the mirror
reads like any other, so a field has one truth on the GPU. The mirror reads
[the presentation view's](runtime-and-delivery.md#the-presentation-view) state
interface, not a document.

**A binding reads eased by default and the stored truth with `.$target`, for
every consumer.** A HUD gauge, a camera operand, and a pipeline parameter answer
one question, so they share one rule: the parsed binding carries its target
flag into its mirror slot, so no consumer can read truth where the token asked
for the eased value. Giving pipelines their own default is rejected
because it leaves two mechanisms for one decision. Changing the default later
moves camera and pipeline pixels and the parity contract, and moves no state
hash, because easing is presentation-side over the exported rows.

**A member is bound or overridden, never both.** A `views.graphs` row's
`parameters` bind a pass's config field to a literal or a state token, and its
`overrides` set the field's authored value, which a live `pipeline.set`
previews and `pipeline.commit` records. A row naming one field of one pass in
both is refused at validation, naming the row, the pass and the field. Letting
one win silently was rejected: an override that a binding overwrites every
frame is a commit that changes nothing on screen, and a binding an override
masks is state that never reaches the pass, and neither shows the author why.
An unbound field keeps the value its source's default and the row's override
give it; a bound field's fallback is its source's default, which it draws while
its binding does not resolve. A live `pipeline.set` of a bound field is refused
by the same rule.

**Residency is chosen from what the adapter reports.** The properties a policy
needs — whether device-local memory is host-visible and coherent, and how much of
it there is — are the memory profile `IGpuDeviceContext` reports, filled by each
backend at device creation. A pure function then chooses per region:
write in place on coherent unified memory, a per-frame ring in host-visible
device-local memory where it exists, and a staged copy otherwise. Nothing
selects on a platform, product, or driver name, nothing is authored, and every
policy produces byte-identical region contents. A handheld with unified memory
pays no copy for what its memory already shares, and a discrete adapter pays one
dispatch per dirty region; a design that serves one of those devices by starving
the other is wrong.

**A quality tier selects a variant of a package with the same interface.** A
tier may change how a pass looks and what it costs. It may not change what a
pass reads, what an array holds, or anything the simulation does, so the
manifest and the mirror are functions of the document alone, and two documents
differing only in tier compile identical manifests, identical mirrors, and
identical state hashes. A tier is named from the one quality vocabulary,
`QualityTiers` (`low`, `medium`, `high`), which also keys a world's
`world.quality` presets, rather than from a second one. Packages carry variants
behind that shared interface, and a pipeline's selected tier chooses its
variant alongside its bound rows. The `default` variant supplies a package's
default implementation. Tier selection changes presentation alone; it cannot
reduce the highest-quality qualification requirement.

**The offscreen host's time is its tick count.** Its frames are its only
output, so it steps one tick per rendered frame, never several ticks in one
iteration after a slow frame, and it steps the next tick only once the root
reports the current tick's frame rendered. A frame that is not yet renderable
(a cold build, a rebuild, an input with no output for the frame) holds the
tick, composing it again, so the tick-to-frame mapping is total and no frame
shows an older image for a newer tick; a refused frame releases the tick, since
holding cannot render it. A wall-clock
catch-up would make the tick a frame shows depend on how long the frame before
it took: a script that waited a few ticks could read a GPU result before it
landed, and a capture's frame could reproject across several ticks. Stepping
per frame makes the tick a frame shows a function of the script alone. The
wall clock only keeps
the host from outrunning the world's rate. The windowed host keeps catching up,
because a player's simulation keeps real time, and so does a headless
authority, whose remote peers do; a headless host renders nothing, so no frame
needs a tick of its own.

**Bound rows are priced, and a capture reports the tick it shows.**
`FixedStepPump.Advance` can run several steps in one call, so a frame composed
after one tick may show a later one. For the simulation tick a scheduled capture
already does both. It fences, because the pump ends its burst at the armed
tick through `IFixedStepSimulation.AwaitsFrame`, so the host composes that
tick's frame before stepping on; only frame interleaving changes, never the steps. The
windowed and offscreen hosts also hold their clock: they
step no tick past the armed one until the capture is served or refused, and
refuse it by name after a bounded hold. Repeated window resizes delay serving
without advancing its tick or blocking the next scheduled capture. A pending
capture presents bound state and poses at fraction one. The scheduler also
reports: `WorldCaptureScheduler` refuses a capture served by a frame showing
another tick as `stale`, naming both ticks, rather than leaving it out of the
manifest. Bindings are priced too: a world's bindings appear in the cost report
as bytes per tick and bytes per frame, beside and separate from the
simulation's cycle bound, and a document over its ceiling is refused at
validation naming the pipeline and the binding. A state-bound parameter adds a
second tick, the one its regions were refreshed at, pinned by the capture hold
along with the simulation. A capture carries it as its region tick, and
`puck parity`'s tick verdict holds it to the armed tick, ordered before the
pixel verdict, so a skewed capture fails as a skew. `puck parity` pins one
reference tier, `high`, and its `bound` station's pixels depend on a bound row
the world's own rules move. Hardware observations also run at `high` and meet
that device's worst-frame bar.

## A far viewer draws a world's far product

**What a world shows of itself from far away is a product of its own, never a residency of the world.** At MMO scale
every zone, moon and server is a world and a player sees many at once, so seeing a world cannot cost holding it. A
sky `view` layer that asks for it (`farProduct: true`, `InfinityViewSpec.FarProduct`) draws the destination's far
product (`SdfFarProduct`): the world's settled static scene (`SdfMeshDraw.InStaticScene`), each mesh at the coarsest
level it carries, placed in the world, its vertices gathered into cells (`SdfMeshCluster`, vertex clustering binned by
facing so thin walls keep both faces), then chunked and levelled (`SdfMeshChunks.Bake`) so the view's draws pick a level
by the pixels they cover. Its materials are the source program's, renumbered into the product's own. Dynamic content
(bodies, skinned or displaced meshes, glass, screens) is absent: a far viewer sees ground and standing masses, never
motion.

**It is a cache derived from what a residency already drew.** A product is taken on the frame thread from a settled
residency's frame (claims hold its meshes) and built on a worker, named by a digest of its inputs, never recorded, and
built once per document version of the world (`WorldDocumentVersion`). The boot world's comes from the presentation's
own residency; a view of a world no residency here holds draws that world whole once and builds the product from that
residency, then swaps to it (the derive on a miss). The publishing world authors what it shows (`render.farProduct`:
the cell, and the smallest part kept), since a far look is the author's choice.

**Its residency composes the product alone.** The view's scene is one emitter that emits the product's materials and
one static draw per piece (`WorldFarProductEmitter`), so the residency plans no bake group and bakes nothing; the
world's session emitter only dresses the frame with that world's sky, lights and exposure, measured from the fitted
camera (`WorldSessionSceneEmitter.ViewPointOf`). A view that draws geometry alone nests no view of its world's sky.

**The hand-over is the seam.** A sky view across a seam shows the far product; crossing the seam moves the seat into
that world, whose own residency draws it. Both draw the same meshes (the product is built from the drawn ones), and
the authored minimum size is what a pixel spans at the nearest far viewer, so what the product leaves out is under a
pixel where the hand-over happens.

Options weighed: following the source world's tier board in a scoped residency (the second-scene path) still plans a
group per placement and bakes whatever the board does not lead; reusing the world's own residency for the far view
keeps the whole world resident and unparked; per-group far tiers in the mesh store cost a draw and a bake per group
for every far world. The merged product holds one mesh a world, bakes nothing, and its triangle count is bounded by
the surface area over the cell squared. Invariant: a far entry draws only a product built from the document version
its source residency's world holds now, enforced at `WorldScreenBinder.FarProductOf`; where it stops, a product is not
yet persisted in the mesh store, so each process derives it once.

## The frame graph and nesting

**The frame graph is the centre of rendering, and the SDF engine is one pass
package in it.** The SDF engine began as the host. It composited panes in its
second stage, it owns the screen slots, it capped nested cameras through a
round-robin refresh budget, and post-processing and the overlay were nodes
chained after it. That
design grew from a prototype, and each capacity in it is a constant rather than
a planned cost. The replacement has to be better at every job the SDF engine
does. So composition, nesting, sources, and output belong to the graph, and
the SDF passes become a package the graph schedules like any other. The move
happens behind a capability matrix generated from the engine's code, so no
feature is lost by accident.

**The frame graph is a document, `puck.render.graph.v1`.** Worlds are runtime
data, and a world should be able to add a pass or a view without new C#. Shader
pipelines are already JSON documents planned by one planner, so a C# graph
declaration would be a second way to say the same thing and could not be
extended by a world. Engine passes ship as packages that the document names.

**A nested view is a graph instance, and a view that sees itself reads its
previous frame.** Every view, from the main camera to a pane to a camera on a
screen, is an instance of a graph, and nesting is one instance reading
another's output. Instances render on demand, at the extent their on-screen
footprint needs, at their own rate, and once per frame however many consumers
they have. A self-reference goes through the planner's previous-frame edge, so a
mirror shows the previous frame, and a same-frame cycle is refused because no
order of passes can satisfy it. The main view, the panes, the split-screen
seats, and every camera and session a screen shows run as graph instances this
way; a camera view is an `sdf.world` instance that reads every view, itself
included, at its previous frame, so a screen showing its own view samples the
output completed before this frame and never the one being written.

**Post passes are passes of the synthesized root graph.** A world names them in
`views.post`, each row written the way a graph document's `packages` row is,
less its ports: the root runs them in order over the composed frame, after
every view and pane is placed and before the overlay, and each reads the frame
the pass before it wrote. A `views.graphs` row per post pass was rejected. A
row is an instance that another instance reads, so a post pass written as one
would run over the world before placement rather than over the composed frame
with its panes, and each would cost an image and a node of its own. A
post-process package is declared once, in the render graph package catalog,
with its stages, members, config and interface, rather than as a manifest
beside a package, so it ships the way `place` and `overlay` do.

**Sources are classified by how an image arrives, and producers register by
id.** The three transports are uploaded, imported, and rendered, and
`Surface`'s CPU-pixel, shared-handle, and same-device kinds already describe
them. Naming a source kind after its producer, such as a brick or a desktop,
was rejected: a second emulator, a Linux capture API, or a video decoder would
each change the schema and the planner. Instead, a producer registers under an
id, and the graph names only the id and the transport.

**A source's content class decides its verification and privacy.** A
deterministic source, such as an emulator framebuffer, is exact, so it gets an
exact pixel verdict. An external source, such as a desktop or a camera, is
private and nondeterministic. It never reaches simulation state, replays, or
the state hash, and captures replace it with a fill or refuse. Presentation
sources follow the ordinary float rules.

**The pipeline owns the mapping from a hit back to a source's pixels.** The
pipeline draws the source, so it is the one component that knows the UV
layout, crop, letterbox, and warps involved, and it publishes that chain as
data. Three destinations consume the mapping:

- Simulation input inverts it in fixed point from document data, so a light
  gun aimed at an emulated game stays deterministic.
- Host passthrough sends pointer and keyboard events to an external window,
  such as an author's editor shown picture-in-picture, where floats are fine.
- Presentation hover can use GPU picking.

Passthrough exists only for a source the local user opened. A world document
arriving through a portal must never be able to type into the player's
desktop. Focus stays with the input system, which reads the mapping. The local
user opens a source in one of two ways, and nothing else counts: the
`source.passthrough open` verb typed at the host's own console, or a physical
press on the framed picture of a window capture's screen that a local seat
engaged in the world the host booted (never a world reached through a portal
or a session view), mapped through the screen's published mapping to the
window's own pixel. A physical press is a pointer press the window pump
delivers to the input filter, or a local pad's press: a binding's dispatch of a
known `gamepad.*` control that a local device reported
(`WorldPassthroughPad.IsLocalPad`). Engaging such a screen, by any principal
(a console `body.engage`, a peer, an addon, a replay), only shows the window;
the press opens it. The engage's own provenance is not what decides: a seat's
channels can be driven by a co-driving addon or a scripted signal, so the
engine cannot prove an engage was the local user's, while a press only the
local machine's own devices produce can only be. A pad counts beside the mouse
because its signals enter the input router only from the local gamepad
capture, stamped with the device that reported them; every scripted or remote
door into the router (`player.signal`, a wheel sector, an authored command, a
typed line) carries no device or no binding origin, and a probe's axis is no
gamepad source, so each is refused by name. A world document's binding page
chooses which pad control means which pointer act (move, scroll, a button, an
arrow key), and a route's `padPointer` its feel; neither makes an act happen.
The source closes when the seat no longer frames the screen, as when it leaves
(`WorldSourcePassthrough`), and the reserved chord, Control+Alt+Escape, leaves
in one step while a seat frames such a window. A document still never authors
a passthrough destination: `route.input: Passthrough` is refused by name.

**The engine's generated declarations are committed and checked against their
generator; compiled binaries are build outputs.** The includes the C# model
owns, `sdf-isa.hlsli`, `sdf-render.hlsli` and the `*.interface.hlsli` of each engine package and
SDF kernel interface, are written by `puck shaders generate` and tracked in
Git, because the engine's kernels include them from the source tree. CI runs
`puck shaders generate --check`, which regenerates each in memory and fails on
any difference, on a checked-in interface include no generator owns, and on a
package whose include is missing, so a committed include cannot fall behind
the model. A world's own pipeline passes commit nothing: the loader generates
their declarations in memory, and a package carries them. Compiled `.dxil` and
`.spv` files are ignored by Git and built by `build/Shaders.targets`, and CI
builds the packages that ship. Every build host already needs DXC. DXIL is built
on Windows alone: Direct3D 12 is its one reader, and DXC's DXIL is not
byte-stable across hosts, so a build elsewhere compiles SPIR-V only and the
cross-host check compares SPIR-V ([freshness](../reference/shaders.md#freshness)).

**A kernel nothing dispatches is deleted rather than kept for the sweep.**
`sdf-child`, `pixelate` and `viewport-composite` compiled into shipped
bytecode that no code loaded, so each build paid for them and each reader had
to discover that they did nothing. Keeping them until P14's final sweep was
rejected: dead source is not a reference anyone needs, and history keeps it.
The same rule removed the SDF-side `resample.comp.hlsl` once the graph's package
library held the one resample pass, now the `place` package. The pixelate
interface fixture under `tests/Puck.Shaders.Tests` is its own copy
and stays with the spike.

**Seats and camera views share one SDF residency, and each renders as its own
instance into its own output.** One `SdfWorldResidency` serves a world. Each
composed view is an `sdf.world` instance of the render graph that runs the
package's passes, mask through composite, over the residency's tables into its own
output. The graph places each output into its seat rect with the `place`
package. Everything a frame's views have in common is therefore shared by
construction: the program upload, the mesh bake and its meshes, the glyph atlas,
screen sources, lights, decals and volumes. Camera views carry their restricted
quality in their own pass blocks and sample screen images through their own
instance's reads. Separate residencies per seat have these costs:

- Each seat residency would upload its own program and tables, costing N times
  the table memory and N times the uploads.
- Each would plan its own mesh bake; the shared mesh library spares the
  re-bake, but every seat would still hold its own region of the same meshes.

N seats cost N instances' passes rather than one dispatch whose Z dimension is
N, and the counted work shows that cost. That holds for seats, which are few
and large.

**Secondary views are one view system, and a view earns its rate from what
moves in it.** A session window, a portal, a mirror, a screen's camera and a
sky view of another world are many and small, and a hub shows dozens, so one
instance a view makes the frame thread's recording, the device's fixed
storage and the installs on a window's growth all scale with the views a
client holds. Three rules replace that, built in the order
[the frame's plan](../plans/frame-production.md#2-secondary-views-are-one-view-system)
gives:

- A view's cadence follows the motion in its image under the perceptible
  step (`SdfUpdateRateTiers.PerceptibleStepPixels`): its camera's measured
  motion across the image and the fastest content its world reports, never an
  assumed parallax. Rejected: a fixed budget of views a frame, which makes a
  view's rate depend on how many others are in sight, so the same window
  judders in a hall and not in a corridor.
- A view owns its published image and its history. The working storage of its
  pass family is leased by extent octave from one pool a device, for the
  frames the view renders. Rejected: shrinking a held view's own storage
  (built, `WorldViewGraphHost.ReserveShrinks`), which still installs a graph a
  view and an extent.
- The views of one residency due in a frame render through one pass family
  over an atlas, a tile naming its view. Rejected: one dispatch a view with
  the recording made cheaper, which keeps the per-view barriers, binds and
  command lists that a hub multiplies.

The invariant is that a view's image comes from one camera and one frame of
its residency whatever storage it shares; the lease a view holds from its
recording to its submission's retirement is the door.

**The lights and the sky are generated records in World-group tables.** A
hand-numbered environment table is a second hand-kept layout beside the frame
data, and carried in every pass block it makes every dispatch bind constant data
that only the sky, shadow and views passes read. The lights table and the sky's
block and layers are C# records whose HLSL declarations are generated
from the types, written as regions that owe only the words that changed, and
referenced only by the kernels that read them, so a pass block holds the view
and frame values alone.

**Temporal reconstruction is Puck's own complete implementation.** The Steam
Deck floor needs render scale to be cheap without looking cheap, and SDF
shading is the dominant per-pixel cost. A vendor upscaler first was rejected. It is a
closed or platform-specific dependency, and it knows nothing about SDF
surfaces, nested sources, or screens showing live content, which all need their
own motion and reactive masks. The package produces the inputs vendor upscalers
expect, so one could still be added later as an alternative pass.

## Global illumination

These decisions shape the [illumination plan](../plans/illumination.md),
which holds their build order.

**Indirect light is an irradiance volume traced on the GPU against the baked
meshes.** The frame walks no field, and worlds are mostly static, so bounce
lighting is cached rather than retraced per pixel. Each residency shares one
three-level nested volume (`SdfMeshIrradianceProbe.Levels == 3`) among its views.
The finest window uses the tier's spacing and dimensions; the storey level's
32×24×32 window uses 4 m × 2 m × 4 m cells and the coarsest 32×12×32 window
32 m cells (`SdfMeshIrradiance.LevelsOf`). Compute kernels traverse the CPU scene's
uploaded two-level hierarchy of static mesh draws; views and later bounce sweeps gather the eight probes around a
receiver through the same visibility rule. Moving bodies receive this light
and cast none into the static volume.

Each toroidal window reuses cell-keyed slots and publishes only once all its
slots are measured. Its centre encloses the residency's view cameras when
they fit within the window's middle, otherwise it follows the first camera.
The CPU probe cache is a cache of its window: each level keeps the probes of its
window grown by the band that holds the window still (an eighth of it a side),
and forgets the rest when the window moves, so the cache is bounded by
residency, not by where the camera has been. Full demand-point windowing and
scene scope are open work in
[the streaming and memory plan](../plans/streaming-and-memory.md#decision-9-lighting-reads-the-same-demand-and-pays-on-demand).
A still cell reuses its measurement until the lighting or geometry changes.
[The handbook](../rendering/sdf/handbook/lighting-and-shading.md#the-mesh-paths-irradiance-volume)
owns its layout, costs and limits.

**Background work belongs to the engine job system.** Its budgeted jobs make
GI proportional to change: newly landed geometry re-measures only probes
whose measurements it can affect. This is the
[job-system and GI scheduling work](../plans/open-items.md#engine-job-system),
rather than a second executor.
[`SdfMeshIrradiance`](../../src/Puck.SdfVm/SdfMeshIrradiance.cs) builds scenes
on the thread pool and reuses unchanged geometry hierarchies. Its probe client
(`SdfMeshProbeGpuWork`) shares the bake's `SdfGpuWork` service: GPU selection
classifies the window's owed work, compute traces and relights it, and indirect
dispatch consumes the compacted relight list. Changed scene boxes retrace only
the probes they can reach. The GPU keeps hit words and publishes measured
records through fence-completed, scene/window-checked copies; old data remains
visible until its replacement is ready. Camera exposure queries use the same
service and shading function. CPU traversal and shading remain the diagnostic
reference, and CPU nearest-surface and short clearance queries still place probes.

Scene updates preserve unchanged geometry addresses. The shared range allocator
places a replacement's top hierarchy and instance records beside the published
scene, and a worker serializes only those records and new geometry. B1 stages
bounded blocks through `GpuRegion` under its host-time, byte and GPU budgets.
The root is the publication point: no query is admitted during replacement,
and old allocations retire only after the new root's upload fence. Admission
then checks the mesh placements again, so a relocation during staging cannot
pair a scene's instance addresses with another mesh layout. Repacking and
uploading the whole trace scene for a field's one changed mesh would make
frame work proportional to world size.

Visibility is an any-hit query: GPU `sdfProbeOccluded` and CPU `Unblocked`
stop at the first accepted triangle, sharing their nearest-hit walk's node
and triangle tests. Shadows, light bits and camera visibility use this door.
The shared scene builder certifies each hierarchy at no more than
`SdfMeshProbeTrace.StackDepth - 2` edges, forcing median splits when necessary.
A hierarchy that cannot fit is refused by name. CPU and GPU walks count stack
overflow as an assertion failure rather than silently ending a walk.
Within each work priority and eight-cell distance band, probes run in Morton
cell order to keep neighbouring origins together without changing their rays.
The budget diagnostic samples median and oldest lighting age per level so a
coarse window cannot hide the finest window's response time.
Under changing lighting, the selection kernel interleaves coarse levels by a
stable cell phase. A level's age bound scales the authored target by its
interleave; reaching it forces eligibility and age relative to it takes
priority. Geometry and publication work bypass the gate. Unchanged lighting
takes whole bounce sweeps. A sweep retains its initial phase across listings,
even when later batches shade under newer lighting, and a lockstep drain always visits every cell from
no bounce. These bounds describe demand under the budget, not guaranteed wall
time when the device is overloaded.

Indirect capture precision is authored as `render.indirect.lightingGrid`.
Its neutral engine default is continuous; the standard chooses angular and
radiometric steps. Directional records used for the lighting key and shading
come from one snap function. The sky radiance each probe ray carries (the
sky texels the relight and the meter read, and the only sky a capture is
compared by) uses a fixed quantum
per colour channel and power-of-two range, rather than a tolerance relative
to an earlier capture. Converged captures compare the resulting values
exactly, including with a zero grid. Thus a frame's drained bounce depends
on its own lighting, scene and window, not when earlier captures happened.
Crossing a grid boundary still drains from no bounce; within a grid cell the
moving sun does not owe another full window. The same grid governs ordinary
asynchronous captures; coarse-level interleave remains a separate work policy.
Below its bound, a probe outside every local view's frustum gets less age
priority, and a probe whose last light changed gets more. Neither weight
removes work. Overdue probes ignore both weights. The budget diagnostic
reports ages inside the first frustum and outside it so the demand weighting
can be measured independently of whole-window ages.

Software compute is the baseline on both backends; no ray-tracing hardware is
required. The trace device law requires exact identities and discrete flags,
scene-scaled float tolerance and independently intersected near ties, with an
injected skipped subtree as its negative control. It qualifies its corpus,
not every possible authored geometry. A settled capture remains waiting while
its prepared lighting or exposure is pending; background work continues to
completion before that frame can certify a still. Throughput and the one-frame
lockstep target remain measurement obligations in the
[illumination plan](../plans/illumination.md#after-the-gpu-trace).

**Visibility is measured as local first-hit surfaces.** Each probe holds a
12×12 octahedral surface map. One bounded closest-hit ray per texel stores an
FP16 distance and a packed surface normal; a negative distance marks a miss
and its measured reach. Neighbouring samples fit a cached normal-gradient
curvature per texel. Four reflected angular taps reconstruct local surface
patches and test the receiver against them, with a smooth surface-gap and
coverage fade. A finite blocker affects its sampled angular footprint rather
than extending an infinite plane through the scene. At the finest level,
where a tap blocks the receiver, a tap whose ray never got past that
blocker's surface by the receiver's distance carries no evidence about the
receiver and leaves the coverage: the receiver standing in front of the
surface that tap met says nothing of a view past the blocker. Counting such
taps as clear lets the ground outside a wall, seen in front of the wall's
base, and the rays running clear past the end of a wall seen at grazing light
the inside of the wall's foot and of a room's corner with the outdoor probe's
sky. A tap whose ray crossed the blocker's plane clear still counts, so a
receiver eases over a texel past a finite block's edge. Coarser levels close
walls on sheets instead, since their metres-wide taps would close a hill's far
slope under this rule.

[`SdfMeshProbeDepth`](../../src/Puck.SdfVm/SdfMeshProbeDepth.cs) and the views
shader's `sdfMeshProbeDepthVisibility` share that rule. Gather weight is the
trilinear share times a smooth distance fade from probes behind the receiver
to probes half a cell ahead of its surface, times depth visibility. The fade
uses the receiver normal and the relocated probe position. A fixed 5 cm normal
bias stays independent of cell spacing. The lookup clamps to boundary probes,
but visibility tests the actual biased receiver. Outside a window the facing
fade eases to one over the first cell, so normals turned away from the window
can still receive its measured light.

**A view gathers once for what its pixels can show, across its cells and its
frames.** The gather itself is the contract the CPU twin holds
(`SdfMeshIrradiance.Gather`); how often a view runs it is presentation. The
probe lookup gathers at one representative pixel of each cell of four pixels
a side, and every pixel blends the representatives of the four cells about
it: each by its bilinear share, weighed by how nearly the pixel lies on the
representative's surface (`sdfProbeBlend`: off its tangent plane by no more
than the 5 cm normal bias, a cell's pixels, or what a smooth surface turns by
over the distance between the two, and normals within about eleven degrees).
The volume's light varies over metres and a cell spans four pixels, so the
blend loses nothing a view can show, and it leaves no cell's edge on a
surface, which sharing one gather across a cell did. A pixel none of the four
serves (a leaf against a wall, the far side of a crease) shares one of its
cell's extras with the cell's other such pixels compatible with it
(`sdfViewsProbesCompatible`), a gather taken at the first of them, so the
blend never crosses a depth or crease edge the record shows. A tolerance in
metres alone would have every pixel of grazing or distant ground gather for
itself. Every view also keeps each representative's
gather for its next render (`sdf-probe-history.hlsli`), a nested view
(a portal, a window, a painting) at the extent it is shown at, so a secondary
view's lookup costs what moved in it rather than its whole image each frame. A
kept gather validates itself against the receiver it was taken at, so reuse
needs only the preceding render's camera and grid, never jittered history, and
a view that does not reconstruct over time keeps one as a temporal view does
(`SdfWorldPackage.IsTemporal` names a temporal fragment by its history color,
not by any history resource). A receiver
projected into the preceding view reuses a kept gather it passes the same
tolerance against, measured from the receiver the gather was taken at, so reuse
never drifts. A kept gather stands while the published geometry's serial is
the one it was taken under (`SdfMeshIrradianceParameters.Geometry`, which a
retrace of a held probe or a window laid out again moves, and which no other
publication of any volume in the process shares, so an entry validates itself
whichever render wrote it), and past a window's
move only where it read no stage another window's place decides and its level
still holds the receiver inside one cage; a gather that met a missing probe is
never kept, since the probe landing moves no serial. The blended light is read
again every frame, so a relight shows at once. Views reads a traced light's
probe visibility from the slots its pixel blends, each of which holds the light
bits every measured corner sees and any sees with those corners' summed weight
and lists the lights they disagree on (`sdfViewsLightVisibility`). A glass pane is lit apart from the surface behind it, so the glass
pass keeps its nearest faces' gathers in a history of its own on the same
functions and test, one entry a cell written by the cell's first pixel; the
glass pass draws nothing in a frame with no glass in view, and the render graph
keeps a history its one writer skipped as its last write left it. A gather costs each corner's angular surface test, so a lookup that
gathers only what changed costs a pass in proportion to motion rather than to
the view's pixels, the headroom heavier worlds' views need.

**Coarser levels supply reach without overriding fine visibility.** A receiver
uses the finest published window containing it within its boundary probes,
or the coarsest published level beyond all windows. A coarser level never
answers inside a finer window: its wider angular footprint could miss a thin
wall the fine level measures. If the selected level's bounded gather has no
support, the same probes extrapolate past measured reach: a measured surface
still blocks, while a missed ray no longer invents a far blocker. This read
reports `measured=0` and supplies no traced light visibility. It
estimates unmeasured light rather than painting every far surface black.

**A level answers only in the share it measured.** The chain's first stage
that answers contributes its share: its weight over the same cage's front
weight before visibility, clamped to one. The rest comes from the next stage
over another cage, and only the last stage that answers is normalized to what
remains; a receiver reads sixteen probe slots, pre-scaled by their stages'
shares. A finer level answering past its window keeps its whole share over
the inner half of its surface reach and eases to none at the reach, so the next
stage takes a receiver over across a band rather than at a line one probe row
tall. Levels are a table per tier with a spacing per axis: the middle level's
4 m × 2 m × 4 m cells put a probe plane in every storey 2.4 m or taller,
which no isotropic coarse level does (an 8 m level had one in every other
4.5 m storey, and its single corner across a slab lit a whole storey). The
[illumination plan](../plans/illumination.md#after-the-gpu-trace) holds the
options this rule left open.

**A coarse tap on a sheet closes; a coarse tap on a solid's near side does
not.** One visibility rule holds at every level: the taps that block a receiver
on solids close it only as a majority, and the taps on sheets close it at
almost any share. The trace marks a coarse level's hit a sheet when the scene's
exact distance shows no solid thicker than 2 m behind it (a surface lies within
0.45 of that thickness of the point half of it inside): a slab, a wall or a
slab's edge is a sheet, with another space beyond it; a hill's near bulge, which
a coarse probe's metres-wide taps alias in front of the slope it sees, is a
solid. A conservative ramp at every coarse tap darkened one clear corner in six
on curved hills; a tolerant one let the 32 m level light a storey through its
slab. Measured along the normal alone, a slab seen edge-on reads twelve metres
thick, so the test is the solid's medial thickness, not its depth along a ray.
The finest level marks no sheets: its half-metre taps resolve slabs themselves,
and its minority tap on a thin finite object (a railing, a hanging sign) lies
beside the receiver's segment.

**A view with no cascade takes the sun's visibility from the volume.** A view
that draws no directional cascade (a session or screen camera's reduced quality,
or `world.shadows off`) reads each shadowing directional light's visibility from
the probes about the surface, as a traced point light's: every probe records
whether its own ray toward the light is clear, recomputed on each relight, and
the surface takes the weight of the measured probes that see it. Without it the
100 klx sun lit every interior face turned toward it.

**A light no cascade measures is shadowed by the probes and the occlusion
together.** A light's visibility is one value, computed once
(`sdfLightInputs`) and read by both the default response and an authored light
phase (`light.visibility`). A directional light with a cascade slot reads that
slot's measured shadow, which resolves every scale. Any other directional or
point light has nothing measuring its shadow at the surface. It takes the
probes' share (one for an untraced light) times the surface's ambient
occlusion. The probes resolve only their own spacing, and the occlusion stands
in for the contact-scale shadowing that nothing measured. Because the one
value serves every kind and every consumer, an authored program that multiplies
by `light.visibility` shadows a lamp exactly as the default response does. A
cel band then treats a lamp's probe shadow as it treats the sun's cascade
shadow. An author who wants the parts apart reads `light.probeShare` and
`light.occlusion`.

**Relocation takes the nearest clear candidate.** An embedded or too-near
centre first tries an exit from the nearest surface, then quarter-cell grid
candidates in increasing distance order within half a cell on each axis. Each candidate
must clear nearby geometry and a 32-ray short-range back-face check before
its full radiance measurement is accepted. There is no openness score. The
surface map, sky and radiance cubes, and light masks use that same origin.
Only each probe's own measured cubes publish; neighbouring cubes are not
borrowed through a doorway. Sky visibility comes from the full-reach radiance
rays with their cosine weights, not from the bounded surface-map rays. The
exposure meter keeps its exact camera-to-probe scene tests.

The [mesh-GI reference](../../.claude/skills/rendering/references/mesh-gi.md)
owns packing, sampling and the CPU/shader sync contract. Surface samples have
finite angular resolution and do not certify geometry below it.

`world.probe` marks changing or unpublished reads unsettled and names the
published and replacement windows. Lighting comparisons use settled bakes and
irradiance, fixed matching cameras, and both Direct3D 12 and Vulkan.

**Considered, and set aside.**

- **A world-space radiance cache traced through the field.** Its probes and
  receivers proved every launch and every sealed boundary through the
  interpreter, so each ray paid the whole field walk on the GPU, and it needs
  the field in the frame, which the frame does not walk. It was
  replaced by the volume; its design is in the repository's history.
- **Screen-space indirect light over a probe fallback.** It is per view and
  view-dependent, so split screen, mirrors and portal windows multiply it and
  disagree, and it cannot see behind a wall from the camera.
- **Radiance cascades.** In screen space they are per view and per frame. In
  world space a dense volume of short intervals costs memory and updates in
  proportion to volume rather than surface area.
- **Per-pixel path tracing with reservoir reuse.** Several traces a pixel a
  frame, per view, is beyond the floor device's budget.
- **Indirect light baked per prototype or into lightmaps.** Either bake is
  static, while lights key on clocks, worlds are edited live and a
  bounce depends on a placement's neighbours, not its prototype.

**Off is the image without it.** With indirect light off a view takes the
authored fill, and no volume is traced.

---

[Decisions](README.md) · [The programme](../plans/rendering.md)

## In-world devices and screens

**An engaged screen is the in-world screen, framed until it passes for an
emulator (the owner's ruling).** Engaging eases the seat camera onto the
screen and mattes the window to the screen's picture; no separate 2D image
stands in for it. Framed, the picture is source-exact: whole window pixels per
source row, no CRT treatment, post or bezel on those pixels, and the matte's
fill around it, so the window is indistinguishable from a desktop emulator.
Easing out fades the room (and a screen material's treatment) back in, which
is the reveal that the player was in the world all along. One per-view value,
the screen fidelity the ease drives, turns the treatment and post off, and the
root's own placement of the seat's view shows only the picture's part of it, so
no pass draws the source a second time. A 2D emulator view that cut to the
source after the ease was rejected: it was a separate image that could disagree
with the screen (it cropped lines the screen showed).

**A device is modelled at the size a person who used one expects, and its
screen glass is exactly its picture (the owner's ruling).** A screen row's half
extents are the source's pixels times the device's pixel pitch, the pitch
carrying the pixel aspect (a 4:3 tube for a 256 by 240 console gives 8:7), and
the bezel lies outside the glass. Because the camera alone sets how large the
glass appears, no object is ever resized for a window. Every device is shown at
its real size (the owner's ruling), the pitch the real one, so the glass is
exactly the picture. A small device reads by how it is displayed, never by
being enlarged: the debug world stands its handhelds on low plinths near the
walkway with an angled name placard, the way a museum shows a small object.

**A screen's look is an authored screen material, and its picture always fills
the face.** The engine's screen shading carries one display treatment, the
display's own pixel structure drawn over the picture (scanlines, subpixel
stripes, a vignette, a glint and a soft rounded edge), whose every term a
document's `screenMaterials` row authors and a screen row or creation face
names by id; a screen naming none shows its picture untreated. A CRT is the
standard package's `crt` material (`worlds/standard/skin.puck`), never an engine
value. No material insets or warps the picture: a device's glass is exactly its
picture, so a material is presentation alone and never moves the mapping a
pointer hit takes, and a bezel is the device's geometry round the face.

**A screen lit by external content keeps lighting the room live (the owner's
ruling), and captures complete anyway.** A desktop or camera screen's glow
follows its pixels; every capture frame discards and re-derives each retained
state that holds external content, so no unfilled pixel or light reaches a
capture and no capture waits on it.

**Glass is a surface of its own after opaque visibility (the owner's ruling).**
A see-through shell, a window pane and a screen's front glass are drawn as
transparent surfaces over the opaque image, not painted fakes.

**Words on a part that follow state are a paint, not a screen and not
relief.** A slug's word, a score on a sheet or a label that tracks a cell is
lettering on a surface, so it is drawn as colour at the hit: a text paint,
whose glyph quads the host supplies each frame from the world's fonts and
state. Three options were weighed. A screen face with a text source already
drew state-bound text, but a screen is a display: it emits, it takes one of the
renderer's screen slots, and its frame did not ride a part's motion. A text run
is relief, so every changed word would bake. A paint is lit, shadowed and worn
as its surface is, rides its parent shape's motion as every parented paint
does, and changes by uploading a few hundred bytes. At the scale of a world in
which every sign, crate and counter may carry live words, the cost that matters
is that a change touches no program and no mesh. What it does not yet reach is
recorded with the open items: a text is the world's, not a body's, and the
slots are a fixed band.

### A screen is an identity; a slot is a cache entry

**A world declares any number of screens; a residency holds a slot for the ones
resident now.** A screen's identity is the authored index its material id
carries (`SdfProgramBuilder.MaxScreenIdentities` bounds the index's format, as
24 bits bound a draw index, never a count). A slot is one of the
`SdfProgramBuilder.MaxScreenSurfaces` rows every per-screen table holds
(surfaces, mappings, emission, decal descriptors, source images). Each frame the
residency settles which screens hold slots (`SdfWorldTables.SettleScreenSlots`):
a screen that shows a source or a glyph grid is wanted, the nearest to the
frame's first camera first when more are wanted than the slots hold, a screen
still wanted keeps the slot it held, and one no longer wanted gives it back. A
screen wanted past the slots holds none and shades as a screen with no source
until a nearer one leaves, so no author ever meets a capacity refusal. The
kernels read the identity-to-slot map from the tail of the mapping table
(`sdfScreenSlot`, one word an identity holding the slot plus one), so a released
screen reads zero and every table read goes through a slot it holds.
`sdf.screens.resident`, `.degraded`, `.claims` and `.releases`
(`SdfCadenceWork`) count the door's work.

**Every set of resident screens is one `UInt128` mask, walked by its set bits.**
The bound, emitting, sampled and changed slots and a light tile's lit screens
are each one 128-bit mask over `BitWords`; the kernels read the same bits as a
`uint4`, exactly as they read the light mask, and a tile holds four light words
and four screen words (`SdfWorldPackage.ShadeTileLightWords`). Per-frame work
follows the members, not the slots: the change watch compares only the slots
read now or at the last render, the emission reduction dispatches up to the
highest bound slot, and a light walk or tile visits set bits.

The options weighed, for a debug world that had used all 32 slots and an MMO
zone that authors hundreds or thousands of screens, portals and windows:

- *A wider fixed slot space (64, 128).* Rejected as the authored limit: the next
  door refuses again. Kept as the resident width, which is the GPU's shape.
- *Per-region programs, each with its own slots.* Rejected: a program per
  region multiplies residencies and their tables to dodge a count that only
  residency should decide, and a screen near a region's edge would still need
  two.
- *Per-tile screen lists on the GPU.* Rejected: the tile already carries light
  masks, and a list there is a second walk with its own bound, where one more
  mask word set walks with the light code it shares.
- *Identity and resident slot (chosen).* The material id keeps the identity, so
  no mesh bakes again when a screen comes or goes; the slot table and the masks
  stay one fixed GPU shape; and residency, which already decides what is drawn,
  decides which screens hold slots.

**The door's invariant.** A slot names at most one resident screen, no table is
read through a released slot, and no screen is refused for capacity.
`SettleScreenSlots` is the one writer of the map, its CPU twin and the resident
mask; every per-screen write resolves its slot through `SlotOfScreen` first, so
a write for a screen holding none lands nowhere; and a release zeroes the
screen's map word, clears the slot's bound flag and decal and forgets its cached
mapping before the slot can be claimed again. One residency packs its tables on
one thread at a time, so the door takes no lock.

**The glyph cells are a pool, not a run per slot.** A screen's glyph grid
claims a run of its grid's size from one cell pool (`GpuRangeAllocator`, the
one door that hands out runs), sized for `SdfWorldTables.MaxScreenDecalScreens`
full grids, so the decal buffer's bytes do not grow with the screen count.

**A closed door keeps its identity and nothing else.** A portal face's index is
assigned when its document composes (`WorldFaceCatalog`); while its door is shut
(`WorldPortalClosures`) its owner holds no session through it, so it shows no
picture, wants no slot and asks for no image
([portals in play](../plans/portals-in-play.md#6-rendering-and-memory)).
## What the state-of-the-art audit found ahead, and what stays rejected

An audit of the renderer's algorithms against published and shipped practice
([the record](../plans/state-of-the-art/audit.md)) is the starting point for the
next research round on these areas, so a round does not re-derive it.

**Ahead of, or placed differently from, the shipped answers.**

- *Content-addressed deterministic derived products.* Identity is computed from
  inputs and a miss is derived again, so a cluster node or a mesh could be shared
  across worlds by name.
- *The dual-contouring core.* A regularised quadric error minimiser in the style
  of Trettner and Kobbelt, with measured simplification. The next steps are a
  per-vertex field certification and measuring each level against the field, not
  against a finer mesh.
- *Probe global illumination that is relit rather than retraced.* It is behind
  on angular payload and its fixed 144 ray directions.
- *Percentage-closer soft-shadow cascades*, cached under a moving sun.
- *Exact resolve inputs.* Motion vectors and history identity are exact, so the
  resolve infers neither.
- *The exact, certifiable field.* It can prove a sight line or a bound, and it is
  the one advantage the pipeline barely uses; the audit's amortisation findings
  (per-vertex probe visibility, a visibility brick cache, certified bake
  skipping, field-contoured levels) are all this advantage applied.

**Rejections that still hold.** Each keeps its reason in the register above or in
the plan that decided it; the audit reread them and found no reason to reopen one.

- ReSTIR GI and path tracing as a baseline.
- Lumen's final gather at the Steam Deck's bar.
- Voxel cone tracing.
- Dense radiance cascades, GIBS and SHaRC (watch).
- Virtual shadow maps (watch).
- Interpreting SDF programs in the frame
  ([the frame does not interpret SDF programs](#the-frame-does-not-interpret-sdf-programs)).
- Hardware variable-rate shading. Per-tile shading rate in the views pass is
  Puck's own and is authored per device row.
- Vendor upscalers as engine parts. They are opt-in packages, and generated
  frames do not count toward the rendered-frame bar (the owner's call).
