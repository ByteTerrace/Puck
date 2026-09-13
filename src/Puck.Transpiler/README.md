# Puck.Transpiler

The `.puck` authoring language's core, with no knowledge of any particular document schema. It turns source text
into a syntax tree, formats it, resolves its imports, and reports diagnostics against source spans. What the tree
means is defined by its document vocabulary: `Puck.World.Transpiler` is the one that lowers it to
`puck.world.def.v1`.

The split exists so a vocabulary can be authored beside the document it describes. `Puck.World.Transpiler`
references `Puck.World.Schema`; `Puck.GamingBricks.Transpiler` references `Puck.GamingBricks.Forge`. Neither could
live in the other's package. A vocabulary is a peer of this project with its own compilation entry point: it owns its
own emitter and supplies `IDocumentVocabulary` for the two questions generic value lowering cannot answer.

## Structure

| Folder | Owns |
|---|---|
| `Ast/` | The syntax tree: documents, blocks, properties, expressions, rules, predicates, effect statements. |
| `Parsing/` | `PuckParser` (partial, over Parlot): the document reader, its operand scanner, its predicate and rule-body readers. |
| `Diagnostics/` | `Diagnostic`/`DiagnosticBag`/`SourceSpan`/`SourceMap` and the `PUCK…` code constants. |
| `Formatting/` | `PuckFormatter`: the canonical text layout, applied to source rather than to a tree. |
| `Lowering/` | `DocumentLowering` (values, arithmetic, indexing, `for`/template expansion), `DocumentScope`, `DocumentValueComparer`, and `DocumentScalars`/`DocumentBuiltins`—the scalar and collection function vocabulary. |
| `Modules/` | `ModuleResolver`: `import` resolution and alias composition. |
| `Units/` | `UnitDimension`/`UnitConversion`: what `deg`, `rad`, `s`, `ms`, `m`, `cm`, `mm`, `hz`, `%` represent. |
| (root) | `CompilationResult<T>`, and `PuckDslVocabulary`—the DSL's spelling of `Puck.State`'s comparison and cell-kind enums. |

## `for`, over an array known at compile time

```puck
for i in range(0, 12) {
    shape Superellipsoid $"braid-0-{i}" {
        parent: braid0Parent[i]
        position: braid0Position[i]
        rotation: braid0Rotation[i]
    }
}
```

`for (name, i) in [...]` also binds the element's ordinal:

```puck
for (leg, i) in ["front-left", "front-right", "rear-left", "rear-right"] {
    shape Cylinder $"leg-{leg}" {
        id: 40 + i
    }
}
```

A `for` is a document-only construct—`DocumentLowering.ExpandFor` runs it while lowering and emits its body once
per element, in place; the document carries the statements it produced and never the loop. `Item` and `Index` are
bound as locals for the length of one iteration and are gone once it ends, so they never leak into what follows the
loop and never collide with a `let` constant of the same name declared outside it. The sequence itself is an array
known at compile time or the `for` is refused (PUCK044). This is distinct from a cartridge rule's `repeat`, which is
a loop the machine runs at play time with a compile-time count in 1..255.

## Strings: plain, interpolated, raw

```puck
"braid"                    // plain — never interpolates, whatever it contains
$"braid-{leg}-{i}"          // interpolated: {expr} holes; {{ and }} for a literal brace
"""
    multi
    line
    """                      // raw — no escape processing at all
$"""
    {title}
    """                      // raw AND interpolated
```

A `$` prefix is what turns either string form into an interpolation; a plain `"..."` never reads a `{` specially, so
every string written before interpolation existed still means what it did. `{{`/`}}` are the only escape an
interpolated string has, standing for one literal brace each—there is no other escape character, so a raw string
can hold anything up to its own closing fence.

A raw string runs to the next `"""` with no escape processing. When the opening fence ends its own line and the
closing fence begins its own line, the CLOSING fence's own indentation is stripped from every content line and the
bracketing newlines are dropped, so the text reads at the indentation it is authored at rather than the
surrounding block's.

## Indexing

```puck
rows[1]
named["beta"]
```

An array takes a whole-number index; an object takes a string key. Either must resolve at compile time—a
non-array/object target, a fractional or unknown ordinal, or an ordinal outside the array is PUCK043.

The `[` must be ADJACENT to what it indexes, with no line break between them: an array literal's own elements are
newline-separated with no comma, so a `[` opening the next line is always the next element, never an index on the
line above.

```puck
let grid = [
    [0, 1]      // element 0 of grid
    [2, 3]      // element 1 of grid — NOT an index into element 0
]

grid[1][0]      // indexing: adjacent brackets on one line -> 2
```

