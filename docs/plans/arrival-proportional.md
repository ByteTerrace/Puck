# Arrival work proportional to change

The design memo for three costs an arrival still pays whole: the frame that
takes a hand-over restages every draw row, the lighting's trace scene rebuilds
its top level and its GPU export on every scene change, and a store load decodes
into managed arrays before its native copy. Each decision names its options, the
evidence, the failure modes each option closes, the favoured option, the
strongest case against it, the invariant and the one place that enforces it. It
is the design of [the visibility plan](visibility.md#v2-instance-updates-proportional-to-change-and-authored-motion-on-the-gpu)'s
"retained instance updates" (its V2b) and says what its record split (V1b) owes
the hand-over. A review from another family has read it. Each finding is
answered in the design itself, and [the review answers](#review-answers) say
where; the steps are ready for build lanes.

Labels: **[opened]** means this memo read the symbol at this tree;
**[reading]** means a count the frame-bar lane measured, which this memo did not
measure again and whose values are in the commit that lands it;
**[inference]** means this memo's reasoning; **[unconfirmed]** means nobody has
checked it. A plan's citations are leads: recheck the symbol before building
from one. A name called a working name is this memo's, for a type, verb or
count that does not exist yet.

## Summary

- **Rows keep their place.** The dresser's draw list becomes a row table: each
  owner (a bake group, the host's list, an instance's own mesh, a portal clip's
  copies) holds a run of rows from a range allocator, a free row is an absent
  record, and the staged table is the producer's table row for row. A hand-over
  that changes a group's piece or copy count frees and allocates that group's
  run and nothing else moves.
- **One writer, one cursor.** Creating a row table hands its write operations
  to its creator as an object nobody else can obtain. The frame carries a table
  and a journal with no write member, and every consumer reads changes through
  one cursor type. The compiler holds the door.
- **The change is derived, never reported.** The dresser already finds what a
  frame changed by comparing each group's shown mesh against what it dressed,
  not by trusting the bake's product revision. That scan stays the one
  derivation. A portal clip is derived inside the table from each row's own
  frame slot, so a host draw and a rigid copy are clipped, and unclipped, like
  any other row.
- **A row's history follows its occupancy.** A row freed and given again, or
  returning from absence, starts a new generation whatever it draws. The
  cross-check verifies that with a ledger of its own, never through the history
  it checks.
- **A hit is resolved through one door that rejects.** Every pass that reads a
  record or a triangle named by GPU data asks one resolver, which bounds the
  draw by the table's extent and the triangle by words the record holds, treats
  a failure as a miss and counts it.
- **A hand-over rewrites its pieces, not its copies,** once the record splits
  into a per-piece part and a per-instance part (the visibility plan's V1b).
  [R0's rule](#r0-the-counts-and-the-rule) decides whether that moves ahead.
- **The trace scene stops allocating first, then its top level becomes
  clusters.** Step S1 pools what a scene build keeps and exports the prefix
  without a second host copy; step S2 partitions the top level into spatial
  cells whose hierarchies are identified by their contents, as each geometry's
  already is, and is taken only if R0's rule sends it.
- **The store decode writes into the storage the mesh keeps,** through one
  decoder with a storage parameter, and a restore reads only the planes it
  restores.

The order is R0 (the counts and the rule that reads them), R1h (the hit door),
R1a (the staged table is the producer's), R1b (rows), R1c (clips), then R2 and
S2 as R0's rule sends them. S1 and D1 (the garbage) share no file with the R
steps and run beside them.

## What exists

### The draw list and its staging

`SdfMeshDresser.Dress` returns the frame with a dense list **[opened]**: the
host's own draws, then each finished bake group's draws in the bake's group
order (each copy's pieces, or the group's pieces in place), then each instance
that carries its own mesh, then the carried copies `SdfSlotClip.Apply` appends
for a body straddling a portal. A draw's place in that list is its row
everywhere below.

The dresser keeps the list across frames and rebuilds it whole
(`SdfMeshDresser.BuildDrawTopology`) when any of these holds **[opened]**: the
last publication did not complete; the host's draw count changed; the bake's
copy layout revision moved; a group finished that the last dressing did not
hold; a group's mesh was replaced by one with another piece count or another
copy count; or the count of finished groups changed. A replacement that keeps
both counts takes the other path (`SdfMeshDresser.Replace`), which rewrites that
group's run in place and names only its rows in the draw journal
(`SdfFrame.MeshDrawChanges`, a `GpuChangedRows`).

A rebuild journals "everything". `SdfWorldTables.TryStageDrawChanges` then
refuses the journal, and `SdfWorldTables.StageMeshRegionCore` plans the arena
over the whole list (`SdfMeshRegionArena.Plan`, counted by
`SdfMeshHistory.StagingPlanRecords`), walks the claims of every draw
(`SdfMeshClaims.Update`), and, where the count changed, packs every record
(`SdfWorldTables.StageMeshMotion`) **[opened]**. A record is
`SdfMeshRecord.DrawWords` words, so the upload carries the whole record
reserve. The frame-bar lane counted the meadow's whole list planned again on
each rebuild of an arrival window **[reading]**.

Two more paths restage the list whole, and neither is counted by
`SdfMeshHistory.DressTopologyBuilds` **[opened]**:

- **A draw waiting for its mesh is filtered out of the staged list**
  (`SdfWorldTables.StageMeshRegionCore`, the landing filter), so staged rows
  are compacted and differ from the producer's rows. Every landing while any
  draw waits changes the filtered count and packs every staged record again,
  and `SdfWorldTables.TryStageDrawChanges` refuses the journal for as long as
  any mesh is owed. A replacement lands ahead of the draws and never filters;
  a group's first mesh does.
- **A body straddling a portal** makes every frame journal "everything"
  (the dresser's portal path), so each such frame plans and compares the whole
  list ([frame rendering](../rendering/sdf/handbook/frame-rendering.md#what-a-frame-uploads)
  says so: "Active or removed portal clipping currently publishes full draw
  changes").

The portal path is also what keeps clipping right today **[opened]**.
`SdfSlotClip.Apply` clips every row whose `SdfMeshDraw.FrameSlot` lies in a
clip's slot run, whoever owns the row: a group drawn in place, a host draw that
carries a slot, and a rigid copy's draws, which take the copy's slot and whose
clip `SdfCompositionFrameSource.ComposeSlotClips` adds for that slot. The
dresser's reverse index (`SdfMeshDresser.IndexBindings`) lists by slot only the
bindings drawn in place; a copy's bindings are listed by copy, and a host draw
by nothing. So only the portal path's visit of every host draw
(`SdfMeshDresser.CollectHostDraws`) and every binding puts back a host row or a
copy row once its clip ends or moves.

A new program makes the dresser visit every binding, since a kept group can be
read under another slot **[opened]**. And these walk every row or copy on
ordinary frames **[opened]**: `SdfMeshPathBake.SameCopyLayout`, the dresser's
copy comparison and its input commit, the dresser's claims on any frame that
changes a mesh (`SdfMeshClaims.Update`), the residency's glass count, the
tables' own glass count, and `SdfWorldTables.GlassMaySee`'s index refresh on
every mesh revision.

### Who holds a row index

| Holder | What it keeps by row | What a shifted row costs it today |
|---|---|---|
| `SdfWorldTables` | The staged draw, its packed record, the arena's name for the row (`SdfMeshRegionArena.Rename`), the glass rows | Packed again |
| `SdfMeshHistory` | A generation per cell, moved whenever the cell's occupant, mesh or slot changes | A new generation for every shifted row |
| The previous-matrix table | Each row's last matrix, seeded from the current one on a new generation | Seeded: the row reads as still |
| Each raster pass's `SdfMeshOcclusion` | Visibility, level and generation per row, on the GPU; the kernel resets a row whose generation differs | Every shifted row leaves the remembered visible set: the early phase draws none of them that a box bounds, so the depth they are tested against holds only the rows that did not shift |
| The temporal resolve | A pixel's receiver identity, whose source is the row (`sdfHistoryReceiverMatches`) | History rejected under every shifted row |
| `SdfDressedPickMap` and `SdfWorldPicker` | Identity by row; a pick's copy is read back frames later and resolved through the map, which assumes the host's rows lead the list | The map is compared row by row, and rebuilt when an identity differs |
| `SdfMeshIrradiance` | A hash and a static flag per row (`SdfMeshIrradiance.StaticKey`), read through the draw journal by row; the key folds the rows in order | Folded whole when the journal refuses, and a new key whenever a row moves, which rebuilds the scene |
| `SdfMeshIrradiance.DiagnosticScene` | The static draws in order | No match, so no diagnostic scene |
| `SdfMeshIrradianceScene` | Nothing by row: its instances are its own list | Rebuilt (decision 3) |

No CPU readback carries a row index besides a pick **[opened]**: the other
readback buffers `Puck.SdfVm` creates are the background work's, portal sight's
and the exposure meter's.

So a shifted row costs more than its record **[inference]**: the frame after a
rebuild draws with little of its occlusion history and resolves without its
temporal history under those rows, which no count in `sdf.instances` shows
beyond `SdfMeshHistory.Seeded`.

### Every writer

The rest-epoch cache in the tick was deleted after missing four writes
([hazards](../development/hazards.md#an-invariant-kept-by-convention)), so the
writers are listed, with what tells the dresser about each today **[opened]**:

| What a row's content depends on | Its writers | How the change is seen |
|---|---|---|
| A group's shown mesh | The bake's product setter and its hand-over, which move the product revision at eight places | Not by that revision: `SdfMeshDresser.Dress` compares each group's shown mesh by reference each frame |
| A group's copies | The frame's instances | `SdfMeshPathBake.SameCopyLayout` compares the layout each frame and moves one revision |
| The program's groups | A program rebuild (an edit, a responding placement) | The bake keeps the group object of every group the new program keeps, and any other differs by reference; the dresser visits every binding |
| A slot's or a copy's pose | The frame source | `SdfMovedTransforms` through its commit, and a reference compare per copy; unknown history visits everything |
| The host's draws | Each scene emitter | The host's journal; unknown history visits everything |
| Portal clips | The frame's `SdfFrame.SlotClips` | Nothing: the portal path visits and journals everything |
| The list itself | Six places in the dresser assign, append or strip draws: three add the row to the journal by hand, the rebuild journals everything, and the clip's append and strip add nothing | The journal, or "everything" |
| Whether a row is drawn | A mesh landing, a refusal, a trim, a relocation (`SdfWorldTables`) | The landing filter, recomputed over the whole list |

Three things follow. The derivation of what changed is already a comparison
against the inputs for meshes, copies and groups, which cannot miss a writer.
The journal is kept by convention: a write and its journal entry are separate
statements at each site, and one writer has none. And the journal's reading is
kept by convention too: the frame publishes the producer's own journal object,
whose `GpuChangedRows.Begin` and `GpuChangedRows.Owe` are public, the dressed
list is a `List` a holder can downcast, and each consumer wrote its own "collect
since my serial, or refresh" guard (`SdfWorldTables.TryStageDrawChanges`,
`SdfMeshIrradiance.StaticKey`, `SdfMeshDresser.CollectHostDraws`, which alone
checks that the runs fit the table, and for the transform journal
`SdfMeshDresser.CollectSlotBindings`, `SdfWorldTables.PrepareMeshTransformCuts`
and `SdfWorldTables.PackDynamicTransforms`) **[opened]**.

### The trace scene and its export

`SdfMeshIrradianceScene.Build` reuses each geometry's hierarchy from the scene
it replaces and builds the rest new **[opened]**: a list of every static
instance sized to the whole draw list, the top level's entries and their
bounds, a binned hierarchy over them, and the claims of every static draw.
`SdfMeshIrradiance.StartSceneBuild` first copies the frame's whole draw list on
the frame thread and claims every mesh it names, and `SdfMeshIrradiance.ChangedBoxes`
counts both lists into a dictionary. `SdfMeshIrradianceScene.CreateGpuExport`
keeps each geometry's words where the arena placed them and packs the whole
prefix (top nodes, entries and one record per instance) into a new array that
the upload sends whole **[opened]**. One door publishes
(`SdfMeshProbeGpuWork.TryTake`): no query is admitted while a replacement
uploads, the root is written last, and an abandoned upload frees only its
prefix.

[The graphics plan](graphics-architecture.md#probe-lighting) sized the top
level as "hundreds of draws, trivial". The scene's static draws at the meadow
are not counted in any reading this memo has **[unconfirmed]**; the allocation
trace named the scene's constructor and the export as the two largest
allocators of an arrival **[reading]**.

### The store decode

`SdfMeshStore.TryRead` reads the entry into a managed array, and
`SdfMeshStoreCodec.TryDecode` inflates it into native scratch and then
`SdfMeshStoreCodec.ReadMesh` decodes every plane into a new managed array
**[opened]**. `SdfMeshLibrary.TryLoad` renumbers into more managed arrays where
the group's ids differ (`SdfMeshLibrary.RenumberManaged`) and copies the result
into native storage (`SdfMesh.NativeCopy`), after which the managed arrays are
garbage. A restore of a published mesh's normals and coarser levels
(`SdfMesh.Hold`, and the owed set's needs) reads and decodes the whole entry
the same way and copies those two parts.

## Failure modes

Each decision is read against these.

| | Failure |
|---|---|
| F1 | A writer changes a row and nothing journals it |
| F2 | A consumer skips frames, the journal's history expires, or the producer or the tables are replaced |
| F3 | A row index is held across the row's reuse: a GPU history, a readback in flight, a CPU map |
| F4 | A pass resolves a hit through a record written for another occupant, and reads past a buffer |
| F5 | A record names a mesh whose words do not stand |
| F6 | A buffer is replaced or grown while a submission reads it |
| F7 | A publication fails partway, with retained rows already rewritten |
| F8 | Free rows accumulate, and every pass and every per-row buffer pays for them |
| F9 | The result depends on the order things arrived in, so it differs from a rebuild's or between two Worlds |
| F10 | A conservative fallback fires every frame and brings the whole-list cost back |
| F11 | A reader traverses a structure being changed, or an abandoned upload leaves a published root over words it no longer describes |
| F12 | A cached part is reused for contents it was not built from |
| F13 | A row returns from absence, or is freed and given again, and keeps the history of what it held before |
| F14 | A check reads the state it is checking, so the two agree whatever the state is |

## Decision 1: rows that keep their place

**What must be decided.** How a draw list keeps its rows when an owner's row
count changes, and what makes the incremental table equal the rebuilt one.

**Options.**

| Option | Evidence | Cost | Fails at | Closes |
|---|---|---|---|---|
| 1a. Keep the dense ordered list; pool what a rebuild allocates | The rebuild's allocations are the pick map, the key arrays and new bindings **[inference]** | Small | Every count change still packs, uploads and re-seeds every later row | None |
| 1b. A free list of single rows | The open item's lead; persistent instance ids in other engines ([sources](#sources)) | Medium | A group's rows scatter, so its changes exceed `GpuRegion.MaxCopyRuns` runs and the journal coarsens toward the whole table | F8 with a compaction rule |
| 1c. A run per owner from `GpuRangeAllocator`, free rows absent, the staged table equal to the producer's | The arena already places meshes this way, and a hand-over that keeps its counts already stays in its run | Medium to large | Row order differs from a rebuild's (F9) | F1 to F8, F10, F13 |
| 1d. A row's place derived from its owner's identity (a history-independent hash table) | Puck-native: an address identified from its inputs, so the incremental table equals the rebuilt one byte for byte, order included | An extent about twice the live rows in every pass and per-row buffer; no contiguous runs | Memory on the Deck; coarsened journals as in 1b | F9 outright |
| 1e. Stable records under a dense order table | Keeps canonical draw order with stable records | A second table that still shifts whole; a read per lane | Solves only depth ties, which are an authoring defect | F9 |

**Favoured: 1c.** It is the arena's discipline applied to rows with the
arena's own allocator, it keeps a group's rows contiguous so a hand-over is
one or two journal runs, and it removes the two uncounted whole-list paths with
the same structure.

**Strongest case against.** Row order becomes a function of arrival history.
Draw order is observable only where two exposed faces coincide exactly, which
the rendering skill and the visibility plan already call an authoring defect,
and the two-phase occlusion already lets such a tie follow the prior visible
set. The answer is under [packing](#packing-and-the-canonical-order): the
invariant is stated as a set, and a frame that must repeat across Worlds is
drawn from the canonical order.

### The row table and its one writer

**The table.** A new type (working name `SdfMeshRows`, in `Puck.SdfVm` beside
the dresser and the tables) owns the rows: each row's base draw as its owner
wrote it, the draw staged from it, its liveness, its occupancy, its owner and
its place in the owner's run, the range allocator and the journal. Its storage
is private.

**The writer.** Creating a table returns the table and its writer together,
once. The writer (working name: the table's `Writer`, a nested type with no
public constructor) offers the only writes there are: allocate a run, free a
run, set a row's base draw, set the frame's clips, and start over. Each
journals the rows it touches inside itself. `SdfMeshDresser` creates the table
and keeps the writer; the dressed frame carries the table, which has no write
member. Code that holds a frame therefore cannot write a row, and code that
creates a table of its own (a law, a tool) can write only that one. No
`InternalsVisibleTo` is needed, since the writer is nested in the table and the
dresser is its creator.

**The frame carries a table, not a list.** `SdfFrame.MeshDraws` stays what the
host supplies, with the type it has. The dressed rows are a new member holding
the table, whose reads are an indexed read that answers a row's staged draw or
"absent", an enumeration of live rows with their indices, and changes through a
cursor. No consumer indexes the dressed rows as a list, and there is no list to
downcast.

**The frame carries a reader, not the journal.** `GpuChangedRows.Begin` and
`GpuChangedRows.Owe` stay with the producer. The frame members that carry a
draw journal (`SdfFrame.MeshDrawChanges` out of the dresser, and the host's
own into it) carry a reader instead (working name `GpuChangedRowsReader`): one
object per journal, so a consumer still tells two producers apart by
reference, with the serial, the row count and nothing that writes.

**One cursor.** One type beside `GpuChangedRows` (working name
`GpuChangedRowsCursor`) replaces the hand-written guards. It holds the reader
it consumed from, the serial it consumed through, and the table's revision and
row count. Its one question takes the frame's reader, serial, revision and row
count and answers with runs: the rows changed since, bounded to the table, or
one run covering the whole table when the reader is another one, the history
expired, the frame owes everything or a run does not fit. A consumer has no
separate refusal to handle, so it cannot handle it wrongly, and it commits the
cursor only once it has consumed the frame. `GpuChangedRows.TryCollect` is
reachable only through the cursor. A guard one consumer needs (the fit check
only the dresser has today) is then every consumer's. The transform journal
(`SdfMovedTransforms`) hands its consumers the same reader, so its three
consumers hold the same cursor, with the whole-table answer given for its
reseats as well.

**Where it stops.** Reflection, and the table's and the journal's own code.
The host's input list and its journal remain the host's to write, and the
transform journal's write side (the frame source's commit) is not changed.

### A run per owner

A bake group holds one run of its copies times its pieces; the host's list,
the instances' own meshes and each clip's copies hold runs of their own. A run
is keyed by its owner's object, and the bake keeps the group object of every
group a new program keeps, so such a group keeps its run and its bindings. An
owner whose row count changes allocates a new run, writes it, and frees the
old one; the allocator's lowest-first fit keeps the extent tight. An owner
holds its run with the ticket the allocator gave it
(`GpuRangeAllocator.TicketOf`) and frees it by that ticket, so an owner holding
a run that was freed and given again cannot free its successor's.

Every allocation stamps its rows with an **occupancy**: a number from a counter
of the table's own that never issues one twice, and that a start over does not
reset. A row's occupancy travels with the row to every consumer,
and is zero for a free row.

**The reconcile is the derivation.** Each frame, under the bake's frame gate as
today, the dresser compares each group's shown mesh, piece count and copies
against its run. The bake's copy-layout revision gates the per-group copy
comparison, so an unchanged layout costs what it costs today. Pose changes
arrive through the transform journal and host changes through the host's, each
read through a cursor, whose whole-table answer is their "visit everything".

### Clips are derived in the door

A clip is not a write to a row. The table keeps each row's base draw, and the
draw it stages is the base draw clipped by the first of the frame's clips whose
slot run holds the row's `SdfMeshDraw.FrameSlot`, which is the rule
`SdfSlotClip.Apply` applies today, or the base draw itself when none does. One
function inside the table computes the staged draw, and no writer supplies it.
There is no "restore" step to forget: a row whose clip ended stages its base
draw because that is what the function returns for it.

The table finds a clip's rows through a slot index of its own: a chain of rows
per frame slot, linked inside the allocate, set and free operations from each
base draw's own slot. The key is the row's slot, never its owner's kind, so a
host draw that carries a slot and a rigid copy's draws are on a chain exactly
as a group drawn in place is. The dresser's binding index stays for poses and
is not what clips read.

The writer's clip operation takes the frame's `SdfFrame.SlotClips` and compares
them, by value and in order, with the list the table holds. An unchanged list
costs that comparison. A clip that started, ended, changed or changed its place
in the list walks the chains of its slots, the ones it held and the ones it
holds, stages those rows again and journals them; staging a row asks again
which clip is first for its slot, so overlapping runs resolve as they do today.
A set of a clipped row's base draw, as when its body moves, stages the row and
its carried copy inside that set.

Each clipped row has one carried copy. A clip owns a run of copy rows, one per
row it clips, allocated when it starts and freed when it ends. A copy row
records the row it copies and is staged from that row's base draw by the same
function, with the copy identities `SdfSlotClip.Apply` gives a carried copy
today. When the rows a clip clips change in number (a group's run is
reallocated, a row's slot changes), the clip's run is reallocated as any
owner's is when its count changes.

R1c takes `SdfSlotClip.Apply` and the dresser's portal path out of the frame.
The whole-list rebuild, with `SdfSlotClip.Apply` as its clip step, stays as the
definition the derivation must meet: only the cross-check and the law call it.

### The staged table is the producer's table

A row whose mesh is owed or refused is staged as an absent record at its own
row, not filtered out, and a free row is the same record.

**One staging function, two branches.** The tables write a record through one
function (today's `SdfWorldTables.StageMeshDraw`, widened), and no other code
writes one:

- **Live**: the row is live in the producer's table and
  `SdfWorldTables.MeshStands` answers for its mesh. The function observes the
  row (`SdfMeshHistory.Observe`, given the row's occupancy) and packs its
  record.
- **Absent**: the row is free, or its mesh is owed or refused. The function
  retires the row's history cell (a per-row retire in place of
  `SdfMeshHistory.RetireAfter`'s suffix) and writes the absent record: the
  absent bit, a zero generation, and zeros in every placement word. It does not
  observe.

A row that returns from absence finds a retired cell, so its observation seeds:
a new generation and a previous matrix seeded from the current one, whatever it
draws. That covers a relocation, which owes every mesh and so sends every row
absent and back with the occupant it had, a store restore, and a refusal that
later lands (F13). Today the landing filter shifts the staged indices, so those
rows seed by accident.

**Rows by mesh.** The tables keep, per mesh, the rows that name it (an
intrusive chain of the kind the dresser keeps per slot and the table keeps per
frame slot; one shared type beside `GpuChangedRows`), so a landing stages
exactly those rows. `SdfWorldTables.DrawsAwaitMeshes` becomes "a live row is
staged absent", which is what a lockstep frame's wait and `world.wait
residency` read.

### The arena is named by row

`SdfMeshRegionArena.Plan` does four things beyond counting names **[opened]**:
it places a mesh no row named before (`SdfMeshRegionArena.TryPlace`, after the
unnamed meshes give up their words), refuses one no free run holds
(`SdfMeshRegionArena.Refused`), grows the draw reserve
(`SdfMeshRegionArena.GrowReserve`, which moves the meshes lying in the words the
reserve takes), and applies the slack and packing rules.
`SdfMeshRegionArena.Rename` does none of them: it requires a placement. The
row operations carry all four:

| Operation | The arena | The tables then |
|---|---|---|
| A row names a mesh (a row gained, or renamed) | Counts the name. A mesh it does not hold takes the lowest free run, the unnamed meshes giving up their words first, or is refused | Placed: the mesh is owed and lands through the background work, its chain absent until it stands. Refused: its chain is absent. Held and standing: the row is staged live |
| A row stops naming a mesh (a row lost) | Uncounts it. A mesh no row names is retained slack | Nothing more |
| The extent passes the reserve | Grows the reserve. Each named mesh in the words it takes is placed again (`SdfMeshRegionArena.Moved`) or refused | A moved mesh that is resident is written in this frame's upload (`SdfWorldTables.WriteMeshWords`), and every row on its chain is staged again in the same staging call. One still restoring from the store, or refused, has its chain staged absent |
| The end of a staging | Applies the slack and packing rules from running totals of placed, named and free words, which the operations above keep | A relocation owes every mesh, so every chain is staged absent |

A row staged through a mesh's chain takes its placement from the arena, never
from the placement words of the record it replaces
(`SdfMeshRegion.ReadPlacement` is a shortcut only for a row whose mesh and
placement stand). That keeps today's rule that a moved mesh is written together
with the records naming it: the mesh's words and its chain's records are owed
by one staging call, so no row is left naming words the reserve took.

`SdfMeshRegionArena.Plan` becomes these operations applied to a whole table,
for the first plan, a relocation and the cross-check, so placement has one
implementation. A refused mesh is named again when a trim
(`SdfWorldTables.ServiceMemoryTrim`) or a forgotten mesh frees words.
`SdfMeshHistory.StagingPlanRecords` then counts names, and equals the rows
gained and renamed.

### Absent is one bit in one kernel

The record's selection flags (`SdfMeshRecord.SelectionWord`) gain an absent
bit; the selection kernel, the one classification door of every raster pass,
rejects it before reading anything else and resets the row's state. The absent
record's placement words are zero, so [the hit door](#how-the-gpu-side-stays-correct)
rejects every triangle of it by the test it applies to every record. The staged
draw count becomes the table's extent.

### Counts the door keeps

The walks over every row are replaced by counts kept where rows are written:

- **Claims.** The writer moves a claim with each write: a claim for a row
  gained, a release for a row lost, `SdfMeshClaims.Move` for a set that changes
  the mesh. The dresser's `SdfMeshClaims.Update` over the whole list goes.
- **Glass.** Glass is counted three ways today: the residency's loop over the
  frame's draws, the tables' count, and `SdfWorldTables.GlassMaySee`'s index,
  rebuilt on every mesh revision. The table counts its live glass rows in its
  writes, which is the frame's count. The tables' staging function keeps one
  set of glass rows (in when a row is staged live and glass, out when it is
  staged absent or not glass), which is the staged count and what
  `SdfWorldTables.GlassMaySee` iterates. An absent row is in neither.
- **Whole walks are counted by the table.** The table's live enumeration adds
  the rows it yields to one work kind (working name
  `sdf.instances.rows.walked-whole`), so a consumer that walks every row shows
  in `sdf.instances` without having to count itself. R1b's check holds that
  kind at zero on a hand-over frame.

The copy comparisons (`SdfMeshPathBake.SameCopyLayout`, the dresser's copy
comparison and input commit) walk the frame's instances, not rows, and are the
derivation; [the refactoring packet](open-items.md#refactoring-campaign) holds
them.

### Packing and the canonical order

**A pack is a start over.** The writer's start over frees every run through
the table and clears the owners' runs, bindings and clips and the claims; the
dresser then reconciles from the inputs as a first dress does, in a fixed
order, journaling everything. Allocating in that order from an empty table
lays the rows out in the canonical order: the host's run, each finished group's
run in the bake's order, the instances' own meshes, then each clip's copies in
the order of the rows they copy. That is the order of today's rebuild, at
today's rebuild's cost. Every row takes a new occupancy.

**Free rows are bounded.** When free rows inside the extent pass a share of it
(a policy constant, as `SdfMeshRegionArena.PackedShare` is for mesh words), the
dresser starts the table over (F8).

**The table knows whether it is canonical.** A start over sets a flag once its
reconcile completes; any later allocation or free clears it; a set leaves it.
On a frame that must repeat across Worlds (`SdfFrame.TickLocked`, which is
every frame under lockstep) the dresser starts over when the flag is clear and
does nothing when it is set. A locked frame whose owners kept their counts
therefore packs nothing, and one that changed a count pays what today's rebuild
pays, so lockstep costs no more than it does now and its order is the
rebuild's.

**The budgeted move is a different operation.** If `host.frames` shows the
packing frames, a compaction that moves the highest run into the lowest hole a
frame is added beside the start over. It clears the canonical flag and never
runs on a locked frame. It is not built first.

### A failed publication starts over

A dress that throws leaves its pending flag set, as `SdfMeshDresser.Dress` does
today. The next dress starts the table over, the same operation a pack uses.
Because a start over frees through the table and not through the owners'
tickets, a run allocated before the throw that no owner remembers is freed with
the rest (F7, F8). Occupancies keep counting across a start over, so every row
is a new occupant to the history and none adopts what a row held before the
failure.

### The invariant, its doors and where it stops

**The invariant.**

1. At the end of every `SdfMeshDresser.Dress` that publishes, the live rows as
   a multiset equal the list a rebuild from the same inputs produces, clips
   applied, compared by `SdfMeshDraw.SameStagedContent`.
2. After every staging, each row's record is the packing of its staged draw at
   its mesh's current placement where the row is live and its mesh stands, and
   the absent record otherwise.
3. An absent row's generation word is zero. A live row's generation differs
   from the one it last carried while live exactly when one of these happened
   to it since: its occupancy changed, it returned from absence, its history
   identity changed (motion key, mesh, slot, flags, clip or carry), or a
   transform cut reseated it. Every such row is seeded in the upload that
   carries its record.

**What enforces it.**

| Guarantee | Enforced by | Stops at |
|---|---|---|
| A row changes only with its journal entry (F1) | The writer is the only object with a write, and only the table's creator has it | The table's own code |
| A clipped row, whoever owns it, stages its clip and loses it (clause 1) | The staged draw is computed in the table from the base draw and the clips; the slot index is kept inside the writes | The same |
| A consumer sees every change or the whole table (F2) | The cursor is the only way to collect, and it answers with runs in both cases | A consumer that commits a cursor without consuming |
| A record is its row's packing (clause 2) | The tables' one staging function, with its two branches | The tables' own code |
| A generation follows occupancy and absence (clause 3, F13) | `SdfMeshHistory.Observe` compares the occupancy, and the absent branch retires the cell | The history identity fields and a reseat still reach it as arguments |
| What changed | Derived by comparison for meshes, copies and groups | Poses and host draws arrive through writer-fed journals |
| A failed publication (F7) | The start over | Nothing remembered across it |

**Where it stops.** Poses and host draws still arrive through writer-fed
journals with their own doors; this design does not change them, and the
cross-check below covers them. Draw order is not part of the invariant. The
kernels' side of the hit door is held by a function signature, which HLSL
cannot make private; that gap is stated under the door.

### How the GPU side stays correct

Reusing a row replaces no GPU resource, so it retires nothing. What it creates
is a new interpretation of a row index, and each holder of one is covered:

- **Records, and the one queue they rest on.** The mesh region is the
  always-staged region: one device-local buffer that every upload copies into,
  a frame's or a landing job's, on one queue in order. A pass already submitted
  never sees a record staged after it. That holds only while every writer of
  the region submits on the queue the frames submit on. Background jobs do
  (`SdfGpuWork` submits through the device's `GpuDeviceServices.QueueSubmitter`),
  and a landing job's region copy also carries draw records a frame staged
  since its upload, because the staged region keeps one owed list
  **[opened]**. With rows reused, a second queue would let a freed row's
  record be rewritten while a submitted frame still reads it. The precondition
  is checked where a landing's copy is recorded
  (`SdfWorldTables.RecordLanding`): the recording states the queue its job is
  submitted on, and a queue other than the tables' own is refused by name.
  Moving background work to another queue then fails at its first landing, and
  would need a fence between a row's free and its reuse, which this design does
  not build.
- **Histories.** `SdfMeshHistory` never reuses a cell's generation, a retired
  cell keeps its count, and the kernel resets a row whose generation differs
  from its remembered one; a new occupant's previous matrix is seeded. The
  design adds the two events that today's observation cannot see (F3, F13).
  A row freed and given again to the same key and mesh has another occupancy,
  which the observation compares. A row returning from absence finds a retired
  cell. Neither depends on a caller remembering to retire: the occupancy
  differs whether or not the cell was retired, and absence is a branch of the
  one staging function. [The generation ledger](#what-the-cross-check-compares)
  checks the result from outside.
- **Hits in a target.** A pass resolves a hit through the current records, so
  a target must have been written under them. Every record write moves the
  mesh revision, which the geometry signature folds
  (`SdfWorldTables.UpdateTablesSignature`), so the mesh pass renders before any
  reader; under this design that increment sits in the tables' one staging
  function. The skip rule is unchanged (`SdfWorldPassRecorder.TargetsHoldClear`),
  and a table whose rows are all absent has a non-zero extent, so its pass runs
  and clears (F4). That argument is this design's guarantee, and it is counted
  rather than assumed: each pass that writes hits (the mesh pass, the glass
  raster) notes the tables' mesh revision it records under, or the current one
  when it skips over targets that hold a clear, and a pass that resolves those
  hits and records while the note is not the tables' current mesh revision
  counts a stale target (working name `sdf.instances.targets.stale`), on every
  run, not only under a lever. The count stays at zero. The hit door below
  bounds the read when the argument is broken from any cause; it cannot tell a
  wrong occupant that is in bounds, which is what the stale-target count is
  for.
- **The hit door.** The reads of a record or a triangle named by GPU data are
  not one function today **[opened]**: `sdfMeshSurfaceNormal` and
  `sdfMeshMaterial` (from the mesh target, the glass target and the glass
  sums' second record), `sdfGlassInterfaceAt`, `sdfPreviousPoint` (from the
  visibility record), and the draw the shading reads its direction through
  (`sdfShadeStamp`, `sdfShadeFlow`). Each indexes the region by a draw and a
  triangle it was handed, and the triangle-material read adds the triangle to
  an address with no bound. The design gives them one resolver (working name
  `sdfMeshResolveHit`) that takes a draw and a triangle and answers a resolved
  hit (working name `SdfMeshHit`: the record's address and the triangle's
  first index) or "none". It accepts a hit when the draw is below the pass
  block's draw count and three times the triangle plus two is below the
  difference of two words the record already holds
  (`SdfMeshRecord.LevelTableWord` less `SdfMeshRecord.IndexWord`, which is the
  count of the mesh's levelled indices and also bounds its triangle
  materials). An absent record's placement words are zero, so it rejects by
  the same test. A rejection is a miss, never a clamp to a triangle that is in
  bounds and wrong: the surface pass writes a miss, the glass pass shades no
  glass there, the reprojection answers no previous point. Each rejection is
  counted through the pass's work counters (working name
  `gpu.mesh-hits.rejected`). Every reader above takes a resolved hit in place
  of a draw and a triangle, so the compiler refuses a read that skipped the
  resolver; HLSL has no private constructor, so a kernel can still fill the
  struct by hand, and that gap is review's. The raster stages keep
  `sdfMeshRecord`, since the draw they index is the one the selection kernel
  just emitted. This turns the review-only class of
  [silent invariants](../development/hazards.md#silent-invariants), a GPU read
  indexed by GPU data, into a counted check: zero rejections across the tour
  on both backends, and a Vulkan run under GPU-assisted validation with the
  lever that forces rejections.
- **The temporal resolve.** A row that keeps its place keeps its history, which
  is the gain. A reused row's pixel can match history by identity only where a
  different surface also lies within the depth tolerance **[inference]**.
- **Readbacks.** A pick is delivered only when its slot is proven complete
  (`RenderGraphSlotResults`, `SdfWorldPicker.Complete`), and today it is
  discarded unless the map it was recorded under is still the frame's
  (`SdfDressedPickMap`, compared by reference). Under this design:
  - The table keeps each row's identity, its owner and place in the owner's
    run, and the table revision at which its identity last changed.
  - A pick is stamped with the table, by reference, and with the revision of
    the rows its hit was drawn from. A hit's row comes from the mesh pass's
    target, so the mesh pass notes, per view instance, the table and revision
    it records under, as `SdfWorldPasses.SubmittedFar` keeps a drawn layer's
    camera; a recording that is abandoned restores the note before it. The
    views pass, which prepares the pick (`SdfWorldPicker.Take`), stamps that
    note and not the frame's latest revision. A view whose mesh pass stood
    holds the revision it last rendered under, which can only be older, and an
    older stamp only discards more.
  - An answer is published only if the frame's table is the stamped table and
    the hit row's identity has not changed since the stamped revision.
    Otherwise it is discarded and recorded again, as an answer under a
    replaced map is today. A replaced producer, new tables after a lost device
    and a view that follows another residency each present another table
    object, which no revision can be mistaken for.
  - A row translates to the host's map through its owner and place: a host row
    to its index in the host's input list, which is its offset in the host's
    run wherever that run lies; a group's row to the group's first instance; a
    clip's copy row to the row it copies, then as that row. Nothing assumes the
    host's rows lead the table, and the search over the host's identities goes.
  - The host's own map (`SdfFrame.PickMap` as the host supplies it) is still
    compared by reference.
  No map is allocated or compared per change.
- **Landing.** A row leaves absent only when `SdfWorldTables.MeshStands`
  answers for its mesh, the landing invariant's one place (F5).
- **Growth.** A table outgrowing the draw reserve, the previous-matrix table
  and each pass's per-row buffers grow as they do now, each retiring what it
  replaced after the submissions that read it (`SdfWorldTables.Retire` over
  `GpuRetirementQueue`, `SdfMeshOcclusion.Completed`) (F6). The extent moves
  less often than today's count, which changes with every landing.

GPU fences and resource lifetimes are a review-only class
([hazards](../development/hazards.md#silent-invariants)); R1a and R1b get that
review.

## Decision 2: what a hand-over rewrites

**What must be decided.** A group whose mesh is replaced with its counts
standing rewrites every one of its rows, because each record holds the mesh's
placement, its level table and a box derived from its bounds. For a prototype
with many copies that is most of the table, on the journal path, with rows
already in place.

**Options.**

| Option | Evidence | Cost | Fails at |
|---|---|---|---|
| 2a. Leave it: a hand-over costs its copies | Decision 1 alone removes the count-changing case | None | A prototype with tens of thousands of copies changing tier |
| 2b. A per-piece row: the placement, level table, flags and local bounds of the mesh an owner's piece draws, which each instance record names | [The visibility plan](visibility.md#v1-retained-instances-ordered-compaction-and-geometry-level-buckets) already splits the record this way for its buckets | The record layout in every kernel that reads it; the box computed in the selection kernel, or kept per instance from bounds no tier changes | The box: computed per lane per pass, or loosened |
| 2c. Spread a large hand-over's rows over frames | No layout change | A group drawn from two tiers at once for some frames | Visible seams where tiers differ |

**Favoured: 2b, as part of V1b, pulled forward only on evidence.** Keyed by the
owner's piece and not by the mesh, a hand-over writes one piece row per piece.
The rows-by-mesh chain of decision 1 stays: under 2b its entries are the piece
rows that name a mesh, far fewer than the instance rows, and the absent bit
moves with the placement to the piece row, so a landing or a moved mesh stages
the mesh's piece rows through the same chain. **Strongest case against:** it
moves the box into the kernel, which is GPU work in every pass against CPU work
on a rare frame. [R0's rule](#r0-the-counts-and-the-rule) decides, from the
records hand-overs pack against the records the whole-list paths pack.

## Decision 3: the trace scene's top level

**What must be decided.** How a scene change costs what changed: on the workers
(garbage, which stops the frame thread at each collection), in the upload
(which shares `SdfGpuWork.UploadBytesPerSecond` with the meshes landing), and in
the wait before probes can trace the new scene.

**Options.**

| Option | Evidence | Cost | Fails at | Closes |
|---|---|---|---|---|
| 3a. Rebuild whole, allocate nothing that is not kept: pool the scene's arrays through its last claim, snapshot only static draws, export the prefix from the scene's arrays on demand | Geometry nodes are already exported without a host copy | Small | Work and upload still follow the scene's size | The garbage |
| 3b. Refit the top level | NVIDIA: "BLAS updates are a good choice after limited deformations"; for the top level, "perform only rebuilds" ([sources](#sources)) | Medium | A tier swap changes a geometry's braid set, which is an insert and a delete, not a moved bound; quality decays | None alone |
| 3c. Mutate one hierarchy in place (insert, remove, rotate) | Proportional | Large | F11: CPU readers hold claims across threads, and an abandoned upload must leave the published root intact | F11 only with copy-on-write, which is 3d |
| 3d. Spatial cells, each cell's hierarchy identified by its entry list and reused when the list is equal, under a small top over the cells | Puck-native: each geometry's hierarchy is already identified by its buffers and kept across scenes, on the CPU and in the GPU arena | Medium | Entries straddling cells loosen cell bounds | F11, F12 |
| 3e. Build the top level on the GPU each change | H-PLOC builds a hierarchy over a million instances in milliseconds ([the graphics plan](graphics-architecture.md#probe-lighting-research) opened it) | Large: a build kernel family on both backends | The CPU still traverses the scene for placement and diagnostics, so it would be a second builder | The upload |

**Favoured: 3a now, 3d if R0's rule sends it.** 3a removes what reaches
the frame thread. 3d makes the rest proportional without changing a kernel
**[inference]**:

- **A cell's cluster is a pure function of its entries.** Each top-level entry
  (one braid of one static draw) goes to the cell that holds its box's centre.
  A cluster holds its entries, a record per instance it names, and the
  hierarchy over them, and is built by one function from the entry list. A new
  scene partitions the static draws again, on its worker, sorts each cell's
  entries by their content (geometry, matrix, material and braid), and takes
  the previous scene's cluster for every cell whose sorted list is equal, entry
  for entry; only the others build. The sort makes the comparison independent
  of the rows' order, so a pack, which keeps the rows as a set and moves them,
  rebuilds no cluster. The scan stays the size of the static scene, over pooled
  workspace; the builds, the claims and the upload follow the changed cells.
- **A cluster is self-contained on the GPU.** Its block holds its nodes, its
  entries and its own copies of its instance records, placed in the scene's
  arena and kept across scenes exactly as a geometry's block is
  (`SdfMeshIrradianceScene.GpuArena`): written once, proven by its completed
  runs, freed when a published scene no longer names it. Nothing shared is
  overwritten in place, so an abandoned upload leaves the published root over
  words that still describe it (F11).
- **The prefix is the top over the cells.** Each of its leaves is written as a
  copy of that cluster's root node, whose children lie in the cluster's block.
  Every link is already an absolute address, so the trace kernels and the
  record layout do not change. The CPU walk gains one level through the
  existing leaf interface.
- **The changed boxes come from the difference.** The entries two unequal lists
  do not share are the boxes `SdfMeshIrradiance.ChangedBoxes` computes by
  counting both draw lists today.

**The scene's key is a set too.** `SdfMeshIrradiance.StaticKey` reads the rows
through the cursor and treats an absent row as not static. Its key becomes a
sum of each static row's own hash, which does not depend on order and is
updated from the rows the cursor names, so a pack leaves the key and the scene
standing and the fold is proportional to change. `SdfMeshIrradiance.DiagnosticScene`
matches its static draws as a set for the same reason. Both land with R1b,
where rows first move without changing.

**The invariant.** A cluster is reused only for an entry list equal to the one
it was built from; the type offers no mutation and one constructor, so a reused
cluster is the rebuilt one (F12). A cluster's block on the GPU also holds its
meshes' placement words, so the block is kept only while those placements
stand, checked per cluster as `SdfMeshIrradianceScene.MatchesGpuPlacements`
checks the whole scene today. The top's depth and a
cluster's depth together stay within the trace's stack bound, enforced where
the hierarchy build certifies its depth today.

**Strongest case against 3d.** It is built for a need nobody has measured: if
the prefix a scene change uploads is small, 3a's rebuild is a few milliseconds
on a worker and a small upload, and 3d is structure without a reading. That is
why S2 waits for R0's rule.

**3e waits.** When probe placement moves to the GPU trace
([the memory packet](open-items.md#memory-on-the-deck-tier)), the CPU scene
goes and a GPU builder stops being a second one.

## Decision 4: the store decode

**What must be decided.** Whether a store load and a restore should stop
producing managed arrays the native copy then discards.

**Options.**

| Option | Evidence | Cost | Fails at |
|---|---|---|---|
| 4a. Pool the decode's managed arrays | The arrays have one user | Small | The arrays' sizes vary per mesh, so a pool keeps the largest of each; the copy remains |
| 4b. One decoder that writes each plane into storage its caller supplies | The plane readers already read from native scratch through spans; the native mesh's blocks are spans | Medium | Nothing found |
| 4c. Map the entry and draw from it | No decode | The entry is delta-coded and compressed; a second on-disk form | The store's bytes on every machine |

**Favoured: 4b.** `SdfMeshStoreCodec.ReadMesh` takes a storage that hands out a
span per part: managed arrays for an offline library, blocks of the mesh's
native payload for a host. The mesh is built over those blocks, so the native
copy of a store load goes; the group's material ids are applied as the material
planes are read, so the managed renumbering goes; the entry's bytes are read
into owned scratch, as its inflated planes already are. A restore passes a
storage that accepts only normals and level indices and skips every other
plane by its stated length. A chunked mesh needs its pieces' counts before the
first plane to size the attributes its chunks share, which the entry's layout
gains.

**It pays.** The decode was the third allocator of the arrival's trace
**[reading]**, its larger arrays go to the large object heap **[inference]**,
and the whole of it is avoidable because its destination is native. It is also
the memory packet's restore lead.

**The invariant.** One decoder: the managed and native results come from the
same plane readers, so they cannot drift (the
[two copies](../development/hazards.md#two-copies-of-one-computation) hazard).
The check is against the truth, not the old path: an encoded mesh decodes to the
mesh that was encoded, for both storages.

## Determinism and the cross-check

All of this is presentation: no row, cluster or decoded array reaches
simulation state, and rule 4 asks only that the result be as correct as a
rebuild's. Rows are compared with a rebuild as a multiset, and a scene's hits
with a rebuilt scene's by distance, bit for bit, with the hit's identity
allowed to differ only where the other triangle lies at the same distance.

### What the cross-check compares

The cross-check is a lever (working name `world.rows check on`), off by
default, with its counts in a source of its own (working name
`sdf.rows.check`). Each comparison counts what it compared and what
mismatched, and names the first differing row and its owner.

| Comparison | Under test | Compared against | Lands with | Cannot see |
|---|---|---|---|---|
| Dress | The live rows, as a multiset by `SdfMeshDraw.SameStagedContent` | A rebuild from the same inputs into scratch, clips applied: today's topology build and clip step, kept as the definition | R1b; R1c adds clips | Where a row lies; generations |
| Pack | Every word of every row's record in the region's host copy, for every row of the table, staged this frame or not | The record `SdfMeshRegion.WriteDraw` packs into scratch from the row's staged draw as the producer's table holds it, the arena's placement and the generation read from the record under test; an absent row against the absent record | R1a | The generation, which it takes as an input; the device |
| Generation ledger | Whether each row's generation word moved since the staging before, for every row | A ledger the checker keeps itself: for each row, the occupancy and staged draw it last read from the producer's table and whether the row was absent (not live, or its mesh not standing), from which it computes whether the generation must have moved; a reseat the tables named for a row is the one cause it is told | R1a for absence, identity and reseats; R1b for occupancy | A transform cut the tables never named |
| Seeds | The rows whose generation moved | Each is among the rows this upload seeds in the previous-matrix table | R1a | The matrices' values |
| Device | The draw reserve's bytes up to the extent, read back from the device-local buffer | The host copy as it stood when the read was recorded | R1a | A frame whose region still owes words |
| Rays | A fixed ray set through the clustered scene | The same rays through a scene built whole | S2 | Rays not in the set |

Three properties of these comparisons are the design, not detail:

- **The pack comparison never observes.** `SdfMeshHistory.Observe` mutates
  its cells, so a checker that called it on the live history would corrupt it,
  and a second history would differ in every generation word. The comparison
  packs through the pure packer with the generation taken from the record, so
  it verifies every other word and says nothing about the generation.
- **The ledger shares no state with what it checks (F14).** It reads the
  generation from the record's word, which is what the kernel compares, and
  computes its expectation from its own copies of each row's occupancy, absence
  and identity, read from the producer's table and the arena for every row and
  not from what the staging function was handed, so a row the tables never
  staged is checked like one they did. Its identity comparison is written out
  in the checker, field by field, and is deliberately not the predicate
  `SdfMeshHistory.Observe` uses: a shared predicate would agree with itself. It
  fails a row whose generation moved without cause, and one whose cause moved
  no generation, which is the reused-row case: a run moved without its cells
  retired, and the row given back to the same key and mesh.
- **The device comparison closes what a host copy hides.** `GpuRegion.Write`
  updates the host copy before any flush, so a lost or misordered flush
  between a frame's slot and a landing job's leaves the host copy right and
  the device wrong. The read is a background job (an `SdfGpuWork` client,
  through `SdfGpuWorkRecording.CopyToReadback` as the bake's answers are),
  taken only while the lever is on, the region owes no words to any slot and
  no landing is in flight. The queue's order makes the device, at that job,
  the result of every flush submitted before it.

A step's check is the walkthrough tour and a meadow arrival on both backends
with zero mismatches, a non-zero compared count for each comparison the step
has, and the non-zero forcing counts the next table gives it, so a run that
exercised nothing cannot pass.

### Forcing every guarded path

The tour and an arrival do not reach most of the paths the design guards. Each
has a lever that forces it and a count that must be non-zero in the check of
the step named. The forcing levers are forms of the same verb as the
cross-check (working name `world.rows force`), or verbs that exist; each count
is in `sdf.rows.check` unless the table names another reading.

| Path | Forcing lever | Count that must be non-zero | Step |
|---|---|---|---|
| A hit rejected by the door | `force hit-bound`: one frame's readers are given a draw count of zero while the targets hold hits | `gpu.mesh-hits.rejected` under the lever, and zero without it | R1h |
| Refusal under the region's limit | `force limit <bytes>`: an admission cap the arena's one placing function checks, below what the rows name | Rows staged absent for a refused mesh; after `force limit auto`, refused rows returned live | R1a |
| Arena relocation, and the region shrinking to fit | `force relocate`: the next staging packs the arena as though `SdfMeshRegionArena.PackedShare` were passed; after a forced limit and a trim the region holds more than half again its need, so `SdfWorldTables.FitMeshRegion` shrinks it | The landing report's relocations (`SdfWorldTables.DescribeLanding`); rows that went absent and returned live, each seeded; region shrinks | R1a |
| A memory trim | `host.memory collect`, which exists and raises the account's trim (`MemoryAccount.TrimCaches`) | Trims the tables serviced; with refused rows standing, refused meshes named again | R1a |
| A consumer skipping past the journal's history (F2) | `force skip <consumer>`: the tables, the scene's key or the dresser's host cursor leave their cursor uncommitted for more frames than the journal keeps (`SdfMovedTransforms.History`) | Whole-table answers the cursor gave that consumer | R1a for the tables; R1b for the rest |
| New tables after a lost device | `gpu.faults lose`, which exists (`GpuCreationFaults.ArmLoss`) | Whole-table answers given to new tables, and a non-zero compared count on the frames after | R1a |
| A replaced producer, and a view following other tables | The tour's crossing into another world through the gallery portal, with a hover pick held across it; the seat's view then follows another residency **[inference]** | Whole-table answers given for another reader; picks discarded for another table | R1b |
| Free-row packing | `force pack`: the next dress starts over whatever the free share | Start overs for a pack, and rows they moved | R1b |
| A failed publication's retry (F7) | `force dress-fault [n]`: the nth dress throws after its first row write, as `gpu.faults record` fails a recording | Start overs after a failure; live rows equal to the sum of the owners' runs after each | R1b |
| Reserve growth with meshes placed | A cold boot, whose groups finish over many frames while the reserve doubles from `SdfMeshRegionArena.MinimumDrawCapacity` **[inference]**; the law covers it device-free | Meshes moved (the landing report's moved count); rows staged again through a moved mesh's chain | R1b |
| Lockstep packing | `world.lockstep on`, which exists, across a hand-over that changes a count | Start overs on locked frames; and a locked frame with no allocation or free adds none | R1b |
| A straddle by a host-drawn body and by a rigid copy | A tour stop that holds one of each across the gallery glass and then withdraws it | Clipped rows by owner kind (host, copy, in place), each non-zero; rows whose clip ended and that stage their base draw again | R1c |

Which debug-world bodies are host-drawn and which are rigid copies at the
gallery glass is **[unconfirmed]**; R1c's lane reads it from the world and adds
the stop, with a body of the missing kind if one is missing.

### The device-free law

Random edits of products, copies, clips and programs against the rebuild, on
the CPU device: the silent classes stay review-only until a check covers them,
and this one outlives the next rewrite of the dresser. It creates its own table
through the same creation any caller uses. Its edits include what the levers
force: host draws that carry slots and rigid copies under clips that start,
move and end; a limit low enough to refuse; a relocation; rows past the reserve
after meshes are placed; a pack; a write that throws; a consumer that skips
past the journal's history. After every edit it asserts the dress and pack
comparisons and the generation ledger. It lands with R1b; R1a lands the part
that needs no row table (absence, refusal, relocation, the ledger).

### Canonical on request

A frame that must repeat across Worlds is drawn from
[the canonical order](#packing-and-the-canonical-order). Its check is the
lockstep byte comparison of two fresh Worlds at one tick that the rendering
skill already describes, taken across a hand-over that changes a count.

## R0: the counts and the rule

R0 adds counts and decides nothing by time. Every staging passes
`SdfWorldTables.StageMeshRegion`, which ends in `SdfWorldTables.TryStageDrawChanges`
or in `SdfWorldTables.StageMeshRegionCore` **[opened]**, so one tally there
sees every record packed whichever path packed it. Today
`SdfMeshHistory.RecordsChanged` is added only by the changed-run staging
(`SdfWorldTables.StageChangedDraws`), never by the whole pack
(`SdfWorldTables.StageMeshMotion`) an arrival takes while a waiting draw is
filtered out, and every kind in `SdfMeshHistory.ProcessWork` is a running
total with no per-frame reading.

**The cause of a packed record.** Each record a staging packs is counted under
one cause. A staging that does not take the journal has one cause for all its
records, the first of the top five below that holds; one that takes it splits
its records between the last two. The dresser states why a frame owes
everything beside the serial it publishes, so the tables can tell the first
three apart.

| Cause | Holds when |
|---|---|
| `rebuild` | The frame owes everything because the dresser rebuilt its topology |
| `portal` | The frame owes everything because of the portal path |
| `retry` | The frame owes everything because the publication before it failed |
| `landing` | The dresser's journal is incremental and the tables refuse it because a mesh is owed, the staged list was filtered or its count differs |
| `cursor` | The journal cannot be collected: another producer or list, an expired serial, the first staging of new tables |
| `hand-over` | The journal is taken; this cause counts the records whose mesh differs from the one staged |
| `pose` | The journal is taken; this cause counts the rest |

**The counts** (working names, in `sdf.instances`; `<cause>` is each cause
above):

| Count | Kind | Holds |
|---|---|---|
| `rows.packed.<cause>` | Total | Records packed under that cause, on either path |
| `frames.<cause>` | Total | Stagings that packed a record under that cause |
| `frames.over-chunk.<cause>` | Total | Stagings whose records under that cause number more than the threshold below |
| `rows.owned.changed` | Total | Rows held by the owners whose mesh, copies or count the dress changed: what a proportional table would write |
| `dress.topology-builds.<reason>` | Total | Rebuilds by the first of the six reasons that fired |
| `peak.rows.packed.<cause>` | Peak | The most records one staging packed under that cause |
| `peak.rows.planned` | Peak | The most rows one staging submitted to `SdfMeshRegionArena.Plan` |
| `peak.rows.extent` | Peak | The most rows the table held |

And in the scene's counters (`SdfMeshIrradiance.Counters`): totals of scene
builds, static draws, top-level entries and prefix bytes exported, and the
peak of the entries and of the prefix bytes of one build.

`SdfMeshHistory.ProcessWork` lists its kinds by hand today, so a kind can be
declared and left out of the set. R0 moves its kinds to a
`WorkKindRegistry`, where declaring a kind is what lists it
(`WorkKindRegistry.Declare`), before it adds the per-cause families.

**A peak** is a per-frame high-water count: the largest value one staging (or
one scene build) published since the peaks were last cleared. `WorkCounterSet`
gains the raise beside `WorkCounterSet.Add`, and `world.counters` gains a form
that clears a source's peaks, as `host.frames reset` opens a frame window. A
reading clears the peaks at the start of its window and reads them at its end.

**The threshold** is one upload chunk of records: `SdfGpuWork.UploadChunkBytes`
divided by `SdfMeshRegion.DrawBytes`. A staging over it packs, on the frame
thread and into the frame's own upload, more record bytes than the background
upload budget allots one slot in one frame at the frame bar
(`SdfGpuWork.UploadBytesPerSecond` is a chunk in every slot each frame). It is
fixed here, before any reading.

**The windows.** Arrival: peaks cleared at the last settled stop before the
meadow, read once `world.wait bakes` and `world.wait residency` settle there.
Boot: a cold boot to its settle. Crossing: a body held across the gallery
glass. Steady: a settled stop with nothing arriving.

**The rule.** Each line is read from the counts alone.

| Reading | Sends |
|---|---|
| `frames.over-chunk.cursor` or `frames.over-chunk.retry` above zero in the steady or arrival window | A cursor is being lost or a publication is failing: that defect is fixed before R1a, since every later count rests on the journal being taken |
| `frames.over-chunk.landing` above zero in the arrival or boot window | R1a is on the arrival's path and takes its queued timing. At zero it still lands, on counts alone, because R1b stands on its absent record |
| `frames.over-chunk.rebuild` above zero in the arrival window, or `SdfMeshHistory.Seeded` exceeding `rows.owned.changed` by more than the threshold | R1b follows R1a. With neither, rows that keep their place buy nothing an arrival pays for: R1b and R1c are not built, and the open items record the reading |
| `frames.over-chunk.portal` above zero in the crossing window | R1c takes a queued timing. At zero it still follows R1b without one, because the clip is the one writer outside the door |
| `frames.over-chunk.hand-over` at zero in the arrival window | R2 stays with the visibility plan's V1b |
| `frames.over-chunk.hand-over` above zero, and `rows.packed.hand-over` below the sum of `rows.packed.rebuild`, `rows.packed.landing` and `rows.packed.portal` | R2 is built after R1c, ahead of V1b's own schedule |
| `frames.over-chunk.hand-over` above zero, and `rows.packed.hand-over` at or above that sum | R2 is built after R1a and before R1b: hand-overs, not shifted rows, are what the arrival packs |
| The peak prefix bytes of one build above `SdfGpuWork.MostUploadJobBytes`, or the arrival's total prefix bytes above one second of `SdfGpuWork.UploadBytesPerSecond` | S2 follows S1: a scene change takes several background jobs before probes may trace it, or the arrival's exports take a second of the landing's budget |
| Neither | S2 is dropped and 3a is the whole answer for the scene |

**R0's own check.** The seven `rows.packed.<cause>` totals sum to
`SdfMeshHistory.RecordsPacked`, the reasons sum to
`SdfMeshHistory.DressTopologyBuilds`, a cold boot shows `landing` above zero,
and a steady window shows every `frames.over-chunk` count unchanged.

## The measurement each step lands on

Counts are the evidence; a timing is a request for the lead's quiet window
(frozen builds before and after, `host.frames` at the meadow across an arrival,
p99 and worst frame with their `produce-work`, collections and thread-allocated
bytes).

| Step | Counts that must move | Queued timing |
|---|---|---|
| R0 | [Its counts](#r0-the-counts-and-the-rule), and its own check | None: its rule decides the order |
| S1 | Allocation trace across an arrival: the scene constructor, the export and the scene start's snapshot near zero; collections in `host.frames` | Meadow arrival p99 and worst frame |
| D1 | Allocation trace: the decode and the managed renumbering near zero; restores read fewer planes | The same window |
| R1h | `gpu.mesh-hits.rejected` and `sdf.instances.targets.stale` zero across the tour on both backends; rejections non-zero under the lever | None |
| R1a | `rows.packed.landing` and `frames.over-chunk.landing` fall to the rows that name each landed mesh; `SdfMeshHistory.Seeded` per landing stops following the list's size | Cold boot's owed frames, if R0 sends it |
| R1b | `rows.packed.rebuild` and `SdfMeshHistory.DressTopologyBuilds` move only on a start over; `SdfMeshHistory.StagingPlanRecords` equals the rows gained and renamed; a hand-over's packed records equal `rows.owned.changed`; the whole-walk kind stays at zero on a hand-over frame; thread-allocated bytes of that frame; the draws the late occlusion phase emits on the following frame | The hand-over frame |
| R1c | `rows.packed.portal` falls to a straddling body's own rows and their copies | A portal crossing at the meadow, if R0 sends it |
| R2 | A hand-over's packed records equal its pieces | Decided by R0 |
| S2 | Clusters built against reused, prefix and cluster bytes uploaded per scene, time from the last landing to a settled volume | Decided by R0 |

## Steps

Each is one lane, in this order; S1 and D1 share no file with the R steps and
run beside them, and R1h shares none with R0. A check that needs a windowed
World on both backends, or a Vulkan run under GPU-assisted validation, is a
verification request to the lead.

| Step | Size | Lands | Door | Check |
|---|---|---|---|---|
| R0. Count | Small | The causes, the totals and peaks, the peak raise and its clearing form, the scene's counts | None | [R0's own check](#r0-the-counts-and-the-rule); the lead reads the windows and applies the rule |
| S1. A scene build keeps what it allocates | Small | Pooled scene arrays returned at the last claim; a static-only snapshot; the prefix exported on demand | The export still has one publisher | The exported words equal the old export's for one scene (a change that claims to change nothing); the allocation trace |
| D1. Decode into the mesh's storage | Medium | The storage parameter, material ids applied on read, the restore's plane filter, piece counts in the entry | One decoder | The codec's round trip for both storages; a warm boot loads every group; a relocation restores |
| R1h. The hit door | Small to medium | The resolver and the resolved hit, every reader taking one, the rejection count, the stale-target count, the lever that forces rejections | The resolver | Zero rejections and zero stale targets across the tour on both backends; rejections counted under the lever with no lost device on Vulkan under GPU-assisted validation; a boot's captures unchanged |
| R1a. The staged table is the producer's | Medium to large | The absent bit and its kernel branch; the staging function's two branches and the per-row retire; rows by mesh and a landing staging them; the journal taken while meshes are owed; the reader and the one cursor in every consumer; the tables' glass row set; the queue precondition; the pack, ledger, seed and device comparisons; the levers for refusal, relocation, trim, a skipping consumer and new tables | The tables' one staging function; the cursor | The cross-check over a cold boot with its forcing counts; the law's part without a row table; a boot's captures fill in as before; `world.wait residency` still names what is owed |
| R1b. Rows | Large | The row table and its writer; runs and occupancies; the arena's row operations; identity by row for picks; claims and glass counted in the writes; the whole-walk count; the start over, used for a failed publication, the free share and a locked frame, and the canonical flag; the scene key and the diagnostic scene as sets; the dress comparison, the ledger's occupancy and the law. Until R1c the portal path stays as a visit of every row through the writer, its copies one run | The writer; `SdfMeshHistory.Observe` given the occupancy | The cross-check over the tour and a meadow arrival, both backends, with its forcing counts; a presented burst across a hand-over with the group in every frame (the capture [the residency packet](open-items.md#residency-and-streaming) still owes); `world.view.pick` across a hand-over and across the gallery portal; two Worlds' lockstep stills across a count change |
| R1c. Clips are derived | Small to medium | The slot index over every row, the staged draw computed from base and clips, a clip's copies as a run; the portal path's "everything" and the list rewrite deleted | The table's staging of a row | The cross-check through a crossing by a host-drawn body and by a rigid copy, with clipped rows of each owner kind and ended clips counted; a straddling body clipped on both sides and whole again after it withdraws |
| R2. Piece rows | Large | With the visibility plan's V1b | That plan's | That plan's, and a hand-over's packed records |
| S2. Clustered top level | Medium | Cells, clusters reused by equal sorted lists, cluster blocks in the arena, the prefix over cells | The cluster's constructor; the one scene publisher | The cross-check's ray set; the irradiance scene laws; a pack rebuilds no cluster; a scene change during an upload abandons without a lost device on Vulkan |

No step is a stopgap for a later one: R1h's resolver is what R1a's absent
record is rejected by, R1a's absent record, row-for-row staging and rows by
mesh are what R1b's free rows and R2's piece rows stand on, and S1's pooled
arrays are what a cluster keeps.

## Systems considered

| System | Verdict | Why |
|---|---|---|
| `GpuChangedRows` and `GpuUploadRuns` (the draw journal) | Reused, split into a producer's journal and a reader, with one cursor | Captured-endpoint cursors per consumer and a conservative refusal are exactly F2's answer; rows that keep their place make "everything" rare instead of routine, and one cursor type ends the hand-written guards |
| `GpuRangeAllocator` | Reused | Already the allocator of the mesh arena, the probe scene's arena and both backends' memory pools; rows are a fourth range space, and its per-allocation ticket is the stale-holder guard a run needs |
| `SdfMeshRegionArena` | Kept, named by row | Placement, refusal, reserve growth, slack and relocation stand as row operations; `SdfMeshRegionArena.Plan` is those operations over a whole table |
| `SdfMeshHistory` | Reused, given the occupancy and a per-row retire | Generations that are never reused per cell are the proof a reused row needs once the observation can see a reuse and an absence |
| `SdfWorldTables` changed-run staging (`SdfWorldTables.StageChangedDraws`) | Becomes the only staging | The whole-list branch is the cursor's whole-table answer, not a second path |
| The landing filter | Replaced | It is why staged rows differ from the producer's |
| `GpuRetirementQueue` and `SdfWorldTables.Retire` | Reused where a buffer is replaced | Row reuse replaces none |
| The slot proof (`RenderGraphSlotResults`, `SdfMeshOcclusion.Completed`) | Reused | A readback carrying a row is delivered only at its slot's completion; the table and the row's serial decide whether it is still true |
| The specialization's retired builds (the residency's retired shade sets and their pins) | Considered, not reused | The same idea, a superseded product kept until nothing pins it, is what scene and cluster claims already do with their own counts |
| `SdfMeshClaims` | Reused | `SdfMeshClaims.Move` per row, with a claim and a release for a row gained and lost, inside the writer |
| `SdfDressedPickMap` | Replaced by the table's identities | It is rebuilt and compared whole, and assumes the host's rows lead the list |
| `SdfSlotClip.Apply` | Replaced by a staged draw the table derives | The one unjournaled writer; its rule (the first clip whose run holds the slot) is kept |
| The dresser's slot index (`SdfMeshDresser.IndexBindings`) | Kept for poses, not used for clips | It lists only bindings drawn in place, and a clip reaches host draws and copies too |
| The bake's product revision | Rejected as a change signal | Eight writers move it by hand; the dresser's comparison needs none |
| The background work's readback (`SdfGpuWorkRecording.CopyToReadback`) | Reused for the device comparison | It is how a background job already answers with device bytes at a point in the queue |
| `gpu.faults` and `host.memory collect` | Reused as forcing levers | A lost device and a trim already have operator levers; the new forcing forms follow `gpu.faults record` |
| `WorkCounterSet` and `WorkKindRegistry` | Reused; the set is extended with a peak | Its totals cannot give a per-frame high-water count, which R0's rule reads; the registry lists R0's per-cause kinds by declaring them |
| The geometry cache and the arena's fence-proven blocks (`SdfMeshIrradianceScene.Geometry`, `SdfMeshIrradianceScene.GpuArena`) | Reused one level up | Clusters are identified and kept exactly as geometries are |
| `SdfMeshIrradiance.ChangedBoxes` and the probes' affected test | Reused | The change set stays the invalidation; its source becomes the cluster difference |
| The native payload's blocks and the restore's fill (`SdfMesh.NativeCopy`, `SdfMesh.Hold`) | Reused as the decode's destination | The decode writes where the copy wrote |
| The visibility plan's V1 and V2b | This memo is V2b's design and feeds V1b | One plan for one mechanism |

## Puck-native options beside the borrowed ones

| Borrowed | Puck-native beside it | Taken |
|---|---|---|
| Persistent instance rows with a free list (Unreal's GPU scene, Unity's batch renderer group, Bevy's retained render world) | Rows as a derived view: the table is reconciled each frame against the inputs, and the rebuild is its definition and its check | Both: 1c is the borrowed structure under the native derivation |
| Retire a replaced buffer a frame later (Unity) | A completion point counted on one queue, and a generation per row | The native one, which exists |
| Rebuild the top level each frame; refit only deformed bottom levels (NVIDIA, Lumen) | Parts identified by their contents and kept, as each geometry is | 3d, behind 3a |
| A hash of contents as a cache key | The contents themselves as the key, compared exactly, since a wrong reuse is silent | 3d |
| An address derived from identity (1d) | The same idea, for rows | Not taken: it doubles every per-row buffer |

### Sources

- NVIDIA, "Best Practices: Using NVIDIA RTX Ray Tracing"
  (<https://developer.nvidia.com/blog/best-practices-using-nvidia-rtx-ray-tracing/>):
  "For TLAS, consider the `PREFER_FAST_TRACE` flag and perform only rebuilds";
  "BLAS updates are a good choice after limited deformations"; "I recommend
  rebuilding the BLAS instead of updating after topology changes". Opened.
- Epic, the Lumen performance guide
  (<https://dev.epicgames.com/documentation/en-us/unreal-engine/lumen-performance-guide-for-unreal-engine>):
  "Hardware Ray Tracing requires rebuilding the Top Level Acceleration Structure
  (TLAS) every frame"; fewer than 100,000 instances after culling. Opened.
- Bevy 0.16 release notes (<https://bevy.org/news/bevy-0-16/>): the retained
  render world "allows the CPU to avoid processing and uploading data that
  hasn't changed since the last frame". Opened.
- Unity, creating batches with a batch renderer group
  (<https://docs.unity.com/en-us/engine/current/manual/analysis/graphics-performance-profiling/in-urp/reduce-draw-calls-urp/batch-renderer-group/creating-a-renderer/batches>):
  "Don't dispose of the previous buffer in the same frame that you call
  `SetBatchBuffer`"; "If Unity renders draw commands that refer to a disposed
  buffer, the result is undefined". Opened.
- Unreal's persistent primitive index and per-primitive instance ids are a
  search summary of Epic's API reference, not opened. The sources disagree on
  nothing this memo uses; none of them states how a freed row is kept from a
  reader in flight beyond Unity's one frame.

## What would change the answer

- **R0's readings.** [The rule](#r0-the-counts-and-the-rule) says which reading
  moves R2 ahead of R1b, drops S2, or leaves R1b and R1c unbuilt.
- **The packing frames show in `host.frames`.** The budgeted move is added
  beside the pack, off locked frames.
- **The absent rows show in a raster pass's GPU time.** The packing share is
  lowered, which bounds their selection lanes; the empty command each still
  emits goes with the visibility plan's ordered compaction (V1a).
- **A rejection or a stale target is counted on an ordinary run.** A target is
  being read under records it was not written under: the cause is found from
  the pass the count names before R1a lands, since the door only bounds such a
  read.
- **Background work moves to another queue.** The queue precondition refuses
  it, and a row's reuse then needs a fence after its free, which is a new
  design.
- **A holder of a row index turns up that this memo and its review missed.**
  That holder gets the table and the row's serial, or the generation, before
  R1b lands.

## Open questions and their owners

| Question | Owner | What decides it |
|---|---|---|
| Which changes the arrival's rebuilds were, and how many rows the changed owners held | R0, read by the lead | The counts by cause and the rule |
| The scene's static draws, entries and prefix bytes at the meadow, and builds per arrival | R0 | The scene's counters and the rule |
| Which debug-world bodies straddle the gallery glass as host draws and as rigid copies | R1c's lane | The world's documents and a pick at the glass; it adds what is missing to the tour |
| The cell size of 3d | S2's lane | Clusters built per scene change and traversal steps per ray, both counted |
| How many runs a program rebuild reallocates | R1b's lane | Runs kept across `world.reload` and a responding placement's toggle |
| The free share that triggers a pack | R1b's lane | Packs per arrival against the peak extent, both counted |

## Review answers

One line per finding of the review, with where the design answers it.

| Finding | Answered in |
|---|---|
| H1. The slot index does not name a clip's host draws and rigid copies | [Clips are derived in the door](#clips-are-derived-in-the-door): the staged draw is computed from the base draw and the clips, over a slot index kept inside the writes; step R1c and its forcing row |
| H2. The pack comparison cannot pack from nothing, and nothing checked the generation | [What the cross-check compares](#what-the-cross-check-compares): the pack comparison takes the generation as an input, and a ledger with its own state checks clause 3; [a run per owner](#a-run-per-owner) gives the observation the occupancy |
| M1. A row returning from absence with the same occupant does not seed | [The staged table is the producer's table](#the-staged-table-is-the-producers-table): the absent branch retires the cell, so the return seeds |
| M2. The door is convention | [The row table and its one writer](#the-row-table-and-its-one-writer): a writer only the creator holds, a table and a reader with no write member, one cursor |
| M3. R0's rule does not decide | [R0: the counts and the rule](#r0-the-counts-and-the-rule): causes counted on every path, peaks, one threshold, a rule read from counts alone |
| M4. Row-wise arena operations hid the plan's work | [The arena is named by row](#the-arena-is-named-by-row): placing, refusing, reserve growth with moved meshes' chains, slack and packing |
| M5. A pick's change count needs the table and the render's count, and host translation assumes leading rows | [How the GPU side stays correct](#how-the-gpu-side-stays-correct), readbacks: the table by reference, the serial the surface pass committed, translation through owner and place |
| M6. The clamp | [How the GPU side stays correct](#how-the-gpu-side-stays-correct), the hit door: one resolver that rejects and counts, as the lead ruled; step R1h |
| M7. The cross-check runs where the guarded paths are not | [Forcing every guarded path](#forcing-every-guarded-path): a lever and a non-zero count for each, assigned to a step |
| L1. A failed reconcile leaks runs | [A failed publication starts over](#a-failed-publication-starts-over): the start over frees through the table |
| L2. Uncounted walks and three glass counters | [Counts the door keeps](#counts-the-door-keeps): claims and glass kept in the writes, whole walks counted by the table |
| L3. The scene's key and S2's lists depend on order | [Decision 3](#decision-3-the-trace-scenes-top-level): a key that is a sum, absent rows not static, sorted cell lists; the holders table is corrected |
| L4. Lockstep packs every frame, in the rebuild's order | [Packing and the canonical order](#packing-and-the-canonical-order): a canonical flag makes the pack idempotent, and the budgeted move never runs on a locked frame |
| L5. R1a's chain was a stopgap for R2 | [Decision 2](#decision-2-what-a-hand-over-rewrites): the chain stays and links piece rows |
| L6. The records argument rests on one queue | [How the GPU side stays correct](#how-the-gpu-side-stays-correct), records: the boundary is stated and checked where a landing is recorded |
| L7. The host copy hides a lost flush | [What the cross-check compares](#what-the-cross-check-compares): the device comparison |

---

[Plans](README.md) · [Visibility](visibility.md) · [Graphics architecture](graphics-architecture.md) · [Open items](open-items.md#residency-and-streaming) · [Frame rendering](../rendering/sdf/handbook/frame-rendering.md)
