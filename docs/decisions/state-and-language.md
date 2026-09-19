# State and the authoring language: decisions

The choices behind [the state and language programme](../plans/state-and-language.md),
each with the problem it answers and what follows from it. A current contract
lives in the manual; a decision here is why the contract has the shape it has.

## The rebuild

**D1 — One store.** The old substrate held state three ways: a document, a
frame over it, and per-transform copies. A columnar `StateArena` is now the
runtime truth for every cell in the document lane and the per-participant
lanes; the document's row list is its serialization, produced by export and
consumed by import; a hypothetical is a journal scope on the same arena, and
installing a mutation is committing one. Search, the browser session, and the
server run the same code over the same structure, so there is no `RowStore`,
no `FrameHost` distinct from the host, and no transform with a document-path
twin. A host-owned row is one a document project declares as served by a
named facet: the arena holds its descriptor and no columns, a rule that reads
it names the facet in its needs, no rule writes it, the arena hash excludes
it, and its owner (the server, for the physics lattice) folds it into the
world hash itself. Only `Lattice` and `Slot` shapes may be host-owned, because
a host can serve those without an ordering contract; other shapes wait for a
case.

**D2 — One cell representation and one shape axis.** Four enumerations once
described what a cell was. `CellValue` is a closed union with one case per
`CellKind`, written to the repository's `[Union]` pattern so "what is a union
here" stays one grep, and the arena stores each kind in its own column set. A
vector case carries opaque bytes, with the typed view owned by
`Puck.State.Vectors`. `RowShape` (`Slot`, `Keyed`, `Ordered`, `Lattice`,
`Ring`) is derived once from the authored domain and is the only shape
enumeration; the participant role is a field on the descriptor. `StateEnum`
joins the document model, so a symbolic value is validated on write and
decompiles to its member name.

**D3 — Capabilities are types.** A rule used to reach a host through booleans
and self-reported lists. `IStateReader` now carries only what every host
answers (the arena, the catalog, the tick pair, the bound-key context, the
per-evaluation scratch) and one question, whether the host advertises a facet.
A document project declares its facets as interfaces; an operand or effect
that needs one names it in its type and receives it as a typed argument, so a
fact cannot reach a facet it did not declare and the compiler records a rule's
needs from those type arguments alone. Admitting a rule to a host compares its
needs against the host's advertisement and refuses by facet name; the same
needs drive scheduling.

**D4 — A rule firing is atomic.** One firing is one gate opening for one
iteration binding, and every effect it produces lands in one journal scope
that commits when every required effect succeeds and rewinds otherwise, with
the rewind one counted refusal naming the effect. The edge latch records the
crossing either way. An arm that leaves the arena is queued during the scope
and preflighted before it commits, in the order the arms fire, each against
what the arms before it would leave. Such an arm is one of two kinds, and the
atomic promise covers one. A transactional arm (a placement or HUD row) is
committed with the firing in two phases: the host prepares the firing's
transactional arms as one unit before the scope commits, where every gate that
can refuse them runs and a refusal rewinds the firing, and installs the
prepared unit after the commit through a step that cannot refuse. A delivered
arm (a cue, a pose, a body motion, a save) fires after the commit; a delivery
that refuses is one counted refusal that undoes nothing and stops no later
delivery, and the promise does not cover it. One class cannot serve both:
rollback can be promised only for an effect whose install is prepared before
the commit and cannot refuse after it. A `transaction` is a savepoint
inside the firing: a refusal inside it rewinds the savepoint, `onFailure` runs
in the firing's scope, later siblings continue, and the firing commits if they
succeed; a refusal inside `onFailure` or a later sibling rewinds the whole
firing. There is no `attempt` group: the inventory found no shipped rule with
a refusable transform at a non-first position, and the six candidates were
partial firing by accident.

**D4a — One publication boundary, and one proposal behind it.** A value written
in the arena reaches everything outside it through one routine, which installs
the rows that moved and then settles every consumer that keeps its own copy of
a value: the routine a value mutation also ends in. A consumer is added there,
never to one of the doors a value can arrive through. What a publication
installs is composed by a step that installs nothing, and a preflight judges
that same proposal, an open scope's writes included, so what a firing was
judged against is what its commit installs. Its cost follows the rows that
moved, by per-row versions, and not what the document declares.

