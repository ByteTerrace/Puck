# State system rebuild: the landing

`Puck.State` is rebuilt on one storage substrate, one cell representation,
typed host capabilities, and atomic rule firing, while every authored spelling
keeps its meaning. The design is in
[the decisions register](../decisions/state-and-language.md#the-rebuild); the
programme it belongs to is [State and the authoring language](state-and-language.md).
This page is the record of what is still in flight before the rebuilt
substrate lands on `state/rebuild`, written as work packages so each can be
handed to one agent.

## Implementation status

Everything up to the facade split is on `state/rebuild` at `85fe71f75`:
waves A, B, and C, the switch wave, the flip, the whole of WP11b, WP11c,
WP11d, WP11e, the costing evidence, S1, S2, and the dashboard schema-id
sweep, each reviewed once by a fresh agent and merged with its suites green.
The sweep — parts A, B, and C, the `StateCell` collapse and the nine deferred
items included — and WP8's relocation are merged into `state/rebuild` too.
The old substrate is deleted, and the whole of it is squashed onto `main`;
`state/rebuild` and the integration branch are retired.

The automatic canary set is green on that head when the machine is quiet.
Three proofs pin exact values at fences measured from where a piped console
command lands, so they race the wall clock and go red under a concurrent
build: `front-door`, `flow-conservation-live`, `tabletop-state`. Rerun one of
those alone before believing it.

## Where the sweep stands

The sweep is merged into `state/rebuild`. The old substrate is deleted, about
39,000 lines: the frame, the store, the old search runtime, and the old rule
stack that still sat in `Puck.State` beside its live twin in
`Puck.State.Rules`, which this page's deletion list never named. `StateCell`
keeps its key and traits and carries one `CellValue`; a cell whose kind
differs from its row's is refused by name at the arena's import, and that
refusal is what caught the one cell the conversion gave the wrong kind.

Every suite the branch owed has run on the merged head and is green,
`tests/Puck.World.Tests` included.
Every deferred item below landed, each its own commit with a law.

Two faults about what the rebuilt substrate means are settled. A cell a rule
writes now reaches a client: the arena-export install marks state delivery
pending, which is the same door a console write's install marks, and a law
reads the value back off a real `WorldClient`'s own definition. The capture
scope now folds each authored cell's clock after its value, so two worlds
differing only in a follower's sampled position and velocity carry different
capture digests; that moved every capture hash once, and the shipped-world
exports, the two shipped state-hash baselines, and the `dive-medium` canary's
observations are re-recorded against it.

The `tests/Puck.World.Tests` failures `state/rebuild` inherited are all
cleared. Most shared one cause: the shared fixture's rate had moved from 240 Hz
to the unauthored 30 Hz while the laws' traces and step counts stayed at 240,
and those laws now ask for their rate by name with no trace re-recorded. Four
were the engine's: a deferred contribution retraction never re-armed its
deadline, a re-declared row restarted a cell that kept its own trait and stored
a stopped cycle's phase rather than the value it showed, a body past a seam
plane took its own rim's height rather than the neighbour's floor, and every
tick allocated a closure refreshing attached colliders it did not have.

What the landing measured. Re-recording every shipped world's export and cost
on the merged head rewrites nothing: each reproduces byte for byte, and no type
the rebuild deleted is named in any source file. Wall time is the median of five
recordings with nothing else running, against WP0's record on the same machine.
Every world is within 1.25 times that record or faster, most near half, but
two:

- **Chinese Checkers, 1.4 times (4.0 to 5.7 ms a tick).** The world plays
  itself, so its search judges candidates on every tick. A sampled profile of the
  real executable puts 87% of that tick in the judge's rule — about a hundred
  keyed cell reads a candidate, each resolving its slot through the row's key
  dictionary again — and none of it in the board recompute on a membership
  change, which does not appear in the profile. The tick is a sixth of the
  world's 30 Hz step, so it does not block. The follow-up is
  [C1](state-and-language.md#c1--ceilings-as-prices)'s: a compiled operand over a
  literal key holds its slot against the row's own reindex.
- **Billiards is not comparable.** WP0 timed the bare module, which declares
  one row; its sequence has since gained a host, a seat and twelve rigid balls.

The whole automatic canary set holds, run alone from a copy of the CLI, and
`puck parity` holds every verdict on both backends against the contract as it
stands, so nothing under `tests/Puck.Parity` is re-recorded. `puck landing`
refuses its own comparison, as it should: `main` has not moved since the branch
was cut, so no landing arrived that the squash could drop, and the squash's tree
is this branch's tree.

Backgammon measured 47 times slower before one correction and is now faster
than WP0. A search took a fold of every input row's content on every tick to
learn whether its position had moved, idle or not; it now takes that fold only
when some input row's version has moved. The stamp a job compares is still the
fold of content, so a restored world continues as the uninterrupted one does.

## Work packages

Each package lists **Owns**, **Deliverables**, **Check** (the command and the
observable pass condition), and **Hand-off**. Load the skills each package
names before starting. The order was the sweep, then the relocation and WP9b's
remainder, then WP13, WP14, and all of it is landed. One
integrator serializes edits to project references, `build/Architecture.props`,
the generated project map, shared fact bases, and World ingress and
serialization.

### Hand-off discipline

Before switching consumers, retain WP0's input scripts and pre-rebuild exports
as immutable comparison inputs. Each package hands off its actual public seams,
refusals, ownership/lifetime rules, focused law cases, commands and outcomes,
and remaining blocked checks. A prepared package is not a verified package;
missing browser or runtime evidence remains explicitly open.

Each package is implemented by one agent and reviewed once by a fresh one.
The review's blockers go back to the implementer, whose context is intact,
with a law required for each; the lead repairs only what that round leaves.
A package whose completion check is a command (a relocation, a deletion sweep
from a named list, a rename, a re-recording, a documentation true-up) goes to
the smaller model and gets no reviewer; a package whose check is "and it is
correct" (a review, a root-cause repair, a port that decides semantics, a
capacity or language package) goes to the larger one. A package runs its own
suites and the architecture gate. The World schema suite, the corpus, and the
baselines run at each integration. The outside review reads the integration
branch once, after everything has landed on it and before WP14 squashes it
to `main`, rather than each commit or each wave as it lands.

Nothing has shipped, so no package spends effort preserving or explaining old
behavior. A test that pins behavior the new design changes on purpose is
re-pinned or deleted with its subject; the laws comparing the old substrate
with the new are deleted with the old substrate rather than ported; and a
package's laws for new behavior are few and pointed. What every package still
holds to is the engine's own contract: the build and architecture gates,
determinism, the state, rules, and search law suites, and a world that boots.

Operating rules every package and review follows, learned the expensive way:

- One worktree per package, branched from `state/rebuild`
  (`git worktree add .claude/worktrees/<name> -b <branch> state/rebuild`);
  every Read, Edit and shell command uses the worktree's absolute path. Never
  `git stash`. A branch takes `state/rebuild` by merge, never rebase, so
  reviewed commits keep their hashes.
- Never create a junction or symlink in a worktree whose target is inside the
  main checkout: `git worktree remove` on Windows deletes through junctions.
  Rebuild what a check needs (`npm ci`, `dotnet publish`) in the worktree, or
  skip the check and say so. Before removing any worktree, list its reparse
  points and delete the links with `[System.IO.Directory]::Delete`.
- Every `dotnet test` and every `puck` verb run is a background task with a
  timeout of at most ten minutes; `dotnet test` carries
  `--blame-hang --blame-hang-timeout 3m --blame-hang-dump-type none`. The
  World suite takes about 100 seconds; a run past five minutes is hung, and
  the blame output names the law. Never wait on a shell that has stopped
  producing output.
- Run `puck` from a copy of `src/Puck.Cli/bin/Release/net10.0`: a running
  `puck` locks its own bin, and the `Puck.World` build copies into it.
  `src/Puck.Cli/publish/puck.exe` and the global `puck` tool are whatever
  commit last published them; the portal's node suite calls the former, so
  republish it (`dotnet publish src/Puck.Cli -c Release -o src/Puck.Cli/publish`)
  before trusting a portal failure.
- A law that enumerates repository sources reads `git ls-files`, never the
  filesystem: a checkout holds other worktrees and scratch under it.
- `puck format --verify` over `src` has not completed on this tree (past 25
  minutes, stopped); `puck format --files` binds its value as the root
  argument (an open chip). CI's formatting bot is the backstop; do not block a
  package on either.
- A reviewer works in its own worktree at the branch head, reads every hunk
  and its enclosing member, reverts and reruns every claimed law, and writes
  blockers with file, line, failure scenario and evidence; blockers go back to
  the implementer by message and land as their own commits with laws.

### WP11b — The remainder

Skills: `puck-world`, `symbol-analysis`.

Landed on `state/rebuild-wave-d/wp11b-port`: one refusal registry anchored on
`Puck.State` and `Puck.State.Rules`, so `world.refusals` lists every door; the
server compiles and runs `ruleGroups`, with a refused group's members running
on neither host and a group's trigger admitted against the host that runs it;
every edge consumer reads one `CellValue` and switches exhaustively on
`CellKind`, the browser `[JSExport]` payloads and their TypeScript types with
it; a load is a birth, so a timed cell turns from the tick it was installed at.
WP11c and WP11d are on `state/rebuild` too: the compose holds one arena per
batch, `SettleCell`, `StateCellWriter` and `DerivedBoards` are gone, and body
action state lives in the arena's slot lanes sized by `WorldSlotLanes.Options`
on both hosts. WP11e remains, then the sweep.

### WP11c — The compose onto the arena

Skills: `puck-world`, `symbol-analysis`.

**Owns:** `WorldServer.MutationCompose.*`, `WorldStateReader`,
`StateCellWriter.cs`, `DerivedBoards.cs`, and the World laws that pin a
compose refusal.

**Deliverables:**

1. A batch holds one arena: load once, apply every member through the
   arena's own kernels in the order the batch authors them, export once,
   rewind on refusal. `OpenWorkspace`, `PlaceRow` and `SyncWorkspace` go with
   the workspace row-list they exist to copy. A same-batch row declaration
   ahead of a write is what the write sees, as today.
2. The arena's admission is the compose's admission: envelope, reserved
   cells, eviction and clock settling are enforced there and nowhere else;
   `SettleCell` and its clock rules are deleted once the arena's birth and
   resettle rules cover every case they did (a law per transition row).
3. `RecomposeDerivedBoards` disappears: the arena recomputes a derived board
   on a keyed write, and `WorldDefinitionValidator.Discrete.cs` checks against
   that recompute. `DerivedBoards.cs` is deleted with `RuleArenaFixture`'s
   consumers reading the arena.
4. `WorldStateReader`'s eight entry points (including `Reduce` and the three
   `ArgExtremum` overloads) read the arena at an `ArenaTime` the caller names;
   `TryParseNumericToken` moves to `CellValue`'s parse, and `StateCellWriter.cs`
   is deleted with `WorldStateBindingContext`, `WorldPlacementResponse` and
   `WorldIdentity` reading through the arena.

