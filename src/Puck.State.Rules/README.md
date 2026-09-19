# Puck.State.Rules

Puck.State.Rules compiles authored `Rule` rows into a program and runs it over a
`StateArena`. A compiled rule carries its gate, its bindings, its effects, and
the `RuleNeeds` the compiler read off its own facts; the evaluator fires it
through one host, which is both the reader every operand answers from and the
mutation door every write installs through.

Four properties hold across every compiled shape:

- **Ordinal addressing.** A read or write addresses a `(row ordinal, CellKey)`
  pair. A row or key name leaves the compiler only inside a `Describe`
  spelling, so nothing on the tick path resolves a name.
- **One comparison semantics.** `compareState` and `compareValue` lower through
  `RuleCompiler.LowerConstantComparison`, which states the literal-against-cell
  conversion once: `x > 1.5` is `x >= 2`, `x < 1.5` is `x <= 1`, `x == 1.5`
  never holds, and `x != 1.5` always does.
- **Needs are a reading of the declaration.** A fact's facet comes from its type
  argument, a host-owned row from the catalog descriptor a read resolves to, and
  irreversibility from the effect kind — never from a member a fact author fills
  in. A top-level arm that cannot be rewound is listed in
  `CompiledRule.Deferred`; one inside a branch or savepoint is deferred by its
  own `Needs`, since the composite itself stays inside the firing's scope.
  Either way, a later effect of the same firing that reads what it wrote is
  refused.
- **A firing is atomic.** One gate opening for one iteration binding is one
  arena journal scope: every reversible effect lands in it, the scope commits
  when they all succeed and rewinds on the first refusal with one counted
  refusal naming the effect. A `transaction` is a nested savepoint, so a refused
  step leaves earlier siblings standing and runs `onFailure` in the firing's own
  scope; with no `onFailure` the refusal propagates and the firing rewinds as
  one. An irreversible arm is queued during the scope, checked against the state
  the firing proposes to commit, and fired only after the commit.
- **A transform has one implementation.** Every `StateTransform` case resolves
  once into an `ArenaTransform` and applies through `ArenaTransforms.TryApply`
  over the arena, whether the caller is the evaluator, a search judge, a
  console, or a host's own command path. A transform writes inside whatever
  journal scope its caller has open — the scalar kernels open none of their own
  and the vector kernels open one that nests inside it — so a refused transform
  is rewound by the firing that submitted it and a random selection's draw is
  consumed only when that scope commits. A reorder (`sortKeyed`, `shuffle`)
  addresses a row whose order is its own, a keyed row or an ordered zone; a
  board's position is a topology cell and a ring's is a slot, so naming one is
  refused where the transform is authored.

## What a caller reaches for

| Type | What it is |
|---|---|
| `RuleCompiler` | The compile surface: `Compile`, `CompileAll`, `CompileGroups`, and every piece a document project composes from. |
| `RuleCompileContext` | What names resolve against, and the per-compile scope the resolvers read. A document project derives its own. |
| `RuleVocabulary` | The four families a document project registers: operands, keys, effects, predicates. |
| `CompiledRule` | One compiled rule: gate, effects, bindings, needs, deferred arms, zone table. |
| `CompiledRuleGroup` | A fixpoint or staged group over compiled rules: members, write set, pass ceiling, trigger, step policies. |
| `RuleRefusal` | Every refusal the state-neutral compiler raises, tagged under the `state.rule.compile` door. |
| `RuleEvaluator` | The run surface: `Evaluate`, `EvaluateRule`, `EvaluateOnce`, `EvaluateGroups`, the trace, and the refusal ledger. |
| `ArenaEffectHost` | An `IEffectHost` for rules that read and write the arena and nothing else; a document project derives it or implements the interface itself. |
| `RuleLatch` / `RuleGroupState` | Edge history and group progress: simulation state, hashed, flattened and restored. |
| `RuleEffectRefusal` | Every refusal the evaluator raises while firing, tagged under the `state.rule.fire` door. |
| `ArenaTransform` / `ArenaTransforms` | A `StateTransform` resolved to ordinals and interned keys, and the one kernel per case that applies it. `RuleCompiler.TryResolveTransform` is the resolution door; `IArenaTransformHost` is what a host serves so a `transformState` effect can fire. |
| `TransformRefusal` | Every refusal the eleven scalar transforms raise, tagged under the `state.transform` door; the four vector transforms pass `Puck.State.Vectors`' own catalogued codes through. |

## Documentation

- [State and rules](https://github.com/ByteTerrace/Puck/blob/main/docs/reference/state.md) — the row, cell, and rule model a compiled program reads.
- [Compile and run rules](https://github.com/ByteTerrace/Puck/blob/main/docs/reference/state/rules.md) — the authored spellings this compiler lowers.
- [Development and verification](https://github.com/ByteTerrace/Puck/blob/main/tests/Puck.State.Rules.Tests/README.md).
- [License](https://github.com/ByteTerrace/Puck/blob/main/LICENSE.md) and [commercial licensing](https://github.com/ByteTerrace/Puck/blob/main/LICENSING.md).
