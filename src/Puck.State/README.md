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
  `StatePhase`/`PhaseGuard` (a guarded submission generation), `StateInverse`
  (a `cellsOf` row declaring itself the inverse of a keyed token row — see
  `DerivedBoards`).
- *Domains:* the `StateDomain` union (`slot`, `keys`, `keysOf`, `cellsOf`,
  `ring`), closed over `Union.cs`' `[Union]` marker.
- *Topologies:* `LatticeTopology` (`grid`, `ring`, `hex`, `box`, `graph`, `tiling`
  — a host registers its dense `field` case as a further derived record),
  `TopologyKind`/`TopologyWrap`/`TopologyDirection`/`TopologyElementAlias`,
  `CompiledTopology` (adjacency, opposites, point-group images, element
  aliases, cell centres, position-to-cell, axial offsets), and
  `TopologyCompilation` (validate, normalize, compile with an anchor offset,
  the unanchored find). A hex topology is `HexagonalIndex` made spatial: cell
  `i` is index `i` (rings outward, consecutive indices adjacent), its six
  directions are `HexagonalCoordinate.Direction(0..5)` named `E, SE, SW, W,
  NW, NE`, and cell `(q, r)` sits at origin + cellSize · (q − r/2, 0, r·√3/2).
  A graph topology is the adjacency table authored directly: `cells` with ids
  and centres (relative to origin, world units), `directions` each naming its
  opposite, and `edges` (`from`, `to`, `direction`, optional `oneWay`) that
  fill one slot per (cell, direction) and, unless one-way, the reverse slot —
  a territory map, a star board, or any tiling a tool emits; position-to-cell
  is the nearest centre within half a `cellSize`, there is no axial offset, and
  the symmetry group is the identity. A tiling (`TilingGenerator`) is a graph
  generated within a radius: the triangular, kagome, truncated-square,
  rhombitrihexagonal, truncated-hexagonal, elongated-triangular, and
  truncated-trihexagonal uniform tilings from their unit cells, and the Penrose
  P3 rhombs by Robinson-triangle inflation from a sun; tiles are cells outward
  from the origin, shared sides are adjacency, and edge normals (`a0`, `a30`, …)
  are the direction slots.
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
- *Reductions:* `$reduce:<max|min|sum|count>:<row>` aggregates a row's cells.
  Append `:between:<lower>:<upper>` to admit only live values within an inclusive
  range, for example `$reduce:count:pieceCell:between:0:63`. Bounds use the source
  row's units and normal numeric-literal rounding; `count` still returns an Int.
  The existing `:where:<filterRow>` admits keys with nonzero filter values. Both
  filters may appear once, in either order, and their conditions intersect.
  Missing filter keys are excluded; an empty result is zero. Bounds must be
  representable and ordered. `arrangementRank` takes neither filter. A range
  reduction costs three work units per candidate capacity (read and two comparisons).