**Check:** `tests/Puck.World.Tests` green except the inherited failures, the
one refusal-text law (`BatchComposeLawTests`) re-pinned; every shipped-world
baseline and the browser parity fixture unchanged or re-recorded with the
reason in the commit; `puck references` returns nothing for `StateCellWriter`,
`DerivedBoards`, `SettleCell`.

### WP11d — Body action state onto the participant lane

Skills: `puck-world`.

**Owns:** `WorldBody.ActionState`, `WorldPopulationCheckpoint`'s arrays, the
participant slot lane and its codec.

**Deliverables:** body action state lives in the arena's participant slot
lane with its own codec; the checkpoint and federation codecs carry it from
there; the arrays go.

**Check:** the checkpoint round-trip and federation arrival laws green with the
fixtures re-recorded; the sharded and colocated four-corners canaries green.

### WP11e — The facade split

Skills: `puck-world`, `boy-scout`.

**Owns:** `WorldServer` and its partials.

**Deliverables:** `WorldDocument`, `WorldTick`, `WorldRuleHost`,
`WorldExtensions`, `WorldGrants` and `WorldPersistence` come out of
`WorldServer`, which keeps its public API, constructor and phase order; bodies
stay behind `IWorldGrantsView`; the federation and checkpoint codecs' records
nested in server classes are un-nested. `SweepDeadlines` runs escrows,
transfer escrows, contribution tenure and parks through `WorldDeadlineTable`
once per tick without LINQ; placement deals, responses and reflow reviews are
reactive comparisons and stay with their triggers. Follows WP11c and WP11d so
it moves settled code once.