**D5 — Ordinal addressing at runtime.** A compiled read or write addresses a
row ordinal and an interned cell key; a key minted at runtime is interned in
mint order, which is deterministic, and exported as an ordinary cell. Names
appear in exactly two places: the catalog that resolves them at compile time
and the replay and console boundary that renders them back.

**D6 — One comparison semantics.** Gates, bindings, and effect sources lower
through one value source and compare exactly: an Int cell against a fractional
literal widens to the fixed carrier, so `score > 2.5` means `score >= 3`, the
compiler folds it to that form before the program runs, and `puck lint` prints
the folded form. `compareState` and `compareValue` are two spellings of one
comparison. No shipped source compared fractionally against an Int cell, so
nothing argued for a looser rule.

**D7 — One expression IR.** `ExpressionProgram`, a postfix program, is what a
world document holds; the `.puck` grammar, the SQL dialect, and infix strings
are front ends that parse to it. Operator metadata (spelling, arity, kinds,
cost, fold) lives in one table the parser, compiler, folder, printer, and
evaluator consume. `puck.cartridge.v1` stays outside: its forge reads its
vocabulary as text, so it spells a program as infix through one converter
that writes the canonical spelling.

**D8 — Projects by concern, one layer.** `Puck.State` holds the model, arena,
catalog, reader, hash, the authored rule vocabulary, the expression IR's data
model, the fact base types and the four families, and the enums `Puck.Physics`
shares; `Puck.State.Rules` the front ends, folder, compiler, evaluator, latch,
budget, and dataflow; `Puck.State.Generators`, `Puck.State.Vectors`, and
`Puck.State.Search` what their names say; `Puck.State.Topology` the board-query
and pattern-automaton layer over an arena — rays and board shapes through one
span kernel, and the compiled pattern machine's incremental resume over an
arena-read word. Compiled lattice adjacency, tilings, and the pattern
algebra's own authored tree stay in core: a lattice row's column layout, a
board's inverse recompute, and a `.puck` pattern's infix parse and print each
read that declaration from inside `Puck.State` itself, so moving it would put
a core file's own compile or import path on a reference to the project meant
to depend on core, not the reverse. Each project declares the engine-services
layer and the reference graph is a DAG the architecture gate checks: core
references only abstractions, assets, and maths; topology, vectors, and
generators reference core; rules references those; search references rules.
Nothing in `src/Puck.World*` names a state type it did not name before except
through the facets of D3. Topology never references generators: a Penrose
patch takes its drawn start tile as a value.

**D9 — The extension seam keeps its four families.** `OperandFamily`,
`EffectFamily`, `KeyFamily`, and `PredicateFamily` keep their registration
shape and JSON discriminators. A family's compiled fact declares the facet it
reads, an effect fires against the arena, an irreversible arm declares itself
and is deferred to commit, and no family declares `AllowsTransaction`, because
every firing is a scope.

**D10 — Every world has an authored source.** The games that existed only as
JSON were decompiled to `.puck` once; every `.world.json` is generated by
`puck compile` from then on, and the lowered shape changes where the model
does, with no read-side tolerance for the old one.

**D11 — Rule groups are runtime constructs.** A fixpoint group evaluates its
members one pass per tick and closes when a pass changes no row version in its
write set, with an authored pass ceiling whose breach is a counted refusal
naming the group; a staged group holds a cursor over ordered steps that
advances when a step's effects commit, stalls on a refusal unless the step
declares `skip`, starts on an authored trigger, and has a terminal stage.
Group progress is checkpoint state beside the latch, hashed with it, never a
row. `stabilize` and `workflow` lower to these groups, and generated rows
carry a mark (D13) so those spellings stop refusing.

**D12 — Families are catalog ranges, and reductions fold.** A declared family
compiles to a contiguous ordinal range, so a live family index is an arena
read and a bounds check, and `$zones[i]` resolves through a family rather than
a per-rule string list; ordered and keyed member rows are admitted, and a cell
set over such a family is its members' current keys. The IR has a `Fold`
instruction that reduces a family in O(n) at evaluation, priced as n times its
subprogram, so `all`, `any`, `count`, and `sum` never unroll into tokens.

