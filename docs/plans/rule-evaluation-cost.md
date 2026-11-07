# Rule evaluation proportional to change

The design memo for the cost of authored rules in the tick: where the debug
world's rule work went, measured per mechanism and per authored rule; what
incremental, dataflow and ECS systems do to make evaluation proportional to
change and to run independent work in parallel deterministically; the approach
taken; and what it leaves open. Versioned facts, the push-based due list, the
measured sheet and deadline wakes have landed, and the [state reference's
scheduling section](../reference/state/analysis.md#skip-rules-whose-inputs-havent-changed)
owns their contract. The [job-system memo](engine-job-system.md) keeps rule
evaluation serial.

The frame the owner set (AGENTS.md): Puck code over engine code, so the engine
accelerates authored rules generically and never hand-writes one in C#;
ergonomic is optimal, so the sweep an author writes costs no more than the
hand-unrolled form; and determinism, so the same document and input give
bit-identical state at any thread count.

## The problem

This section is the starting point the design answers. Every tick,
`WorldRuleHost.EvaluateWorldRules` evaluated every compiled rule in document
order (the ungrouped array, then the groups, then the interactions), and the
document order is the semantics: a committed firing is visible to the next
rule's gate
([rules](../reference/state/rules.md#ordering-and-visibility-of-earlier-writes)).
The evaluator skipped a rule whose gate closed last time when a row version
proved every row it read unchanged, and memoized locals the same way. That
skip was pull-based: every rule, and every binding of every sweep, was visited
every tick to ask whether it might be skipped, and a rule whose schedule was
volatile (it read `$tick`, `$search:ply`, any host facet such as
`bodyState(...)` or `bodyPosition(...)`, a host-owned row, or a row with a
value-over-time trait, effects included) was never skipped at all.

### The measurement

The headless debug World (`worlds/debug`, `--devices none`, 30 Hz) on the lead
machine while other lanes built and ran, so every reading below is a count or a
share of samples, never a wall-clock claim. Two instruments:

- A 20 s sampled-thread-time trace (`dotnet-trace`, the frame thread's stacks
  aggregated by inclusive share of the thread's time).
- A scratch build with a per-rule counter in `RuleEvaluator` (evaluations,
  skips, firings and a stopwatch around `EvaluateRule`, with each rule's
  `RuleSchedule` volatility), dumped every 900 ticks and read over 1,800 ticks.
  The two windows (0 to 900, 900 to 1,800) scale linearly, so boot transients
  do not shape the numbers. The counter was a prototype; the measured
  sheet (`world.budget.rules --spent`) is its deterministic half.

What the tick is made of (share of the frame thread's time; `StepCore` is
47.5 % of the thread, the rest the pacer wait):

| Phase | Share of the thread | Share of the tick |
|---|---|---|
| `WorldRuleHost.EvaluateWorldRules` | 17.5 % | 37 % |
| `WorldPopulation.AdvanceSimulated` (body advance, motion programs, contacts) | 13.0 % | 27 % |
| `WorldMachineHost.Advance` (of which 6.1 % is the frame thread's monitor wait on the machine worker's queue, under `QueuedMachineHost.Submit`) | 6.4 % | 13 % |
| `WorldInstanceHost.StepInstances` (the garden and night-field instances) | 5.3 % | 11 % |
| Addon read staging, input admission, contacts, snapshot, hashing | the remainder | 12 % |

The rule share is the largest phase of the tick. Inside it (17.0 % in
`RuleEvaluator.EvaluateRule`):

| Mechanism | Share of the thread | Note |
|---|---|---|
| Gate (`RuleEvaluator.Decide` → `RuleEvaluation.Decide`) | 2.8 % | authored |
| Locals (`EvaluateBindings` → `RuleExpressions.Run`) | 2.0 % | authored |
| Firing (`FireEffects`) | 2.3 % | authored |
| Sweep key snapshot (`EachKeys`, of it `BodyKeys` 1.8 %, of it a string-keyed `TryResolve` per live body 1.6 %) | 2.3 % | machinery |
| Latch bookkeeping (`RuleState.Binding` 1.1 %, `RuleLatch.Rule` name hash 0.5 %, `EndSweep` 0.5 %, the `Held` lookup 0.6 %) | 2.7 % | machinery |
| Version capture and match (`VersionsMatch`, `CaptureVersions`) | 0.3 % | machinery |
| The rest of `EvaluateOnceCore` and `EvaluateRule` (fault scopes, trace arming, outcome folding, counters) | 4.6 % | machinery |

So of the rule phase, about 42 % is the authored programs (gate, locals,
effects) and about 58 % is the machinery that visits them.

Per tick, from the counters (`world.counters state` over a 300-tick window and
the per-rule dump): 1,423 rules; 6,134 evaluations, of which 1,633 are skips
(27 %) and 96 are firings; 666 of the 1,423 rules are volatile, and they
account for 71 % of the evaluations and 76 % of the stopwatch time. The
versioned rules' evaluations are skips 90 % of the time, and a skip is not free:
`go-sync-mark` skipped 649,439 of its 649,800 bindings in the window and still
cost 0.28 µs per binding, so the 1,633 skips a tick cost about 0.45 ms. The
costliest authored rules a tick:

| Rule | Shape | Schedule | Evaluations a tick | Firings a tick | Share of rule time |
|---|---|---|---|---|---|
| `go-read-stones` (`go.puck`) | `forEach: goCode` | volatile (`search(ply)`), 6 rows | 361 | 0 | 10 % |
| `noclip-form` (`noclip.puck`) | `forEach: $bodies` | volatile (`bodyState` facets), 0 rows | 558 | 0 | 9 % |
| `noclip-flight` (`noclip.puck`) | `forEach: $bodies` | volatile, 0 rows | 558 | 0 | 4.5 % |
| `fall-recovery` (`valley.puck`) | `forEach: $bodies` | volatile (`bodyPosition`), 0 rows | 558 | 0 | 4.3 % |
| `klondike-seat-card` (`cardtable.puck`) | `forEach: kSlotOf`, Level | versioned, 5 rows; gate open | 52 | 52 | 3.8 % |
| `companion-senses` | once, Level | volatile, 3 rows; gate open | 1 | 1 | 3.3 % |
| `go-sync`, `go-put-pose` | `forEach: goCode` | volatile | 361 each | 0 | 2.7 %, 2.5 % |
| `go-sync-mark`, `go-sync-keep` | `forEach: goCode` | versioned, 99.9 % skipped | 361 each | 0 | 2.6 %, 2.4 % |
| the `reversi-*` sweeps (6 rules) | `forEach` over 64 cells | volatile | 64 each | ~0 | 6 % together |
| about twenty once-evaluated Level rules whose gate is always open (`*-seat-presses`, `chess-play-edges`, `reversi-moves-*`, `*-move-ready`) | once | mixed | 1 each | 1 each | 8 % together |

Three facts decide the design:

1. **Facets, not rows, make rules volatile.** The body sweeps read only facets
   (`bodyState`, `bodyPosition`, `fact`) and evaluate all 558 live bodies every
   tick without firing once. No row version can see a facet, so the skip can
   never apply to the rules that cost the most. The host owns those facts and
   knows when each changes; the evaluator cannot ask.
2. **The pull is the cost.** Visiting 1,423 rules and 6,134 bindings to find
   96 firings spends more on dictionaries, key snapshots and fault scopes than
   on the authored programs. A rule nothing it reads has changed should not be
   visited at all.
3. **Some cost is authored and must stay visible.** A Level rule whose gate is
   always open fires every tick by design (`klondike-seat-card` writes
   `kSeat.card` every tick; the `*-seat-presses` rules read channels every
   tick). The engine must not elide these; it must show the author what they
   cost, so the author can choose Edge or a narrower gate.

## What incremental, dataflow and ECS systems do

Each citation was opened for this memo; the sentence quoted is what the design
draws on.

| System | What it does | Source |
|---|---|---|
| Salsa (incremental queries) | A single revision counter; each input records the revision it last changed in; a memoized function re-executes only when a dependency changed since it was verified, and a re-execution that produces an equal value is backdated so dependents stay valid ("early cutoff"). Dependencies are checked by walking them. | [overview](https://salsa-rs.github.io/salsa/overview.html), [algorithm](https://salsa-rs.github.io/salsa/reference/algorithm.html), [maybe_changed_after](https://salsa-rs.github.io/salsa/plumbing/maybe_changed_after.html) |
| Adapton (demand-driven incremental computation) | A demanded computation graph of cells and thunks; "when a cell changes, the thunks that read it are the ones whose results are in question", the rest is reused. | [adapton.org](http://adapton.org/) |
| Rete (production rules) | Matching proportional to change: "the production system needs only to evaluate the changes (deltas) to working memory"; stored partial matches trade memory for speed; conflict resolution orders the agenda by salience and rule order; each instance fires at most once per cycle. | [Wikipedia: Rete algorithm](https://en.wikipedia.org/wiki/Rete_algorithm) |
| Datalog, semi-naive evaluation | The fixpoint re-derives nothing: "semi-naïve evaluation avoids this repeated computation by only working with the new tuples generated in the previous iteration." | [Wikipedia: Datalog](https://en.wikipedia.org/wiki/Datalog) |
| Differential dataflow | Computations maintained as their inputs change, with updates "in as little as milliseconds"; collections as differences, operators over differences. The paper's PDF did not open for this memo; only the book's introduction was read. | [introduction](https://timelydataflow.github.io/differential-dataflow/introduction.html) |
| Bevy ECS scheduler | Access is inferred from each system's signature; non-conflicting systems run in parallel, and for conflicting systems "the order is nondeterministic by default" unless `before`/`after` constrain it; an ambiguity is "systems with conflicting access but indeterminate order", reported at the level `ambiguity_detection` sets (default ignore). | [cheatbook: system order](https://bevy-cheatbook.github.io/programming/system-order.html), [ScheduleBuildSettings](https://docs.rs/bevy_ecs/latest/bevy_ecs/schedule/struct.ScheduleBuildSettings.html), [MultiThreadedExecutor](https://docs.rs/bevy_ecs/latest/bevy_ecs/schedule/struct.MultiThreadedExecutor.html) |
| Bevy change detection | `Changed<T>` "only retains results the first time after they have been added or mutably dereferenced"; a mutable access counts whether or not the value changed, since Bevy "does not compare components to their previous values", and the filter still iterates every entity. | [Changed](https://docs.rs/bevy_ecs/latest/bevy_ecs/query/struct.Changed.html) |
| Unity Entities version numbers | A global version increases before every system update; each chunk keeps, per component type, the version of its last writeable access, which "doesn't guarantee that anything has changed, only that it might have changed"; `WithChangeFilter` processes only chunks written since the system last ran. | [version numbers](https://docs.unity3d.com/Packages/com.unity.entities@1.3/manual/systems-version-numbers.html) |
| Unity Entities job dependencies | "ECS keeps track of which systems read and write which components"; a system's `Dependency` is built from earlier systems' jobs that wrote what it reads or read what it writes, so readers overlap and a writer waits. | [scheduling jobs](https://docs.unity3d.com/Packages/com.unity.entities@1.3/manual/systems-scheduling-jobs.html), [job dependencies](https://docs.unity3d.com/Packages/com.unity.entities@1.3/manual/scheduling-jobs-dependencies.html) |
| Deterministic lockstep | The simulation is a pure function of initial state and inputs, exact enough that "a checksum of your entire physics state at the end of each frame ... would be identical"; floating point across compilers and build configurations is what breaks it. | [Gaffer on Games](https://gafferongames.com/post/deterministic_lockstep/) |
| Photon Quantum systems | Systems run in insertion order "on all clients"; they are stateless, all mutable state lives in the frame; per-entity scheduling is `entity.Index % schedulePeriod == frame.Number % schedulePeriod`, a function of simulation data alone. | [Quantum systems](https://doc.photonengine.com/quantum/current/manual/quantum-ecs/systems) |

What Puck already has, and what each system adds to it:

- Puck's row version is Salsa's "changed at": it moves only when a commit
  leaves a row's bytes different (`StateArena.RowVersion`,
  `StateArena.MarkRowVersion`), which is the early cutoff Salsa backdates for
  and Bevy and Unity explicitly lack (a write access counts as a change there).
  The latch's captured versions are the "verified at". What is missing is the
  push: Salsa walks dependencies from the query being asked, which is pull;
  Adapton and Rete start from the changed cell and reach the readers.
- Puck's document order is Rete's conflict resolution and Quantum's insertion
  order: fixed, authored, and part of the semantics. Bevy's and Unity's
  parallelism comes from declaring order ambiguous where access does not
  conflict; Puck cannot take that path, because its hazard analysis finds
  86,280 order-sensitive pairs among the debug world's rules
  (`world.rule.hazards`, `RuleHazards.Analyze`), and the order is what authors
  rely on. Puck's parallelism must keep the order and speculate instead.
- Unity's per-chunk change versions and Bevy's component ticks are the
  precedent for versioning what the host owns, not only the arena: a facet is
  a component type the host writes, and it can carry a version the same way.
- Semi-naive evaluation is what a fixpoint group should do: a pass over the
  members whose write rows moved in the last pass, not over every member.

## The options, weighed

1. **Keep the pull, trim its constants.** Cache the body keys, resolve the
   latch state once per compiled rule (the
   [open item](open-items.md#other-frame-time-findings)), drop the fault scope
   when a rule cannot fault. Taken for the body keys and the latch state, and insufficient alone:
   the visit count stays O(rules + bindings), and the volatile 71 % is
   untouched.
2. **A Rete network over cells.** Alpha memories per cell, beta joins per
   gate, matches stored. Rejected: it is a second evaluator beside
   `RuleEvaluation.Decide`, it stores partial matches for gates that are a
   few comparisons, and it has no answer for effects with refusal and rewind.
   Puck's rows are already the join keys; row versions give the delta without
   the memories.
3. **Rules as a dataflow (differential) program.** Rejected: rule effects are
   imperative writes under a journal with refusal, transforms and irreversible
   arms; expressing them as operators over difference collections changes
   what an author writes. The idea survives at one point: a fixpoint group's
   next pass runs only the members a moved row makes due.
4. **Declared-access parallelism with reordering (Bevy, Unity).** Build the
   conflict graph from `RuleHazards` and run non-conflicting rules, effects
   included, on the pool. Rejected now: it changes rule-read semantics (the
   job-system memo's reason for keeping the phase serial), and parallel
   commits need the per-partition journal, key minting and pool claims the
   memo files under the kit-transaction redesign. It is the later path once
   that redesign lands, and the hazard analysis is the graph it would use.
5. **Push-based due set from versions, speculation validated by versions.**
   Taken (see what landed), speculation deferred. The due set makes evaluation proportional to what
   changed; the speculative gate phase runs the due set's gates in parallel
   and the serial phase reuses a verdict only when versions prove it still
   holds, so order and results are exactly today's.
6. **Per-cell versions now.** The arena versions rows; a sweep over a row of
   361 cells is due whenever any cell moves. Deferred: the journal already
   knows the written cells (`FlagOpenRows` with a written list), so a
   per-binding due set is a refinement of the same mechanism, taken when the
   sheet says a sweep's whole-row re-evaluation is what
   remains.
7. **Wall-clock budgets per rule.** Rejected: a timing is not deterministic
   and not comparable across machines; the sheet prices in work units
   (`RuleWorkBudget`), and the measured line counts what ran.

## What landed

One mechanism: a rule's schedule names what its judgement depends on, the host
versions what it owns, and changes push the rules that read them onto a due
list. The [state reference](../reference/state/analysis.md#skip-rules-whose-inputs-havent-changed)
describes each part as it runs; this section keeps the reasoning.

**The invariant.** An evaluation the scheduler elides is one the in-place
evaluation would reproduce bit for bit. One door enforces it,
`RuleEvaluator.VersionsMatch`: a verdict is kept only when the schedule is not
volatile, the tick is before the verdict's deadline, and every dependency the
schedule names reports the version captured with the verdict. A dependency a
schedule cannot name keeps it volatile, and a volatile rule is judged every
tick. Nothing the scheduler keeps enters the state hash
(`RuleLatch.AppendStateHash` folds only the edge bits), so every cache can be
dropped at any time without moving a hash.

**Where the guarantee rests, and what covers it.**

- A judgement is the locals, live rows and gate, never the effects
  (`RuleDataflow.CollectJudgement`), so a schedule's completeness rests on that
  walk reaching every operand a judgement reads, nested programs included. The
  comparison law covers the walk's shapes
  (`LatchAndTriggerLawTests.TheDueListChangesNoObservableResult`); a new
  operand that evaluates a program of its own needs the walk extended
  (`RuleDataflow.CollectOperandFacts`).
- Every row version moves through one door, `StateArena.BumpRowVersion`, which
  feeds the due list's moved-row feed; adoption marks every row moved.
- A host fact's version is derived from the value its read answers, never
  recorded by a writer (`WorldRuleHost.RefreshFacts` compares each body's pose,
  facts and liveness with the last refresh), so no door that moves a body can
  forget a version. The derivation is current wherever it is read because the
  evaluator refreshes before each family's walk and after any firing that
  delivered an arm, the only rule-time work that changes host state. The
  placement table's version moves at its one writer
  (`WorldPopulation.RebuildPlacementOrdinalTable`).
- A replaced arena, or a latch whose scheduler caches were forgotten
  (`RuleLatch.SchedulerEpoch`), marks every rule due; the due list notices both
  itself rather than relying on the host's call.
- A deadline is a gate conjunct whose falsehood alone closes the gate
  (`RuleDeadline.Find`, a three-valued fold of the postfix gate); its wake is
  checked against the live tick read at capture, and any doubt gives tick 0,
  which no skip trusts.

**What it did on the standing debug tick** (headless, 30 Hz, counted over
ticks 900 to 1800, hashes identical at every fixed tick): rule visits fell from
1,423 to 182 a tick; evaluations that ran (not kept) from about 4,500 to 337;
firings stayed at 96. The body sweeps judge only the bodies whose facts moved
(about 10 a tick for `fall-recovery`), the Go and Reversi reads judge the
stones whose pose moved, and the Moth's `blink-*` rules are judged at their
deadlines instead of every tick.

## Speculation is not worth a fork yet

The memo's third part, speculating the due list's gates on the pool
(`JobPool.Fork`) and committing in order, was to be taken only when the due
list held enough non-volatile evaluations to fill more than one partition. The
spent sheet after the rest landed answers it: of 337 evaluations that run a
tick, 171 belong to volatile rules (never speculable), and of the 166 versioned
ones, 95 fire, so their cost is the serial firing, not the gate. About 70
non-firing versioned gates a tick remain, a few tens of microseconds; a fork's
dispatch and join, the partition-owned evaluation context it needs extracted
from `WorldRuleHost` and `ArenaEffectHost`, and the version-validated reuse in
the serial walk cost more than that. Revisit when the sheet shows hundreds of
non-firing versioned evaluations a tick in a world that needs them; the
partition-owned context stays the first step, and any speculation reuses
`RuleSchedule` and `VersionsMatch`.

## What it leaves open

- The volatile rest, as the spent sheet names it: the chess sweeps read
  `physics(quiescent, ...)` (three rules, 32 bindings each), a binding-free
  world fact whose value the host could version by comparison at each refresh,
  as it does a body's pose.
- A placement-pose move marks its readers in full: `go-read-stones` is visited
  every tick (its 361 bindings keep their verdicts) because some placement
  inhabitant moves every tick. Reporting the moved placement ordinal, and
  marking a row sweep's binding for that key, would leave it unvisited.
- Per-cell (per-binding) due sets for sweeps over rows (option 6): a moved row
  re-judges every binding of a sweep that reads it, each passing the version
  door.
- Facet versions for host facts that are not per-body storage (engagement, the
  input channels' epoch, placement responses), each a host door of its own,
  taken as the spent sheet names them.
- The parallel commit of independent rules (option 4), behind the job-system
  memo's kit-transaction redesign.
- The machine worker wait under `WorldMachineHost.Advance` and the instance
  host's share of the tick are outside this memo.
- The latch's two dictionaries per binding
  ([open item](open-items.md#other-frame-time-findings)).

---

[Plans](README.md) · [Engine job system](engine-job-system.md) · [State and language](state-and-language.md) · [Open items](open-items.md#engine-job-system)