**Check:** the solution builds; `tests/Puck.World.Tests` unchanged in its
results; `puck lengths` green with no new recorded file; `puck architecture
--check` green.

### The sweep — LANDED

Merged into `state/rebuild`: part A (everything below except the `StateCell`
collapse and the nine deferred items), the `StateCell`-to-`CellValue`
collapse, and all nine deferred items.

The deletion is larger than "the headline types", and not three commits. The
old rule stack lived whole beside the new one in `src/Puck.State` — a
same-named twin of every file `Puck.State.Rules` owns: `RuleCompiler.*`,
`RuleEvaluator.*`, `RuleEvaluation`, `RuleCompileContext`, `RuleVocabulary`,
`CompiledRule`, `Operands`, `RuleLatch`, `RuleSchedule`, `RuleWorkBudget`,
`RuleDataflow`, `RuleHazards`, `RuleCost`, `ZoneTable`, `ZoneEndKey`,
`CompiledValueSource`, `ExpressionEvaluator`, the vector operand and effect
facts, and `CellOrdinalIndex`. `IRuleReader.Store` is a `StateStore`,
`FrameHost` is an `IRuleHost` the old evaluator is constructed over, and
`WorldRuleVocabulary` is the only registrar of the old compiler, so the
frame, the stack, and the world's old reader and vocabulary are one compile
closure and land as one commit. `SearchRuntime` is separable and lands before
it. `BrowserSession.HostOwnDenseRows` is a no-op on a validated document and
lands after, with a law on each host. `StateCell` does not disappear: it keeps
its key and its traits, and its `Value`/`Text`/`Vector` siblings become one
`CellValue`, so the "`puck references` returns nothing" check applies to those
sibling members, never to the record.

