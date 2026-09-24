# State system rebuild: the landing

`Puck.State` runs on one storage substrate, one cell representation, typed host
capabilities, and atomic rule firing, while every authored spelling keeps its
meaning. The design is in
[the decisions register](../decisions/state-and-language.md#the-rebuild); the
programme it belongs to is [State and the authoring language](state-and-language.md).
This page keeps what later packages still use from the rebuild: how a package
is run and reviewed, what the landing settled about where code lives, the
candidates adopted into later packages, and the gaps the acceptance games
found, each with the package that owns it.

## Implementation status

The rebuild is landed on `main`, and every work package on this page is
closed: waves A, B, and C, the switch wave, the flip, WP11b to WP11e, the
costing evidence, S1, S2, the dashboard schema-id sweep, the sweep, WP8's
relocation, WP13, and WP14. The old substrate is deleted, and `state/rebuild`
and its integration branch are retired. What stays open is in
[the gaps the acceptance games found](#gaps-the-acceptance-games-found).

## What the landing settled

The old substrate is gone: the frame, the store, the old search runtime, and
the old rule stack that sat in `Puck.State` beside its live twin in
`Puck.State.Rules`. `StateCell` keeps its key and traits and carries one
`CellValue`; a cell whose kind differs from its row's is refused by name at the
arena's import.

A cell a rule writes reaches a client: the arena-export install marks state
delivery pending, which is the same door a console write's install marks, and
a law reads the value back off a real `WorldClient`'s own definition. The
capture scope folds each authored cell's clock after its value, so two worlds
differing only in a follower's sampled position and velocity carry different
capture digests.

A search takes a fold of every input row's content only when some input row's
version has moved, not on every tick. The stamp a job compares is still the
fold of content, so a restored world continues as the uninterrupted one does.

Four declarations stay in `Puck.State`, each with a live caller:
`RuleRefusal` and `RuleEffectRefusal`, because `Puck.State.Vectors` raises
refusals and cannot reach `Puck.State.Rules` (Rules depends on Vectors), so
both are declared once there, under the `state.rule.compile` and
`state.rule.fire` doors; `VectorTransforms`, the vector arithmetic kernel
`ArenaVectorTransforms` calls; `GateProgramEvaluator`, the Boolean stack fold a
rule gate and a body action gate share; and `RuleFacts`, whose prefixes the
live compiler, the cartridge expressions and the expression speller all read.

`Puck.State.Topology` and `Puck.State.Generators` each depend only on
`Puck.State` and `Puck.Maths`. A type stays in `Puck.State` whenever a core
file — `ArenaLayout`, `StateArena`, or `StateRow` themselves, not a
document-authoring convenience — names it directly, so moving it would put a
core reference on the project that would own it:

- `LatticeTopology`, `CompiledTopology`, `TopologyCompilation` and
  `TilingGenerator`: `ArenaRowLayout.Topology` and the arena's import and
  export doors read a `CompiledTopology` while laying out or walking
  `StateArena`'s columns.
- `Draw` and `StateGenerator`: `StateRow.Draw` types on `Draw` directly, and
  `StateGenerator.MaskCount`/`Exhausts` sit on the declaration itself so the
  arena's layout compiler needs no `Puck.State.Generators` reference to size a
  draw site's mask.
- `PatternNode` and `PatternCapacity`: core's `.puck` infix front end,
  `PatternSpelling`, parses and prints `PatternNode` directly.
- `BoardMask`: `CompiledTopology`'s own shift-mask precompute reads
  `BoardMask.MaxCells`.
- `PenroseRhomb` and `PenroseTiles`: `TilingGenerator` returns them, so they
  need `CompiledTopology`'s own visibility.
- `StateVector` and `StateSpace`: the document JSON converter and
  `StateSpelling` call both directly for every vector cell's wire round-trip.
  Narrowing that to an interface seam is a deliberate redesign no later
  package has taken up.

`PenrosePatch` takes its drawn start tile as a value, so topology never
references generators. A `match` operand reads its word into a caller's buffer
through `StateArena.ReadWord` or `ArenaBoards.TryReadRay` at every evaluation
and walks it from the first letter; nothing carries over between reads.

Trigger admission is `RuleNeeds.Admit`, and its law is
`ReaderFacetLawTests.AdmittingARuleWhoseFacetTheHostLacksRefusesWithTheFacetName`
in `tests/Puck.State.Tests` — a needs set built over a test-declared facet
`interface : IFacet`, refused by facet name against a reader that does not
advertise it, with `AdmittingARuleWhoseFacetsTheHostServesSucceedsWithNoRefusal`
as its control. The reachable half of group admission is pinned on the hosts:
a group the browser refuses to compile runs neither itself nor the rules it
claims (`BrowserRuleGroupLawTests`), and the server refuses such a document
outright (`RuleGroupServerLawTests`). Each host's own trigger-admission arm is
unlawed and unreachable by any document until a second facet exists —
`IWorldFacts` is the only declared facet and both hosts serve it — so the
mechanism's law is where the refusal is pinned.
`WorldRuleHost.AdmitRules`/`AdmitGroups` are private and called only from
`WorldRuleHost.Install`, and the browser's loop is inside the private
`BrowserSession.Install`.

The facade split's members are as narrow as what reaches them. The
board-enforcement latch and the decision runtime each serve persistence through
one `IWorldPersistedSection` seam — append-hash, capture, restore, plus the
decision runtime's own checkpoint validation — implemented explicitly, so
`WorldPersistence` holds two private seam properties rather than reaching into
public members. The possessed-inhabitant walk is the tick's alone and owns its
list, so no buffer is aliased across facades. Everything a facade exposes only
to the rest of its own assembly is `internal`, and a member nothing outside its
declaring type reads is private.

`RuleArenaFixture` is one arena over the catalog its rules compiled against,
loaded per position through `StateArena.TryLoad`, with `RuleLatch.Reset`
before each judge, because the shipped tabletop judges are `Edge` rules and a
latch shared across positions judges the first alone.

## Work packages

A package lists **Owns**, **Deliverables**, **Check** (the command and the
observable pass condition), and **Hand-off**, and names the skills to load
before starting. Every package below is closed and keeps its status. One
integrator serializes edits to project references,
`build/Architecture.props`, the generated project map, shared fact bases, and
World ingress and serialization.

### Hand-off discipline

Before switching consumers, retain the input scripts and exports from before
the change as immutable comparison inputs. Each package hands off its actual
public seams, refusals, ownership/lifetime rules, focused law cases, commands
and outcomes, and remaining blocked checks. A prepared package is not a
verified package; missing browser or runtime evidence remains explicitly open.

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
branch once, after everything has landed on it and before it is squashed to
`main`, rather than each commit or each wave as it lands.

Nothing has shipped, so no package spends effort preserving or explaining old
behavior. A test that pins behavior the new design changes on purpose is
re-pinned or deleted with its subject. A law whose subject is deleted goes
with it; a pure old-versus-new differential is deleted; a law whose subject
survives is ported, never deleted; and a law that mixes the two is trimmed to
the half that survives. A package's laws for new behavior are few and pointed.
What every package still holds to is the engine's own contract: the build and
architecture gates, determinism, the state, rules, and search law suites, and
a world that boots.

Operating rules every package and review follows, learned the expensive way:

- One worktree per package, branched from `main`
  (`git worktree add .claude/worktrees/<name> -b <branch> main`);
  every Read, Edit and shell command uses the worktree's absolute path. Never
  `git stash`. A branch takes its base by merge, never rebase, so reviewed
  commits keep their hashes.
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
- Run `puck` from a copy of the CLI's Release build output: a running
  `puck` locks its own bin, and the `Puck.World` build copies into it.
  The published `puck.exe` under the CLI's `publish` directory and the global
  `puck` tool are whatever commit last published them; the portal's node suite
  calls the former, so republish it
  (`dotnet publish src/Puck.Cli -c Release -o src/Puck.Cli/publish`) before
  trusting a portal failure.
- A law that enumerates repository sources reads `git ls-files`, never the
  filesystem: a checkout holds other worktrees and scratch under it.
- `puck format --check` over `src` runs longer than a package can wait on it.
  CI's formatting bot is the backstop; do not block a package on it.
- A reviewer works in its own worktree at the branch head, reads every hunk
  and its enclosing member, reverts and reruns every claimed law, and writes
  blockers with file, line, failure scenario and evidence; blockers go back to
  the implementer by message and land as their own commits with laws.

### WP11b — The remainder

Skills: `puck-world`, `symbol-analysis`.

**Status:** closed. One refusal registry is anchored on `Puck.State` and
`Puck.State.Rules`, so `world.refusals` lists every door; the server compiles
and runs `ruleGroups`, with a refused group's members running on neither host
and a group's trigger admitted against the host that runs it; every edge
consumer reads one `CellValue` and switches exhaustively on `CellKind`, the
browser `[JSExport]` payloads and their TypeScript types with it; a load is a
birth, so a timed cell turns from the tick it was installed at.

### WP11c — The compose onto the arena

Skills: `puck-world`, `symbol-analysis`.

**Status:** closed, with one deliverable in another shape. A batch holds one
arena (`WorldDocument.Compose.Batch.cs`): it loads once, applies every member
through the arena's own kernels in the order the batch authors them, exports
once, and rewinds on refusal, and a same-batch row declaration ahead of a write
is what the write sees. The arena's admission is the compose's admission:
envelope, reserved cells, eviction and clock settling are enforced there and
nowhere else. The arena recomputes a derived board on a keyed write,
`WorldDefinitionValidator.Discrete.cs` checks against that recompute through
`BoardDerivation`, and `RuleArenaFixture`'s consumers read the arena. A cell
token parses through `CellValue.TryParse`. `BatchComposeLawTests` pins the
compose refusal text.

The deliverable in another shape: `WorldStateReader` was to read the arena at
an `ArenaTime` the caller names. It is the document-anchored door to
`StateReader` instead, whose entry points (`Reduce`, the three `ArgExtremum`
overloads, and the `TryRead` family) read a document's rows at a tick and an
engine tick the caller names.

### WP11d — Body action state onto the participant lane

Skills: `puck-world`.

**Status:** closed. Body action state lives in the arena's slot lanes, sized
by `WorldSlotLanes.Options` on both hosts: a body binds its lane through
`WorldBody.BindActionStateLane`, and the checkpoint and federation codecs
carry it from there.

### WP11e — The facade split

Skills: `puck-world`, `boy-scout`.

**Status:** closed. `WorldDocument`, `WorldTick`, `WorldRuleHost`,
`WorldExtensions`, `WorldGrants` and `WorldPersistence` are facades out of
`WorldServer`, which keeps its public API, constructor and phase order; bodies
stay behind `IWorldGrantsView`. `SweepDeadlines` runs escrows, transfer
escrows, contribution tenure and parks through `WorldDeadlineTable` once per
tick; placement deals, responses and reflow reviews are reactive comparisons
and stay with their triggers.

### The sweep

**Status:** closed. The old substrate is deleted, with the nine items the
WP11b to WP11e reviews deferred to it, each its own commit with a law. What the
sweep settled is [above](#what-the-landing-settled).

### WP8 — The relocation, and WP9b's remainder

Skills: `maths-usage`, `symbol-analysis`.

**Status:** closed. `Puck.State.Topology` holds
`BoardQuery`/`BoardQueries`/`BoardCombination`, `CompiledPattern`/`PatternRow`
and `ArenaBoards`; `Puck.State.Generators` holds `GeneratorEngine`/`PrivateDraw`,
`TableDocument`/`CompiledTable`, `ArenaDraws` and `PenrosePatch`; each has its
own law suite, `Puck.State.Topology.Tests` and `Puck.State.Generators.Tests`.
Which types stayed in `Puck.State`, and why, is
[above](#what-the-landing-settled).

### WP13 — Documentation and skills

Skills: `documentation`, `boy-scout`.

**Status:** closed. The state chapters under `docs/reference/state` describe
the arena, `CellValue`, facets, atomic firing, groups, families, the pattern
algebra, and the six projects; `docs/project-map.md` has a row for each of the
six; no chapter says "frame subset".

### WP14 — Landing

Skills: `boy-scout`, `puck-world`.

**Status:** closed. The landing bar it held, which a later landing keeps:

1. A moved export is re-recorded, not explained: nothing consumes these worlds
   (rules 4 and 5), so the bar is that the recorder's double replay agrees,
   the world boots, and its scripted sequence runs through without an
   unhandled exception.
2. Tick cost per shipped world is re-recorded exact and machine-independent,
   with one line per world explaining the change.
3. `puck references` on every type the plan deletes returns nothing.
4. `puck landing` and `puck parity` are green, the full automatic canary set
   runs with no concurrent build or test on the machine, and the integration
   branch is squashed to `main` with a hand-written summary (rule 6).

Re-deriving `RuleCapacity.MaxWorkUnitsPerTick`, and every other ceiling, is
[C1](state-and-language.md#c1--ceilings-as-prices), so one package owns every
limit.

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

The first seven acceptance games (Reversi, Hidden ranks, Hearts, Snake,
Word spy, Paddleball, Arena) each reported what they could not express or what
answered wrongly. Two were defects and are fixed on the rules side with laws: a
computed key carries the reads of the expression behind it, so a binding
through a live zone follows the selecting cell when it moves; and a declared
binding is checked against the ceiling after the keys its expression mints, so
a rule cannot bind more values than its buffer holds.

The rest are limits, each with the package that owns it.

| Gap | Consequence | Owner |
|---|---|---|
| `setRay`'s `from` is a literal cell only; `transfer`, `clearEnclosed`, and `writeSet` take a live key | Reversi needs one flip rule per cell, 120 rules; Hidden ranks' Scout cannot use it | LANDED: `from` resolves a live key as its siblings do, and Reversi's flips are two rules gated per direction |
| `push` writes a literal value; `pushState` resolves a source | Hearts and Snake each need one rule per pushed value | LANDED: the `push` transform is retired; `pushState` (`push row = value`) is the one ring append, and it takes every value source |
| `history(row, age)` takes a constant age | Snake's tail clear is a chain over each possible length, which caps the snake at six segments | LANDED in the compiler: the age is an expression, and an age outside the ring reads the empty value (`HistoryAgeLawTests`). The shipped Snake source still reads four fixed ages and caps its length at six |
| A search job's judge cost sums every judge rule with none of the mutual-exclusion folding the tick budget applies | A rule set whose rules exclude one another by gate is priced as though every rule fired, so its judge can refuse to plan | LANDED: `JudgeCost` tallies through the same sheet the tick budget uses, exclusion folding included (`WorldSearchJudgeCostLawTests`) |
| A rule's name is a literal token; a template parameter in the name position lowers to the parameter's own identifier with no diagnostic | A compile-time loop cannot mint one rule per cell, and the silent form produces duplicate rule names | LANDED: a rule named after a live template parameter is refused (`RuleNameNotLiteral`), and a rule name interpolates (`rule $"…"`) |
| The decompiler's `table` and `pile` sugar reads `capacity` as an integer and drops one the lowering produced as a number | A row authored `row { capacity: N }` loses its capacity through decompile, which the CLI pair hides and the corpus gate does not | LANDED: the sugar reads `capacity` as any integral number |
| `puck format` unindents a wrapped `export` facet's continuation lines, after which the parser reads the facet as empty | Formatting silently drops exports | LANDED: `puck format` is parse-then-print through `PuckPrinter`, which writes an `export` on one line |
| The baseline runner's steps issue `UpsertStateCell` only, which carries no phase guard | `phase` and `phaseOf` are authored and exported by Hidden ranks and Hearts, and the generation advance is unproved | OPEN, [S8](state-and-language.md#s8--tests-are-worlds): a `schedule` step submits a guarded transform (`world.state.act`), but no test in either game submits one |
| A rule's `bind` shares its name with the input system's bindings and chords, authored in the same sources | Authors and readers confused the two | LANDED: a rule's working value is a `local`, read by its bare name ([decisions](../decisions/state-and-language.md#capacities)) |
| One entity is a dozen separately declared rows that share keys, and a family is hand-numbered | Nothing states that the rows are one thing, and sixteen players are sixteen copies or a convention | LANDED: S7 records and pools, with timed record fields; Paddleball and Arena keep their entities as records ([S7](state-and-language.md#s7--records-and-pools)) |
| Knowledge is indexed by board cell, so a revealed piece that moves leaves its rank on the cell it was revealed on | Any hidden-information game inherits it | LANDED: C3 observations can key knowledge by token and resolve visibility through a token-position row; Hidden ranks uses that projection ([C3](state-and-language.md#c3--a-match-answers-where)) |
| A Bool row refuses an expression as its write source, and an expression refuses to carry a Bool cell into an Int destination | Word spy keeps its live masks as Int and converts by hand | LANDED: an expression written to a Bool row stores 0 or 1, and a Bool cell reads as 0 or 1 (`BoolExpressionKindLawTests`); Word spy declares its masks as Bool |
| The transpiler manual says an `sql { }` column can declare `TEXT EMBEDS (<table>)`; nothing implements it | Word spy authors its embedded tables natively | LANDED: the manual says there is no SQL spelling for `embeds` |
| `nearest` and `remember` always took the cross-row path and the frame refused them, so no search judge could evaluate a vector game | No search judge could plan a vector game | LANDED: a search judge runs the same evaluator as the live host, which fires both. No shipped world has a search judge that uses them |
| A `properties` carrier tag is keyed by a literal body index, and an inhabited placement takes the highest free body slot, so a module's tags depend on its host's body capacity | Paddleball and Arena derive their keys from their fixture host | LANDED: `properties.carriers` maps a pool's enum field to a named placement, a seat, or a detached binding, resolved at run time (`WorldPoolCarrierLawTests`); Paddleball and Arena use it ([S7](state-and-language.md#s7--records-and-pools)) |
| The baseline runner's `pose` step writes a zero yaw, and it has no `leave` step | A placed body loses its authored facing; leaving a live match is unproved | OPEN, [S8](state-and-language.md#s8--tests-are-worlds): a `schedule` step submits `player.leave`, but no Arena test uses it, and the runner still poses at zero yaw |
| An Edge rule latches its gate, not an input crossing, so a gate that becomes true partway through a held press fires on that transition | An authoring hazard | LANDED: [the rules chapter](../reference/state/rules.md) states the hazard |

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

A package names its own suites in its **Check**.

---

[Plans](README.md) · [State and the authoring language](state-and-language.md) ·
[Decisions](../decisions/state-and-language.md) · [State and rules](../reference/state.md)
