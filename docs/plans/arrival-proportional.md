# Arrival work proportional to change

The design memo for three costs an arrival still pays whole: the frame that
takes a hand-over restages every draw row, the lighting's trace scene rebuilds
its top level and its GPU export on every scene change, and a store load decodes
into managed arrays before its native copy. Each decision names its options, the
evidence, the failure modes each option closes, the favoured option, the
strongest case against it, the invariant and the one place that enforces it. It
is the design of [the visibility plan](visibility.md#v2-instance-updates-proportional-to-change-and-authored-motion-on-the-gpu)'s
"retained instance updates" (its V2b) and says what its record split (V1b) owes
the hand-over. The steps are ready for build lanes.

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

The row and consumer doors are current; the remaining whole-scene trace cost
and store path are inventoried below. [R1b as built](#r1b-as-built) records
the enforcing structures and [its request](#windowed-r1b-verification-request)
holds the windowed observations still owed.

### The draw rows and their staging

`SdfMeshDresser.Dress` keeps a run for each owner instead of rebuilding a dense
list when a group's count changes. The host's list, each bake group, each
own-mesh occurrence and the portal copies have separate runs. A reshaped owner
allocates its replacement run and frees the old run by ticket; other runs stand.
The table carries staged draws, owner provenance, occupancy and a reader-only
journal. The frame retains the host's input list separately.

`SdfWorldTables.StageMeshRegion` consumes row changes through `GpuRowCursor`,
updates arena names and claims, applies placements and stages changed records
through `StageMeshDraw`. A free, owed or refused row is absent, never filtered.
`GpuRowIndex` supplies each mesh's row chain, so a landing or reserve growth
revisits only the affected mesh's rows. A new/expired cursor takes a bounded
whole-table answer through that same path. Normal extent changes keep the row
journal incremental; dense input lists retain the conservative extent check.

### Who holds a row index

| Holder | What validates the row |
|---|---|
| The staged record and previous matrix | `StageMeshDraw`, observing table identity and occupancy through `SdfMeshHistory.Observe`, or retiring absence |
| Occlusion and detail history | The record's generation; a changed occupant seeds its previous matrix |
| A delayed pick | The mesh target's table/revision stamp, the row's last identity revision, and the immutable host map |
| The static scene key | A cursor-maintained sum of static-row hashes, independent of order |
| The diagnostic scene | Equality of static contents as a multiset |

### Every writer

Only `SdfMeshRows.Writer` can allocate, free, set or start over its table. Each
operation owns its journal entry, mesh claims, glass count and picking
provenance. `SdfMeshDresser` derives changed groups and copies by comparison,
and reads host and transform journals through independent cursors. A failed
publication remains pending; retry starts over through the table, including
allocations the failed owner never remembered.

`SdfMeshRows.Writer.SetClips` compares the ordered clip values and visits the
changed clips' old and new slot chains. The table derives each staged draw
from its base and the first matching clip, and owns each clip's carried run.
The slot index includes host, rigid-copy and in-place rows; the dresser's
pose-binding index is independent. `SdfSlotClip.Apply` remains only in the
rebuild oracle and laws. [R1c as built](#r1c-as-built) owns the details and
[its request](#windowed-r1c-verification-request) the observations still owed.

The copy comparisons (`SdfMeshPathBake.SameCopyLayout` and the dresser's input
snapshot) remain the derivation over instances. The static trace scene still
builds its top level for changed contents, as decision 3 describes.

### The trace scene and its export

`SdfMeshIrradianceScene.Build` reuses each geometry's hierarchy from the scene
it replaces and builds the rest new **[opened]**: its draws, the top level's
entries and a binned hierarchy over them, and the claims of every static draw.
The arrays the scene keeps are rented at the static draws' and entries' counts
and go back to the pool at the scene's last claim
(`SdfMeshIrradianceScene.Content`, step S1), and the build's workspace is rented
and returned within it. `SdfMeshIrradiance.StartSceneBuild` copies only the frame's
static draws, on the frame thread, into a spare rented array it swaps with the
installed scene's draws, and moves its mesh claims to them;
`SdfMeshIrradiance.ChangedBoxes` counts both static lists in workspace kept
across builds. `SdfMeshIrradianceScene.CreateGpuExport` keeps each geometry's
words where the arena placed them and writes the prefix (top nodes, entries and
one record per instance) from the scene's arrays as the upload reads it, so the
prefix has no host copy; the upload still sends the whole prefix **[opened]**.
One door publishes
(`SdfMeshProbeGpuWork.TryTake`): no query is admitted while a replacement
uploads, the root is written last, and an abandoned upload frees only its
prefix.

The graphics research round sized the top
level as "hundreds of draws, trivial". The scene's static draws at the meadow
are not counted in any reading this memo has **[unconfirmed]**; the allocation
trace named the scene's constructor and the export as the two largest
allocators of an arrival **[reading]**.

### The store decode

A host's load decodes an entry straight into the storage its mesh keeps:
`SdfMeshLibrary.TryLoad` hands `SdfMeshStoreCodec.TryDecode` the mesh's native
storage (`SdfMesh.Native`), the group's material ids are applied as the
material planes are read, and `SdfMeshStore` reads the entry into owned
scratch **[opened]**. Managed arrays remain only for an offline library, which
has no allocator (`SdfMeshLibrary.RenumberManaged`). A restore of a published
mesh's normals and coarser levels (`SdfMesh.Hold`, and the owed set's needs)
reads only those planes and skips the rest by their stated length. This is
[decision 4](#decision-4-the-store-decode)'s favoured option, built as step D1.

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
draw journal (`SdfMeshRows.Changes` out of the dresser, and the host's own
`SdfFrame.MeshDrawChanges` into it) carry a reader instead (working name `GpuChangedRowsReader`): one
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
them, by complete staged value and in order, with the list the table holds.
`SdfSlotClip.SameStagedContent` includes floating-point bits, matching the draw
oracle, and is shared with the host's retained clip-list comparison. An unchanged list
costs that comparison. A clip that started, ended, changed or changed its place
in the list walks the chains of its slots, the ones it held and the ones it
holds, stages those rows again and journals them; staging a row asks again
which clip is first for its slot, so overlapping runs resolve as they do today.
A set of a clipped row's base draw, as when its body moves, stages the row and
its carried copy inside that set when membership stands. Changed membership
is reconciled by `SetClips` and again at `Publish`, before the rows are published.

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
derivation; [the frame-time findings](open-items.md#other-frame-time-findings) hold
them.

### Packing and the canonical order

**A pack is a start over.** The writer's start over frees every run through
the table and clears the owners' runs, bindings and clips and the claims; the
dresser then reconciles from the inputs as a first dress does, in a fixed
order, journaling everything. Allocating in that order from an empty table
lays the rows out in the canonical order: the host's run, each finished group's
run in the bake's order, the instances' own meshes, then each clip's copies in
the order of the rows they copy, with clips in frame-list order. This is a
canonical row order across Worlds; the rebuild oracle compares contents as a
multiset, independently of that order. Every row takes a new occupancy.

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
| 3e. Build the top level on the GPU each change | H-PLOC builds a hierarchy over a million instances in milliseconds (Benthin et al., H-PLOC, opened by the graphics research round) | Large: a build kernel family on both backends | The CPU still traverses the scene for placement and diagnostics, so it would be a second builder | The upload |

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
([how memory is read](streaming-and-memory.md#what-adds-and-what-only-names)), the CPU scene
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

**Favoured: 4b.** `SdfMeshStoreCodec.TryDecode` takes a storage that hands out a
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

`worlds/debug/portal-rows.puck` supplies the missing host-mesh witness: the brass
authored diamond is a host draw, and its teal field keel is an in-place draw.
Its neutral timeline keeps it in `WorldCreationInstances`; the added highest
population slot leaves existing indices intact. The idle fuzzer stand-in
(`worlds/debug/stand-in.puck`, placed by `worlds/debug/fuzzer.puck`) is a rigid
copy by `WorldRigidAssets.IsRigid`. The tour holds and withdraws each at the
linked paintings. These are source-confirmed owner kinds; the straddle,
its picks and its appearance still require the [windowed check](#windowed-r1c-verification-request).

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

### R1a as built

`SdfWorldTables.StageMeshDraw` is the one record-writing door, on full and
incremental staging. It asks `MeshStands`: a standing row observes and packs
the arena's current placement; an owed or refused row calls
`SdfMeshHistory.Retire` and `SdfMeshRegion.WriteAbsent`. The absent record has
only `SdfMeshRecord.AbsentFlag` set, in the selection flags. Its generation and
every placement word are zero. `sdf-mesh-occlusion.comp.hlsl` resets its state
and emits empty commands before any other selection metadata or mesh read;
R1h's `sdfMeshResolveHit` rejects every triangle through its existing empty
index-range test. The table extent is the producer count throughout landing.

`GpuRowIndex` keeps the tables' mesh-to-row chains and replaces the dresser's
separate slot/copy chain implementation. Completion marks just the landed
mesh's rows for the next staging. `StageChangedDraws` compares standing versus
absent as well as draw content, so a returning row gets a new generation and
seed even if its identity did not change. The draw journal remains usable
while other meshes are owed. The staging door also maintains the live glass
row set; `GlassMaySee` walks it without a mesh-revision refresh.

The compiler separates `GpuChangedRows`' producer from the frame's
`GpuRowChanges` reader. Only `GpuRowCursor` collects it. All six consumers use
that type: dynamic transforms, mesh transform cuts, tables' draws, dresser's
slots and host draws, and the irradiance static key. The cursor owns reader
and roster identity, extent, revision, captured serial, retained-window and
run-bound checks, and returns runs on both incremental and whole-table
answers. Consumers commit the endpoint they consumed. This guarantee stops
at a consumer committing without consuming; R1a does not yet give the
producer's dense draw list R1b's write door or occupancy tokens.

`SdfGpuWorkRecording.Queue` reports its service's actual submitter.
`RecordLanding` refuses a queue other than the tables' device frame queue
before owing or copying anything. The tables' counted wrapper forwards to
that same device queue. The merged memory-admission work keeps ownership of
buffer replacement and reservations; the diagnostic cap is checked beside
the presentation grant in the arena's one `TryPlace`.

`world.rows check on` enables `SdfRowChecks` across replacement tables.
`SdfWorldTables.CheckMeshRows` reads every producer row and arena placement,
packs scratch without observing, owns a field-by-field identity/absence
ledger independent of `SdfMeshHistory`, and checks pending previous-matrix
seeds. `MeshRowsReadback` uses the existing background readback service to
compare device bytes to host bytes captured at recording, only with no owed
region copy or carry and no landing in flight. `world.rows status` names the
first differing row and identity. The `sdf.rows.check` source exposes compared
and mismatched counts for pack, ledger, seeds and device, plus the forcing
witnesses. Occupancy joins the ledger in R1b; undisclosed transform cuts and
device bytes outside a sampled job remain outside this check.

The implemented forcing forms are `world.rows force limit <bytes|auto>`,
`world.rows force relocate`, and `world.rows force skip tables`. The last
holds the cursor across more producer serials than the journal retains,
including repeated staging of the same serial. Existing `host.memory collect`
and `gpu.faults lose` cover trim and replacement tables.

**Device-free evidence.** The Release run of `SdfMeshArrivalLawTests`,
`GpuChangedRowsLawTests`, `SdfMeshStagingCauseLawTests`,
`SdfFrameBlockLawTests`, `SdfLightAndSkyLayoutLawTests` and
`SdfKernelHeaderReadLawTests` passed all 28 cases after merging the integration
tip. The arrival law's 33-row boot packed and seeded 33 landing rows. A later
one-row mesh landing packed and seeded one, while the standing edit made
with that mesh still owed packed one. The same run compared 2,840 pack and
ledger rows, 101 seeds and 297 modeled device rows with zero mismatches;
counted 34 refused absent rows, 101 returns live, two serviced trims, 12
whole-table answers and 66 skipped cursor commits. These aggregate forcing
counts include the second law: its forced relocation shrank a region from
33,554,432 to 16,777,216 bytes, with one shrink counted. No timing is inferred
from this CPU upload model.

The extended law also stages the same owed frame twice without counting it
twice, then renames its last owed row to a standing mesh. The current demand
settles without uploading the abandoned payload; `AccountMeshOwed` closes
the clock at completion or that last-name change, independently of a plan.
`DescribeLanding` exposes `owed-clock=running|settled` beside its counts.
The arena's `Rename` also removes a refusal when its last draw name leaves;
a direct arena witness covers that case.

The additional requested `SdfKernelSetLawTests` run passed six of nine cases;
three fixture failures remain in the [refactoring packet](open-items.md#refactoring-campaign).
The full-encoding versus field-only fingerprint failure also reproduces
against the saved pre-change assemblies. `puck shaders generate --check`
reports every generated declaration and both fingerprints current. This is
not a clean kernel-set suite result, and the windowed backend check remains owed.

**Windowed verification request.** On a fresh debug World process on each
backend, enable the check before its first completed presentation, retain a
boot capture burst, then use the boot waits and counter reads below, adding
`world.counters sdf.rows.check --json` and `world.rows status`. Expect nonzero
pack, ledger, seed and device comparisons, zero mismatches, zero unforced
hit rejections and stale targets, and captures filling in the same places as
before. Read deltas around `force limit 0`, `host.memory collect`,
`force relocate`, `force limit auto`, `force skip tables` followed by more
than `SdfMovedTransforms.History` produced frames, and `gpu.faults lose`.
Refused-absent, returns-live, trims, shrink (after the oversized region is
relocated under the cap), skipped commits and new tables' whole answers must
all have nonzero witnesses. Read `world.wait residency` under the cap: it
must name the refusal; with the cap restored it must settle. The lead's quiet
`puck measure` window compares frozen pre-R1a and R1a cold boots, the owed
frames' `host.frames` p99/worst `produce-work`, collections and allocated
bytes, beside packed rows, seeds and `DescribeLanding`'s owed counts. The
headless World stages no draws and supplies no windowed comparison or timing.

File the timing question against the integration base and this lane's final
head, which freezes both input trees and builds, then drain the returned
request ids in the lead's quiet window. These commands retain the shared
mesh store and each process's initial frame history; the boot metrics are
read beside the owed interval, not claimed to filter it automatically.

```text
puck measure request --name r1a-boot-directx --before-commit lead/authoring --after-commit lane/r1a --metric /metrics/boot.frames.p99/median --metric /metrics/boot.frames.max/median --decides "Report paired cold-boot p99/max in ms beside owed-frame produce-work, collections, allocation, packed rows and seeds; investigate any increase." -- bench frames worlds/debug --backend directx --ticks 120 --samples 1
puck measure request --name r1a-boot-vulkan --before-commit lead/authoring --after-commit lane/r1a --metric /metrics/boot.frames.p99/median --metric /metrics/boot.frames.max/median --decides "Report paired cold-boot p99/max in ms beside owed-frame produce-work, collections, allocation, packed rows and seeds; investigate any increase." -- bench frames worlds/debug --backend vulkan --ticks 120 --samples 1
puck measure run <request-ids> --repeats 5 --window-seconds 1200
```

The headless debug World booted through the Operator and stopped with exit 0.
Its `sdf.instances` and `sdf.rows.check` counts were all zero. This host
registers no `world.rows` verb, and `world.wait ready` and `world.wait residency`
refused with their explicit no-renderer reasons. The nonzero witnesses above
come from real tables on the CPU upload model, not from that headless boot.

#### R1a systems considered

The owning rendering map, its C#/HLSL sync pairs, and the .NET performance
measurement guidance routed this change. The rendering skill's stale landing
filter and cursor description was corrected; this is a code-map update, with
no agent behavior evaluation claimed.

`puck search -M 0 'intrusive|m_nextBindings|TryCollect' src/Puck.Abstractions src/Puck.SdfVm`
found the bounded journal and the dresser's slot/copy chains. R1a reuses the
journal, splits off its reader and replaces those chains with `GpuRowIndex`;
it does not keep another chain implementation beside the new mesh index.
The memo's table frame-slot index was a lead, not a current reusable type.

The branch duplicate check's real overlap was the journal's run-append loop
and the tables' run-copy loop. The technique search
`puck search -M 0 'void Append|CopyRuns|source.Start' src/Puck.Abstractions/Gpu/Residency src/Puck.SdfVm/SdfWorldTables.Motion.cs`
found those two implementations. They now use the shared
`GpuUploadRuns.Append`, in the type that owns coarsening and merging.
The other neighbour records are type-specific identity equality and hashing,
command registration through `CommandDefinition.WithWireArgs`, dictionary
lookups, array clears, resource disposal and the two different background
completion/abandon actions; none provides another row index, cursor or packer.

Both helper flags were read. `SdfMeshIrradiance.StaticKey` is the existing
static-row fold and journal consumer, not a new hash primitive. The search
`puck search -M 0 'Fnv1aHash|HashCode.Combine' src/Puck.Maths/Fnv1aHash.cs src/Puck.SdfVm/SdfMeshIrradiance.cs`
found the stable integer FNV accumulator and this presentation key's existing
`HashCode.Combine` calls; R1a changes only its journal consumer.
`SdfWorldTables.ServiceMemoryTrim` is the existing account callback servicing
the arena. Searching `TrimUnused|TrimCaches|Lru` in that file, the arena and
`MemoryAccount` found `MemoryAccount.TrimCaches` and
`SdfMeshRegionArena.TrimUnused`; no cache or retry mechanism was added.
The pure pack comparison reuses `SdfMeshRegion.WriteDraw` and
`WriteAbsent`; the independent ledger deliberately repeats only the identity
predicate it checks, as F14 requires. Device comparison reuses
`SdfGpuWorkRecording.CopyToReadback` and the existing slot/fence lifetime.

### R1b as built

`SdfMeshRows` holds private row storage. `Create` delegates to the nested writer's
factory, which constructs a fresh table and a writer with a private constructor;
there is no operation that obtains a writer for an existing table. Frames retain
the host's input list and publish `MeshRows` separately. The writer's allocate,
free, set and start-over operations own the journal, claims, glass count,
occupancies and picking provenance. `GpuRangeAllocator` supplies the runs and
checks their tickets. An occupancy is scoped by table identity and never resets.

`SdfMeshDresser` compares actual group products and copy membership under the
bake's frame gate. It replaces only that owner's run when its count changes;
other owners retain their addresses and bindings. A start over frees through
the table, including a run no owner retained after a failure, and rebuilds in
host/group/own-mesh/clip-copy order. Allocation and free clear the canonical flag;
only the first completed reconciliation or a completed start over sets it.
`PackingFreeDivisor` chooses the free share. Locked frames start over only when
ordering is no longer canonical or reconciliation will change an owner's count.
R1b's portal walk can discover a copy-run change after reconciliation; on a locked
frame it retries from empty before publishing, then stands on subsequent frames.

The arena's `BeginRows`, `NameRow` and `EndRows` own reserve growth, names,
admission, slack and relocation. `Plan` calls those same operations for a whole
input. Running placement and unused-word totals replace the old placement scan.
Unnames precede placing, so their released slack is available to new names.
The table consumer moves claims and mesh-row membership per changed name.
`StageMeshDraw` remains the only record writer: it observes a live draw with its
table and occupancy or retires and writes absent. Newly exposed records upload
every word, because a grown reserve can cover old mesh words even where its new
host shadow reads zero. A moved mesh's chain stages in the same transaction.

`SdfDressedPickMap` translates owner provenance instead of storing a copied
identity list. `SdfWorldPasses` retains the mesh target's `SdfMeshRowStamp` per
view; the recorder restores its previous stamp on abandonment. The views pass
stamps that epoch, not the latest frame revision. `SdfWorldPicker.Current` rejects
another table or a row identity changed since that epoch; `SdfPickResult` applies
the same identity bound when a published answer is read later. The host map is
still compared by reference. Unrelated hand-overs retain valid picks.

The irradiance key is an incrementally maintained sum of static-row hashes.
Its static-row set supplies snapshots; a pack alone keeps the scene, and
`DiagnosticScene` compares scene contents as a multiset, including multiplicity.
The dresser's optional oracle rebuilds from inputs and compares live draws as a
multiset. The independent staging ledger also reads occupancy and producer-table
identity; it shares no observation predicate with the live history.

The enforcing doors are therefore the writer for row mutation and its journal,
the allocator ticket for stale frees, `StageMeshDraw` for record/absence/history,
`Observe` for occupancy, `MeshStands` for landing, and the recorded row epoch for
picks. Clip derivation is now inside the writer, as [R1c](#r1c-as-built) describes.
The table's `Live` enumeration counts whole walks; disable the diagnostic oracle
when reading production whole-walk deltas, since the oracle intentionally reads
every live row. GPU queue ordering and resource lifetime still owe the lead's
silent-invariant fix pass and windowed validation.

#### Windowed R1b verification request

Use a World started from this checkout through `puck_world_start`, document
`worlds/debug`, `windowed: true`, physical devices disabled. Run once with
`backend: directx` and once with `backend: vulkan`, then stop each through
`puck_world_stop`. Use the shared mesh store. On its named Operator attachment:

```text
world.rows check on
world.cadence off
world.wait ready 600
world.wait bakes 600
world.wait residency 600
world.counters sdf.rows.check --json
```

Run the existing tour against that same process (replace `<processId>` with
the id the start returned; use a backend-specific output directory):

```text
dotnet .claude/scratch/r1b-cli/Puck.Cli.dll sweep --attach <processId> --no-headless --no-scenarios --output .claude/scratch/r1b-tour
```

Then run the arrival and forcing window on that same attachment:

```text
debug.go stage
world.wait bakes 600
world.wait residency 600
world.counters sdf.instances clear-peaks
world.counters sdf.instances --json
world.counters sdf.rows.check --json
debug.go meadow
world.wait bakes 600
world.wait residency 600
world.rows force pack
world.wait 2
world.rows force dress-fault
world.wait 2
world.rows force skip host
world.rows force skip scene
world.rows force skip tables
world.wait 300
world.rows force limit 0
world.wait 2
host.memory collect
world.rows force limit auto
world.rows force relocate
world.wait bakes 600
world.wait residency 600
world.counters sdf.rows.check --json
world.counters sdf.instances --json
world.rows status
```

Require nonzero dress/pack/ledger/seed/device comparisons and zero mismatches;
nonzero pack/retry starts, skipped and whole answers for each consumer,
refused/returned rows and trims. Repeat after `gpu.faults lose` and require new
table whole answers and further comparisons. The device-free laws cover orphan
allocations, same-key/same-mesh reuse without an intervening absent publication,
reserve growth, holes, refusal, relocation, expired history and static multiset
packing. Windowed observations, captures and timings remain unconfirmed here.

For the hand-over burst, keep the meadow view fixed, first settle it at a lower
render scale, then request full demand while the burst is armed:

```text
world.rows check off
debug.go meadow
world.render-scale primary pin 0.5
world.wait bakes 600
world.wait residency 600
world.counters sdf.instances --json
world.screenshot .claude/scratch/r1b-hand-over.png presented 60
world.render-scale primary pin 1
world.view.pick primary 0.5 0.5
world.wait captures 60
world.wait bakes 600
world.wait residency 600
world.view.pick primary 0.5 0.5
world.counters sdf.instances --json
```

Keep only a burst whose `rows.packed.hand-over` advanced, and identify its group
from the world, not from pixels. Every presented frame must contain the group;
unaffected rows retain history, hand-over packed records equal owned changed rows,
and rebuild counts move only with a start over. If the scale change finishes
outside the capture window, repeat the burst around the next demand change;
an unexercised burst supplies no evidence. Record thread-allocated bytes and the
following frame's late-phase draws in the lead's quiet instrumentation window.

For gallery picks and canonical order:

```text
world.rows check on
debug.go portal-link
world.wait bakes 600
world.wait residency 600
world.view.pick primary 0.5 0.5
body.stop 0
body.pose 51.5 0.05 34.5 90 0 0
world.wait 30
world.view.pick primary 0.5 0.5
debug.go portal-garden
world.wait 2
view.recenter 1
body.fly -1 0 0 0 0 0 4
world.wait 150
body.stop 0
world.wait bakes 600
world.wait residency 600
world.view.pick primary 0.5 0.5
world.rows status
world.counters sdf.rows.check --json
```

Also hold a hover while playing the walkthrough's gallery-to-garden painting
crossing. A replaced table discards its pending answer; the next pick names the
surface in the destination world, and unaffected-row picks survive a hand-over.
For the two-World still comparison, start two fresh paused lockstep Worlds,
advance both through the same count-changing arrival, then take the existing
tick-exact lockstep stills at the same tick and pinned exposure. A stable locked
frame adds no start over. The capture and lifetime review belong to the lead;
this lane's finish line remains headless.

### R1c as built

The invariant is enforced by `SdfMeshRows.Writer`: callers set base draws and
ordered clips, never staged draws. `SetStaged` and the dresser's portal scratch
walk are gone. The table's private slot index is maintained by `Set` and `Free`,
and includes every base row with a frame slot, independently of provenance.
`StageBase` chooses the first clip holding that slot or the base draw itself;
the latter restores authored planes and carries even when a plane already
equalled the portal's. `SdfSlotClip.Contains` and `Carried` share slot membership
and carried identities with the rebuild oracle; the oracle independently walks
the original input list through `SdfSlotClip.Apply`.

Each clip owns its carried allocation. Membership is indexed with `GpuRowIndex`
too; a changed member list is sorted by source row and compared with source
occupancies before the run is reused. An address freed and reused before the
next publication therefore replaces its carried run too. Unchanged membership
lets a base update stage only that row and its carried copy. `SetClips` completes
membership reconciliation before the dresser's oracle; `Publish` also completes
it for direct writer callers. The guarantee stops at the writer's own code and
reflection, as the row-table guarantee does. No public operation accepts a
derived draw for an existing source row.

`WorldCreationInstances.PackTransforms` now publishes a host mesh's root
`FrameSlot`; it keeps world material coordinates through `UsesRootMaterialFrame`
so giving clipping its slot does not change its material frame. The debug
world's `portal-row-witness` combines a brass authored diamond (host) and a teal
field keel (in place) under a neutral timeline. It occupies the extra highest
population slot, body 560; existing bodies, including the rigid stand-in 44,
keep their indices. `worlds/debug/sweep.json` adds paired straddle/withdrawal
stops for both witnesses.

`SdfMeshRowsLawTests` covers all three source-owner kinds, an authored plane
already equal to the portal's, unchanged clip values, base motion under a clip,
glass accounting, overlapping and reordered clips, reslotting and source-address
reuse, including a clip whose only difference is a signed-zero bit. It checks the retained contents against `SdfSlotClip.Apply` and checks
packed records and generations through the existing upload-model device. The
large unrelated owner stays untouched: clipping adds no whole-row walk and no
over-chunk rebuild. The headless World confirms the authored population and
body pose/stop commands. It stages no draw rows and does not register
`world.rows`; row-work evidence comes from the upload-model laws. Windowed
counters, picks and appearance remain the lead's verification below.

#### Windowed R1c verification request

Start `worlds/debug` through `puck_world_start` from the merged checkout, with
`windowed: true`, once with `backend: directx` and once with `backend: vulkan`.
Use the shared store and stop each through `puck_world_stop` after its captures
complete. On that World's named Operator attachment:

```text
world.lockstep off
world.rows check on
world.cadence off
debug.go portal-link
world.wait ready 600
world.wait bakes 600
world.wait residency 600
world.counters sdf.instances --json
world.counters sdf.rows.check --json
body.stop 560
body.pose 51.5 0.05 34.5 90 0 0 560
world.wait 30
view.override camera look 54.5 1.2 33.3 51.33 1 34.5
world.wait 2
world.view.pick primary 0.5 0.5
world.screenshot .claude/scratch/r1c-host-west.png presented 60
world.wait captures 60
view.override camera look 66.2 1.2 35.7 69.37 1 34.5
world.wait 2
world.view.pick primary 0.5 0.5
world.screenshot .claude/scratch/r1c-host-east.png presented 60
world.wait captures 60
world.counters sdf.instances --json
world.counters sdf.rows.check --json
view.override camera look 54.5 1.2 33.3 51.33 1 34.5
body.pose 53 0.05 34.5 90 0 0 560
world.wait 30
world.screenshot .claude/scratch/r1c-host-whole.png presented 60
world.wait captures 60
world.counters sdf.instances --json
body.pose 54.5 0.05 32.5 90 0 0 560
body.stop 44
body.pose 51.5 0.05 34.5 90 0 0 44
world.wait 30
world.view.pick primary 0.5 0.5
world.screenshot .claude/scratch/r1c-copy-west.png presented 60
world.wait captures 60
view.override camera look 66.2 1.2 35.7 69.37 1 34.5
world.wait 2
world.view.pick primary 0.5 0.5
world.screenshot .claude/scratch/r1c-copy-east.png presented 60
world.wait captures 60
world.counters sdf.instances --json
world.counters sdf.rows.check --json
view.override camera look 54.5 1.2 33.3 51.33 1 34.5
body.pose 53 0.05 34.5 90 0 0 44
world.wait 30
world.screenshot .claude/scratch/r1c-copy-whole.png presented 60
world.wait captures 60
world.counters sdf.instances --json
world.rows status
body.pose 56 0.05 -32 0 0 0 44
view.override camera auto
debug.go portal-link
```

Use backend-specific destinations for the second run. Each capture is a
presented series, with authority advancing. Require `rows.clipped.host`,
`rows.clipped.copy` and `rows.clipped.in-place` each to advance, then each
corresponding `rows.restored` count to advance on withdrawal. Require nonzero
dress/pack/ledger/seed comparisons and zero mismatches. `world.rows status`
must name no failure. A nonadvancing owner kind means that kind's straddle was
not exercised; fix its pose from the authored glass and body before accepting
the check. Picks must identify the witness or its carried surface, not an
unrelated wall; aim the pick from the inspected geometry if the center ray
misses the narrow keel.

The west painting's wall is `wingX0` plus `canvasProud` in `paintings.puck`,
with counterpart on the east wall. The source and carried half must meet their
respective glass without a lost part, and withdrawal must leave the whole body
with no carried remnant. Inspect both sides' presented series for holes, pops
and unstable clipping. Run the new paired tour stops as part of the R1b tour
request too. Their existence supplies no appearance claim until inspected.

For production work deltas, repeat the holds and withdrawals with
`world.rows check off` and read `world.counters sdf.instances --json` before and
after each, followed by 300 unchanged ticks. Unrelated owner rows remain
standing; `rows.walked-whole` and `frames.over-chunk.rebuild` do not advance for
clipping, and the steady window adds no `rows.clipped`, `rows.restored` or
`rows.packed.portal`. Count an independently caused start over separately.
The oracle is disabled for that window because its comparison deliberately
walks all live rows. No queued timing is required by R0's crossing reading.

## R0: the counts and the rule

Every staging passes `SdfWorldTables.StageMeshRegion` and packs through
`StageChangedDraws` and the one `StageMeshDraw` door. One staging tally publishes
the packed total and its causes together: every packed record has exactly one
cause. The original R0 readings selected R1b; the counts now describe the
retained-row path and never decide simulation state or schedule work.

**The cause of a packed record.** A published start over states rebuild or
retry beside its captured serial. A publication with clipped or restored rows states portal. Those causes
apply only on the first consumption of that publication. Otherwise a row
returning live is landing, a changed mesh name is hand-over, and the rest is
pose. A cursor's conservative answer compares through the same staging door;
it does not invent a rebuild. The cursor kind remains available to callers
without a classified producer cause.

| Cause | Holds when |
|---|---|
| `rebuild` | The dresser published a canonical start over |
| `portal` | The publication changed clipped rows, their carried copies, or restored base rows |
| `retry` | A failed publication forced a start over |
| `landing` | A row returns live from absence |
| `cursor` | An unclassified conservative consumer refresh |
| `hand-over` | The record changes its mesh name, including a freed interior row |
| `pose` | The record keeps its mesh; pose, metadata or a history cut changes it |

**The counts** (in `sdf.instances`; `<cause>` is each cause
above):

| Count | Kind | Holds |
|---|---|---|
| `rows.packed.<cause>` | Total | Records packed under that cause, on either path |
| `frames.<cause>` | Total | Stagings that packed a record under that cause |
| `frames.over-chunk.<cause>` | Total | Stagings whose records under that cause number more than the threshold below |
| `rows.owned.changed` | Total | Unique addresses gained, renamed or freed inside the published extent; counted inside row writes |
| `rows.written.<owner>` | Total | Changed staged draws set through the writer, by `host`, `copy`, `in-place` or `carried`; frees remain in `rows.owned.changed` |
| `rows.clipped.<owner>` | Total | Base rows derived under a clip, by `host`, `copy` or `in-place`; an unchanged clip list and unchanged bases add none |
| `rows.restored.<owner>` | Total | Base rows revisited after their selected clip ended, by the same three owner kinds, including an already equal authored plane |
| `dress.topology-builds.<reason>` | Total | Start overs by retry, free share, locked order or operator request |
| `peak.rows.packed.<cause>` | Peak | The most records one staging packed under that cause |
| `peak.rows.planned` | Peak | The peak of names submitted in one staging |
| `peak.rows.extent` | Peak | The most rows the table held |

The scene's counters (`SdfMeshIrradiance.Counters`, source `sdf.mesh-gi`) expose
`scenes`, `static-draws`, `entries` and `prefix-bytes`, and `peak.entries` and
`peak.prefix-bytes`. Builds count their static draws and top-level entries;
each GPU export counts its prefix bytes, including an export abandoned before
publication. Re-exporting a scene counts another prefix, because it owes another
upload. An empty export still owes its one zero word.

`SdfMeshHistory.ProcessWork` takes its kinds from the existing
`WorkKindRegistry`: declaring a kind is what lists it
(`WorkKindRegistry.Declare`, and `WorkKindRegistry.DeclarePeak` for a peak).
The first read of `WorkKindRegistry.Kinds` closes the registry, so a declaration
cannot be added behind the set that reports it.

**A peak** is a per-frame high-water count: the largest value one staging (or
one scene build) published since the peaks were last cleared. `WorkCounterSet`
raises it with `WorkCounterSet.Raise` beside the total's `WorkCounterSet.Add`.
`world.counters <source-or-prefix> clear-peaks` clears the selected sources'
peaks without changing totals, as `host.frames reset` opens a frame window.
The JSON kind legend marks peaks with `peak: true`. A reading clears the peaks
at the start of its window and reads them at its end; totals are end minus start.

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
`SdfMeshStagingCauseLawTests` proves the partition over the real tables on the
CPU upload model, the topology reason sum, a landing, a publication retry and
a pose. It also exercises a replacement producer at the same serial, exactly
one chunk versus one record over it, and a settled window longer than the frame
ring. `WorkPeakLawTests` proves raising, clearing and forwarding peaks without
clearing totals. A headless World with presentation `none` stages no draws;
its zero totals do not replace the table law's nonzero witnesses.

### Windowed verification request

The lead runs these console scripts through the named World's Operator
attachment on `worlds/debug`, with physical devices disabled. Repeat on
Direct3D 12 and Vulkan. Use a fresh process for the boot window and the shared
mesh store; this does not require an empty mesh cache. Do not clear boot peaks:
they start at zero with the process, so the first frames remain in the window.

```text
world.wait ready 600
world.wait bakes 600
world.wait residency 600
world.counters sdf.instances --json
world.counters sdf.mesh-gi --json
```

For arrival, settle at the stage, then clear peaks and read the start totals
before travelling to the meadow. The end reads follow both settle reports.

```text
debug.go stage
world.wait bakes 600
world.wait residency 600
world.counters sdf.instances clear-peaks
world.counters sdf.mesh-gi clear-peaks
world.counters sdf.instances --json
world.counters sdf.mesh-gi --json
debug.go meadow
world.wait bakes 600
world.wait residency 600
world.counters sdf.instances --json
world.counters sdf.mesh-gi --json
```

For the steady window, remain at that settled meadow stop:

```text
world.counters sdf.instances clear-peaks
world.counters sdf.mesh-gi clear-peaks
world.counters sdf.instances --json
world.counters sdf.mesh-gi --json
world.wait 300
world.counters sdf.instances --json
world.counters sdf.mesh-gi --json
```

For crossing, use the gallery's same-world linked paintings. The west glass
is at the authored wall's x plus its canvas offset; the pose below holds the
Moth just in front of it, within her body's reach. This pose's straddle is a
windowed check, not a headless observation. If `frames.portal` does not advance,
the window has not exercised clipping and supplies no reading for the rule.

```text
debug.go portal-link
world.wait bakes 600
world.wait residency 600
world.counters sdf.instances clear-peaks
world.counters sdf.instances --json
body.stop 0
body.pose 51.5 0.05 34.5 90 0 0
world.wait 30
world.counters sdf.instances --json
debug.go portal-link
```

For each window, sum the seven packed-row deltas and compare with
`SdfMeshHistory.RecordsPacked`'s delta; sum the start-over reason deltas and compare
with `SdfMeshHistory.DressTopologyBuilds`' delta. Read peaks directly, never
subtract them. The steady over-chunk deltas must all be zero. Boot must show
landing; crossing must show portal staging. Apply the fixed rule above to
these readings; the headless checks select none of R1b, R1c, R2 or S2. Bakes
and residency do not wait for the trace scene's lighting convergence, so an
end read with a scene build still pending is reported as incomplete rather
than used to drop S2.

## The measurement each step lands on

Counts are the evidence; a timing is a request for the lead's quiet window
(frozen builds before and after, `host.frames` at the meadow across an arrival,
p99 and worst frame with their `produce-work`, collections and thread-allocated
bytes).

| Step | Counts that must move | Queued timing |
|---|---|---|
| R0 | [Its counts](#r0-the-counts-and-the-rule), and its own check | None: its rule decides the order |
| R1h | `gpu.mesh-hits.rejected` and `sdf.instances.targets.stale` zero across the tour on both backends; rejections non-zero under the lever | None |
| R1a | `rows.packed.landing` and `frames.over-chunk.landing` fall to the rows that name each landed mesh; `SdfMeshHistory.Seeded` per landing stops following the list's size | Cold boot's owed frames, if R0 sends it |
| R1b | `rows.packed.rebuild` and `SdfMeshHistory.DressTopologyBuilds` move only on a start over; `SdfMeshHistory.StagingPlanRecords` equals the rows gained and renamed; a hand-over's packed records equal `rows.owned.changed`; the whole-walk kind stays at zero on a hand-over frame; thread-allocated bytes of that frame; the draws the late occlusion phase emits on the following frame | The hand-over frame |
| R1c | `rows.packed.portal` falls to a straddling body's own rows and their copies | A portal crossing at the meadow, if R0 sends it |
| R2 | A hand-over's packed records equal its pieces | Decided by R0 |
| S2 | Clusters built against reused, prefix and cluster bytes uploaded per scene, time from the last landing to a settled volume | Decided by R0 |

## Steps

Each is one lane, in this order. The scene build's pooling and the decode into
a mesh's own storage (the memo's S1 and D1) are built. A check that needs a
windowed World on both backends, or a Vulkan run under GPU-assisted validation,
is a verification request to the lead.

| Step | Size | Lands | Door | Check |
|---|---|---|---|---|
| R1h. The hit door's checks | Owed | The resolver is built (`sdfMeshResolveHit`, the kernel kind `gpu.mesh-hits.rejected`, `sdf.instances.targets.stale`, `world.rows force hit-bound`). What remains is its windowed check | The resolver | Zero rejections and zero stale targets across the tour on both backends; rejections counted under the lever with no lost device on Vulkan under GPU-assisted validation |
| R1a. The staged table is the producer's | Built; windowed check owed | [The implemented doors, levers and device-free evidence](#r1a-as-built) | `StageMeshDraw`; `GpuRowCursor`; the compiler's reader-only frame | 28 focused laws pass; the cold boot's captures, device counts and owed-frame timing on both backends remain the lead's request |
| R1b. Rows | Headless implementation; windowed verification owed | The row table and its writer; runs and occupancies; the arena's row operations; identity by row for picks; claims and glass counted in the writes; the whole-walk count; the start over, used for a failed publication, the free share and a locked frame, and the canonical flag; the scene key and the diagnostic scene as sets; the dress comparison, the ledger's occupancy and the law | The writer; `SdfMeshHistory.Observe` given the occupancy | The cross-check over the tour and a meadow arrival, both backends, with its forcing counts; a presented burst across a hand-over with the group in every frame (the capture [the residency packet](open-items.md#residency-and-streaming) still owes); `world.view.pick` across a hand-over and across the gallery portal; two Worlds' lockstep stills across a count change |
| R1c. Clips are derived | Headless implementation; windowed verification owed | The table's slot index over every owner, staged draws derived from base and ordered clips, one carried run per clip; the dresser's portal walk and staged-write bypass deleted; source-confirmed host and rigid witnesses and paired tour stops | `SdfMeshRows.Writer.Set`, `SetClips`, `Free` and publication's copy reconciliation | [The exact straddle request](#windowed-r1c-verification-request): clipped and restored rows of each owner kind; both clipped halves and a whole body after withdrawal, on both backends |
| R2. Piece rows | Large | With the visibility plan's V1b | That plan's | That plan's, and a hand-over's packed records |
| S2. Clustered top level | Medium | Cells, clusters reused by equal sorted lists, cluster blocks in the arena, the prefix over cells | The cluster's constructor; the one scene publisher | The cross-check's ray set; the irradiance scene laws; a pack rebuilds no cluster; a scene change during an upload abandons without a lost device on Vulkan |

No step is a stopgap for a later one: the hit door's resolver is what R1a's
absent record is rejected by, R1a's absent record, row-for-row staging and rows
by mesh are what R1b's free rows and R2's piece rows stand on, and the scene
build's pooled arrays (built) are what a cluster keeps. Decoding into the
mesh's own storage (decision 4) is built too.

**S2 follows S1: the windowed reading reverses the offline one.** The offline
reading over the store's static meshes put one scene's prefix at a small
fraction of `SdfGpuWork.MostUploadJobBytes`. The frame's own static draws,
rigid copies included, are what the scene exports: a cold windowed debug World
prints `prefix-words` above twelve million in its `[sdf.mesh-gi] scene` lines,
several times `SdfGpuWork.MostUploadJobBytes` in one build, so the rule's first
clause holds and S2 is built after the R steps.

**The rule's reading.** Read from the seam walk's nine windows on both
backends, whose per-cause counts span an arrival (the valley door, the lift)
and a crossing (the gallery glass): no `frames.over-chunk.cursor` or `retry`
moved, so no lost cursor stands before R1a; `frames.over-chunk.landing` rose in
every arrival window, so R1a is on the arrival's path and takes the cold
boot's queued timing; `frames.over-chunk.rebuild` rose in the arrival windows,
so R1b follows R1a; the glass crossing moved no `frames.over-chunk.portal`, so
R1c follows R1b without a timing; `frames.over-chunk.hand-over` stayed at zero,
so R2 stays with the visibility plan's V1b. The order is R1h, R1a, R1b, R1c,
then S2.

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
| `SdfDressedPickMap` | Reused as a row-table reader | Resolves owner provenance with the recorded identity epoch, without a per-change map |
| `SdfSlotClip.Apply` | Replaced by a staged draw the table derives | The one unjournaled writer; its rule (the first clip whose run holds the slot) is kept |
| `GpuRowIndex` | Reused for the table's slot and selected-clip membership chains | Allocate/set/free own membership; clip changes reach every source-owner kind without a second index implementation |
| `SdfMeshRegion`'s former generic bit comparison | Moved to `Bitwise.Equal` in `Puck.Maths` | Draw, packer and clip comparisons share the same raw-representation operation; the clip value declares padding-free layout, and partial matrix comparisons remain their own field selections |
| The dresser's slot index (`SdfMeshDresser.IndexBinding`) | Kept for poses, not used for clips | It lists only bindings drawn in place, and a clip reaches host draws and copies too |
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
| The source-confirmed host-mesh and rigid witnesses straddle and withdraw correctly at the gallery glass | Lead's windowed R1c check | [The exact request](#windowed-r1c-verification-request): owner-kind clip/restore deltas, picks and presented series from both sides, both backends |
| The cell size of 3d | S2's lane | Clusters built per scene change and traversal steps per ray, both counted |
| Runs kept through live program reloads and responding placements | Lead's windowed R1b check | Compare occupancies of unaffected groups across `world.reload` and a responding placement toggle |
| Whether the row free-share policy needs tuning | Lead's windowed R1b check | `SdfMeshRows.PackingFreeDivisor` bounds the holes; compare `start-over.pack` with `peak.rows.extent` over the arrival before changing it |

---

[Plans](README.md) · [Visibility](visibility.md) · [Illumination](illumination.md) · [Open items](open-items.md#residency-and-streaming) · [Frame rendering](../rendering/sdf/handbook/frame-rendering.md)