Three declarations the deletion list expected to delete are kept, each with a
live caller: `src/Puck.State/RuleRefusal.cs` whole, because `Puck.State.Vectors`
raises 31 members of its two refusal families and cannot reach
`Puck.State.Rules` (Rules depends on Vectors) — the refusal census is unmoved,
and reconciling the two families is a design question; `VectorTransforms.cs`,
the vector arithmetic kernel `ArenaVectorTransforms` calls; and
`GateProgramEvaluator.cs`, the Boolean stack fold a rule gate and a body action
gate share. `RuleFacts` stays too: the live compiler, the cartridge expressions
and the expression speller all read its prefixes.

Each test file is classified rather than swept. A law whose subject is the old
stack (the frame, the store, the host, the old compiler or evaluator, the old
search runtime) is deleted with its subject, because
`Puck.State.Rules.Tests`/`Puck.State.Search.Tests` own that behaviour on the
arena. A pure old-versus-new differential is deleted. A law whose subject
survives is ported, never deleted: the expression suite moved onto
`Rules.RuleCompiler`/`RuleExpressions`, and `RuleFrameFixture` became
`RuleArenaFixture` — one arena over the catalog its rules compiled against,
loaded per position through `StateArena.TryLoad`, with `RuleLatch.Reset` before
each judge, because the shipped tabletop judges are `Edge` rules and a latch
shared across positions judges the first alone. A law that mixes the two is
trimmed to the half that survives, not deleted: `WorldFactsCompilerParityLawTests`
became `WorldFactsCompilerShippedWorldLawTests`, keeping every shipped-world
claim that reads the arena compiler alone.

