# Rendering research

This page is the plan that came out of a research round on Puck's renderer.
It says what is wrong today, what we propose to build, in what order, and
what the owner is asked to decide.

Nine research passes each read one area of the renderer's code, collected
what had been measured there, and judged published work against it: the
mesh bake, indirect light, shadows, visibility, shading, nested views, GPU
scheduling, foliage and terrain, and presentation. Reviews then read the
result against the code. None of the reviews ran anything.

Three things live elsewhere:

- **The step designs.** Each decision here gives the problem, the proposal,
  the reason, the cost and what is unknown. The steps in full, with their
  checks, are in [the step designs](rendering-research/step-designs.md).
  After the owner's discussion each decision's steps move into the plan
  that owns them, which every decision names.
- **The review history.** Each review's findings and the answer to each
  are in this file's history (`git log --follow
  docs/plans/rendering-research.md`).
- **The queue.** [Open items](open-items.md#next) ranks this work against
  everything else.

## Summary

### What is wrong today

- **The frame misses its bar on the CPU, and in at least one place on the
  GPU as well.** One thread runs the simulation step and then builds the
  frame. On the lead machine under Vulkan the step takes about 8 ms and
  building the frame about 17 ms, where 60 frames a second allows 16.7 ms
  for both. At the meadow the GPU alone also misses
  ([the job system plan](engine-job-system.md)). Every one of these
  readings was taken in a window of 1280 by 900 or smaller. The bar is read
  at 2560 by 1440 on that machine, which has more than three times the
  pixels, so the GPU's share is larger than any figure on this page shows.
- **The slowest frames are garbage-collection pauses.** In one run of 600
  loop iterations, 64 ran over 16.7 ms, and the worst carried pauses of 20
  to 28 ms. The bar is read on the slowest frame, so no other fix shows
  until this one lands.
- **Indirect light follows a lighting change far too late.** After a
  switch to night, the finest lighting data was up to 44 seconds old. The
  GPU is not short of time: each lighting job takes about 25 ms to go
  through the frame's queue and back, for 1 to 2 ms of GPU work. The
  automatic exposure meters that stale light, so night is exposed as day.
- **Lighting and shadows look wrong almost everywhere, and three causes
  are defects.** Linear light values are shown as if they were already
  encoded for the display. A shadow's softness jumps about seven-fold
  where the near shadow map hands over to the far one, and a moon that
  emits no light holds a shadow map all day. Views through portals and on
  screens get no shadow maps and no ambient occlusion at any size.
- **A change to the bake's code costs a whole bake.** An edit to a creation
  rebakes only that creation. An edit to the code that bakes rebakes every
  mesh, which takes just over a minute. Six of the fourteen blend exhibits
  fail the mesh audit.
- **GPU memory is never given back, and a mesh that has not arrived is a
  hole in the picture.**
- **Nothing is budgeted for the weaker devices.** Every measurement comes
  from the RTX 4070. No plan says how many milliseconds or bytes a pass may
  use on the RTX 2060 or the Steam Deck.

### What we propose

1. **Fill the engine gaps first.** A gap is a limit in the engine that
   stops a place reaching its look: terrain that bakes too coarsely and
   has no rock treatment, no shadow past 120 m, relief that buries
   anything set into it, and water whose colour ignores its depth. Twelve
   are listed, ranked by what each blocks
   ([engine gaps, filled first](#engine-gaps-filled-first)).
2. **Start now, without waiting for any measurement:** stop allocating
   memory every frame; fix the colour conversion; give the shadow maps to
   the light that is lit; make what a nested view gives up an authored
   table; and start the terrain research
   ([work that starts now](#work-that-starts-now)).
3. **Measure, at the size the bar is read at.** Build the benchmark's
   missing readings, and take them on the RTX 4070 at 2560 by 1440
   ([stage 0](#stage-0-measure)).
4. **Then the frame thread and the lighting's round trips.** Capture and
   prepare only what a frame will show, shorten the simulation step, and
   let lighting jobs overlap inside a budget that follows the GPU time the
   frame leaves free.
5. **Then the shared structures that scale:** one table of instances
   tested once for every pass, and one chain of meshes per shape from fine
   to coarse, each measured against the authored shape.
6. **Hold every decision to a budget per device.** [The budget
   table](#the-bars-and-the-budget) gives each pass its milliseconds and
   each store its bytes on all three devices. The RTX 4070's column is
   measured. The other two are targets, and their code paths are written
   as if the hardware were on hand.

### What we are unsure of

- Where most of the 17 ms of frame building goes. Only about 1 ms of it
  has a breakdown.
- How far the RTX 4070 is from its bar at 2560 by 1440. Nothing has been
  read at that size.
- Whether the cold bake is limited by its processors or by waiting on the
  GPU.
- What it costs a weak device to turn a received program into a first
  surface. An MMO client depends on that number and nothing has measured
  it ([the fifth bet](#the-bets)).
- Seven designs rest on something no engine we read does. Each is a bet
  with a number that ends it ([the bets](#the-bets)).

### What the owner is asked

Four questions about what the engine does, listed in full under
[questions for the owner](#questions-for-the-owner):

1. Confirm how a derived product that is shared between machines is named
   and trusted. The lead recommends a design, and it is written into this
   page marked as awaiting that confirmation.
2. Confirm the memory bar's figures for each device.
3. Whether a device's quality table may lower the render scale.
4. Whether a world that shows a viewer only pictures renders them once for
   an audience, and never once for each observer.

No look value is among them. The tone curve, exposure, a light's softness,
the look of night and the quality tables are authored values over a neutral
engine default ([looks are authored](#looks-are-authored)).

## Words this page uses

| Term | What it means here |
|---|---|
| Tick | One step of the simulation. The debug world ticks 30 times a second. |
| Frame thread | The one thread that runs the tick and then builds the frame (`HostLoop.Run`). |
| Produce | Everything the frame thread does to build one frame and hand it to the GPU. |
| View | One rendering of a world from one camera. A nested view is shown inside another: through a portal or a window, or on a screen in the world. |
| Residency | The GPU-side copy of one world: its instance tables, baked meshes and lighting data (`SdfWorldResidency`). |
| Capture | Reading a world's poses and state for one frame and packing them for the GPU. Preparing a residency captures it. |
| Bake | Puck's shapes are authored as signed distance fields. The bake samples a field and builds a triangle mesh from it. A cold bake starts from an empty mesh store. |
| Tier, level | A tier is a mesh built from the field on a coarser grid. A level is a simplified copy of one tier's mesh. |
| Floor | A shape's coarsest acceptable tier, kept in GPU memory so there is always something to draw. |
| Cascade | One of the shadow maps a directional light uses: a near one, a far one, and one for a second light. |
| Probe | A point in a 3D grid that stores the indirect light arriving there. Tracing a probe finds which surfaces its rays hit. Relighting it shades those hits with the current lights. |
| Kernel | A compute shader: a program the GPU runs over many pixels or records at once. |
| Bar devices | The RTX 4070 and RTX 2060 at 60 frames a second (16.7 ms a frame), and the Steam Deck at 45 (22.2 ms). |
| Route, chapter | The benchmark plays a scripted walk through the debug world. A chapter is one named leg, reported on its own. |
| The register | The record of settled rendering decisions ([decisions](../decisions/rendering.md)). |

## The bars and the budget

The owner has ruled how the frame bar is read
([AGENTS.md](../../AGENTS.md#puck-is-pre-release), "Upper hardware
enhances"):

- **At what size.** 2560 by 1440 on the RTX 4070, 1920 by 1080 on the RTX
  2060, and the Steam Deck's own panel in its native configuration.
- **In both topologies.** With the world's authority in the same process,
  and with a remote authority on the Azure deployment. In the first the
  frame thread pays for the tick. In the second it pays to receive and
  apply what the authority sends.
- **On the RTX 4070 first.** The work meets both bars there before
  anything is tested on the RTX 2060, the Steam Deck or an HDR display. No
  fundamental work waits for that hardware. The other devices' paths are
  still written now: a quality table authored for each device, and a
  memory bar for each device.

So "highest quality" on a device means that device's authored table. The
standard package authors the three tables, and any world can override
them. The engine's work is the mechanism that selects a table by device.

The table below splits the RTX 4070's frame so each decision can say
which part it moves and by how much. Its figures are a forcing function,
in the sense of the owner's velocity rulings: they keep the work honest
and make a regression visible, they are not finish lines, and a change is
judged by what it does for a player moving through the world, never by
whether it crosses a row. The split is provisional. Every measured figure
was taken in a window of 1280 by 900 or smaller, so it understates the cost
at 2560 by 1440. Stage 0's first readings at the bar's size replace the
measured column, and the split is then set from those readings and from
the gains each decision claims, with the reasoning written beside each row.
Until then, the targets below show the shape of the frame, not commitments.

| Part of the RTX 4070's frame at 2560 by 1440 | Measured so far, at a smaller window | Provisional share | What the share assumes |
|---|---|---|---|
| The whole frame | Misses: about 25 ms on the frame thread | 16.7 ms | The bar |
| Tick, authority in process | About 8 ms, every world instance stepped in series | 4 ms | The parallel tick (the job-system plan) halves it; see the note on world count |
| Applying a delivery, authority remote | Not measured | 1.5 ms | Stage 0 measures it on the Azure deployment |
| Produce | About 17 ms | 9 ms | Decisions 2 and 4: work follows change, and view recording runs in parallel |
| GPU: drawing the meshes | 1.7 ms at the lobby | 1.5 ms | Caster culling and compaction offset the larger window |
| GPU: shadow maps | 3.9 ms at the lobby | 2.5 ms | Shadow maps do not grow with the window; caster culling cuts them |
| GPU: shading | 3.7 ms at the lobby | 4.5 ms | About 3.2 times the pixels of the measured window, so this assumes the shading pass gets about 2.6 times faster per pixel; unproven, and the row most likely to move |
| GPU: glass and nested views | Not split out | 2.0 ms | One nested view in frame; see the note on views |
| GPU: image history, bloom, encode and the overlay | Overlay 0.9 ms with the benchmark panel | 2.0 ms | Decision 13's smaller history |
| GPU: background work (lighting, bake, uploads) | An allowance of 4.2 ms, of which the share used is unknown | At least 1.5 ms | Decision 5: the allowance follows the GPU time the frame leaves free |
| GPU: margin | None: about 12.6 ms for the main view alone, before nested views | 2.7 ms | |

| Store on the RTX 4070 | Today | Provisional cap | What the cap assumes |
|---|---|---|---|
| All GPU memory | Not measured as one figure | 8 GB of the card's 12 | Room for the system and other programs (Puck plays nice with its host) |
| Resident baked meshes | About 3 GB, growing toward 4 GiB and never shrinking | 3 GB resident around the viewer | Residency evicts by distance and view; the cap is independent of how large the world is |
| Image history | 188 bytes a pixel for each frame in flight: about 2.1 GB at 2560 by 1440 | 0.9 GB | Decision 13 |
| Lighting probes | About 72 MB a table in up to four copies, and a hit cache of about 90 MB, for every world shown | 0.6 GB in one shared pool | One pool for every world in view, not a pool per world |
| Shadow maps | 96 MiB for the cascades, and 144 MB of glass storage for each shadowed view at the highest tier | 0.4 GB | Glass storage shared across views |
| The bake's own memory while baking | More than 10 GB of private memory in a cold bake | Not a play-time cost, but bounded | The bake's peak must fall far enough that a player's machine can derive (see the fourth principle) |

Notes on reading it.

- **Every cap is per viewer, never per world.** At the scale Puck is
  built for, the world a player stands in is unbounded and many worlds can
  be in view through portals. A memory cap is therefore a cap on what is
  resident around the viewer, and a store whose size follows world size or
  world count is a defect against this table, whatever its total today.
- **Views and worlds in frame.** The nested-view row and the tick row
  assume one nested view and today's few worlds. Each extra view in frame
  and each extra world stepped costs more, so stage 0 measures the cost of
  one more view and one more world, and the design states how that cost is
  bounded (by screen coverage, by distance, by a cap on views stepped at
  full rate). Every world instance stepping in series on the frame thread
  (`WorldInstanceHost.StepInstances`) does not scale to many visible
  worlds; the [job-system plan](engine-job-system.md) owns that question.
- **The image history's 2.1 GB is arithmetic, not a reading**: 188 bytes
  times the pixels of 2560 by 1440, times three copies in flight. It is the
  single largest effect of the owner's size ruling on this plan, and it
  moves the history split ([decision 13](#13-shade-through-one-path-and-shrink-the-image-history))
  from last to early.
- **The other devices are not given milliseconds here.** Under the owner's
  ruling their testing waits until the RTX 4070 meets its bars, and their
  hardware differs too much for borrowed figures to mean anything: the
  Steam Deck's GPU has a small fraction of the RTX 4070's throughput. Each
  device's quality table (its window, shadow tier, probe tier and detail)
  is authored so that its counted work fits a share of its frame, and is
  checked on the lead machine by counted work: draws, vertices, shaded
  pixels, shadow texels and dispatches, which are deterministic and read
  the same on every machine. The memory bars for the RTX 2060 (4.5 GB of
  its 6) and the Steam Deck (10 GB for the process and GPU together, the
  streaming plan's figure) are [a question for the owner](#questions-for-the-owner).

Each decision below states what it claims against this table. Stage 1's
fixes add GPU work: an encode pass for a single view, shadows for nested
views, and a probe lookup where no shadow map reaches. Each of those items
therefore lands with its measured GPU cost beside it.

## What Puck's renderer is

The thesis was written after the build order, and the first three stages
would be the same without it. Its use is for the later stages: it says
which existing mechanism each decision should extend, and it names the
bets.

In one sentence: Puck's renderer turns authored programs into pixels, and
treats everything in between as a cache that knows what it was made from,
how far it is from its source, and what it costs. Four principles follow.

### The four principles

**1. The authored field is the reference, and everything drawn is a
measured copy of it.** A shape is authored as a distance field, which is a
program. A baked mesh, a coarser mesh and a probe's hits are all derived
from that program. Because the program is still there, each derived
product can be measured against it, in both directions, and can record
that error in a unit a consumer turns into pixels or shadow texels. Part
of this exists: the bake already bounds a simplified mesh against the
field and finds lost parts (`SdfMeshFieldLevels.Measure`), though no
decision reads that record yet. An engine that starts from triangles
measures a coarse mesh against a finer mesh, and cannot ask what the
simplification erased. Not everything has a field behind it. An author can
supply a triangle mesh, and an emulated machine supplies frames. Those are
identified by their real inputs and are not measured against a field.

**2. Work follows change.** A frame should cost what changed since the
last frame, plus what a viewer newly sees. Puck can know what changed
because its simulation is deterministic and presentation reads state
through one path. Today a view still learns that nothing changed by doing
all the work: `SdfWorldResidency.IsUnchanged` prepares the residency
first and compares a hash afterwards. Two things keep this true while the
world moves. Inputs that change all the time are kept out of caches that
hold structure: a moving sun should relight probes, not trace them again.
And an authored program's bounds say how far its change can reach: a
motion program's swept box bounds where a moving part can be.

**3. A frame is demand from the display, flowing through worlds.** There
is no root scene. The display asks for a view of a world at a size, by a
deadline. That view asks for others: a portal, a painting, an emulator's
screen. Each is served what its consumers are owed first, and the rest is
divided by the pixels each one presents. Demand is also predicted: as a
player approaches a portal, the far side prepares what the player is
already allowed to see of it.

**4. Results cross between worlds under each world's rules.** A world
chooses where its simulation runs. It can also choose where its derived
products come from, and what each viewer may receive. A viewer's machine
derives only from what it was delivered, so drawing and hidden
information follow one rule: at a card table, another seat's face-down
hand cannot leak through a shadow or a reflection, because its inputs
never reached the spectator.

### Three ways worlds compose

A world is the unit of authority, identity and disclosure. The unit of
rendering is the view. Worlds reach a view in three ways.

- **Nest.** A world in another space is another view at another size:
  behind a portal, or on a screen.
- **Merge.** Worlds in the same space feed one view's table of instances
  as rows: two adjacent zones, or a character standing in a zone. Adjacent
  zones are not merged today. The band of a neighbour's geometry that
  crosses a border holds at most eight placements and eight bodies
  (`WorldAdjacencyGeometry`), and the debug world authors no adjacency.
- **Publish.** One world hands another a finished product, and the
  receiver cannot derive anything further from it: scenery a neighbour
  places in the near world, the pictures a viewer gets when it is allowed
  only pictures, the light leaving an opening, and a machine's frame.

Publication also settles who renders the pictures for a viewer that is
allowed only pictures. We propose that a
picture is rendered once, by the publishing world's host, for a camera and
an audience the world declares, at a rate it authors, and travels as an
image. It is never rendered once for each observer, which at MMO scale
would mean a server rendering for every player. A viewer who wants a
camera of its own needs rows. This is
[a question for the owner](#questions-for-the-owner). Today only the
in-process case exists, and a remote world's screens show nothing.

### What is Puck's own, and what is borrowed

Truly Puck's own, as far as the sources we read show:

- Error measured against an analytic source in both directions, kept as a
  record that chooses between products.
- Converting a part exactly where its program has a closed form, and
  deriving a finer mesh than any stored.
- Caches keyed by what a viewer was delivered, so drawing and hidden
  information follow one rule.
- A portal crossing prepared ahead, limited to what the walker may
  already see.
- Light handed across a boundary between two authorities, with a test
  that it discloses nothing.
- Continuous change carried as proved clock spans and motion programs, so
  a moving sky sends nothing over the network.

Standard techniques that appear here under Puck's names:

| This page's idea | The standard technique |
|---|---|
| A product identified by its inputs | A content-addressed cache of derived data |
| Work follows change | Incremental computation |
| Merging worlds into one view | Level streaming |
| A floor | The coarsest page a virtual-geometry system always keeps (Nanite's root page) |
| A still nested view that keeps its picture | A render target drawn on demand |
| Light stored for each source | A small basis of light groups |
| Distance bricks for tracing | A global distance field. Puck built one once as its main picture and rejected it on measurement |

Borrowing these is right. The claim to originality rests on the first
list only.

### What the thesis was missing

Five ideas Puck is placed to try were absent, and each now has a home.

- **Send programs, not products.** The source of a shape is a small
  program, which is cheap to send. Deriving from it is what costs. The
  measure is the time from receiving a program to drawing its first
  surface ([the fifth bet](#the-bets)).
- **Compose characters from shared parts.** In a city of player
  characters nearly every look is distinct, so sharing whole looks saves
  little. Sharing baked parts and composing them as rows does
  ([the third bet](#the-bets)).
- **Let the authority decide what to send by proved visibility.** The
  field evaluator can prove a sight line clear or blocked
  (`SdfFieldEvaluator.TryCertifiedLineOfSight`), and nothing outside the
  tests calls it. An authority could use it to withhold what a viewer
  provably cannot see. This belongs to [the disclosure plan](disclosure.md),
  and its first step is a count of what the proof costs for each pair of
  viewer and body.
- **Let demand reach the simulation's rate.** A world nobody sees is drawn
  less often but still steps at full rate, in series, on the frame thread
  (`WorldInstanceHost.StepInstances`). Whether an unseen world may step
  less often is its authority's choice, and the question goes to
  [the job system plan](engine-job-system.md) with decision 3.
- **Budget in counted work.** Deterministic counts hold on every machine,
  so they can stand for a device that is not on hand
  ([the budget](#the-bars-and-the-budget)).

### Where the thesis is weak

- **It does not close today's gap.** The lead machine misses its bar now.
  The thesis shapes the fixes and replaces none of them.
- **Work that follows change saves still frames, and the bar is read on
  the slowest frame.** A quick turn in a crowd changes nearly every pixel.
  The saving is real where views stand still (unseen worlds, held
  screens), and it frees GPU time for background work. It lowers the
  slowest frame only where that frame is slow because many views render
  at once.
- **The field is a reference only where it is correct.** An authored field
  can overstate its own distance, and every audit inherits that.
- **A counted price is not GPU time.** A short shading program that keeps
  many values alive can cost more than a long one. Only measurement on
  the device finds that. And nothing yet prices every consumer in one
  unit: that step of decision 4 is a design round that has not started.
- **One unit of error is exact for geometry and a model for light.**
- **Puck pays twice: for the bake, then for the triangles.** A grass
  blade bakes to about 474 triangles where its prism has a dozen. The
  thesis holds only where the field gives fewer and better triangles than
  a hand-made mesh would.
- **Sharing products between machines that do not trust each other is
  dropped**, and so is sharing precomputed light, which a moving sun makes
  stale all day.

## How a shared product is named and trusted

*This section is the lead's recommendation and awaits the owner's
confirmation. Nothing is built on it until then.*

**The problem.** A baked mesh's identity today hashes the bake's own code
and each backend's compiled kernels (`SdfMeshBakeIdentity`). That is right
for a cache on one machine: any change to the baker makes the old mesh a
miss. It cannot work for products that are shared or fetched. In an MMO
every patch to the baker would make every stored product a miss on every
client, and an authority would have to derive again for every client
build and every backend. The owner has ruled that an identity is
"computed from inputs, never recorded", so a version number bumped by hand
is not available.

**What we propose.** Two names for two jobs.

- **A local cache key** stays as it is: computed from every input, the
  baker's code included, and never recorded.
- **A shared product's name** says what the product means: the source
  program, the authored parameters, and the version of the product's byte
  format. The baker's code is not part of it.
- **Trust comes from checking the product, not from who made it.** A
  machine that receives a product, or keeps one across a patch, verifies
  it against the analytic source within the tolerance the product
  declares. The check covers every attribute the product carries:
  geometry, normals and material ids, not distance alone. A product that
  passes is accepted whichever baker, backend or machine made it.
- **A better baker re-derives only where better quality is wanted.** An
  old product that still verifies stays valid. The engine or an author
  asks for a tighter tolerance where it matters, and only those products
  are derived again. A patch never invalidates everything.
- **Nothing is bumped by hand.** The decoder refuses a byte format it does
  not know, so a stale format shows as a refused product and a counted
  derivation, never as a wrong mesh.

**What it costs.**

- *Verification is work.* Checking a product against the field costs
  field samples. If a full check costs as much as deriving, fetching
  saves nothing.
- *A spot check does not see everything.* Checking a sample of points is
  cheap and can miss a thin feature or a part that vanished. The check
  therefore samples over the authored shape's bounds, not the product's
  own, and the share of the surface it covers is a stated number.
- *Some attributes have no field to check against.* An authored triangle
  mesh and a machine's frame have no analytic source. A probe's hits
  depend on the whole scene around them. Those are derived locally, or
  arrive as publications from the world that owns them.

**What it does not change.** Who may fetch a product is still the
disclosure layer's decision. A name grants no access, and a viewer that
was not delivered a shape's program cannot verify a product of it, so it
receives only publications.

**What we still have to find out.** What verification costs against
deriving on each device. How wide the difference between GPU vendors is
against a declared tolerance. How the format's version is produced
without a number someone edits: we propose computing it from the layout
the encoder and decoder share.

## The bets

Most of this plan is ordinary repair. Seven items are different: each
rests on something no engine we read does, and each can fail without
anyone noticing or at great expense. A bet gets a design memo and a
bounded prototype before anything is built on it. It also gets a number
that ends it, a time box for one lane, and a fault put in on purpose that
its check must catch. A bet that
passes its box without a result is stopped and reported.

| Bet | What ends it | Time box | The fault its check must catch |
|---|---|---|---|
| 1. A still view stands on the change records the client already reads | On the benchmark's multi-view chapter, the slowest frame falls by less than 2 ms and background work completed per second rises by less than a quarter | Memo 3 days, prototype 7 | One record is silenced on purpose (one emitter stops reporting its revision). The audit must report the stale view in that frame |
| 2. A portal crossing prepared ahead | The slowest frame in the second around a crossing is slower than the slowest frame of the walk up to it, or more than half the bytes prepared are wasted when the walker turns away | 7 days | Preparation is switched off for one walk. The check must report the missing surface at the crossing |
| 3. Worlds merged into one view, and crowds built from shared parts | With 200 characters of distinct looks built from a wardrobe of 40 parts, more meshes are baked than there are distinct parts and blended seams, or work for each frame grows by more than a tenth between 50 and 200 characters while the visible ones stay fixed | Memo 4 days, prototype 10 | A part's identity is made to include its wearer. The check must report bakes growing with the crowd |
| 4. Products shared by a name that says what they mean | Fetching and verifying a product takes longer to a first usable surface than deriving it, or more than 1 in 100 products exchanged between GPU vendors fails verification at its declared tolerance | Memo 4 days, prototype 8, after the owner confirms the design | A product with the right name and one part removed, one material id swapped and one patch of normals flipped. Verification must refuse each |
| 5. A program received cold shows a surface quickly | On the RTX 4070 the median creation of the debug world takes more than 2 seconds from program to drawn floor. The Steam Deck's target is 5 seconds | Measurement 2 days, prototype 5 | A program whose floor cannot be baked. The frame must show the authored fallback and count the failure, never a hole |
| 6. Light carried across an opening between two worlds | A few samples of light cost more than a quarter of a second view's GPU time, or differ from a full reference by more than a tenth on the lit floor, or any derived light differs between two deals a spectator may not tell apart | 8 days | The opening's light is made to depend on a private hand. The disclosure check must catch the difference |
| 7. Tracing light with the field instead of triangles | Neither field method cuts the probe trace's steps by a third at equal quality, or one false "clear" occurs against the current geometry | 6 days, after stage 3 measures the trace | A blocker is added after a box was proved empty, and the proof is not withdrawn. The check must report the false "clear" |

### The first bet stands on the client's records

The first bet matters most, because decision 2 leans on it and because
the obvious place to read change from is the wrong one.

That place is the authority's state arena, which records every committed
write. Presentation never reads an arena: no file in the presentation or
renderer projects
(`Puck.World.Client`, `Puck.SdfVm`, `Puck.World`) mentions `StateArena`.
A remote client never holds the authority's arena at all. So the bet is
restated on what the client already has, which holds for a colocated
client and a remote one alike.

Two records exist and are read today.

- **The state mirror** (`WorldStateMirror`) is the one path presentation
  reads state through. Every delivery names the rows that moved
  (`WorldStateStamp.MovedRows`), and a refresh reads only the slots bound
  to those rows, so "a tick that moves nothing reads nothing". The mirror
  exposes a revision that moves when any slot's value changes, a second
  for colours, and a per-slot answer (`Revision`, `ColorRevision`,
  `Changed`).
- **The scene's revision vector** (`ISdfSceneEmitter.WriteRevision`). Each
  part of a scene reports its own counters every frame, and the composed
  program is rebuilt when any one differs. The counters are kept apart
  and never summed, because a sum can cancel.

The bet is that these two, with a few additions, cover every input a view
reads. Its first step is a memo that lists what
`SdfWorldResidency.Prepare` reads on the client and names, for each input,
the client record that covers it: the mirror's revision, the scene's
revision vector, the tick's snapshot, the clock phase evaluated for the
presented tick, or the camera. Inputs with no record are listed as gaps.
The prototype then closes gaps in an existing revision vector. It does not
join the authority to the frame.

Three rules come with it, because its failure is silent: a view that
wrongly keeps its picture also lowers the count the work is judged by.

- The existing refusals stay outside the shortcut. No view keeps its
  picture while meshes are pending, lighting is settling or a source is
  waiting, or while a screen's image is rewritten in place
  (`SdfWorldTables.ForcesRender`).
- An audit compares each skipped view with the full decision the
  residency makes today, and counts views that were eligible, skipped and
  audited. A check that never skips audits nothing and would pass, so the
  audit requires a real skip in a still scene.
- Nothing forces a future reader of a new input to add a record. The
  audit is the only check for that, so the shortcut stays under
  adversarial review ([hazards](../development/hazards.md#silent-invariants)).

Checked for this section: the three projects' lack of any `StateArena`
reference, the mirror's summary and its refresh from the stamp's moved
rows, and the revision vector's contract. Not checked: that every input of
`Prepare` reaches the client through one of these. That is the memo's
job.

## The decisions

Fifteen decisions follow. Each gives the problem, what we propose, why,
what it costs or risks, and what we still have to find out. Each ends
with what was checked, and with the plan that owns its steps. "Checked"
means the code or the source was opened for this page and says what the
text says. "Not checked" covers three weaker kinds of claim, and the note
says which: a reader's report that was not reopened, a single measurement
on the lead machine that was not repeated, or arithmetic. The steps in
full are in [the step designs](rendering-research/step-designs.md).

### 1. Measure before building

**The problem.** Most of the gains this plan ranks are arithmetic that no
counter can confirm. Every reading comes from one GPU, in a window
smaller than the size the bar is read at.

**What we propose.** Take the missing readings first, on the RTX 4070 at
2560 by 1440, on both backends and in both topologies. The readings that
decide the most:

- produce split by part, and the tick split by phase and by world;
- garbage collections and bytes allocated for each loop iteration, by
  source;
- GPU time for each frame, split into the main view, nested views and
  background work, and how much of its allowance the background work
  uses;
- for each attempt to present a frame, whether it was shown, skipped or
  dropped, and the time from input to display;
- what limits a cold bake, with its time and its peak memory;
- the time from receiving a creation's program to drawing its first
  surface;
- the time from saving an edit to seeing it, for a `.puck` document, a
  shader and C#;
- counted work for each pass, which fills the budget table's counts.

Each reading has a row in the step designs that names the step that needs
it. A step waits only for its own rows. Nothing here changes what a player
sees, and nothing is recorded as a baseline.

**Why.** A fix ranked by arithmetic can spend weeks on a cost that turns
out small.

**What it costs or risks.** Measuring delays fixes the owner can already
see. So the fixes that rest on the code alone do not wait
([work that starts now](#work-that-starts-now)).

**What we still have to find out.** Everything in the list. The same
readings on the RTX 2060 and the Steam Deck wait for the hardware, and no
step waits for them.

**Evidence.** Checked: how the Vulkan presenter reports timing, the window
of 32 frames behind a pass timing, and that world instances step in
series. Not checked: produce at about 17 ms and the tick at about 8 ms are
single readings on Vulkan.

**Owning plans.** [The benchmark](benchmark.md) for the route and the
report, and [the job system plan](engine-job-system.md) for the frame
thread's breakdowns.

### 2. Capture and prepare only what a frame shows

**The problem.** The frame thread prepares every world it holds before
anything has decided what the frame needs. Preparing a residency captures
it: it reads poses and state and packs them for the GPU. The work is then
asked for a second time, because the question "has this view changed?" is
answered by preparing and comparing a hash afterwards. Each world that is
arriving also renders a hidden window, because only a scheduled render
moves its data to the GPU.

**What we propose.** Capture only the residencies a frame needs, in this
order within a frame.

1. *Upkeep for every residency.* Finished GPU jobs are collected and
   their callbacks run, uploads are polled, and requests to give memory
   back are answered, whether or not the residency is captured.
2. *The main world's capture*, which places the cameras. This stays first
   because nothing else can supply them.
3. *The list of what to render is sealed after the cameras are placed*,
   not before.
4. *A residency is captured only if something reads it this frame:* a view
   on screen, an upload that is owed, or a consumer's minimum. A minimum
   is work owed whatever is on screen: a predicted arrival's first image
   and lighting, a screen out of view whose glow lights a visible room,
   picking, and the lighting trace. The minimums replace the hidden
   render and land in the same change.
5. *The schedule*, and then one reconciliation of the final render list
   before commands are recorded.

Stop allocating memory every frame, on every thread, at the sources a
trace names. This is first in the whole plan and needs nothing else.

A review of this order against the code raised ten findings. The design
answers each. None was reproduced in a running World.

| Finding | What the design does |
|---|---|
| A filming camera's views are added to the render list after the scheduler has copied it, so a window only that camera sees is never drawn | The scheduler takes its list at the point the frame seals, after camera placement (step 3) |
| A destination can reveal a sky world after the main capture has fixed the list | Reads that a capture discovers are added before scheduling. Possible reads are declared ahead, and a change to the set of graphs is published whole at a frame boundary |
| A visibility test can prepare the far world while the host is still composing, and so read last frame's clock | Visibility gets a read-only lookup of occlusion that is already complete. Where the answer is not known yet the view is kept, so it is served too much and never missed |
| Changing a viewer's route does not withdraw a picture already published | A defect in today's code, filed in [the security packet](open-items.md#security-review). Disclosure is enforced where a picture is published and where it is reused, and the viewer's old output is invalidated at once |
| An audit that compares hashes cannot certify a skipped redraw, and a check that never skips passes with nothing audited | The audit compares each skip with the full decision, and counts eligible, skipped and audited views ([the first bet](#the-first-bet-stands-on-the-clients-records)) |
| "A view that covers some of the frame" is too wide a reason to capture, and it misses readers the final schedule adds | Capture follows actual readers, using the scheduler's own rules, and the final render list is reconciled once before recording. Every package's freshness question goes through the same filter |
| Upkeep for an unseen world is more than polling: finished jobs owe callbacks, a trim walks mesh pages, and a shader reload needs built pipelines | Completion is served before any packing. Quiet polling and requested work are counted apart. A reload stays pending until there is a safe place to install it |
| A world behind a portal that is skipped for several frames loses the time between captures, so its animation shows an old phase | A residency receives the time since its own last capture, or seeks its timeline to the presented time. Exposure adaptation gets the same |
| A count of fallback images can miss a missing picture, and an unchanged capture count can be correct | The check places a changing marker in each destination and reads reader, producer and picture on the delivered frame. Captures, packs and filmed views are counted apart |
| A delayed exposure reading is stored by slot number, so it can land on the next view to take that slot | The reading carries the identity and generation of the view that submitted it, and a reading for a retired generation is discarded |

**Why.** Capture cost today grows with the number of worlds held, not
with what is on screen. The worlds that cost the most for nothing are the
ones no view shows, and those need no answer about freshness at all.

**What it costs or risks.** Upkeep's cost is not constant: a trim or a
reload is real work, which is why it is counted apart. A shortcut that
lets a shown view keep its picture without capturing is a second answer
to a question the hash answers today, and it fails silently. It is built
only if the breakdown calls for it, as the first bet.

**What we still have to find out.** The breakdown of produce, which sets
the order of the smaller cuts: field queries on the CPU (about a third of
the meadow's frame), draw records uploaded again each frame, and motion
interpreted every frame.

**Against the budget.** Produce, from about 17 ms toward 9 ms. The world
nobody sees still steps and still delivers. That is the tick's cost and
this decision does not reduce it.

**Evidence.** Checked: the loop order, the prepare of every residency,
the freshness question that prepares first, and, of the ten findings, the
render list copied in `RenderGraphRuntimeNode.ProduceFrame` before
`WorldViewGraphHost.RootFilmedViews` adds to it, the exposure reading
published by slot number (`SdfViewExposures.Publish`), and a private
session measuring its own interval (`WorldSessionFrameSource.CaptureFrame`).
Not checked: the other findings' code paths, and every failing case, which
are deductions from reading. The allocation and pause figures are one
run on a loaded machine.

**Owning plans.** [Streaming and memory](streaming-and-memory.md#decision-4-a-residency-is-staged-by-its-demand-not-by-a-render)
for the capture order and the minimums,
[the job system plan](engine-job-system.md) for allocation, and
[disclosure](disclosure.md) for the withdrawn picture.

### 3. Keep the tick ahead of the frame, and make it shorter

**The problem.** A frame that also steps the simulation carries a whole
tick before it starts on the picture: about 8 ms in front of about 17.

**What we propose.** Keep the tick where it is, and shorten it. Its work
is split into partitions that run on one thread pool, and the frame
thread waits for them inside the step. The order is the job system
plan's, and it is a real dependency: first bound every wait the console
and input can be held behind, then build the pool, then split the tick,
then fix the tick's serial costs in the order its breakdown gives.

**Why.** The alternative is a tick on its own thread, which shortens the
frame by showing the player an older state. The register rules that out:
"A lower tier, a coarser shadow snap, reduced contact detail or a frame
of latency is never the fix for a missed bar". Puck's purpose weighs feel
as heavily as looks.

**What it costs or risks.** The two planned splits may not be where the
debug world's tick spends its time. One trace shows about a third of it
moving bodies and under a third evaluating rules, each with a serial
cause open items names. Every world instance also steps in series, and
stepping them in parallel is weighed once their cost is measured.

**What we still have to find out.** Where the tick's time goes. Whether
the tick and produce each fit a frame while their sum does not. If so, a
tick on its own thread returns as a question for the owner, with a
measured cost in latency, because it reverses a recorded ruling.

**Against the budget.** The tick, from about 8 ms to 4 ms with the
authority in process. With a remote authority the client pays to apply a
delivery instead, which nothing has measured.

**Evidence.** Checked: the loop order. Not checked: the tick's shares,
which are one trace, and the job system plan's contract, which is taken
from that plan.

**Owning plan.** [The job system plan](engine-job-system.md#6-build-order).

### 4. Run background GPU work without waiting a frame for each job

**The problem.** Background GPU work is everything that is not drawing
the frame: relighting probes, tracing their rays, sampling the field for
the bake, and uploading meshes. It is slow because it waits on round
trips, not because the GPU is busy. The lighting admits one job at a
time, blocks on each answer, and is granted work for each rendered frame,
so a game presenting 30 frames a second gets half the lighting of one
presenting 60. A group's bake makes about twenty round trips. A full bake
batch is about 32 ms of GPU work against an allowance of 4.2 ms, so a
bake during play already submits jobs several frames long.

**What we propose.** In this order:

1. *Make the budget follow the GPU time the frame leaves free.* The
   engine already measures the frame's GPU load for dynamic resolution
   (`IWorldFrameLoadSource`). The background share is what the views
   leave of the display's period, with a lower limit so lighting never
   starves. Every job is cut to fit one frame's allowance.
2. *Cut bake jobs to the allowance.*
3. *Let several lighting jobs run at once*, let relights that a lighting
   change made stale share the worker with traces still owed, and grant
   lighting work for each second, not each frame.
4. *Move the lighting's work list onto the GPU*, so the CPU reads only
   counters.
5. *Hand background jobs to the GPU before the frame's own passes are
   recorded*, so an answer arrives a step sooner.

**Why.** The budget rule comes first for a reason that is easy to miss.
The main view uses about 12.6 ms of GPU time and the background
allowance is 4.2 ms. If the lighting used all of its allowance the two
would exceed a 16.7 ms frame. The first fix that makes the lighting
effective would create that overrun.

**What it costs or risks.** An average budget cannot protect one frame: a
long job already queued delays the frame behind it. That is why each job
is cut to a frame's allowance. Two controllers reading one load figure
can fight, so their order is stated: optional background work gives way
before the render size moves. The work list on the GPU and the early
hand-off sit in two classes of defect that only review catches.

**What we still have to find out.** How much of its allowance the
background work uses today. How the 44 seconds divides between its three
causes. The overlapping jobs exist on an unmerged branch that has never
run in a World.

**Against the budget.** The background row: at least 1.5 ms of GPU time
a frame, taken only from what the views leave.

**Evidence.** Checked: the one-at-a-time admission, the blocking wait,
the grant for each frame, the bake's batch size, and the load source.
Not checked: the 25 ms round trip, the 44 seconds and the 12.6 ms are
single readings, and the 12.6 ms records no window size.

**Owning plans.** [Illumination](illumination.md) for the lighting, and
the bake's packet in [open items](open-items.md#bake-proportional-to-change)
for the bake's jobs.

### 5. Convert scene light to display values exactly once

**The problem.** Tones are wrong across the whole picture. Scene light is
linear: twice the value is twice the light. A display expects values
encoded on the sRGB curve. The step that turns one into the other does
not exist on any path a world's view takes to the display. An SDR display
shows linear light as if it were encoded, and an HDR display decodes it a
second time. The exposure meter aims a scene's average at about a tenth
of white, and without the encode that shows as about 1% of white. The
debug world compensates by hand with two stops of exposure, which
corrects one point on the curve.

**What we propose.** Shade in linear light everywhere, and convert once,
as the last stage of each view's own graph, where no consumer can bypass
it. Every image declares which of three things it holds: scene light, a
window's partly exposed light, or an image encoded for the display. A
graph that joins two kinds without the conversion does not compose. An
authored colour becomes one of two types, a scene colour or a display
colour, and only a scene colour can enter shading. The debug world and
the standard package are retuned in the same change, because every place
is tuned against the missing encode today.

**Why.** What a pixel value means is correctness, not look. Every later
judgement of exposure, tone, bloom and materials depends on it.

**What it costs or risks.** Every place's look moves at once. A single
full-screen view gains a pass it did not have. A light's range is defined
as a share of display white, so ranges lengthen unless that is restated,
and longer ranges mean more relighting.

**What we still have to find out.** Nothing blocks the fix. The work
builds two instruments first: an authored grey card with luminance
patches, and a readback of the presented image in the display's own
format, since HDR output has never been observed.

**Against the budget.** One more pass for a single view, inside the image
row's 2.0 ms. The lane reports its cost at 2560 by 1440.

**Evidence.** Checked: the display encode's branches, the placement
pass's comment that no encode follows it, the hex colour's comment, the
debug world's compensation, and the three paths a view takes. Not
checked: the 1% figure is arithmetic.

**Owning plan.** [Rendering](rendering.md), with
[the terrain pass](terrain-pass.md#order-of-work), which already expects
the grey card to read correctly.

### 6. Give every receiver the same shadow answer

**The problem.** The shadow a surface receives depends on which shadow
map happens to answer and on what kind of view asks. Softness jumps about
seven-fold at the boundary between the near and far maps, because the
filter is sized in texels. Past the shadow maps everything reads as
sunlit. Glass and nested views get no shadow maps. And the wrong light
holds the maps: the ranking looks at a light's mode before its
brightness, so by day a moon that emits nothing holds a map, and at night
the unlit key light holds the near and far ones.

**What we propose.**

1. *Give the shadow maps to lights by role.* A light that emits nothing
   is given nothing. The brightest directional light takes the near and
   far maps, and a change of role fades as a change of slot does.
2. *Where no shadow map contains a point, use the lighting probes' record
   of whether that light reaches it.* Nested views and glass become the
   same rule with no maps. A band blends from the last map into the
   probes' answer. A probe that has not been measured yet keeps today's
   answer, which is lit, and fades to the measured one. It never answers
   dark.
3. *Size the shadow filter in metres*, from the light's angular size,
   with wide samples read from a reduced copy of the map. A prototype
   behind a switch is compared with a brute-force filter first.
4. *Centre the maps on the eye and author them as one set of radii*, with
   far entries held between refreshes.

**Why.** Roles give a whole map back by day. The probe answer stops
distant surfaces reading as sunlit. A filter in metres removes the
seven-fold jump.

**What it costs or risks.** Past the far map only the coarsest probes
answer, and they are 32 m apart. The valley's far view, which the art
direction leans on, would be shadowed in 32 m steps until stage 4's far
maps exist. So the fallback is judged by presented captures from the
valley's rim, and it is kept there only if it reads better than today's
sunlit answer. Nothing outside Puck shows a metre-sized filter over a
depth pyramid in a moving scene, which is why the prototype comes first.

**What we still have to find out.** Which light holds which map at noon,
by measurement. Whether the filter matches the brute-force reference.

**Against the budget.** The shadow row, from 3.9 ms toward 2.5 ms. Roles
remove one map's draws by day. The probe lookup adds work for each pixel
past the maps, reported with the change.

**Evidence.** Checked: the cascade test with no blending, the texel
clamp, the fully lit return, the allocator's ordering, and the moon's
and key light's authoring. Not checked: which light holds which map by
day and night is reasoned from that code, and the probe spacing is a
reviewer's report.

**Owning plans.** [Illumination](illumination.md) for roles and the
fallback, and [transmission](transmission.md#implementation-steps) for
the filter, which rewrites the same function.

### 7. Make a lighting change cost what changed

**The problem.** Any change to the scene relights the whole probe volume
from scratch. The stored light is reused only while two numbers for the
whole scene match, and one of them moves whenever a mesh swaps its level
of detail because the camera moved. The sky map is also rendered three
ways, two of them on the CPU.

**What we propose.** A geometry change costs a trace. A change of a
light's direction costs a relight. A change of brightness or colour costs
a multiplication, where the light still reaches the same places.

1. Fix the round trips first (decision 4).
2. Render the sky once, on the GPU.
3. *Store light for each group of sources and combine when publishing.* A
   group is a set of sources that scale together: each shadow-casting
   directional light, the sky, each local light, every emitting material,
   and each screen or opening. A change of brightness or colour is then
   exact, not an estimate.
4. *Light emitting surfaces as areas, not points.* The hall's ceiling
   strips light its floor in blotches. This shares the store with step 3,
   so it is built beside it.
5. While a full relight works through the grid, scale the probes it has
   not reached by the change in the sun's and sky's brightness.
6. Light each surface once and share it between the probes that hit it,
   and store which lights reach each surface as geometry.
7. Trace against a mesh the camera never changes.
8. One lighting budget and one pool of probes across all worlds shown.

**Why.** At fixed geometry the light a surface receives is linear in a
source's brightness, so a change is a multiplication and the stale night
metering goes away.

**What it costs or risks.** Nothing we read stores light for each source
in a dynamic probe volume, so its check is Puck's own: after an edit the
stored light must equal a relight from scratch, in seven named cases. It
costs memory for each group for each probe. A local light's brightness
does not qualify today, because its range follows its brightness.

**What we still have to find out.** The memory cost of light stored for
each group. How many probe hits share a surface. Whether floors are good
enough to trace against.

**Against the budget.** The probe row: one pool of 0.6 GB on the RTX
4070 in place of a volume for every world shown. Storage for each group
is priced against it before it is built, and narrows to the directional
lights and the sky if it does not fit.

**Evidence.** Checked: the reuse condition, how the scheduler's predicted
cost differs from what the kernel tests, the range formula, and the sky's
three renderers. A frame holds at most 128 lights (`SdfLights.MaxLights`),
so research on many lights is owed only before stage 4. Not checked: the
memory figures are arithmetic, and the nearest shipped relative
(Assassin's Creed Shadows) is a reviewer's reading of the deck.

**Owning plan.** [Illumination](illumination.md).

### 8. Choose detail by one error budget, along one chain of meshes

**The problem.** Two rules decide how detailed a mesh is drawn, and they
disagree. A shape's tier is chosen by half a pixel at the nearest point
that asks. A level is chosen by one pixel of error, measured a different
way. As a result the finest mesh is almost never drawn and still holds
about 1.1 GB. One demanding copy sets the detail of every copy: one fern
beside the player sets the detail of every fern in the world. And nothing
cheap stands in for a distant object.

**What we propose.** One error budget, in pixels for a view and in texels
for a shadow map. One function that projects an error to the screen. One
chain of meshes for each shape, from fine to coarse, in which every mesh
has a recorded deviation from the authored field. Each copy and each
shadow map draws the coarsest mesh in the chain whose recorded deviation
fits its budget. The floor is the first mesh added to the chain.

**Why.** Three constants become one budget. Far copies and far shadow
casters stop drawing the nearest copy's detail. The debug world's
coarsest tier has about a twentieth of its finest tier's triangles. Puck
can check every mesh in the chain against the field, so no stand-in mesh
is authored by hand.

**What it costs or risks.** Measuring every mesh costs bake time. If
that adds more to the cold bake than the chain saves in draws, the chain
stops at the floor. The measure samples, so a feature thinner than its
sampling can be missed. A shape with such features is recorded as not
qualified and keeps its finer mesh. The measure must run over the
authored shape's bounds: one that starts from the mesh cannot see a part
that vanished.

**What we still have to find out.** How many triangles each level and
tier draws, which no counter shows. Whether a change of tier for one copy
shows as popping on thin parts.

**Against the budget.** The mesh rows: 1.5 ms of drawing and 3 GB of
meshes on the RTX 4070. The chain is how the RTX 2060's 1.5 GB is meant
to be reached.

**Evidence.** Checked: the three constants, the two projection formulas,
the prototype baked at the finest demand of any copy, and the existing
measure against the field. Not checked: the 1.1 GB is arithmetic, and the
twentieth is one reading.

**Owning plans.** [Virtual geometry](virtual-geometry.md) and
[bake detail bounded by demand](bake-demand-tiers.md).

### 9. Give memory back, and never show a hole

**The problem.** GPU memory for meshes only grows, up to about 4 GiB. A
mesh that is not in memory leaves a hole in the picture. Relieving
pressure means loading a coarser mesh beside the fine one, which costs
more memory first.

**What we propose.** Follow the streaming plan in its own order: the
floor, then eviction, then keeping what was observed in use, on a memory
backing that can return memory to the system. This round adds four
things. Decision 8's counters come first, because they size a floor.
Requests for detail run ahead along the path the camera can take. What
shows before a floor exists is stated: until a new place's floors and
lighting stand, the frame keeps the last complete scene or the window's
authored fallback, and never publishes a scene with holes. And eviction
is judged in physical bytes, because deleting a mesh's rows frees nothing
while a reader still holds its block.

**Why.** The owner has ruled the outcome: a shape that has not landed or
has been evicted "shows its coarsest level ... never a hole".

**What it costs or risks.** A floor can contour a thin wall away to
nothing. An implementation that never frees anything could pass a check
by always naming something in the way, so the check requires one observed
release with a matching fall in bytes held.

**What we still have to find out.** Decision 8's counts.

**Against the budget.** The mesh store: from about 3 GB and growing to 3
GB held on the RTX 4070, 1.5 GB on the RTX 2060 and 2 GB on the Steam
Deck. Without eviction no device's memory bar can be met.

**Evidence.** Not checked: the five facts in the problem come from the
research pass's map of the streaming area. Checked: the owner's ruling in
the residency register.

**Owning plan.** [Streaming and memory](streaming-and-memory.md).

### 10. Make a bake cost what changed, and close the contour defects

**The problem.** Three things. A change to the bake's own code makes
every stored mesh a miss, because one identity covers the whole store, so
a developer who edits the simplifier waits a minute. The whole bake is
just over its figure of about a minute, and uses more than 10 GB of
private memory. And some shapes bake wrongly: six of fourteen blend
exhibits fail the mesh audit, and a grass blade bakes to about 474
triangles where its prism has a dozen.

**What we propose.**

1. *Count first whether each failing cell was ever flagged for finer
   sampling.* The bake already samples a flagged cell again. If the
   failing cells were never flagged, the defect is a trigger that never
   fires and the fix is small. This starts now.
2. *Key each stage's product by what that stage read.* The bake samples
   the field on the GPU, contours the samples, and then simplifies and
   stores. Keyed apart, an edit to the simplifier starts from stored
   contours and samples nothing again. These are local cache keys and
   stay computed from the code.
3. *A contour rule for a cell that cannot resolve its faces*, inside the
   existing refinement, not as a second path.
4. *Remove triangles without changing the surface:* drop collinear
   outline vertices, and convert a part exactly where its program proves
   a closed form.
5. *Then whichever speed fix a measured bake ranks first:* the GPU
   sampling in one round trip for each stage, or the simplify-and-store
   stage on the thread pool.
6. *One store that both backends read.* A product made by the other
   backend is accepted by checking it against the field, never by equal
   bytes. This is the first use of
   [the shared product's name](#how-a-shared-product-is-named-and-trusted)
   and waits for the owner's confirmation.

**Why.** The wait between a change and seeing it costs the most, because
it breaks a developer's flow. Keying by stage attacks that wait with the
principle the store already rests on.

**What it costs or risks.** Stored contours cost disk and memory. A key
that is too narrow is the dangerous direction: an edit that should have
invalidated a mesh and did not shows as a stale mesh nothing reports. So
each stage's key includes everything that stage's code reaches. A contour
fix reaches every bake, so it is judged by an audit of every stored mesh
in the world, not by fourteen exhibits.

**What we still have to find out.** What limits the cold bake. How large
each stage's product is. What a cold bake costs on a weak device: if it
does not fit the Steam Deck's memory, fetching products is how that
device plays at all, and [the fourth and fifth bets](#the-bets) stop
being optional.

**Against the budget.** The bake's memory row, and the cold bake's figure
of about a minute, read at the close of every stage so it cannot drift.

**Evidence.** Checked: what the store's identity hashes, that the code
identity keeps its parts apart, and that the store names the part that
changed on a miss. Not checked: the 10 GB comes from the research pass's
map, the stage shares are single readings, and the triangle counts are
arithmetic.

**Owning plans.** The bake's packets in open items
([bake proportional to change](open-items.md#bake-proportional-to-change),
[bake detail](open-items.md#bake-detail)), and
[visibility](visibility.md) for the removed triangles.

### 11. Keep one instance table, and test it once for every pass

**The problem.** The cost of submitting draws follows how many objects
exist, not how many a pass shows. Every candidate costs a draw command,
drawn or not. Only the far shadow map is culled by which surfaces will
read it. At the lobby the shadow maps take 3.9 ms against 1.7 ms for the
main mesh pass. A skinned draw has no bounding box, so it is never culled
and always draws its full mesh.

**What we propose.** One table of instances with stable identities, one
kernel that classifies it for every pass, and a command list equal to
what survives. In the visibility plan's order:

1. Far placements update less often, and only instances that changed are
   uploaded.
2. The command list is compacted, in order, to the draws that survive.
3. Shadow casters are culled, in every shadow map, by which surfaces will
   read them. The mask must cover every texel a reader could touch for as
   long as the map is shown, or else that map culls nothing.
4. Bounding boxes for skinned draws.
5. Commands grouped by geometry and level. The table is designed from the
   start to take rows from several worlds, so that merged worlds need no
   pass of their own ([the third bet](#the-bets)).
6. Indexed draws, as an experiment measured pass by pass.

**Why.** At one stop, culling the far map's casters by reader took a full
refresh from 23,125 draws to 4,138 and from 8.5 ms to 2.9 ms. The near
map is culled by box only.

**What it costs or risks.** A mask that misses a reader removes a shadow.
A bounding box that is not conservative hides a limb. A mask step can
look successful and save nothing while every empty command is still
walked, so the mask is judged by the shadow that results and the command
count is judged at the compaction step.

**What we still have to find out.** What an empty command costs. What
uploads about 2,600 draw records again each frame when nothing moves,
which may be a defect cheaper to fix than any step here.

**Against the budget.** The shadow row and the mesh row. This is the
largest planned cut to GPU time, and it is a cut by a fraction: it does
not by itself bring a device with half the throughput to its bar.

**Evidence.** Checked: one command for each candidate, the far map's
mask, and the missing box for skinned draws. Not checked: the timings
are single readings at one stop.

**Owning plans.** [Visibility](visibility.md) and
[virtual geometry](virtual-geometry.md).

### 12. Keep motion and placement as authored programs at zone scale

**The problem.** A zone of an MMO holds orders of magnitude more plants,
moving parts and terrain than the debug world's meadow. An authored swing
is interpreted from the document every frame. A gust's phase is a literal
number for each blade, so every tile is its own prototype and tiles
cannot share a bake. A scatter is evaluated for the whole world at once,
under a ceiling of 32,768 colliders. Terrain bakes at 2.8 m cells with
relief capped at 4 m, and nothing casts a shadow past 120 m.

**What we propose.** The engine speeds up the authored program and
derives its bounds. It writes no grass, wind or terrain path in C#.

1. *One compiled form for a motion program*, from which come a CPU
   evaluator, a GPU kernel, and the routine that derives the motion's
   bounds.
2. *Placement evaluated cell by cell, by one function, for collision and
   drawing alike.* The cell belongs to the placement, not to the
   renderer, because the server has no renderer and must collide against
   the same population that is drawn. Each placed thing gets an identity
   from what its placement means, so two ways of writing one scatter give
   the same identities. What play changes (a plant harvested) is
   simulation state keyed by that identity.
3. *Terrain as tiles of one relief series, with a far shadow map*, after
   a terrain research round.

**Why.** "Puck code over engine code" keeps behaviour in authored
programs, and "Ergonomic is optimal" means a scatter costs what it means,
not how it was written.

**What it costs or risks.** Almost no terrain source was read. The
terrain decides whether the valley reads as a valley, so its research
round starts now and does not wait for stage 4. A plant that was
harvested must stay harvested when its cell is evicted and derived again,
which is why play's changes live in the simulation.

**What we still have to find out.** How closely CPU and GPU motion
agree. Everything about terrain seams and scale.

**Against the budget.** Produce and the tick at zone scale. Nothing here
is claimed against today's debug world.

**Evidence.** Checked: the compiled placement list that drawing and
collision both read, the collider ceiling, and that the server does not
reference the renderer. Not checked: the facts about swings, gusts and
terrain come from the research pass's map of the foliage area.

**Owning plans.** [Dense landscapes](dense-landscapes.md),
[the terrain pass](terrain-pass.md), and [visibility](visibility.md) for
motion on the GPU.

### 13. Shade through one path, and shrink the image history

**The problem.** Surface shading exists twice: authored shading programs,
which are the intended path, and older fixed layers, which the default
frame still uses. Shading is the largest pass. And the history that
antialiasing keeps is 188 bytes for every pixel, in one copy for each
frame in flight.

**What we propose.**

1. *Split the history.* Forty of its forty-seven words are a window of
   the last eight frames. That window moves to one buffer updated in
   place where a pixel did not move, and only the words that follow
   motion stay in a copy for each frame.
2. *One shading path*, after decision 5 has fixed the colour conversion.
   The ways of running a shading program are measured against the fixed
   layers on the same device, and the fixed layers are then deleted.
3. Measure the per-pixel probe lookup on its own, which is about a third
   of the shading pass, then evaluate it once for each view at its own
   rate.

**Why.** The owner's ruling on the bar's size makes the history the
largest single store after the meshes: about 2.1 GB at 2560 by 1440. The
split brings it to about 0.9 GB. One shading path removes a second
implementation.

**What it costs or risks.** A buffer updated in place cannot serve a
pixel whose history comes from somewhere else, because another pixel may
be writing there in the same pass. So the window is carried in place only
where the two positions are shown to be equal. No source covers this,
and it is designed and reviewed before it is built.

**What we still have to find out.** How often a pixel's history comes
from its own position on the moving route. What a player sees when a
shading program arrives that has no compiled kernel yet.

**Against the budget.** The history row, from about 2.1 GB to 0.9 GB on
the RTX 4070, and the shading row's 4.5 ms.

**Evidence.** Checked: the switch's default, and the history's words for
each pixel. Not checked: the 2.1 GB is arithmetic from that layout, and
the shading pass's 3.7 ms is one reading at a smaller window.

**Owning plan.** [Author shaders](author-shaders.md).

### 14. Treat a nested view as the same view at another size

**The problem.** A view through a window or on a screen costs more than
it should and looks different from the same scene seen directly. Two
identical engine constants turn shadows and ambient occlusion off for
every nested view at any size, so a portal that fills the screen is lit
differently from the room behind it. A sliver of window can cost a full
image. One pane of glass costs a clear of the whole image.

**What we propose.**

1. *Replace the two constants with an authored table.* The engine's
   default restricts nothing. The standard package authors what a nested
   view gives up at each size on screen, for each device. This starts
   now.
2. *A view that nothing reads costs nothing.* A world's demand is its
   consumers' minimums first and its pixels on screen second (with
   decision 2).
3. *Fit each window's image to the part of its glass that shows.*
4. *Make glass cost what the visible glass covers.*
5. *A result reused between viewers is keyed by the content of what each
   was delivered*, and checked again when a picture is bound to a reader
   and when a finished job installs its result. Two viewers delivered the
   same rows share. A viewer delivered less does not.

**Why.** The table removes a visible pop in lighting at every portal
crossing, and a look built into the engine twice.

**What it costs or risks.** Restoring shadows to nested views adds GPU
work on every device, which is why what a view gives up is authored for
each device. Crossing a threshold in the table rebuilds the view's graph,
which can be refused when memory is full.

**What we still have to find out.** Why a still camera feed keeps
rendering today. The cause of an unexplained device loss on Vulkan near
the glass passes.

**Against the budget.** The glass and nested row's 2.0 ms.

**Evidence.** Checked: the two constants, the window's sizing rule, and
the glass clear. Not checked: that glass shades the whole grid comes from
the research pass's map.

**Owning plans.** [Streaming and memory](streaming-and-memory.md) for
demand, [transmission](transmission.md) for glass, and
[water](water.md) for a camera inside water.

### 15. Present frames the display has room for, and know when they showed

**The problem.** The engine cannot say when a frame reached the display.
On Vulkan a frame can be rendered and then thrown away, because the
presenter asks for room after the frame is built, with no wait. An
emulated machine's video is shown at the simulation's rate and not the
machine's. The interface overlay costs pixels times records.

**What we propose.**

1. *One record for every attempt to present:* whether it was submitted,
   whether it was shown, and the time from input to display.
2. *One admission rule on both backends:* the host gets a place in the
   present queue before it renders, and renders no frame that has none.
3. Machine frames shown at the machine's rate, through a short queue of
   complete frames.
4. The overlay as clipped geometry with text from an atlas.
5. Display timing on Linux, when a Steam Deck is on hand.

**Why.** An even loop interval can hide judder on the display. The frame
rate players see is unexplained today.

**What it costs or risks.** A true display time on Vulkan needs an
extension a device may not have. Its absence is reported as a capability.
A device with no display time can only be judged on the frames the host
produced, which does not show the bar was met on screen.

**What we still have to find out.** Which timing extensions each device's
driver offers. Whether machine frames are dropped on a running World.

**Against the budget.** No row directly. The record is what reads the
whole-frame row honestly, and the overlay claims its 0.9 ms back.

**Evidence.** Checked: the zero timeout on acquiring an image, the
overlay shader's first line, and the video source's declared rate. Not
checked: that machine frames are dropped is read from code and not seen
on a running World.

**Owning plans.** [The benchmark](benchmark.md) for the record,
[the UI overhaul](ui.md) for the overlay, and
[machines and cartridges](machines-and-cartridges.md) for the machine's
rate.

## The build order

The order is by dependency. A stage is a group of items, not a gate: an
item waits only for what it names. Every stage's full checks are in
[the step designs](rendering-research/step-designs.md#the-checks-for-each-stage).

### How every check is read

- **The bar.** The slowest frame's work, on the benchmark's route, at the
  device's authored quality table and the size the owner ruled for it,
  with the render scale native and dynamic resolution off. It is read on
  Direct3D 12 and on Vulkan, with the authority in process and with it
  remote. Where the device reports display outcomes, no present is
  skipped or dropped.
- **A change.** Each landing is compared with the build it started from,
  both frozen and run in the same quiet session. One slowest frame is not
  enough either way, so a check reads the slowest frame with its cause,
  how many frames missed and by how much, and the counted work, over
  repeated runs.
- **A claim that can fail.** Each check states the gain its change claims
  against the budget table and the result that would show the claim
  false.
- **Sharing.** Every world of the debug world admits every viewer in
  full, so a check run there shares as much as can be shared. A check
  that claims work is shared between viewers is also read with one window
  onto a world that gives its viewer less.

### Engine gaps, filled first

An engine gap is a limit in the engine that stops a place or a creation
reaching the look it is meant to have. The owner has ruled that a gap a
lane, a critic or a review names "is filled as soon as possible": it goes
to the front of its owning plan's order, ahead of further work that would
sculpt or tune around it, and is never only filed
([AGENTS.md](../../AGENTS.md#pucks-purpose)). So each gap below starts as
soon as it can, and a look lane that meets a listed gap stops and waits
for it.

They are ranked by how many places and exhibits each one blocks. The
counts come from the visual-bar, water and terrain packets in open items,
the modeling plan's list of limits, and the look critic's report on the
rebuilt lake and glacier.

| Gap | What it blocks | Its cause in the code | The exhibit that proves it | Owning plan | Against stage 0 |
|---|---|---|---|---|---|
| 1. Large terrain bakes coarser than it declares, with limits sized for props | All eight wall sectors of the valley, the glacier's trough and moraines, and any stone placed in a wall | A creation whose parts carve each other, or that carries relief, is emitted as one instance for the whole creation (`WorldPlacementStamper`), so a sector's bound, not its declared spacing, sets its cell: about 2.8 m where it declares 1.56 m. Relief is capped at 4 m (`CreationNoiseDocument.MaxAmplitude`) and a smooth blend at 0.5 m (`ShapeDocument.MaxSmooth`) | The scoured trough floor and the moraines stair-step, seen from the valley floor (`worlds/debug/glacier-site.puck`) | [The terrain pass](terrain-pass.md#order-of-work), its bound fix and relative caps | Starts now. It needs one count first: the triangles a tighter bound costs |
| 2. Terrain has no rock treatment: no rule by slope, altitude or strata, and the default frame does not run authored shading programs | The same eight sectors and the rim: one olive colour, smooth shading over flat colour | Authored programs sit behind a switch the code calls temporary and leaves off (`WorldRenderSettings.ShadePrograms`), so the frame shades with the fixed layers, which have no slope or altitude rule | The north-west wall from the valley floor, against the direction's layered strata | [The terrain pass](terrain-pass.md#slope-altitude-and-curvature-materials), with [author shaders](author-shaders.md) | Starts after the colour conversion, which starts now. Its measurement of shading on the bar devices is taken on the RTX 4070 alone |
| 3. Nothing casts a shadow past 120 m, and nothing stands in for distant geometry | Every view of the valley from a distance: the rim, the far walls, the ridges | The shadow maps split at 16 m and 120 m, and a point outside them is returned fully lit (decision 6). No coarse mesh exists for a distant shape (decisions 8 and 9) | `valley-rim`: far walls with no ridge shadows | [The terrain pass](terrain-pass.md#order-of-work) for the far shadow map, [streaming and memory](streaming-and-memory.md) for the floor | The floor is the first step of the streaming plan's order and starts now. The far shadow map starts once shadow roles land, which starts now |
| 4. Relief noise cannot be masked or flattened | Anything set into terrain: the lake's bowl, the pool, the river's channel, the glacier's cradle. The noise moves the authored ground by metres and buries them | A relief has a frequency, an amplitude, octaves, a gain, a lacunarity and a seed (`CreationNoiseDocument`), and no mask or region | The lake's bowl in the north-west wall | [The terrain pass](terrain-pass.md#one-relief-three-consumers), designed into its one relief op | Starts now |
| 5. Water's colour and opacity do not follow depth over the bed | The lake, the pool, the river and the plunge: one flat colour, no clear shallows, stones invisible | The water's medium is a function of the path length along the view ray to the first opaque surface (`sdfGlassThicknessAt`), with one scatter colour and one coefficient for the material. Nothing gives shading the water's depth over its bed | The lake over its bowl and stones | [Water](water.md) | Starts now |
| 6. A flow layer carries one velocity | The lake's outlet sill, the river's winding course and the falls | A palette's flow layer has one `Velocity` for the whole surface (`PaletteFlowDocument`) | The lake's sill, where the water should speed up and nowhere else | [Water](water.md) | Starts now |
| 7. A feature thinner than the bake's cell is lost or faceted | Six of the fourteen blend exhibits, the Moth's shin rims, the swallow's streamers, and every grass blade | A tip thinner than about one and a half cells stops short, and a cell that cannot resolve its faces is contoured wrongly (decision 10) | The blend exhibits' mesh audit, and the swallow's streamers | [Bake detail](open-items.md#bake-detail) | Starts now, with the count of flagged cells. The contour rule follows the count, and builds its own check first: an audit of every stored mesh |
| 8. A cutter that moves against the surface it cuts keeps its rest cut, and a creation with one takes no relief | The glacier: it wastes back only by scaling about its head (`sizeBy`), which the look critic rejected; its relief works only because nothing in it moves apart. Also a door carved into rock, and erosion | A cut across motion owners splits the group and each part borrows the cutter at its authored pose (`CreationMotionPlan.Borrows`), and relief refuses any independently moving shape (`CreationMotionPlan`, PUCK030) | The glacier retreating by a cut up its trough | [Moving blends](moving-blends.md#gap-8-a-cutter-that-moves-against-the-surface-it-cuts), designed | Starts now, with step G1 |
| 9. A camera inside water sees a flat wash | The four water places, whenever the player swims | The glass pass has no case for an eye inside a medium (decision 14) | A dive in the pool | [Water](water.md) | Starts now |
| 10. A paint does not follow a blended seam between parts that move | The Moth and the swallow | Material coordinates across a blended seam are not blended | A painted marking on a rigged head | [Moving blends](moving-blends.md), its step 4 | Starts now. The two creations themselves wait for the owner's pair session |
| 11. A primitive under a non-uniform scale gives a bound, not a distance | Any stretched cutter, the lake's bowl among them: it frays the surface it cuts | The scaled primitive answers a bound on distance | A stretched ellipsoid cut | [Rendering](rendering.md#bake-quality). The design is decided | Starts now |
| 12. A frame holds at most 128 lights | No place today | `SdfLights.MaxLights` | None yet | [Illumination](illumination.md) | It blocks nothing today, so it keeps its place: research on many lights before stage 4, or as soon as a place authors more lights than the cap |

Three more gaps block every place, and already stand at the head of the
order below: the missing colour conversion, the shadow maps held by an
unlit light, and nested views with no shadows.

Checked for this table: the relief's fields and its cap, the smooth cap,
the switch and its default, the flow layer's one velocity, the glass
thickness the medium reads, and the light count. Gap 8's cause was read in
the code and reproduced at compile time by its design
([moving blends](moving-blends.md#what-the-gap-is-in-the-code)). Not
checked: the 2.8 m and 1.56 m cells and the 120 m split are the terrain
plan's readings, and the thin-tip limit and the unblended paint are the
modeling plan's. No other gap was reproduced for this page.

### Work that starts now

None of these waits for a measurement or for another item, and they run
beside the gaps above. The first matters most: until it lands, every
slowest-frame reading in stage 0 is a garbage-collection pause.

| Item | Its first step | It worked when | Owning plan |
|---|---|---|---|
| Stop allocating every frame, and remove the collection pauses | Attribute the allocation by source with a GC trace of the steady loop | No collection pauses the steady loop on the route | [Job system](engine-job-system.md) |
| The withdrawn picture (a defect in today's code) | Reproduce both cases: a viewer narrowed to a private view, and a viewer revoked while another keeps the world alive | The first frame delivered after the change shows nothing only the full view could show | [The security packet](open-items.md#security-review) |
| The colour conversion (decision 5) | Author the grey card and patches, and read back the presented image in the display's format | The card reads 118 of 255 through every path a view takes | [Rendering](rendering.md) |
| Shadow maps by role, with the probes' answer past them (decision 6) | Read which light holds which map at noon | At noon the second light's map draws nothing, and an exchange of sun and moon shows no frame without shadow maps | [Illumination](illumination.md) |
| What a nested view gives up, as an authored table (decision 14) | Delete both constants and read the table | A lit exhibit through a full-size portal matches the direct view, and neither constant remains | [Streaming and memory](streaming-and-memory.md) |
| Why the failing bake cells fail (decision 10) | For each failing shape, count whether its cell was ever flagged for finer sampling | The report says, for each, flagged or not | [Bake detail](open-items.md#bake-detail) |
| A quality table and a memory bar for each device | The mechanism that selects a table by device, and the memory account read against a bar for each device | Selecting each device's table on the lead machine changes its counted work to that device's counts | [Streaming and memory](streaming-and-memory.md) |
| The benchmark reads the bar as ruled | Run the route at 2560 by 1440, and once against a remote authority | The report states the size, the backend, the topology and the conditions of each run | [The benchmark](benchmark.md) |
| The first bet's memo | List what `SdfWorldResidency.Prepare` reads and the client record that covers each input | Every input has a named record or is listed as a gap | [Runtime and delivery](runtime-and-delivery.md#the-presentation-view) |
| Terrain research | A research round on seams between tiles, scale, and far shadows | The terrain pass's plan holds its findings | [The terrain pass](terrain-pass.md) |

### Stage 0: measure

**What lands.** The readings of decision 1, on the RTX 4070 at 2560 by
1440. The benchmark's route already has named chapters and a second route
that plays a portal crossing at three paces. It gains the places later
checks name that it lacks, and a way to reach night.

**It is done row by row.** A row is done when its reading is in the
benchmark's report. A step waits only for the rows it names. The stage
has no single finish line, and stage 1 does not wait for all of it.

**What it adds to the budget table.** The measured column at the bar's
size, on both backends and in both topologies, and the counted work for
each pass.

### Stage 1: fix what the player sees everywhere

**What lands.** Beside the work that started early: the budget that
follows the GPU time the frame leaves free, bake jobs cut to it, and
several lighting jobs at once (decision 4); the bake's keys by stage, its
contour rule and its removed triangles (decision 10); and the prototype
of the shadow filter sized in metres (decision 6).

**It needs.** Stage 0's GPU time for each frame, for the budget rule. An
audit of every stored mesh, for the contour rule.

**It is done when** the lighting's age holds its bound after a switch to
night without the main view slowing; an edit to the simplifier rebakes
with no field sampling; no class of contour defect is worse over the
whole world; and every chapter of the route meets its reference in
motion, judged by someone other than the lane that tuned it.

### Stage 2: the frame thread and the round trips

**What lands.**

- Decision 2's capture order, upkeep and minimums.
- The bounded waits, then the thread pool, then the shorter tick
  (decision 3).
- Uploads only for instances that changed, the compacted command list,
  shadow casters culled by reader in every map, and boxes for skinned
  draws (decision 11).
- The lighting's work list on the GPU (decision 4), one sky, light stored
  for each group of sources, and emitting surfaces lit as areas
  (decision 7).
- The split image history (decision 13), moved here from the last stage
  because of what it costs at the bar's size.
- The bake's first speed fix (decision 10).
- The first, second and fifth bets' prototypes.

**It needs.** Stage 0's breakdowns of produce and the tick. Stage 1's
budget rule, before anything raises the background work's share.

**It is done when** no residency is captured that nothing read; a console
line is answered within one loop iteration while a tick's work is
overdue; commands executed equal the draws that survive; a change of only
a light's colour relights nothing and matches a relight from scratch; and
the slowest frame of every chapter is compared with the stage's starting
build.

### Stage 3: one instance table and one chain of meshes

**What lands.** Commands grouped by geometry and level, and the instance
rows read once for each world (decision 11). The chain of meshes
(decision 8). The streaming plan's eviction and backing, behind the floor
that started as an engine gap (decision 9). Shared surface lighting, and a trace mesh the camera never
changes (decision 7). One shading path (decision 13). The third and
fourth bets, the fourth only after the owner confirms the shared name.

**It needs.** Stage 2's compaction.

**It is done when** walking up to one tree changes no distant tree's
triangles; a burst of cold arrivals shows no frame with a missing
surface; memory given back is observed as a fall in physical bytes; and
the RTX 4070's budget table is read again.

### Stage 4: scale

**What lands.** Motion and placement at zone scale, and terrain tiles
(decision 12). The rest of the transmission plan (decision 6). The rest of the nested-view work (decision 14). One
lighting pool across worlds (decision 7). The present queue's admission
rule, the overlay and the machine's frame rate (decision 15). The sixth
and seventh bets.

**It is done when** the slowest frame of the route holds 16.7 ms on the
RTX 4070 at 2560 by 1440, in both topologies, inside the memory bar.

**Then the other devices.** Testing on the RTX 2060, the Steam Deck and
an HDR display starts when the RTX 4070 meets both bars. By then each
device has its authored table, its memory bar and its budget in counted
work, so the first session on a device is a comparison against targets
and not a first look.

## Who decides what

Most choices on this page are settled by the code, a measurement or a
recorded ruling, and need no call. Looks belong to authors. A short list
belongs to the owner.

### Looks are authored

A look value is an authored parameter with a neutral engine default
([AGENTS.md](../../AGENTS.md#puck-is-pre-release), "Authors rule"). The
standard package chooses the values it wants and any world overrides
them. The owner corrects a look and is never asked to choose one. No step
of the build order waits for a look.

This plan touches these looks, and each has or gets an authored home: the
tone curve, exposure and its change between places, a light's softness,
the widest soft shadow edge, how far shadows reach and how far a distant
shadow may lag, the night, the indirect light's allowances, the quality
tables for each device, what a nested view gives up, the detail budgets,
how ground cover thins with distance, and how much of its own exposure a
window's world applies. [The step designs](rendering-research/step-designs.md#where-each-look-is-authored)
name the authored setting for each and say which exist today.

One default is not neutral. The detail budgets (one pixel of error for a
level, one texel for a shadow map) are the owner's recorded rulings. They
stay the engine's defaults, and an authored table may differ.

### Questions for the owner

Each is about what the engine does. Each has a default the work proceeds
on, so none blocks a step.

1. **How a shared product is named and trusted.** The design is
   [above](#how-a-shared-product-is-named-and-trusted). It changes what
   the register says a mesh's identity is, for products that leave the
   machine that made them. *Until confirmed:* local cache keys only, and
   nothing is fetched.
2. **The memory bar's figures.** The Steam Deck's is the streaming plan's
   10 GB for the process and the GPU together. This page proposes 8 GB of
   GPU memory on the RTX 4070 and 4.5 GB on the RTX 2060, which leaves
   room on each card for the rest of the desktop. *Until confirmed:* the
   work is held to those figures.
3. **Whether a device's quality table may lower the render scale.** The
   register sets the bar at a native render scale. A device's table can
   already choose its shadow tier, its probe tier, its detail budgets and
   what nested views give up. *Until the owner says otherwise:* no table
   lowers the render scale.
4. **Whether pictures are published, never rendered for each observer.**
   A world may show a viewer only pictures. We propose the world's host
   renders each picture once for a declared camera and audience
   ([three ways worlds compose](#three-ways-worlds-compose)). The other
   choice is a server that renders for every observer. *Until the owner
   says otherwise:* published pictures only.

Four things are not questions. The size the bar is read at, the two
topologies and the order of devices are ruled. A tick on its own
thread stays ruled out, and returns only if decision 3's measurement
shows the tick and produce each fit a frame alone. Sharpening stays ahead
of bloom, as the register rules. And the hardware is not needed until the
RTX 4070 meets its bars.

## What would change the plan

The results most likely to change it, and what would happen then. The
full list is in [the step designs](rendering-research/step-designs.md#what-would-change-the-answer).

- **At 2560 by 1440 the GPU alone is far over 16.7 ms.** Shading and the
  shadow maps move ahead of the frame-thread work: the probe lookup at
  its own rate (decision 13), and casters culled by reader (decision 11).
- **The breakdown of produce names a part no decision covers.** Decision
  2's order follows the breakdown, not this page.
- **The tick's breakdown shows the planned splits save little.** The
  tick's serial fixes come first.
- **Light stored for each source costs more memory than the probe row
  allows.** It narrows to the directional lights and the sky.
- **Measuring every mesh in the chain costs too much bake time.** The
  chain stops at the floor.
- **A cold bake does not fit a weak device.** Fetched products become the
  way that device plays, and the fourth and fifth bets move up.

## Where the rest is

[The step designs](rendering-research/step-designs.md) hold, for each
decision, the steps with their order, the options that were not taken,
and the reasoning in full. They also hold the table of missing
measurements, the checks for each stage, the properties the order rests
on and what enforces each, the outside techniques that were considered
and not taken, the research still owed, and the sources.
