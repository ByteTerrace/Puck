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

## Running

```powershell
dotnet test tests/Puck.State.Rules.Tests/Puck.State.Rules.Tests.csproj -c Release
```

## Documentation

📚 [State and rules](../../docs/reference/state.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
