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

## Names

One rule, `Puck.State.IdentifierSpelling`, for every Puck language (document
grammar, expression spelling, pattern/cell-set/state spellings, `sql { }`):

| Class | Characters |
|---|---|
| identifier start | ASCII letter or `_` |
| identifier part | ASCII letter, ASCII digit, or `_` |
| name | identifier, optionally opened by the `$` sigil (`$value`, `$replace`) |

- `$` only opens a name: `a$b` is two tokens, `$"…"` is an interpolated string.
- Any other name is quoted: `"seat-1"` in the document grammar, `` `seat-1` ``
  in an expression. Non-ASCII names (`"größe"`) are quoted too.
- A position's reservations (statement keywords, reserved call names, pattern
  words `any`/`empty`/`except`/`none`, SQL keywords, dotted paths, `$x:seg`
  continuations) layer on the rule as that position's own set. They never add
  a character class.
- Every printer prints a name bare exactly when the bare spelling reads back as
  that name and the position doesn't reserve it. `IdentifierSpellingLawTests`
  (State) and `IdentifierAgreementLawTests` (World.Transpiler) gate it, over
  `tests/Shared/IdentifierCorpus.cs` with `tests/Shared/SpellingLaws.cs`. A new
  printer position gets a row in one of those law classes.
- Never add a `char.IsLetter` check or another identifier predicate. Call
  `IdentifierSpelling`. A document-name printer, the decompiler included, calls
  `PuckPrinter.PrintName` or `PuckPrinter.PrintPropertyName` rather than deciding
  quoting itself, and a printer of a name inside an expression calls
  `ExpressionSpelling.PrintName` (`quoted: true` for a literal row no binding may
  capture) rather than wrapping backquotes by hand.

## Row declarations: `table`/`slot`/`pile`/`grid`

```
table name [: Enum] [modifier(...)]* { key = value [modifier(...)]* ... }
slot name [: Enum] [= value] [modifier(...)]*
pile name of tokenRow [modifier(...)]* { token ... }
grid name [: Enum] [modifier(...)]* [{ key = value ... }]
```

