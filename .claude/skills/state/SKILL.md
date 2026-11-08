---
name: state
description: "Maps Puck's state and rule engine (Puck.State, .Rules, .Search, .Topology, .Vectors, .Generators): arena, journal scopes, undo, hashing, rule compiler, faults, transforms, search, draws."
---

# State

A map of the engine beneath every world's `state`, `rules` and `search`
sections. It has two layers, and most mistakes come from building in the wrong
one:

- **The libraries.** `Puck.State` (model, catalog, `StateArena`, expressions),
  `Puck.State.Topology`, `.Generators` and `.Vectors` above it, `Puck.State.Rules`
  (compiler, evaluator, transforms, analysis) and `Puck.State.Search` on top.
  They know no bodies, cameras or networks; a host teaches them its concepts.
- **The World host.** `WorldRuleHost` (the facts, effects and decisions only a
  world has), `WorldArenaHost`, `WorldServer.PublishArena`, the disclosure
  views and `WorldAuthorityCheckpointCodec`, all in `Puck.World.Server` and
  `Puck.World.Schema`.

[State and rules](../../../docs/reference/state.md) is the human manual (twenty
pages under `docs/reference/state/`, organised by task);
[the state decisions](../../../docs/decisions/state-and-language.md) hold the
reasoning. This file names the one home of each capability and the contracts
that bite. When it and the code disagree, the code wins and this file is
corrected in the same change.

## Find the owner by what you need