## Scalar functions come from `Puck.State`, not from here

`squareRoot`, `sine`, `hexIndex`, `mortonIndex`, `greatestCommonDivisor`, `select`, and every other named scalar
function are not declared in this project. They come from `Puck.State.ExpressionVocabulary`, a projection of
`ExpressionOperators`—the SAME table the rule language (the one a compiled rule runs, over `FixedQ4816`/`Int`
state cells) is built from. One table, two evaluators, so a spelling can never mean one thing in a rule and
something else, or nothing, in a document.

The two evaluators fold a name differently, on purpose:

- A function defined over integers (the bit, lattice, grid, and combinatorial family—
  `greatestCommonDivisor`, `leastCommonMultiple`, `remainder`, `binomialCoefficient`, `primeAt`, `setBitCount`,
  `hexIndex`, `mortonIndex`, `hilbertIndex`, `pairMinimum`, `pairMaximum`, and the rest) is evaluated by the engine's
  own `ExpressionArithmetic` rather than reimplemented here. Integers are exact in both evaluators, so delegating
  makes a numeric disagreement between a rule and a document impossible rather than merely unlikely.
- Everything else the document language can fold—`absolute`, `ceiling`, `clamp`, `floor`, `maximum`, `minimum`,
  `round`, `sign`, `sine`, `cosine`, `squareRoot`—uses `double` for fractional operations, while whole-number
  inputs to integer-preserving functions retain their signed 64-bit precision. Routing an authored `0.7071068` through the
  rule language's `Q48.16` fixed point to agree with it bit-for-bit would quantize the authored value to 1/65536
  for no gain: the folded number is document DATA, baked into JSON at compile time, and never simulation state. The
  double fold already agrees with the fixed-point one exactly on whole numbers.
- `select(condition, whenTrue, whenFalse)` reads its condition first and lowers only the arm it takes—the other
  arm is never evaluated, which is what lets an untaken arm reference something the taken one does not need.

A vocabulary name the document language cannot fold this way is refused BY NAME (`the rule language evaluates
'<name>'; the document language does not fold it`), never reported as unknown—today every declared function is
either integer-delegated or explicitly foldable in double, so the refusal is the guard for the next fixed-point-only
or otherwise non-foldable addition to the shared table, not a currently reachable name.

## Collection builtins and lambdas

