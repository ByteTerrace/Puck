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
| PUCK040 | Error | A colon in front of a block or a list (`host: {`, `variables: [`). | Drop the colon: `host {`, `variables [`. |
| PUCK123 | Error | A section block holding only its rows (`kits { rows [ … ] }`). | Write the array of them: `kits [ … ]`; a section that also authors a policy member keeps its block. |
| PUCK121 | Error | An import spelling a file (`import "skies.puck"`, `import "theme.world.json"`). | Name what the file carries: `import "skies"`. A library's declarations or a document's composition follows from the named file, never the spelling. |
| PUCK002 | Error | Operand text an effect/`when`/`score` position handed to `Puck.State.ExpressionSpelling` failed to parse. | Fix the operand's own syntax — it is the rule language's expression grammar, not this document's. |
| PUCK024 | Error | A value carrying a physical dimension in a field the vocabulary's dimension table doesn't cover. | Drop the unit, or confirm the field name is the one the vocabulary actually declares. |
| PUCK025 | Error | A value whose dimension is not its field's (`radius: 2m * 3m`, `angle: 4m/s`), or a unit no suffix spells. | Fix the arithmetic or the unit; the message names both dimensions. |
| PUCK137 | Error | Two dimensions meeting where they must agree: `2m + 3s`, a comparison, a choice's arms, `minimum`'s arguments, `cosine(2m)`, a `let`'s value against its annotation. | Make the two agree; a bare number is free and never mismatches. |
| PUCK036 | Error | A statement inside `placements`/`prototypes` (or another section) that section's grammar has no case for. | Check the section's own grammar — an unrecognized statement is refused, never silently dropped. A statement that did not parse draws its parse fault alone, never PUCK036 as well. |
| PUCK037/038 | Error | Control flow or a call-form gate in a vocabulary whose rule shape cannot carry it. World rules admit `if` and compound assignment; `repeat`/`break` still fire PUCK037. | Iterate with `forEach`, unroll a fixed count with a compile-time `for`, or use a gate the vocabulary admits. |
| PUCK041 | Error | A compile-time call refused at the call: a collection builtin (`map`/`filter`/`reduce`/`range`/`length`/`concat`/...) or a scalar function called with arguments that can't be evaluated at compile time, or a name that is neither a compile-time function nor a document arm (`min`; the message names `minimum`). | Make every argument resolve to a compile-time value — no runtime state inside a builtin call; spell a function exactly as the vocabulary names it, with no abbreviation. |
| PUCK042 | Error | A lambda written where no builtin takes one. | A lambda is only ever an argument to one of the collection builtins. |
| PUCK043 | Error | An index (`a[i]`) that can't resolve at compile time — non-array/object target, fractional/unknown ordinal, or out of range. | Index a compile-time array/object with a whole-number or string key known at compile time. |
| PUCK044 | Error | A `for`'s sequence isn't an array known at compile time. | Bind the sequence to a `let`/literal array, or a builtin's compile-time output, first. |
| PUCK119 | Error | A row (`slot left: Element`, `row { enum: Element }`) or a record field (`field: Element`) names an enum the composed world's `state.enums` does not hold (drawn once by validating the composed world, so `lint` reports it; a basis's enums count). | Declare it with `enum Element { … }` here or in the basis; a cell kind (`: Int`) is never written there (PUCK107). |
| PUCK122 | Warning | An animated creation's pooled `group` joins, by a blend other than `Union`, shapes with different motion owners (drawn by validating the composed world, so `compile --validate`, `lint` and `creation stats` report it). The seam stretches as they move and bakes again once their relative pose settles. | Where the motion is meant, acknowledge it on the line before the row: `allow PUCK122 "why"`. Otherwise give both shapes one owner ([sdf-authoring](../../sdf-authoring/SKILL.md#making-it-move)). |
| PUCK131 | Warning | An animated creation's ungrouped shape declares a blend other than `Union` after another shape. The pooled bake gives each ungrouped shape a group of its own, so the blend draws as a hard union where a static placement draws it (`compile --validate`, `lint` and `creation stats` report it). | Give the shape and the shapes it blends into one `group`; PUCK122 then names any of them that move apart. |
| PUCK132 | Warning | A creation's driver authors a swing, a slide or a clip pose on a shape its mask (`parts`) does not cover, so the motion never plays (`compile --validate`, `lint` and `creation stats` report it). | Add a part covering the shape to the driver's `parts`, or drop the motion ([rig](../../sdf-authoring/references/rig.md#drivers)). |
| PUCK133 | Warning | A later effector writes a shape an earlier effector's tip hangs from, so the later one, solving last, can carry the earlier tip off its target (`compile --validate`, `lint` and `creation stats` report it). | Intended when the later contact should win; otherwise reorder the effectors or split their chains ([rig](../../sdf-authoring/references/rig.md#chains-and-effectors)). |
| PUCK125 | Error | A `let`, a `template` or a template parameter declared twice in one scope; the second declaration is refused and the first stands. `lint` adds no shadow or unused finding for it. | Rename or delete one. |
| PUCK124 | Error | An `allow` line naming a code no constant declares, giving no reason, or with no statement after it to cover. | Write `allow <code> "why"` directly above the row or `let` it covers. An error cannot be acknowledged. |
| PUCK120 | Error | A graph row's bound parameter is not a `parameter pass.member = value` statement inside its `graph` block: the statement stands elsewhere, binds one member twice, or the block writes a raw `parameters { }`. | Write one `parameter <pass>.<member> = <value>` line per bound member inside the `graph` block. |
| PUCK114 | Error | A `when`, `decision` or nested `rule` inside a `for` that stands in a rule body — a rule has one gate and one decision. | Write the condition in the rule's `when` or in an `if` inside the loop; stamp rules with a `for` outside any rule body. |
| PUCK047 | Error | Compilation work, source size, collection size, or nesting depth exceeded a ceiling. | Split the generated data or source into smaller pieces. |
| PUCK_LINT_005 | Information | A `state`/`comparandState`/`fromState` name, or a `State` token in a `compareValue`/`expression`/`score` operand, resolves to no declared state row. | Check spelling against the declared `state.*[].name` rows (including any basis/import). |
| PUCK034 | Warning | A shape's `parent` resolves to no sibling `name` in the same `shapes` array. | Fix the referenced name; this check is local and always runs, even inside a module. |
| PUCK049–066 | Error | A `table`/`slot`/`pile`/`grid` declaration refusal — outside `state.world`, a mixed array/block section, a duplicate name, an invalid default, a modifier the kind or shape doesn't admit, an inexact `advance` rate, an unknown or wrong-shaped `pile`/`grid` row reference, a `pile` capacity past its token domain, a `grid` kind other than `Int`/`Bool`, an invalid `grid` cell ordinal, `grid` `inverse` combined with authored cells, or a reused topology name. | See `Puck.World.Transpiler/README.md`'s [State declarations](../../../../src/Puck.World.Transpiler/README.md#state-declarations) table for the exact code-by-code list. |

PUCK034,
PUCK_LINT_010 (a vector-mix term whose weight share is below 1/64), and
PUCK_LINT_011 (division or remainder by a potentially zero divisor in an eager
ternary arm) are Warning
severity; every other `PUCK_LINT_*` check is Information, so `lint --strict`
(which promotes warnings to failures) does not fail on an Information-level
finding by itself. A `--strict`/`compile
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
