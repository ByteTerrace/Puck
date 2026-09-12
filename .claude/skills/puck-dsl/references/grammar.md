# `.puck` core grammar

The authoritative source is
[`Puck.Transpiler/README.md`](../../../../src/Puck.Transpiler/README.md); this
reference distills it for quick lookup while authoring. Verify a surprising
edge case against that README or the parser itself before relying on it.

## Documents and blocks

```
identifier [target] [name] { statements }   // a structural block; up to two leading tokens name it
identifier [ elem, elem2 ]                    // an array property
identifier: expression                        // a scalar property
identifier(k: v, k2: v2)                      // a call-expression statement
identifier                                    // a bare flag statement, only where nothing else could follow
```

A container value is always a block and a scalar always takes a colon, at
every depth and inside an object literal too — a colon in front of `{`/`[` is
**PUCK040**. A name that merely stands for a container keeps its colon
(`origin: bounds`), because the rule is about the punctuation in front of a
literal, not what the value turns out to be; a call's named argument keeps its
colon too (`worldPoint(point: [0, 1, 0])`), being call syntax rather than a
statement.

## `let`, `template`, `import`/`export`

```
let name = expression                          // compile-time constant, lexically scoped
template name(param, param2 = default) { }     // parametric block; defaults may reference earlier params
import "path" [as alias]
export read|action|binding name, name2         // names may wrap across indented lines
```

Each `let`'s unit conversion is checked separately per destination field.
Duplicate declarations, missing/duplicate template arguments, unknown
templates, cyclic bindings, integer overflow, and non-finite results all
produce diagnostics. A `let`/`for` name resolves to its bound value wherever it
appears in an expression, so it is never mistaken for a machine or state name.

## Units

`deg rad s ms m mm cm hz % pct` — what each is worth (the conversion
arithmetic) lives in the core; *which* field of a given vocabulary is
dimensioned is that vocabulary's own table. A unit suffix on a field the table
doesn't cover is **PUCK024**; a unit the field's own dimension doesn't admit is
**PUCK025**. The one place the core itself asserts a dimension regardless of
vocabulary is `schedule <row> in <N><unit>`, where the grammar — not a field
name — says the operand is a time, and the unit must be one the seconds
dimension admits. `schedule ... in` needs a literal number-with-unit at that
position; a bare identifier there (even one bound by `let` to a seconds value)
is a parse error, not a unit-conversion one.

## Strings

