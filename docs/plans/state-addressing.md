# State addressing on the tick path

Every rule operand and every state effect in a world tick resolves its cell
through several layers that were built for correctness first: a name-keyed
row lookup, a string key parsed on each read, and a linear scan over the row's
authored cells. This plan removes those layers from the tick path while
keeping compiled rules independent of frame memory layout, keeping the
document-backed store as a fallback, and keeping every state hash
bit-identical. It is ordered so that the cheap, local changes land first and
the two structural changes wait for a profile that says they are worth it.

## Implementation status

Reviewed against `95e5c8c0a`. Nothing has landed. The facts below were read
from the code at that commit; recheck them before scheduling a stage.

## What the tick path does today

A rule operand such as `health.$value` or `discs.1` reaches its value through
[RuleEvaluation.ReadStateFact](../../src/Puck.State/RuleEvaluation.cs), which
calls [StateReader.TryReadHandle](../../src/Puck.State/StateReader.cs) with a
`StateHandle` the compiler minted, then `StateReader.ReadCell`, then the
store's `TryStored`. During `WorldServer.EvaluateWorldRules` the store is a
[StateFrame](../../src/Puck.State/StateFrame.cs), a flat `long[]` laid out
once per section by `FrameLayout`. Outside that window the store is a
`RowStore` over the installed document.

On the frame, one read costs:

1. **A name-keyed row lookup the handle already answers.** `TryStoredCore`
   hashes `row.Name.Value` through `Layout.TryOrdinal` to recover a row
   ordinal, but the handle's descriptor already carries `LaneOrdinal`, and the
   frame's row ordinals are the document lane's ordinals by construction
   (the comment beside `IRuleReader.TryRowVersion` in
   [WorldServer.RuleHost.cs](../../src/Puck.World.Server/WorldServer.RuleHost.cs)
   says so). `TryWrite` and `TryStoredAt` repeat the same lookup.
2. **A scan over authored cells that most reads do not need.** `ReadCell`
   asks the metadata overload of `TryStored` so it can hand
   `StateReader.Live` a `StateCell` for its advance and cycle traits.
   `TryStoredCore` therefore runs `IndexOf(row.Cells, key)`, a linear scan
   comparing `CellName` values, before the row-kind switch, for slot, board,
   ring, and zone rows alike. Keyed rows need that index for the value itself.
3. **A string key parsed on every read.** `RuleEvaluation.ResolveKey` returns
   a string, `ReadCell` runs `CellName.TryParse` on it, and a board row then
   runs `int.TryParse` on it again inside `CompiledTopology.TryCell`. A
   literal key was already validated when the rule compiled.
4. **A name-keyed find per `forEach` rule.** `RuleEvaluator.EachKeys` resolves
   the iterated row through `Store.Find(name)`, a linear scan by name, once per
   `forEach` rule per tick, and publishes the bound key as a string that each
   `$each` read then parses and scans for again.
5. **A whole-frame reload before any rule runs.** `LoadRuleFrame` in
   [WorldServer.RuleFrame.cs](../../src/Puck.World.Server/WorldServer.RuleFrame.cs)
   calls `StateFrame.Load` against the document every tick. `Load` reads every
   framed row cell by cell through `RowStore`, copies each row's previous
   values into a scratch span, and compares the two so a row's version only
   moves when its content did. That is proportional to the section's total
   cell count whether or not anything changed.

The earlier proposal to store a frame offset on each compiled operand is not
viable and is recorded here so it is not proposed again. A keyed-cell mint or
removal changes a row's frame length, fails `FrameLayout.Fits`, and rebuilds
the layout mid-tick through `ReloadRuleFrame`; a checkpoint restore mints a
fresh catalog and layout with identical rows. Rules compile once against the
catalog and never against a layout, so an offset held by an operand would go
stale with nothing to notice.

## Constraints every stage keeps

- **Bit-identical state.** Each stage is a representation change. The state
  hash of any world at any tick is the same before and after, or the stage is
  wrong. Determinism pins the mapping from document and input to state, and
  none of this work changes that mapping.
- **Rules stay layout-blind.** A compiled rule knows the catalog it was
  compiled against and nothing about how a frame lays rows out. Anything that
  depends on a layout lives in the layout and is rebuilt with it.
- **The document store keeps answering.** Every read a frame answers must have
  an answer through `RowStore`, because readers outside the tick's own rule
  evaluation see the installed document, never a frame.
- **Absence semantics are part of the contract.** A board cell whose frame
  value equals the row's `Empty` reads present only when the row authored it;
  a declared keyed cell the row does not hold reads as integer zero; an empty
  zone endpoint reads as absent. These answers do not move.
