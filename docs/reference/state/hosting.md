# Host and extend State

A **host** connects the state evaluator to a running application's storage,
admission rules, and persistence. An **extension vocabulary** teaches the
compiler about facts and effects that only that application can understand.
A headless card-game resolver and Puck.World can share the state engine while
making different choices at those boundaries.

## Assign each responsibility

| State supplies | The application supplies |
|---|---|
| Row and cell vocabulary, shared read/write helpers | Its document model and complete validation. |
| Compilation and evaluation of registered rules | Which rules run, their order, and the simulation tick. |
| State-neutral mutations and a preflight protocol | Admission, installation, journaling, and delivery. |
| Frame evaluation and search | Which rules are safe for candidates and how results are applied. |
| Generator mechanics | Stable seed inputs, draw-site state, and installation of draws. |
| Latch and search checkpoint structures | A consistent checkpoint of all authoritative state. |

## Implement the mutation boundary

`TryApply` has two meanings selected by `preflight`. In preflight it validates
and composes a private candidate; outside preflight it performs the application's
normal installation. Later reads within a scope must see the candidate already
composed by earlier effects.

`BeginPreflight` opens a nested scope. `EndPreflight` discards it and restores
the previous view. `TryCommitPreflight` installs the whole candidate as one
mutation; a scope containing no changes installs nothing.
The [transaction diagram](rules.md#make-several-effects-succeed-together)
shows the evaluator's use of this protocol.

Use the same admission and write composition rules for external commands and
rule-driven mutations. The evaluator compiling an effect does not grant it
authority. Apply capacity checks, value envelopes, FIFO eviction, time-trait
rebasing, and journaling through the application's chosen mutation boundary.

## Add a concept without forking the evaluator

Suppose a host wants a rule to read the distance between two bodies.
The core library has no bodies, but the host can register an operand family:

```mermaid
flowchart LR
    Family["Registered<br/>operand family"] --> Compiler["Rule compiler"]
    Compiler --> Fact["Compiled<br/>OperandFact"]
    Fact --> Evaluate["Read through<br/>the host's reader"]
    Fact --> Analyses["Report dependencies<br/>and cost"]
    Evaluate --> Gate["Common gate,<br/>latch, and trace"]
```

1. The family recognizes the reserved spelling and validates it at compile time.
2. It produces an `OperandFact` with the correct kind, read dependencies,
   cost, and host-only classification.
3. At evaluation, that fact reads through the host's extended reader interface.
4. The common gate, latch, trace, and refusal machinery uses the answer.

An effect family follows the corresponding path to an `EffectFact` and
`FireEffect`. Explicit transaction admission is part of its contract.
A compiled fact's read/write sets matter beyond evaluation: hazard checks,
work budgets, scheduling, and frame suitability depend on them. Omitting a
dependency can make a later analysis reach the wrong conclusion.

The registration and derivation points are specified below. JSON registration
belongs to the document's serializer as well as its compiler so newly authored
arms can round-trip under their declared discriminators.

## Save meaning, rebuild acceleration

A consistent host checkpoint includes current rows, time-trait state, the
simulation tick, continuing Edge latches, draw cursors and masks, and active
search progress where used. Add any host-specific authoritative state and
seed identities needed for continuation.

Compiled catalogs, rule schedules, row-version snapshots, and memoized bindings
can be rebuilt from their inputs. Keep their lifetimes aligned: a handle belongs
to the catalog that minted it, and a cached scheduling answer belongs to its
exact compiled schedule.

## Diagnose order and cost before scaling up

`RuleDataflow` describes what a rule reads and writes. `RuleHazards` identifies
order-sensitive read/write and write/write pairs. A hazard explains why moving
one rule before another could matter; it is not automatically an invalid program.

`RuleWorkBudget` estimates bounded work, including gate checks for rules whose
effects do not fire. Proven mutually exclusive gates can share effect allowance.
These are heuristic work units. They are not measured CPU cycles and do not
establish a wall-clock latency guarantee.

## How a document project extends it

Extension is by registration and derivation, never by an edit inside this
package:

- A `RuleVocabulary` registers the families a document project adds: an
  `OperandFamily` claims the reserved spellings it answers and compiles them
  to its own `OperandFact`s; an `EffectFamily` owns an `ActionEffect`-derived
  arm, its `$type` discriminator, its explicit `AllowsTransaction` admission,
  and its compile to an `EffectFact`; a `PredicateFamily` owns a predicate arm
  (which may refuse in rule scope); a `KeyFamily` answers a dynamic-key
  spelling with a `KeyFact`. Registered families are consulted before the
  library's own. `RuleVocabulary.ExtendJson` is the `JsonTypeInfo` modifier a
  document project's serializer installs so the registered arms read and
  write under their discriminators.
- A `RuleCompileContext` is derived to anchor what only the document knows
  (`FindTopology` in a placement's frame, `FindDraw` through a lattice fill)
  and to carry the per-compile scope the families cache into.
- A host implements `IRuleHost`—`IRuleReader` widened with the mutation
  door (`TryApply`, composing privately under `BeginPreflight`/`EndPreflight`
  or installing), `FireEffect` for the effect arms it registered,
  `TryEvaluateOwn` for the rule kinds only it understands (which run each
  evaluation back through `RuleEvaluator.EvaluateOnce`, so latch, trace, and
  ledger stay one mechanism), and `RefusalRecorded`—and widens the reader
  further to whatever its registered operands read through; a compiled fact
  casts the reader it is handed. A host owns one `RuleEvaluator` and one
  `RuleLatch` per family it evaluates.
- `CompiledRule` is non-sealed: a document project's rule overrides
  `CollectReads`, `CollectWrites`, and `Cost` to add the branches it alone
  carries, and every analysis follows.
- A polymorphic base declared here (`LatticeTopology`) lists only the cases
  this package owns; a document adds its own case as a derived record through
  the same modifier. `StateRow` is non-sealed on the same terms, with
  `StateRowJsonConverter<TRow>` as its converter base.

## What stays a host's

Composing a `StateMutation` against a row—eviction, a numeric upsert's
advance/dynamics rebase, envelope validation, the journal—is each host's
door; the pieces are here (`StateCellWriter`, `StateAdvance`,
`StateRow.ClampToEnvelope`), the pipeline is not. Pairwise interactions and
timed decisions are host-evaluated kinds until a pair domain over rows is
designed.

## Analyses

Compiled facts support `RuleDataflow` (a rule's read and write sets), `RuleHazards`
(the write-after-read and write-after-write pairs document order decides,
skipping pairs whose gates pin one cell to disjoint ranges), and
`RuleWorkBudget` (the pinned-cell exclusion trie, contributor lines, writer
counts, gate/expression/effect costs) over the facts' own answers. A non-board
pattern is priced by its source row's declared capacity times one match step
plus the actual per-token expression cost, with saturating arithmetic.
Its read set includes the source, attribute row, key indirection, and token
expression dependencies.

## Work budgets

Rule budgeting currently uses heuristic work units. `RuleCost` separates the
checks every candidate may perform from effects that fire, allowing mutually
exclusive effects to share a budget without discarding closed-gate work.
`CostModel` defines a portable abstract service policy; its reference operation
and memory coefficients remain uncalibrated and return `CostBound.Unmodeled`.
Heuristic weights cannot be converted to reference cycles. The
[costing brief](../../plans/abstract-machine-costing.md) describes the
portable capability baseline and evidence needed before cycle-based admission.

## Where the rest lives

The world's registered families (`WorldRuleVocabulary`, `WorldRuleCompileContext`,
`IWorldRuleReader`), its rule and decision records, and the validators live in
[`src/Puck.World.Schema`](../../../src/Puck.World.Schema/README.md); `WorldServer` is the
host (`WorldServer.RuleHost.cs` in
[`src/Puck.World.Server`](../../../src/Puck.World.Server/README.md)).
`tests/Puck.State.Tests` proves the expression syntax, the function families,
the hex topology, the evaluator, and the search runtime over a headless host
(a row list, no world); the converter and schema facts that need the world
document's JSON context stay in `tests/Puck.World.Schema.Tests`. The `search`
document section (`WorldSearchSection`/`WorldSearchRow`/`WorldSearchShape`)
and the compiler that resolves it into a `SearchPlan` (`WorldSearchCompilation`,
reading the document's placements and topologies to fill in what a row leaves
implicit) live in `src/Puck.World.Schema`; `WorldServer` holds the runtime and
translates its landed `SearchWrite`s into the world's own mutation door
(`WorldServer.cs`, `WorldServer.Step.cs` in
[`src/Puck.World.Server`](../../../src/Puck.World.Server/README.md)); the game laws that
boot a server stay in `tests/Puck.World.Tests`.

---

[State and rules](../state.md) · [State test suite](../../../tests/Puck.State.Tests/README.md)
