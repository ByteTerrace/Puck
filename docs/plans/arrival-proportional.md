# Arrival work proportional to change

The design memo for three costs an arrival still pays whole: the frame that
takes a hand-over restages every draw row, the lighting's trace scene rebuilds
its top level and its GPU export on every scene change, and a store load decodes
into managed arrays before its native copy. Each decision names its options, the
evidence, the failure modes each option closes, the favoured option, the
strongest case against it, the invariant and the one place that enforces it. It
is the design of [the visibility plan](visibility.md#v2-instance-updates-proportional-to-change-and-authored-motion-on-the-gpu)'s
"retained instance updates" (its V2b) and says what its record split (V1b) owes
the hand-over. No build lane starts before a review from another family has read
it; [its answers](#review-answers) go at the end.

Labels: **[opened]** means this memo read the symbol at this tree;
**[reading]** means a count the frame-bar lane measured, which this memo did not
measure again and whose values are in the commit that lands it;
**[inference]** means this memo's reasoning; **[unconfirmed]** means nobody has
checked it. A plan's citations are leads: recheck the symbol before building
from one.

## Summary

- **Rows keep their place.** The dresser's draw list becomes a row table: each
  owner (a bake group, the host's list, an instance's own mesh, a portal clip's
  copies) holds a run of rows from a range allocator, a free row is an absent
  record, and the staged table is the producer's table row for row. A hand-over
  that changes a group's piece or copy count frees and allocates that group's
  run and nothing else moves.
- **The change is derived, never reported.** The dresser already finds what a
  frame changed by comparing each group's shown mesh against what it dressed,
  not by trusting the bake's product revision. That scan stays the one
  derivation, and every row write goes through one door that journals it, so no
  writer has anything to remember.
- **A hand-over rewrites its pieces, not its copies,** once the record splits
  into a per-piece part and a per-instance part (the visibility plan's V1b).
  Whether that is needed for the frame bar now is a count this memo names and
  has not got.
- **The trace scene stops allocating first, then its top level becomes
  clusters.** Step S1 pools what a scene build keeps and exports the prefix
  without a second host copy; step S2 partitions the top level into spatial
  cells whose hierarchies are identified by their contents, as each geometry's
  already is, and is taken only if S1's readings leave a need.
- **The store decode writes into the storage the mesh keeps,** through one
  decoder with a storage parameter, and a restore reads only the planes it
  restores.

The order is R0 (counts), S1 and D1 (the garbage, in separate files), R1a (the
staged table is the producer's), R1b (rows), R1c (clips), then R2 or S2 by
R0's and S1's readings.

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

A new program makes the dresser visit every binding, since a kept group can be
read under another slot **[opened]**. And these walk every row or copy on
ordinary frames **[opened]**: `SdfMeshPathBake.SameCopyLayout`, the dresser's
copy comparison and its input commit, the residency's glass count, and
`SdfWorldTables.GlassMaySee`'s index refresh on every mesh revision.

### Who holds a row index

| Holder | What it keeps by row | What a shifted row costs it today |
|---|---|---|
| `SdfWorldTables` | The staged draw, its packed record, the arena's name for the row (`SdfMeshRegionArena.Rename`), the glass rows | Packed again |
| `SdfMeshHistory` | A generation per cell, moved whenever the cell's occupant, mesh or slot changes | A new generation for every shifted row |
| The previous-matrix table | Each row's last matrix, seeded from the current one on a new generation | Seeded: the row reads as still |
| Each raster pass's `SdfMeshOcclusion` | Visibility, level and generation per row, on the GPU; the kernel resets a row whose generation differs | Every shifted row leaves the remembered visible set: the early phase draws none of them that a box bounds, so the depth they are tested against holds only the rows that did not shift |
| The temporal resolve | A pixel's receiver identity, whose source is the row (`sdfHistoryReceiverMatches`) | History rejected under every shifted row |
| `SdfDressedPickMap` and `SdfWorldPicker` | Identity by row; a pick's copy is read back frames later and resolved through the map | The map is compared row by row, and rebuilt when an identity differs |
| `SdfMeshIrradiance` | A hash and a static flag per row for the scene's key | Folded whole |
| `SdfMeshIrradianceScene` | Nothing by row: its instances are its own list | Rebuilt (decision 3) |

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

Two things follow. The derivation of what changed is already a comparison
against the inputs for meshes, copies and groups, which cannot miss a writer.
The journal is kept by convention: a write and its journal entry are separate
statements at each site, and one writer has none.

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

## Decision 1: rows that keep their place

**What must be decided.** How a draw list keeps its rows when an owner's row
count changes, and what makes the incremental table equal the rebuilt one.

**Options.**

| Option | Evidence | Cost | Fails at | Closes |
|---|---|---|---|---|
| 1a. Keep the dense ordered list; pool what a rebuild allocates | The rebuild's allocations are the pick map, the key arrays and new bindings **[inference]** | Small | Every count change still packs, uploads and re-seeds every later row | None |
| 1b. A free list of single rows | The open item's lead; persistent instance ids in other engines ([sources](#sources)) | Medium | A group's rows scatter, so its changes exceed `GpuRegion.MaxCopyRuns` runs and the journal coarsens toward the whole table | F8 with a compaction rule |
| 1c. A run per owner from `GpuRangeAllocator`, free rows absent, the staged table equal to the producer's | The arena already places meshes this way, and a hand-over that keeps its counts already stays in its run | Medium to large | Row order differs from a rebuild's (F9) | F1 to F8, F10 |
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
set. The answer is below, under [determinism](#determinism-and-the-cross-check):
the invariant is stated as a set, and a frame that must repeat across Worlds
asks for the canonical packing.

### The design

**One row table, written through one door.** A new type (working name
`SdfMeshRows`) owns the rows, a liveness per row, the range allocator and the
journal. It offers three writes: allocate a run, free a run, set a row. Each
journals the rows it touches inside itself, and nothing else can reach the
storage: the dressed frame carries a read-only view whose enumeration yields
live rows with their indices and whose indexed read answers "absent" for a free
row. `SdfFrame.MeshDraws` stops being a list a consumer can index blindly; the
host's input list keeps its own type.

**A run per owner.** A bake group holds one run of its copies times its pieces;
the host's list, the instances' own meshes and each clip's copies hold runs of
their own. A run is keyed by its owner's object, and the bake keeps the group
object of every group a new program keeps, so such a group keeps its run and
its bindings. An owner whose row count changes allocates a new run, writes it,
and frees the old one; the allocator's lowest-first fit keeps the extent tight.

**The reconcile is the derivation.** Each frame, under the bake's frame gate as
today, the dresser compares each group's shown mesh, piece count and copies
against its run. The bake's copy-layout revision gates the per-group copy
comparison, so an unchanged layout costs what it costs today. Pose changes
arrive through the transform journal and host changes through the host's, with
their existing "visit everything" fallbacks. Clips name slots, and the
dresser's slot index already names the bindings on a slot, so a clip's start,
move and end touch only its own draws.

**The staged table is the producer's table.** A row whose mesh is owed or
refused is staged as an absent record at its own row, not filtered out, and a
free row is the same record. The tables keep, per mesh, the rows that name it
(an intrusive chain of the kind the dresser keeps per slot; one shared type
beside `GpuChangedRows`), so a landing stages exactly those rows. The arena is
named row by row (`SdfMeshRegionArena.Rename` and two siblings for a row
gained and a row lost), and `SdfMeshRegionArena.Plan` remains for the first
plan and a relocation.

**Absent is one bit in one kernel.** The record's selection flags
(`SdfMeshRecord.SelectionWord`) gain an absent bit; the selection kernel, the
one classification door of every raster pass, rejects it before reading
anything else and resets the row's state. The staged draw count becomes the
table's extent.

**Free rows are bounded.** When free rows inside the extent pass a share of it
(a policy constant, as `SdfMeshRegionArena.PackedShare` is for mesh words), the
table is packed: the rebuild that exists today. If `host.frames` then shows the
packing frames, packing becomes a budgeted move of the highest run into the
lowest hole a frame; it is not built first.

### The invariant, its door and where it stops

**The invariant.** At the end of every `SdfMeshDresser.Dress` that publishes,
the live rows as a multiset equal the list a rebuild from the same inputs
produces, compared by `SdfMeshDraw.SameStagedContent`. After every pack, each
row's record is the packing of its draw where the row is live and its mesh
stands, and absent otherwise; and a row's generation moved exactly when its
occupant's history identity did.

**What enforces it.** Three structures, none of them a caller's care:

- The row table's storage is private to it, so a row cannot change without its
  journal entry (F1). A failed publication journals "everything" on the next
  frame, as `SdfMeshDresser.Dress` already retries (F7).
- The tables consume rows only through the journal cursor or the whole-table
  refresh the journal demands (`GpuChangedRows.TryCollect` refusing), one
  function for both (F2). New tables, a lost device and a replaced producer are
  the refresh.
- The change itself is derived by comparison for meshes, copies and groups, so
  no writer reports it.

**Where it stops.** Poses and host draws still arrive through writer-fed
journals with their own doors; this design does not change them, and the
cross-check below covers them. Draw order is not part of the invariant.

### How the GPU side stays correct

Reusing a row replaces no GPU resource, so it retires nothing. What it creates
is a new interpretation of a row index, and each holder of one is covered by a
mechanism that exists:

- **Records.** The mesh region is the always-staged region: one device-local
  buffer that every upload copies into, a frame's or a landing job's, on one
  queue in order. A pass already submitted never sees a record staged after it.
- **Histories.** `SdfMeshHistory` never reuses a cell's generation, a retired
  cell keeps its count, and the kernel resets a row whose generation differs
  from its remembered one; a new occupant's previous matrix is seeded. This is
  the proof a reused row needs, unchanged (F3). The per-row retire replaces
  `SdfMeshHistory.RetireAfter`'s suffix.
- **Hits in a target.** A pass resolves a hit through the current records, so
  a target must have been written under them. Every record write moves the
  mesh revision, which the geometry signature folds
  (`SdfWorldTables.UpdateTablesSignature`), so the mesh pass renders before any
  reader; under this design that increment sits in the tables' one row-staging
  function. The skip rule is unchanged (`SdfWorldPassRecorder.TargetsHoldClear`),
  and a table whose rows are all absent has a non-zero extent, so its pass runs
  and clears (F4). The hit resolver trusts the triangle it is handed today;
  clamping it to the record's count there would turn any missed case, from any
  cause, into a bounded read, and is offered to the review as a hardening, not
  as this design's guarantee.
- **The temporal resolve.** A row that keeps its place keeps its history, which
  is the gain. A reused row's pixel can match history by identity only where a
  different surface also lies within the depth tolerance **[inference]**.
- **Readbacks.** A pick is delivered only when its slot is proven complete
  (`RenderGraphSlotResults`, `SdfWorldPicker.Complete`) and is discarded unless
  the map it was recorded under is still the frame's. The row table keeps the
  identity per row and the count at which each row's identity last changed; a
  pick records the count it was taken at and is discarded when its row changed
  since. No map is allocated or compared per change.
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
owner's piece and not by the mesh, a hand-over writes one piece row per piece,
and a piece row's ready bit replaces the per-mesh row chain of decision 1 for
landing. **Strongest case against:** it moves the box into the kernel, which is
GPU work in every pass against CPU work on a rare frame. R0's count decides:
the largest `SdfMeshHistory.RecordsChanged` of any frame on which
`SdfMeshHistory.DressTopologyBuilds` did not move, across a meadow arrival.

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

**Favoured: 3a now, 3d if its readings leave a need.** 3a removes what reaches
the frame thread. 3d makes the rest proportional without changing a kernel
**[inference]**:

- **A cell's cluster is a pure function of its entries.** Each top-level entry
  (one braid of one static draw) goes to the cell that holds its box's centre.
  A cluster holds its entries, a record per instance it names, and the
  hierarchy over them, and is built by one function from the entry list. A new
  scene partitions the static draws again, on its worker, and takes the
  previous scene's cluster for every cell whose list is equal, entry for entry;
  only the others build. The scan stays the size of the static scene, with no
  allocation; the builds, the claims and the upload follow the changed cells.
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

**The invariant.** A cluster is reused only for an entry list equal to the one
it was built from; the type offers no mutation and one constructor, so a reused
cluster is the rebuilt one (F12). A cluster's block on the GPU also holds its
meshes' placement words, so the block is kept only while those placements
stand, checked per cluster as `SdfMeshIrradianceScene.MatchesGpuPlacements`
checks the whole scene today. The top's depth and a
cluster's depth together stay within the trace's stack bound, enforced where
the hierarchy build certifies its depth today.

**Strongest case against 3d.** It is built for a need nobody has measured: if
the static scene is a few thousand draws, 3a's rebuild is a few milliseconds on
a worker and a small upload, and 3d is structure without a reading. That is why
S2 waits for S1's readings.

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
into owned scratch, as its inflated planes already are. A restore passes a storage that accepts
only normals and level indices and skips every other plane by its stated
length. A chunked mesh needs its pieces' counts before the first plane to size
the attributes its chunks share, which the entry's layout gains.

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
rebuild's.

- **Equal as a set.** Rows are compared with a rebuild as a multiset, and a
  scene's hits with a rebuilt scene's by distance, bit for bit, with the hit's
  identity allowed to differ only where the other triangle lies at the same
  distance.
- **The cross-check is a lever, run over a tour.** With it on, each dress also
  runs the rebuild into scratch and compares; each pack also packs every live
  row from nothing and compares it with the region's host copy; each scene
  build also builds whole and compares a fixed ray set. It counts rows, records
  and rays compared and mismatches, and names the first differing owner. A
  step's check is the walkthrough tour and a meadow arrival on both backends
  with zero mismatches, a non-zero compared count, and non-zero counts of the
  events it guards (products handed, runs reallocated, landings, clips,
  clusters reused), so a tour that exercised nothing cannot pass.
- **A device-free law for the row table.** Random edits of products, copies,
  clips and programs against the rebuild, on the CPU device: the silent classes
  stay review-only until a check covers them, and this one outlives the next
  rewrite of the dresser.
- **Canonical on request.** A frame that must repeat across Worlds (a lockstep
  capture, `SdfFrame.TickLocked`) packs the table first, which is the rebuilt
  order. Its check is the lockstep byte comparison of two fresh Worlds at one
  tick that the rendering skill already describes.

## The measurement each step lands on

Counts are the evidence; a timing is a request for the lead's quiet window
(frozen builds before and after, `host.frames` at the meadow across an arrival,
p99 and worst frame with their `produce-work`, collections and thread-allocated
bytes).

| Step | Counts that must move | Queued timing |
|---|---|---|
| R0 | New: rebuilds by cause, rows owned by the changed owners against rows restaged, whole-list packs by cause (rebuild, landing filter, portal path), the largest changed-record count of a frame without a rebuild; the scene's static draws, entries, prefix bytes and builds per arrival | None: it decides R2 and S2 |
| S1 | Allocation trace across an arrival: the scene constructor, the export and the scene start's snapshot near zero; collections in `host.frames` | Meadow arrival p99 and worst frame |
| D1 | Allocation trace: the decode and the managed renumbering near zero; restores read fewer planes | The same window |
| R1a | A landing packs the rows that name its mesh: `SdfMeshHistory.RecordsPacked` and `SdfMeshHistory.Seeded` per landing stop following the list's size | Cold boot's owed frames |
| R1b | `SdfMeshHistory.StagingPlanRecords` and `SdfMeshHistory.DressTopologyBuilds` move only on a packing; a hand-over's packed records equal its owner's rows; thread-allocated bytes of that frame; the late phase's draws on the frame after | The hand-over frame |
| R1c | A straddling body's frames compare and plan its own rows | A portal crossing at the meadow |
| R2 | A hand-over's changed records equal its pieces | Decided by R0 |
| S2 | Clusters built against reused, prefix and cluster bytes uploaded per scene, time from the last landing to a settled volume | Decided by S1's readings |

## Steps

Each is one lane, in this order; S1 and D1 share no file with the R steps and
run beside them.

| Step | Size | Lands | Door | Check |
|---|---|---|---|---|
| R0. Count | Small | The counts above, in `sdf.instances` and the scene's counters | None | The lead reads them across a meadow arrival and a cold boot |
| S1. A scene build keeps what it allocates | Small | Pooled scene arrays returned at the last claim; a static-only snapshot; the prefix exported on demand | The export still has one publisher | The exported words equal the old export's for one scene (a change that claims to change nothing); the allocation trace |
| D1. Decode into the mesh's storage | Medium | The storage parameter, material ids applied on read, the restore's plane filter, piece counts in the entry | One decoder | The codec's round trip for both storages; a warm boot loads every group; a relocation restores |
| R1a. The staged table is the producer's | Medium | The absent bit and its kernel branch, rows per mesh, a landing staging its rows, the journal valid while meshes are owed | The tables' one row-staging function | The cross-check over a cold boot; a boot's captures fill in as before; `world.wait residency` still names what is owed |
| R1b. Rows | Large | The row table and its door, runs per owner, the per-row arena names and claims, identity by row for picks, live-row enumeration in every consumer, the packing rule, the lever and the law, canonical packing for a lockstep capture | The row table | The cross-check over the tour and a meadow arrival, both backends; a presented burst across a hand-over with the group in every frame (the capture [the residency packet](open-items.md#residency-and-streaming) still owes); `world.view.pick` across a hand-over; two Worlds' lockstep stills |
| R1c. Clips are rows | Small to medium | A clip's copies as a run; the portal path's "everything" deleted | The row table | The cross-check through a portal crossing; a straddling body clipped on both sides |
| R2. Piece rows | Large | With the visibility plan's V1b | That plan's | That plan's, and a hand-over's changed records |
| S2. Clustered top level | Medium | Cells, clusters reused by equal lists, cluster blocks in the arena, the prefix over cells | The cluster's constructor; the one scene publisher | The cross-check's ray set; the irradiance scene laws; a scene change during an upload abandons without a lost device on Vulkan |

No step is a stopgap for a later one: R1a's absent record and row-for-row
staging are what R1b's free rows stand on, and S1's pooled arrays are what a
cluster keeps.

## Systems considered

| System | Verdict | Why |
|---|---|---|
| `GpuChangedRows` and `GpuUploadRuns` (the draw journal) | Reused | Captured-endpoint cursors per consumer and a conservative refusal are exactly F2's answer; rows that keep their place make "everything" rare instead of routine |
| `GpuRangeAllocator` | Reused | Already the allocator of the mesh arena, the probe scene's arena and both backends' memory pools; rows are a fourth range space |
| `SdfMeshRegionArena` | Kept, renamed by row | Placement, slack and relocation stand; the list walk in `SdfMeshRegionArena.Plan` is what a rebuild paid |
| `SdfMeshHistory` | Reused unchanged in meaning | Generations that are never reused per cell are the proof a reused row needs |
| `SdfWorldTables` changed-run staging (`SdfWorldTables.StageChangedDraws`) | Becomes the only staging | The whole-list branch is its "everything" case, not a second path |
| The landing filter | Replaced | It is why staged rows differ from the producer's |
| `GpuRetirementQueue` and `SdfWorldTables.Retire` | Reused where a buffer is replaced | Row reuse replaces none |
| The slot proof (`RenderGraphSlotResults`, `SdfMeshOcclusion.Completed`) | Reused | A readback carrying a row is delivered only at its slot's completion; the row's change count decides whether it is still true |
| The specialization's retired builds (the residency's retired shade sets and their pins) | Considered, not reused | The same idea, a superseded product kept until nothing pins it, is what scene and cluster claims already do with their own counts |
| `SdfMeshClaims` | Reused | `SdfMeshClaims.Move` per row, with a claim and a release for a row gained and lost |
| `SdfDressedPickMap` | Replaced by the row table's identities | It is rebuilt and compared whole |
| `SdfSlotClip.Apply` | Replaced | The one unjournaled writer |
| The bake's product revision | Rejected as a change signal | Eight writers move it by hand; the dresser's comparison needs none |
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

- **R0 shows the bad frames are not rebuilds.** If the frames past the bar have
  a large changed-record count and no rebuild, R2 moves ahead of R1b.
- **R0 shows a few thousand static draws.** S2 is dropped and 3a is the whole
  answer for the scene.
- **The packing frames show in `host.frames`.** The budgeted move replaces the
  whole packing.
- **The absent lanes show in a raster pass's GPU time.** The packing share is
  lowered; the visibility plan's ordered compaction (V1a) removes the cost at
  its cause.
- **The review finds a holder of a row index this memo missed.** That holder
  gets the generation or the change count before R1b lands.

## Open questions and their owners

| Question | Owner | What decides it |
|---|---|---|
| Which changes the arrival's rebuilds were, and how many rows the changed owners held | R0, read by the lead | The counts by cause |
| The scene's static draws and entries at the meadow, and builds per arrival | R0 | The scene's counters and its log line |
| Whether the hit resolver clamps its triangle | The review of R1a | A reviewer's judgement of the class, then a Vulkan run under GPU-assisted validation |
| The cell size of 3d | S2's lane | Clusters built per scene change and traversal steps per ray, both counted |
| How many runs a program rebuild reallocates | R1b's lane | Runs kept across `world.reload` and a responding placement's toggle |

## Review answers

No review has read this memo yet. Each finding is answered here, one by one,
with the section it changed.

---

[Plans](README.md) · [Visibility](visibility.md) · [Graphics architecture](graphics-architecture.md) · [Open items](open-items.md#residency-and-streaming) · [Frame rendering](../rendering/sdf/handbook/frame-rendering.md)
