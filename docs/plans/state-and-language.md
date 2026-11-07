# State and the authoring language

A world is a document. This programme makes the document worth writing: one
storage substrate under it, ceilings that refuse it for a reason an author can
read, a surface language that projects it instead of competing with it, a price
for the work it asks per tick, and known games that prove the vocabulary is
real. The proving ground is `worlds/debug`: its authored programs exercise the
state substrate, admission, and composition together. Missing capabilities from
experimental worlds are ported into debug-world exhibits; those worlds stay
quarantined. The reasoning behind every decision here is in
[the decisions register](../decisions/state-and-language.md); this page holds
the work.

## The forcing world

The forcing artifact is the debug world, running its authored programs through
one live store, one write door, and one deterministic allowance. Exercise a
reusable cabinet module twice in its device row, pool claims and releases in
its games, and identity-owned records across separate authorities. Missing
experimental capabilities belong in the [port inventory](open-items.md), with
their source and destination exhibit identified before implementation.

Run the real debug world through `puck_world_start`, inspect it with `puck sweep`
and captures, and compare replay state for fixed document and input. Highest
quality targets are 60 FPS on the RTX 2060 and RTX 4070, and 45 FPS (every frame
under 22.2 ms) on Steam Deck. The job system and bake fundamentals lead feature
work. Rulepush, Go at 19×19, and Tetromino supply language workloads where a
debug-world exhibit does not yet exercise the vocabulary.

## Packages

Each package names what it **owns**, what it **delivers**, and its **check**.
Every package lands on the integration branch with its hashes re-recorded in
the same change (rule 4); a moved export is re-recorded, not explained. Current substrate contracts live in
[State and rules](../reference/state.md); branch and review procedures live in
[contributing](../development/contributing.md).

### C1 — Ceilings as prices

