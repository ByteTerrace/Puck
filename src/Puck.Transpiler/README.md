# Puck.Transpiler

The `.puck` authoring language's core, with no knowledge of any particular document schema. It turns source text
into a syntax tree, formats it, resolves its imports, and reports diagnostics against source spans. What the tree
MEANS is a document vocabulary's business: `Puck.World.Transpiler` is the one that lowers it to
`puck.world.def.v1`.

The split exists so a vocabulary can be authored beside the document it describes. `Puck.World.Transpiler`
references `Puck.World.Schema`; `Puck.GamingBricks.Transpiler` references `Puck.GamingBricks.Forge`. Neither could
live in the other'''s package. A vocabulary is a PEER of this project, not a plugin registered into it: it owns its
own emitter and supplies `IDocumentVocabulary` for the two questions generic value lowering cannot answer.

## What is here

| Folder | Owns |
|---|---|
| `Ast/` | The syntax tree: documents, blocks, properties, expressions, rules, predicates, effect statements. |
| `Parsing/` | `PuckParser` (partial, over Parlot): the document reader, its operand scanner, its predicate and rule-body readers. |
| `Diagnostics/` | `Diagnostic`/`DiagnosticBag`/`SourceSpan`/`SourceMap` and the `PUCK…` code constants. |
| `Formatting/` | `PuckFormatter`: the canonical text layout, applied to source rather than to a tree. |
| `Modules/` | `ModuleResolver`: `import` resolution and alias composition. |
| `Units/` | `UnitDimension`/`UnitConversion`: what `deg`, `rad`, `s`, `ms`, `m`, `cm`, `mm`, `hz`, `%` are WORTH. |
| (root) | `CompilationResult<T>`, and `PuckDslVocabulary` — the DSL's spelling of `Puck.State`'s comparison and cell-kind enums. |

## Array builtins and lambdas

`range`, `map`, `filter`, `reduce`, `length` and `concat` are evaluated WHILE LOWERING, so a document carries the
array they produced and never the call. A lambda is `item => expression` or `(running, item) => expression`, and is
an argument to one of those builtins and nothing else — there are no first-class functions.

```
arrays [
    { name: "field", initial: map(range(0, 180), i => 0) }
    { name: "speeds", initial: map(range(0, 21), level => 48 - (level * 2)) }
]
```

`map` and `filter` pass the item and, optionally, its index. `reduce` takes the running value and the item.
A lambda parameter shadows a constant of the same name for the length of one application.

The builtin names are reserved in VALUE position only: a statement-position call of the same name belongs to its
vocabulary (a cartridge's `map(row:, column:, tile:)` rule step is untouched).

Value expressions carry `+ - * / %`, the six comparisons, member access, calls, arrays, objects, ranges and colours.
A comparison yields a boolean, which is what `filter`'s lambda is written to return.

## The one engine it names

`Puck.State`, and only for the expression language: an operand span is handed to `ExpressionSpelling.TryParse`
rather than re-parsed here, so the DSL and a compiled rule can never disagree about what `a + b * c` means.
`PuckDslVocabulary` is the same choice for the comparison and cell-kind enums — one table, read by the parser, the
emitter, the decompiler and the language server alike, instead of four hand-typed lists of member names.

## Units are split in two on purpose

A millisecond is a thousandth of a second wherever it is written, so the arithmetic lives here. WHICH of a
document's fields is a time is that document's own vocabulary — `WorldDocumentEmitterUnits.Classify` says
`delaySeconds` is a `Seconds`, and hands the number to `UnitConversion` to divide. A second vocabulary adds its own
classification and reuses the same arithmetic; it never restates that `ms` divides by 1000.

The one place the core itself asserts a dimension is `schedule <row> in 250ms`, where the grammar — not a field
name — says the operand is a time.

## Verification

`tests/Puck.World.Transpiler.Tests` covers parser, formatter, linter, emitter, decompiler and LSP together against
the world vocabulary, because that is the vocabulary that exists; it is the regression net for changes here.