```
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

A raw string's closing-fence indentation is stripped from every content line
when the opening fence ends its own line and the closing fence begins its own
line, so the text reads at the indentation it is authored at.

## Indexing

```
rows[1]
named["beta"]
```

An array takes a whole-number index, an object a string key; either must
resolve at compile time or the read is **PUCK043**. The `[` must be adjacent
(no line break) to what it indexes — an array literal's own elements are
newline-separated with no comma, so a `[` opening the next line is always the
next element, never an index on the line above:

```
let grid = [
    [0, 1]      // element 0 of grid
    [2, 3]      // element 1 of grid — NOT an index into element 0
]

grid[1][0]      // adjacent brackets on one line -> 2
```

## `for`, compile-time only

```
for i in range(0, 12) {
    shape Superellipsoid $"braid-0-{i}" { position: braid0Position[i] }
}

for (leg, i) in ["front-left", "front-right", "rear-left", "rear-right"] {
    shape Cylinder $"leg-{leg}" { id: 40 + i }
}
```

`DocumentLowering.ExpandFor` runs the loop while lowering and emits its body
once per element in place — the document carries the statements produced,
never the loop. `Item`/`Index` are bound as locals for one iteration and never
leak. The sequence must be an array known at compile time or the `for` is
**PUCK044**; a body that assigns a field instead of emitting a row is
**PUCK046**; nesting past the expansion ceiling is **PUCK045**. This is
distinct from a cartridge rule's `repeat`, a play-time loop with a compile-time
count in 1..255.

## Scalar functions come from `Puck.State`

`squareRoot`, `sine`, `cosine`, `absolute`, `ceiling`, `floor`, `clamp`,
`minimum`, `maximum`, `remainder`, `greatestCommonDivisor`,
`leastCommonMultiple`, `setBitCount`, `binomialCoefficient`, `primeAt`,
`hexIndex`, `mortonIndex`, `hilbertIndex`, `pairMinimum`, `pairMaximum`,
`select`, and the rest are not declared in the transpiler — they come from
`Puck.State.ExpressionVocabulary`, the same table a compiled rule runs
against, so a name can never mean one thing in a rule and another (or nothing)
in a document:

- The integer/bit/lattice/combinatorial family delegates exactly to the
  engine's own `ExpressionArithmetic` rather than being reimplemented, so
  integers are exact in both evaluators and can never disagree.
- Everything else the document language folds (`sine`, `squareRoot`, `round`,
  ...) uses `double`; whole-number inputs to integer-preserving functions keep
  signed 64-bit precision. The folded number is document DATA baked into JSON
  at compile time — never simulation state — so quantizing it to the rule
  language's `Q48.16` fixed point would only lose precision for no gain.
- `select(condition, whenTrue, whenFalse)` lowers only the arm it takes; the
  untaken arm is never evaluated.
- A name the document language cannot fold this way is refused **by name**
  (`the rule language evaluates '<name>'; the document language does not fold
  it`), never reported as unknown.

## Collection builtins and lambdas

Evaluated **while lowering** — a document carries the array produced, never
the call. A lambda (`item => expr`, `(item, index) => expr`, `reduce`'s
`(running, item) => expr`) is an argument to exactly one of these and nothing
else; a lambda anywhere else is **PUCK042**, an unevaluable call is **PUCK041**.

| Builtin | Reads | Lambda |
|---|---|---|
| `range(start, count)` | two whole numbers | — |
| `length(value)` | array, object, or string | — |
| `concat(a, b, ...)` | any number of arrays/values | — |
| `map(array, lambda)` | an array | item, or item+index |
| `filter(array, lambda)` | an array | item, or item+index; kept when truthy |
| `reduce(array, seed, lambda)` | an array and a seed | running value + item |
| `distinct(array)` | an array | — |
| `sort(array[, lambda])` | an array | item, projecting the sort key |
| `groupBy(array, lambda)` | an array | item, or item+index, projecting the group key |

`distinct` compares by value (structural hashing), keeping the first
occurrence. `sort` is stable and total over a mixed array: numbers order
before strings before booleans before everything else. `groupBy` yields an
object keyed by the lambda's result, in first-seen key order. A lambda
parameter shadows a same-named `let` for the length of one application. The
builtin names are reserved in **value position only** — a statement-position
call of the same name belongs to its own vocabulary (a cartridge's
`map(row:, column:, tile:)` step is untouched).

## Comparisons are 1 or 0

`+ - * / %`, six comparisons (`== != < <= > >=`), member access, calls, arrays,
objects, ranges (`a..b`), colors (`#rrggbb[aa]`), unit-suffixed numbers. A
comparison yields `1`/`0`, never a JSON boolean — the rule language has no
boolean either. `filter`'s lambda, `select`'s condition, and a `for`'s reads
all use the same truth test: a number is true when non-zero, a string when
non-empty, a present container is true, an absent value is false.

## Control flow, call gates, and compound assignment are cross-vocabulary

The core parses `if`/`else if`/`else`, `repeat`/`break`, call-form gates
(`key(left, held)`), and compound assignment (`+= -= *= /= %= &= |= ^= <<=
>>=`) for **every** vocabulary. Whether a given vocabulary's rule shape can
carry them is that vocabulary's own answer:

| Code | Refusal |
|---|---|
| PUCK037 | Control flow (`if`/`repeat`/`break`) in a rule shape that is straight-line only. |
| PUCK038 | A call-form gate where the vocabulary admits comparisons only. |
| PUCK039 | A compound-assignment operator the vocabulary's effects don't carry. |

The world vocabulary's rule body is straight-line, so PUCK037 fires there;
cartridge rules support `if`/`repeat`/`break` (`rom-forge` owns that grammar).

## Compilation limits

One compilation admits at most 4,000,000 source characters, 1,000,000
elements per collection, 64 levels of source nesting or active evaluation, and
4,000,000 work units across evaluation, expansion, and output copies (checked
before bulk allocation; exceeding one is **PUCK047**). Both vocabulary
emitters accept a cancellation token. Objects compare independently of
property order; integer comparison, sorting, group keys, and integer functions
preserve values above 2^53.