`range`, `length`, `concat`, `map`, `filter`, `reduce`, `distinct`, `sort` and `groupBy` are the document language's
OWN vocabulary—a rule has no arrays to fold, so these exist in one language only, and are evaluated WHILE
LOWERING: a document carries the array they produced and never the call. A lambda is `item => expression` or
`(item, index) => expression` (`reduce`'s is `(running, item) => expression`), and is an argument to one of these
builtins and nothing else—there are no first-class functions.

```puck
arrays [
    { name: "field", initial: map(range(0, 180), i => 0) }
    { name: "speeds", initial: map(range(0, 21), level => 48 - (level * 2)) }
]
```

| Builtin | Reads | Lambda |
|---|---|---|
| `range(start, count)` | two whole numbers |—|
| `length(value)` | an array, object, or string |—|
| `concat(a, b, ...)` | any number of arrays or values |—|
| `map(array, lambda)` | an array | the item, or the item and its index |
| `filter(array, lambda)` | an array | the item, or the item and its index; kept when the result is truthy |
| `reduce(array, seed, lambda)` | an array and a seed | the running value and the item |
| `distinct(array)` | an array |—|
| `sort(array)` / `sort(array, lambda)` | an array | the item, projecting the key sorted by |
| `groupBy(array, lambda)` | an array | the item, or the item and its index, projecting the key grouped by |

`distinct` keeps the first occurrence of each value and drops the rest, comparing by VALUE rather than by
reference—two separately written `[0, 1]` literals are one element. `sort` is stable and total over a mixed
array: numbers order before strings before booleans before everything else (arrays, objects, absence), so a
comparison between two values with nothing in common between them is a no-op rather than a refusal that only
fires on the second element it sees. `groupBy` yields an object keyed by the lambda's result, in first-seen key
order, each value the array of items that produced it.

`map` and `filter` pass the item and, optionally, its index; `reduce` takes the running value and the item.
A lambda parameter shadows a constant of the same name for the length of one application.

The builtin names are reserved in VALUE position only: a statement-position call of the same name belongs to its
own vocabulary (a cartridge's `map(row:, column:, tile:)` rule step is untouched).

## Comparisons are 1 or 0

Value expressions carry `+ - * / %`, the six comparisons, member access, indexing, calls, arrays, objects, ranges
and colours. A comparison yields `1` or `0`, never a JSON boolean—the rule language has no boolean either, so
`cleared + (row > 0)` means the same thing on both sides of the compiler. `filter`'s lambda, `select`'s condition,
and a `for`'s own reads are all read back through the same truth test: a number is true when non-zero, a string
when non-empty, a present container is true, and an absent value is false.

## Names are spelled out

`absolute`, `squareRoot`, `ceiling`, `minimum`, `maximum`, `remainder`, `greatestCommonDivisor`,
`leastCommonMultiple`, `setBitCount`, `binomialCoefficient`, `primeAt`, `sine`, `cosine`, `hexIndex`, `mortonIndex`,
`hilbertIndex`, `pairMinimum`, `pairMaximum`—every name in the shared vocabulary is spelled out in full, never
abbreviated. Puck prefers function syntax and a verbose, explanatory name over an operator glyph or an
abbreviation wherever the two would otherwise compete.

This is why there is no `//` integer-division operator: `//` opens a line comment (`PuckWhiteSpaceParser` skips it
everywhere, not only at statement starts), so `a // b` can only ever read as `a` followed by a comment. `floor(a /
b)` is the spelling — the same `floor` the rule language has, with the same rounding, rather than a second
operator invented here for one case.

## State engine dependency

`Puck.State`, for the expression language on both of its seams. An operand span inside a `when`/`bind`/effect
expression is handed to `ExpressionSpelling.TryParse` rather than re-parsed here, so the DSL and a compiled rule can
never disagree about what `a + b * c` means; a scalar function call in ordinary value position is handed to
`ExpressionVocabulary`/`DocumentScalars` for the reasons above. `PuckDslVocabulary` is the same choice for the
comparison and cell-kind enums—one table, read by the parser, the emitter, the decompiler and the language server
alike, instead of four hand-typed lists of member names.

## Unit conversion boundaries

A millisecond is a thousandth of a second wherever it is written, so the arithmetic lives here. WHICH of a
document's fields is a time is that document's own vocabulary—`WorldDocumentEmitterUnits.Classify` says
`delaySeconds` is a `Seconds`, and hands the number to `UnitConversion` to divide. A second vocabulary adds its own
classification and reuses the same arithmetic; it never restates that `ms` divides by 1000.

The one place the core itself asserts a dimension is `schedule <row> in 250ms`, where the grammar—not a field
name—says the operand is a time.

## Verification

The [DSL release plan](../../docs/plans/dsl-release-hardening.md) preserves the semantic-consistency decisions, regression combinations and combined World release evidence required before further language growth.

Lowering keeps compile-time values immutable and borrows cached arrays for reads. Indexing one element does not
copy its containing array; a copy is made when a value enters output. `distinct` uses structural hashing and
retains the first occurrence. Objects compare independently of property order, and integer comparison, sorting,
group keys and integer functions preserve values above 2^53.

Constants and templates have lexical definition scopes. Template arguments capture the caller's loop or lambda
bindings; defaults can refer to earlier parameters. Each constant's unit conversion is checked separately for
each destination field. Duplicate declarations, missing or duplicate arguments, unknown templates, cyclic
bindings, integer overflow and non-finite numeric results produce diagnostics. Convenience compilation methods
throw when lowering reports errors, so callers cannot accidentally publish partial output.

One compilation admits at most four million source characters, one million elements per collection, 64 levels
of source nesting or active evaluation, and four million work units across evaluation, expansion and output
copies. Copied strings charge their length. These bounds are checked before bulk allocation; `PUCK047` asks the
author to split excessive generated data. Both vocabulary emitters accept a cancellation token. Raw and
interpolated string contents survive formatting unchanged, including indentation and blank lines. Nonempty objects expand their opening brace, members, and closing brace onto separate lines; scalar arrays and vectors stay compact. Formatting defaults to two spaces per level. The editor honors its `tabSize` and `insertSpaces` formatting options; `puck fmt --indent-size 4` or `--tabs` supplies an explicit CLI override. The CLI applies the same default style to newly decompiled sources.

There is no test project of this project's own: `tests/Puck.World.Transpiler.Tests` (parser, formatter, linter,
emitter, decompiler and LSP against `puck.world.def.v1`) and `tests/Puck.GamingBricks.Transpiler.Tests` (compile and
decompile against `puck.cartridge.v1`) are the regression net for a change here—a core change is proven by
whichever of the two vocabularies it reaches.

## Documentation

📚 [Engine manual](../../docs/README.md) · 🛠️ [Development](../../docs/development/README.md)