| You need | Owner | Read |
|---|---|---|
| Declare rows, cells, kinds, domains, enums; compile names to handles | `StateRow`, `CellValue`, `StateCatalog` (handles and descriptors) | [data model](../../../docs/reference/state/data-model.md) |
| A value that refills, eases, cycles, caps or stays hidden from some readers | row traits on `StateRow` (`StateDynamics`, `StateCycle`, `StateVisibility`) | [traits](../../../docs/reference/state/traits.md) |
| Bounded sets of typed instances, claims, generations, pair pools | `StatePool` and the arena's pool storage (`StateArena.Pools.cs`) | [records and pools](../../../docs/reference/state/records-and-pools.md) |
| Store, read and write cells; nest savepoints; take a change back | `StateArena` with `ArenaJournal`: `StateArena.Commit`, `StateArena.Rewind` | [arena](../../../docs/reference/state/arena.md#journal-scopes) |
| Prove a row unchanged, or invalidate a cache derived from it | `StateArena.RowVersion` (content) versus `RowGeneration` (any mutation) | [row versions](../../../docs/reference/state/arena.md#row-versions-and-generations) |
| Hash the state; compose the world's hash | `StateArena.ComputeHash` (row digests cached by version; `RecomputeHash`, `CheckDigests`), composed by `WorldStateHashComposition` | [state hashing](../../../docs/reference/state/arena.md#state-hashing) |
| Save, restore, relayout; replay one scope while the world runs | arena export/import and snapshots (`ArenaSnapshot.cs`, `ArenaScopeSnapshot.cs`); `WorldScopedReplayRuntime` | [checkpoints](../../../docs/reference/state/arena.md#checkpoints), [scoped replay](../puck-world/references/replay.md#scoped-live-replays) |
| Boards: grids, hexes, graphs, tilings, rays, masks, cell sets | `CompiledTopology`, `BoardQueries`, `BoardMask` | [topologies](../../../docs/reference/state/topologies.md) |
| Match words over board cells ("does this hand form a run") | `CompiledPattern` (Topology), the `$match` operand | [patterns](../../../docs/reference/state/patterns.md) |
| Read state in an expression; fold a row; call a function | the expression IR (`ExpressionProgram`), one interpreter `RuleExpressions.Evaluate` over `ExpressionArithmetic`; spelling in `ExpressionSpelling` | [expressions](../../../docs/reference/state/expressions.md) |
| Compile a rule; read a compile refusal | `RuleCompiler` (partial per concern: gates, effects, operands, patterns, transforms, vectors, disclosure) | [rules](../../../docs/reference/state/rules.md#compile-rules) |
| Evaluate rules each tick; Level versus Edge; atomic firings | `RuleEvaluator`, `RuleLatch`, `EvaluationFrame` | [rules](../../../docs/reference/state/rules.md#follow-one-evaluation), [below](#faults-firings-and-the-one-door) |
| Shuffle, deal, sort, arrange, flip a ray, transfer tokens, observe | one kernel per case in `ArenaTransforms.TryApply`, resolved once by `RuleCompiler.TryResolveTransform` | [transforms](../../../docs/reference/state/transforms.md#one-implementation-for-every-caller) |
| A chain reaction, staged phases, take back a turn | rule groups (`RuleGroup.cs`, `RuleEvaluator.Groups.cs`) and arena undo (`ArenaUndo.cs`, served through `ArenaEffectHost`) | [rule groups](../../../docs/reference/state/rule-groups.md) |
| Reproducible dice, decks, generated text, Penrose patches, lookup tables | `GeneratorEngine`, `ArenaDraws`, `PenrosePatch`, `CompiledTable` (Generators); unbiased bounded draws come from `Puck.Maths` `BoundedRandomSampling` | [generators](../../../docs/reference/state/generators.md) |
| Vector rows: nearest, mix, mean, remember | `ArenaVectorTransforms` (Vectors) | [vectors](../../../docs/reference/state/vectors.md) |
| Legal moves, a computer opponent's best move, chance nodes | `ArenaSearch` over journal scopes, judged by `IArenaSearchJudge.Judge` (`RuleArenaSearchJudge`); world jobs are `search` rows (`WorldSearchSection`) | [search](../../../docs/reference/state/search.md) |
| Read and write sets, order-sensitive pairs, price a tick's worst case, skip rules whose inputs did not move | `RuleDataflow`, `RuleHazards`, `RuleWorkBudget`, `RuleSchedule` against `RuleCapacity.MaxWorkUnitsPerTick` | [analysis](../../../docs/reference/state/analysis.md) |
| A host fact, effect or row family only your host understands | facets and families registered on the compile context; `ArenaEffectHost` as the base host | [hosting](../../../docs/reference/state/hosting.md) |
| Decide what a recipient may read | `WorldRecipientView` over `WorldStateDisclosure`; audience enclosure in `StateArena.ReadAudienceEncloses` | [disclosure plan](../../../docs/plans/disclosure.md) |
| Every ceiling (cells, journal bytes, body slots, work units) | `StateCapacity`, `ArenaCapacity`, `RuleCapacity` | [limits](../../../docs/reference/state/limits.md) |

The world document's rule sugar (`rule`, `decision`, `forEach`, `schedule`,
`remove`) and how it lowers to state mutation kinds belong to `puck-world`
([mutations](../puck-world/references/mutations.md#rule-effect-sugar-puck--state-mutation-kind));
the `.puck` grammar to `puck-dsl`.

## Faults, firings and the one door

**A rule fault is in the value.** `RuleExpressions.Evaluate(ref frame, ...)` answers an `Outcome` (a number, an
absence, or a `Domain` or `Forever` fault) and nothing else carries it: `RuleEvaluation.Decide(ref frame, gate)` answers
a `Verdict` (`Faulted`, `Closed`, `Open`), `RuleEvaluator.EvaluateBindings` a `BindingsResult`, `IArenaSearchJudge.Judge`
a `JudgeOutcome` and `RuleEvaluator.Evaluate` a `RuleOutcome` (`Faulted` over `Refused` over `Fired` over `Idle`). The
deciding place reads what it was handed: a rule's judgement holds its latch on a fault, a group trigger neither runs nor
re-arms and a fixpoint pass counts no progress, a decision keeps its choice, a search step lands nothing and restarts. An
effect that meets a fault refuses its firing by name, so a firing that proposes is one no fault touched. Local code may
drop a value but never the fault: firing evaluations use `EvaluationFrame`; `RuleExpressions.EvaluateDetached`
enters the same interpreter only from the constant folder, a flock's affinity and browser read-back, as
`OutcomeDetachedAllowlist` specifies. Each consumer handles the cases of what it gets back.
`Absent` is a value each consumer answers, never a fault
([expressions](../../../docs/reference/state/expressions.md#constant-folding-and-faults)).

**An operand never evaluates an expression.** An operand read answers a `Fact` (an `Outcome`, a case of `Faulted`,
`Finite`, `Absent` or `Forever`, beside its cell kind) and an address resolves to a `Resolved<T>`; both default to the
faulted case and give their value only through `TryFinite` or `TryNamed`. An operand declares the addresses it reads
through (`IRuleOperand.Addresses`) and reads through what the interpreter resolved (`Read(reader, keys)`,
`RuleExpressions.ReadOperand`): an address a program computes (a `cards[<expression>]` key, a `$zones[<expression>]` row,
a ring age) is tokens of the enclosing program that `RuleCompiler`'s lowering puts in front of the operand's own, so a
computed address is priced as the tokens it is; a pattern's per-token program is `IWordFoldOperand.WordProgram`, run
once per token by `RuleExpressions.FoldWord`, which `world.match` spells its word through as well. A new operand that
reads through a computed address declares it in `Addresses` and receives it in `keys`; it never calls
`RuleExpressions.Evaluate`.

**A firing is one journal scope.** `RuleEvaluator.FireProposal` (a rule) and `FireEffects` (a host's decision effects)
open it, write the edge bit through `RuleLatch.SetHeld` (and a host's own state through an `IFiringPrelude`), propose the
effects, and close it through the one door, `RuleEvaluator.Commit`, which switches on a `FiringDecision` (`Faulted` and
`Refused` rewind, `Closed` and `Held` commit the bookkeeping alone, `Fires` commits and delivers). A store that is
simulation state beside the arena's columns implements `IJournalParticipant` and records what it overwrites through
`ArenaJournal.RecordParticipant` before writing (it throws with no scope open); `StateArena.Rewind` hands the records
back newest first. `RuleLatch`'s lifecycle writers (`Restore`, `Clear`, `ClearRule`, `Prune`) take the arena's journal,
no default, and refuse inside a scope, since only a firing's writes are recorded; `EndSweep`, `Forget` and `Reset`
record what they remove when a scope is open. A decision's common and option effects are one firing: `FireEffects`
proposes the common effects, then the `IFiringContinuation`'s effects into the same scope, and the `IFiringPrelude`'s
binding rewinds with both; a decision's no-choice effects are one firing with the binding they clear the same way. The
continuation is handed the arms the common effects queued and skips the option when the population a reconcile of the
firing's proposal produces no longer holds its pair: the queued document rows compose against the proposed document,
and `WorldPopulation.ReconcileKeeps` runs the install's own reconcile (structural when
`WorldDocument.RebuildsPopulation` says the unit rebuilds the population, then the cell-driven counts from the open
arena, then the census re-clamp) over a scratch slot table. The reconcile is written once, generic over the slot table,
so a new retire rule in it reaches the forecast. A decision reads every gate and score before it writes its binding, so
a fault writes nothing. A queued arm keeps the host bindings it was queued under (`IEffectHost.CaptureBinding`,
`ApplyBinding`) for its preflight and delivery.

**Effects that leave the arena declare their sources.** An arm that fires outside the arena evaluates nothing itself:
it declares `IRuleEffect.Sources` (a key read as an index, an expression program, an operand) and
`RuleEvaluator.ProposeEffects` reads them during queued preflight, after the reversible effects and continuation, so a
source that faults is a `Faulted` firing and an absence is a value the arm answers. The values reach the arm through
`EffectFiring.Values`, by the position it declared them at, and are the same at the preflight and at the delivery
after the commit. A write's destination key answers a typed `WriteKey` (`Resolved`, `Faulted`, `Refused`), never a
reason to compare. The state transaction's input resynchronisation is two phases: `ValidateStateTransaction` prepares
each owner's selection (`WorldInputRuntime.TryPrepareBodies` and `TryPrepareAll`) while the journal can still rewind,
and `CommitStateTransaction` applies them (`ApplyPrepared*`), which cannot refuse.

`OutcomeAnalyzer` in `Puck.Analyzers` enforces `OUTCOME001`–`OUTCOME004` as Roslyn build errors outside xUnit v3
assemblies: dropped outcomes, unchecked `TryFinite`/`TryNamed` and one-case verdict comparisons, unlisted detached
evaluation, and an operand resolving its own address. Holding declared addresses for read sets and costs is allowed.
`OutcomeDiscardAllowlist` names the host bool entries; the analyzer cannot prove a stored bool is read or an explicit
fault arm is correct. See [the outcome and host contract](../../../docs/reference/state/hosting.md#outcomes-analyzer)
and [the behavior examples](../../../docs/reference/state/rules.md#rule-fault-examples). The
[remaining work](../../../docs/plans/rule-faults.md) includes body-target generations, proposal-owned host scratch
and the source-read timing choice; these are not guarantees the current implementation makes.

## Contracts that bite

- **Determinism.** Values are integers and Q48.16 (`maths-usage`); no wall
  clock reaches a rule. A draw is a site with a persisted cursor, never an
  ambient generator. A deliberate correction moves state hashes; re-record
  what it invalidates (AGENTS.md rule 4).
- **One transform kernel per case.** A rule firing, a search judge, the
  console and a host command reach the same `ArenaTransforms.TryApply` inside
  the caller's scope. A second shuffle or deal written in a host is a
  duplicate, not a feature.
- **Writes outside a scope cannot be rewound**, and a relayout cannot run
  while a scope is open. Open a scope before any change you may need to take
  back.
- **Addressing shortcuts change only the path, never the value.** Tick-path
  callers read by ordinal and interned key through one entry point; name-keyed
  entry points serve read-backs and validators
  ([the rules](../../../docs/reference/state/arena.md#rules-for-addressing-shortcuts)).
- **A row version moves only when committed bytes differ.** A cache keyed by
  it is invalidated by content; a cache over storage uses the generation. A
  write that changes bytes without moving the version leaves a stale digest,
  which `StateArena.RecomputeHash` against `ComputeHash` (every tick under
  `puck test --reproduce`) and `CheckDigests` sampling catch.
- **Bounded before it runs.** Every row, pool, pattern and search has a
  ceiling checked at compile or load, and a world whose rules could exceed
  `RuleCapacity.MaxWorkUnitsPerTick` is refused at load. When merged content
  hits a ceiling, raise the engine constant rather than cutting content
  ([the rule](../../../docs/reference/state/analysis.md#when-merged-content-collides-with-a-capacity)).
- **Disclosure is checked at every read.** Every untrusted read and delivery
  asks the recipient view again; caching a bound address never caches
  permission.

## Where each contract is enforced

| Contract | Enforced by |
|---|---|
| A fault is never dropped; an operand never resolves its own address | `OutcomeAnalyzer` (`OUTCOME001`–`OUTCOME004`), build errors |
| A firing's writes land together or not at all | the scope `RuleEvaluator.FireProposal`/`FireEffects` open and `RuleEvaluator.Commit` closes |
| Side state rewinds with the arena | `IJournalParticipant`, recorded through `ArenaJournal.RecordParticipant`, which throws with no scope open |
| Latch lifecycle writes stay outside firings | `RuleLatch` writers take the journal and refuse inside a scope |
| A transform reads no names while it runs | `RuleCompiler.TryResolveTransform` resolves to an `ArenaTransform` of ordinals |
| A rule fits its host | `RuleNeeds`, read off the rule's own declarations by `RuleNeedsBuilder` and admitted against the host's facets; a rule the host cannot serve is refused before evaluation |
| Worst-case work fits a tick | `RuleWorkBudget` against `RuleCapacity.MaxWorkUnitsPerTick`, at load |
| A read audience encloses the destination's | `StateArena.ReadAudienceEncloses`, called by search and the disclosure transforms |
| Same input, same state | row digests in `StateArena.ComputeHash`; `puck test --reproduce` compares two runs and recomputes every tick |

## Open gaps

- **Three disclosure models.** `WorldRecipientView` is the new one;
  `WorldStateDisclosure` statics and `WorldStateReadView` remain until the
  plan's step 6; bodies are disclosed spatially by `WorldObserverDisclosure`
  beside both ([disclosure plan](../../../docs/plans/disclosure.md)).
- **Body action gates are a second gate interpreter.** `WorldBody.GateOpen`
  folds `CompiledPredicate[]` with its own loop beside `RuleEvaluation.Decide`,
  sharing only `GateProgramEvaluator`. Merging them is a design step for the
  physics and body lane ("Puck code over engine code": body facts are operands
  a rule gate could read).
- **The draw-seed ladder folds strings its own way.** `GeneratorEngine`'s
  private `FoldDelimited` folds a 64-bit length where `Fnv1aHash.Add` folds a
  32-bit one; merging re-rolls every authored draw stream, so it belongs in a
  change that looks at the debug world afterwards.
- Rule-fault follow-ups are in [the rule-faults plan](../../../docs/plans/rule-faults.md),
  evaluation cost in [the rule-evaluation-cost plan](../../../docs/plans/rule-evaluation-cost.md).

## Verify

- Library laws, headless and GPU-free, one suite per project:
  `tests/Puck.State.Tests`, `tests/Puck.State.Rules.Tests`,
  `tests/Puck.State.Topology.Tests`, `tests/Puck.State.Generators.Tests`,
  `tests/Puck.State.Vectors.Tests`, `tests/Puck.State.Search.Tests`. Several
  also check the tick path allocates nothing once warm.
- What authors wrote keeps its meaning: `tests/Puck.State.Rebuild.Corpus`
  compiles, decompiles and recompiles every shipped `.puck` source.
- A world's own verdicts: `puck test worlds/debug/<name>-tests.puck --jobs 1`,
  with `--reproduce` for anything that reaches the hash. `rule-faults-tests`,
  `unbiased-draws-tests`, `snapshot-partitions-tests` and the board-game
  scenarios (`chess-tests`, `go-tests`, `reversi-tests`) exercise this area.
- Live: `world.state`, `world.why` and the rule trace over stdin
  ([verify state changes](../../../docs/reference/state/testing.md) maps each
  change to its check; its canaries table names checks quarantined under
  `experimental/`).

## Look-alikes

- `world.match` and the document `patterns` section match words over board
  cells; `inputPatterns` match timed input history (`input`).
- Operator document undo and history seek (`WorldPersistence.ApplyUndo`,
  `WorldHistory`) are not the arena's turn undo; the replay tape
  (`puck-world` replay) is not a scoped replay image.
- `ArenaTransforms.TryShuffle` draws through a refusable draw site;
  `Pcg32XshRr.Shuffle` draws unrefusably. They are different source
  contracts, not duplicates.

## Route adjacent work

| Skill | Route there for |
|---|---|
| `puck-world` | Rule sugar and its lowering, the tick order, grants and refusals, `world.state` and other verbs, booting and running a World. |
| `puck-dsl` | `.puck` grammar for `state`, `rules` and `search`, the compile and lint verbs, PUCKnnn diagnostics. |
| `maths-usage` | Fixed-point types, seeded streams and hashing primitives a state value or draw uses. |
| `physics` | Body facts a rule reads (`physics(quiescent)`), field lattices behind `field` rows, the body action gates. |
| `input` | `inputMatch`, `consumeInput` and the input operands a rule reads. |
| `verification` | Leases, the private CLI copy, flaky versus failing scenarios. |
