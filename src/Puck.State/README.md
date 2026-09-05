# Puck.State

The state and rule engine of a deterministic fixed-step simulation, as a
library with no world, body, rendering, or presentation concept. A **rule**
reads and writes named **state rows** through a bounded postfix **expression**
that also carries an infix spelling; every value is exact integer or Q48.16
fixed-point arithmetic over `Puck.Maths`; and the lookup tables, atomic state
transforms, validated identifiers, and reserved fact channels a rule can name
are all data a document declares. `Puck.World.Schema` consumes and extends this
package for the world document, so a card game, a turn-based resolver, or
another engine's frontend can run authoritative rules over `Puck.Maths` and
`Puck.State` alone.

`dotnet pack` produces `ByteTerrace.Puck.State`; the first NuGet.org release has
not been published yet. The project depends on `ByteTerrace.Puck.Abstractions`,
`ByteTerrace.Puck.Assets`, and `ByteTerrace.Puck.Maths`, each named directly, so
the package's declared dependencies are the whole closure rather than part of it
plus whatever another package happens to carry along. `Puck.Physics` references
this package, never the reverse.

## ⚖️ Licensing

ByteTerrace.Puck is source-available and dual-licensed. It is not open source.
The default is the
[PolyForm Noncommercial License 1.0.0](https://polyformproject.org/licenses/noncommercial/1.0.0),
under which noncommercial use is free: study, hobby projects, research,
evaluation, and use by any school, university, public research organization,
charity, or government body. Shipping or operating it commercially requires a
paid commercial license from ByteTerrace, whatever the size of the user.

Both documents ride inside the package.
[`LICENSE.md`](https://github.com/ByteTerrace/Puck/blob/main/LICENSE.md) is the
binding noncommercial license;
[`LICENSING.md`](https://github.com/ByteTerrace/Puck/blob/main/LICENSING.md) is
the plain-language summary of who needs which, and how to ask for commercial
terms.

## 📦 What the package holds

Nothing here carries a `World` name — a state library names no world.

- *The state section:* `IStateSection` is the contract every reader and
  compiler consumes — the document-owned `Rows`, the `Lattices` they may lie
  over, and two per-participant slot lanes (`ParticipantSlots`,
  `IdentitySlots`, each an `IStateSlot`). `StateSection` is the standalone
  document's own record of it; a document project declares its own record
  over the same interface and adds what only it can name.
- *Rows and cells:* `StateRow` (non-sealed — a document project derives its
  own row to add traits only it reads) over the `StateCell` substrate, with
  `StateCapacity`, `StateReservedCells`, and the `StateRows` finders. The row
  converter `StateRowJsonConverter<TRow>` owns the wire shape (`value`-vs-
  `cells`, the decimal fixed-point spelling) and exposes two hook points a
  derived row's converter writes its own members at.
- *Traits:* `StateAdvance` (exact rational accumulation), `StateDynamics`
  (a second-order follower over a `DynamicsRow`), `StateCycle`/`CycleOutput`
  (a tick-indexed rotation through a symmetry-lattice word), `StateVisibility`/
  `HiddenCells`/`StateKnowledge`/`StateObservation` (observation policy),
  `StatePhase`/`PhaseGuard` (a guarded submission generation).
- *Domains:* the `StateDomain` union (`slot`, `keys`, `keysOf`, `cellsOf`,
  `ring`), closed over `Union.cs`' `[Union]` marker.
- *Topologies:* `LatticeTopology` (`grid`, `ring`, `hex`, `box` — a host
  registers its dense `field` case as a further derived record),
  `TopologyKind`/`TopologyWrap`/`TopologyDirection`/`TopologyElementAlias`,
  `CompiledTopology` (adjacency, opposites, point-group images, element
  aliases), and `TopologyCompilation` (validate, normalize, compile with an
  anchor offset, the unanchored find).
- *The catalog and the reader:* `StateCatalog` compiles a section into
  `StateDescriptor`s and catalog-bound `StateHandle`s by `StateLane`
  (`Document`, `Participant`, `Identity`) and `StateStorageShape`;
  `StateReader` is the one (row, key) → raw-value computation (advance, cycle,
  eased reads, reductions, arg-extrema); `StateCellWriter` the one cell-write
  composition with FIFO eviction.
- *Authored randomness:* `Draw`/`DrawTiming` (the site facet),
  `StateGenerator` with its context/alternative/weighted-outcome rows,
  `GeneratorMode`/`GeneratorSource`/`GeneratorCapacity`, `ClosedBitset256`
  (drawn masks), and `GeneratorEngine` (the seed ladder, the fixed-cost
  advance-per-sample seek, every source's emission).
- *Patterns:* `PatternRow`/`PatternSymbol`/`PatternNode`/`PatternCapacity`
  and `CompiledPattern`/`CompiledPatterns` (a Brzozowski-derivative machine
  inside a state budget).
- *Expressions:* `ValueExpression` and its `ValueToken` postfix
  vocabulary (constants, state reads, arithmetic, comparison, bit and board
  operations, `select`), `ExpressionSpelling` (the infix spelling and its
  inverse — syntax only, no second evaluator), `ValueExpressionJsonConverter`
  (reads either spelling, writes each back in its own), `ExpressionOp` (the
  compiled opcode), and `ExpressionArithmetic` (the allocation-free Int and
  Q48.16 evaluator every opcode lowers to).
- *Tables:* `TableDocument` (`puck.table.v1`), `TableEntryDocument`, and
  `TableCanonicalizer` (validate → normalize → canonicalize), with
  `TableRow` as the name/source/hash reference a document pins one by and
  `CompiledTable` as the sorted, binary-searched loaded form.
- *State transforms:* the `StateTransform` union (`transfer`, `setRay`,
  `shuffle`, `sortZone`, `sortKeyed`, `writeSet`, `push`, `observe`),
  `ZoneSelector`, and `SortKey`.
- *Identifiers:* `SafeName` and `CellName` (validated at construction,
  refusing by name; `SafeName.MaxSuffixLength` reserves the file suffix a
  document project may append), their JSON converters, and the
  `TryParseStringJsonConverter<T>` shape they share.
- *Reserved channels:* `RuleFacts`, the state-neutral `$`-prefixed channels
  a rule may read instead of a declared row (`$tick`, `$bind:`, `$table:`,
  `$cell:`, `$reduce:`, `$match:`, `$history:`, `$symmetry:`). The channels
  only a world can answer — bodies, distance, line of sight, screens, links,
  regions, the population — are the document project's `WorldRuleFacts`.
- *Literals and spellings:* `NumericLiteral`, the one decimal→Q48.16
  conversion every authored constant and table value crosses, and
  `StateSpelling`, the one home for how a refusal spells a kind, a generator
  source, or a cycle output.
- *Gate vocabulary:* `ActionStateComparison` (with `ActionStateComparisons.Holds`,
  the one evaluation, including the positive-infinity case) and
  `ActionTriggerMode` (level or edge) — shared by a `compareState` operand and
  `Puck.Physics`' compiled per-body predicates, so neither can grow an arm the
  other lacks.

## 🔌 How a document project extends it

Extension is by registration and derivation, never by an edit inside this
package:

- A polymorphic base declared here (`LatticeTopology`) lists only the cases
  this package owns. A document adds its own case as a derived record and
  appends it to the base's `PolymorphismOptions` through a `JsonTypeInfo`
  modifier on its own serializer options (`Puck.World.WorldJsonVocabulary`) —
  the same discriminator convention, one more arm.
- A record that a document must widen (`StateRow`) is non-sealed; the
  derived record forwards the shared members to the base constructor and adds
  its own, and its converter derives from `StateRowJsonConverter<TRow>`.
- What a document must resolve by itself (a placement-anchored topology
  frame, a definition-anchored read, an asset off disk) stays in the document
  project as a thin entrance over the engine's own method, never a second
  implementation of it.

## 🧭 What it will hold

The charter's remaining phases, in order:

1. *The rule vocabulary as registries.* Predicates, effects, and operands
   become families a document project registers; the compiler, the work
   budget, the dataflow and hazard analyses, and the runtime read side of
   every operand move here.
2. *The evaluator*, behind a state-host interface — the mutation door, the
   journal, checkpoints, and the fact reader the operands answer through —
   that `WorldServer` implements.

Refused by the charter: a compatibility shim between old and new spellings at
any phase, and moving the evaluator before the seams exist.

## 🔗 Where the rest lives

The compiler (`WorldRuleCompiler`), the operand and effect unions, and the
validators stay in [`src/Puck.World.Schema`](../Puck.World.Schema/README.md)
until their phase; the evaluator stays in
[`src/Puck.World.Server`](../Puck.World.Server/README.md).
`tests/Puck.State.Tests` proves the expression syntax's parse/print laws; the
converter and schema facts that need the world document's JSON context stay in
`tests/Puck.World.Schema.Tests`.
