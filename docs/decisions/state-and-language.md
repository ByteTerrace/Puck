# State and the authoring language: decisions

The choices behind [the state and language programme](../plans/state-and-language.md),
each with the problem it answers and what follows from it. A current contract
lives in the manual; a decision here is why the contract has the shape it has.

## The rebuild

**D1 — One store.** The old substrate held state three ways: a document, a
frame over it, and per-transform copies. A columnar `StateArena` is the
runtime truth for every cell in the document lane and the per-participant
lanes; the document's row list is its serialization, produced by export and
consumed by import; a hypothetical is a journal scope on the same arena, and
installing a mutation is committing one. Search, the browser session, and the
server run the same code over the same structure, so there is no second row
store, no frame host distinct from the host, and no transform with a
document-path twin. A host-owned row is one a document project declares as
served by a named facet: the arena holds its descriptor and no columns, a rule that reads
it names the facet in its needs, no rule writes it, the arena hash excludes
it, and its owner (the server, for the physics lattice) folds it into the
world hash itself. Only `Lattice` and `Slot` shapes may be host-owned, because
a host can serve those without an ordering contract; other shapes wait for a
case.

**D2 — One cell representation and one shape axis.** One enumeration
describes what a cell is, not four. `CellValue` is a closed union with one case per
`CellKind`, written to the repository's `[Union]` pattern so "what is a union
here" stays one grep, and the arena stores each kind in its own column set. A
vector case carries opaque bytes, with the typed view owned by
`Puck.State.Vectors`. `RowShape` (`Slot`, `Keyed`, `Ordered`, `Lattice`,
`Ring`) is derived once from the authored domain and is the only shape
enumeration; the participant role is a field on the descriptor. `StateEnum`
joins the document model, so a symbolic value is validated on write and
decompiles to its member name.