The reviews of WP11b to WP11e deferred nine items to the sweep; each is its own
commit with a law. All nine landed: the browser's rebind loads at the session's
own time rather than the origin, so a cycling cell keeps the tick arithmetic
the server's resync gives it; the composition-cache law asks per (path,
fingerprint) key instead of counting a process global it also cleared;
`RemoveStateCell` on a lattice row is pinned to the arena's own refusal;
`BoardDerivation` names why a derivation could not be computed and sizes its
lanes through `WorldSlotLanes.Options`; two mutation refusals are pinned to the
arena's whole sentence instead of a fragment; the schema README spells a
postfix expression as an `instructions` array of `op`-named steps; and the two
contribution-tenure laws are green, one half a fixture rate and the other an
engine defect — a deferred retraction never re-armed the deadline the dequeue
had taken out of the table; trigger admission is lawed on the mechanism rather
than on either host; and the facade split's widened members are narrowed to
what their callers reach.

Trigger admission is `RuleNeeds.Admit`, and its law is
`ReaderFacetLawTests.AdmittingARuleWhoseFacetTheHostLacksRefusesWithTheFacetName`
in `tests/Puck.State.Tests` — a needs set built over a test-declared facet
`interface : IFacet`, refused by facet name against a reader that does not
advertise it, with `AdmittingARuleWhoseFacetsTheHostServesSucceedsWithNoRefusal`
as its control. The reachable half of group admission is pinned on the hosts
already: a group the browser refuses to compile runs neither itself nor the
rules it claims (`BrowserRuleGroupLawTests`), and the server refuses such a
document outright (`RuleGroupServerLawTests`). Each host's own
trigger-admission arm stays unlawed and unreachable by any document until a
second facet exists — `IWorldFacts` is the only declared facet and both hosts
serve it — so the mechanism's law is where the refusal is pinned; `WorldRuleHost.AdmitRules`/`AdmitGroups` are private inside
`WorldRuleHost.Install`, and the browser's loop is inside the private
`BrowserSession.Install`.

The facade split's widened members are narrowed by what reaches them.
Nine are read outside `Puck.World.Server`, all by laws; the rest implement an
interface a facade serves, or are reached only from inside the assembly, or
from nowhere outside their declaring type. The board-enforcement latch and the
decision runtime each serve persistence through one `IWorldPersistedSection`
seam — append-hash, capture, restore, plus the decision runtime's own
checkpoint validation — implemented explicitly, so those members leave both
facades' public surface and `WorldPersistence` holds two private seam
properties instead of reaching into seven public ones. `AdmitRules`/`AdmitGroups`
are private inside `Install`. `m_ruleInhabitantScratch` is gone: the
possessed-inhabitant walk is the tick's alone and owns its list, so no buffer
is aliased across facades. Everything a facade exposes only to the rest of its
own assembly is `internal`, and a member nothing outside its declaring type
reads is private. `WorldServer`'s public API, constructor and phase order are
unchanged; `ShippedWorldStateBaselineTests`, `StateAddressingBaselineLawTests`
and the checkpoint round-trip laws are the proof nothing observable moved.

**Check:** solution builds including the wasm publish; `tests/Puck.World.Tests`,
`tests/Puck.World.Schema.Tests`, `tests/Puck.World.Browser.Tests` green except
the inherited failures; the browser determinism canary in
`src/Puck.World.Browser/README.md` passes; `puck references` on each deleted
type returns nothing; `puck architecture --check` green.

**Hand-off:** the checkpoint fixtures re-recorded, for WP14.

### WP8 — The relocation, and WP9b's remainder — LANDED

Skills: `maths-usage`, `symbol-analysis`.

Merged into `state/rebuild`. Two new projects exist, each depending only on
`Puck.State` and `Puck.Maths` with `<PuckLayer>Engine services</PuckLayer>`:
`Puck.State.Topology` (`BoardQuery`/`BoardQueries`/`BoardCombination`,
`CompiledPattern`/`PatternRow`/`PatternMemo`, `ArenaBoards`) and
`Puck.State.Generators` (`GeneratorEngine`/`PrivateDraw`,
`TableDocument`/`CompiledTable`, `ArenaDraws`, `PenrosePatch`).
`PenrosePatch` takes its drawn start tile as a value so topology never
references generators; the law suites moved to `Puck.State.Topology.Tests` and
`Puck.State.Generators.Tests`;
`docs/project-map.md` is regenerated and every consumer's project reference
follows.

