# The `state` section

Part of [`puck.world.definition.v1`](documents.md). Field names, `kind`/`domain`
enums, and numeric ceilings are generated (`puck schema`, or
`Assets/worlds/schema/state.schema.json`); this file is the decision/derivation
prose the schema cannot state. `rules` — the primitive that reads and writes
these rows — is [documents-rules.md](documents-rules.md).

`state` (`WorldStateSection`, `Puck.World.Schema/WorldState.cs`) is the
document's abstract state inventory. It has three ownership lanes:
`world` holds mutation-addressable document cells; `body` holds ephemeral
per-body counters and timers; `identity` holds the same compact slot vocabulary
but synchronizes it through the durable identity-document seam. Body and
identity names share one world-wide namespace and are compiled once into each
body's bounded ordinal arrays — declarations never live under individual
actions, and the runtime never performs document lookups on the action hot path.
The lane is the lifetime declaration; there is no second `lifetime` field to
contradict it.

`StateCatalog` (`Puck.State/StateCatalog.cs`) is the typed compiled view of
that inventory. It assigns catalog-bound `StateHandle` values in
world → body → identity document order and records each declaration's ownership
lane, slot/keyed/lattice storage shape, deterministic value kind, and lane-local
ordinal. Runtime processors resolve `(lane, name)` once, retain the handle while
that catalog is current, and index the immutable descriptor catalog thereafter;
a replacement declaration shape rejects old handles, and the catalog neither
owns values nor changes their lane-specific storage. `WorldDefinition.StateCatalog`
is non-serialized; definitions sharing `StateRaw` share its compiled view, and
`WithWorldState` preserves that view and its handles across value-only updates.

`state.world` (`WorldStateRow`) is genre-neutral game state — score, rounds,
inventory, flags, semantic memories, and vector embeddings. **A slot is a table with one
key, and there is ONE authored spelling for both.** A row names itself,
declares its `kind` (`Int`, `Fixed`, `Bool`, `Text`, or `Vector`), and carries EITHER a bare `value` — sugar for the one
cell keyed `WorldStateRow.SlotKey` (`"$value"`) — OR a `cells` array of
author-keyed `{"key","value"}` objects. Two optional fields, never two
discriminators: a row carrying both, or a `value` beside a `capacity`
(declaring a capacity is declaring keyed-row intent), refuses by name.
Omitting both is a declared-but-empty row. When `kind` is `Vector`, the row references
a declared embedding space in `state.spaces` (`spaces { space <name> { ... } }`).
Text tables can declare `embeds(vectorRow)` to automatically link text entries to companion vector rows.

Runtime rule operands compile world-row names to catalog-bound handles. Keyed
reads return stored values and authored behavior metadata together. Reductions
share one accumulator; sparse filters build a read-local ordinal index, and dense
frame filters retain topology key lookup. Unfiltered count stays constant-time. Rule numeric literals are exact JSON
decimal values (not binary32); integer values beyond 2^24 retain their low bits,
and fixed literals lower through the invariant Q48.16 parser. Contiguous state
effects in one rule are preflighted as one candidate and apply atomically. A
value-only `UpsertStateCell` uses targeted cell validation/install; declaration
changes still use whole-document validation. `WorldRuleCompilation` carries that
validation's rules, interactions, and tables directly into mutation/reload install
for the exact unchanged definition; derived-board recomposition invalidates the
receipt when it produces another definition. Do not cache programs by catalog
shape or share evaluator latches with the compilation result.

An advancing trait's compiled rational lives in a weak external cache, never in
record equality. A cell's `clock.y0`/`v0` are always raw Q48.16 continuous-state
bits, even for an integer target. `clock.substepTicks` sits in
`[0,ticksPerStep)` so reload preserves the next transition, not only the value
visible at the save tick.

`world.state.hash` defaults to the historical `capture` digest and accepts an
explicit `capture|pose|world|authoritative` scope. `capture` remains the manifest
digest (pose plus resolved `state.world` values); `pose` is the replay pose fold;
`world` includes stored state traits plus resolved values; `authoritative` adds
the tick, poses, rule/interaction edge latches, per-body body/identity action
registers, and live field-lattice cells. Use the named authoritative scope when
future-decision state, rather than manifest compatibility, is the assertion.

```json
"state": {
  "spaces": [{"name":"lore","model":"puck-fixture","revision":"1","dimensions":256}],
  "world": [{"name":"score","kind":"Int","value":0,"min":0,"max":1000}],
  "body": [{"name":"jumpUses","kind":"Counter","initial":0,"resetFact":"Grounded"}],
  "identity": [{"name":"stance","kind":"Counter","initial":0,"playerWritable":true,
    "envelope":{"$type":"set","values":[0,1,2]}}]
}
```