**D3 — Capabilities are types.** A rule reaches a host through typed facets,
never booleans or self-reported lists. `IStateReader` carries only what every host
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
world document holds; the `.puck` grammar and infix strings are front ends that
parse to it. Operator metadata (spelling, arity, kinds,
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
span kernel, and the compiled pattern machine's walk over an arena-read
word. Compiled lattice adjacency, tilings, and the pattern
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

**D10 — Every world has an authored source.** A world is authored as `.puck`,
and a `.world.json` is a cache that `puck compile` derives from it
([a derived product is a cache](engine-design.md#a-derived-product-is-a-cache-never-the-truth)).
The lowered shape changes where the model does, with no read-side tolerance for
an old one. The tracked `.world.json` files that remain are quarantined under
`experimental/`, fixtures whose subject is the JSON document shape itself
(`tests/Puck.World.Tests/RoundTripDocuments`, the Dashboard portal's workspace),
or documents that still owe a `.puck` source compiled when needed:
the browser-parity presentation document is a
world in its own right, not a shape fixture.

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
A match walks its word from the first letter on every read and keeps
nothing between reads: each read re-reads the whole word off the arena, so a
remembered prefix state would save only steps that cost no more than the read
that precedes them.

**D15 — Verification is replay of exported state, not hash equality.** With
one execution path there is nothing to compare against itself, and the
arena's hash differs from the frame's by construction. The comparable
artifact is the canonical exported state document after a fixed scripted
input sequence, replayed at a fixed code version: `puck test --reproduce` runs
a world twice and compares the exports byte for byte. A deliberate change to
the operator table or to the state's meaning moves the export and is
re-recorded in the change that makes it (AGENTS.md rule 4). Work units are
exact, because they are counted, not timed. Wall time is a measurement read on
a quiet machine, useful as a before-and-after comparison within one change,
and never a recorded threshold a later run must stay within.

**Search.** A position is keyed over the rows its plan names, not the whole
arena; a tree job's draw seed is an authored plan field; chance nodes and
per-seat scores refuse by name until their package lands.

## Capacities

**K1 — Two real limits, one semantic allowance.** Per-document memory bounds
and one deterministic semantic work allowance refuse work for a reason an author
can inspect. A count survives only when it names the storage it sizes or the
allowance share it bounds. Admission, execution, search, and reports use that
one allowance; a unit that cannot fit refuses atomically or resumes from a
complete continuation at a defined boundary. Physical headroom is measured
separately and never chooses authoritative prices from the executing host.

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

The [engine job-system memo](../plans/open-items.md#engine-job-system) extends
K2's single-owner scratch contract: fixed partitions independent of worker
count, declared read epochs, partition-owned scratch and explicit borrowing
and handoff, stable commit, allocation, key-minting and draw order, and snapshots
only at publication boundaries. Residency, caches, and jobs reserve from one
physical byte-accounting owner rather than adding competing budgets.

**K2a — The memory bound is trait-admitted storage, in bytes.**
`ArenaLayout.ColumnsOf` reserves optional columns only for declared traits:
text, clocks and behavior, visibility and observation, draw cursors, phases,
and row-shape links. There is no per-slot provenance column. Layout bytes count
the columns actually admitted, indexes, vectors, and bounded reference payloads;
`StateArena.Bytes` adds bounded declaration and live visibility payloads.
Pair pools allocate `MaxLive` slots with explicit endpoints, rather than the
Cartesian endpoint universe (`StateCatalog`). Layout overflow refuses before
allocation, and construction, import, growth, and writes all enforce the byte
bound. See [byte accounting](../reference/state/arena.md#byte-accounting).

The journal has its own byte ceiling, including retained overwritten payloads.
It is checked between effects: an effect completes its member writes, then a
firing over the ceiling refuses and rewinds atomically.

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

**K5a — Token-set writes are pool iteration.** `for each` snapshots the pool's
original live, generation-checked handles, and a typed field condition selects
the noun to rewrite. The matching writes share the rule firing's atomic scope,
so a later refusal restores them together. `writeSet` stays a board-cell
operation; giving it a second token mode would duplicate the pool iterator and
its lifetime rules. Separate noun rules remain separate firings in game
authorship.

**K6 — Undo is scoped and admitted.** A retained journal segment belongs to a
declared set of rows. A turn that wrote outside the set cannot be undone, and
a group that declares undo may hold no irreversible arm and no host-owned
write, refused at load. The retained ring is state: hashed, checkpointed, and
dropped by a relayout. Undo carries no engine policy of its own: who may undo
is the gate of the rule that fires `rewindGroup`, so one world lets the player
undo, another requires both seats, and a third never opens the gate. Depth is
bounded in bytes by the journal ceiling, refused at load naming both figures
when a declared depth's worst-case turns cannot fit; there is no separate
count of turns.

A pending retained segment may span ticks; an ordinary arena journal scope never
does. Settled passes with no retained writes consume no history. A successful
rewind suppresses its group for the rest of that tick. Pool reservations count
the columns their mutation doors can reach; ordinary rows also reserve their
mutable metadata. Checkpoint imports use the same storage plan.

**Pool body bindings are logical enum mappings.** One `properties.carriers`
declaration maps every member of a pool field's enum to a named inhabited
placement, a local seat, or detachment. It compiles to field and placement
ordinals. The live generation supplies the current field value; a released
instance cannot retain an attachment. Generation-pinned document attachments and
raw physical body-index fields have no parallel runtime path.

**Every ceiling is per document, counted after modules expand.** A module
used twice counts its rows twice; a pool counts its rows once and its capacity
in the budget; the refusal names the module instance that crossed the ceiling.

**A rule's working value is a `local`.** `bind` collides with the input
system's bindings and chords, authored in the same sources, and `let` is the
language's compile-time constant, so a `let` inside a rule would read as
evaluated at compile time. The declaration is `local name : Kind = expression`,
read by its bare name, ceiling `MaxLocalsPerRule`, with no alias for an older
spelling.

**Go ships at 19×19.** A 9×9 answer would rest on the 256-cell set the pattern
algebra lowered to; C2 widened that carrier for reasons of its own. The debug
world has the [full physical table](../../worlds/debug/go.md), with all 361
stones, and Go does not wait on engine work.

## The language

**P1 — The document is the truth; a surface owes it two things.** Every
construct that writes an authored row or value lowers to the document and
prints back from it, and `compile(print(compile(source)))` equals
`compile(source)`; such a construct without a printer is not admitted. Two
things print from a document, and nothing else does: `world.save`, which writes
a changed value or a new row into the source that authors it and leaves every
other character as written, and `puck decompile`, which imports a hand-written
JSON document no source authors. Neither ever prints a row a construct
generated: a save refuses an edit to one by name, and a hand-written document
holds none. So a construct that generates rows (`ground`, `test`, a
composition's `world`, `border` and `door`, an aliased `use`) is written in
source alone, like the compile-time layer, and prints back from nothing; a
generated name it leaves in a document is refused by name. A rule scope's
members are authored rules under a joined name and still print back. Nothing
else about a surface is a contract: names, keywords, and shapes change freely,
rewritten across the sources with no alias.

**P2 — Two layers, reshaped differently.** The vocabulary layer (`rule`,
`table`, `pattern`, `local`, a gate, an operand) is a projection of the
document and reshapes by changing its one description. The compile-time layer
(`let`, `module`, `for`, `function`, imports, units) exists only
in the source, is evaluated away, and reshapes by rewriting the syntax tree. A
decompiled document shows the expansion; a saved one keeps the layer as
written, since a save edits only the changed nodes.

**P3 — Formatting is printing.** The formatter parses a source and prints its
tree, comments and blank lines included, so it cannot produce a source that
means something else. A formatter that splits `next{12}` across lines stops
the source compiling, and one that unindents a wrapped `export` list silently
empties it.

**P4 — Operands are grammar.** The colon channels (`$reduce:count:…`,
`$match:…:distance[…]`, `$history:row:age`, `$bind:`) were strings the state
compiler parsed, and where authors met literal-only arguments. They become
function forms in the source and structured nodes in the document, so nothing
anywhere is a string with its own grammar.

**P4a — A string is text.** A call argument that holds a name, a cell key, a
value expression or an enumeration word is written bare, and one that holds
text is a string literal; the document model says which, so no list of
members is kept beside it. The same holds for an object literal's property
and a block's property line: lowering carries the model type each value
fills, so a member is classified where it stands rather than by the call
that happens to hold it. Two spellings of one argument made every reader
carry both and let the quoted one hold a grammar of its own (`"$local:x"`),
which is what P4 retired everywhere else. An interpolated string computes a
token, never a program: a loop mints `piece{i}`, which no bare word can say,
so the string is a name's or a key's whole value, or an atom inside a bare
expression (`pieceCell[$"piece{i}"]`). An expression built as text was a
second spelling of every expression, read in the document's dialect, opaque
to the tools that read a bare one, and open to whatever syntax a hole
computed; an atom's result is one name or one number by construction. A
compile-time binding is read by its own name inside a bare operand and
shadows a row of the same name; an enumeration word is never shadowed, and
one bare word that is a key is never a binding.

**P5 — One door.** Every consumer that compiles a source calls the same entry
point with the same options; a harness that compiles a source differently
from `puck compile` hides the difference until a game uses the construct it
touches.

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

**Pool storage uses fixed identity slots.** Single pools reserve their declared
slots; pair pools reserve `MaxLive` slots with explicit left and right endpoint
columns. Endpoint-universe size validates possible relationships but does not
size storage. Claim and release never shift another instance. Handles include
generation, snapshots visit live slots in order, and free-slot scans and
dependent-pair release remain bounded, charged work.

**Pool generations are continuation state.** Releasing a slot advances its
generation even while the slot is dead. The arena hash therefore includes the
generated generation row: a reclaimed generation-one handle and a fresh
generation-zero handle have different future stale-handle behavior and are not
the same simulation state. Replay, checkpoint, and rewind preserve that row.
Tests that compare logical live contents may project the live set, but the
authoritative hash never discards allocator continuation.

**Latch bindings always hash both generation lanes.** A latch distinguishes a
released instance from its replacement even when their slot and visible values
agree. Its canonical binding shape includes left and right generations for every
entry; ordinary bindings use zero. Keeping one shape avoids a world-dependent
hash format. Reordering insertion does not change the latch hash.

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

**Tests are worlds.** A `test` block lowers to a generated document, so the
runner is the engine's own boot, verdicts are state, a failing test is a
world one can open, and the same test through both hosts is the parity check.
C# keeps the engine laws; a shipped game's behavior is pinned in its own
language.

Author-facing work belongs to the existing language packages. Their remaining
cross-host and scenario gaps live in the [state plan's open items](../plans/state-and-language.md#open-items).

### The authoring consensus

The owner asked for the language to genuinely improve, with big changes
welcome: "Our main goal is for the language to truly grow and improve." The
work and its order are [S9](../plans/state-and-language.md#s9--the-authoring-consensus).

- **One expression language.** A gate is any expression; `and`, `or` and `not`
  are operators. The predicate grammar, the kind suffix and the
  `((e) == 1 : Int)` idiom go; `compareState` and `compareValue` are the
  emitter's lowering choice. Truth conversion, short-circuiting, absence and
  kind inference are specified before the old grammar is deleted, and only
  provable 0/1 uses of `&`/`|` migrate.
- **A local reads where it is written.** A rule's locals evaluate in the order
  the source shows, or a reading the source would mislead is refused.
- **One bracket rule.** `row.key` is a literal key and `row[expr]` evaluates its
  expression. Names resolve lexically, a function only in call position, and an
  unknown bare word is an error, never a literal key.
- **Every condition is that expression**: HUD visibility, placement responses
  and element text included.
- **Compound assignment in world rules.** `-=` lowers to `addState` with a
  negated operand, keeping `+=`'s envelope; the destination key is evaluated
  once.
- **Names, not numbers.** Bodies, spawns, kits, cameras, machines and screens
  are named by the feature that declares them and allocated by the compiler;
  built-in reads become dot reads on named subjects (`liftCar.y`).
- **One composition construct.** `template` folds into `module`; `use`
  instantiates and `import` brings declarations only. A feature is one module,
  owning its sections and identities, and can be instantiated twice.
- **Tests say what a player does and when a claim holds.** Steps are typed
  calls on the existing verbs, checked against their signatures; a bare
  expectation holds at the end and `eventually` marks one that holds on some
  tick, reporting its witness tick.
- **No JSON showing through.** Keywords are contextual, sections take arrays
  directly, and noise fields take defaults. PUCK040 stays (a literal's
  punctuation marks its shape; a binding keeps its colon).
- **State sugar is complete**, so no source writes `row { }`; `: Kind` is
  always allowed.
- **One rule grammar for worlds and cartridges**, each target refusing what its
  machine cannot do. Cartridge rules take whole expressions over compiler
  temporaries, and their variables are `slot`s.
- **One world-authored authorization declaration** produces an addon's requests
  and its grants. An imported addon's requests never authorize themselves.
- **Creation shapes share the world's axes** and take unit-bearing rotations.
- **`function`**: compile-time, inlined, non-recursive.
- **Saving edits the source.** `world.save` rewrites the source tree, so a save
  keeps `let`, `for` and modules. That work decided P1's printer: a save prints
  only changed values and new authored rows, so the decompiler's read-back of
  generated rows (ground, test, composition links) went, and P1 now covers the
  constructs that write authored rows. The printer wraps long lines.
- **`sql { }` is deleted.** It is a second spelling of rows and rules that never
  printed back.

### Expression semantics

A condition converts its result to truth: numeric zero and absence are false;
nonzero Int and Fixed values are true. `and`, `or` and `not` produce Int 0 or 1.
`and` reads its right operand only when the left is true; `or` reads it only
when the left is false. A skipped operand neither reads state nor faults.
Their operands may use different numeric kinds, while each arithmetic operation
requires matching operand kinds. Both operands must still name declared values
at compile time. Other arithmetic keeps its existing absence propagation; use `??` to
provide a missing value or `isAbsent` to test it.

A fault in an evaluated gate conjunct closes the whole gate, under negation and
disjunction too, through the shared gate evaluator. Ordinary rules, group
triggers and conditional effects obey the same verdict. Thus `(not (1 / a > 0))
!= 0` and its printed truth condition both stay closed with `a = 0`, reporting
`Arithmetic`; printing cannot turn a failed comparison into a firing.
A fault is not a false: closing the gate means the rule does not fire, never
that the gate read false. So anything that remembers a verdict keeps the last
one it read successfully. An `Edge` rule whose gate faults neither fires nor
re-arms (treating the fault as false would re-arm it and fire on recovery), and
a world decision that faults keeps its choice and its interrupt edge and runs
no `onNoChoice` (treating the fault as "no choice" would turn it into an
answer). An effect that faults refuses the whole firing, except inside a
`transaction`: there the fault is a refusal of the savepoint as D4 describes,
so the savepoint rewinds, its `onFailure` runs and the firing can commit; a
fault in `onFailure` refuses the whole firing. `onFailure` is the authored
refusal arm, not a reading of the fault as false. A firing's
verdict covers everything the firing decides: the arena scope, the edge latch,
a decision's choice and the arms it queued commit through one door after the
verdict is in, or not at all.

The mechanism is [one outcome, one interpreter and one journal](#rule-faults),
with its precise behavior and examples in
[Reads and expressions](../reference/state/expressions.md#constant-folding-and-faults),
[Rules and firing](../reference/state/rules.md#make-a-firing-atomic) and
[the host boundary](../reference/state/hosting.md#the-install-boundary).
The printer elides a numeric truth test only when its operand reparses as the
identical test. Explicit compound expression boundaries remain, because fact
comparisons have special absence and infinity semantics.

From weakest to strongest, operators bind as: conditional `?:`, `??`, `or`,
`and`, prefix `not`, equality, ordering, `|`, `^`, `&`, shifts, addition and
subtraction, multiplication and division, arithmetic prefix operators. Thus
`mask & flag != 0` compares the masked value, and `not x == y` negates the
comparison. Parentheses override that order.

State declarations and earlier locals determine operand kinds. Numeric
literals take the expression's carrier; an Int carrier rounds them to even,
while a comparison against an Int cell keeps its exact threshold. Comparisons
and truth operators yield Int even when they read Fixed operands. Int and
Fixed state reads do not silently convert into one another. An unconstrained
integral expression infers Int; a decimal spelling such as `1.0` selects Fixed
for an unconstrained expression. A gate converts its result to truth without
a kind annotation.
Rule locals run in declaration order before the rule's gate and effects.
Declarations after a gate or effect are refused: move the declaration before
the gate to request that pre-effect read. A local may read earlier locals,
never a later declaration. This keeps the runtime's existing evaluation
model visible in the source.

### Rule faults

Every authored evaluation returns an `Outcome`; the fault is a case of that
value, rather than a status register beside it. `Outcome` and `Resolved<T>`
default to faulted and expose their value only through `TryFinite` or
`TryNamed`. Absence is a separate value: truth operators convert it to false,
`isAbsent` and `??` answer it, and other consuming arithmetic returns absence.
At a gate comparison, both source programs run before combining their answers;
a fault on either side takes precedence over absence and stops the gate
before negation or disjunction. A fault is absorbing, rather than an unknown
that Boolean logic can recover. `RuleEvaluation.Decide` owns that combination.

The old status-register approach required two channels to agree by convention.
A nested success could overwrite a shared fault variable, the register lived
on an arena that installation could replace, every deciding caller had to
remember to poll it, and a read-side flag could not order latch and decision
writes. The correction removes those mechanisms: `RuleExpressions.Run`
returns each child outcome as a value, and `RuleEvaluator.Commit` reads the
proposal's `FiringDecision` once. Faulted and fires are distinct cases, with
no faulted-and-fires combination.

There is one token interpreter. `RuleExpressions.Evaluate` takes an
`EvaluationFrame`; `EvaluateDetached` enters the same loop for the closed
consumer list in `OutcomeDetachedAllowlist`. Computed addresses lower into
tokens ahead of the operand, and `RuleExpressions.ReadOperand` resolves all
declared `IRuleOperand.Addresses` before handing the operand `ResolvedKey`
values. A failed address cannot become a successful read through a second
evaluator. `RuleExpressions.FoldWord` likewise owns per-token pattern programs,
including host match read-back. Pricing follows those executed tokens through
`RuleWorkBudget.ExpressionCost`; computing an address must cost its actual
work, even when correcting that price makes a document exceed its budget.
An ergonomic spelling and its unrolled equivalent owe the same work.

Atomicity covers everything a firing decides, not only cells. Latch bits,
decision bindings and group progress enlist through `IJournalParticipant`
and `ArenaJournal.RecordParticipant`; `StateArena.Rewind` restores all of
them at every nesting depth. A separate proposal copy installed at commit
would not restore a child firing when its parent search or savepoint rewinds,
and would make the state library know host-private decision types. The generic
journal participant avoids both problems. A `transaction` handles its body's
fault as a savepoint refusal and then runs its authored failure arm; a fault
in that arm still refuses the whole firing.

`RuleEvaluator.FireEffects` keeps a decision's binding, common effects and
option effects in one scope, using `IFiringPrelude` and `IFiringContinuation`.
The continuation checks the pair against what the common effects propose to
install, rather than today's population. `WorldRuleHost.ProposedPairLive`
composes queued document rows and calls `WorldPopulation.ReconcileKeeps` with
the open arena. `WorldPopulation.Reconcile` runs over live or forecast slots,
so structural reconciliation, cell-driven counts and census clamping have
one implementation. The forecast precedes the option's own consequences and
assumes counts settled at this commit; actual tick publication can occur later.
The [host reference](../reference/state/hosting.md#the-install-boundary)
owns those limits and the W11 examples.

The arena commit is the rollback boundary. Arms declare sources and reuse
`EffectFiring.Values` at preflight and delivery, so delivery evaluates nothing
against an arena a document install may have replaced. Input selections
prepare while refusal can rewind and apply after commit without refusal
(`WorldServer.ValidateStateTransaction`, `WorldServer.CommitStateTransaction`).
Transactional installation is non-refusing by contract; delivered effects can
refuse only as counted failures that undo nothing. A throw past commit remains
an authority defect, not something a second rollback mechanism can conceal.
Proposals are serial, under the tick's authority gate, and do not cross
snapshot publication; parallel execution would need partition-owned journals
and scratch plus an ordered merge.

Types alone cannot stop a caller ignoring `TryFinite`'s bool or treating every
verdict except open as closed. `OutcomeAnalyzer` in `Puck.Analyzers` supplies
the compiler-symbol checks: `OUTCOME001` refuses discarded outcomes,
`OUTCOME002` refuses unchecked extraction and one-case verdict tests,
`OUTCOME003` closes detached evaluation to its allowlist, and `OUTCOME004`
refuses address resolution inside an operand. These are Roslyn build errors,
not a `puck scan` rule. The [analyzer reference](../reference/state/hosting.md#outcomes-analyzer)
owns exemptions and limits: a stored bool never read, a literal arena read,
wrong finite arithmetic or a detached consumer's incorrect fault arm still
needs review. A default frame is refused at runtime, not made unforgeable.

The behavioral witnesses are indexed in
[Rule-fault examples](../reference/state/rules.md#rule-fault-examples).
[The plan](../plans/rule-faults.md) holds the remaining generation, scratch,
source-timing and measurement work only.

### The state review

The owner asked for a critical review of the state system as a whole, beside a
usage audit of where the engine misuses or bypasses it. The work is
[S10](../plans/state-and-language.md#s10--the-state-review). No determinism
defect was found; the weaknesses are structural and economic.

- **One live store.** The arena is the only live gameplay state, read through
  one reader and published from the journal's changed positions. A definition
  holds declarations and seeds and is materialized only at export and edit
  boundaries; a checkpoint snapshots the arena itself. Traits a live read needs
  move into the catalog or columns, so no tick reads the document. Opaque
  machines and native cores keep their own snapshot owners.
- **One write door.** Rules, addons and host cell operations commit through one
  grant-checked transaction that carries a batch and is atomic; post-commit
  deliveries (sound, network) are named as such. Reads use bound handles, and
  every addon read and change delivery is checked against per-recipient
  disclosure. A bound address never caches authorization.
- **Readback answers from the reader's projection.** Query existence, candidate
  counts and row summaries carry only disclosed cells. `Omit` hides counts too;
  authoritative arena sizes and hashes are operator diagnostics. Pattern
  readback uses that same projection for its word and attribute dependencies,
  rather than refusing based on the presence of withheld cells.
- **Appearance chosen by state is a projection,** never a document edit:
  placement responses and deals derive from state, and only colliders whose
  geometry changes are updated.
- **Charge what is used.** No column is reserved or priced on a row that does
  not declare its trait; the unused provenance column goes; a growable row
  charges its growth deterministically; relationships do not reserve a
  Cartesian universe; removing or evicting a cell never materializes or shifts
  more than it must.
- **One deterministic work allowance,** priced by what a world declares and
  does ([costing](#costing)), refusing atomically or resuming across ticks, beside memory and
  journal bounds. The uncertified reference-cycle model is deleted. Measured
  per-world reports establish headroom against 16.7 ms; they do not replace
  deterministic refusal, which untrusted mods require.
- **Everything that steers the future is hashed and checkpointed with it**:
  input-hold history, edge histories, portal occupancy, the machine-binding
  memo, the music clock, and a running addon's continuation (gameplay
  continuation in arena rows, or its guest captured), so a live modded world can
  checkpoint.
- **Rewind restores a complete continuation**: rows, pool generations, owned
  latches and progress, draws and an explicit clock policy. Firing scopes stay
  for speculation, gameplay undo is rebuilt on that bundle, document-edit undo
  stays distinct, and `world.history` stays the editor's time travel.
  Scoped row reservations rewind through the full snapshot relayout mechanism;
  growth after capture is reversible, including a zero-capacity keyed row.
  Refusal leaves no active drive or partial first recording behind.
- **Entities are records in pools**: the one entity noun, with a reference cell
  kind persisting pool, slot and generation, `forEach` over a pool, in-place
  reloads, and native integer-indexed storage instead of string-keyed
  generated rows. Seats are entities too: a rule can evaluate per seat with
  `$seat` bound.
- **Edges are values** (`previous`, `rose`, `fell`) with specified sampling,
  absence and undo semantics, stored only for rows read that way; a facet a
  rule reads is versioned so the rule can skip; rule order an author relies on
  is an explicit phase or dependency.
- **Key names are reclaimed** deterministically once nothing references them,
  with generation-safe identifiers.
- **The row stops being a kitchen sink**: rare traits are optional facets stored
  where declared, and disclosure policy is evaluated per recipient while its
  future-affecting inputs stay authoritative state.

## Costing

The compiled semantic operations share one deterministic allowance. Compose
setup, every check, firing, failed speculative work, rewind, search restart and
cancellation at their actual execution boundaries. Search consumes an aggregate
allowance with deterministic partition shares. Reserve a bounded unit before
execution; if it cannot fit, refuse atomically or resume from a complete
continuation. Unmodeled work and overflow remain explicit refusals.

Reports identify contributors and measured storage; quiet-machine measurements
show runtime headroom separately on the debug world. Reference-throughput
constants and a calibrated reference-cycle admission model are removed. Host
timing never changes simulation admission, ordering, or results.

**A price reads declarations, never members.** Every price reads a row's room
through one door, `RuleCompileContext.RowCapacity`, and the answer is a
function of declarations alone: a slot is one cell, a lattice its topology's
cells, a ring its length, and a keyed or ordered row its declared capacity, or,
with none, the engine's `StateCapacity.MaxCellsPerRow`, bounded for a row keyed
over a token domain by the domain row's own room. Storage stays lazy: an
uncapped row reserves its seeds and the arena grows it on demand, charging the
copy, but a growth never moves a price, so the work sheet is computed once per
compilation and an arena growth is judged against what that sheet leaves.

The options weighed:

- *Price at the current reservation* (what a row holds or the arena has grown
  it to). Rejected: whole-document validation re-prices on every install, and a
  definition carries live members at every export and edit boundary, so a
  hidden row's size would move the sheet, a search job's allowance and minimum
  (text a recipient is told) and whether a public row's growth is admitted, and
  a world's cost would depend on what it has run rather than on what its author
  wrote.
- *Require a capacity on every row a price reads.* Rejected by the "ergonomic
  is optimal" ruling: an author whose row fits at the ceiling, or whose row is
  keyed over a capped domain, would write a number that changes nothing.
- *Fix a capacity from the authored seeds at compile time.* Rejected: the seeds
  a validator sees are the live members, so the capacity would need its own
  recorded copy, and it changes meaning by spelling (seeding one example cell
  would cap a growing row at one, while an empty row grows to the ceiling).
- *Price at the ceiling the row may grow to.* Chosen. Omitting a capacity means
  "up to the engine ceiling", and costs that, so two spellings of one meaning
  cost the same; a keyed-over row takes its domain's bound with nothing
  written. At MMO scale a world's cost is a fixed property of its documents, so
  a shard can be admitted, budgeted and placed before it runs.

What it costs authors: a rule that iterates or folds an uncapped row, or a
search that keys on one, is priced at the ceiling; the refusal names each row
priced there for want of a capacity (`WorldRuleWorkBudget.RowsAtCeiling`) and
says to author its capacity or key it over a capped domain. The rows that need
one are the few a price actually walks: in the debug world, per-body rows a
rule iterates, the card, stone and move tables, and record-like tables a search
keys on. A record-like table of fixed fields states its field count by hand; a
spelling that derives it from the fields is
[open](../plans/open-items.md#language-and-authoring).

Enforcement: `RowCapacity` is the only room a price reads, and no context can be
pointed at a live layout. What it does not cover: growth work and memory
admission (`ReservationWork` over the whole layout, the arena byte and
retained-journal ceilings, and the validator's vector-byte and undo-byte checks)
still read reservations, the remaining instance of the class
([disclosure open items](../plans/disclosure.md#open-items)).

## Games

**Known games only, one exception.** The rules of each are public, so
"correct" needs no design discussion; Ribbon is original because no known game
uses an aperiodic board, and states its complete rules. A game that needs a
construct the transpiler refuses waits for the package that lands it; no
package authors a hand-rolled substitute. Word spy resolves its embeddings
through `puck embed` with the fixture generator, never a network service.

**Three games are scheduled and six deferred.** Go, tetromino, and rulepush
carry the constructs the forcing world cannot reach on its own; the rest are
scheduled unchanged when a capability they force is otherwise unproven.

## Unbiased bounded draws and raw cursors

State ranges, random transfers and shuffles share Maths' Lemire rejection
mapping. Fixed result consumption is withdrawn: a cursor counts actual raw
words for every source, including rejected words, so seeking remains exact
without replaying earlier results. Batches return their raw cursor delta.
A bounded selection has a priced 64-word attempt budget and refuses atomically
if exhausted; it never falls back to a biased result. Scripted range values
compile to accepted raw preimages, so their authored prefix costs one word
per value. Corrected outputs and persisted raw cursor values may change; no
path preserves the previous biased mapping.

---

[Decisions](README.md) · [The programme](../plans/state-and-language.md)
