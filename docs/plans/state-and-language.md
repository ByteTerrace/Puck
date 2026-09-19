# State and the authoring language

A world is a document. This programme makes the document worth writing: one
storage substrate under it, ceilings that refuse it for a reason an author can
read, a surface language that projects it instead of competing with it, a price
for the work it asks per tick, and known games that prove the vocabulary is
real. Everything is proved against one artifact, the forcing world: a quilt of
four corner worlds around an island with an arcade district, authored in
`.puck` from nothing, running on the rebuilt substrate, priced by the schedule
that refuses it. The reasoning behind every decision here is in
[the decisions register](../decisions/state-and-language.md); this page holds
the work.

## Implementation status

Everything below has landed on `main`.

- **The rebuild.** The sweep that deletes the old substrate, the relocation
  into `Puck.State.Topology` and `Puck.State.Generators`, and the documentation
  true-up are merged. `tests/Puck.World.Tests` fails nothing on a built tree.
  [The landing record](state-rebuild.md#where-the-sweep-stands) holds what it
  measured.
- **Games.** Seven of sixteen are landed: Reversi, Stratego,
  Hearts, Snake, Codenames, Pong, Arena.
- **Embeddings** is done. The reference schedule's evidence is landed:
  `src/Puck.State/ReferenceSchedule.json` pins eight targets
  across two ISA families, prices the fourteen unary expression operations,
  and names every other coefficient — the binary, function, program and fold
  kernels, the ten effect arms, and the whole memory profile — as unmodeled,
  with a law that refuses a registered operation carrying neither. Pricing
  those remains C1's, as reviewed loop formulas per implementation family and
  a published-configuration memory profile.
- **S1** is landed: `WorldCompiler.Compile` is the one door, and
  `ProjectionLawTests` holds over every shipped source and a generated corpus
  of every registered construct. `puck lint` and the language server both
  publish `WorldSourceDiagnostics.Diagnose`, which compiles through that door
  and then runs the lint, the engine's validation of the composed world and the
  reference lint, so an editor refuses what `puck lint` refuses, by the same
  code, text and line.
- **S2** is landed: the syntax tree carries trivia, `puck fmt`,
  the language server and `puck decompile` are parse-then-print through
  `PuckPrinter`, the text formatter is gone, and formatting every shipped
  source is a fixed point that moves no document.
- **S4** is landed: `src/Puck.World.Transpiler/Vocabulary`
  describes every world construct once, keyed by where it is written and its
  keyword, with a member identified by its construct and its name; the
  parser's flag admission, the printer's sugar guards, completion, hover, and
  `puck vocabulary`, which generates `docs/reference/world-vocabulary.md`,
  read it. A described member no lowering consumes is refused rather than
  dropped, a law compiles one probe per construct and holds each row's
  document keys to what appears, and a refusal names its construct's own
  line. The retired `transform label = call(…)` spelling is refused by
  name, an effect's refusal resolves to the effect's own line rather than its
  rule's, and both root dispatches switch over the arm the table names. The
  kind-conditional admission is a row facet every emitter site reads, and
  `draw`, `deal` and `shuffle` are described as rows rather than recorded as
  another vocabulary's. `PuckLinter.References` resolves a row name at every
  site `WorldNameRegistry` registers, read by the site's role, so it holds no
  field list of its own.
- **S5** is landed: a migration is a named rewrite over the
  syntax tree (`src/Puck.Transpiler/Rewriting`), and `puck migrate` applies
  one to a directory, stages every write, compiles each source before and
  after, and refuses a run that changes a member or a comment the migration
  did not declare. The registry is empty until a reshape needs it.
- **S8's runner** is landed: a tick-scheduled `schedule` section, a
  `verdict` row trait, and `puck test`. The five ways a test could pass when it
  should fail are closed — the section is inert unless the boot arms it and says
  only test steps, reaching the authored export tick is part of the verdict,
  only a rule's firing writes a verdict and the firing tick says so, every row
  declares the outcome it expects, and a scheduled run refuses to pretend it is
  resumable. The browser host is not covered.
- **S8's `test` construct for a world** is landed:
  `test "name" { given when expect }` at a world's root, described
  once in the construct table, lowering through `WorldCompiler.Compile` to one
  generated test world per block, and `puck test <file.puck | directory>`
  running them. A world compiled without `puck test` is byte-identical to the
  same source with its tests deleted, and the CLI suite runs every test a
  shipped world authors. `with module(arguments)` is refused by
  name until `use` exists, and the `given`/`when` vocabulary the drafted pages
  below use — standing at a place, walking, pressing a channel, reading a body's
  own position — waits on the operands a rule can read those through.
- **The forcing world** is drafted below, and two canaries hold what today's
  engine already does for it. Everything else below has not started.

## The forcing world

Four corner documents from one parameterized module, and hovering over the
point where the four meet, a sky island of districts — a Dalaran: each district
a module, an arcade among them, and portals that are federation links to other
worlds, never worlds inside a world. A cabinet module is instantiated twice
into two hosted devices, a pool a rule fills and empties, and a record follows
a player across authorities. It reproduces no existing JSON, so the
language is pulled toward reading well rather than toward the shapes it
replaces. Its pages are [drafted below](#the-forcing-world-drafted); each page
is the check for the package that makes it compile, so the world grows with
the language instead of arriving after it. It is authored under the product
tree from its first line ([Runtime and delivery](runtime-and-delivery.md)
roots it at `content/puck`).

The place, as a player meets it:

- **The Concourse** is a round plaza at the island's centre, directly above
  the point where the four corners meet, ringed by portal arches. Every
  district opens onto it, and no district entrance is more than about 60 m
  from its centre.
- **The districts** sit on spokes, one module each: the Arcade (the cabinet
  module, twice), the Arena, the Studio, the Cistern (the island's reservoir,
  which is the dive pool), the Garden, and the Granaries. The kart track is
  the **rim road**, a lap of about 565 m at a 90 m radius with nothing below
  its outer edge; the jump course is the **spires** that rise over the plaza.
- **The four corners are four biomes**, one `corner` module given a biome
  module as a parameter: a glacier to the north-west, a mesa desert to the
  north-east, a mangrove wetland to the south-west, an old-growth forest to
  the south-east. Each is its own document, so each owns its one physical
  field: cold, heat, water, and none. Every biome exports an `ascent`, its own
  way back up: an ice wall climbed on holds, a thermal updraft, a waterspout,
  a canopy climb.
- **There are two ways off the island.** The rim is a real drop: four borders
  lie flat beneath the island, one per quadrant and facing straight down, so a
  body that falls through the north-west quadrant is handed to the glacier's
  authority in mid-air with its velocity and its record intact. The arches are
  doors: each leads to a disconnected world with no spatial relation to the
  island, and the first of those are board games, one `parlor` module given a
  game module as a parameter, one shared parlor per game.
- **Movement is one kit**, used by the island and all four corners so the feel
  cannot differ across an authority change: the double jump, jetpack, and
  coyote-time scheme, tuned tight in the manner of Hollow Knight and
  Silksong. The island and the corners author 60 Hz so a 0.1 s window is six
  ticks; a parlor keeps 30 Hz.

**Check:** the world boots and runs in `Puck.World`; a body walks a circuit of
the four corners under real separate authorities and its record arrives intact
at each hop; a body that steps off the rim over each quadrant arrives in that
quadrant's biome still falling, with the velocity, stamina, and record it left
with, and a body hovering on the border plane does not change authority every
tick; a body returns to the island by each biome's ascent; winning a game in a
parlor writes a badge onto the visitor's record, and the badge is there on the
island afterward; the cabinet module used twice yields two independent sets of
rows and two devices; the projection law holds over every source in it; every
module in it ships tests, the movement kit's among them, and `puck test` is
green over them on both hosts.

Three games finish the proof the world cannot reach on its own. **Baba Is
You** forces a match that answers *where*, a push along a ray, a write across a
set, and undo across turns. **Go at 19×19** forces the cell-set carriers past
256 cells. **Tetris** is the one proof of a staged `workflow` with a drawn bag
and a saturating score. Six more games are deferred until a capability they
force is otherwise unproven; the table at the end says what each would prove.

## Packages

Each package names what it **owns**, what it **delivers**, and its **check**.
Every package lands on the integration branch with its hashes re-recorded in
the same change (rule 4); a moved export is re-recorded, not explained. The
[hand-off discipline](state-rebuild.md#hand-off-discipline) says who
implements, who reviews, and which model.

### The landing

[State system rebuild](state-rebuild.md) is the record of the landing: every
package it lists is landed on `main`, each with its own check, and the old
substrate is deleted. Every package below branches from `main`.

### C1 — Ceilings as prices

**Owns:** the constants in `RuleCapacity`, `StateCapacity`, `PatternCapacity`,
`SearchCapacity`, `GeneratorCapacity`, `ArenaCapacity`, `RuleGroupCapacity`,
`WorldRuleCapacity`, `TopologyCompilation`; their refusal texts; the reference
schedule and its activation from [costing](abstract-machine-costing.md).

**Delivers:** two real limits, the per-tick work budget and per-document memory
bounds; every surviving count names the bytes it sizes or the budget share it
multiplies, and a count that protects nothing is deleted (`MaxJumpHops`,
`MaxSortKeys` as a copy of `MaxRows`, `PatternMemo.MaxEntries`,
`MaxTotalCells` replaced by a byte ceiling). No `stackalloc` sized by a
document: the board transforms take scratch from `BoardScratch`, the
expression evaluator from a host buffer; the gate nesting depth is its own
constant; the pattern table is capped as one byte figure; `MaxSymbols` is
bounded so its alphabet runs fit the letter mask. The raises:

| Limit | Now | Target |
|---|---|---|
| `MaxBindingsPerRule` (becomes `MaxLocalsPerRule`) | 16 | 64 |
| `MaxExpressionTokens`, `MaxSubprograms` | 64, 16 | 256, 64 |
| `MaxEffectsPerRule`, `MaxTransactionEffects` | 64 | 256 |
| `MaxPredicateTokens` | 256 | 1,024 tokens, 64 nesting |
| `MaxRows`; `MaxEnumMembers` / `MaxEnums` / `MaxFamilies` | 256; 256 / 64 / 64 | 1,024; 1,024 / 256 / 256 |
| `MaxTextValueLength`; `MaxTopologies` | 256; 16 | 1,024; 64 |
| `MaxVectorSpaces`, `MaxNearestResults`, `MaxMixTerms` | 16, 64, 8 | 64, 256, 32 |
| Pattern `MaxRepeat` / `MaxRows` / `MaxStates` | 64 / 64 / 256 | 128 / 256 / 1,024 under the byte cap |
| Lane widths, `MaxDrawnMasks`, `MaxContexts` | 64, 64, 32 | 256, 256, 64 |
| Generator sources, emission bound, extended table | 64, 64, 1,024 | 256, 256, 4,096 |
| Search depth, jobs, shapes, promotions, chance outcomes | 32, 8, 8, 8, 64 | 64, 16, 16, 16, 256 |
| `TranspositionEntries`, `TreeNodes` | 1,024, 2,048 | 8,192, 8,192 |
| Group members, passes | 64, 64 | 256, 256, their product priced |
| `RuleExpressions.MaxArguments`, `MaxTraceEvaluations` | 8, 32 | 16, 256 |
| `ExpressionSpelling.MaxLength` | 4,096 | 16,384 |

`TopologyCompilation.MaxCells` stays 4,096 until a document asks. Every
ceiling is per document, counted after modules expand: a module used twice
counts its rows twice, a pool counts its rows once and its capacity in the
budget, and the refusal names the module instance that crossed it. Every
ceiling refuses at compile time naming the limit and the alternative. The
arena's journal and touched-position buffers take a byte ceiling, and a firing
that would exceed it is one named refusal that rewinds. `ArenaVectorTransforms`'
`Nearest` allocates its candidate buffer on every call; it moves to scratch the
arena owns with the other scratch changes here, under the same allocation law. A
compiled operand over a literal key resolves its slot through the row's key
dictionary on every read, which is most of a judged search candidate's cost;
it holds the slot against the row's own reindex instead. The per-tick budget is
one price: the reference schedule (the benchmark kernels captured, coefficients
derived, formulas and evidence in one manifest with `CostModel.EvidenceDigest`
set, every reachable operation priced or explicitly unmodeled) replaces the
heuristic work-unit count, and `MaxWorkUnitsPerTick` becomes that schedule's
per-step allowance in reference cycles, calibrated with headroom above every
shipped world; validator, console, search plans, `BrowserExports`, and the
portal worker read one report with source-path attribution. The method is
[costing §3 to §7](abstract-machine-costing.md#3-price-schedule-and-evidence).

**Status:** the scratch changes are landed. `ArenaScratch` is the arena's
working storage, leased and returned in order: the board transforms, the sorts,
`arrange`, a jump chain's visited cells, and all four vector transforms (each of
which allocated on every call, not `Nearest` alone) take their buffers from it,
and the expression evaluator leases its value stack at the program's own length
through `IStateReader.Scratch`, which replaces `BoardScratch`. The generator
engine keeps its stack buffers: they are sized by `MaxEntriesPerSet`, a constant,
and the engine is a pure surface with no arena behind it. The compiled-operand
slot did not survive its profile. Cell reads are about 43% of a judged
candidate, but the cost was the arena copying its row's twenty-field layout two
or three times a read, because `ArenaLayout`'s indexer returned it by value; the
indexer returns a reference now, and a row's key-to-slot map is an array over
the row's key ordinals rather than a dictionary. A slot held on the operand was
rejected: compiled rules are shared by every arena they run over. Chinese
Checkers measures 4.15 ms a tick against 5.1.

Three of the four counts that protected nothing are gone. `MaxJumpHops` bounded
nothing the node ceiling did not: a chain over even one direction enumerates
8,192 candidates a token at thirteen hops against the 4,096 a job judges in a
tick, and that check is the one that refuses, overflowing counts included.
`MaxSortKeys` was a copy of `MaxRows` under a check that already requires
distinct rows. `PatternMemo.MaxEntries` dropped the memo at 256 walks, which a
board read from every cell in every direction passes at once; it is one
mebibyte, 16,384 walks at the 64 bytes each occupies. `sortZone` and
`sortKeyed` lease their key buffers instead of allocating them per firing.
`MaxTotalCells` waits on the document's memory bound, which does not exist yet:
the arena's layout knows every column's width, and the bound belongs there
rather than on boards alone. The raises, that memory bound and the journal's,
and the reference schedule have not started.

**Check:** the state, rules, search, and World schema suites green with a law
at each raised edge for the two ceilings that bind today (locals, expression
tokens) and for each scratch change; every ceiling refusing at compile time;
the allocation laws still report nothing after warm-up; `puck schema --check`;
observed modeled service never exceeding admitted static service on
exhaustively enumerated small worlds, with equal reports native and WASM; every
document and skill that states a value corrected.

### C2 — Variable-width cell sets

**Owns:** `BoardMask`, `BoardQueries`, `CompiledTopology`'s shift and image
tables, `CompiledPattern`'s term and letter masks, `CellSetExpression` and its
lowering, the search's `Legal` and reach paths, the browser's board-mask export.

**Delivers:** the internal carriers widen to the topology's word count; a cell
set wider than a word lives in a board row, one cell per cell, combined with
`boardCombine` and the cell-set algebra at a width taken from its topology,
with an inline path for sets of at most 256 cells so today's documents pay
nothing. `$board:mask`, `writeSet` from an Int cell, and the search's `Legal`
row remain the convenience for boards of at most 64 cells and say so; the
reach board is the general form a wider plan must declare. Generator drawn
masks stay 256 wide.

**Check:** the cell-set algebra laws run at 64, 256, 361, and 594 cells; a
19×19 board's enclosure and a 33×18 board's per-noun sets compute through the
same operators; existing hashes do not move.

### C3 — A match answers where

**Owns:** the `$match` operand family, its spelling and decompiler.

**Delivers:** beside `count` and `distance`, a match answers `at` (the cell
where the nth match along a ray or across a board begins) and `length`; a rule
reads the cells at offsets from it with ordinary computed keys. No captures.
Knowledge becomes keyed by token rather than by board cell, so a revealed piece
that moves keeps its rank.

**Check:** a law derives "which noun IS which property" from three-tile runs
along both axes using one pattern over symbol classes and two cell reads, and
agrees with a brute-force scan; the pattern laws for intersection, complement,
and De Morgan are untouched.

### C4 — A push along a ray

**Owns:** one transform in `src/Puck.State.Rules/Transforms/`, its compiler
arm, refusals, spelling, decompiler.

**Delivers:** `pushRay` moves a run of tokens one cell along a direction from
a live origin exactly when the run matches a pattern and ends on a cell the
pattern admits, all of it or none, inside the caller's scope, over tokens
holding their cell so several may share one; under `for each`, every mover's
line resolves in one firing.

**Check:** laws for a free push, a blocked push that moves nothing, a line
pushed off a full board refused, two movers into one cell in a deterministic
order, zero allocation after warm-up.

### C5 — A write across a set

**Owns:** `writeSet`'s compiler arm and kernel.

**Delivers:** `writeSet` takes a cell set (a board row) or a token row filtered
by value as its target, so every rock becomes a flag in one effect.

**Check:** a law rewrites every token of one value in one firing and rewinds
as one.

### C6 — Undo across turns

**Owns:** the arena's journal retention, the rule-group `undo` declaration, a
`rewindTurn` effect, the hash, the checkpoint codec.

**Delivers:** a rule group declares `undo` over a set of rows with a depth;
each committed turn keeps its journal entries for those rows as a closed
segment in a ring of that depth, and no scope stays open between ticks.
`rewindTurn` restores the newest segment and drops it, refused by name when
the ring is empty. A group declaring undo holds no irreversible arm and no
host-owned write; a turn that wrote outside the set marks its segment
unrewindable. Who may undo is the gate of the rule that fires `rewindTurn`.
Depth is bounded in bytes by the journal ceiling, refused at load when its
worst case cannot fit. The ring folds into the hash and the checkpoint and is
dropped by a relayout.

**Check:** eight turns then eight rewinds reach the hash recorded before the
first; a checkpoint taken mid-ring restores and rewinds to the same hashes; a
relayout empties the ring; each refusal has a law; a world declaring no undo
allocates and hashes nothing for it.

### S1 — The projection law and the one door

**Owns:** the transpiler test harnesses; one compile entry point in
`Puck.World.Transpiler`.

**Delivers:** every harness compiles through the entry point `puck compile`
uses. A law runs over every shipped source and a generated set covering every
construct: compile, print, compile again, and the documents are equal; format,
compile, and the document is unchanged. A construct the generator does not
cover fails the law by name.

**Check:** the law green over the shipped sources, and failing when a printer
arm is removed, shown once.

### S2 — A tree that prints itself

**Owns:** `src/Puck.Transpiler/Parsing`, `Ast`, `Formatting`.

**Delivers:** the syntax tree keeps comments, blank lines, and the author's
line breaks; `puck fmt` is parse then print, and the text formatter is
deleted. The parser's identifier, string, and interpolated-name reads become
one routine.

**Check:** formatting every shipped source twice is a fixed point and leaves
every generated document byte for byte the same; the language server's
formatting request uses the same printer.

### S3 — Operands as grammar

**Owns:** the expression front end in `Puck.State`, the world transpiler's
operand lowering and decompiling, the document's operand shape.

**Delivers:** function forms replace the colon channels: `channel(1, strafe)`,
`count(zone)`, a match with `count`, `distance`, `at`, `length`,
`history(row, age)` with an expression age, a cell read, a `local` read by
its bare name. The document's operand becomes a structured node, so nothing
in a document is a string with its own grammar. Kinds are inferred from the
cell and the unit, so `Kind` leaves the surface; rates and durations are
spelled in seconds and the engine places each trait on its tick coordinate;
one timing effect, an absolute deadline. Every shipped source is rewritten
once through S5; the old spellings are refused with a diagnostic naming the
new form.

**Check:** the projection law; no shipped source contains a colon channel, an
authored `Kind`, a tick-counted duration, or a second timing effect; every
generated document's meaning is unchanged by the rewrite.

### S4 — One description per construct

**Owns:** a construct table in `Puck.World.Transpiler`; the parser's statement
dispatch, the decompiler's sugar, the language server's completion and hover,
the manual's vocabulary tables.

**Delivers:** each vocabulary construct is described once: keyword, members
with kinds and defaults, the document member it lowers to, what its printer
requires before it may sugar a row, and the authored span of every member
through the emitter's `SourceMap`, so any refusal prints the `.puck` line.
Parser, decompiler guards, completion, hover, and the manual read that
description. Every construct this programme adds (`module`, `use`, `record`,
`pool`, `claim`, `release`, `function`, `select`, `world`, `border`, `test`)
is on the table from its first commit; older constructs move over afterward,
and a construct is off the hand-wired path when its old code is deleted. The
`sql { }` dialect is one row, the `sql` keyword delegating to the SQL parser.

**Check:** adding a member to a described construct is one edit and the law,
completion, and manual follow, shown by a law that adds one in a test
description; a refusal from any described construct names its line.

### S5 — Surface migrations as a verb

**Owns:** `puck migrate` in `src/Puck.Cli`.

**Delivers:** a migration is a named rewrite over the syntax tree, applied to
every source under a directory and printed by S2, so comments, layout, and
the compile-time layer survive; it lands with the reshape it serves and is
deleted after it has run.

**Check:** the rewritten sources compile to documents equal to the ones
before, apart from the members the reshape names.

### S6 — Modules, and the forcing world

**Owns:** the module expander (`module`, `use`, `import`, aliases, re-export,
parameters of kind `Point`, `Angle`, `Asset`, `Module`, `Pool`, `Row`,
`Gate`), `function` and `select`, several documents from one source,
`border`, `asset` with its lock file, and the forcing world itself under the
product tree.

**Delivers:** what [the pages](#the-forcing-world-drafted) require. `use`
without an alias stamps a body where it stands and with one prefixes every
name the body declares, recursively; `import` brings names into scope and
instantiates nothing; `export alias.name` passes a control upward. A source
with `world` statements emits one document per `world`; `border a.side, b.side`
generates both reciprocal adjacency rows and derives each side's centre and
outward yaw from the two grounds, or takes them as written when the border is
not between two grounds (the four that lie flat beneath the island). `door
a.arch, b.arrival` generates both halves of a jump between worlds with no
spatial relation: the destination and reference rows on each side and the
return arch in the far world. A parameter of kind `Module` may state the
exports it requires (`biome: Module exporting ascent`), and a `use` of a
module that lacks one is refused by name at the `use`. `asset "path"` is a
reference whose hash is pinned in a lock file. A diagnostic and a cost read quilt, island, arcade,
cabinet, line, and a ceiling crossed inside an expanded module names the
instance. After it: the shipped island, its seven districts, and the five
shards move from hand-written JSON onto the same modules, the canaries are
pointed at the result and re-recorded, and the JSON sources are deleted.

**Check:** the forcing world's check above.

### S7 — Records and pools

**Owns:** a `pools` member in the state section and its compile in
`Puck.State` (S7a); the `record`, `pool`, `claim`, and `release` constructs
(S7b).

**Delivers:** a record names its fields with kinds, bounds, and defaults; a
pool names a record and a capacity and compiles to the arena's keyed rows, one
per field over one key domain, a key minted at claim and compacted at release.
A rule over a pool iterates its live instances by the name on its rule line
and reads a field by name; an absent instance reads absent; claim refuses at
capacity; who may claim or release is the gate of the rule that fires it. A
record instanced in `identity { }` rides the owned identity across
authorities, so nothing is marked as travelling. A pool of pairs is a pool, so
interactions and decisions can leave host evaluation. Hand-numbered families
are rewritten onto pools, and a `properties` tag names a pool instance rather
than a body index.

**Check:** a pool of sixteen: claim, release, and reclaim leave the rows
compact and the hash equal to a fresh world with the same live set; a rule
over the pool costs its capacity in the budget; an interaction over a pair pool
reaches the host evaluator's decision; the projection law covers every
construct.

### S8 — Tests are worlds

**Owns:** a `test` construct; the `schedule` section and the `verdict` row
trait in `Puck.World.Schema`; `puck test` in `src/Puck.Cli`; the deletion
of the C# game laws, the baseline runner's raw cell writes, and the browser
parity fixtures.

**Delivers:** a `test` block in a module or world lowers to a generated test
world: the subject `use`d with the test's arguments in an empty host, `given`
as initial cells through the host's ingress, `when` as tick-scheduled command
rows, `expect` as rules whose gate is the expectation and whose effect writes
a verdict row folding the values the gate saw. `puck test` boots each test
world headless to its last scheduled tick and reads the verdicts from exported
state; the same world's hash through the server and the browser host is the
parity and cross-host determinism check. A module's tests run once wherever it
is used, and a `use` of a module whose tests fail is a compile refusal. A
module's parameters are its test seam.

A module's tests run wherever it is used, its author's or not, so a green
result has to be evidence and a test world can do nothing but simulate. A
schedule is inert unless the boot arms it; any other boot of a document
carrying one submits nothing and says so. A scheduled command is one that
routes to the simulation (a mutation, a guarded transform, an intent or a
channel press, a join, a leave), landing on its authored tick; a command that
touches the process, the clock, or the file system is refused at validation,
and no row acts as the console: a test acts as a seat, with what it needs
authored in the document's grants. Reaching the authored export tick is part
of the verdict, and every declared row is reconciled against what ran. Only
a rule's firing writes a verdict row: the row refuses value-over-time traits
and every other door's write, carries the tick of the firing that wrote it,
and reads as never evaluated without one; a verdict that failed once stays
failed and names the first failing tick. Each row states the outcome it
expects, submitted unless the test is about a refusal, and any other outcome
fails the run; a handler that throws is a host failure with its own exit
code. A checkpoint or a save inside an armed run is refused. A test over several `world` statements
boots the set as the federation canaries do and reads its verdict in the
destination world. The export-hash baseline stays as the determinism check
only.

**Check:** every module in the forcing world ships tests, green on both hosts;
the C# laws that pinned a shipped game's behavior are deleted with the tests
that replace them; a failing verdict names the gate and the values it saw.

The runner half is landed and is what the construct lowers to: the `schedule`
document section (rows of tick/principal/command/expect plus the derived export
tick), the `verdict` state-row trait, `world.schedule`/`world.verdicts`, and
`puck test` over `tests/Puck.World.Verdicts`.

What a green run now means. The section submits nothing unless the boot armed it
with `--schedule-dir`, and a row may open only with a verb from a closed step
vocabulary tied to the live command registry by a law, acting as a seat under the
document's own grants. Reaching the authored export tick is part of the verdict,
and every declared row is reconciled against the manifest at its own position.
Only a rule's own effect writes a verdict row: the effect door stamps the
firing's tick, a status with no stamp reads never-evaluated whatever the cell
says, a failing verdict is sticky so the report names the first failing tick, and
every other door refuses the write by one text the boot loader shares. Each row
declares the outcome it expects and any other recorded outcome fails the world by
name, while an outcome the host rather than the world answered — a handler that
threw, a principal with no ingress, a refusal that never reached a handler — is a
usage refusal instead. The two legs' exports and manifests are both compared byte
for byte, and the manifest records only facts a rerun reproduces. A scheduled run
refuses `world.save`/`world.load`/`world.reload`/`world.undo` by name: a schedule
carries no cursor, so a restored world would submit every row again from tick 1.

Two gaps stay open. The browser host cannot run a leg at all — it has no
command ingress, no acting principal and no authoritative server — so
`--host browser` is refused by name and cross-host determinism is unproven. A
scheduled command's own mutation verdict is recorded as a run-level echo in
`schedule.json` rather than attributed to the row that submitted it, because a
verb registers its correlation id inside its own submission; so an echo refusal
no row expected is printed rather than failing the run, and a rule operand over
recorded refusals would let a world assert on its own refusal directly instead of
on the refusal's consequence.

The construct is what a world's own behaviour is written as. `test "name"
{ given { } when { } expect { } }` stands at a world's root, is described once in
the construct table, and lowers through `WorldCompiler.Compile` to one generated
test world per block — the enclosing document unchanged, plus the `given` cells,
plus `when` as `schedule` rows on a tick grid, plus one verdict row and one rule
per `expect` line, the rule gated on the export tick alone so it fires once.
`puck test <file.puck | directory>` compiles the source and runs those worlds,
and `--keep <dir>` leaves each of them on disk as an ordinary world document.
What it cannot yet say: `with module(arguments)` is refused by name until `use`
exists, an expectation reads what a rule reads (a state row, never a body's own
pose or position), and a generated verdict row carries no `Generated` mark
because no document member does — the verdict trait is what says the row is
machinery.

A verdict row is an Int row, so what a gate read of a `Fixed` or a `Bool` row
is written by the same firing to a `witness` row of that kind: a row trait
naming its verdict, refused at every door the verdict is refused at, frozen
when the verdict settles, and printed beside the verdict's own cells. A rule
compares numbers, so no gate reads a `Text` or a `Vector` row and neither has a
witness.

One limit is owed. The gate grammar reads `true` and `false` as row names, in a
rule's `when` as much as in an `expect` line, so a `Bool` row is compared to `0`
or `1`; S3's operand grammar is where the two literals belong.

### The costing correction

**Owns:** `RuleCost`, `RuleWorkBudget`, `WorldRuleWorkBudget`, the search
allowance, `WorldCostReport`.

**Delivers:** setup, check, and firing costs separated, with every check
always charged and the exclusion trie applied to firing work only;
interactions priced as carrier gathering, up to `L × R` distance tests,
selection, then at most `L × min(K, R)` evaluations; decisions, sorts,
transactions, and mutations priced at the paths they execute; a rejecting
result for an unregistered operation and explicit `Unmodeled` and `Overflow`
propagation; search spending a bounded, resumable allowance per job with
chance and playouts obeying it; validation against independently counted
execution traces on exhaustively enumerated small worlds. The specification is
[costing §4 to §8](abstract-machine-costing.md#4-composition-rules-and-mathematical-obligations).

**Check:** `observed modeled service <= admitted static service` on the
enumerated worlds; the adversarial cases in costing §8; equal reports native
and WASM.

**Status:** the accounting is corrected and search is bounded. A bound is a
`RuleWork`: a number of heuristic work units, an operation nothing prices, or
an overflow, and the last two survive every composition and fit no ceiling. A
line is setup, check, and firing, with the exclusion trie over firing work
alone. An interaction's sweep (carrier gathering, every left against every
right, nearest-neighbour selection) is setup, and its neighbour limit bounds
only the evaluations; a decision is charged its cell lookups, query walk, heap,
per-candidate perception and gate, retained scores, and sorts; a reordering
transform is charged the insertion sort it runs; a transaction is charged the
costlier of its two paths. `WorkSheetTraceLawTests` enumerates small worlds and
holds the evaluator's own trace under the admitted sheet.

Search spends a `SearchWork` allowance: what the sheet and one fold of every
row leave, divided equally among the jobs, unspent and unshared. Every unit of
the walk is priced, the costliest is reserved before any runs, replay and
restart come out of the same allowance, and a share that cannot cover a
restart, a full replay and one unit is refused by that sum. A chance ply is a
ply on the walk's explicit stack, at the root or inside it, so a step suspends
inside one and a checkpoint carries what it has folded; a tree step is a unit
like any other. `ArenaSearchAllowanceLawTests` holds every step under its
allowance against a judge counted from outside the search.

Owed: a line-of-sight test is a flat weight. The test is a budgeted sphere
trace over the world's compiled solids: at most the evaluator's march budget of
exact samples, each one a run of the compiled field program, plus one banded
lookup per bound step along the segment. Bounding it needs that program's
length, its step scale and the band's step floor, which a server compiles and
the document's cost context does not carry, and a sound product of them is a
price in reference cycles rather than a work-unit weight; both arrive with C1.
No document can spell an unpriced operation or an overflowing count, so
admission's refusal of one is held on admission's own function,
`WorldRuleWorkBudget.Refuse`, and at the sheet, not through a world. The
reference schedule, reference-cycle admission, and the shared report are C1's.

### Embeddings (done)

**Owns:** `src/Puck.Embeddings`, `EmbeddingLock`, `EmbedCommand`,
`VectorStateLawTests`.

**Delivers:** the open defects, each with a law: server fixes gain their tests
(a copy into an absent key, transform refusals by name, an Edit-only principal
applying each transform); weak tests made to fail when their subject breaks;
`puck embed` exits 2 without writing when lowering has errors and collects
texts from both dialects; `Puck.World.Transpiler` stops referencing
`Puck.Embeddings` (lock spaces keyed by `StateSpace` identity); `PruneEntries`
and `PruneSpaces` are called; `EmbedCommandTests` compares a fixture lock byte
for byte. The measurement reported with the change: flagship tick timing
unregressed, `nearest` over 256 × 256 under 100 µs in Release.

**Check:** `puck embed --check` offline with fixture locks regenerating
byte-identically; both authored samples boot and trace their rules; neither
`packages.lock.json` lists a provider package.

### G4 — Tetris

**Owns:** `games/tetris.puck`, baselines. Constructs: `workflow`,
`countdownState`, `cycle`, row patterns, 7-bag draw, `overflow: saturate`.
Spawn, fall, lock, clear as a `workflow`; gravity as a `cycle`; lock delay as
a countdown; a full-row pattern that clears; a 7-bag generator with a drawn
mask; score saturating. **Check:** the sequence locks three pieces and clears
one line; the corpus inventory counts every claimed construct.

### G1 — Go

**Owns:** `games/go.puck`, its host fixture, baselines. Constructs:
`clearEnclosed`, `writeSet`, `$board:component`, `$board:boundary`,
`$board:enclosedAt`, `boardCombine`, `stabilize`, `$history` for ko, UCT
search. A 19×19 board (after C2); capture by liberty fixpoint; ko refusal
through the history ring; territory count; a `search` row that plays one CPU
move. **Check:** an atari, a capture, and a ko refusal, each visible in the
export.

### G16 — Baba Is You

**Owns:** `games/baba.puck`, its host fixture, baselines. The published rule
set: a row or column of three text tiles is an active rule while it stands;
`YOU` moves on a press; `PUSH` moves with it, whole line or none; `STOP`
blocks; `SINK` destroys both; `DEFEAT` destroys a `YOU`; `YOU` on `WIN` ends
the level; text is always `PUSH`; undo steps back a turn. Conditions, `NOT`,
`AND`, `HAS` are out. Six nouns, the six properties, noun-to-noun rewriting,
three levels of at most 16×16 (a full-size 33×18 level needs C2), undo at
least 32 deep. The property table is derived each turn under `stabilize`
(rules read from state); a match answers where (C3); objects are tokens
holding their cell so several share one; a push chain is C4; a rewrite across
the board is C5; undo is C6. **Check:** the sequence breaks `WALL IS STOP` by
pushing a word, forms `ROCK IS FLAG` and wins on a rewritten rock, sinks an
object, loses every `YOU` and undoes out of it, and undoes eight turns to a
state whose hash equals the one recorded eight turns earlier.

### Deferred games

Each is scheduled unchanged if the capability it forces turns out to be
unproven elsewhere; the constructs are the ones no shipped world exercises.

| Game | Would prove | Check |
|---|---|---|
| Monopoly | ring topology, records and enums, escrowed trade, dice chance, jail countdown, `overflow: refuse`, join and leave mid-game, `sortKeyed` | one trade and one refused overdraft |
| Wordle | text cells, pattern algebra over letters, `observe` feedback, a guess ring | two wrong guesses and one right |
| Match-3 | `stabilize` cascades under a `workflow`, run patterns, refill draws | a cascade of two |
| Catan | Hex plus Graph topology, longest road as a component, staged turn, hidden development cards, `derive` for victory points, UCT with chance | one trade and one longest-road change |
| Tower defense | `$nav`, `$board:pathCost`, wave generators with cursors, a flow-field `stabilize`, families of tower slots | one wave routed around a placed tower |
| Ribbon (original: a connection game on a seeded Penrose patch, sliding along de Bruijn ribbons, enclosure fill) | the tiling generator as a seeded board source, ribbon rows, graph adjacency over an aperiodic patch, `$match` along a ribbon, enclosure past 64 cells, UCT with a territory judge | six stones, one full-length slide, one enclosed pocket, one connection |

Landed games with an unproved claim: Stratego's and Hearts' `phaseOf` advance
(the baseline runner cannot submit a guarded transform; S8 replaces the
runner), Codenames' optional `nearest` arguments, Arena's leave (no leave
step). Reversi landed without its `search` row and gains it when WP11a's judge
cost folds exclusions.

## Sequencing

The landing carries the rebuilt state system and the means to build and test a
world against it: the rebuild, S8's runner, and S8's `test` construct for a
world. A test inside a module, and the rule that a `use` of a module whose tests
fail is a compile refusal, follow with S6's expander, which is what gives a
module its `use`.

| Step | In parallel | Why here |
|---|---|---|
| 1 | [The landing](state-rebuild.md): the sweep, WP8, WP13, WP14; S4's remainder; S8's runner and its `test` construct for a world | The critical path. Everything below edits the new substrate and is verified with `puck test`. |
| 2 | S3, S7a, S7b, S6's expander; C1 and the costing correction | Each language package adds spellings and follows S4's table; S7a is state work; C1 is what every later package allocates under, and the correction needs the rebuilt operator table. |
| 3 | S6's remainder, S8's module tests, and the shipped island, districts, and shards onto the modules | Needs the language proven by step 2. |

After the landing a package branches from `main` and lands as its own squash.
`puck test` runs on the server host; running the same test world through the
browser host is not scheduled.

### Deferred with the games that force them

C2 to C6 exist for three acceptance games: C2's variable-width cell sets for Go
at 19×19, and C3 to C6 for Baba Is You; Tetris needs none of them and proves the
same substrate the landed games already exercise. The five capabilities and the
three games (G4, G1, G16) are scheduled together, after step 3, with each game
verified by its own `test` blocks. Their packages above stay as written.

## Verification summary

```bash
dotnet build Puck.slnx -c Release
```

```bash
dotnet test tests/Puck.State.Rebuild.Corpus -c Release
```

```bash
dotnet test tests/Puck.State.Tests -c Release
```

```bash
dotnet test tests/Puck.State.Rules.Tests -c Release
```

```bash
dotnet test tests/Puck.State.Search.Tests -c Release
```

```bash
dotnet test tests/Puck.World.Transpiler.Tests -c Release
```

```bash
dotnet test tests/Puck.World.Tests -c Release --filter ShippedWorldStateBaselineTests
```

```bash
puck architecture --check
```

```bash
puck schema --check
```

```bash
puck fmt src/Puck.World/Assets/worlds --check
```

```bash
puck test src/Puck.World/Assets/worlds
```

```bash
puck embed --check
```

```bash
puck landing --against origin/main --base <the commit the branch was authored from>
```

## The forcing world, drafted

These pages were written before any of their machinery existed, so the
spellings could be judged by reading them; nothing here compiles today.
Statements are keyword-led blocks, as today: the keyword at the start of a
line is what the construct table, a diagnostic, and a reader key on, and
composability comes from parameter kinds rather than from making every
declaration a value.

| Need | Spelling | Lowers to | Carried by |
|---|---|---|---|
| A reusable part | `module name(parameters) { }` | nothing until used | replaces `template` |
| Using one | `use name(arguments)` stamps the body where it stands; `use name as alias(arguments)` also prefixes every name the body declares | the module's statements once per `use` | the import-alias rewrite plus template substitution, made recursive |
| Another file's modules | `import "path"` brings names into scope | nothing | the import resolver |
| A name inside a part | the bare name; from outside, `alias.name` | the flat prefixed name | the same rewrite |
| What a part shows | `export read\|action\|binding name`; `export alias.name` passes one upward | the `exports` member | exists; re-export is new |
| One definition, many live instances | `record Name { field: Kind ... }`, `pool name of Record capacity(n)` | the `pools` member, then keyed rows | S7 |
| A rule over instances | `rule name for each x in pool { }` with `x.field` | one rule iterating live keys | `forEach` |
| Making and removing one | `claim pool as x { x.field = value }`, `release x` | two effects | S7 |
| A named calculation | `function name(parameters) = expression` | inlined at each call | the lambda evaluation `let` has |
| A choice by value | `select value { 0: a, 1: b, else: c }` | the conditional chain | expression lowering |
| A rule's working value | `local name : Kind = expression`, read bare | `locals` | the `bind` rename |
| An input or reduction | `channel(1, strafe)`, `count(zone)`, `history(row, age)` | a structured operand | S3 |
| Several worlds from one source | `world name = module(arguments)` in an ordinary source | one document per `world` | S6 |
| State that follows a player | a record instanced in `identity { }` | rows on the owned identity | identity-carried facts |
| A seam between neighbouring worlds | `border a.side, b.side { }` | both reciprocal `adjacencies` rows | S6 |
| A jump between unrelated worlds | `door a.arch, b.arrival` | the `destinations` and `references` rows on both sides, and the return arch | S6 |
| What a module parameter must offer | `biome: Module exporting ascent` | nothing; a `use` that lacks the export is refused | S6 |
| Bulk data | `asset "path"` | a reference, hash pinned in a lock file | the embedding lock's pattern |
| A test | `test "name" [with module(arguments)] { given { } when { } expect { } }` | a generated test world | S8 |

### Page 1 — A cabinet, and the arcade that holds two of them

```puck
// buildings/cabinet.puck
module cabinet(at: Point, facing: Angle, engine: Engine, cartridge: Asset) {
  machine console {
    engine: engine
    content: cartridge
  }

  screen display {
    origin: at + [0, 1.25m, 0]
    facing: facing
    size: [0.6m, 0.54m]
    source: console.video
    engage { radius: 2.2m  channel: jump  kit: arcadePad }
  }

  export read display
  export binding display
}
```

```puck
// districts/arcade.puck
module arcade(origin: Point) {
  import "buildings/cabinet.puck"

  use cabinet as left(
    at: origin + [-1.5m, 0, -2.5m], facing: 0deg,
    engine: gamingBrick(model: cgb), cartridge: asset "cartridges/mirror.cgb")
  use cabinet as right(
    at: origin + [1.5m, 0, -2.5m], facing: 0deg,
    engine: advancedGamingBrick(boot: fast), cartridge: asset "cartridges/pip.agb")

  record Token { position: Point  value: Int 1..5 = 1  ttl: Int 0..600 = 600 }
  pool tokens of Token capacity(32)

  slot spawnClock : Int = 0

  rule spawn {
    when spawnClock == 0 and count(tokens) < 32
    claim tokens as token {
      token.position = origin + scatter(radius: 6m)
      token.value = draw(tokenValue)
    }
    spawnClock = 90
  }

  rule expire for each token in tokens {
    when token.ttl == 0
    release token
  }

  test "a token expires" with arcade(origin: [0, 0, 0]) {
    given { claim tokens as token { token.ttl = 1 } }
    when  { ticks 2 }
    expect { count(tokens) == 0 }
  }

  export left.display, right.display
  export read tokens
}
```

`screen` loses its hand-picked index; `console.video` is a checked reference
to a sibling; two `use` lines are two devices; `export left.display` is the
re-export; `tokens` is a pool a rule fills and empties; the test is a world.

### Page 2 — One corner, four biomes, and the quilt

```puck
// quilt/corner.puck
module corner(name: Name, center: Point, biome: Module exporting ascent) {
  documentId: $"quilt-{name}"
  basis: "island.puck"
  simulation { rate: 60hz }

  ground terrain { center: center  size: [132m, 132m] }
  spawn arrival { at: center }

  use traveller()                       // the one movement kit, page 3
  use biome as land(origin: center)     // props, the field, the tune, the way up
  export land.ascent
}
```

```puck
// quilt/biomes/mesa.puck
module mesa(origin: Point) {
  field heat { lattice: grid(origin, cells: [32, 32], size: 4m)  ambient: 0.35 }

  // A column of rising air at the canyon mouth: a body inside it is lifted
  // until it clears the island's rim.
  gravityArea thermal {
    at: origin + [40m, 0, -40m]  radius: 9m  height: 140m
    up: 34
  }

  export read thermal as ascent

  test "the thermal clears the rim" with mesa(origin: [0, 0, 0]) {
    given { stand at thermal.at }
    when  { ticks 600 }
    expect { self.position.y >= 120m }
  }
}
```

```puck
// quilt.puck
record Traveller {
  tokens: Int 0..999 = 0
  badges: Int 0..64 = 0
}

identity {
  self : Traveller
}

import "island.puck"
import "quilt/corner.puck"
import "quilt/biomes/glacier.puck"
import "quilt/biomes/mesa.puck"
import "quilt/biomes/mangrove.puck"
import "quilt/biomes/forest.puck"

world island = island()

// The corners meet at the origin; the island hovers 120 m above that point.
for (name, center, biome) in [
    (nw, [-66m, 0, -66m], glacier), (ne, [66m, 0, -66m], mesa),
    (sw, [-66m, 0,  66m], mangrove), (se, [66m, 0,  66m], forest)
] {
  world name = corner(name: name, center: center, biome: biome)
}

border nw.east, ne.west   { width: 132m  height: 40m }
border nw.south, sw.north { width: 132m  height: 40m }
// ... the remaining two ground seams, one line each

// The rim drop: four borders lying flat 40 m beneath the deck, each facing
// straight down, one per quadrant. Neither side is a ground, so the centre
// and the facing are written.
for (name, center) in [
    (nw, [-45m, 80m, -45m]), (ne, [45m, 80m, -45m]),
    (sw, [-45m, 80m,  45m]), (se, [45m, 80m,  45m])
] {
  border island.under(name), name.sky {
    center: center  pitch: -90deg  width: 90m  height: 90m
    hysteresis: 2m
  }
}

test "tokens travel" {
  given { nw.self.tokens = 3 }
  when  { walk from nw.arrival across nw.east }
  expect { ne.self.tokens == 3 }
}

test "a fall off the north-east rim lands in the mesa, still falling" {
  given { stand at island.rim(45deg)  self.stamina = 7 }
  when  { walk off the rim  ticks 90 }
  expect {
    authority(self) == ne
    self.velocity.y < 0
    ne.self.stamina == 7
  }
}

test "hovering on the border plane does not thrash" {
  given { stand at island.rim(45deg) }
  when  { walk off the rim  hold rise at height 80m for ticks 120 }
  expect { authorityChanges(self) <= 1 }
}
```

```puck
// quilt/parlor.puck: one room, any board game
module parlor(game: Module exporting seats, outcome) {
  simulation { rate: 30hz }

  ground floor { size: [12m, 12m] }
  spawn arrival { at: [0, 0, 4m] }

  use traveller()
  use game as table(at: [0, 0.9m, 0])

  // The parlor is its own authority; the badge is a write onto an identity
  // it does not own, so it goes through the identity write-back door.
  rule award for each seat in table.seats {
    when table.outcome == seat.side
    mode: edge
    seat.occupant.badges |= table.badge
  }
}
```

```puck
// in quilt.puck: the arch ring
import "quilt/parlor.puck"
import "games/reversi.puck"
import "games/chess.puck"

world reversiParlor = parlor(game: reversi)
world chessParlor   = parlor(game: chess)

door island.arch(1), reversiParlor.arrival
door island.arch(2), chessParlor.arrival
```

```puck
// inside the arcade, a rule that pays the player who walks over a token
rule collect for each token in tokens {
  when touching(token.position)
  self.tokens += token.value
  release token
}
```

A source with `world` statements emits several documents; `border` writes
both halves of a seam and `door` both halves of a jump; `Traveller` is
declared once and every world shares it; what follows a player is what
`identity { }` declares, because the owned identity is what the hand-off
carries. A body's position, heading, and velocity are the engine's to hand
over and are not authored. `corner` refuses a biome that exports no `ascent`
and `parlor` a game that exports no `seats` or `outcome`, at the `use` that
passed it.

### Page 3 — The traveller's kit, and its feel as tests

```puck
// kits/traveller.puck
module traveller() {
  kit traveller {
    collider: capsule(endpoint: [0, 1m, 0], radius: 0.35m)

    run   { speed: 6.5  acceleration: instant }
    fall  { gravity: 46  rise: 28  terminal: 40 }

    slot airJumps : Int 0..1 = 1
    slot airDashes : Int 0..1 = 1
    timer dashCooldown

    action jump {
      when pressed(jump, within: 0.12s) and recently(grounded, 0.1s)
      vertical = 11.5
    }
    action doubleJump {
      when pressed(jump) and not recently(grounded, 0.1s) and airJumps > 0
      vertical = 10
      airJumps -= 1
    }
    action jumpCut {
      when released(jump) and rising
      vertical *= 0.4
    }
    action dash {
      when pressed(dash, within: 0.12s) and dashCooldown.elapsed
           and (grounded or airDashes > 0)
      impulse { direction: facing  distance: 5m  duration: 0.2s  gravity: off }
      airDashes -= 1 unless grounded
      dashCooldown = 0.5s
    }
    action restore {
      when grounded or holding(wall)
      airJumps = 1
      airDashes = 1
    }
    action jetpack {
      when held(rise) and fuel > 0
      thrust { up: 18 }
      fuel -= 1 per second
    }
  }

  test "coyote time is six ticks, not seven" {
    given { stand at ledge.edge }
    when  { walk off the ledge  press jump at tick 6 }
    expect { self.velocity.y > 0 }
  }
  test "a seventh-tick press is a double jump" {
    given { stand at ledge.edge }
    when  { walk off the ledge  press jump at tick 7 }
    expect { self.velocity.y > 0 and airJumps == 0 }
  }
  test "a dash is five metres and flat" {
    given { stand at origin  facing east }
    when  { press dash  ticks 12 }
    expect { self.position.x == 5m and self.position.y == 0 }
  }
  test "the air dash is spent until touchdown" {
    when  { jump  press dash  ticks 40  press dash }
    expect { airDashes == 0 }
  }
  test "a released jump is cut to forty percent" {
    when  { press jump  ticks 3  release jump }
    expect { self.velocity.y == 0.4 * velocityAt(tick: 3) }
  }
}
```

The kit is one module used by the island and every corner, so the numbers
cannot drift apart across an authority change. Each window is written in
seconds and must land on whole ticks at the world's rate: at 60 Hz the coyote
window is six ticks and the dash twelve, and the tests pin both edges of each
window because a feel regression is otherwise invisible until someone plays
it. A press on the seventh tick still leaves the ground behind, but as the
double jump, which is why it spends the air jump.

The `rim-drop` and `traveller-kit` canaries under `tests/Puck.World.Canaries`
hold what today's engine already does, measured on the real executable. A body
that walks off a deck through a border lying flat beneath it is handed to the
world below while airborne, still falling, with its per-body registers intact.
A kit authored at 60 Hz has coyote time (the recency predicate), a buffered
press (a trigger's latch), the jump cut, a dash of exactly 5 m on the ground,
instant acceleration (a shaping row with no rate), air charges restored on
contact (a slot's reset fact), timers, and unequal rise and fall gravity. What
these pages still ask of the engine:

| Ask | Why |
|---|---|
| A flat border maps a body straight down | Today the arrival is mirrored across one horizontal axis and turned 180°, because the map reverses the boundary frame's right and normal, which is right for a wall and wrong for a floor; the pair validator proves only that world up survives, so nothing refuses it |
| A pitched border's hysteresis sized for a hovering body | Its deadband is sized for a settling body, about 0.03 m at 60 Hz, so a jetpack held at the plane can change authority repeatedly |
| A refused crossing keeps a falling body's velocity | Every refusal clamps the body inside the boundary and zeroes its velocity |
| A press placed on an authored tick | A console press lands on whatever tick the pump drains, so no feel test can say "tick 6"; the test runner's schedule is where it belongs |
| An impulse that suspends gravity | A dash in the air covers its 5 m and keeps falling |
| Two actions on one button, chosen by a gate | A kit has one press trigger per channel and refuses `if` in a body action, so a jump and a double jump cannot differ in launch speed |
| An action gated on a held role channel and a state row | The jetpack is only expressible as a lift hold that spends a row, with no gate of its own |
| A duration that is not a whole tick is refused | A dash of 0.22 s is accepted and ends on a partial step; the state engine refuses the same mistake by name |
| Read-backs for a body's velocity, a hold's spend, and a body's state on a named instance | A test can see that an arrival is falling but not how fast |
| `kit`, `action`, and `timer` as constructs | Today a kit is an open block of document members |

### Page 4 — Snake's step, as a check on the small scale

```puck
module snake(width: Int = 8) {
  function cell(row, column) = row * width + column
  function wrap(value, delta) = (value + delta + width) % width

  rule step {
    when alive and tempo.elapsed
    mode: edge
    local column : Int = head % width
    local row : Int = head / width
    nextCell = select heading {
      east:  cell(row, wrap(column, 1))
      south: cell(wrap(row, 1), column)
      west:  cell(row, wrap(column, -1))
      north: cell(wrap(row, -1), column)
    }
    stepSerial += 1
  }
}
```

The same rule today is one line of four nested conditionals over
`$bind:rw * 8`, and every name carries a `snake` prefix because the source
has no namespace.

---

[Plans](README.md) · [Decisions](../decisions/state-and-language.md) ·
[The landing record](state-rebuild.md) ·
[Abstract-machine costing](abstract-machine-costing.md)
