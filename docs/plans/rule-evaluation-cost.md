# Rule evaluation proportional to change

The design memo for the cost of authored rules in the tick: where the debug
world's rule work goes, measured per mechanism and per authored rule; what
incremental, dataflow and ECS systems do to make evaluation proportional to
change and to run independent work in parallel deterministically; and the one
approach this memo recommends, with its invariant, its checks and an
implementation brief. It extends the [job-system memo](engine-job-system.md),
which keeps rule evaluation serial and names the path to a parallel rule phase,
and the [state reference's scheduling section](../reference/state/analysis.md#skip-rules-whose-inputs-havent-changed),
which owns the current skip's contract.

The frame the owner set (AGENTS.md): Puck code over engine code, so the engine
accelerates authored rules generically and never hand-writes one in C#;
ergonomic is optimal, so the sweep an author writes costs no more than the
hand-unrolled form; and determinism, so the same document and input give
bit-identical state at any thread count.

## The problem

Every tick, `WorldRuleHost.EvaluateWorldRules` evaluates every compiled rule in
document order (`WorldRuleHost.Evaluation.cs`, the ungrouped array, then the
groups, then the interactions), and the document order is the semantics: a
committed firing is visible to the next rule's gate
([rules](../reference/state/rules.md#ordering-and-visibility-of-earlier-writes)).
The evaluator already skips a rule whose gate closed last time when a row
version proves every row it reads unchanged (`RuleEvaluator.EvaluateOnceCore`,
`RuleSchedule`, `RuleLatch.BindingSchedule`), and memoizes locals the same way.
That skip is pull-based: every rule, and every binding of every sweep, is
visited every tick to ask whether it may be skipped, and a rule whose schedule
is volatile (it reads `$tick`, `$search:ply`, a host facet such as
`bodyState(...)` or `bodyPosition(...)`, a host-owned row, or a row with a
value-over-time trait) is never skipped at all (`RuleNeedsBuilder.Build`,
`RuleSchedule.Build`).

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
  do not shape the numbers. The counter is a prototype, not landed; step 1
  below lands its deterministic half.

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
| Gate (`GateOpen` → `RuleEvaluation.GateHolds`) | 2.8 % | authored |
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
   when a rule cannot fault. Worth doing first (step 2) and insufficient alone:
   the visit count stays O(rules + bindings), and the volatile 71 % is
   untouched.
2. **A Rete network over cells.** Alpha memories per cell, beta joins per
   gate, matches stored. Rejected: it is a second evaluator beside
   `RuleEvaluation.GateHolds`, it stores partial matches for gates that are a
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
   Recommended below. The due set makes evaluation proportional to what
   changed; the speculative gate phase runs the due set's gates in parallel
   and the serial phase reuses a verdict only when versions prove it still
   holds, so order and results are exactly today's.
6. **Per-cell versions now.** The arena versions rows; a sweep over a row of
   361 cells is due whenever any cell moves. Deferred: the journal already
   knows the written cells (`FlagOpenRows` with a written list), so a
   per-binding due set is a refinement of the same mechanism, taken when the
   measurement after step 3 says a sweep's whole-row re-evaluation is what
   remains.
7. **Wall-clock budgets per rule.** Rejected: a timing is not deterministic
   and not comparable across machines; the sheet prices in work units
   (`RuleWorkBudget`), and the measured line counts what ran.

## The recommendation

One mechanism, in four parts, each landing on its own: the schedule names what
an evaluation depends on; the host versions what it owns; changes push the
rules that read them onto a due list; and the due list's gates may run in
parallel ahead of the serial, document-ordered commit.

### The invariant

**An evaluation the scheduler elides, or a speculated verdict it reuses, is one
whose result the in-place evaluation would have reproduced bit for bit.**

Enforced at one door, `RuleSchedule`: a schedule names every dependency an
evaluation can have (the arena rows it reads, the facets it reads, whether it
reads the tick), and an elision or a reuse is admitted only when the schedule
is not volatile and every named dependency's version equals the one captured
with the verdict (`RuleEvaluator.VersionsMatch`). A dependency a schedule
cannot name makes it volatile, and a volatile schedule is never elided or
reused: that is the safe default today (`RuleNeedsBuilder.Build` marks any
facet, host-owned row, tick read or trait row volatile), and a facet becomes
versionable only by the host advertising a version for it (step 4). Nothing
the scheduler keeps enters the state hash: the latch's `Held` bits remain the
only hashed half (`RuleLatch.AppendStateHash`), so a cache can be dropped at
any time without moving a hash.

Where the guarantee stops, and what covers it:

- A host that moves a row's bytes outside `StateArena.MarkRowVersion` (undo
  restore, snapshot restore, relayout each bump `m_rowVersions` themselves)
  must call `RuleLatch.InvalidateScheduler`, as `WorldRuleHost` does when the
  arena is replaced; the due list is invalidated by the same call (every rule
  becomes due once).
- A facet that advertises a version must move it on every write to what the
  facet reads. That is the host's single-door obligation per facet, and the
  check that catches a missed door is the reproduce comparison below: a run
  with elision off and a run with it on must give the same authority hash
  sequence.
- `$tick` and `$search:ply` stay volatile except for the deadline form step 5
  proves (`$tick >= X` with no other tick read in the rule).

Checked by: `puck test worlds/debug/*-tests.puck --reproduce` (two runs agree,
every tick's authority hash); `puck test --workers 1,2,0` for the parallel
phase (identical hash sequences at every worker count, the harness the
job-system memo's T0 built); and the existing comparison law under
`RuleEvaluator.SchedulingEnabled` (`LatchAndTriggerLawTests` in
`tests/Puck.State.Rules.Tests`, which evaluates one rule set with scheduling on
and off and compares the cells), extended so that a scenario's hash sequence
with the due list and speculation off equals the sequence with them on. A
scheduler counter never enters a hash, so `world.state.hash authoritative` is
the same under every setting.

### 1. The schedule names facets as well as rows

`RuleSchedule` gains the facets the rule reads (`RuleNeeds.Facets`, already
collected by the compiler) beside its rows, and the host answers a
facet-version query (a `FacetRef` in, a version out) beside
`IStateReader.TryRowVersion`, the same shape. A facet with no version keeps the schedule volatile. The
schedule also names the iterated row of a sweep (`ForEachOrdinal`), so a key
minted or removed makes the sweep due; a body sweep names the population's
roster version.

Granularity is per facet, then per binding where the host can afford it: the
action-state lane and the body-facts lane are per-body storage, so their
versions are per body ordinal, and a body sweep's binding for body `i`
depends on body `i`'s version. The crowd's bodies then cost the sweep only
when their own state moves.

### 2. Changes push the readers onto a due list

At install, the evaluator builds the dependency index: per row ordinal and per
facet, the compiled rules whose schedules read it, in document order. The
arena's one door that moves a row version (`MarkRowVersion`, which already sets
the delivery mark) and each facet's door mark their readers due. The tick's
evaluation walks the due list in document order instead of the rule array;
a rule not due is not visited, not even to ask.

Due means: the rule is volatile (or its schedule is unknown), or a row or facet
it reads moved since its last verdict, or its last verdict was an open Level
gate (its firing is the author's per-tick choice), or it is a group member
whose group is running, or it is traced. An Edge rule whose gate was held and
whose reads are unchanged is not due: its verdict is still "open, already
held", which fires nothing, so it may stay off the list where today's skip
(`!wasOpen`) runs it.

Sequential semantics hold because the push happens at commit: a firing earlier
in document order that moves a row marks that row's readers due in the same
tick, and the walk reaches a later reader after the mark, exactly where today's
version check would find the version moved; an earlier reader sees the new
value next tick, as it does today.

### 3. Speculate the due gates in parallel, commit in order

When the due list holds enough non-volatile evaluations to fill more than one
partition, the tick forks their gates on the engine pool (`JobPool.Fork`, the
`Tick` class, fixed partitions of the due list in document order, independent
of the worker count): each partition evaluates locals and gate against the
arena as the phase starts, reading through a partition-owned evaluation
context (locals, bound key, instance bindings, pattern word and scratch, which
today live on the host, `WorldRuleHost.cs`), writes nothing, and records the
verdict, the locals and the versions it observed. The serial phase then walks
the due list in document order: a speculated verdict whose versions still
match is taken as the evaluation's result (its locals restored for the
effects); one whose rows moved under an earlier firing is evaluated in place,
as today. Firings commit serially, in document order, through the one journal.
Host-owned rule kinds (`IRuleOwner.TryEvaluateOwn`: decisions and
interactions) and pool sweeps (`PoolForEach`, whose membership an earlier
firing can change) are never speculated; they run in the serial walk.

This is the path the job-system memo named (partition-owned context,
`RuleSchedule` deciding what is speculable, the serial commit re-evaluating
what it cannot reuse). The gain is bounded by what the due list holds after
steps 1 and 2; the memo measured the whole gate phase at about 0.7 ms, so this
step is taken only when the counters after step 2 show a due list worth a fork
(the threshold is a measured parameter, and the fork runs inline below it).

### 4. The author sees what a rule costs

`world.budget.rules` keeps its worst-case sheet and gains a measured sheet:
`world.budget.rules --spent [top]` lists, per rule, the evaluations the
scheduler ran, the ones it elided, the firings, and the work units spent
(each evaluation at the line's `check` price, each firing at its `fire` price,
from the same `RuleCost` the ceiling uses, so the spent line is deterministic
and comparable across machines), with the rule's schedule beside it: the rows
and facets it depends on, and the reason it is volatile when it is (`reads
$tick`, `reads search(ply)`, `facet BodyPosition has no version`, `row goCell
carries a trait`). `--why <rule>` prints the same schedule. The counters
`state.rules.due`, `state.rules.elided` and `state.rules.speculated` join
`evaluations`, `skips` and `firings` in `world.counters state`. An author who
sees `klondike-seat-card` spend 52 firings a tick knows it is their Level gate;
one who sees `fall-recovery` volatile on `BodyPosition` knows which facet needs
a version, and the standard's rules are tuned on that sheet.

### 5. Deadlines wake rules, the tick does not

The authored deadline pattern (`schedule cell in 2s`, then `when $tick >=
cell[$each]`, the Moth's `boot-boost-*` and `blink-*` rules) reads `$tick`, so
it is volatile and evaluates every tick. For a gate whose only tick read is a
top-level conjunct `$tick >= X` or `$tick > X`, the compiler records X in the
schedule as a deadline read, and a closed verdict carries a wake tick: the
value X evaluated to. The binding is due at that tick, or earlier when a read
moves. This is sound because the gate's truth can change only when the tick
crosses X or a read changes, and both are named. A rule that reads `$tick`
elsewhere stays volatile.

### Cost

Expected from the measurement, for the debug world's standing tick:

- Evaluations a tick fall from 6,134 to the due set: the roughly 100
  evaluations of Level rules whose gate is open and fires every tick (the
  twenty once-rules and `klondike-seat-card`'s 52 bindings), plus the readers
  of rows and facets that moved that tick. With per-body versions, the three
  body sweeps' 1,674 evaluations become the bodies whose action state or facts
  moved; `fall-recovery` stays due for every body whose position moved, which
  is the crowd, unless its author gates it on a fact. The `go-*` sweeps'
  1,805 evaluations become zero while no Go stone moves. A reduction of 80 to
  90 % in evaluations, and the 1,423 per-rule visits become the due list's
  length.
- The rule phase's share of the tick falls from 37 % toward the authored
  firings' own cost: the always-firing rules spend about 0.5 ms of the
  measured 4 ms a tick, and the machinery around rules that are not due
  disappears with the visits. The ceiling the sheet admits
  (`world.budget.rules total`, 3,426,465 of 4,250,000 units) does not move: it
  is a worst case over capacities, and the spent line is what shows the
  change.
- Step 3's gain is what remains of the gates after step 2, measured before it
  is built.

Fewer evaluations is the cost reading; the correctness reading is the hash
sequence, and a step that moves it has a defect, never a correction, because
nothing here changes what a rule computes.

### How it proves itself in the debug world

The standing tick's `world.counters state` before and after each step; the
spent sheet's top lines; `puck test worlds/debug/*-tests.puck --reproduce`
(the Go, chess, card-table, input and rule-fault scenarios reach sweeps,
groups, deadlines, pools and interactions); `puck test --workers 1,2,0` for
step 3; and the sweep's signals (`puck sweep`) unchanged.

### What it leaves open

- Per-cell (per-binding) due sets for sweeps over rows (option 6), after
  step 3's measurement.
- Facet versions for facets that are not per-body storage (engagement, the
  input channels' epoch, placement responses), each a host door of its own,
  taken as the spent sheet names them.
- The parallel commit of independent rules (option 4), behind the
  job-system memo's kit-transaction redesign.
- The machine worker wait (the monitor wait under `WorldMachineHost.Advance`,
  13 % of the tick) and the instance host's share are outside this memo; the
  trace that found them is in this memo's commit.

## Implementation brief

For one lane in `src/Puck.State` (the arena's facet-version door and the
reader interface), `src/Puck.State.Rules` (schedule, due list, counters,
speculation), `src/Puck.World.Server` (the host's facet versions and the
partition-owned context), `src/Puck.World.Console` (`world.budget.rules
--spent`), and the state reference pages. Reuse, never duplicate:
`RuleSchedule` and `VersionsMatch` are the one dependency door;
`StateArena.MarkRowVersion` is the one row-change door; `JobPool.Fork` is the
one fork; `RuleWorkBudget`'s prices are the one cost model; `RuleHazards` is
the one conflict analysis. Each step lands alone, verified by building what it
touched and running the check named.

1. **The measured sheet.** Per-rule deterministic counters in `RuleEvaluator`
   (evaluations, elided, firings, spent units from `RuleCost`), reported by
   `world.budget.rules --spent [top]` and `--why`, with `state.rules.due` and
   `state.rules.elided` in `world.counters state`. Check: the per-rule totals
   sum to the evaluator's `evaluations`, `skips` and `firings`; a `--reproduce`
   run prints identical spent sheets on both replicas; the standing debug tick
   shows the rules named in this memo's table at the counts it gives.
2. **The pull's constants.** Body keys resolved once per population roster
   version (an index-to-key table built when the layout interns the indices,
   not a string lookup per body per rule per tick); each compiled rule's
   `RuleState` resolved once at install and held beside the rule; one entry per
   binding holding the hashed edge bit and the unhashed schedule cache (the
   [open item](open-items.md#other-frame-time-findings)); the fault scope and
   trace arming skipped for a rule the compiler proves cannot fault and is not
   traced. Check: `state.rules.*` counts and the authority hash sequence
   identical before and after on the debug test worlds; the sampled share of
   `EachKeys` and `RuleState.Binding` on the frame thread falls.
3. **Facet versions and the due list.** The facet-version query on
   `IStateReader` with per-body versions for the action-state and body-facts lanes and a
   roster version for `ForEachBodies`; the schedule names facets and the
   iterated row; the dependency index and the due list; `InvalidateScheduler`
   marks everything due. Check: `--reproduce` on every debug test world; the
   `SchedulingEnabled` comparison law extended to the due list; the standing
   tick's `state.rules.evaluations` falls by the share this memo's cost section
   predicts, with the body sweeps' lines near zero while the crowd's state
   holds.
4. **Deadline wake.** The compiler's deadline read and the wake tick on a
   closed verdict. Check: `boot-boost-off` and `blink-*` evaluate once per
   crossing in the spent sheet (a count equal to the schedule writes), hashes
   identical.
5. **Speculated gates on the pool.** The partition-owned evaluation context
   extracted from `WorldRuleHost` (locals, bound keys, instance bindings,
   pattern word, scratch) and from `ArenaEffectHost`; the arena read-only
   during the fork with any lazily built read cache prepared before it (the
   job-system memo's contract rule 2); fixed partitions of the due list;
   version-validated reuse in the serial walk; `state.rules.speculated` and
   `state.rules.reused`. Check: `puck test --workers 1,2,0` on the debug test
   worlds, identical hash sequences; the sampled rule share on the frame thread
   at the standing tick; the fork inline below the measured threshold.
6. **Documents.** The state reference's scheduling section and the rules page
   rewritten to the landed contract; the job-system memo's "Rule evaluation
   stays serial" paragraph replaced by a pointer here; then this memo's
   recommendation is deleted and the decision cites the research table that
   stays.

---

[Plans](README.md) · [Engine job system](engine-job-system.md) · [State and language](state-and-language.md) · [Open items](open-items.md#engine-job-system)
