# Diagnostics an author hits most

The full catalog is declared once, in
[`PuckDiagnosticCodes.cs`](../../../../src/Puck.Transpiler/Diagnostics/PuckDiagnosticCodes.cs),
and narrated with examples in
[`Puck.Transpiler/README.md`](../../../../src/Puck.Transpiler/README.md) and
[`Puck.World.Transpiler/README.md`](../../../../src/Puck.World.Transpiler/README.md).
Read those rather than a copy here for anything not listed below — a
restated catalog drifts the moment a code is added or reworded.

| Code | Severity | Fires on | Fix |
|---|---|---|---|
| PUCK040 | Error | A colon in front of a block or array (`host: {`, `variables: [`). | Drop the colon: `host {`, `variables [`. |
| PUCK002 | Error | Operand text an effect/`when`/`score` position handed to `Puck.State.ExpressionSpelling` failed to parse. | Fix the operand's own syntax — it is the rule language's expression grammar, not this document's. |
| PUCK024 | Error | A unit suffix on a field the vocabulary's dimension table doesn't cover. | Drop the suffix, or confirm the field name is the one the vocabulary actually declares. |
| PUCK025 | Error | A unit suffix the field's own dimension doesn't admit (e.g. `deg` on a field classified as time). | Use a unit from that field's dimension. |
| PUCK036 | Error | A statement inside `placements`/`prototypes` (or another section) that section's grammar has no case for. | Check the section's own grammar — an unrecognized statement is refused, never silently dropped. |
| PUCK037/038/039 | Error | Control flow, a call-form gate, or a compound-assignment operator in a vocabulary whose rule shape can't carry it. | Restructure to the vocabulary's admitted shape (see `grammar.md`'s cross-vocabulary table), or move the logic to a vocabulary that does carry it. |
| PUCK041 | Error | A collection builtin (`map`/`filter`/`reduce`/`range`/`length`/`concat`/...) called with arguments that can't be evaluated at compile time. | Make every argument resolve to a compile-time value — no runtime state inside a builtin call. |
| PUCK042 | Error | A lambda written where no builtin takes one. | A lambda is only ever an argument to one of the collection builtins. |
| PUCK043 | Error | An index (`a[i]`) that can't resolve at compile time — non-array/object target, fractional/unknown ordinal, or out of range. | Index a compile-time array/object with a whole-number or string key known at compile time. |
| PUCK044 | Error | A `for`'s sequence isn't an array known at compile time. | Bind the sequence to a `let`/literal array, or a builtin's compile-time output, first. |
| PUCK047 | Error | Compilation work, source size, collection size, or nesting depth exceeded a ceiling. | Split the generated data or source into smaller pieces. |
| PUCK_LINT_005 | Information | A `state`/`comparandState`/`fromState` name, or a `State` token in a `compareValue`/`expression`/`score` operand, resolves to no declared state row. | Check spelling against the declared `state.*[].name` rows (including any basis/import). |
| PUCK034 | Warning | A shape's `parent` resolves to no sibling `name` in the same `shapes` array. | Fix the referenced name; this check is local and always runs, even inside a module. |

Only PUCK034 is Warning severity; every other `PUCK_LINT_*` check is
Information, so `lint --strict` (which promotes warnings to failures) does not
fail on an Information-level finding by itself. A `--strict`/`compile
--validate` failure with no PUCK_LINT line came from parsing, lowering, or
engine-schema validation instead — read the reported code, not the lint
family, to find it.

A document with no `schema`/`basis` of its own is a **module** — a fragment
some unknown root may import — and reference-resolution lint (PUCK_LINT_005
through 009) never reports an unresolved name against it standalone, since the
name may be one the eventual root supplies. Shape-parent resolution (PUCK034)
is the one check that ignores this distinction: it is scoped to sibling shapes
in the same array, which no basis or importer could change, so it always runs
and always reports.
