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

## Implementation status

Package status below describes the code as it stands; it does not imply that
every package is complete.

- **The rebuild** is landed on `main`: the sweep that deleted the old
  substrate, the relocation into `Puck.State.Topology` and
  `Puck.State.Generators`, and the documentation true-up.
  [State and rules](../reference/state.md) owns the current contract.
- **Games.** Ten of sixteen are landed: Reversi, Hidden ranks, Hearts, Snake,
  Word spy, Paddleball, Arena, Rulepush, Tetromino, and Go. Lineup landed
  outside the sixteen, in the [Parlor package](../../experimental/worlds/parlor/README.md#lineup).
- **Embeddings** is done: the open defects in `Puck.Embeddings` and `puck embed`
  are closed, each with a law ([Embeddings](#embeddings-done)).
- **C1** inventories semantic accounting and report gaps
  ([C1](#c1--ceilings-as-prices) holds its status and what remains).
- **S1** is landed: `WorldCompiler.Compile` is the one door, and
  `ProjectionLawTests` holds over every shipped source and a generated corpus
  of every registered construct. `puck lint` calls
  `WorldSourceDiagnostics.Diagnose`; the language server runs the two tiers
  `Diagnose` composes, `DiagnoseSource` and then `DiagnoseSemantic`. The source
  tier compiles through that door and runs the lint; the semantic tier runs the
  engine's validation of the composed world and the reference lint, and only
  when the source tier reported no error. An editor therefore refuses what
  `puck lint` refuses, by the same code, text and line.
- **S2** is landed: the syntax tree carries trivia, `puck format`,
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
- **S3** is landed: an operand is a call or a bare
  name, a document holds a reserved channel as a node, and no shipped source
  spells a colon channel outside an interpolated string. A member has one
  spelling wherever it stands, read from the document model through the
  position lowering carries: a name, a cell key, a value expression and an
  enumeration word are bare, and text is a string literal (PUCK110,
  PUCK111). A direction is a name of its topology. An interpolated string
  computes a name, a key, a number or text and never an expression
  (PUCK112), so no member's text is read in the document's dialect. Sugar
  operands now hold parsed trees, and member expressions project into those
  trees structurally. Atoms remain opaque values; constants, enums, families,
  derived expressions and module aliases bind through syntax rather than
  text replacement. The formatter and rewriter preserve literal versus live keys.
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
- **S8's `test` construct** is landed:
  `test "name" [with module(arguments)] { given when expect }` at the root of a
  world document or of a module body, described once in the construct table,
  lowering through `WorldCompiler.Compile` to one generated test world per block,
  and `puck test <file.puck | directory>` running them. A test written with a
  module stands that module up alone under the test's own arguments; a test
  written inside a module body runs once per distinct instantiation, under that
  instantiation's arguments, named for the instance. A world compiled without
  `puck test` is byte-identical to the same source with its tests deleted,
  whether or not a module it uses brings tests, and the CLI suite runs every test
  a shipped world and asset package authors. A subjectless test at the root of a
  source that emits several worlds is the composed run: `<world> { … }` inside
  `given`, `when` and `expect` says which world a line is about, the first
  declared world boots and arms the rest, and a body crossing a `border` is
  asserted on the far side by the destination world's own rule. The
  `given`/`when` vocabulary the drafted pages below use — standing at a place,
  walking, pressing a channel, reading a body's own position — waits on the
  operands a rule can read those through.
- **The proving ground** is the debug world. Its device row, board games,
  traveller records, and authored motion supply composition workloads.

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

### C2 — Variable-width cell sets

**Status:** implemented. Cell-set expressions retain an inline path through 256
cells and use immutable additional words up to the topology ceiling. Laws cover
word boundaries, 19×19 enclosure, and authored per-noun boards at 33×18. The
64-cell mask conveniences and generator alphabet/drawn masks keep their
separate widths.

**Owns:** `BoardMask`, `BoardQueries`, `CompiledTopology`'s shift and image
tables, `CompiledPattern`'s term and letter masks, `CellSetExpression` and its
lowering, the search's `Legal` and reach paths, the browser's board-mask export.

**Delivers:** the internal carriers widen to the topology's word count; a cell
set wider than a word lives in a board row, one cell per cell, combined with
`boardCombine` and the cell-set algebra at a width taken from its topology,
with an inline path for sets of at most 256 cells so today's documents pay
nothing. `board(mask, …)`, `writeSet` from an Int cell, and the search's `Legal`
row remain the convenience for boards of at most 64 cells and say so; the
reach board is the general form a wider plan must declare. Generator drawn
masks stay 256 wide.

**Check:** the cell-set algebra laws run at 64, 256, 361, and 594 cells; a
19×19 board's enclosure and a 33×18 board's per-noun sets compute through the
same operators.

### C3 — A match answers where

**Status:** implemented. Occurrence laws cover both board axes; knowledge follows
token identities in the shipped `hiddenranks` world.

**Owns:** the `match(…)` operand family, its spelling and decompiler.

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

**Status:** implemented. The kernel includes bounded cycle refusal, stable
multi-occupant movement, and separate `pushPattern` and `stopPattern` selectors. A
`stopPattern` occupant blocks the move even when `pushPattern` also selects it; passable
occupants remain in place and may share the terminator cell with the mover. The
`push-ray` executable canary exercises the authored pool iteration and
live-origin binding.

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

**Status:** implemented by the pool iteration vocabulary. A rule snapshots the
original live handles with `for each`, filters them by their typed value field,
and rewrites each matching token inside the same atomic firing. A later refusal
rewinds every rewrite. `writeSet` remains the board-cell operation; it gains no
second token mode.

**Owns:** the pool iteration and typed pool-field effect path used for a token
rewrite.

**Delivers:** a value-filtered `for each` rewrites every originally selected
rock into a flag in one firing. Multiple noun rules still compose as separate
rule firings in the authored game.

**Check:** a law rewrites every token of one value in one firing and rewinds
as one.

### C6 — Undo across turns

**Status:** implemented. Retained journals are admitted against the byte ceiling,
validated before checkpoint installation, and covered by multi-turn, checkpoint,
relayout, and allocation laws. See the [current undo contract](../reference/state/rule-groups.md)
for the boundary between selected state rows and host continuation state.

**Owns:** the arena's journal retention, the rule-group `undo` declaration, a
`rewindGroup` effect, the hash, the checkpoint codec.

**Delivers:** a rule group declares `undo` over a set of rows with a depth;
each committed turn keeps its journal entries for those rows as a closed
segment in a ring of that depth, and no scope stays open between ticks.
`rewindGroup` restores the newest segment and drops it, refused by name when
the ring is empty. A group declaring undo holds no irreversible arm and no
host-owned write; a turn that wrote outside the set marks its segment
unrewindable. Who may undo is the gate of the rule that fires `rewindGroup`.
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
line breaks; `puck format` is parse then print for `.puck`, and the text formatter is
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
once through S5, and the parser stops reading the old spellings.

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
and a construct is off the hand-wired path when its old code is deleted.

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

**Owns:** a `test` construct; the `schedule` section and the `verdict` row
trait in `Puck.World.Schema`; `puck test` in `src/Puck.Cli`; the deletion
of the C# game laws and the baseline runner's raw cell writes.

**Delivers:** a `test` block in a module or world lowers to a generated test
world: the subject `use`d with the test's arguments in an empty host, `given`
as initial cells through the host's ingress, `when` as tick-scheduled command
rows, `expect` as rules whose gate is the expectation and whose effect writes
a verdict row folding the values the gate saw. `puck test` boots each test
world headless to its last scheduled tick and reads the verdicts from exported
state, and `puck test --reproduce` runs each world on the server host twice and
compares the two exports byte for byte. A module's tests run once per distinct
instantiation, wherever it is used, and `puck test` is the door that runs them:
the transpiler cannot boot a world, so `puck compile` stays a pure lowering and
a `use` of a module whose tests fail is not a compile refusal. A module's
parameters are its test seam.

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
code. A checkpoint or a save inside an armed run is refused. A test over several
`world` statements boots the set in one process and reads its verdict in the
destination world. The export-hash baseline stays as the determinism check only.

**Check:** every reusable module in the debug world ships tests, green on the server
host and reproducing under `--reproduce`; the C# laws that pinned a shipped
game's behavior are deleted with the tests that replace them; a failing verdict
names the gate and the values it saw.

The runner half is landed and is what the construct lowers to: the `schedule`
document section (rows of tick/principal/command/expect plus the derived export
tick), the `verdict` state-row trait, `world.verdicts`, and
`puck test` over `experimental/tests/Puck.World.Verdicts`.

What a green run now means. The section submits nothing unless the boot armed it
with `--schedule-dir`, and a row may open only with a verb from a closed step
vocabulary tied to the live command registry by a law, acting as a seat under the
document's own grants. A step is due in the tick after the one it was submitted
at: its session stamps the earliest capture tick rather than the capture
clock's now, which is wall time, so a world whose tick takes about as long as
its period lands each step on the same tick every run instead of wherever the
simulation had caught up to. Reaching the authored export tick is part of the verdict,
and every declared row is reconciled against the manifest at its own position.
Only a rule's own effect writes a verdict row: the effect door stamps the
firing's tick, a status with no stamp reads never-evaluated whatever the cell
says, a failing verdict is sticky so the report names the first failing tick, and
every other door refuses the write by one text the boot loader shares. Each row
declares the outcome it expects and any other recorded outcome fails the world by
name, while an outcome the host rather than the world answered — a handler that
threw, a principal with no ingress, a refusal that never reached a handler — is a
usage refusal instead. Ordinary authored tests run one unpaced leg;
`--reproduce` adds a second and compares both exports and manifests byte for
byte. The manifest records only facts a rerun reproduces. A scheduled run
refuses `world.save`/`world.load`/`world.reload`/`world.undo` by name: a schedule
carries no cursor, so a restored world would submit every row again from tick 1.

A composed run is one source. `schedule.instances` arms sibling worlds beside the
booted one through the same `WorldInstanceHost.TryStart` that
`world.instance.start` takes, every armed world is exported at the one host step
the booted world's export tick falls on, and `puck test` reads each world's
verdicts out of its own export and compares every world's bytes across two legs.
A subjectless `test` at the root of a source that emits several worlds lowers to
that set: one generated document per world, the entry world carrying the
schedule that arms the rest and naming each sibling by the document the run
writes beside it. Inside `given`, `when` and `expect`, a `<world> { … }` block
says which world a run of lines is about; there is no default world, so a line
that names none is refused, as is one naming a world the source does not declare.
The tick grid stays outside every such block, since one grid carries the whole
run. A far world's verdict rule fires one tick before the booted world's export
tick — an instance armed at boot counts from its own zero — and the lowering
derives that rather than leaving it to the author.

What a step can address is narrower than what a block can: a row's `world` member
rides the trailing `instance:` token a verb's own grammar reads, which
`player.join`, `player.leave`, `body.fly`, `body.pose` and `body.stop` carry and
the `world.*` state verbs do not, so a step addressing another world with one of
those is refused at compile time by the same rule the validator applies. Driving
a far world's state therefore means driving what crosses into it:
`tests/Puck.World.Verdicts/composition` flies the seat standing in the booted
world over a `border`, and the destination world's own rule writes the row the
test expects when its `placements` region reads as occupied. The seat is embodied
by the module's own `bodies.seatSpawns`, so the composed worlds carry the kit,
collision and seat rig a body needs rather than inheriting them from a basis.

Two gaps stay open. The browser host cannot run a leg at all — it has no
command ingress, no acting principal and no authoritative server — so
`--host browser` is refused by name and server-versus-browser determinism is
unproven; native Windows against Linux is held by `puck determinism`. A
scheduled command's own mutation verdict is recorded as a run-level echo in
`test-run.json` rather than attributed to the row that submitted it, because a
verb registers its correlation id inside its own submission; so an echo refusal
no row expected is printed rather than failing the run, and a rule operand over
recorded refusals would let a world assert on its own refusal directly instead of
on the refusal's consequence.

The construct is what behaviour is written as. `test "name" [with
module(arguments)] { given { } when { } expect { } }` stands at the root of a
world document or of a module body, is described once in the construct table,
and lowers through `WorldCompiler.Compile` to one generated test world per block
— the `given` cells, plus `when` as `schedule` rows on a tick grid, plus one
verdict row and one rule per `expect` line, the rule gated on the export tick
alone so it fires once. What that is added to is the test's subject: the
enclosing document unchanged for a test written at a world's root, and the named
module expanded with the test's own arguments, standing alone, for a test
written with one. `puck test <file.puck | directory>` compiles the source and
runs those worlds, and `--keep <dir>` leaves each of them on disk as an ordinary
world document.

A module's parameters are its test seam, and a module's tests run once per
distinct instantiation. A `test` written inside a module body is carried to
every `use` and every `world name = module(arguments)` that stamps that module,
restated there as a test with that instantiation's own module and arguments, and
named for the instance it ran for. Two instantiations that read identically run
it once; two that differ run it twice, against what each asked for. The
enforcement is `puck test` and nothing else: the transpiler cannot boot a world,
so `puck compile` stays a pure lowering and a `use` of a module whose tests fail
is not a compile refusal. A source whose modules are never run through
`puck test` has no verdict from them.

What it cannot yet say. An expectation reads what a rule reads — a state row,
never a body's own pose or position. A generated verdict row carries no
`Generated` mark because no document member does; the verdict trait is what says
the row is machinery. A `test` written inside a module body reaches only the
module's own names, so a claim about a `let` the enclosing source declares is
written beside that source instead. And a composed test drives its far worlds
only through the verbs whose grammar carries a world token, so a claim about a
far world's own state is written as a claim about what crossed into it.

A verdict row is an Int row, so what a gate read of a `Fixed` or a `Bool` row
is written by the same firing to a `witness` row of that kind: a row trait
naming its verdict, refused at every door the verdict is refused at, frozen
when the verdict settles, and printed beside the verdict's own cells. A rule
compares numbers, so no gate reads a `Text` or a `Vector` row and neither has a
witness.

The gate and effect operand lowering recognizes bare `true` and `false` as
boolean constants. A `Bool` row can therefore be compared with those literals
and assigned from them without the document compiler mistaking either word for
a row name.

Named verdict rows use the explicit `state { world { row { … } } }` vocabulary,
including the status cell that stays never evaluated until a rule fires.
An authored `schedule` carries a refusal expectation and optional refusal text;
`simulation { rateHz: 0 }` keeps a stopped schedule stopped. A generated test's
`when` spells `seatN refused ["text"]: command`, and the decompiler preserves
that outcome as well as distinguishing authored names from generated
`expect$N` rows.

All five scheduled fixtures under `experimental/tests/Puck.World.Verdicts` are `.puck`
sources. Their parsed documents survive native decompile and compile exactly;
`puck test` collects the authored schedule/verdict form through its normal
composition and staging path without manufacturing a generated test block.
The stopped fixture is booted directly with `quit` so its unreached rows are
observed without waiting for an export tick it cannot reach.

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
execution traces on exhaustively enumerated small worlds. The surviving composition contract is in [S10 step 7](#s10--the-state-review).

**Check:** `observed modeled service <= admitted static service` on the
enumerated worlds; failed speculative work, restart, and cancellation cases; equal reports native
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
costlier of its two paths.

Search spends a `SearchWork` allowance: what the sheet and one fold of every
row leave, the whole of it each job's, since a tick steps one job with work to
do. Every unit of
the walk is priced, the costliest is reserved before any runs, replay and
restart come out of the same allowance, and a share that cannot cover a
restart, a full replay and one unit is refused by that sum. A chance ply is a
ply on the walk's explicit stack, at the root or inside it, so a step suspends
inside one and a checkpoint carries what it has folded; a tree step is a unit
like any other. `ArenaSearchAllowanceLawTests` holds every step under its
allowance against a judge counted from outside the search.

Owed: the line-of-sight test's structural work envelope is not integrated into
the semantic cost context. Charge its bounded exact field-program samples and
grid accesses through [S10 step 7](#s10--the-state-review), alongside failed
work and aggregate search accounting. Full-workload timing measures headroom
separately; it never sets a replay's authoritative allowance.

### Embeddings (done)

**Owns:** `src/Puck.Embeddings`, `EmbeddingLock`, `EmbedCommand`,
`VectorStateLawTests`.

**Delivers:** the open defects, each with a law: server fixes gain their tests
(a copy into an absent key, transform refusals by name, an Edit-only principal
applying each transform); weak tests made to fail when their subject breaks;
`puck embed` exits 2 without writing when lowering has errors and collects
texts from both dialects; `Puck.World.Transpiler` references no
`Puck.Embeddings` (lock spaces carry `Puck.State`'s `EmbeddingIdentity`); `PruneEntries`
and `PruneSpaces` are called; `EmbedCommandTests` compares a fixture lock byte
for byte.

**Check:** `puck embed --check` offline with fixture locks regenerating
byte-identically; both authored samples boot and trace their rules; the
`packages.lock.json` of `src/Puck.Transpiler` and of `src/Puck.World.Transpiler`
lists no embedding provider package (no `OpenAI` or `Azure.AI.OpenAI` entry).

### G4 — Tetromino

**Status:** implemented as [games/tetromino.puck](../../experimental/src/Puck.World/Assets/worlds/games/tetromino.puck),
the falling-block game under the name the decision record gives it. A piece's
life is the `piece` workflow (spawn, fall, lock, clear); gravity is a `cycle`
row a level; the lock delay is a `schedule` deadline; the full-row pattern reads
the well's line masks and its occurrences drive the clear; the bag is a
weighted source drawn without replacement with its drawn mask, restarted every
seven; the score row saturates. Turns take the published wall-kick tests from
static rows, and hold, soft and hard drops are ordinary rules. Its `test` blocks
lock three pieces and clear one row, clear four at once, deal a bag of seven,
expire a lock delay, saturate the score and end a game on a blocked spawn, and
reproduce byte for byte; the `tetromino-clear` canary plays the three-piece
clear through the real `Puck.World`, `TetrominoTickLawTests` pins a piece's
life by counters, and the worst-case tick prices at about 109,000 work units.
The quarantined game-claims law
(`experimental/tests/Puck.State.Rebuild.Corpus/CorpusInventoryTests.cs`) held
the game's own source to every construct it claims. The package exposed two language gaps, each closed with laws: a
compile-time `for` could not stand inside a rule, step or effect list (a step
parsed it and dropped it), and a computed name that named a rule's local read a
row of that name instead. The well is simulated but not drawn.

**Owns:** `games/tetromino.puck`, baselines. Constructs: `workflow`,
`scheduleState`, `cycle`, row patterns, 7-bag draw, `overflow: saturate`.
Spawn, fall, lock, clear as a `workflow`; gravity as a `cycle`; lock delay as
a scheduled deadline; a full-row pattern that clears; a 7-bag generator with a
drawn mask; score saturating. **Check:** the sequence locks three pieces and
clears one line; the corpus inventory counts every claimed construct.

### G1 — Go

**Status:** played at 19×19 on
the debug world's Go table ([go.md](../../worlds/debug/go.md)), whose rules
are the same move door under a physical table of stones.
[go-tests.puck](../../worlds/debug/go-tests.puck) shows an atari, a group
capture, a suicide refusal, a ko refusal and the exchange that lifts it, the
area count, occupied-point arrangements put right on the table and one CPU
move, and reproduces
byte for byte. The 19x19 package exposed four engine faults, each fixed with
laws: a tree search judged every
candidate of a node when it expanded, so a 19x19 pool filled after about
twenty expansions (it now grows one node an iteration under progressive
widening); the canonical fingerprint folds mirror images, so the live table's ko check
compares the predecessor board point for point; a fold was scheduled and priced by
its row's authored cells, so a fold over a row written at run time was stale in
a memoized local; and an assignment target could not take the `$"…"` key a
read takes. Capture is `clearEnclosed`, the placement's own liberty flood. The
beginner opponent uses a one-ply tactical and positional score, bounded by eight candidates per step.

**Owns:** `worlds/debug/go.puck` and its tests. Constructs:
`clearEnclosed`, `writeSet`, `board(component, …)`, `board(boundary, …)`,
`board(enclosedAt, …)`, `boardCombine`, `stabilize`, `history(row, age)` for ko, UCT
search. A 19×19 board (after C2); capture by liberty fixpoint; ko refusal
through the history ring; territory count; a `search` row that plays one CPU
move. **Check:** an atari, a capture, and a ko refusal, each visible in the
export.

### G16 — Rulepush

**Status:** implemented as the [rulepush package](../../experimental/worlds/rulepush/README.md). The
rules are one module and each level is only its map; an overworld borders the
three levels and opens a gate from the visitor's own record. The package's
`test` blocks cover the check below except the hash comparison. The
`rulepush-undo` canary covers that comparison against the real `Puck.World`,
and `rulepush-overworld` plays from the overworld into the levels and back. The
board is simulated but not drawn.

**Owns:** `worlds/rulepush/`, its canaries, and the turn cost laws. The rule
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
gaps; this package holds what landed and the designs still to choose.

Landed:

- **The spelling-equivalence corpus**,
  `tests/Puck.World.Transpiler.Tests/SpellingEquivalence/` with
  `SpellingEquivalenceCorpusTests`. Each pair is two `.puck` documents that
  mean the same; the pair holds when both compile to the identical canonical
  document and the identical generated test worlds, so every identity and
  price derived from the document agrees. A pair the compiler does not yet
  hold is listed by name in the test against its open item, and fails the
  test the day it starts holding. A new spelling lands with its pair.
- **Defaults are canonical.** A shape row's `group`, `material` and `position`
  carry described defaults, filled on lowering like `blend`, `rotation`,
  `scale` and `smooth`, so a member written at its default and the member
  omitted are one document. A shape's literal `rotation` lowers in its one
  sign (`w > 0`, or the first nonzero of `x`, `y`, `z` positive when `w` is 0),
  so `q` and `-q` key one creation (the audit's S10, confirmed: the
  canonicalizer normalized length only, so the two hashed apart).
- **`allow CODE "why"`** on the line before a row, `let` or other statement
  acknowledges one warning or note over that statement's span. It never
  reaches the document. An unknown code, a missing reason or nothing to cover
  is PUCK124; an error is never acknowledged.
- **Diagnostics read against their author.** A finding against the composed
  world is placed on the line that authored it: the source's own layer through
  its source map (which names the library a template came from), or a basis's
  or import's row through the origins the composition retains, read through
  that document's own source map. `puck lint <dir>` prints a finding placed in
  another file once. An imported declaration is linted only in its own source,
  and a module library's lets and templates are never reported unused.
- **`for` and `repeat` in a test's `given` and `when`.** `for item in [...]`
  binds names a given line's key and value read and a seat step's command
  substitutes as `{item}`; `repeat n { }` repeats steps. Both expand at
  compile time to the lines written out (the corpus holds both).

#### Designs to choose

Each lowers to the existing canonical form, so the corpus proves it with a pair
the day it lands. Each lists options and a recommendation; the owner chooses.

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

Recommendation: (a), shallow, with a nested override spelled by nesting
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

Recommendation: (b) now, since it needs only G1 and the existing library
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

Recommendation: (a), as decided. The pair is a call against the expression
written out with the arguments substituted.

**G7 — Totals summed by hand across documents.** `bodies.capacity`
(`worlds/debug/debug.puck:180`, summing counts `shapes.puck` restates from each
game) and `placements.policy.derivedFaceScreens` (`debug.puck:191`,
`garden.puck:83`, `night.puck:78`) are summed by hand from what imports
declare, and drift when an import changes.

- (a) Derive them: the engine counts the composed world's derived face screens
  and its populations, and the authored member goes (a ledger, which the
  owner's rulings delete).
- (b) Contributions: each import writes its share (`bodies { capacity += 54 }`)
  and composition sums the shares, as a basis merge does for keyed rows.
- (c) A compile-time fold over imports (`sum(imports, d => d.cardBodies)`).

Recommendation: (a) for both, since each is a function of the composed world;
(b) only for a budget the author must choose rather than count. With (a) the
pair is an authored total against none.

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

Recommendation: (a), one construct for G3 and G8. The pair is a guard named by
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

Recommendation: (a) now, since it removes the need at this site and fixes the
pair at its cause; (b) as an `if` around members if a site needs a member whose
zero is not "none".

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

Recommendation: fold constants now; then (a), since the shared part of the
seat-input rules is effects and locals as well as gates.

**G15 — Enum words in a `let` and in a row.** A row or a template's body takes
a closed-vocabulary word bare and refuses it quoted (`bond: "Surface"` in a
kit's hold is PUCK110), while inside a `let`'s object both `bond: Surface` and
`bond: "Surface"` compile to the same document, since the value's place in the
model is not known until a row reads it (the `enum-word-in-let` pair holds:
`worlds/standard/kits.puck`'s `groundHold` quotes its words). One spelling is the
fix: a `let`'s object refuses the quoted word too, once lowering can type the
let's value where a row reads it, or the row admits the quoted word and the
printer writes it bare. Recommendation: refuse it in the `let` (PUCK110 names
the bare spelling), migrating the standard's lets with `puck migrate`.

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

Recommendation: (a). The irregular offsets are history, not design; once the
ids are regular the existing template expresses the side, and the language
needs (b) only if several creatures (the pond's spider and hound) want it.

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

Recommendation: one lowering for a write whatever names its row, key or value:
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

Landed: the rebuild and the means to build and test a world against it
([state reference](../reference/state.md)), S1 to S5, S6's expander (modules, `use`,
`import`, several documents from one source, `border`, `door` and the asset
lock), S7's substrate and World integration, S8's runner, `test` construct and
composed run, the costing correction, C2 to C6, and the games G4, G1 and G16.
A `test` inside a module runs once per distinct instantiation under `puck test`;
a `use` of a module whose tests fail is not a compile refusal, because the
transpiler cannot boot a world. `puck test` runs on the server host; running the
same test world through the browser host is not scheduled.

What is open, in order:

| Step | In parallel | Why here |
|---|---|---|
| 1 | The job-system memo and S10 step 0, then the single allowance in step 7 with C1's accounting inventory | Parallel storage and evaluation need one chosen ownership and ordering contract. |
| 2 | S6's reusable modules in the debug package, after the fundamentals | Proves the language on the live proving ground; missing experimental features are ported there. |
| 3 | S7's two open steps: [remote home adoption](records-and-pools.md#remote-home-adoption) and records carrying timed row traits | Remote adoption closes the debug-world record round trip across separate authorities. |
| 4 | The deferred games, each when the capability it forces is otherwise unproven | See the table above. |

After the landing a package branches from `main` and lands as its own squash.

### The packages C2 to C6 serve

C2 serves Go at 19×19 (G1), and C3 to C6 serve Rulepush (G16); Tetromino (G4)
needs none of them and proves the same substrate the landed games already
exercise. All five capabilities and all three games are implemented, each game
verified by its own `test` blocks and its canary: the debug world's `go-tests.puck` for Go,
`rulepush-undo` and `rulepush-overworld` for Rulepush (with `push-ray` for C4),
and `tetromino-clear` for Tetromino.

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
dotnet test tests/Puck.World.Games.Tests -c Release --filter-class "*ShippedWorldStateBaselineTests"
```

```bash
puck architecture --check
```

```bash
puck schema --check
```

```bash
puck format worlds --only puck --check
```

```bash
puck test worlds
```

```bash
puck embed --check
```

```bash
puck landing --against origin/main --base <the commit the branch was authored from>
```

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

- S7's substrate and World integration are present: generation-aware single and
  pair pools, snapshots, lexical claim/release/iteration, logical body carriers,
  pair interactions, identity record transfer, and the Paddleball and Arena
  migrations. The open package is [remote home adoption](records-and-pools.md#remote-home-adoption)
  across separate authorities and the timed row traits still absent from records
  that [S7](#s7--records-and-pools) names before converting their consumers.
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