**D13 — Generated state is not authored state.** A row the runtime or a
lowering synthesizes carries a `Generated` mark; console listings, HUD
bindings, the decompiler, and the schema treat it as implementation detail,
while the hash and checkpoint still cover it.

**D14 — Boolean-closed algebras, spelled once, over three carriers.** The
compiled pattern already compiles sequence, choice, intersection, complement,
and bounded repetition by Brzozowski derivatives into a bounded machine over
at most 32 numeric symbol classes, with no external regex engine. The same
operator vocabulary spells patterns in `.puck`, cell sets (a family, a board, a
zone, or a filter over one), and match facets read from one derivative walk.
Derivatives step one symbol at a time, so a pattern over an ordered zone
resumes past its memoized state only when the row's append generation, a
counter that moves on any mutation other than a tail push, proves the prefix
unchanged; a row version alone never proves a prefix.

**D15 — Verification is replay of exported state, not hash equality.** With
one execution path there is nothing to compare against itself, and the
arena's hash differs from the frame's by construction. The comparable
artifact is the canonical exported state document after a fixed scripted
input sequence; the gate is the ported law suites, the acceptance corpus
compiled and decompiled without diagnostics, every shipped world's export
equal to the pre-rebuild export or different only where a named decision
moved it, the browser determinism canary, and `puck landing`. Work units are
exact and re-recorded with no tolerance, because the rebuild re-prices the
operator table by design; wall time is the advisory tolerance, the median
tick per world over five runs on the named recording machine within 1.25
times the pre-rebuild record, and a faster run on different hardware is not
evidence.

**Search.** A position is keyed over the rows its plan names, not the whole
arena; a tree job's draw seed is an authored plan field; chance nodes and
per-seat scores refuse by name until their package lands.

**Two spellings the rebuild fixed in shipped worlds.** The reveal gates in
`klondike` and `spider` read an absent zone endpoint and recorded a counted
refusal; a `when` gate now admits an expression comparison and they read
`?? 1`, which keeps the same answer. The ribbon a Penrose board slides along
is the de Bruijn ribbon, the long straight run through a rhomb's parallel
sides, numbered one row per axis; the literal chain of thin rhombs is never
longer than two tiles.

## Capacities