- **No parallel doors.** When a tick-path caller moves to an ordinal or handle
  entrance, the name-keyed call it replaces is deleted from that caller. The
  name-keyed entrance itself stays for console read-backs, presentation
  bindings, and validators, which run off-tick.

## Stage 0: restore the measurement

`puck bench world` refuses the shipped world at admission because
`WorldBenchServer.Boot` in
[WorldBenchHarness.cs](../../src/Puck.Cli/Bench/WorldBenchHarness.cs)
constructs its machine host with no engines. `WorldBenchmarks` already calls
`CliWorldVocabulary.EnsureInstalled()`, which returns a `WorldMachineCatalog`
with both brick engines registered, and then discards it. `WorldMachineHost`
has a constructor that takes a catalog. The fix is to thread that catalog into
`Boot`.

With the bench running, profile one steady-state tick of `puck.world.json`
with a sampling profiler such as `dotnet-trace` attached to the bench process.
Do not add timers to `EvaluateWorldRules` or the state library; they perturb
the measurement and leave code on the tick path. Record, in the commit that
lands the fix:

- the idle-tick median and quiet-tick allocation the bench already reports;
- the share of tick time under `StateFrame.Load`, `StateFrame.TryStoredCore`,
  `StateFrame.TryWrite`, key parsing, and rule dispatch;
- the count of compiled operands that address a fixed row with a literal key
  against those that resolve a key or a row at evaluation time.

The two profile-gated stages below are scheduled from these numbers, and the
`puck bench world` open item in [Open plan items](open-items.md) closes with
this stage.

## Stage 1: remove the redundant work the handle already paid for

This stage needs no catalog or lifecycle change and lands as one commit.

**Ordinal entrances on the store.** Add `TryStored(int rowOrdinal, …)`,
`TryStoredAt(int rowOrdinal, …)`, `TryKeyAt(int rowOrdinal, …)`, and
`CellCount(int rowOrdinal)` to `StateStore`. `StateFrame` indexes
`Layout[rowOrdinal]` directly; `RowStore` indexes `Rows[rowOrdinal]`.
Add ordinal writes on `StateFrame` too. Route
`StateReader.TryReadHandle`, `IRuleHost.TryApply`, and the reduction and
board readers that already hold a descriptor through them using
`descriptor.LaneOrdinal`. The `string.Equals` check between the resolved row's
name and the descriptor's name stays: it is the invariant that the ordinal
still names the row the handle was minted for.

Carry the resolved ordinal through identity-lane synchronization, filter-key
indexing, and row-version checks too. Both push spellings retain their compiled
row handle through frame application; the serialized transform retains its
row name, checked against that handle before the frame changes.
Use `StateReader.TryResolveRowHandle` for the shared catalog ownership, ordinal,
and current row-name checks. Compile-time push handles use `ResolveHandle` so
a missing catalog entry fails compilation. A compiled write or push whose
handle names a different destination fails evaluation as an invariant violation.
The internal row-name lookups in `clearEnclosed`, `writeSet`, and `boardCombine`
remain outside Stage 1.

**Skip the authored-cell scan when no trait needs it.** Record on
`FrameRowLayout` whether the row or any of its cells declares `Advance` or
`Cycle`. For slot, ring, and zone rows without one, `TryStoredCore` skips
`AuthoredIndex()` and hands `Live` a null cell. For a board row it skips the
scan only when the frame value is not the row's `Empty`; a cell at `Empty`
still needs the authored check to answer presence. Keyed rows keep the scan
because the index is the value's address; the cell they find is free.

**Parse literal keys once.** `StateCellOperand`, `CompiledCellRef`, and the
write effects carry a `CellName` for a literal key beside the string the
refusal text uses, minted by the compiler that already validated it.
Literal reads and writes use that parsed name directly. Indirection still reads
the source cell's value to resolve the destination key.

**Bind the `forEach` index beside the key.** `RuleEvaluator` already tracks
`BoundEachPosition`; expose it through `IRuleReader`, and let a `$each` read
whose row is the iterated row answer through `TryStoredAt` with that index
instead of parsing the key and scanning for it, after `TryKeyAt` proves that
the position still names the snapshotted key. A removal or reorder falls back
to reading by key. Resolve the iterated row for
`EachKeys` through a handle compiled with the rule rather than `Store.Find`.
An omitted optional handle still resolves the row through the current catalog.
`ReadTupleWord` in [Operands.cs](../../src/Puck.State/Operands.cs) walks a
zone by index when it binds `$token` and `$previous`; publish those indices
the same way so a token's value expression reads by position.

## Stage 2: intern static cell addresses, resolved per layout

