# Recurring hazards

Most high findings in Puck's reviews come from a handful of code shapes, each
found one instance at a time. This page names those shapes, shows real
instances, says what closed them, and says how each is detected: an exact
compile-time analyzer in `Puck.Analyzers`, a `puck scan` heuristic a person
triages, or review alone. A lane reads it before changing code in an area these
shapes touch, and a reviewer hunts for them by name.

Two homes hold every check, and a third tool is never added:

- **Exact rule: a `Puck.Analyzers` diagnostic.** The rule is decidable from the
  compiled program and the marks the code carries, so the compiler enforces it
  on every build. A rule whose open findings would turn the tree red lands as a
  warning kept out of `TreatWarningsAsErrors` (`Directory.Build.props`) until
  they are fixed.
- **Heuristic: a `puck scan` analyzer.** The shape is recognizable from syntax
  but whether it is a defect is a judgement, so the analyzer lists sites for a
  person and its precision is tuned against the false positives found
  ([reference](../reference/cli.md#puck-scansource-sweep)).

Prefer a structural fix (a type, one door, a derived view) to either: a check
finds the next instance, while a structure leaves it no representation.

## Summary

| Shape | Detection | Where |
|---|---|---|
| [An invariant kept by convention](#an-invariant-kept-by-convention) | Review | The fix is a door or a type, which the review names |
| [A result a caller can drop](#a-result-a-caller-can-drop) | Exact, planned | `OUTCOME001`-`004` in the [rule-faults plan](../plans/rule-faults.md) |
| [A reference captured across a replacement](#a-reference-captured-across-a-replacement) | Exact | `STALE001`-`003`, `StaleCaptureAnalyzer` |
| [Two copies of one computation](#two-copies-of-one-computation) | Heuristic for C#; review for C# and HLSL | `puck scan --only clones` |
| [Recorded versus derived](#recorded-versus-derived) | Heuristic | `puck scan --only identity-counters` |
| [One local fault poisoning a global query](#one-local-fault-poisoning-a-global-query) | Review | None |
| [A cache that cannot help](#a-cache-that-cannot-help) | Heuristic | `puck scan --only exact-keys` |
| [Unbounded retention](#unbounded-retention) | Review | None |

## An invariant kept by convention

A cache stays valid only if every writer remembers to bump its version; a
geometric routine is right only for the shapes its author pictured; an oracle
summarizes what it checks and so misses what it summarized away. Each holds
until the next writer, shape or case arrives.

- A body's simulation continuation has no single write door, so a rest-epoch
  cache of its hash missed four writes to sleeping bodies and was deleted
  (`WorldPopulation.AppendContinuationHash`,
  `src/Puck.World.Server/WorldPopulation.Checkpoint.cs`; the redesign is in the
  [refactoring campaign](../plans/open-items.md#refactoring-campaign)).
- The cascades' exit layer assumes a caster encloses a solid
  (`SdfMeshDraw.EnclosesSolid`); a body clipped at a portal's glass is a thin
  caster that shadows its own wrapped side
  ([lighting packet](../plans/open-items.md#lighting-and-gi)).

**Closed by** one door that moves the version (a storage type the writer
reaches only through an accessor), or a type that cannot represent the case the
author did not picture. **Detected by** review: whether an invariant has one
enforcing place is a design question no syntax shows. A lane names the
invariant and its enforcing place in its commit, as the delegate contract asks.

## A result a caller can drop

A method reports failure beside its value (an `out` flag, a register, a `bool`)
and a caller discards it, or a nested call's success overwrites an outer
failure.

- `GateOpen(faulted: out _)` discards the fault on the strength of an enclosing
  scope (`src/Puck.State.Rules/Evaluation/RuleEvaluator.cs:573`,
  `RuleEvaluator.Groups.cs:101`,
  `src/Puck.World.Server/WorldRuleHost.DecisionNeighbors.cs:98`, `:324`).
- A nested `RuleExpressions.Run` is handed its caller's `fault` variable, so a
  nested success writes `None` over an outer fault
  ([rule faults](../plans/rule-faults.md#the-verdict-on-the-approach), reason 1).
- `out _` on `KeyNaming` drops whether an address named anything.

**Closed by** carrying the fault in the value: `Outcome`, `Resolved<T>` and
`Verdict` in the [rule-faults design](../plans/rule-faults.md#the-design), whose
default is the faulted case. **Detected by** `OUTCOME001`-`004`, which that
plan owns: an unconsumed outcome, an unchecked `TryFinite`/`TryNamed`, a new
detached evaluator, and an operand holding its own addresses. This page does not
duplicate them.

## A reference captured across a replacement

A host can swap a live object while code holds the old one: a document install
or checkpoint restore replaces the state arena, and the scratch and journal go
with it. A local taken before the swap names the old instance after it.

- `RuleEvaluator.FireEffects` took `scratch` once and opened each delivery's
  fault scope on it after `CommitTransactional` had installed a new arena, so a
  delivery fault after a document install threw in a live World. The fix is
  one door: `RuleEvaluator.Deliver`
  (`src/Puck.State.Rules/Evaluation/RuleEvaluator.Effects.cs`) reads the scratch
  the host serves after the commit.
- `RuleEvaluator.RunFixpoint` takes `arena` before the pass and ends its change
  window on it after members that can fire a transactional commit
  (`src/Puck.State.Rules/Evaluation/RuleEvaluator.Groups.cs:145`, STALE001).
  Open.
- `RuleEvaluator.EvaluateOnce` leaves its fault scope on the scratch it entered
  before the evaluation fired (`src/Puck.State.Rules/Evaluation/RuleEvaluator.cs:509`,
  STALE001), and `WorldRuleHost` reads `scratch.RaisedFault` after a decision's
  effects fire (`src/Puck.World.Server/WorldRuleHost.Decisions.cs`). Open; step 4
  of the [rule-faults plan](../plans/rule-faults.md#the-design) deletes the
  register these read.

**Closed by** reading the live object from its owner after the call, or by
moving the work after the call into a method of its own that reads it there
(the `Deliver` door).

**Detected by** `StaleCaptureAnalyzer`, exactly, from marks:

- `[Replaceable]` on a type whose live instance can be swapped (`StateArena`),
  and `[Replaceable(typeof(StateArena))]` on what is replaced with it
  (`ArenaScratch`, `ArenaJournal`). The marks are `build/ReplacementAttributes.cs`,
  linked into every project.
- `[Replaces(typeof(StateArena))]` on a member that can swap it:
  `WorldServer.AdoptPreparedArena`, `IEffectHost.CommitTransactional`, and the
  members the rule below requires.
- **STALE001** (warning): a local or parameter of a replaceable type read, on a
  path of the method's control flow graph, after a call that replaces its root,
  with no new value written between. The analyzer follows calls inside one
  compilation on its own. A variable handed to the replacing call is the
  replacement and is exempt.
- **STALE002**: a member reached by dispatch carries the mark on the member it
  overrides or implements, and a public member of a type that hands out the
  root or replaces it directly carries the mark, so the next assembly sees the
  replacement. Test assemblies owe no mark.
- **STALE003**: a method that assigns the field a property serves (the live
  pointer, as `WorldServer.Arena` serves `m_arena`) outside a constructor
  carries the mark.

The roots are types, not instances: a call that replaces a shadow server's
arena counts against a capture of the live one. A read that can only name an
instance the call leaves alone is suppressed at the read, with its reason.
STALE001 stays a warning while the findings above are open.

## Two copies of one computation

The same formula written twice drifts: one copy is fixed and the other is not,
or one adds a field the other forgets.

- The test copies of the bake's sample-position formula, written out in four
  `SdfVm` test helpers and `MeshCpuBake`, now one `SdfMeshPathBake.SampleAt`
  (`src/Puck.SdfVm/SdfMeshPathBake.cs:44`).
- Five copies of a `NativeMemory`-backed test `IAllocator` named
  `HeapAllocator` (`tests/Puck.Vulkan.Tests/VulkanIndirectCountLawTests.cs:155`
  and four more; [refactoring campaign](../plans/open-items.md#refactoring-campaign)).
- The Superellipsoid's CPU and GPU formulas disagree at small radii
  (`src/Puck.SignedDistance/Queries/SdfFieldEvaluator.cs:836` against
  `mapCore`), and the box cull is written in C# and HLSL
  (`SdfMeshCull.Keeps`, `sdf-mesh-selection.hlsli`).

**Closed by** one implementation every caller shares; for a C# and HLSL pair,
one generated from the other or an agreement law that evaluates both on the same
inputs.

**Detected by** `puck scan --only clones` for C# against C#: whole type bodies,
callable bodies, nested blocks and outermost expressions, each fingerprinted
with identifiers and literals abstracted, so renamed copies collide. The type
pass finds a pasted helper type whose members are each under the token floor
(the `HeapAllocator`s), and the expression pass finds a formula copied into
callers whose surrounding statements differ (the sample position). A C# and
HLSL pair is review territory: the two languages share no syntax tree, and a
lane that writes one side names the other in the change.

## Recorded versus derived

A value the replay or the restore can derive is recorded anyway, and the two
disagree; or a process-local counter names simulation state, so a restored or
replayed run mints different identities.

- The replay tape recorded lifecycle events the replay also derives; the tape
  now records only authority inputs.
- `WorldHandleTable`'s generation counter named handles a guest keeps in its
  memory without riding the checkpoint; its `Capture` and `Restore` now carry it
  (`src/Puck.World.Server/WorldHandleTable.cs`).
- `WorldSessionResolver.m_nextGenerationId`
  (`src/Puck.World.Schema/WorldSessionResolver.cs:75`) mints the generation ids
  a transfer continuation's checkpoint carries, and nothing captures the counter.
  Unconfirmed: a restored host's adoption mints afresh rather than reusing.

**Closed by** deriving the value from what is recorded, or carrying the counter
in the checkpoint codec of the object that mints.

**Detected by** `puck scan --only identity-counters`: an integer field of the
simulation projects incremented to hand out an identity, or named for one, that
no checkpoint, snapshot or codec member of its type mentions. It matches names,
so a person decides whether the identity reaches simulation state.

## One local fault poisoning a global query

One bad input spoils an answer every caller shares: one unbounded shape
collapsed the world field's frame for every query, and one scope's scale was
folded into the whole program's step.

**Closed by** bounding the input where it enters (a shape without a bound is
refused or given its own frame), so a global answer is built only from inputs
that each satisfy its precondition. **Detected by** review: whether a value is
local or global is the design's meaning, not its syntax.

## A cache that cannot help

A cache keyed on inputs that move every frame never hits, and costs its lookups
and its memory; or a ring is flushed by entries that can never match.

- `WorldCameraClearance.ClearanceQuery` and `WorldEffectorSolver.SurfaceQuery`
  compare floats by their bits
  (`src/Puck.World.Client/WorldCameraClearance.cs:146-165`,
  `src/Puck.World.Client/WorldEffectorSolver.cs:242-280`), so a moving camera or
  a walking foot asks a new question every frame and the 32- and 64-entry rings
  are flushed by answers that never match again. The two rings are also one
  mechanism written twice.

**Closed by** quantizing the key to the resolution the answer is good for, or
deleting a cache whose hit rate does not pay; a counter of hits and misses shows
which.

**Detected by** `puck scan --only exact-keys`: a cache-key type comparing float,
vector or fixed-point members exactly, an equality of float bit patterns, and a
comparison against a remembered continuous field. Exactness is right where the
value is copied rather than recomputed (a weld of identical vertices, a change
detector), which the person triaging decides.

## Unbounded retention

A collection that grows with the session (a cache without eviction, a log, a
per-entity table whose entries outlive their entity) is a memory leak on a long
run. **Closed by** a bound or an owner whose lifetime ends the entry.
**Detected by** review; the long-run memory measurements in the
[job-system packet](../plans/open-items.md#memory-ownership) show where it
costs.
