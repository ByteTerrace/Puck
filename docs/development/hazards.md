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
| [A result a caller can drop](#a-result-a-caller-can-drop) | Exact | `OUTCOME001`-`004`, `OutcomeAnalyzer` |
| [A reference captured across a replacement](#a-reference-captured-across-a-replacement) | Exact | `STALE001`-`003`, `StaleCaptureAnalyzer` |
| [Two copies of one computation](#two-copies-of-one-computation) | Heuristic for C#; review for C# and HLSL | `puck scan --only clones` |
| [Recorded versus derived](#recorded-versus-derived) | Heuristic | `puck scan --only identity-counters` |
| [One local fault poisoning a global query](#one-local-fault-poisoning-a-global-query) | Review | None |
| [A cache that cannot help](#a-cache-that-cannot-help) | Heuristic | `puck scan --only exact-keys` |
| [Unbounded retention](#unbounded-retention) | Review | None |

## Silent invariants

Running the debug world shows most defects. These classes break without a
visible symptom, so a change in a class marked review-only gets an adversarial
review ([orchestration](../../.claude/skills/orchestration/SKILL.md#spend-verification-not-writing));
every other change is checked by running it and by the area fix passes. A class
leaves the review column when a check or a structure covers it, which is the
preferred outcome.

| Class | What catches it | Review-only |
|---|---|---|
| Determinism: same document and input, bit-identical state | `puck test --reproduce`; fixed-point `Puck.Maths` types in simulation state | No |
| The C# and HLSL shader interface: bindings, block layout, strides | One `ShaderInterface` generates both sides; `ShaderInterfaceLayout.Mismatch` refuses a module that disagrees | No |
| The SDF ISA's C# encoder and HLSL decoder (`SdfProgram.IsaLayout`, the SdfVm README's contract pairs) | Convention | Yes |
| An exactness-preserving skip in an exact evaluator: a cull, certificate or bound that claims a skipped part could not change the answer (`SdfFieldEvaluator`'s segment hierarchy and relief certificates) | Counterexample tests at the arithmetic edges (overflow, seeds, ties); sampled agreement with a reference evaluator misses them | Yes |
| [A reference read after its replacement](#a-reference-captured-across-a-replacement) | `STALE001`-`003` | No |
| GPU fences and resource lifetimes: a buffer retired, reused or resized while a queue still reads it | Review | Yes |
| A GPU read indexed by GPU data (a raster hit's draw and triangle, a record a pass left from an earlier frame) past its buffer: Direct3D 12's bounded views read zero, Vulkan reads freed or unmapped memory and loses the device, intermittently | A Vulkan run that loses the device names the passes in flight (`VK_NV_device_diagnostic_checkpoints`); GPU-assisted validation sees it only on a run where the read happens | Yes |
| Process environment reads and any-address listeners | `ENV001`, `NET001` | No |
| Trust boundaries: grants, federation, admission, credentials, a cap on what a sender claims | Review | Yes |
| Deleting, moving or rewriting files and persisted data outside the intended root or shape | Review | Yes |
| [An invariant kept by convention](#an-invariant-kept-by-convention), [unbounded retention](#unbounded-retention), [one local fault poisoning a global query](#one-local-fault-poisoning-a-global-query) | Review | Yes |

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
- A raster part with no draws skips its frame only while its target holds a
  clear. Keyed instead on "written once", a part whose draw count fell to zero
  left its last hits in the target, and the glass pass read their draw and
  triangle against the current draw records. `SdfWorldPassRecorder.TargetsHoldClear`
  is the one place the skip asks, and `RecordMesh`, the one writer of the
  targets, sets it.
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

- The retired gate protocol allowed `GateOpen(faulted: out _)` to discard a
  fault on the strength of an enclosing scope.
- The retired `RuleExpressions.Run` protocol handed a nested call its caller's
  fault variable, so a nested success could clear an outer fault
  ([Rule faults](../decisions/state-and-language.md#rule-faults)).
- `out _` on a key's `Resolved<T>` or `TryNamed` drops whether an address named anything.

**Closed by** carrying the fault in the value: `Outcome`, `Resolved<T>` and
`Verdict` in the [rule-fault decision](../decisions/state-and-language.md#rule-faults), whose
default is the faulted case. **Detected by** `OutcomeAnalyzer`, exactly, from
the compiler's symbols, in every build:

- **OUTCOME001**: an expression statement, a discard assignment or a discard
  declaration over `Outcome`, `Resolved<T>`, `Verdict` or `RuleOutcome`. An
  assignment keeps its value, and a statement in a `catch` that rethrows is let
  through, since the exception is the caller's answer. `OutcomeDiscardAllowlist`
  names the two host entries (`RuleEvaluator.Evaluate` and `EvaluateRule`, the
  bool overloads) that answer only whether the arena moved.
- **OUTCOME002**: a `TryFinite` or `TryNamed` whose bool is dropped, or a
  `Verdict` or `FiringDecision` compared (`==`, `!=`, an `is` constant pattern)
  to a case other than `Faulted`. A comparison to `Faulted` is the handling
  itself; a `switch` that names every case is the spelling for the rest.
- **OUTCOME003**: a call of `RuleExpressions.EvaluateDetached` from a file
  `OutcomeDetachedAllowlist` does not name; each entry states what its
  `Faulted` arm does.
- **OUTCOME004**: a member of an `IRuleOperand` that resolves an address
  against the reader itself (`LiveRow.Resolve`, a `CompiledCellRef` or
  `IRuleKey` resolution). An operand may hold its declared addresses (its read
  set, price and `Addresses` list are built from them) and receives them
  resolved; it may not call them.

Test assemblies (those that reference xUnit v3) are held to none of the four:
a test watches state or evaluates as the oracle. What the rules cannot see: a
bool held in a local and never read, an outcome handed to a method that ignores
it, and a hand-written arena read with a literal key.

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
- `RuleEvaluator.RunFixpoint` took `arena` before a pass and ended its change
  window on it after members that can fire a transactional commit. It now ends
  the window on the arena the host serves after the pass
  (`RuleEvaluator.EndPassWindow`), and a replaced arena counts as progress.
- `RuleEvaluator.EvaluateOnce` entered a fault scope on the scratch, ran an
  evaluation that can fire a transactional commit, and left the scope on the
  scratch it entered. The scope now covers judging alone
  (`RuleEvaluator.Judge`), which fires nothing; the firing scopes its own
  faults before and after its commit.

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
- **STALE001**: a local or parameter of a replaceable type read, on a
  path of the method's control flow graph, after a call that replaces its root,
  with no new value written between. The analyzer follows calls inside one
  compilation on its own. A variable handed to the replacing call is exempt,
  on the unproved assumption that the call adopts it.
- **STALE002**: a member that reaches a replacement and is reached by dispatch
  has the mark on the member it overrides or implements, and a public member of
  a type that hands out the root (`WorldServer.Step`, `ArenaSearch.Step`)
  carries it, so the next assembly sees the replacement. Test assemblies owe no
  public mark.
- **STALE003**: a method that assigns the field a property serves (the live
  pointer, as `WorldServer.Arena` serves `m_arena`) outside a constructor
  carries the mark.

The roots are types, not instances: a call that replaces a shadow server's
arena counts against a capture of the live one. A read that can only name an
instance the call leaves alone is suppressed at the read, with its reason
(`ArenaSearch.StepJob`, whose search owns its arena). A member of an assembly
that cannot see `StateArena` (`IFixedStepSimulation.Step` in `Puck.Hosting`)
carries `[Replaces(typeof(object))]`, which covers every root. Test assemblies are not
held to STALE001: a test keeps the retired arena on purpose, or drives a host
whose commit never replaces. STALE001 is an error like every other warning
(`TreatWarningsAsErrors`).

What the rule cannot see: a replacement reached through a delegate, an event,
reflection or a constructor; a capture held in another object's field; dispatch
through a framework interface, which cannot carry the mark; and, across
assemblies, a replacement reached through an unmarked public member of a type
that does not hand out the root.

These gaps are open as well, until the
[analyzer's soundness item](../plans/open-items.md#agent-tooling-and-skills)
closes them:

- a getter that returns its field through a local, an auto-property or `??=`;
- the catch and finally paths after a refresh on the normal path;
- a read inside a local function or lambda that a later call runs;
- an async replacement that completes after the capture;
- an inherited root accessor across assemblies.

## Two copies of one computation

The same formula written twice drifts: one copy is fixed and the other is not,
or one adds a field the other forgets.

- The test copies of the bake's sample-position formula, written out in four
  `SdfVm` test helpers and `MeshCpuBake`, now one `SdfMeshBakeRequest.SampleAt`
  (`src/Puck.SdfVm/SdfMeshPathBake.cs`).
- Five copies of a `NativeMemory`-backed test `IAllocator` named
  `HeapAllocator` (`tests/Puck.Vulkan.Tests/VulkanIndirectCountLawTests.cs`
  and four more; [refactoring campaign](../plans/open-items.md#refactoring-campaign)).
- The Superellipsoid's CPU and GPU formulas disagree at small radii
  (`SdfFieldEvaluator.SdfSuperellipsoid` against
  `mapCore`), and the box cull is written in C# and HLSL
  (`SdfMeshCull.Keeps`, `sdf-mesh-selection.hlsli`).
- The irradiance probe gather is written in C# (`SdfMeshIrradiance.Select` and
  `Gather`, `SdfMeshProbeDepth.Visibility`) and HLSL (`sdfMeshProbesAt`,
  `shade/sdf-mesh-irradiance.hlsli`). `SdfMeshIrradianceGatherDeviceLawTests`
  closes the pair: it runs both on one published volume and requires the same
  slots and weights on both backends.

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
  (`src/Puck.World.Schema/WorldSessionResolver.cs`) mints the generation ids
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

- The chase camera's clearance sweeps and the effectors' surface probes each
  kept a thread-static ring keyed on float bits, so a boom drifting a tenth of
  a millimetre missed every frame and a ring was flushed by answers that never
  matched again. The fix holds each answer on its owner (`WorldFieldAnswer`), and a
  clearance sweep is cast a margin wider so its answer stands for any drift
  within that margin.

**Closed by** keying on the resolution the answer is good for (a bound the
answer was computed under, or a quantized key), holding the answer beside its
owner rather than in a shared ring, or deleting a cache whose hit rate does
not pay; a counter of reused and marched answers shows which.

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