C1 inventories the charged operations and the remaining accounting and report
gaps. [S10 step 7](#s10--the-state-review) owns the single deterministic semantic
allowance and the deletion of the reference-cycle model. Admission and reports
share one semantic work sheet; separately measured headroom explains physical
cost without choosing authoritative prices from host timing.

The inventory covers rule setup, binding and gate checks, firing effects,
interaction gathering and distance tests, neighbour selection, decision queries
and scores, sorting, arena transforms, transactions, and synchronous mutation
work. Setup is charged once where execution shares it; every evaluated check
is charged, and exclusion reasoning applies only to firing work.

Still owed: the line-of-sight envelope's field-program visits and grid accesses
in the semantic sheet; refused speculative prefixes and their rewind costs;
search restart, replay, cancellation, and output work; and source-attributed
reports that distinguish known, unmodeled, and overflowing work. Reports must
survive budget refusal and expose arena bytes separately from measured runtime
headroom. Server, console, browser, and portal consumers read the same report.
The operation and failed-work inventory feeds S10 step 7 rather than activating
or recapturing a reference-machine schedule.

### S6 — Modules, and the forcing world

**Owns:** the module expander (`module`, `use`, `import`, aliases, re-export,
parameters of kind `Point`, `Angle`, `Asset`, `Module`, `Pool`, `Row`,
`Gate`), `function` and `select`, several documents from one source,
`border`, `asset` with its lock file, and the forcing world itself as an
asset package.

**Expander status:** the source module slice is implemented:
typed arguments, required exports and re-exports, aliased recursive naming,
lexically closed imported modules and constants, hygienic external row/pool/gate
arguments, bounded expansion, formatter projection, and diagnostic/source-map
origins for imported definitions and instance chains. Multi-world emission,
reciprocal `border` and `door` expansion, and explicit asset-lock refresh are
implemented. Each output has an independent source map, while compilation limits
are shared across the source. Borders support derived ground sides and explicit
pitched frames, including an authored minimum ownership deadband. Doors generate
return arches from self-contained placement faces. Asset pins are checked during
ordinary compilation and refreshed only by an explicit successful update.
Reusable module integration in the debug world remains open; quarantined worlds
are not migrated or revived.
A composition is tested as one run: `schedule.instances` arms its sibling
worlds and each is exported ([S8](#s8--tests-are-worlds)). Current
syntax and constraints live in [authoring](../authoring/README.md#composing-neighbouring-worlds).

**Delivers:** the [debug-world workloads](#debug-world-authoring-workloads). `use`
without an alias stamps a body where it stands and with one prefixes every
name the body declares, recursively; `import` brings names into scope and
instantiates nothing; `export alias.name` passes a control upward. A source
with `world` statements emits one document per `world`; `border a.side, b.side`
generates both reciprocal adjacency rows and derives each side's centre and
outward yaw from the two grounds, or takes them as written when the border is
not between two grounds. `door
a.arch, b.arrival` generates both halves of a jump between worlds with no
spatial relation: the destination and reference rows on each side and the
return arch in the far world. A parameter of kind `Module` may state the
exports it requires (`biome: Module exporting ascent`), and a `use` of a
module that lacks one is refused by name at the `use`. `asset "path"` is a
reference whose hash is pinned in a lock file. A diagnostic and a cost name the
debug-world module instance and defining
source span. The open integration work is reusable modules in the debug world
and its standard package, with authored tests, source-attributed refusals, and
independent state for repeated instances. Quarantined JSON shards are never
migrated in place.

**Check:** boot the debug package, exercise two uses of a module, and observe
record transfer across authorities. Port missing experimental capabilities
through the inventory before adding their debug-world exhibits.

### S7 — Records and pools

The [implementation contract](records-and-pools.md) specifies instance lifetimes,
allocator continuation, ownership, and the staged verification required below.

**Owns:** a `pools` member in the state section and its compile in
`Puck.State` (S7a); the `record`, `pool`, `claim`, and `release` constructs
(S7b).

**Delivers:** records declare typed fields, bounds, and defaults. Pools reserve
their slot keys at admission and maintain dense slot-indexed fields, presence bits, and persistent
generation storage. Rules claim, release, and iterate generation-checked lexical
bindings. Pair pools own bounded relationships and cascade release atomically.
World interactions attach logical pool lifetimes to named placements or seats;
geometry remains a host concern. An identity explicitly selects its capacity-one
record pools for transfer, preserving private document state. Paddleball and Arena
exercise the model; rows with timed traits remain rows until records support
those traits without changing their semantics.

**Check:** claim, release, and rewind preserve fixed-slot storage and complete
allocator continuation. Equal hashes require equal dead-slot generations as
well as live values. Budgets charge field width, fixed-slot writes, occupancy scans, and cascades;
iteration multiplies the body cost by its declared bound. Round-trip laws cover
authoring, snapshots, checkpoints, and identity transfer. Warmed speculative pool
operations allocate nothing. The implementation contract lists the remaining
host and real-executable verification gates.

### S8 — Tests are worlds

The `test` construct and `puck test` are built
([testing a world](../authoring/testing-a-world.md)): a `test` block lowers to a
generated test world whose `when` lines are `schedule` rows and whose `expect`
lines are rules writing `verdict` rows, and a module's tests run once per
distinct instantiation. What remains:

- Every reusable module in the debug world ships tests, green on the server
  host and reproducing under `--reproduce`, and the C# laws that pinned a shipped
  game's behaviour are deleted with the tests that replace them.
- The `given`/`when` vocabulary for standing at a place, walking, pressing a
  channel and reading a body's own position waits on the operands a rule can
  read those through: an expectation reads what a rule reads, a state row, never
  a body's own pose or position.
- The browser host cannot run a leg (no command ingress, no acting principal,
  no authoritative server), so `--host browser` is refused by name and
  server-versus-browser determinism is unproven.
- A scheduled command's own mutation verdict is recorded as a run-level echo in
  `test-run.json` rather than attributed to the row that submitted it, because a
  verb registers its correlation id inside its own submission; a rule operand
  over recorded refusals would let a world assert on its own refusal directly.
- A composed test drives its far worlds only through the verbs whose grammar
  carries a world token, so a claim about a far world's own state is written as
  a claim about what crossed into it.
- A `test` written inside a module body reaches only the module's own names.

### Deferred games

Each is scheduled unchanged if the capability it forces turns out to be
unproven elsewhere; the constructs are the ones no shipped world exercises.

| Game | Would prove | Check |
|---|---|---|
| Landlord | ring topology, records and enums, escrowed trade, dice chance, a countdown that holds a player out of turns, `overflow: refuse`, join and leave mid-game, `sort` | one trade and one refused overdraft |
| Wordguess | text cells, pattern algebra over letters, `observe` feedback, a guess ring | two wrong guesses and one right |
| Match-3 | `stabilize` cascades under a `workflow`, run patterns, refill draws | a cascade of two |
| Hexcolony | Hex plus Graph topology, the longest connected route as a component, staged turn, hidden action cards, `derive` for victory points, UCT with chance | one trade and one longest-route change |
| Tower defense | `nav(…)`, `board(pathCost, …)`, wave generators with cursors, a flow-field `stabilize`, families of tower slots | one wave routed around a placed tower |
| Ribbon (original: a connection game on a seeded Penrose patch, sliding along de Bruijn ribbons, enclosure fill) | the tiling generator as a seeded board source, ribbon rows, graph adjacency over an aperiodic patch, `match(…)` along a ribbon, enclosure past 64 cells, UCT with a territory judge | six stones, one full-length slide, one enclosed pocket, one connection |

Landed games with an unproved claim: Hidden ranks' and Hearts' `phaseOf` advance
(a `schedule` step can submit the guarded transform, `world.state.act`, but no
test in either game does), Word spy's optional `nearest` arguments (the world
has no tests), and Arena's leave (a `schedule` step can submit `player.leave`,
but the world has no tests). Reversi landed without its `search` row; its flips
are two rules, and no job has been authored or planned against them.

### S9 — The authoring consensus

The team's consensus on improving the language
([decision](../decisions/state-and-language.md#the-authoring-consensus)), in
waves. Each wave migrates every shipped source with `puck migrate` and deletes
what it replaces.

| Wave | Items |
|---|---|
| 1 | Compound assignment, one expression language and the locals' order, then one bracket rule. Beside it: `sql { }` deleted, a wrapping printer, and saving through the source tree. |
| 2 | Every condition as an expression (HUD, placement responses, element text); no JSON showing through; `function`. |
| 3 | Names for bodies and identities with dot reads; one composition construct; then typed test steps with `eventually`. |
| 4 | Complete state sugar; one rule grammar for cartridges; one authorization declaration; creation axes and rotations. |

**Check:** each item's before and after compiles every shipped source to the
same documents where the meaning is unchanged, `puck test worlds/debug
--reproduce` passes, and the deleted spelling is refused by name.

### S10 — The state review

The work behind [the state review](../decisions/state-and-language.md#the-state-review),
in order; the language work of S9 continues beside it.

| Step | Delivers |
|---|---|
| 0 | Fix the state/jobs ownership contract in the [engine job-system memo](open-items.md#engine-job-system): fixed partitions independent of worker count, declared read epochs, partition-owned scratch, stable commit, slot-allocation, key-minting and draw order, and snapshots only at publication boundaries. Parallel rule evaluation must preserve this chosen mapping. |
| 1 | Addon reads and change deliveries checked against per-recipient disclosure (a correctness repair, first and independent). |
| 2 ✓ | Charge what is used: the provenance column gone, trait columns reserved only where declared, growth charged, no Cartesian relationship universe, `MoveCell` never materializing an absent column, ordered and evicting rows without whole-row shifts. |
| 3 ✓ | One live store and one write door: journal-driven publication, one arena reader, grant-checked atomic batches for runtime writers, live disclosure, arena checkpoints, and responses, deals and contribution tenure as projections. |
| 4 | Hash and checkpoint everything that steers the future, a running addon's continuation included. |
| 5 | Rewind as a complete continuation bundle, extending scoped capture and restore in `WorldScopedReplayRuntime`, the plain-row column image in `ArenaScopeSnapshot`, and checkpointed scoped replays and undo in `WorldPersistence.Checkpoint.cs`. Scoped capture currently refuses timed, vector, and pool rows; the complete bundle must include their continuation too. |
| 6 | Entities as records in pools, with references, seats as entities and the chess pieces migrated. |
| 7 | Edges as values, versioned facets and explicit rule order; key-name reclamation; rare traits as facets; one deterministic semantic allowance with measured reports, the reference-cycle model deleted. |

Step 7 composes setup, checks, and firing separately: sequence adds work, a
conditional charges its predicate plus the larger branch, and a bounded loop
charges setup, each permitted iteration, and teardown. All evaluated checks
remain charged; exclusion discounts only effects whose writers preserve that
exclusion. Failed speculative work pays for the refused prefix, rewind, and
failure branch; a shared flush is charged once only where execution shares it.
Interactions retain their gathering and full candidate scan before selection.
Synchronous rebuild and installation stay charged to the step that runs them.
Overflow and reachable unmodeled work refuse explicitly.

Search uses one aggregate allowance after recurring work and input-change
folding, with deterministic partition shares under the job system. Reserve a
bounded unit before running it and yield when it cannot fit; restart, replay,
cancellation, candidate inspection, frame copies, judges, chance expansion,
playouts, hashing, and output installation all spend that allowance. Refuse a
share that cannot cover restart, replay, and one unit; publish completed output
atomically across yields. Worker count and host timing never change shares or
completion order. Measure physical headroom separately on the debug world.

**Check:** each step's before and after in its commit (arena bytes and work
sheet for the debug world, allocation, `host.frames`), and `puck test
worlds/debug --reproduce` passing with re-recorded hashes where a step changes
them.

### S11 — Ergonomic is optimal

The owner's ruling ([AGENTS.md](../../AGENTS.md#puck-is-pre-release)): cost
follows what a document means, never how it is spelled, and every spelling
lowers to one canonical form. An audit of the debug world's sources listed the
gaps; this package holds the designs the owner chose and what they leave open.

#### Designs chosen

Each lowers to the existing canonical form, so the corpus proves it with a pair
the day it lands. Each lists the options considered; the owner chose the
recommendation in every case, and each entry's **Decided** line records the
choice:

- **G1, G2:** `base with { ... }`, shallow, from a library `let` (G2 (b)).
- **G3, G8:** `function name(p) = expression`, inlined into values and gates.
- **G7:** derived by the engine; the hand-summed totals go.
- **G13:** canonical form first: a zero member that means none is omitted.
- **G14:** constants fold; a template whose body splices into a rule body.
- **G15:** a `let`'s object refuses a quoted enum word (PUCK110).
- **G16:** renumber the Moth's right side by one offset, then a `side` template.
- **G17:** one lowering for a write whatever names its row, key or value.

**G1 — Deriving a record from another.** A value cannot be another with
overrides. Sites: the gallery map re-lists every text-run field to move six
runs (`worlds/debug/shapes.puck` around `:410`, the `map(..., run => { … })`),
the Moth's palette repeats the whole `weathering { }` block per plate slot
(`worlds/debug/avatars/moth.puck:1233` on, five times), the 13 near-identical
emboss runs in `shapes.puck`.

- (a) `base with { member: value }`, an expression: a block after `with`,
  members replacing the base's by name, shallow. Reads as Puck's own block
  spelling and needs no colon rule of its own.
- (b) A spread inside an object literal: `{ ...base, member: value }`.
  Familiar from JavaScript; a second object-literal grammar.
- (c) A builtin: `merge(base, { … })`. No grammar change; reads worst at the
  sites above.

Decided: (a), shallow, with a nested override spelled by nesting
(`weathering: plate.weathering with { seed: 72 }`). It lowers to the object
written out, so the pair is a `with` against the literal.

**G2 — Deriving a keyed row under a new id.** Refining a row by id works only
for the same id, so `range-walker` copies the walker kit
(`worlds/debug/range.puck:122`) rather than deriving from it.

- (a) A row header naming its base: `kit "range-walker" from "walker" { … }`,
  resolved at compile time against the rows the source, its libraries, its
  basis and its imports declare, emitting the whole row.
- (b) No new construct: move the walker kit into a library `let` and derive
  with G1's `with` (`kits [ walkerKit with { name: "range-walker" … } ]`).
- (c) A template the walker's library exports, called with overrides.

Landed, G1 and G2 (b): `base with { ... }` (`WithExpressionNode`), a postfix
on its base, read only when a block follows the keyword. The standard kits
library holds `walkerMotion` and `stillMotion`; the walker, suit pilot, range
walker and Moth kits, the piece and held kits, `cardSlab` and the lift derive
from them, and the Moth's `groundFlight` hold derives from `groundHold`. Her
`flight` hold stays written out (with a shared `flightSpend`): it is
`fallHold` without the `envelope`, and `with` cannot remove a member. The
corpus holds `with-record` and `with-library`. The Moth's per-slot
`weathering`, the gallery's text runs and the emboss runs are still to convert.

Decided: (b) now, since it needs only G1 and the existing library
import, then (a) only if a site needs to derive from a row that is a
document's own and cannot move into a library. (a) needs the compile to read
a basis's or import's rows, which today it reads only for enums.

**G3 — Value functions.** A `let` cannot hold a lambda (PUCK042) and a template
emits rows or members, not values, so a repeated value inside an array (text
runs, paints, palette entries) cannot be named once. The Moth's blink frame is
built twice, once in `worlds/debug/avatars/moth.puck` (the `blink` frame of the
`moth` prototype) and again in `moth-pilot.puck` (around `:603`), the two
`map(..., row => { id … position … rotation … })` bodies differing only in the
head's anchor and scale. The register already
decides `function`: compile-time, inlined, non-recursive
([decision](../decisions/state-and-language.md#the-authoring-consensus)).

- (a) `function name(p, q = d) = expression`, called in call position, the
  body inlined with the arguments bound as constants.
- (b) Admit a lambda in a `let` (`let run = (text, at) => { … }`): no new
  keyword, but a value that is sometimes a function, and two ways to name one.

Decided: (a). The pair is a call against the expression
written out with the arguments substituted.

**G7 — Totals summed by hand across documents.** `bodies.capacity`
(`worlds/debug/debug.puck:180`, summing counts `shapes.puck` restates from each
game) and `placements.policy.derivedFaceScreens` (`worlds/debug/debug.puck:191`,
`garden.puck:83`, `night.puck:78`) are summed by hand from what imports
declare, and drift when an import changes.

- (a) Derive them: the engine counts the composed world's derived face screens
  and its populations, and the authored member goes (a ledger, which the
  owner's rulings delete).
- (b) Contributions: each import writes its share (`bodies { capacity += 54 }`)
  and composition sums the shares, as a basis merge does for keyed rows.
- (c) A compile-time fold over imports (`sum(imports, d => d.cardBodies)`).

Decided: (a) for both, since each is a function of the composed world;
(b) only for a budget the author must choose rather than count. With (a) the
pair is an authored total against none.

Landed for the face screens: `placements.policy.derivedFaceScreens` is gone,
and `WorldDefinition.DerivedFaceScreens` is the composed world's claiming
faces (`WorldFaceCatalog.ClaimingFaceCount`), so every claiming face is seated
and none starves; the debug world boots with its twelve at screens 18 to 29.
A live definition that lights more faces than boot reserved is refused by
`WorldDocument.Compose`, naming the restart, as before.

Open for the capacity: `bodies.capacity` is not a count of the composed
world. It is the inhabitants plus seat slots a document reserves (the Moth
basis authors 8 with no inhabitants; chess 41 = those 8, 32 pieces and a
cursor; the debug world 559 = 1 + its 558 inhabitants, which a headless boot's
`world.population` confirms), and every game addresses its pieces by body
index computed from the same hand sum (`goTop`, `reversiTop`, `cardsTop`,
chess's "bodies 8 to 39") through grants such as `body:{goTop - 1 - i}`.
Deriving the total therefore moves the drift into those grants, where it
fails silently. The fix at the cause addresses a piece by its placement (a
grant or a rule naming `goStones`' body `i`, resolved at load) so no source
computes an index; then the capacity is `reserve + inhabitants`, `reserve`
being the seat slots a document chooses (`pond.puck` already writes
`reserve + creatures`).

**G8 — Naming a rule expression.** A `let` cannot name a state expression, so
the idle-door guard is restated in nine Klondike rules and eight FreeCell rules
(`worlds/debug/klondike.puck:246` on), and Klondike adds a `moveReady` latch
cell (`klondike.puck:472`) partly to keep the work budget's price low.

- (a) G3's `function` over state reads: `function idle() = solitaire.table == 1
  and …`, inlined into each gate, so the rule program and its price are the
  hand-written guard's.
- (b) A derived state row (`derived idle = …`): a run-time cell, priced and
  stored as one, which changes the program.
- (c) A `let` holding an unevaluated expression, inlined where read: a second
  meaning for `let`.

Landed, G3 and G8: `function name(p, q = d) = expression` is a `let` holding a
`FunctionExpressionNode`, so libraries hand functions over like any `let`.
A call in a document value evaluates the body with the arguments bound as
locals; a call in a rule operand is spliced before the constant fold, its
parameters replaced by the argument syntax, so a gate built from a function
is reclassified as the guard written out, and an inlined `and` or `or` joins
its enclosing junction in order (`all [all [a, b], c]` is `all [a, b, c]`,
which also flattens one parenthesized group in chess). A function calling
itself is refused. The Moth's blink frame is `mothBlinkFrame(at, s)` in
`moth-head.puck`; Klondike and FreeCell name their guards
`klondikeIdle`/`klondikeRequest` and `freecellIdle`/`freecellRequest`. Each
`moveReady` latch stays: rules run in sequence, so the latch and the inlined
guard behave alike, but the latch evaluates the ten-cell guard once a tick
for the move rules, each of which then reads three cells. The corpus holds
`function-gate` and `function-value`.

Decided: (a), one construct for G3 and G8. The pair is a guard named by
a function against the guard written out. Whether a latch cell and the guard it
factors price the same (the audit's S9) is a cost question for the work
budget, not a spelling pair: the latch is extra state.

**G13 — A member written only when it applies.** A template cannot leave a
member out on a condition. `worlds/standard/bounds.puck`'s `boundsBox` takes an
optional `bevel`: a `for` over `filter([bevel], b => b > 0)` that writes
`chamfer: b` is PUCK046 (a `for` emits rows, never a field), and
`chamfer: null` lowers to a JSON `null` member. So every box writes
`chamfer: 0`, which is a second document beside the box with none: the
`chamfer-default` pair fails. The renderer, the stamp emitter and the
validator read both as no chamfer (`shape.Chamfer ?? 0f`,
`CreationCanonicalizer.cs:2420`, `:2511`, `CreationStampEmitter.cs:148`), so the
SDF program and its bake agree, but `CreationCanonicalizer.Normalize` keeps
`Chamfer` as written, so the creation's canonical hash differs too.

- (a) Canonical form first, no syntax: lowering leaves out a member equal to
  "none" for that member (a zero `chamfer` or `rounding`), and `Normalize` does
  the same, so `chamfer: 0` is the omitted chamfer everywhere. The template
  keeps writing `chamfer: bevel`.
- (b) A conditional member: `chamfer: bevel if bevel > 0`, or an `if` block
  around members in a row body (`if bevel > 0 { chamfer: bevel }`), evaluated at
  compile time.
- (c) `null` means absent: a member whose compile-time value is `null` is not
  written. Simple, but a document member that is a meaningful `null` (an
  explicit null name, a basis-merge removal) then needs another spelling.

Decided: (a) now, since it removes the need at this site and fixes the
pair at its cause; (b) as an `if` around members if a site needs a member whose
zero is not "none". Landed with the typed canonical walk above; the
`chamfer-default` pair holds.

**G14 — A template inside a rule, and a branch folded at compile time.** A
template's body is document statements, so it cannot carry a gate, a local or
an effect, and a template called inside a rule body is read as an effect of
that name (`climb(limit: 3)` inside a rule is PUCK030, "unknown member
'limit' at /rules/1/effects/0"). A compile-time choice in a gate is not folded:
`when (strict == 1) ? (level < 3) : (level <= 3)` with `let strict = 1` lowers to
a `compareValue` over an expression evaluating both arms, where `when level < 3`
lowers to `compareState`, so the rule program and its price change with the
spelling (the `ternary-fold` pair fails). Together they block one shared
seat-input template across Go, Alchemy, Reversi, the card table, the granaries
and chess (`worlds/debug/go.puck` around `:1153` and its siblings).

- Folding is a defect, not a design: an expression whose operands are all
  compile-time constants folds before lowering, a ternary on a constant
  condition becomes its chosen arm, and `x == c` folds wherever `c` and `x` are
  constants. Then the pair holds.
- For the template half: (a) a rule-body template, `template seatPress(button)
  { local … ; when … ; seat.edge = … }`, admitted only where a rule body
  stands, its statements spliced into the rule as a rule-body `for` already
  is; (b) G3's `function` for the gates and values, with the effects written
  per game; (c) a template emitting whole rules (possible today) with the
  per-game differences as parameters.

Decided: fold constants now; then (a), since the shared part of the
seat-input rules is effects and locals as well as gates. The fold landed (see
above); the `ternary-fold` and `constant-fold` pairs hold. The rule template
landed as `rule template name(p, q = d) { … }`, its body parsed as rule-body
statements and spliced by `DocumentLowering.ExpandBody` where a rule body calls
it, under its bound arguments, as a rule-body `for` splices; a document template
called in a rule body, or a rule template outside one, is refused by name. A
binding a key or member is read through that holds a name (`seat` holding
`"goSeat"`) names that row, in a read and as a write target alike. The standard's
`seat.puck` holds the seat buttons (`seatButtons`, `seatSteps`), `seatHeld` and
`seatPress`; Go, Alchemy, Reversi, the card table and the granaries call
`seatPress` in a `for`, chess its `seatHeld`, and every document is unchanged.
The hand-made edge cells stay. The corpus holds `rule-template`.

**G15 — Enum words in a `let` and in a row.** A row or a template's body takes
a closed-vocabulary word bare and refuses it quoted (`bond: "Surface"` in a
kit's hold is PUCK110), while inside a `let`'s object both `bond: Surface` and
`bond: "Surface"` compile to the same document, since the value's place in the
model is not known until a row reads it (the `enum-word-in-let` pair holds:
`worlds/standard/kits.puck`'s `groundHold` quotes its words). One spelling is the
fix: a `let`'s object refuses the quoted word too, once lowering can type the
let's value where a row reads it, or the row admits the quoted word and the
printer writes it bare. Decided: refuse it in the `let` (PUCK110 names
the bare spelling), migrating the standard's lets with `puck migrate`. Landed:
a `let` holding a record lowers where it lands at that position, as the
record written there would (`DocumentScope.TryLowerBindingAt`, once per
position), so its members take a row's refusals (a quoted closed word is
PUCK110, attributed to the `let`'s own source) and its bare words read as the
position's words: before, a `let`'s bare `hold: None` read the chess `None`
enum member as 0. Only `worlds/standard/kits.puck`'s holds quoted their words;
they are bare now. The corpus holds `enum-word-in-let` (now bare) and
`let-word-at-position`.

**G16 — The Moth's mirrored halves.** The right half of `moth.puck` (from about
`:2508`) restates the left. Of 114 left/right shape pairs the right id is the
left's plus 100 (43 pairs), 68 (39), 20 (8), 7 (7), 8 (5), 16 (4), 1 (3), 6 (3),
48 and 69, so a `side` template (as `moth-pilot.puck`'s `pilotArm(side)`) needs a
table as long as the shapes. The other residuals are systematic: positions,
pivots and profile points negate x, a rotation reflects to `(x, -y, -z, w)` up
to sign (now one canonical sign), swing amplitudes, axes and phases flip.

- (a) Renumber: give the right half `id = left id + 1000` (and groups
  likewise), updating every id the frames, chains and effectors name, then
  write each limb once as a template over `side`. Nothing new in the language.
- (b) A mirror construct over a set of shape rows,
  `mirror x { rename: ("left", "right"), ids: +1000 } { shape … }`, emitting the
  reflected copies at compile time; it lowers to both halves written out, so
  the corpus pair is the construct against the halves.

Decided: (a). The irregular offsets are history, not design; once the
ids are regular the existing template expresses the side, and the language
needs (b) only if several creatures (the pond's spider and hound) want it.

Measured, not yet done: the compiled Moth pairs 125 left and right shapes at
eleven id offsets (100: 43, 68: 35, 16: 15, 20: 8, 7: 7, 1: 5, 8: 5, 6: 3,
2: 2, 48 and 69). Beyond the mirror (x negated in positions, a rotation
reflected to `(x, -y, -z, w)` up to sign, a sweep's curve points and a
swing's axis and phase flipped, a side's own group and material), what
differs: 8 axially symmetric cylinders and 4 prisms keep the left's rotation
(the same geometry, so a template may mirror them); the right back pod's 40
parts (ids 1200 to 1240) carry rotations that are not the mirrors of the left
pod's (1100 to 1140), one systematic difference; `sideLock`'s and
`blinkCrease`'s positions are not mirrored; `kneeGuard`'s profile differs.
Writing the right side through a `side` template makes the right half the
left's mirror wherever these differ, so which half is right is a look to
judge in a windowed capture before the template lands.

**G17 — What the hidden-information games hit.** Authoring the four games
(`worlds/games/auction.puck`, `shedding.puck`, `deduction.puck`,
`skirmish.puck`), each wall checked against the compiled document or a running
World. A rule-body template and `schedule … in` with a parameter are G14's.

- A row name computed per seat (`$"sheddingHand{s}"`) reads in a gate or a
  local, but an assignment, a `remove` or a sugar key's `zone(…)` naming it is
  PUCK001/PUCK002, so every per-seat write is a call form (`setState`,
  `addState`, `removeStateCell` with `state: $"…"`): two spellings of one
  write, one admitted only for literal names.
- In a call form, `key: expr(card)` with `card` a local lowers as a read of a
  row named `card` (the `expression:` argument resolves the same local), and a
  `for` variable bare in `key:` lowers as its literal name; `key: $"{c}"`
  works. A compile-time string on an assignment's right side
  (`row[k] = $"seat{s}"`, or a `let` holding one) lowers as a copy from a row
  of that name (`fromState`), where a literal string lowers as text;
  `setState(…, text: $"seat{s}")` writes the text. Compiling the imported
  fragment alone with `--validate` passes the bad copy; only the composing
  world refuses it (`StateRowUnknown`).
- `export read` refuses a backquoted dashed name (`` `card-back` ``, PUCK001)
  and takes a quoted one.

Landed: an interpolated row name is a target wherever a literal is (an
assignment, a compound assignment, `remove`, a bracketed key holding one, as
in `sealed[zone($"hand{s}", first)]`), read through the atom-aware operand
parser; a local inside a call form's computed key (`key: expr(card)`) keeps
its local spelling when the key is read again after the rule's locals leave
scope; an interpolated string, or a let or loop variable holding a string, on
a write's right side is text; and `export` takes a backquoted name, refuses a
quoted one (PUCK110) and prints the backquote (the printer and the decompiler
share `PuckPrinter.PrintExportName`). A bare word in a call form's `key:`
stays the literal key it spells, as `row.word` does (the one bracket rule):
a computed key is `$"{c}"` or `expr(c)`. The games' per-seat writes, removes
and texts are sugar now, every document unchanged; a keyed copy of a text
cell stays a `setState(…, fromState, fromKey)` call, since a keyed read on a
right side lowers to an expression program, which cannot carry text. The
corpus holds `computed-write`.

Decided: one lowering for a write whatever names its row, key or value:
an interpolated name is a write target wherever a literal is, a local or a
`for` variable resolves in every argument of a call form as it does in the
sugar, a compile-time string on a right side is text, and the export list takes
the backquoted name; each with a corpus pair against the spelling that works
today.

Engine behaviour the games met, which a game spells around rather than a
language gap: a rule's write never mints a key in a keyed row (only a pile
takes new members), so every chat slot, audience cell and vote is declared up
front, and a literal-key read of a cell no seed declares is refused at
validation (`StateCellUndeclared`); a search job restarts whenever a row it
reads changes and `observe` restamps every remembered cell, so a job scoring
over a `knowledge` row finishes only when the observe runs on change.

## Sequencing

What is open, in order:

| Step | In parallel | Why here |
|---|---|---|
| 1 | The job-system memo and S10 step 0, then the single allowance in step 7 with C1's accounting inventory | Parallel storage and evaluation need one chosen ownership and ordering contract. |
| 2 | S6's reusable modules in the debug package, after the fundamentals | Proves the language on the live proving ground; missing experimental features are ported there. |
| 3 | S7's two open steps: [remote home adoption](records-and-pools.md#remote-home-adoption) and records carrying timed row traits | Remote adoption closes the debug-world record round trip across separate authorities. |
| 4 | The deferred games, each when the capability it forces is otherwise unproven | See the table above. |

## Debug-world authoring workloads

The cabinet module used twice, cross-authority traveller records, pooled game
entities, and authored motion are the composition workloads. Reusable designs
belong in `worlds/standard/`; debug owns their places and exhibits. The
experimental quilt, island, and One World stay quarantined. Inventory their
missing capabilities and port each to a named debug-world place rather than
reviving their drafted sources. Snake's variable-age history and a played
Word spy round remain language scenarios below, independent of those worlds.

## Open items

What the programme still owes. A line is deleted when its work lands, never
ticked; the [open items](open-items.md#next) rank the work packets drawn from it.

- C1: the charged-operation, failed-work, and report inventory above feeds
  [S10 step 7](#s10--the-state-review).
- Rule-fault propagation: the fault register on `ArenaScratch` is replaced by
  the in-band `Outcome`, the one interpreter and the transactional firing of
  [the rule-faults plan](rule-faults.md), in its nine steps with their
  witnesses and checks.

- S7: [remote home adoption](records-and-pools.md#remote-home-adoption) across
  separate authorities, and the timed row traits still absent from records that
  [S7](#s7--records-and-pools) names before converting their consumers.
- S6: reusable modules and source-attributed diagnostics in the debug package;
  missing experimental capabilities move through the port inventory.
- S8: server-versus-browser replay remains unsupported; Hidden ranks and Hearts
  need an authored guarded phase advance, Arena needs a leave scenario, and the
  runner's pose step still needs authored yaw. These are the remaining scenario
  gaps from the rebuild, whose WP11b–WP14 packages are closed.

- Namespace the remaining module declarations. A module used under an alias
  namespaces its rows, rules, placements, prototypes and look sources as
  `alias$name`, but spawn points, kits, look names, cameras, navigation domains
  and property names stay flat, so two uses of one module declaring the same
  spawn point declare it twice. A JSON import with `as` has no dotted
  `alias.name` form in `.puck`; a hand-written JSON composition can make an
  aliased pool's rows collide with a host pool named after the alias, which only
  the `.puck` door refuses (PUCK117); and the export check does not look inside a
  deal's variant map.
- Deferred as a block: Landlord, Wordguess, Match-3, Hexcolony, tower defense,
  Ribbon. When their missing capabilities are ported to debug-world exhibits:
  Snake grows past six segments
  (`history(row, age)` takes an expression age, and an age outside the ring
  reads the empty value, per `HistoryAgeLawTests`, but
  [`snake.puck`](../../experimental/src/Puck.World/Assets/worlds/games/snake.puck)
  still reads four fixed ages, clears its tail through a branch per length and
  caps `snakeLength` at six), and Wordspy's recorded baseline plays a round (its
  state export stays at stage 1 with an empty clue log, so the baseline never
  exercises the clue-word copy the
  [baselines README](../../experimental/tests/Puck.World.Games.Tests/ShippedWorldStateBaselines/README.md)
  describes).
- S10 the state review ([S10](#s10--the-state-review)):
  - State/jobs ownership and ordering from the memo, before pools and parallel evaluation.
  - Everything that steers the future hashed and checkpointed, running addons
    included.
  - Rewind as a complete continuation. Chess allows undo (the owner's ruling): a
    player takes back a move through gameplay rewind over the complete
    continuation, the first consumer of this step.
  - Entities as records in pools; seats as entities.
  - Edges as values, versioned facets, explicit order, key reclamation, facets,
    one work allowance.
- S9 the authoring consensus ([S9](#s9--the-authoring-consensus)):
  - Wave 2: every condition as an expression (HUD visibility, placement
    responses, element text); the noise fields that remain (`mode: Level`
    written in rule bodies where it is the default, and one hash spelling for
    `hash: "auto"`, `"sha256-64/…"` and a bare digest); `function`.
  - Wave 3: names for bodies and identities, one composition construct, typed
    test steps with `eventually`.
  - Wave 4: complete state sugar, one rule grammar for cartridges, one
    authorization declaration, creation axes and rotations.
- A decision cannot sweep `$bodies` (refused by name); its `forEach` names a
  keyed row. The browser rule host serves no bodies, so it refuses a `$bodies`
  rule at admission as `ILiveBodies`.

---

[Plans](README.md) · [Decisions](../decisions/state-and-language.md) ·
[State and rules](../reference/state.md)