A type the package's own inventory expected to move stayed in `Puck.State`
whenever a core file — not a document-authoring convenience but
`ArenaLayout`, `StateArena`, or `StateRow` themselves — names it directly, so
moving it would put a core reference on the project that would own it:
`LatticeTopology`/`CompiledTopology`/`TopologyCompilation`/`TilingGenerator`
(`ArenaRowLayout.Topology` and the arena's import/export doors read a
`CompiledTopology` while laying out or walking `StateArena`'s columns),
`ArenaWord`/`WordSource` (minted by `StateArena.ReadWord`, a member of the
arena's own partial class, which cannot span two assemblies), `Draw`/
`StateGenerator` (`StateRow.Draw` types on `Draw` directly, and
`StateGenerator.MaskCount`/`Exhausts` moved onto the declaration itself so the
arena's layout compiler needs no `Puck.State.Generators` reference to size a
draw site's mask), `PatternNode`/`PatternCapacity` (core's `.puck` infix front
end, `PatternSpelling`, parses and prints `PatternNode` directly), `BoardMask`
(`CompiledTopology`'s own shift-mask precompute reads `BoardMask.MaxCells`),
and `PenroseRhomb`/`PenroseTiles` (`TilingGenerator` returns them, so they
need `CompiledTopology`'s own visibility). `StateVector` and `StateSpace` also
stayed in `Puck.State`, unlike the package's own inventory: the document JSON
converter and `StateSpelling` call both directly for every vector cell's
wire round-trip, and narrowing that to an interface seam is a deliberate
redesign no later package has taken up. `ArenaWord`'s constructor is `public`
rather than `internal` so `Puck.State.Topology`'s `ArenaBoards.TryReadRay` —
a separate assembly — can mint one (the accessibility ruling: widen the
member, not the assembly).

**Landed:** `puck architecture --check` green; both new test projects green;
solution builds including the wasm publish; the browser vector tests pin the
`Nearest` and `Remember` result rather than the old refusal.

### WP13 — Documentation and skills

Skills: `documentation`, `boy-scout`.

**Owns:** `docs/reference/state.md` and `docs/reference/state/*.md`;
`docs/project-map.md` (regenerated block plus the per-project rows);
`src/Puck.State*/README.md`; `tests/Puck.State*.Tests/README.md`;
`.claude/skills/puck-world/SKILL.md` and `puck-dsl/SKILL.md` and their
references where they name state types; `docs/plans/README.md`;
`docs/plans/open-items.md`; every other plan that names a deleted type
([machines and cartridges](machines-and-cartridges.md)) corrected where it
names one. The state addressing, state consolidation, and game state-and-rules
plans are already retired, with their decisions recorded
in `docs/reference/state/frames.md`, `docs/reference/state/rules.md`, and
`docs/reference/state/data-model.md` and their incoming links repaired.

**Reads:** every earlier package's hand-off.

**Deliverables:**

1. The seven state chapters rewritten to describe the arena, `CellValue`,
   facets, atomic firing, groups, families, the pattern algebra, and the six
   projects, at the student floor the documentation guide sets. No chapter
   says "frame subset".
2. Project map rows for the six projects; `puck architecture --map` output
   pasted.
3. An orphan audit: no link to a deleted file, no anchor to a removed heading,
   no skill or plan naming a deleted type (`puck search -M 0` over the old
   names).

**Check:** `puck doc-links` over every rewritten page green;
`puck architecture --check` green.

### WP14 — Landing

Skills: `boy-scout`, `puck-world`.

**Owns:** baselines only: the post-rebuild exports in
`ShippedWorldStateBaselines/`, parity contract hashes under
`tests/Puck.Parity`, checkpoint fixtures, `FileLengths.json` (new files under
2500 lines; nothing recorded grows).

**Deliverables:**

1. Post-rebuild exported state and cost per shipped world re-recorded. A
   moved export is re-recorded, not explained: nothing consumes these worlds
   (rules 4 and 5), so the bar is that the recorder's double replay agrees,
   the world boots, and its scripted sequence runs through without an
   unhandled exception. The same holds wherever a package moves an export on
   the way here.
2. Tick cost per shipped world under decision 7: work units re-recorded
   exact and machine-independent with one line per world explaining the
   change, and the median wall
   time per world on the recording machine within 1.25 times WP0's record,
   measured as the median of five runs without competing builds. A wall-time
   regression past the tolerance is recorded with its cause and becomes a
   follow-up; it blocks landing only when it makes a world unplayable at its
   authored tick rate. The measurement names the arena's whole-board recompute
   on a membership change, since that is the one cost the reviews flagged as
   a candidate for an incremental path.
3. `puck references` on every type the plan deletes returns nothing.

   Re-deriving `RuleCapacity.MaxWorkUnitsPerTick`, and every other ceiling,
   is [C1](state-and-language.md#c1--ceilings-as-prices), which follows this
   landing so one package owns every limit.
4. `puck landing` green; `puck parity` green after its contract is re-recorded.
5. The integration branch squashed to `main` with a hand-written summary
   (rule 6).

6. The full automatic canary set (`puck canary`, run from a copy of the CLI)
   with no concurrent build or test on the machine; the three wall-clock
   proofs named in the status rerun alone if red.

**Check:** the commands above, run from a clean checkout of the squash.

## Candidates adopted into packages

Author-facing changes the rebuild makes cheap. Each is adopted into a package
that already exists rather than becoming one of its own.

| Candidate | Adopted, where |
|---|---|
| `puck test` with `test` and `invariant` blocks in `.puck` | [S8](state-and-language.md#s8--tests-are-worlds), which is that package |
| Cross-host determinism in `puck test` | S8: every test world's exported state compared native against wasm, which is S8's parity check |
| Retire colon-channel spellings from `.puck` in favor of function forms | [S3](state-and-language.md#s3--operands-as-grammar), which is that package |
| Every ceiling refuses at compile time, naming the limit and the alternative | [C1](state-and-language.md#c1--ceilings-as-prices); [S6](state-and-language.md#s6--modules-and-the-forcing-world) names the module instance that crossed it |
| Refusals name the authored span | S6 for the attribution through module instances, [S4](state-and-language.md#s4--one-description-per-construct) for the span every described construct carries, both through the emitter's `SourceMap` |
| One refusal registry | WP11b, with a `RefusalCatalog`: `[Refusal(door:…)]` on every runtime refusal, and free-text `reason` strings become a code plus arguments |
| Kinds inferred from the cell and the unit; `Kind` leaves the authored surface | S3 for the operand forms that infer it, S4 for the construct descriptions that stop declaring it |
| One authored clock, the engine placing each trait on its tick coordinate | S3: rates and durations are spelled in seconds |
| One timing effect (absolute deadline), the other derived | S3: one spelling for "later" |
| Hazards in `puck lint` and the language server, plus an authored ordering word | Deferred. Rule order stays invisible semantics until a world is authored that the order surprises |

## Gaps the acceptance games found

The first seven acceptance games (Reversi, Stratego, Hearts, Snake,
Codenames, Pong, Arena) were authored on the old substrate, and each reported what it could not express or
what answered wrongly. Two were defects and are fixed on the new rules side
with laws: a computed key carried no reads of the expression behind it, so a
binding through a live zone kept serving the last zone's answer when the
selecting cell moved; and a declared binding landed with no ceiling check
after the keys its expression minted, so a rule compiled to seventeen bound
values for a buffer of sixteen and threw on its first evaluation. The old
substrate keeps both until it is deleted; Hearts is authored around them, so
its baseline does not depend on either.

The rest are limits, each with the package that owns it.

| Gap | Consequence | Owner |
|---|---|---|
| `setRay`'s `from` is a literal cell only; `transfer`, `clearEnclosed`, and `writeSet` take a live key | Reversi needs one flip rule per cell, 120 rules; Stratego's Scout cannot use it | WP12b, in the new compiler: `from` resolves a live key as its siblings do |
| `push` writes a literal value; `pushState` resolves a source | Hearts and Snake each need one rule per pushed value | WP12b: `push` takes the value source `pushState` takes |
| `$history:<row>:<age>` takes a constant age | Snake's tail clear is a chain over each possible length, which caps the snake at six segments | WP12b: the age is an expression |
| A search job's judge cost sums every judge rule with none of the mutual-exclusion folding the tick budget applies | Reversi sits at 14% of the tick budget and cannot carry a search row | WP11a with the search translation: the judge is priced through the same exclusion folding |
| A rule's name is a literal token; a template parameter in the name position lowers to the parameter's own identifier with no diagnostic | A compile-time loop cannot mint one rule per cell, and the silent form produces duplicate rule names | WP12b: refuse the silent form; decide whether a rule name interpolates as a placement's does |
| The decompiler's `table` and `pile` sugar reads `capacity` as an integer and drops one the lowering produced as a number | A row authored `row { capacity: N }` loses its capacity through decompile, which the CLI pair hides and the corpus gate does not | WP12b |
| `puck fmt` unindents a wrapped `export` facet's continuation lines, after which the parser reads the facet as empty | Formatting silently drops exports | WP12b |
| The baseline runner's steps issue `UpsertStateCell` only, which carries no phase guard | `phase` and `phaseOf` are authored and exported by Stratego and Hearts, and the generation advance is unproved | WP14: a runner step that submits a guarded transform |
| A rule's `bind` shares its name with the input system's bindings and chords, authored in the same sources | Authors and readers confused the two | LANDED: `bind` is `local` and `$bind:` is `$local:`, rewritten once with no alias ([decisions](../decisions/state-and-language.md#capacities)) |
| One entity is a dozen separately declared rows that share keys, and a family is hand-numbered | Nothing states that the rows are one thing, and sixteen players are sixteen copies or a convention | [S7](state-and-language.md#s7--records-and-pools): a record and a pool, after this plan lands |
| Knowledge is indexed by board cell, so a revealed piece that moves leaves its rank on the cell it was revealed on | Any hidden-information game inherits it | Knowledge becomes keyed by token: [G16](state-and-language.md#g16--baba-is-you) with [C3](state-and-language.md#c3--a-match-answers-where)'s match work, which is where a token row and a match position first meet |
| A Bool row refuses an expression as its write source, and an expression refuses to carry a Bool cell into an Int destination | Codenames keeps its live masks as Int and converts by hand | WP12b, in the new compiler: a comparison's Int is admitted into a Bool cell, and a Bool cell reads as 0 or 1 |
| The transpiler manual says an `sql { }` column can declare `TEXT EMBEDS (<table>)`; nothing implements it | Codenames authors its embedded tables natively | WP12b: implement it or delete the sentence |
| `nearest` and `remember` always took the cross-row path and the frame refused them, so no search judge could evaluate a vector game | The arena runs both wherever it runs (WP9), so the limit goes with the frame | WP11; Codenames' baseline is the check |
| A `properties` carrier tag is keyed by a literal body index, and an inhabited placement takes the highest free body slot, so a module's tags depend on its host's body capacity | Pong and Arena derive their keys from their fixture host | [S7](state-and-language.md#s7--records-and-pools): the literal index is replaced by a pool instance reference, so a tag names an instance and never a slot number |
| The baseline runner's `pose` step writes a zero yaw, and it has no `leave` step | A placed body loses its authored facing; leaving a live match is unproved | WP14, with the guarded-transform step |
| An Edge rule latches its gate, not an input crossing, so a gate that becomes true partway through a held press fires on that transition | An authoring hazard, stated in the rules chapter | WP13 |

Two findings are outside the state system and are recorded in
`docs/plans/open-items.md`: a rigid body spawned exactly touching a static
surface never establishes contact and free-falls while reporting grounded,
and a sphere-against-box pair rests much further apart than its half-extents
predict, so a Distance interaction authored at the geometric contact range
never latches.

## Verification summary

Run from the integration branch after every package, and from the squash
before landing:

```bash
dotnet build Puck.slnx -c Release
```

```bash
dotnet test tests/Puck.State.Rebuild.Corpus -c Release
```

```bash
puck architecture --check
```

```bash
puck landing --against origin/main --base <the commit the branch was authored from>
```

Suites per package are named in each package's **Check**. The inherited
`tests/Puck.World.Tests` failures recorded by WP0 are not attributable to this
plan and are tracked separately; a package that fixes one in passing says so.

---

[Plans](README.md) · [State and the authoring language](state-and-language.md) ·
[Decisions](../decisions/state-and-language.md) · [State and rules](../reference/state.md)