As `.puck` — `state.body`/`state.identity` have no dedicated sugar beyond the
shared block/array/property grammar (the same generic form `looks`/`views`
use); `envelope`'s `$type` union prints through the shared call-form escape
hatch. `state.world` additionally admits `table`/`slot`/`pile`/`grid`
declarations — see
[`Puck.World.Transpiler/README.md`](../../../../src/Puck.World.Transpiler/README.md#state-declarations)
for the exact grammar, lowering, and refusals (`pile` lowers to a `keysOf`
domain with `ordered` set over another row's keys; `grid` mints a
`state.lattices` Grid topology and a `cellsOf` row over it in one
declaration — see [documents.md](documents.md)'s `state.lattices` section and
the top-level skill's tabletop-primitive notes for the shape either spells
explicitly); `puck-dsl`'s
[`grammar.md`](../../puck-dsl/references/grammar.md#row-declarations-tableslotpilegrid)
owns the schema-agnostic parse the core supplies it through. A `state.world`
row is either the array/block-of-explicit-rows form below, or `table`/`slot`/
`pile`/`grid` sugar, never mixed within the same section:

```
state {
    world [
        { name: "score" kind: "Int" value: 0 min: 0 max: 1000 }
    ]
    body [
        { name: "jumpUses" kind: "Counter" initial: 0 resetFact: "Grounded" }
    ]
    identity [
        {
            name: "stance"
            kind: "Counter"
            initial: 0
            playerWritable: true
            envelope: set(values: [0, 1, 2])
        }
    ]
}
```

or, the same `score` row as `state.world` declaration sugar:

```
state {
    world {
        slot score = 0 bounds(minimum: 0, maximum: 1000)
    }
}
```

`puck compile` (structural, no `--validate`) round-trips either `state.world`
spelling byte-for-shape back to the JSON above; the decompiler always emits
the declaration-block spelling, printing a row as `table`/`slot`/`pile`/`grid`
sugar only when the whole row (and, for `grid`, its `state.lattices` topology)
is representable without loss and falling back to `row { }` (the explicit
form) otherwise.

There is **no `$type`** and no `rows` member; both refuse as unmapped members.
`kind` is `Int`|`Fixed`|`Bool`|`Text`; never float, the determinism
contract. A `Fixed` value (`value`, `min`, `max`, or a cell's own `value`) is
a **DECIMAL STRING** through `FixedQ4816.TryParse`/`ToString`, never the raw
Q48.16 bit pattern — only the per-cell mutation wire (`UpsertStateCell`) and
the addon ABI channel stay raw. `min`/`max` are each INDEPENDENTLY optional on
a numeric row — a one-sided range (a floor with no ceiling, or the reverse) is
legal; when both are present `min` must be less than `max`, and every cell
must fall inside — the range a HUD gauge bound to `state.<row>` or
`state.<row>.<key>` reads (see [hud.md](hud.md)). `overflow` (`"Refuse"`, the
default, or `"Saturate"`) says what a write that would leave that range — or a
genuine 64-bit arithmetic overflow, even on a row declaring no range at all —
does: refuse it by name, or clamp it to the crossed bound (or to the storage
limit on a side with no declared bound). `StateRow.TryAdmitWrite` is the one
method every write path decides through, and it is what the row-shape's own
`writeSet`/`boardCombine`/ring-push arms and the `UpsertStateCell` compose arm
all call. Both `min`/`max` and `overflow` are legitimate only on `Int`/`Fixed`
rows. The row `name` and every
cell `key` are `CellName` (`Puck.State/SafeName.cs`) — a
validated type that cannot hold an empty, unsafe, DOTTED or backquoted value,
refused at JSON parse naming the character; the dot-free rule is what makes
`state.<row>.<key>` parse unambiguously (the engine-minted `"$value"` slot
key is the one reserved exception), and the backquote-free rule lets an
expression write every row and key in its escape-free backquotes. Declaring `min: 0` is a per-row floor ANY
numeric row may declare; `int` + `min: 0` IS a timer, never
a fifth kind, and the cross-document write-back channel
(`Server.WorldOwnedWorlds.Decide`) reads that same row envelope through
`TryAdmitWrite` rather than assuming a floor of its own. Capped at `StateCapacity.MaxRows` (1,024)
rows, `MaxCellsPerRow` (4,096) cells per row (which an authored `capacity` may
only NARROW, never widen; a slot or keys row authoring none gets `DefaultCellRoom`, 128), and
`MaxTextValueLength` (1,024) text UTF-16 code units, refused by name past any. The rows
together are bounded in bytes, not cells: `StateArena.Bytes` against
`ArenaCapacity.MaxBytes` (64 MiB); `world.state` prints both. When validating a
document without building its arena, combine `ArenaLayout.Bytes` with
`StateArena.TryMeasureVisibility`; layout alone omits the bounded declaration
and live visibility payload. Keep runtime writes, imports, journal retention,
rewind and relayout on the same visibility charge, including `ReadersFrom`.

A keyed row may set `evicts: true` to trade its ordinary refuse-on-overflow
capacity ceiling for FIFO drop-oldest: an `UpsertStateCell` write that mints a
brand-new key past `capacity` succeeds and evicts the row's OLDEST surviving
cell instead of refusing (in-place rewrites of an existing key never grow the
row, so they can never trigger it, and never move that key's age — true
insertion-order FIFO, not LRU). Requires a declared `capacity` — refused by
name without one, which also covers a slot row, since a slot never declares
one. The eviction itself belongs to `StateArena`'s mint (`Puck.State`), and
both doors reach it: `WorldServer.TryCompose`'s `UpsertStateCell` arm composes
through an arena over the running world's own document (so a live write and
every `world.undo` journal re-composition reproduce the identical victim; the
dropped key is named on that write's `[world.mutation: …]` echo,
`"(evicted '<key>')"` — never a silent drop), and
`WorldIdentity.TryAppendEvictingText` composes through an arena over the owned
identity's document, outside the ordered mutation domain (a self-authored `chat.log`, or a cross-document
`chat.whisper` landing in a bounded inbox — see `authority.md`'s `ChatCommandModule` entry)
— one composition, never two readings of the eviction rule.

**A row's trait is the default for every cell it carries, including a key a
later write mints.** A row may declare `advance` (`StateAdvance`,
`perSecondNumerator`/`perSecondDenominator`), `dynamics` (`StateDynamics`, `{row}`), or
`cycle` (`StateCycle`, `{word?, power, output, ticksPerStep}`) — at most one,
legitimate on int/fixed cells whatever the row's domain (slot or keyed), never
beside `draw`. A cell replaces its row's default WHOLESALE with its own
`advance`/`dynamics`/`cycle` (never two of the three; never on the reserved
slot key, since a slot's one cell has no separate default to override), or
opts out entirely with `"behavior": "None"` (refused beside its own trait, and
on the slot key). `EffectiveBehavior.Resolve(row, cell)` is the ONE place
every consumer — readers, the validator, rebase/settle, JSON conversion, save
capture — decides which trait, if any, governs a cell; `world.state` and the
HUD read through it the identical way.

Timing state — the epoch, a dynamics follower's sampled position/velocity, a
cycle's carried substep remainder — lives on the CELL, in `clock`
(`StateCellClock`, `{epochTick?, epochEngineTick?, y0?, v0?, substepTicks?}`,
every field optional and defaulting to zero), never on the trait: a key a
write mints later starts its own clock from the tick (and engine tick) it was
created, and a slot row's timing state is authored as a `clock` object beside
its bare `value` sugar. The two epochs are independent clocks, never one
coordinate under two names: `epochTick` is a simulation tick, read by
`dynamics`/`cycle`; `epochEngineTick` is an engine tick (50,400/second,
`FixedTickConversion.TicksPerSecond`, wall-clock-uniform regardless of the
world's own `simulation.rateHz`), read only by `advance`.
`y0`/`v0` ride the SAME per-kind encoding an ordinary cell value takes — raw
`FixedQ4816` bits for a `fixed` row, a whole number for `int` (rounded to
whole units for a dynamics rebase) — authored the same spelling too (a decimal
string for `fixed`, a plain number for `int`).

**Advance.** The stored cell is a BASE; the read value is
`base + rate*(currentEngineTick-clock.epochEngineTick)/TicksPerSecond`,
computed LAZILY (no per-tick write, no journal entry) via
`Puck.Maths.DiscreteMeasure`'s exact rational allocation, folding the engine's
fixed 50,400-tick-per-second clock (`FixedTickConversion.TicksPerSecond`) into
the allocation's own denominator. The rate is per SECOND, in the row's own
DISPLAYED unit (a `fixed` row's `1/1` is `1.0` per second, so `1/240` reads
`0.00072835...` per elapsed engine tick, exact); a NEGATIVE rate mirrors its
positive twin rather than flooring the signed quantity. Advance is evaluated
against engine ticks, never simulation ticks, so a live change to the world's
own `simulation.rateHz` moves no epoch and skews no accumulation — the same
rate reads the same value at the same wall-clock moment regardless of rate. A
`rateHz: 0` world never steps, so its engine tick never advances either;
`advance` is legal there (not refused) and simply never accrues past whatever
base its last explicit write left it at.
An explicit write RE-BASES (base=written value, `clock.epochEngineTick`=the
applying engine tick, unconditionally — `StateArena.TryWriteLive`, the arena
door a cell write composes through, for a per-cell write;
`StateArena.TrySettleRedeclared` for a whole-row re-declaration, which settles
every cell the row carries across the transition table. A re-declaration
carries the stored value, so one restating the base a cell already held is not
a write of the value: the epoch moves and the accumulation the cell had reached
becomes the new base, which is why changing a row's default rate never
restarts a cell that declares its own. Both run inside
`world.undo`'s per-entry replay, keyed off each journal entry's own RECORDED
engine tick (every journal
entry, in-memory and durable, carries an engine tick beside its simulation
tick), so undo restores `(base, clock)` bit-exactly). A declared `min`/`max`
CLAMPS the computed value every read without rewriting the stored base — the
read side of the envelope duality (an explicit write is decided by
`TryAdmitWrite` instead, refusing or saturating per the row's `overflow`). The
application lives at exactly ONE site, `WorldStateReader.TryRead` (see
[documents.md](documents.md)'s routing map), so `world.state`, a rule's
`compareState`, a HUD gauge and the `UpsertStateCell` Add compose arm all see
the same number: an `add` composes against the LIVE value and then re-bases
(live 41, `add -10` → base 31, still advancing). A row declared with no value
carries no slot cell, so a rule READING it refuses `StateCellUndeclared` until
the first write. Read back on `world.state`'s cell line as
`advance=<num>/<den>/s@engineEpoch<n>`.

`clock.epochEngineTick` is SESSION-relative (an engine tick count from process
start), so `world.save` writing it verbatim would leave a reloaded document
reading FROZEN at its stored base until the new session's own engine-tick
counter climbed back past the old epoch — the fix: **settle at save, in the
serialized PROJECTION only.** `WorldSessionCapture.Capture` (the `world.save`
fold, `src/Puck.World.Server/WorldSessionCapture.cs`) writes every advancing cell's
base as its LIVE value (`StateAdvance.ComputeCurrentValue`) at the server's
completed engine tick, and projects `clock.epochEngineTick: 0` (alongside
`clock.epochTick: 0` for the same cell's dynamics/cycle epoch, when it carries
one) — never touching the live in-memory document, exactly like the
render-lever/population/screens folds this same class already does. Engine
tick 0 of the reloaded session therefore already reads what the save observed
and keeps advancing immediately with no freeze. A cell whose clock already
starts at epoch 0 and whose reading has not moved keeps its authored form, a
document with no `state` section gets none, and only authored rows settle: a
pool's generated storage rows keep their live state in the pool's own
continuation and are never written into `state.world`.

Authority is TWO holds, both decided by the one admission predicate
(`WorldServer.TryAdmitMutation`): `Mutate`/`section:state` gates the four
State kinds like any other section, PLUS a second, row-scoped `Edit` over the
CONCRETE `state:<name>` subject (`GrantSubjectKind.State`) or the `all`
wildcard — the SAME subject for the whole-row pair (`UpsertStateRow`/
`RemoveStateRow`) and the per-cell pair (`UpsertStateCell`/`RemoveStateCell`),
narrower authority than any other section (see [mutations.md](mutations.md)
and [authority.md](authority.md)).

**Dynamics.** `row` names a `dynamics` section row (see
[documents-render.md](documents-render.md)). The stored `Value`/cell value
remains the TRUTH — rules, grants, and `world.state`'s `value=` column read it
unchanged. A write REBASES the cell's `clock`: the live eased sample at the
applying tick becomes the new `(y0, v0)`, `v0` additionally taking a
`Retarget` velocity kick sized by the truth's own jump (so the follower keeps
chasing continuously through a mid-flight rewrite rather than snapping), and
`epochTick` moves to that tick — the closed-form counterpart of `advance`'s
own rebase, applied at the same compose site. The eased value is read LAZILY,
on demand (`WorldStateReader.TryReadEased`), through
`Puck.Maths.SecondOrderDynamics.Evaluate` — no per-tick write, no journal
entry, so a `dynamics` cell costs nothing between reads. `world.state`'s row
and cell lines report the authored trait and its live `eased=` value beside
`value=`; every presentation `state.<row>[.<key>]` read (HUD, camera
operand, marker, render and theme color, binding bar, overlay predicate, a
body's look lanes, gait drivers and scale) reads
the SAME eased value through the client's `WorldStateMirror`, interpolated
between tick samples at the frame's fraction, while an explicit trailing
`.$target` facet reads truth (see [hud.md](hud.md)). The mirror refreshes a
slot only when a delivery's `WorldStateStamp` names its row as moved or while
its trait is still moving. `world.save` settles a `dynamics` cell's clock the
identical way it settles `advance`'s: `y0`/`v0` become the live eased sample
at the saved tick and `epochTick` projects to `0`, so a reloaded session
keeps easing with no freeze. A cell composed with a `dynamics` behavior but no
`clock` at all reads its own stored value as the eased sample (at rest, zero
velocity) rather than easing in from zero.

**Cycle.** The value is a pure function of the server tick through a
generator of the lattice's reflection group (`Puck.Maths.SymmetryWord`:
`word` is one to eight mirror nodes, or omitted for the lattice's own
thirty-step cycle, `Puck.Maths.CyclicRotation`; `power` is applications per
step, nonzero and inside the order — with no word 1, 7, 11 and 13 are the
four rotation planes; one step lasts `ticksPerStep` ticks from
`clock.epochTick`). The period is the word's derived order (`Order` on the
record; `world.symmetry.word` prints it); an identity word or power is
refused. `output` is `Step`/`Node`/`Ring` on an `int` row,
`Turns`/`Cos`/`Sin`/`ProjectionX`/`ProjectionY` on a `fixed` row — the rotation
outputs read the order's root of unity (`CyclicRotation.Rotor(step, order)`),
the lattice outputs read `Puck.Maths.SymmetryLattice`, the stored value being
the node (0..239) carried `power` applications along its orbit per step. The
stored value is the phase in the row's displayed unit — an ordinary write
(the effective cycle unchanged) sets the phase and leaves the clock alone;
only a re-authored default or override (a fresh key, a switch in or out, a
parameter change) settles the clock. Settling out of a cycle stores the value
the rotation was displaying, never its phase, so removing a rotation leaves a
reader's number where it was; settling from one cycle into another carries the
phase, which the new rotation re-derives its own output from. Both per the
behavior transitions in
[Row and cell behavior](../../../../docs/reference/state/traits.md#re-authoring-settles-cells).
`world.state` echoes
`cycle=<coxeter|[m,…]>^<power>:<output>/<ticksPerStep>@epoch<n>[+<substepTicks>] order=<n>`;
`world.save` settles the value to the current index/node at epoch `0`. Read
laws: `tests/Puck.World.Schema.Tests/StateCycleReadLawTests.cs` and
`StateCycleWordLawTests.cs`; live proof: `tests/Puck.World.Canaries/state-cycle-trait`.

**Reserved `$` names are ENGINE-MINTED ONLY.** The
rule lives in `StateReservedCells.TryValidateReservedCell`
(`Puck.World.Schema/WorldState.cs`), called from `WorldDefinitionValidator`'s state
walk — which runs at boot, at every live mutation and on every undo-replay entry
— AND from the `UpsertStateCell` compose arm, so a hand-authored file and a
console verb refuse by the same code, with the verb naming it at the verb. A
`$`-prefixed ROW name is refused outright (nothing mints a row; this is also what
keeps `$tick`/`$population`/`$region:` from being shadowed), and so is a
`$`-prefixed RULE name. A `$`-prefixed CELL key is refused unless it is exactly
the key that row's shape mints — `$value` on a slot-addressable row, and nothing
else. Draw bookkeeping lives in typed row FIELDS at the site
(`drawCursor`/`drawnMasks`), refused by the field's own range check, not by
the cell namespace.

## `state.lattices` + the `lattice` row trait — the lattice (scalar rows, reactions, lattice-derived geometry)

`WorldFields.cs` (the compiled composite) + `WorldState.cs` (the document
spelling). `state.lattices` declares one or more topologies (name, origin,
`cellSize`, `width` × `depth` × `layers`, `stepEveryTicks`, `reactions`); at
most one is `Field`-kind and drives THIS trait (`WorldTopologyCompilation.
FindPhysical` — reactions, lattice-derived geometry) — the rest are discrete
`Grid`/`Ring`/`Hex`/`Graph`/`Tiling` topologies: a placement's `board` facet
(`$board:cellOf`/`offset`, `world.tabletop`) anchors a `Grid`, a `Hex`, a
`Graph`, or a `Tiling` (`family`: triangular, kagome, truncatedSquare, rhombitrihexagonal, truncatedHexagonal,
elongatedTriangular, truncatedTrihexagonal, penrose; `radius` in edge lengths; a graph generated at boot, directions
`a<degrees>`) topology (`Ring` refuses it) — a grid resolves positions against its
rectangular X/Z frame, a hex against its lattice (cell `(q, r)` at origin +
cellSize · (q − r/2, 0, r·√3/2), ordinals in `HexagonalIndex` ring order,
directions `E, SE, SW, W, NW, NE`), a graph to the nearest authored centre
within half a cell size (`cells: [{id, centre}]`, `directions: [{name,
opposite}]`, `edges: [{from, to, direction, oneWay?}]`; one neighbour per
(cell, direction) slot, so k neighbours need k directions; `offset` refuses
it; identity symmetry only) — and `cellSize` must quantize to a
positive Q48.16 value — it is the divisor `$board:cellOf` resolves world
positions against (the garden's `chessBoard` alongside its own `pondBasin`
water field — see `Puck.World.Schema/README.md`'s tabletop-primitive
section). The board facet's own `enforcement` (`record`, the default, or
`return`) is the one engine-side reaction to its `verdict` row refusing a
move — `return` poses the mover back onto `move`'s `from` cell on the
refuse edge; see that same section for the full contract. A discrete topology's own `directions` (optional; each kind's
compass/space names are the unauthored default) replaces its whole direction
vocabulary — see the schema README's discrete-boards section for the
authoring shape and validation. Field-shaped
state rows: `{"name": …, "kind": "Fixed", "domain": {"$type": "cellsOf",
"topology": …}, "field": {"initial"/"min"/"max", optional
"heightScale"/"color", "paint": […]}}` — `domain.topology` names the
`Field`-kind topology (the same `cellsOf` case a discrete board's own domain
uses; which storage a `cellsOf` row gets is an implementation choice keyed on
`kind`, never a second authored trait), and `lattice` (`WorldStateFieldTrait`)
carries only what is left once the topology moves to `domain`.
`WorldFieldsSection.Compile` assembles the runtime composite
the engine consumes (`WorldDefinition.Fields` is that compiled view — never
an authored section; there is no top-level `fields` member). A cell
write against a lattice row (`world.state.cell.set`) refuses through
whole-document revalidation — the lattice's cells are simulation state, not
authored cells. Rows are seeded by their trait's `paint` rectangles and
evolved by the topology's `reactions` in document order each step: `diffuse`, `decay`, `transform`
(`when` conditions on the cell → `then` set/add writes), `emit` (bodies tagged
nonzero in a keyed row deposit into the cell they stand in), `expose` (writes
1/0 into a keyed row per body by a field test at the body's cell — the bridge
to body-level chemistry), `flow` (moves a field downhill, mass-conserving,
over the combined surface height of itself plus its `over` terrain fields;
each cell donates an equal share of its previous-step value to each of the
lattice's active-axis directions; an optional `spillRow` catches an edge
cell's outward share — without one, edges are walls). A row with `heightScale` IS geometry: its value
raises a solid column above the origin that bodies stand on
(`Puck.Physics.Fields.FieldLatticeSolid`, unioned with the authored solids for contact) and
the renderer shows (`WorldFieldEmitter`: one CPU-baked distance brick per
height field, coloured by `color`, uploaded through the engine's brick pool).
`WorldFieldProgram.Compile` is the typed reaction compiler view over that same
authored topology and reaction list: stable field/node handles, its canonical
state catalog, fixed-point scalar inputs, typed state dependencies, immutable
canonical read/write sets, the dependency DAG they imply, and separate
cell-node/full-cell/body work counts. It is deliberately not a second serialized
graph language; editors and schedulers consume it beside `WorldDefinition.Fields`,
which remains the complete topology/paint/display composite and the document
remains the one authoring home. `WorldDefinition.FieldProgram` is the cached,
non-serialized door. It retains compatible handles across unrelated definition
edits and value-only state updates, and replaces them when field or reaction
program inputs change.
The authoritative `Puck.Physics.Fields.FieldLattice` executes a
`FieldLatticeInput` `WorldPopulation.CompileFieldLatticeInput` flattens from
this program — field handles become ordinals, `WorldFieldWriteOp` maps onto
the kernel's own enum — so the kernel itself parses no document; it never
lowers the authored reaction rows again. Compatible live reaction edits
replace the input while preserving
cell values, deltas, revision, and checkpoint shape. A lattice presence,
topology, cadence, or field-envelope change is an allocation change and
refuses live with restart guidance. `world.fields` reports the installed node
order, dependency edges, and pass counts after its cell statistics.
`layers: 1` is a ground lattice; more layers is a voxel volume and costs
proportionally. A lattice carries at most 262,144 cells so a full eight-field
primer (eight lattice rows) remains inside the federation frame; when any row has `heightScale`,
the XZ footprint is at most 126 × 126 cells and the sum across layers may raise
at most 126 cells, fitting the padded 128³ render brick without truncation.
Cell values are sim state beside the population — stepped
after the rules, checkpointed (`Fields` block), delivered as `FieldCells`
deltas on the snapshot (`FieldsFull` on a primer) — never document rows, so
nothing journals them. Read back with `world.fields`. A lattice can paint grass
beside an ice glacier; a burning body emits heat, heat ignites grass, fire emits
heat and consumes grass, heat melts ice into water, water quenches fire — no
interaction names the boundary.

### The tabletop primitive (`board` facet)

The tabletop primitive: a placement's `board` facet (`WorldPlacementBoard`)
anchors a discrete `Grid` `state.lattices` topology (only `Grid` carries the
rectangular X/Z frame `$board:cellOf`/`offset` resolve against; `Ring`/`Hex`
refuse the facet) to a physical row/body game (`$board:cellOf`/`offset`,
`world.tabletop`). A `Grid` topology's `cellSize` must quantize to a
positive Q48.16 value — it is the divisor `$board:cellOf` resolves world
positions against, so a zero or negative edge is refused at validation, not
left to crash the per-tick rule path. A rule firing is one journal scope: any
refused effect rewinds the whole firing, a `transaction` effect is a savepoint
inside it, and an `if` effect branches to one of two effect lists (see
[documents-rules.md](documents-rules.md)). A piece resolving to no
cell of its own (captured, lifted off) never itself registers as the mover
on either side of a settle. Chess's candidate source and destination
(`trailingZeroCount` of a mask) clamp an empty mask to `-1` rather
than writing the mask's own bit width (64), which the cell's envelope
refuses. `$upright:<bodyRef>` (a body's own up
axis dotted against gravity-up) is the reserved channel a piece's own
occupancy derive gates on, so a knocked-over piece reads as displaced
rather than occupying its last resting cell. `$fact:<bodyRef>:<fact>` reads
one live `BodyFacts` bit (`Airborne`, `Grounded`, …) as 1/0 — the door a
rule writes a body's transient into a world row through (an eased
`airPose` cell a creation's drivers then read), where `$identity:` is the
persisted fact lane.
See `Puck.World.Schema/README.md`'s tabletop-primitive section; the garden's `chessBoard` is the worked example.

## Authored randomness — SOURCE x SITE x MOMENT

One primitive, three separable parts. A **source** is a shape, a **site** is a
place that draws, a **moment** is when.

**Source** (`StateGenerator`) is the document's whole randomness vocabulary.
`source` selects the shape and each shape reads a DISJOINT field set — a foreign
field refuses BY NAME, including `bound`/`mode`, which are non-nullable and are
refused against their declared defaults:

- `markov` — `start`, `bound`, `mode`, `contexts` (weighted alternatives, each
  naming the context it moves INTO). Writes TEXT; exhausts per context. One
  emission is one walk from `start` to a TERMINAL context (one declaring no
  alternatives), refusing by name at `bound` rather than truncating. `mode` is
  `withReplacement` (default), `withoutReplacement` (drawn out → refuse by name)
  or `restartOnExhaustion`.
- `uniformRange` — `rangeMin`/`rangeMax`, both or neither. One numeric draw;
  refuses a `mode`.
- `weightedNumeric` — `weighted` (`{value, weight, multiplicity?}` rows) and `mode`.
  One numeric draw; under an exhausting `mode` the outcomes are drawn through the
  site's single `drawnMasks` mask — the numeric shuffle bag. `multiplicity` (also
  on a Markov alternative) is that many units per pass; a set's units total at
  most 256.
- `streamDraw` — no fields. One raw 32-bit draw; refuses a `mode`.

The alias table over a source's full entry set is compiled once per
`StateGenerator` instance (`GeneratorEngine`, a `ConditionalWeakTable`), so
per-tick draws do not rebuild it; a drawn-down pool is rebuilt allocation-free
in bounded stack storage per emission, with the identical alias mapping.

A lattice row's paint may carry one `draw` fill (`WorldLatticeFill.Draw`,
`{ "$type": "draw", "source" | "generator" }`, numeric sources only): the
per-cell lattice draw. It is one whole-field pass of the row's stream
(`GeneratorEngine.TryFireBatch`; cell `k` = the sample at
`drawCursor + k`, mask threaded cell to cell), painted at boot by `WorldServer`
at the pass the row's `drawCursor`/`drawnMasks` name, and advanced one pass plus
repainted by `world.generate <row>` (`TryComposeGenerate`'s lattice arm, then
`WorldTick.RepaintChangedLatticeDraws`, which repaints only a row whose draw
position or fill moved). Draw keeps its authored position in the
paint list: it overwrites earlier fills and later fills overwrite it. Whole-
document rebuild/load/reset repaint every draw row; undo repaints only a row
whose cursor/mask position rewound, preserving unrelated reaction-evolved
fields. Read law:
`tests/Puck.World.Schema.Tests/WorldLatticeDrawLawTests.cs`; live proof:
`tests/Puck.World.Canaries/lattice-draw-fill`.

`GeneratorCapacity`: 64 contexts, 256 alternatives per context (one
drawn-mask bit each), bound ≤ 256, token ≤ 64 UTF-16 units, 256 weighted outcomes,
256 declared sources, an extended table of at most 4,096 words, uniform bounds inside int32.

**A source holds NO position.** Declare it once in the optional `generators`
section (`{"name": …, "generator": {…}}`) and reference it from any number of
sites, or inline it at one site — the two spellings compile to the identical
record.

**Site** (`Draw`) declares a value is drawn: exactly one of `source` (a
declared row's name) or `generator` (inline), plus `timing`. Three sites:

```json
{"name":"bark","kind":"Text","draw":{"source":"barkTable","timing":"event"}}
"population": { "capacityDraw": {"generator":{"source":"uniformRange","rangeMin":128,"rangeMax":128},"timing":"boot"} }
"host": { "backendDraw": {"source":"backendTable","timing":"boot"} }
```

The CURSOR and drawn MASKS live on the SITE (`drawCursor`/`drawnMasks`, engine-
minted row fields — never cells), so **two sites referencing one source draw
INDEPENDENT sequences**. That is what makes a reference safe.

**Moment** (`timing`): `boot` (drawn once at first fill; a later `generate`
refuses by name), `tickPeriod`, `event`. The latter two redraw through the SAME
`WorldMutation.Generate` (ordinal 49) / `world.generate <row> [key ...]` — the
site owns its whole draw; a keyed site (a dice tray) redraws every cell, or the
named cells alone with the rest held. Cadence is an ordinary `$tick`-scheduled or
event-gated rule, so timing costs no mutation ordinal.

**The seed ladder is four rungs**, each LENGTH-DELIMITED before its bytes:
engine constant → `generation.worldSeed` → running INSTANCE identity → SITE
DESCRIPTOR (`state.<row>`, `population.capacity`, `host.backend`). The descriptor
is an IDENTITY, never a positional ordinal: the live site set moves under
ordinary operation (a settled facet clears, `world.row.remove state` retires a row,
`UpsertStateRow` adds one), and a positional stream would silently re-point a
live site while its cursor kept counting.

**The engine SEEKS, never replays.** Fixed advance cost per sample (which is why
`uniformRange` is a multiply-high map, uniform to within `n/2^32`, not a
rejection-sampled bounded draw), so resuming at cursor `n` is one `Advance` —
O(1). There is NO per-tick cadence ceiling.

**A source may declare `extended`** (`GeneratorExtended`): an authored
`Pcg32Extended` table replacing that generator's own self-seeding — `{"k": 2..4096
(power of two), "table": [k uint32 words]}` verbatim, or `{"k": …, "script": [up
to k values in the source's own OUTPUT space]}`, compiled at boot resolution
(word `i` is `wanted_i XOR base_i`; words past the script are the self-seeded
table's own). Only `streamDraw` and `uniformRange` admit a script — the two
sources that sample in one fixed-cost draw with no drawn-mask state, so a wanted
value maps back to a single raw draw; `markov`/`weightedNumeric`/`symmetryOrbit`
draw through an alias table whose selection depends on the site's own drawn
masks, so a script cannot map back. Nothing new persists — the table is a pure
function of (seed ladder, authored table/script, cursor), so save/reload/undo
resume exactly as an ordinary site's do. `Draw.skip` (non-negative, default `0`)
is an authored seek: a rebuild advances `(skip + cursor) * cost` rather than
`cursor * cost`, and never writes the cursor. `world.state <row>` echoes
`extended k=<k> scripted=<n> skip=<s>` on a site carrying the facet.

**Boot-only sites SETTLE AND CLEAR** into their ordinary literal field and
NARRATE on stderr (`[world.draw: settled <site> instance=<name> -> <value>]`) —
settling erases the only evidence the value was random. State sites keep facet +
cursor and RESUME on reload; they fill only while the row carries no cell, so an
authored `value` is a deliberate override. `host.backendDraw` draws its backend
BY NAME from a weighted TEXT source over the backend tokens (never an unnamed
ordinal) and is XOR-by-presence against `host.backend`;
`population.capacityDraw` cannot be (its record is a STRUCT, so an authored
an explicitly authored default-valued `capacity` is indistinguishable from the
record default) — there the draw wins.

**Domains narrow STATICALLY** against the site's own envelope, the census
coherence sum, and every reachable backend token — so a roll can never decide
whether the world boots. `population.capacityDraw` is TEMPORARILY floored at
`WorldBodiesLimits.CapacityCeiling` (4096) because `world.population` crashes
below it; that collapses its domain to a single value until the population lane
lifts the floor.

## `search` — what the board would be

`WorldSearch.cs` owns `search.jobs`. A job names `tokens` (keyed int row, values
are cells of `board`; a non-cell value is off the board) and `board`; `turn`/
`verdict` derive from the tabletop board binding over `board` unless authored, as does the
accepting verdict value (the binding's `accept`, else 1). A job over piles names `zones` (ordered `keysOf` rows
over the token domain `tokens` names) instead of `board`: the zones are its cells, `tokens` is the domain row,
the one shape is `transfer` (`selector`: `last`/`first`, the zone end a token must stand at; `insertFirst`), and
`turn`/`verdict` are authored (no binding, no `reach`). Pile order is the zones' own: only an end token moves, and
the judge reads the moved pile through the arena, which lays an ordered zone out by capacity and moves
membership without rewriting a row.

`shapes` names the candidate shapes the walk enumerates, ahead of token and
target/direction; absent, the one default `relocate` (`displace: true`) this
section always ran. `drop` (a token off the board enters an empty cell),
`jump` (`over`: a direction-name list, or `["any"]` for every direction the
topology declares — two cells along one, over an occupied intermediate that
leaves the board, onto an empty destination), and `pair` (`with`: a cell key
of `tokens` — the companion relocates by the same lattice translation, `CompiledTopology.TryTranslation`
carried by `TryOffset`: grid, ring, hex, or box; a graph or tiling refuses it; its own
destination empty), and `promote` (`codes`: an int row keyed
by the tokens; `to`: up to sixteen codes — relocate and change the token's code to each in turn) are the other
arms. `relocate` with `displace: false` leaves the standing token in place rather than evicting it. A job
with a score keeps a transposition table (`SearchCapacity.TranspositionEntries`
slots keyed by relevant state and effective move ply) that rides the checkpoint
and hash. Preserve the move-ply component when changing cache probes or stores:
judges may read `$search:ply`, and chance scopes do not advance it. When scoping
a world's judge, follow each reached derived board's inverse token and code
rows as dependencies, including boards reached through score expressions or
other judge rules.

Outputs: `legal` (int row keyed by the tokens, a per-token destination mask,
boards ≤ 64 cells). `reach`/`held`/`counts` work for a board
of any size: `reach` (int board over `board`'s topology) is painted 1 at every
cell `held` (int slot, the token's ordinal in `tokens`) may reach, empty
elsewhere — out of range paints nothing — and `counts` (int row keyed by the
tokens) is each token's own accepted count. `reach`/`held` are authored
together. The job walks every (shape, token, target cell or direction) triple
in that order: it applies the shape's move inside a journal scope on the
arena (`ArenaSearchCandidate`) — an invalid candidate (occupied landing,
unoccupied jump intermediate, off-board companion) is skipped before judging —
and judges it with the rules a scoped judge can evaluate (no interaction, no
decision, no world-only read), accepting when the verdict reads
`accept` and the turn changed. This root walk never prunes and never skips a
valid candidate, so the root outputs are unaffected by `depth`/`score`. It
restarts when any framed cell other than its outputs changes. Quota derives
from what the work sheet leaves divided by the judge's cost (`nodes` may only
lower it, and a deeper search spends the same quota over more ticks); progress
hashes and checkpoints. `world.search` narrates each job. Sharp edge: a token
row whose `min` is a cell ordinal is refused — the off-board value must be no
cell.

`depth` (default 1) and `score` search deeper: `score` is a value expression
(infix text in `.puck`, stored as its `instructions` program like every other
document expression and refused by name as text, compiled like a rule binding,
refused if it reads a host-only fact) required once `depth` exceeds one or `best` is
authored. `best` is a keyed int row receiving `token`/`to`/`score` — the
deepest completed depth's answer. Iterative-deepening negamax with alpha-beta:
each accepted root candidate recurses one more ply (negated — the value is
from the perspective of the side that just moved) while plies remain, else
`score` evaluates directly; a position with no accepted relocation scores
`-SearchCapacity.MateScore`. Alpha-beta prunes every ply past the root
only. `method: MonteCarlo` searches the same `score` by UCB1 instead: `iterations` rounds over a
`SearchCapacity.TreeNodes` pool, each growing at most one node (a node scans its own candidates from a drawn start
until one is accepted, and holds at most one child more than the square root of its visits), playouts drawn from a SplitMix64 stream seeded by the plan's `DrawSeed` (zero for world jobs), the
score read from the mover's side at a dead end or the `depth` cap, landing the most-visited root move. The recursion is an explicit stack (one journal scope per ply beyond the
root), not the call stack, so it suspends at any node across a tick boundary
and checkpoints byte-for-byte.

## `navigation` — bounded surface, flight, and medium routes

**Navigation is authored world truth.** `navigation.domains` owns bounded
`surface`, collision-free `volume`, and live-field-constrained `medium` grids.
A `BodyTargetSource.Navigated` producer points at one domain and one ordinary
authority-checked target register. Keep A* fixed-point, budgeted, stable-tied,
checkpointed, and hashed; bake static solid clearance once (a surface edge's
sweeps run from `maxStepHeight` above the foot to the head — a sweep skimming
the floor advances one contact skin per march step and exhausts on a
diagonal), but recheck a
medium field before traversing its cached edge — a medium's free surface
(value × heightScale) is bounded by the shared lattice's body-coupling
ceiling, not by its topology's own layer count, so a shallow `layers: 1`
topology under a deep medium still resolves correctly
(`Puck.Physics.Fields.FieldLattice.IsInsideMedium`/`IsSegmentInsideMedium`). Extend this
vocabulary for
engine-integral movement semantics; addons/agent extensions remain the home
for arbitrary policy and planning, not collision/path correctness.

An optional `parent` names a static unit-scale placement frame (including its ancestors). Origin and grid X/Z
axes follow that frame's position/yaw; surface probes remain vertical. `world.navigation` echoes the resolved
origin/yaw. A domain retains its workspace only after matching tuning, capacity, and the complete fixed cell/edge
bake against the replacement query, which it then uses for future checks. Medium retention also requires the same
field provider and synchronized revision. Unproved domains rebuild; do not infer tile-level SDF invalidation.
Dealt placement instances carry reserved `dealSlot` identities independently of their transforms. `deal.preserve`
selects instance-owned transforms, prototypes, and facets. Spatial volumes distinguish occupation, shared clearance,
and opaque influence channels; static planning does not ban dynamic facets from ordinary placements. Reflow previews
travel through attributed queries and return an ordinary batch with spatial, named-input, and payment guards.
The granary module guide owns the spatial/reflow
authoring contract: `src/Puck.World/Assets/worlds/modules/README.md#grow-and-rearrange-the-court`.

`WorldNavigation.cs` owns named finite domains. `surface` samples SDF ground,
step/slope limits, a vertical capsule, and swept neighbour edges from
`maxStepHeight` above the foot to the head; `volume`
uses swept-sphere cells and edges in three dimensions; `medium` adds a named
`state.world` lattice row carrying `lattice.medium`, checked live at nodes and
half-cell-or-shorter swept boxes so field evolution can invalidate a cached edge.
Each piece checks every intersected voxel and its local free surface (at most 27),
not just corners or point samples that can miss dry pockets.
Volume connectivity is authored as 6/18/26 neighbours, with blocked-axis
corner cutting refused. A `BodyTargetSource.Navigated(domain, register)` keeps
the ordinary authority-checked designation as its goal and supplies bounded
fixed-point A* waypoints to `ProduceSteeringIntent`'s approach shape; volume and medium targets
also drive `MoveUp`. Stable ties are `(f, h, nodeOrdinal)`. Static edges bake
once, search arrays are reused, and a body's route array allocates on first
use. Domain/cell/search/path ceilings are representation bounds, reported by
`world.navigation`, `body.targets`, and `world.budget`; `$nav:<bodyRef>:<facet>`
is the rule operand. Routes are local runtime state: clear them on producer,
designation, transfer, or domain-rebuild discontinuities; checkpoint and hash
them wherever uninterrupted simulation continuity is promised.

Optional domain `shared: { goalCapacity, expandedNodesPerTick }` replaces per-body
A* searches with queued reverse-Dijkstra destination trees. Domain + goal cell
is the sharing key; never bind cache ownership to a leader or body generation.
Each body still owns its copied path and cursor, and uses its exact designated
point at the end. Domain profiles partition clearance/topology/medium compatibility;
shared volume/medium users must fit the domain's root-centered clearance sphere.
Searches take deterministic round-robin turns under the domain's aggregate
per-tick expansion budget, each visiting at most 26 predecessor edges. Pending
requests pin resident trees; otherwise eviction is LRU using unique, contiguous
recency ranks (never saturated counters). Full pinned capacity
reports `$nav:<bodyRef>:capacity`, queued work reports `pending`, and neither
means `unreachable` or permits an unbudgeted fallback. `maxPathNodes` still bounds
extraction; a shared tree may settle the whole domain rather than stopping at the
independent A* `maxExpandedNodes`. Hard totals bound cells × goals and per-tick work.
Checkpoint/hash discovered costs, successors, settled flags, pending starts,
ages, and scheduler cursor; derive heap layout and cached hashes. Node hashes
use canonical 64-cell blocks with dirty-block invalidation, while pending hashes
sort the bounded request list; never scan a settled domain every replay tick.
Referenced-medium writes reset
the affected trees (not writes to unrelated fields), and obsolete trees are
canonical empty state at checkpoint/hash time. Restore field values before
restoring navigation's derived invalidation stamps. This is not incremental
repair, hierarchical routing, crowd collision avoidance, or group membership;
see the server README's Navigation section for the current limits.

## Records, pools, and retained turns

Use the current [records and pools](../../../../docs/reference/state/records-and-pools.md)
and [rule groups](../../../../docs/reference/state/rule-groups.md) references when editing records,
pools, token knowledge, or retained turns. A pool-field reference is structural:
`StateChannelRef.PoolField` carries either binding + field or pool + slot + field;
the `.puck` printer may show `piece.health` or `pieces[0].health`, but document code
must not recover those parts by splitting a dotted string. A lexical reference
holds its generation-checked handle. A static slot reference resolves the current
live occupant and therefore follows release/reclaim.

World body carriers are record enum fields plus `properties.carriers`. Map every
enum member exactly once to one single-inhabitant placement, one local seat, or an
explicit detached entry. Store no physical body index in a record, and do not add
a generation-pinned attachment path beside carriers. Arena and Paddleball are the
shipped examples: both author enum roles; Arena also keeps fighter health,
respawn, and pickup state in records. Interaction geometry resolves the logical
role to a body, while effects read or write the same instance through lexical
bindings.

Pool lifetime tests must include release/reclaim and export/reload, including a
reclaimed nonzero generation and pair carriers. Exercise module read/action
exports through lexical bindings and include lossless compile/decompile laws.
For retained turns, declare the complete pool closure (domain, generation, and
field rows), account for depth plus one pending segment, and cover claim/release,
advancing-field clocks, checkpoint/hash continuation, outside-row invalidation,
and atomic `rewindGroup`. A write outside the closure makes the turn unrewindable;
it must never be silently omitted from a compact retained record.

After changing this surface, regenerate schema, vocabulary, and name-registry
outputs with their CLI owners and run the affected State, Rules, World, and
World.Transpiler suites. A generated projection probe must cover every new arm.