Scheduled only if the Stage 0 profile shows keyed-row reads or writes carry a
visible share of the tick after Stage 1. Under Stage 1 a keyed read still
scans the row's cells with string comparisons.

The compiled program, not the catalog, owns a table of every literal
`(row handle, key)` pair its operands and effects spell. `StateCatalog` is
compiled from the section before any rule compiles, and it must stay an
immutable product of the section, so it cannot mint these. `CompileAll`
returns the table beside the rules; each `StateCellOperand` and write effect
carries its entry's ordinal as a `StateCellHandle`. The descriptor behind a
handle carries the row handle and the key and nothing else. An intra-row
index is a layout fact and does not belong there.

`FrameLayout` is constructed against the catalog and that table and computes
`int[]` mapping each cell handle to a frame index: the slot offset, the
keyed cell's list position, or the topology cell for a board, and `-1` for a
key the row does not currently hold or an unframed row. A layout rebuild
recomputes the array, so a keyed mint, a removal, or a checkpoint restore can
never leave a stale index anywhere. A literal key on a keyed row that has not
been minted yet maps to `-1` until the layout that follows the mint resolves
it. `EnsureRuleFrame` rebuilds when the catalog or the table identity
changes, in addition to its existing `Fits` check.

A frame read through a cell handle is one array index plus the existing
absence rule for boards. `RowStore` answers a cell handle by looking up its
descriptor's row and key, so nothing off-tick changes.

## Stage 3: reload only rows that changed

Scheduled only if the Stage 0 profile shows `StateFrame.Load` carries a
visible share of the tick.

A `StateRow` is a record and its `Cells` is an `IReadOnlyList`; every
subsystem that mutates the installed document between ticks does so by
installing a definition whose changed rows are new instances. `Load` can
therefore skip any row whose reference is unchanged since the last load. That
is one reference comparison per row, with no change tracking across the fold,
external mutations, or cross-row reloads, and the version semantics are
unchanged because an unchanged row cannot have bumped a version today either.

Two conditions must be proven before this lands. First, run `puck references`
over every writer of `StateRow.Cells` and confirm none mutates a list in
place; a shared mutable list would defeat the reference check. Second, a zone
row's frame content also depends on its domain row, so a zone reloads when
either reference changed. The full `Load` stays for the cross-row reload path
and for a layout rebuild.

The naive alternative, keeping the frame live across ticks, is excluded. The
frame is scoped to `EvaluateWorldRules` so every other subsystem sees the
committed document; a live frame would drift unless every mutation in the
server were intercepted.

## Verification for every stage

The garden and frozen-island replay fixtures that earlier state work used as
its hash gate are retired; their rules now live in `puck.world.json`. The
live gates are:

- `tests/Puck.State.Tests`, in particular `RuleEvaluatorLawTests`,
  `RowVersionSchedulingLawTests`, `ZoneFrameLawTests`,
  `WriteSetFrameLawTests`, and `LiveZoneLawTests`.
- `tests/Puck.World.Tests`, in particular `WorldRuleFrameFoldLawTests`,
  `HandleTickPathLawTests` (which guards the shipped world's idle tick against
  added allocation), and `WorldRuleCompilerAdversarialLawTests`.
- `world.state.hash` read from a headless boot of `puck.world.json` at fixed
  ticks, taken before and after the change on the same base commit and
  recorded in the commit message. The ticks 31 and 151 already used by the
  island milestone are sufficient. A differing hash means the stage changed
  behavior and is wrong; there is no deliberate correction in this plan that
  could excuse one.
- `puck canary tabletop-state state-cycle-trait`, whose scripts exercise
  keyed, board, zone, and cycling-trait reads and end in `replay.verify`.
- `puck landing`, which runs the automatic canary set, and `puck parity`.
- `puck bench world`, before and after, with the numbers in the commit
  message. A stage that shows no improvement is reverted rather than kept as
  tidiness.

## Deliberately excluded

- A frame offset stored on a compiled operand, for the reason given above.
- A direct read on the frame that bypasses the board absence rule.
- A compile-time split of `Live`'s trait branch; the branch is four null
  checks and the cost was producing its argument.
- A cache keyed on layout identity held anywhere outside the layout.
- Any change to what a state hash covers.

## Open decisions

- Whether `TryReadHandle`'s two entrances (rows and store) collapse into one
  once the store carries an ordinal entrance. The rows form serves
  `WorldStateReader` off-tick and can likely become a `RowStore` call.
- Whether Stage 2's table is worth carrying for `RowStore` reads at all, or
  whether an operand with a cell handle should fall back to its row handle and
  key string when the store is not a frame.