A schema-agnostic grammar shape (`Ast/StateDeclarationNodes.cs`, `Parsing/PuckParser.StateDeclarations.cs`): a name,
an optional `: Enum` (the node's `Enum`; a cell-kind word there is PUCK107, since the kind is inferred), an optional
bare-identifier reference (`pile`'s `of tokenRow`), zero or more
`name(args)` modifier calls, and an optional `{ }` body of `key = value` cell entries (`table`/`grid`) or bare
`token` entries (`pile`). The core parses the shape only; which kind names, modifier names, and locations are legal
is the owning vocabulary's answer. `puck.world.definition.v1`'s `state.world` is the one vocabulary use today —
grammar, lowering, defaults, and PUCK049–PUCK066 refusals are in
[`Puck.World.Transpiler/README.md`](../../../../src/Puck.World.Transpiler/README.md#state-declarations).

## State SQL dialect: `sql { ... }`

```sql
sql {
    CREATE TABLE characters (
        id   TEXT PRIMARY KEY,
        hp   INT  NOT NULL DEFAULT 100 CHECK (hp BETWEEN 0 AND 100) ON OVERFLOW SATURATE,
        mana INT  DEFAULT 0 ADVANCE 5 PER SECOND
    ) CAPACITY 32;

    INSERT INTO characters (id, hp, mana) VALUES
        ('hero', 80, 50);

    DECLARE turnCount INT DEFAULT 0;

    CREATE RULE healHero ON ENTER AS
        UPDATE characters SET hp = hp + 10 WHERE id = 'hero';
}
```

Embedded hermetic SQL state dialect compiling to native Puck state rows and rules.
Refusals PUCK070–PUCK076 enforce type safety, non-empty primary keys, and exact arithmetic.

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
named.beta
"B=Y..w"[2]
```

An array takes a whole-number index, an object a string key, and a string a
whole-number index that reads one UTF-16 unit as a one-character string. An
object's member also reads as `object.member`, on a `let` or on any compile-time
object. Each must resolve at compile time or the read is **PUCK043**. The `[` must be adjacent
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
never the loop. A `for` in a `stabilize` body stamps one member rule per
element, and so does a template called there. A `for` inside a rule body, a
workflow step's body, or an effect list (`if` branch, `transaction`,
`onFailure`, `claim`, `for each`) repeats locals and effects in place, in both
vocabularies; `local $"name{i}"` names each iteration's local, and a `when`,
`decision` or nested `rule` inside it is **PUCK114**. `Item`/`Index` are bound
as locals for one iteration and never leak. The sequence must be an array known
at compile time or the `for` is **PUCK044**; a body that assigns a field
instead of emitting a row is **PUCK046**; nesting past the expansion ceiling is
**PUCK045**. A test's `when` block has its own step grammar and takes no `for`. This is
distinct from a cartridge rule's `repeat`, a play-time loop with a compile-time
count in 1..255.

## Scalar functions come from `Puck.State`

`squareRoot`, `sine`, `cosine`, `absolute`, `ceiling`, `floor`, `clamp`,
`minimum`, `maximum`, `floorModulo`, `greatestCommonDivisor`,
`leastCommonMultiple`, `setBitCount`, `binomialCoefficient`, `primeAt`,
`hexIndex`, `mortonIndex`, `hilbertIndex`, `pairMinimum`, `pairMaximum`,
and the rest are not declared in the transpiler — they come from
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
- An infix operator folds as its `ExpressionOperators` row means it over the
  reals (the rule language's `Fixed` reading, at the document's precision):
  whole operands with a whole result go through `ExpressionArithmetic`'s Int
  arm, and only a non-whole `/` of two whole numbers parts from an `Int` rule
  (`7 / 2` is `3.5`). `OperatorTableLawTests` holds both readings to the rule
  evaluator.
- `condition ? whenTrue : whenFalse` lowers only the arm it takes; the
  untaken arm is never evaluated, and an arm may hold any value.
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
builtin names apply in compile-time value positions. A call constructing a
declared document arm takes its meaning from that position: array `sort(...)`
and a world's `sort` transform coexist. Statement-position calls likewise
belong to their own vocabulary (a cartridge's `map` step is untouched).

## Comparisons are 1 or 0

The rule language's conditional (`c ? a : b`, loosest and right-associative, and
the only spelling of a choice), its infix operators (`?? | ^ & == != < <= > >= << >> >>> + - * / %`),
and its two prefix operators (`-` and `~`; there is no prefix `+`), member access, calls, arrays, objects, ranges (`a..b`),
colors (`#rrggbb[aa]`), unit-suffixed numbers. Both parsers read the infix
operators and their binding from `Puck.State.ExpressionOperators` (the
`Binding` column, C's order, left-associative), so a `let` and a rule group the
same text the same way; the range sits between the relational comparisons and
the shifts. `& | ^ << >> >>> ~` read whole numbers and fold through
`ExpressionArithmetic`; a zero divisor under `/` or `%` and a shift count
outside 0..63 are refused. Never add an operator or a precedence level to the
document parser alone: add the row to the table. Two
strings compare with `==` and `!=` only. A comparison yields `1`/`0`, never a
JSON boolean — the rule language has no
boolean either. `filter`'s lambda, a conditional's condition, and a `for`'s reads
all use the same truth test: a number is true when non-zero, a string when
non-empty, a present container is true, an absent value is false.

## Control flow, call gates, and compound assignment are cross-vocabulary

The core parses `if`/`else if`/`else`, `repeat`/`break`, call-form gates
(`key(left, held)`), and compound assignment (`+= -= *= /= %= &= |= ^= <<=
>>=`) for **every** vocabulary. Whether a given vocabulary's rule shape can
carry them is that vocabulary's own answer:

| Code | Refusal |
|---|---|
| PUCK037 | Control flow (`repeat`/`break`, or `if` in a vocabulary whose rule shape has nothing to lower it onto) in a rule shape that can't carry it. |
| PUCK038 | A call-form gate where the vocabulary admits comparisons only. |
| PUCK039 | A compound-assignment operator the vocabulary's effects don't carry. |

The world vocabulary's rule body lowers `if`/`else if`/`else` to the state
engine's conditional effect (`ActionEffect.If`), reusing the `when` gate's own
predicate lowering for the condition — see the [world transpiler
guide](../../../../src/Puck.World.Transpiler/README.md#rules). `repeat`/`break`
have nothing to lower onto there, so PUCK037 fires for them.
Cartridge rules support all three, `if`/`repeat`/`break` (`rom-forge` owns
that grammar).

## Compilation limits

One compilation admits at most 4,000,000 source characters, 1,000,000
elements per collection, 64 levels of source nesting or active evaluation, and
4,000,000 work units across evaluation, expansion, and output copies (checked
before bulk allocation; exceeding one is **PUCK047**). Both vocabulary
emitters accept a cancellation token. Objects compare independently of
property order; integer comparison, sorting, group keys, and integer functions
preserve values above 2^53.
