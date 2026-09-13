# State consolidation

The state duplication pass on 2026-09-06 identified the opportunities below.
Several foundations now live in the owning projects; this plan proposes the
remaining consolidation while preserving the distinctions that authors and
runtime hosts can observe.
Expression-key binding reuse, shared board combination, one transaction effect
vocabulary, operator metadata, effect source reads, reduction accumulation,
trait field validation, and validation-to-install compilation reuse now live in the owning
[State](../../src/Puck.State/README.md) and
[World Schema](../../src/Puck.World.Schema/README.md) implementations.
The remaining proposals preserve author expression, deterministic evaluation,
and authority and transaction boundaries.

Author conveniences can lower into a smaller common representation. Removing
an operator merely because an author could emulate it with several rules can
increase program size, exceed a work limit, or change atomicity.

## 1. Share value sources and comparison machinery without erasing semantics

[WriteEffect and PushStateEffect](../../src/Puck.State/EffectFact.cs) carry
literal, operand, and expression alternatives. `IValueSourcedEffect` already
shares analysis and single-pass effect source resolution, but
[write resolution](../../src/Puck.State/RuleCompiler.Effects.cs) and
[gate evaluation](../../src/Puck.State/RuleEvaluation.cs) still branch over
separate source representations. `GateToken` likewise carries both operand
comparisons and a pair of expression programs.

A common compiled value source can represent a literal, an operand, or an
expression, with shared reads, dependencies, pricing, and failure results.
Keep useful literal fast paths and carry typed facts through the common path.
The existing `RuleFact` distinctions are part of the contract.

**Executed counterexample to a naive syntax collapse:** for an integer slot
containing zero, `compareState` with `< 0.5` returns true, whereas Int
`compareValue` with expressions `pick` and `0.5` returns false. The former
[lowers the fractional threshold exactly](../../src/Puck.State/RuleCompiler.cs);
the latter rounds the numeric literal to the expression's integer domain.

Other distinctions a common representation must retain:

- A fact comparison can compare `forever`; numeric expression evaluation
  refuses it.
- An absent zone endpoint makes a direct comparison false, including
  `NotEqual`; an expression reading it reports an absence fault.
- A direct copy from an absent or forever fact does not fire. An expression
  source has diagnostic failure behavior.
- Text copies are not numeric expressions.
- `countdownState` consumes runtime engine-step width and handles the final
  partial step; `scheduleState` writes an absolute simulation-tick deadline
  with an upward-rounded delay.
- Add and set differ when a cell is missing and when a cycling cell stores
  a phase but reads a transformed value.

Lower author spellings into a common implementation while retaining these
policies. Replacing every operation with an ordinary arithmetic expression
would not preserve the current language.

## 2. Consolidate slot and keyed-cell behavior handling

[StateRow and StateCell](../../src/Puck.State/StateRow.cs) both carry advance,
dynamics, and cycle traits. The scalar trait lives on the row; the keyed trait
lives on its cell. That distinction recurs in
[StateReader](../../src/Puck.State/StateReader.cs),
[row conversion](../../src/Puck.State/StateRowJsonConverter.cs),
[validation](../../src/Puck.World.Schema/WorldDefinitionValidator.State.cs), and
write/rebase logic.

Resolve a cell's effective behavior once into a shared view, and reuse its
validation and rebase operations. A larger representation change could put
the behavior on one compiled cell descriptor while keeping the scalar JSON
spelling compact. Preserve a trait declared before its slot has any value:
normalization must not manufacture a zero-valued cell that changes existence
or initial-fill behavior.

Only the location and handling are duplicated. Advance, cycle, and dynamics
describe different computations. Visibility at row and cell level also has
different meaning: their restrictions compose and cannot simply replace one
another. Keep strict JSON shape validation, and share semantic checks where
both parsed documents and programmatic mutations need them.

## 3. Share the body and world gate structure, retaining host-specific leaves

[WorldBodyMotionProgram](../../src/Puck.World.Schema/WorldBodyMotionProgram.cs)
compiles a second postfix Boolean representation,
[CompiledPredicate](../../src/Puck.Physics/Motion/CompiledBodyAction.cs).
[WorldBody.ActionState](../../src/Puck.World.Server/WorldBody.ActionState.cs)
evaluates its `all`, `any`, and `not` structure separately from the state
engine's `GateToken` evaluator. Comparison operations are already shared.

Share the Boolean program structure and traversal, with body facts, recency
slots, and held-channel checks supplied as host-specific leaves. Keep ordinal
slot access and bounded storage. A full move to `IRuleReader` is worth judging
against allocation and per-body execution cost before adopting it.

The broader ownership/value distinction deserves the same treatment:
[StateValueKind](../../src/Puck.State/StateCatalog.cs) contains both `Fixed`
and participant `Counter`, alongside `Int` and participant `Timer`. Storage
encoding, units, and ownership can be separate properties of one descriptor.
That does not justify deleting timer behavior, reset facts, input latches,
durable write-back, or the body's clamp policies. This is a larger architectural
refactor than sharing the Boolean traversal.

## Smaller opportunities and deliberate distinctions

| Surface | Finding and direction |
|---|---|
| `sortZone` / `sortKeyed` | They already share `FinishSort` in [WorldStateTransforms](../../src/Puck.World.Server/WorldStateTransforms.cs). One authored sort with an own-value or attribute-key selector could remove the two frontends. Preserve stable ties, per-key direction, and token-domain validation. The runtime win is smaller than the vocabulary win. |
| World wrappers | [WorldStateRow](../../src/Puck.World.Schema/WorldState.cs) repeats the base row's constructor fields and copy forwarding; [WorldRule](../../src/Puck.World.Schema/WorldRules.cs) similarly forwards core fields. Inherited init properties or generated forwarding can remove maintenance duplication. These wrappers do not themselves imply a second state engine. |
| Board masks / board rows | A mask has at most 64 cells; board operations cover larger topologies and preserve cell values. Keep both capabilities. A loop of single-cell writes also lacks an atomic board transform's publication boundary. |
| `pushState` / transform `push` | One resolves an authored numeric source; the other carries its resolved raw mutation. They already meet at the transform door. Source handling is shared; preserve the mutation boundary. |
| `shuffle` / `arrange` | A deterministic rank-selected permutation and a draw-consuming shuffle differ in randomness, bounds, and cursor advancement. Preserve both meanings. |
| State ring / topology ring | A bounded history buffer and a spatial/cyclic adjacency structure are different domains despite the shared name. |
| Live zones / dynamic keys | Both select an address component, but live-zone gaps close the entire evaluation before the gate. Generalized addressing must retain that policy and each key's evaluation timing. |
| Bindings / state rows | A per-evaluation value and persistent, mutable simulation state have different lifetimes and observability. They are not interchangeable. |

## Further compilation reuse and verification

Mutation and reload installation reuse the exact definition's validation result.
Boot, checkpoint restore, separate hazard/budget requests, and recomposition that
changes the definition still compile fresh programs. Extending receipt propagation
to those boundaries requires explicit ownership; catalog shape alone is not a safe
cache key. Keep latches, frames, search continuations, and binding values private
to their host.

Trait placement and body/world Boolean traversal remain separate proposals.
Scalar and keyed advance/dynamics field checks now share implementations, as cycle
fields already did. Preserve their location restrictions and distinct computations.

Preserve the shipped game programs and the targeted State, Schema, and World
laws. Keep refusal and rollback controls beside successful cases, compare
frames against live composition, and check serialization when authoring types
change. Retain the fractional-comparison counterexample above: fewer internal
representations must not erase distinct author semantics.
