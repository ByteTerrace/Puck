# Measured changes

Read this before landing a change whose reason is speed or allocation. Back to
[../SKILL.md](../SKILL.md). It distils the review record of ASP.NET Core's
agent-driven performance effort
([dotnet/aspnetcore#70219](https://github.com/dotnet/aspnetcore/issues/70219),
97 PRs). Its gains are each author's own microbenchmark on unmerged PRs; the
techniques and the defects review found are what transfer. Each technique cites one PR and one place in Puck.

## What a change is judged by

- **Changes pay dividends** (AGENTS.md). A change is kept when it wins on a
  measured metric (time, bytes, work counters) or when it simplifies, unifies
  or opens a design the next step builds on. One that does neither is dropped,
  and the hand-back says so.
- **Compatibility practices do not carry over.** Exact exception types and
  their timing, constructor order around virtual calls, deferred execution, "no
  API change" and "the smallest possible change" protected ASP.NET's customers.
  Puck has none (rule 5): change the type and update every caller in the same
  change. What holds is determinism (rule 4) and observable behaviour: what a
  World shows, writes, hashes, persists or sends.

## Measure

- **Stop a redirected trace explicitly and collect rundown.** `dotnet-trace`
  does not treat Enter as cancellation when stdin is redirected
  ([collector source](https://github.com/dotnet/diagnostics/blob/main/src/Tools/dotnet-trace/CommandLine/Commands/CollectCommand.cs)).
  Use its bounded duration, or stop the EventPipe session through DiagnosticsClient,
  and let the collector drain while the target remains alive. Killing a collector
  can leave a readable trace whose missing rundown makes managed stacks unusable.
  Convert and analyze one trace sequentially: TraceEvent readers share its
  `.etlx.new` intermediate, so concurrent readers can collide while creating it.
  Inspect the output artifact too; a converter can print an exception and still
  exit successfully.
- **A lane proves a win on load-independent counters:** allocated bytes
  (`AllocationWindow`, `puck bench world`'s allocation rows), march and ray step
  counts, `world.budget` work counts, and `puck bench world`'s contact counts.
  They hold beside other lanes' builds; a timing taken there does not
  ([the frame bar](../../../../docs/plans/engine-job-system.md#what-the-measurements-say)).
- **Wall-clock claims are interleaved A/B rounds** against a base build the
  lane built: base, candidate, base, candidate, reversed once, three pairs or
  more, every round reported. They run in the lead's quiet window through
  `puck measure run` or `puck lease quiet`, never beside other lanes' work
  ([orchestration](../../orchestration/SKILL.md#spend-verification-not-writing)).
  Read `BenchReport.HostLoad` before interpreting the result: the reservation
  coordinates leased work, and another test launched without a lease can still
  contaminate it. A report that records another heavy test is not quiet-window
  timing evidence; retain it and repeat only after that work has ended.
- **Prove the steady state runs the changed branch,** not a warm cache or a
  default path: count the changed path's calls, or break the change and see
  the number move. Two of #70158's six benchmark headers took the known-header
  branch, #70146 measured the CNG encryptor on Windows instead of the managed
  one it changed, and #70174's and #70175's tests passed with the cache
  reverted.
- **A motion harness must advance the production inputs.** A zero changed-pose
  count is not evidence of static foliage until the state mirror has installed
  the document's initial cells and the creation pool's clock has advanced.
  `WorldSceneEmitter.Tick` and `WorldCreationInstances.Tick` are separate doors;
  advancing only the former left a placement census motionless. The corrected
  production capture moves thousands of independent authored grass slots. Label
  a placement-only census separately from the native World's body population.
- **Measure where `TieredPGO` matches the target.** The World process runs
  without dynamic PGO
  (the `TieredPGO` property in [`Puck.World.csproj`](../../../../src/Puck.World/Puck.World.csproj)), and
  the setting is process-wide, so every library it loads runs that way.
  `puck bench kernels` and `puck bench world` run in the CLI with default PGO,
  and the World test hosts turn tiering off. A win that rests on PGO (guarded
  devirtualization, a stack-allocated enumerator) can show there and not in the
  World: confirm it in the World's build.

## Know what the change changed

- **Simulation:** compare state hashes from both builds over the touched
  scenarios. Equal hashes mean the change moved no result; a deliberate
  correction is re-recorded with its reason (rule 4); anything else is a bug.
- **Derived bytes:** compare digests of bake stores, payloads and captures
  across the builds, as #70125 compared every serialized test output.
- **No scenario reaches the path:** run old against new in a scratch harness
  over random and boundary inputs (#70157: 150,924 headers). It is commit-body
  evidence, not a committed test.
- **Drift review kept finding:** a skipped call that observed cancellation
  (#70180) and a decoder that turned lenient (#70142, a security defect).
  Change either on purpose when the design is better; never by accident.

## Techniques

| Technique | PR | In Puck |
|---|---|---|
| Decide before the expensive path. A prefilter may give only false positives, and a miss never repeats the fast path's work | #70119 | `RuleEvaluator.EvaluateOnceCore` skips bindings and gate when nothing the rule reads changed ([source](../../../../src/Puck.State.Rules/Evaluation/RuleEvaluator.cs)) |
| Cache a derivation keyed by exactly what it reads, and cache an absence when absence is the common answer | #70220 | `RuleLatch.RuleState` keys a rule's schedule by evaluator and compiled rule ([source](../../../../src/Puck.State.Rules/Evaluation/RuleLatch.cs)); `WorldPlacementStamper.Queryable` includes the composed parent frame and distribution seed, even when the child row is unchanged |
| Hoist keyed setup out of a repeated operation | #70157 | `SdfWorldTables.UpdateTablesSignature` hashes each region once with `XxHash3` ([source](../../../../src/Puck.SdfVm/SdfWorldTables.Cadence.cs)) |
| Size first, then write once into the final destination | #70134 | `SdfWorldTables.StageChangedDraws` writes a draw straight into its staged record ([source](../../../../src/Puck.SdfVm/SdfWorldTables.MeshRegion.cs)) |
| Remove re-copies and rescans of finished work | #70139 | `SdfMeshPathBake.Pending`'s done-through cursor ([source](../../../../src/Puck.SdfVm/SdfMeshPathBake.cs)) |
| Allocate rarely used state on first use | #70189 | `SdfFieldEvaluator`'s point walk builds its reach scratch on a thread's first walk ([source](../../../../src/Puck.SignedDistance/Queries/SdfFieldEvaluator.PointWalk.cs)) |
| A `static` lambda with a state argument, not a capturing closure | #70185 | `WorldSchema.ResolveDescriptionForMember` ([source](../../../../src/Puck.World.Schema/WorldSchema.cs)) |
| Without PGO, loop over the concrete collection where an interface enumerator boxes | #70147 | `SdfMeshPathBake` indexes copy membership when topology changes; `SdfMeshDresser` reads retained bindings instead of enumerating all copies per group |
| Format text only when it is emitted | #70178 | `GenlockPhaseAligner.LogPhase` checks `IsEnabled` first ([source](../../../../src/Puck.Launcher/GenlockPhaseAligner.cs)) |
| Look up by span through an alternate lookup, and decode strictly | #70142 | `CellKeyTable.TryResolve` ([source](../../../../src/Puck.State/CellKey.cs)) |
| Cache `SearchValues` statically; never rebuild one per call | #70224 | `IdentifierRules.ReservedCharacters` ([source](../../../../src/Puck.State/SafeName.cs)) |
| A scan over a few fixed entries beats hashing; one over a list that grows with the world is the quadratic | #70209 | Bake demand and dressing share one copy-membership index, replacing the old per-group scan over all copies ([data and memory](data-and-memory.md#a-scan-per-item-is-a-quadratic)) |
| Publish shared state as an immutable object in one reference write | #70176 | `CodeDerivedTable` swaps a whole generation: an old in-flight derivation publishes only into its detached weak table ([source](../../../../src/Puck.Abstractions/CodeDerivedTable.cs)) |
| Scratch its owner keeps and reuses, over a shared pool | #70159 | `WorldBody.StepScratch` ([source](../../../../src/Puck.World.Server/WorldBody.SweepRefusal.cs)) |

## Hazards review found

- **Never read a byte you did not write** (#70146 derived wrong keys from a
  rented array it assumed was zero); in Puck it is a determinism defect
  ([data and memory](data-and-memory.md#zero-only-what-you-read)).
- **A rent never returned costs more than an allocation** (#70195).
- **A borrow ends before its owner grows, resets or returns the storage,** and
  a writer handed out supports every member its interface promises (#70228).
- **Bound every capacity taken from input** (#70166 capped a list sized from a
  comma count).
- **A cache key that omits an input goes stale** (#70141 took two review rounds).
- **Clearing a shared table is not a generation boundary.** A derivation that
  started before the clear can publish after it. `CodeDerivedTable` holds one
  immutable generation through lookup, derivation and publication; replacing the
  table prevents old work from contaminating the new code's cache.
- **Materialize a query before storing it in a long-lived object** (#70183).
- **Key a fast path on the compiled canonical form,** never on how an author
  spelled something or which world runs (AGENTS.md, "Ergonomic is optimal").
- **Seal and change the type** rather than guarding a fast path with
  `GetType() == typeof(X)` beside a kept slow path (#70126): in Puck the kept
  path is a second implementation.
