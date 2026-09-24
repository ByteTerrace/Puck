# Puck.State.Rules.Tests

This suite exercises the rule compiler in `Puck.State.Rules`. Its law cases
cover what a compiled program addresses (a row ordinal and an interned cell key,
with authored names only inside a read-back spelling), the one conversion table
`compareState` and `compareValue` both lower a literal through, the needs a rule
folds from its own facts, which arm a firing defers and which it refuses a read
of, the counted refusal every malformed bracketed row position earns,
rule-group compilation in both shapes, the one vector operand grammar the four
vector spellings read through, and one producing site per declared refusal.

It also exercises the evaluator: one journal scope per firing with commit on
success and rewind on the first refusal, a `transaction` as a nested savepoint,
an irreversible arm queued and checked before the commit and fired after it,
Level against Edge triggering, the keys an iteration binds, the latch's hash and
checkpoint round trip, both rule-group shapes, and zero allocation per
evaluation after warm-up.

The refusal suite reads the `RuleRefusal` enum itself, so a member added with no
authored rule that reaches it fails the suite rather than passing unnoticed.

The transform laws cover each kernel's own semantics and refusal code over the
arena, the row shapes a reorder may address, the four vector transforms fired
through the same entry point, and a per-firing allocation ceiling for every
scalar case — measured one transform at a time, so one that starts rebuilding a
record cannot hide behind the others.
`TransformRowShapeLawTests` also pins the unified authored sort: own-value and
attribute keys select their kernel at compilation, preserve direction, and
refuse empty, duplicate, or mixed own-value/attribute keys.
`ArrangementRankLawTests` checks that an ordered zone ranks the same off its
document row and off the arena for every arrangement of a six-token domain.
`PatternLiveWordLawTests` checks that a `$match` word reads each letter's live
value at the evaluation's time, through an attribute row whose cells advance and
over a token-keyed row whose own cells advance. `TransformLiveReadLawTests`
checks the same for the transforms that read cell values: both sort kernels
order by live keys, `arrange` takes a live rank, `writeSet` a live mask, and
`observe` reads each token's live position and live source value and remembers
it through the live write. Both fixtures are checked against the world
validator through `WorldAdmission`, so every trait they rely on sits where a
world document may author it; the search suite links the same helper.

## Running

```powershell
dotnet test tests/Puck.State.Rules.Tests/Puck.State.Rules.Tests.csproj -c Release
```

## Documentation

📚 [State and rules](../../docs/reference/state.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