- *Expressions:* `ValueExpression` and its `ValueToken` postfix vocabulary,
  `ExpressionSpelling` (the infix spelling and its inverse — syntax only, no
  second evaluator), `ValueExpressionJsonConverter` (reads either spelling,
  writes each back in its own), `ExpressionOp` (the compiled opcode), and
  `ExpressionArithmetic` (the allocation-free Int and Q48.16 evaluator every
  opcode lowers to). Periodic masks use `replicationMask(width)` (one bit at
  the bottom of each block) and `repeatBits(pattern, width)` (repeat a block
  across the 64-bit carrier). Width must be 1, 2, 4, 8, 16, 32, or 64, and a
  pattern must fit its block; invalid arguments refuse evaluation. Width 64
  repeats the whole word unchanged, including negative bit patterns. Both
  calls are Int-only and accept live expressions. For example,
  `replicationMask(8)` is `0x0101010101010101`, `repeatBits(127, 8)` is
  `0x7F7F7F7F7F7F7F7F`, and `repeatBits(3, 4)` is the Fermat-style mask
  `0x3333333333333333`. After validating every authored token, the compiler
  evaluates successful constant subexpressions once using the runtime evaluator.
  This includes calls with literal arguments and immutable topology operations.
  Live reads remain live; invalid constant subexpressions still refuse at runtime,
  even in an unselected conditional branch. Work budgets price the resulting
  program, while serialization retains the authored expression. Beside arithmetic, comparison, bit and board operations,
  and `select`, the call vocabulary carries one family per prefix over
  `Puck.Maths`: `pair`/`pairX`/`pairY` and the Szudzik algebra (`pairSwap`,
  `pairMax`, `pairMin`, `pairSum`, `pairDifference`, `pairTranslate`,
  `pairScale`); `morton`/`mortonX`/`mortonY`; `hilbert(order, x, y)`/
  `hilbertX`/`hilbertY`; the hex family over `HexagonalIndex` (`hex(q, r)`,
  `hexQ`, `hexR`, `hexRadius`, `hexEuclideanSquared`, `hexDistance`, `hexNeighbor`,
  `hexRotate`, `hexMirror`, `hexSwap`, `hexAdd`, `hexSubtract`,
  `hexMultiply`, `hexScale`, `hexTranslate`); the square family over
  `SquareIndex` on the same spellings (`square(x, y)`, `squareX`, `squareY`,
  `squareRadius` (Chebyshev), `squareLength` (Manhattan), `squareEuclideanSquared`,
  `squareDistance` (Manhattan), `squareChebyshev`, `squareNeighbor` (E, N, W,
  S), `squareRotate` (quarter turns), `squareMirror`, `squareSwap`,
  `squareAdd`, `squareSubtract`, `squareMultiply`, `squareScale`,
  `squareTranslate`); `gcd`/`lcm`; the floored `mod(a, m)` with
  `cycleForward(a, b, m)` and `cycleDistance(a, b, m)` over an m-cycle; `smallestMissing(mask)`
  (the smallest non-negative integer missing from a 64-bit set); `isPrime(n)` (exact over 64 bits
  in bounded work) and `prime(i)` (the first 256 primes, derived once) — never
  a next-prime or n-th-prime search, which is unbounded on the tick path and
  belongs to a generator draw source; `choose(n, k)` and `factorial(n)`
  (n ≤ 20); the combinatorial number system over a subset spelled as a bitmask
  (`subsetRank(n, mask)`, `subsetAt(n, k, rank)`, `subsetMember(n, k, rank, i)`,
  colexicographic, n ≤ 64 — a poker hand is one rank below `choose(52, 5)`);
  the factorial number system over a permutation packed as nibbles, position
  i in bits 4i..4i+3 (`arrangementRank(n, packed)`, `arrangementAt(n, rank)`,
  `arrangementMember(n, rank, i)`, lexicographic Lehmer codes, n ≤ 16); the layer family over
  `LayerSequence` (`layer`, `layerOffset`, `layerStart`, `layerSize`, each
  `(index-or-layer, start, step, seed)`); and `sqrt` (int floor root or fixed
  root), `sin`, `cos` (fixed radians). Every function is int-only except
  those three; a domain fault (a negative index, a component past the cell,
  an order outside 1..31) fails the expression rather than wrapping.
- *Rules:* `Rule` and `RuleBinding` (the authored row), `ActionPredicate`
  (`compareState`, `compareValue`, `all`, `any`, `not`), `ActionEffect`
  (`setState`, `addState`, `pushState`, `transformState`, `countdownState`,
  `generate`, `removeStateCell`, `scheduleState`, `transaction`) with its
  `TransactionStep` mirrors, `ActionTarget`, `ActionStateComparison`, and
  `ActionTriggerMode` — the last two shared with `Puck.Physics`' compiled
  per-body predicates, so neither can grow an arm the other lacks.
- *The compiler:* `RuleCompiler` compiles a `Rule` against a
  `RuleCompileContext` (the section, its catalog, its pinned `TableSource`s,
  patterns, generators, and the simulation rate) into a `CompiledRule` —
  `GateToken`s, `EffectFact`s, `CompiledRuleBinding`s, `CompiledExpressionToken`s.
  Every piece (`CompileGate`, `CompileEffects`, `CompileExpression`,
  `CompileBindings`, `ResolveOperand`, `TryResolveDynamicKey`) is public so a
  document project composes them with its own arms. `RuleRefusal` names every
  compile-time refusal; a `RuleException` carries one, or a document project's
  own enum.