**K1 — Two real limits, and the budget is a price.** The per-tick work budget
and per-document memory bounds are what refuse a document. A count survives
only when its doc comment names the bytes it sizes or the share of the budget
it multiplies; a count that protects nothing is deleted. There is one per-tick
budget, and it is priced: [costing](#costing)'s reference schedule replaces the
heuristic work-unit count, so a ceiling's budget share is a share of reference
cycles and `MaxWorkUnitsPerTick` survives as that schedule's calibrated
per-step allowance rather than as a second, unpriced limit. Two currencies for
one deadline would let a document pass one and fail the other, which is the
surprise both limits exist to remove.

**K2 — A buffer sized by a document is heap, grown once.** No `stackalloc` is
sized by a topology's cell count, a token count, or an expression's length
times its nesting; 4,096 cells is 96 KiB on the stack and sixteen times that
is 1.5 MiB. Scratch is leased from the arena's own growing buffers
(`ArenaScratch`), returned in order so a nested evaluation never writes over its
caller's, and is covered by the allocation laws after warm-up. It is the
arena's own rather than `ArrayPool<T>.Shared` because leases nest by element
type: an expression that calls a function or folds a family rents a second
value stack while the first is open. The shared pool serves only the first
rental of a size class from its thread-local slot and takes a per-core locked
stack for the rest, which measures about four times a depth-indexed buffer for
three nested 64-word leases. A single small lease is about twice the cost, and
leases of thousands of elements cost the same either way, because clearing
them dominates.

**K2a — The memory bound is the arena's layout, in bytes.** `ArenaLayout.Bytes`
measures every column at its full width, the change stamps a settle keeps per
position, the row and key indexes, the vector components, and the strings its
reference columns point at, with a reference counted as eight bytes on every
host so a document measures the same wherever it is admitted. A string is
counted at the length ceiling its write doors hold it to: a provenance for
every cell slot, a text for every slot of a text row. `ArenaCapacity.MaxBytes` bounds it, and a section is refused at
the row that crossed before anything is allocated for it. A count of cells
cannot stand in for it: a cell slot costs about 250 bytes once every column a
row may use is allocated, and lanes, draw masks and vectors are not cells. The
undo record has its own ceiling (`ArenaCapacity.MaxJournalBytes`), which counts
its entries, its snapshotted vector components, and the texts and other
references it overwrote, since the record alone keeps those alive. It is read
between effects: a write is never dropped or interrupted, because a member write spans
several columns and a torn one would be read by whatever ran next, so the
record may pass the ceiling by the writes of the one effect that crossed it
before the firing is refused and rewound.

**K3 — A set of cells wider than a word lives in a board row.** An expression
value stays one 64-bit word and the bit operators stay generic Int operators.
A cell set over a board of any size is a board row, one cell per cell,
combined with `boardCombine` and the cell-set algebra at a width taken from
its topology. `$board:mask`, `writeSet` from an Int cell, and the search's
`Legal` row remain the convenience for boards of at most 64 cells and say so.
No second value kind enters the expression language.

**K4 — Generator drawn masks stay 256 wide.** They are a format in three
places and nothing presses on them.

**K5 — A match answers where, never what.** The pattern algebra keeps its
bounded derivative machine with intersection and complement, so it gains no
captures. A match reports the position and length it matched at, and a rule
reads the cells at offsets from that position.

**K6 — Undo is scoped and admitted.** A retained journal segment belongs to a
declared set of rows. A turn that wrote outside the set cannot be undone, and
a group that declares undo may hold no irreversible arm and no host-owned
write, refused at load. The retained ring is state: hashed, checkpointed, and
dropped by a relayout. Undo carries no engine policy of its own: who may undo
is the gate of the rule that fires `rewindTurn`, so one world lets the player
undo, another requires both seats, and a third never opens the gate. Depth is
bounded in bytes by the journal ceiling, refused at load naming both figures
when a declared depth's worst-case turns cannot fit; there is no separate
count of turns.

**Every ceiling is per document, counted after modules expand.** A module
used twice counts its rows twice; a pool counts its rows once and its capacity
in the budget; the refusal names the module instance that crossed the ceiling.

**A rule's working value is a `local`.** `bind` collides with the input
system's bindings and chords, authored in the same sources, and `let` is the
language's compile-time constant, so a `let` inside a rule would read as
evaluated at compile time. The declaration is `local name : Kind = expression`,
read by its bare name, ceiling `MaxLocalsPerRule`; the old spelling is
rewritten once with no alias.

**Go ships at 19×19.** The 9×9 answer rested on the 256-cell set the pattern
algebra lowered to. C2 widens that carrier for reasons of its own, so the full
board is no longer engine work one game has to justify, and Go waits for C2
rather than shipping small twice.

## The language

**P1 — The document is the truth; a surface owes it two things.** Every
surface construct lowers to the document and prints back from it, and
`compile(print(compile(source)))` equals `compile(source)`. A construct
without a printer is not admitted. Nothing else about a surface is a
contract: names, keywords, and shapes change freely, rewritten once across
the sources with no alias.

**P2 — Two layers, reshaped differently.** The vocabulary layer (`rule`,
`table`, `pattern`, `local`, a gate, an operand) is a projection of the
document and reshapes by changing its one description. The compile-time layer
(`let`, `module`, `for`, `function`, imports, units, `sql { }`) exists only
in the source, is evaluated away, and reshapes by rewriting the syntax tree. A
decompiled document shows the expansion.

**P3 — Formatting is printing.** The formatter parses a source and prints its
tree, comments and blank lines included, so it cannot produce a source that
means something else. It once split `next{12}` across lines, which stopped
compiling, and unindented a wrapped `export` list, which silently emptied it.

**P4 — Operands are grammar.** The colon channels (`$reduce:count:…`,
`$match:…:distance[…]`, `$history:row:age`, `$bind:`) were strings the state
compiler parsed, and where authors met literal-only arguments. They become
function forms in the source and structured nodes in the document, so nothing
anywhere is a string with its own grammar.

**P5 — One door.** Every consumer that compiles a source calls the same entry
point with the same options; three test harnesses once compiled a source
differently from `puck compile`, unnoticed until a game used an `sql { }`
block.

**The shape the other plans demand is a module language over a closed
vocabulary.** Eight plans need composition, five need sources of hundreds to
thousands of rows, three need costs and errors traced back to the authored
line. So: modules with typed parameters and export lists, instantiated under
an alias with names rewritten locally, nesting; functions and modules
compile-time, total, and evaluated away, while a cartridge procedure is a
document row that exists at run time; generators legal wherever a list is;
payload by reference; records and pools; and kept out, a second scripting
language inside state, game nouns in the grammar (they are libraries), cost
refusals, and a graph language.

**Construct distance.** A built-in construct maps to one document member and
prints back locally. Sugar that would need to recognize a cluster of rows
means the document lacks a member, and the member is added (`ruleGroups`,
`pools`). Anything larger is a module.

**Two kinds of instance.** A module instance exists at compile time, fixed in
the document, its rows copied per alias, costing document size. A pool
instance exists at run time, claimed and released, one set of keyed rows with
a capacity, its rules written once and run per live instance, costing per-tick
work bounded by the capacity. They compose: a module may declare a pool and
take one as a parameter. Claim and release are effects, whether the cause is
a join or a rule, and the firing rule's gate is the authority. An unclaimed
instance is absent, not empty.

**Nothing runs inside another world.** Worlds link by federation; modules nest
at compile time; a link (`border`, `door`) is one declaration generating both
halves; each world has its own budget. What follows a player is what
`identity { }` declares, because the owned identity is what the hand-off
already carries, so nothing is marked as travelling.

**The spellings.** Statements are keyword-led blocks: the keyword at the start
of a line is what the construct table, a diagnostic, and a reader key on, and
composability comes from parameter kinds (`Module`, `Pool`, `Row`, `Gate`).
The alternative, every declaration a value (`let cabinet = module(…) => { }`),
is more uniform but makes every line an expression to evaluate before knowing
what it declares. `template` folds into `module`: `use` with an alias
instances, without one stamps; `import` brings names into scope and `use`
makes instances. A rule over a pool names its instance on the rule line,
because the name is what the body reads. A source with `world` statements
emits several documents and one with none emits one; there is no separate
file kind.

**`sql { }` is compile-time layer.** One row on the construct table, the
`sql` keyword delegating to the SQL parser, with the projection law running
through the expansion. A paradigm earns a parser; a genre earns a library.

**Tests are worlds.** A `test` block lowers to a generated document, so the
runner is the engine's own boot, verdicts are state, a failing test is a
world one can open, and the same test through both hosts is the parity check.
C# keeps the engine laws; a shipped game's behavior is pinned in its own
language.

**Nine author-facing candidates are adopted into existing packages** and one
deferred; [the table](../plans/state-rebuild.md#candidates-adopted-into-packages)
says where each landed.

## Costing

**A weighted semantic execution model over a portable ideal machine.** Price
the compiled operation Puck performs, including its evaluator work, on a
reference that is no named processor: platform names, compiler targets, and
measured clocks belong in offline evidence, and production pricing carries
semantic operations and abstract service rates. Directly pricing the host's
JIT output or benchmark timings is excluded from authoritative pricing and
kept as diagnostics; simulating an out-of-order processor is deferred as more
machinery than the contract needs. The reference throughput, three billion
cycles per second with half reserved for authored work, is product policy
until a measurement argues otherwise, named as versioned constants and never
disguised as measurements.

## Games

**Known games only, one exception.** The rules of each are public, so
"correct" needs no design discussion; Ribbon is original because no known game
uses an aperiodic board, and states its complete rules. A game that needs a
construct the transpiler refuses waits for the package that lands it; no
package authors a hand-rolled substitute. Codenames resolves its embeddings
through `puck embed` with the fixture generator, never a network service.

**Three games are scheduled and six deferred.** Go, Tetris, and Baba Is You
carry the constructs the forcing world cannot reach on its own; the rest are
scheduled unchanged when a capability they force is otherwise unproven.

---

[Decisions](README.md) · [The programme](../plans/state-and-language.md)