- *The facts:* `OperandFact` (`StateCellOperand`, `BindingOperand`,
  `TableOperand`, `TickOperand`, `ReductionOperand`, `SymmetryOperand`,
  `BoardOperand`, `PhaseOperand`, `PatternOperand`, `HistoryOperand`) and
  `EffectFact` (`WriteEffect`, `CountdownEffect`, `GenerateEffect`,
  `RemoveStateCellEffect`, `ScheduleStateEffect`, `TransactionEffect`,
  `TransformStateEffect`, `PushStateEffect`) are class hierarchies, never
  unions: each answers `Read(IRuleReader)` (operands), `Cost(context)`, and
  the cells it reads and writes (`CollectReads`/`CollectWrites` into
  `RuleAccess` lists). `CompiledCellRef` is the one key-indirection carrier
  (`$cell:`, a bound key, an `$expr:` key that compiles to an implicit binding
  read back through `BindingKeyFact`, a `$bind:` binding, a `$zone:` endpoint,
  or a document project's `KeyFact`). `LiveZone` is its row-side mirror: a
  rule declares a `zones` table (ordered zones over one token domain and kind,
  in index order, an empty entry a gap), and any row position in the rule — a
  `compareState` `state`, a `$reduce:`/`$match:` row, a `$zone:` endpoint's
  zone, a transfer end, an expression's row — may spell `$zones[<index>]`,
  the index an infix key (`game[from]`, `$each`, `$bind:<name>`, or any
  expression), so one rule serves every pile of a game; `forEach: "$zones"`
  iterates the table's own non-empty indices. Every channel splits through
  `RuleFacts.SplitChannel`, which keeps a bracketed index whole, and the
  infix lexer keeps `$zones[...]` inside a reserved name.
  `$zone:<ordered-zone>:first|last` preserves the member's original string key
  and reads the active store, including uncommitted frame transfers. An index
  selecting no zone, or an empty zone's endpoint, names no cell: a read of it
  is an absent fact (`RuleFact.IsAbsent`) that no comparison holds against —
  not even `NotEqual` — an expression over it refuses, a copy from it does not
  fire, a write addressed by it refuses, and a transfer end through it refuses
  by name.
- *Evaluation:* `RuleEvaluator` runs a compiled family over an `IRuleHost` —
  gates in array order, bindings before the gate, Edge/Level latching per
  binding in a `RuleLatch` (simulation state: it hashes, flattens to a
  checkpoint, restores), a forEach row's keys snapshotted before the first
  evaluation, and every state-neutral effect fired through the host's door as
  a `StateMutation` (`UpsertCell`, `RemoveCell`, `Generate`, `Apply` a
  transform). A top-level effect preflights itself under a host scope and
  installs alone; a `transaction` preflights its whole branch as one candidate
  and the host commits that scope as one mutation (`TryCommitPreflight`).
  The evaluator is the evaluation in flight — the tick, the bound forEach key
  and participants, the binding values — which the host's `IRuleReader`
  forwards to its operands. It also owns the trace (`RuleTraceEvaluation`,
  armed per rule name) and the refusal ledger (`RuleRuntimeDiagnostic`, exact
  counts per category; the host narrates a category's first occurrence). A
  binding, effect, or `compareValue` conjunct whose expression faults is a
  counted `RuleEffectRefusal.Arithmetic`, never a gate that silently stopped
  holding; `ExpressionFault` names why. `IRuleReader` is what a fact reads
  through; `RuleEvaluation` holds `GateHolds`, `TryEvaluateExpression`,
  `ResolveKey`, and the state reads they share.
- *The store and the frame:* `StateStore` is where a read finds a cell's
  stored value beneath the rows' structure — `RowStore` over a section's own
  cells (`IRuleReader.Store`), or `StateFrame`, every integer cell of a section
  laid out once by a `FrameLayout` (slot, keyed, board by cell ordinal, ring
  with its cursor, an ordered zone by capacity — count, member ordinals in pile
  order, member values — so a `transfer` changes membership inside the frame;
  text and field rows read through to their cells). `StateStore.CellCount` and
  `TryKeyAt` enumerate a row's live members, which is how a count, an
  arrangement rank, a filtered reduce, and a `$match` word read a moved pile.
  A frame copies as one span copy, refuses a key its row lacks, and applies `boardCombine`,
  `writeSet`, `push`, `clearEnclosed`, and `transfer` (first, last, or key; never a draw) densely.
  A board declaring `Inverse` is derived,
  never written directly: `FrameLayout` resolves its tokens/codes row ordinals
  once, and a frame write to the tokens row recomputes only the moved token's
  old and new board cells (`DerivedBoards.Compose` is the equivalent
  whole-document answer a host composes into the candidate at install, so the
  two never disagree). `FrameHost` is an `IRuleHost` over a
  frame with its own evaluator and latch: a preflight scope is a frame copy,
  an effect arm the library does not own is skipped, and `Judge` evaluates a
  rule array as one tick with every edge closed. `OperandFact.HostOnly`,
  `KeyFact.HostOnly`, `EffectFact.ReadsHost`, and `RuleDataflow.ReadsHost`
  name what a frame cannot evaluate.
- *Analyses:* `RuleDataflow` (a rule's read and write sets), `RuleHazards`
  (the write-after-read and write-after-write pairs document order decides,
  skipping pairs whose gates pin one cell to disjoint ranges), and
  `RuleWorkBudget` (the pinned-cell exclusion trie, contributor lines, writer
  counts, gate/expression/effect costs) over the facts' own answers. A non-board
  pattern is priced by its source row's declared capacity times one match step
  plus the actual per-token expression cost, with saturating arithmetic.
  Its read set includes the source, attribute row, key indirection, and token
  expression dependencies.
- *Tables:* `TableDocument` (`puck.table.v1`), `TableEntryDocument`, and
  `TableCanonicalizer` (validate → normalize → canonicalize), with
  `TableRow` as the name/source/hash reference a document pins one by and
  `CompiledTable` as the sorted, binary-searched loaded form.
- *State transforms:* the `StateTransform` union (`transfer`, `setRay`,
  `shuffle`, `sortZone`, `sortKeyed`, `writeSet`, `boardCombine`, `arrange`,
  `push`, `clearEnclosed`, `observe`),
  `ZoneSelector`, and `SortKey`. `clearEnclosed`'s `from` and `writeSet`'s
  `setKey`, and a transfer's `key` accept a dynamic key (`TransformStateEffect.KeyRef`,
  resolved live in `RuleEvaluator.Effects.Fire` before the mutation
  submits) alongside a literal cell: a rule gated on the held token paints
  `plan` from `legal[$cell:held:token]` after a `boardCombine clear`.
- *Identifiers:* `SafeName` and `CellName` (validated at construction,
  refusing by name; `SafeName.MaxSuffixLength` reserves the file suffix a
  document project may append), their JSON converters, and the
  `TryParseStringJsonConverter<T>` shape they share.
- *Reserved channels:* `RuleFacts`, the state-neutral `$`-prefixed channels
  a rule may read instead of a declared row (`$tick`, `$bind:`, `$table:`,
  `$cell:`, `$reduce:`, `$match:`, `$history:`, `$symmetry:`, `$phase:`,
  `$board:`). The channels only a world can answer — bodies, distance, line
  of sight, screens, links, regions, the population — are the document
  project's.
- *Literals and spellings:* `NumericLiteral`, the one decimal→Q48.16
  conversion every authored constant and table value crosses, and
  `StateSpelling`, the one home for how a refusal spells a kind, a generator
  source, or a cycle output.

## 🔌 How a document project extends it

Extension is by registration and derivation, never by an edit inside this
package:

- A `RuleVocabulary` registers the families a document project adds: an
  `OperandFamily` claims the reserved spellings it answers and compiles them
  to its own `OperandFact`s; an `EffectFamily` owns an `ActionEffect`-derived
  arm, its `$type` discriminator, the `TransactionStep` that mirrors it, and
  its compile to an `EffectFact`; a `PredicateFamily` owns a predicate arm
  (which may refuse in rule scope); a `KeyFamily` answers a dynamic-key
  spelling with a `KeyFact`. Registered families are consulted before the
  library's own. `RuleVocabulary.ExtendJson` is the `JsonTypeInfo` modifier a
  document project's serializer installs so the registered arms read and
  write under their discriminators.
- A `RuleCompileContext` is derived to anchor what only the document knows
  (`FindTopology` in a placement's frame, `FindDraw` through a lattice fill)
  and to carry the per-compile scope the families cache into.
- A host implements `IRuleHost` — `IRuleReader` widened with the mutation
  door (`TryApply`, composing privately under `BeginPreflight`/`EndPreflight`
  or installing), `FireEffect` for the effect arms it registered,
  `TryEvaluateOwn` for the rule kinds only it understands (which run each
  evaluation back through `RuleEvaluator.EvaluateOnce`, so latch, trace, and
  ledger stay one mechanism), and `RefusalRecorded` — and widens the reader
  further to whatever its registered operands read through; a compiled fact
  casts the reader it is handed. A host owns one `RuleEvaluator` and one
  `RuleLatch` per family it evaluates.
- `CompiledRule` is non-sealed: a document project's rule overrides
  `CollectReads`, `CollectWrites`, and `Cost` to add the branches it alone
  carries, and every analysis follows.
- A polymorphic base declared here (`LatticeTopology`) lists only the cases
  this package owns; a document adds its own case as a derived record through
  the same modifier. `StateRow` is non-sealed on the same terms, with
  `StateRowJsonConverter<TRow>` as its converter base.

## 🧭 What stays a host's

Composing a `StateMutation` against a row — eviction, a numeric upsert's
advance/dynamics rebase, envelope validation, the journal — is each host's
door; the pieces are here (`StateCellWriter`, `StateAdvance`,
`StateRow.ClampToEnvelope`), the pipeline is not. Pairwise interactions and
timed decisions are host-evaluated kinds until a pair domain over rows is
designed rather than assumed. Refused: a compatibility shim between old and new
spellings.

## 🔗 Where the rest lives

The world's registered families (`WorldRuleVocabulary`, `WorldRuleCompileContext`,
`IWorldRuleReader`), its rule and decision records, and the validators live in
[`src/Puck.World.Schema`](../Puck.World.Schema/README.md); `WorldServer` is the
host (`WorldServer.RuleHost.cs` in
[`src/Puck.World.Server`](../Puck.World.Server/README.md)).
`tests/Puck.State.Tests` proves the expression syntax, the function families,
the hex topology, and the evaluator over a headless host (a row list, no
world); the converter and schema facts that need the world
document's JSON context stay in `tests/Puck.World.Schema.Tests`.
