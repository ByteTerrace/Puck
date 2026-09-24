# Puck DSL and document transpilation

The `.puck` authoring language's core, with no knowledge of any particular document schema. It turns source text
into a syntax tree, formats it, resolves its imports, and reports diagnostics against source spans. What the tree
means is defined by its document vocabulary: `Puck.World.Transpiler` is the one that lowers it to
`puck.world.definition.v1`.

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
| `Formatting/` | `PuckPrinter`: the one formatter, printing a parsed tree back as source. |
| `Lowering/` | `DocumentLowering` (values, arithmetic, indexing, `for`/template expansion), `DocumentScope`, `DocumentValueComparer`, and `DocumentScalars`/`DocumentBuiltins`—the scalar and collection function vocabulary. |
| `Modules/` | `ModuleResolver`: `import` resolution and alias composition. |
| `Rewriting/` | `PuckSyntaxRewriter` and `PuckMigration`: a named rewrite over the tree, and what it declares it reshapes. |
| `Units/` | `UnitDimension`/`UnitConversion`: what `deg`, `rad`, `s`, `ms`, `m`, `cm`, `mm`, `hz`, `%` represent. |
| (root) | `CompilationResult<T>`, and `PuckDslVocabulary`—the DSL's spelling of `Puck.State`'s cell-kind enum. |

## Names

Every Puck source language reads a name by one rule, `Puck.State.IdentifierSpelling`: the document grammar, the
expression spelling a rule runs, the pattern, cell-set and state spellings, and the `sql { }` dialect.

- An **identifier** opens with an ASCII letter or `_` and continues with ASCII letters, digits and `_`:
  `pieceCell`, `_scratch`, `row2`.
- A **name** is an identifier, optionally opened by `$`, the sigil for an engine-reserved name: `$value`, `$each`,
  `$tick`, `$replace`. The `$` stands only at the front. `a$b` is not one name, and `$"…"` is an interpolated
  string, not a name.

A name outside the rule is still writable: the document grammar quotes it as a string (`"seat-1": 1`,
`rule "raw named" { }`), and the expression spelling puts it in backquotes (`` `seat-1` ``). A non-ASCII name,
such as `"größe"`, is written the same way. Every formatter and decompiler prints a name bare exactly when that
bare spelling reads back as the same name and the position doesn't reserve it. Otherwise it quotes the name. In
the document grammar that decision is `PuckPrinter.PrintName`, and for a property `PuckPrinter.PrintPropertyName`.
In the expression spelling it is `ExpressionSpelling.PrintName`, which also backquotes a name that would read as a
number, a unit-bearing literal or a dotted read of another row.
The formatter keeps a block's leading word quoted when the source quoted it, because `"for" { }` is a member named
`for`, while `for` bare opens a loop.

What a position reserves is layered on top of the rule and never changes its characters. Examples are a keyword
a statement reader would take (`let`, `rule`, `when`), a reserved call name in an expression (`count`, `dot`),
a pattern's reserved words (`any`, `empty`, `except`, `none`), the SQL keywords, and the dotted path and
`$name:segment` continuations the expression spelling reads. Such a name prints quoted in that position, and
bare elsewhere.

### Generated names

Some names are written by Puck rather than by an author: a `stabilize` group's member rules, the rows and rules
a `test` block adds, a pool's backing rows, a door's return arch, and every name a module instance used under an
alias declares. Each one is spelled with a character no
author-written name may carry, so it reads as machinery wherever it is printed and can never equal a name an
author wrote. `Puck.State.GeneratedName` holds the rule, and every minting site uses it.

| Where the name lives | Parts joined by | Examples |
|---|---|---|
| Inside a document: a row, a cell key, a rule, a rule group, a placement, a prototype | `$` | `turn$east`, `expect$1`, `expect$1$fixed`, `$pool$pieces$live`, `ground$floor`, `return$hub$arch`, `left$score` |
| A file or directory: a generated test world, an instance the engine starts | `~` | `rulepush~push-block`, `rulepush~visit~hedges`, `dungeon~0` |

The `$` is the sigil the identifier rule keeps to the front of a name, so a generated document name is never an
identifier and never reads bare. At the front, `$` still opens a reserved word or channel (`$value`, `$tick`,
`$cell:`). A file-backed name uses `~` instead because a shell expands `$` inside a path, while `~` is inert
in every shell, in a URL, and in a file name.

An author never writes either form, bare or quoted. A declared name carrying `$` anywhere past its first
character, or `~` anywhere, is refused with PUCK113, and so is a declared world name or a source file name
carrying `~`. A world document is what the compiler writes, so its JSON admits `$`-joined names in the positions
the compiler fills. The compiler never writes `~` into a document name, so the loader refuses a document whose
row, key, rule, placement, destination or other declared name carries one, naming it.

A module instance's names are the one generated form a source reads back by name. `use counter as left()`
declares the module's row `score` as `left$score`, and the source that uses it writes `left.score` wherever a
name stands: an operand, an assignment target, a module argument, a re-export (`export left.score`), a border
endpoint (`west.left.floor.east`). A nested instance reads through each alias, so `box.child.score` is
`box$child$score`. The alias resolves from the `use` lines of the scope and its modules' bodies, so a reference
reads the same written before its `use` as after it. A JSON document and the console write the document spelling
(`world.state left$score`). A document that imports another under an alias (`imports: [{"as": "dive", ...}]`)
restates or reads that import's names in the same spelling (`dive$diveCourt`); the compiler admits a declaration
headed by one of the document's own import aliases, and the decompiler prints those names as written.

When a document name reaches a directory, its `$` is spelled `~`: the instance a generated link destination
`link$west` starts is named for `link~west`. Because no document name carries `~`, that spelling keeps every two
names apart.

Decompiling reads each generated name back as the construct that generated it, so decompiling a document and
compiling the source gives the same document: a `ground` block from its prototype and placement, `rules` scopes
from a joined rule or group name, a `test` block from a generated test world, and `border` and `door` from the
worlds of one composition decompiled together. A generated name no construct prints back is refused by name
rather than printed as an authored one. A module instance's rows, placements and prototypes are refused so, since a
module's use does not print back from the document it expanded into. `GeneratedNameReversalLawTests` holds every minting site to this, one row
per site, and fails when a minting call appears that no row reads back.

The classes are ASCII by design. A rule built on the runtime's Unicode letter tables would change which names
print bare whenever the runtime's Unicode version changed. It would also let two names that look identical be
different text, for example composed and decomposed accents or a Cyrillic `а` for a Latin `a`.

## Row declarations: `table`/`slot`/`pile`/`grid`

```puck
table name [: Enum] [modifier(...)]* { key = value [modifier(...)]* ... }
slot name [: Enum] [= value] [modifier(...)]*
pile name of tokenRow [modifier(...)]* { token ... }
grid name [: Enum] [modifier(...)]* [{ key = value ... }]
```

`Ast/StateDeclarationNodes.cs` and `Parsing/PuckParser.StateDeclarations.cs` parse this shape for every vocabulary:
a declared name, an optional `: Enum` naming the enum the row's cells are drawn from (the way a record field names
one, `field: Enum`), an optional bare-identifier reference (`pile`'s
`of tokenRow`), zero or more `name(args)` modifier calls (reusing the ordinary call-expression grammar verbatim),
and an optional `{ }` body — `key = value` cell entries with their own modifiers (`table`/`grid`), or bare `token`
entries (`pile`). The core assigns no meaning to a reference, a modifier's name, or where a declaration is
legal; it is a syntax shape only, so a second document vocabulary with its own row-shaped concept could reuse it
without touching this project. `Puck.World.Transpiler` is `puck.world.definition.v1`'s vocabulary for it today —
see its [README](../../src/Puck.World.Transpiler/README.md#state-declarations) for `state.world`'s exact lowering,
defaults, and refusals.

A member has one spelling wherever it stands: a call's argument, an object literal's property, a block's
property line. Lowering carries a position — the vocabulary's own token for the shape a value fills
(`IDocumentVocabulary.CallContext`, `MemberContext`) — and asks the vocabulary what the member there holds
(`ClassifyMember`): a name, a cell key, a value expression or a word of a closed vocabulary is written bare, and
free text is a string literal. A plain string literal where a member is bare is PUCK110 and a bare word where
one is text is PUCK111. An interpolated string computes one name, one key, one number or text, and never an
expression: it is a name's, a key's or a text member's whole value, and inside a bare expression or key it is an
atom the operand grammar reads as the one token it computes. The key of an assignment's target takes the same
atom, so a template writes `stones[$"{colour}"] += 1` and the target is the key the atom computes, exactly as the
same key read on the right. An atom that computes the name of one of the rule's locals reads that local, as the
same name written bare does, so a loop's `local $"fit{t}"` is read back as `$"fit{t}"`; any other computed name
reads the row of that name. An expression written as an interpolated string is PUCK112.

Operands carry parsed `OperandExpressionNode.Syntax` trees. The source parser uses the state expression
parser's lexer, precedence and nesting bounds, with unresolved names and interpolation atoms represented as
nodes. Sugar assignments, comparisons, locals, scores, derives and pattern values take this path. A block
member parsed by the compile-time grammar is converted structurally when the vocabulary classifies it;
lowering does not print and reparse that AST or rewrite identifiers in source text.

Binding distinguishes expression names, row names, literal keys and quoted names. An interpolation atom is
resolved once, so its value cannot inject an operator or become another compile-time binding. Parentheses in
key position remain significant: `row[key]` names a literal key, while `row[(key)]` reads the key expression.
Family folds retain their runtime subprograms. Embedded vector literals resolve in the instruction program,
including its subprograms. A vocabulary that classifies nothing leaves values to the compile-time grammar.
The world vocabulary's [README](../../src/Puck.World.Transpiler/README.md) describes its member positions.

For world state, the row kind is inferred from authored values and bindings, `space(...)`, and fractional bounds or
advances. A declaration does not spell `: Int` or another kind; such an annotation is refused with PUCK107. The
word after `:` names an enum instead: `slot left: Element = Air` is an `Int` row whose cells are `Element`'s members,
and every enum a world document declares reaches its `state.enums`. A document with a `basis` reads the enums its
basis's composed document declares, so a child writes `slot left: Element = Air` with an enum only its basis
declares. A row or a record field naming an enum the composed world's `state.enums` does not hold is PUCK119, drawn
once by the validation of the composed world at the line that names it, and `table cards: Card` naming a record is
PUCK107, since a table of records is a `pool`. Every refusal the validation of a composed world draws lands on the
source's own line: the validator's path indexes the composed document, where a basis's or an import's rows come
first, and it is traced back into the source's own document before it is read against the source map.
When decompilation cannot prove that declaration sugar preserves a row's kind—especially an empty non-`Int` row—it
keeps the explicit `row` form so a round trip does not silently change the domain.

A few invariants hold across every declaration form the world vocabulary lowers:

- **One declaration produces one row.** The transpiler never splits a declaration into several rows, never
  generates a state name, and never changes a grant or a binding.
- **Declarations are accepted only under `state.world`.** `state.body` and `state.identity` hold a different
  record, `ActionStateSlot`, and take no declaration syntax.
- **A declaration does not imply storage scope.** A `table player { }` declares one world row, not
  per-player storage.
- **One trait has one spelling.** Accumulation is `advance`, with the sign carrying direction; there is no
  separate `regen` and `decay`.
- **Dot access belongs to the state engine's grammar.** `row.key` is an `ExpressionSpelling` spelling of
  `row[key]`, so the DSL, JSON documents, and every other expression consumer agree on what it means. Row and
  cell names themselves forbid dots, so the HUD binding `state.<row>.<key>` stays unambiguous.
- **Equivalence is classified per form.** A concise declaration must compile byte-identically to its explicit
  JSON equivalent. Dot access keeps the authored operand text, so it is judged by semantic equivalence instead:
  identical tokens and identical compiled rule facts against the bracket spelling.

Records and pools use `record Name { field: Kind ... }` and
`pool name of Name capacity(n)`. Field bounds spell a range as
`bounds(lo..hi)` with an optional `overflow: Refuse|Saturate`; Vector fields
name their embedding space with `space(name)`. A rule can visit a stable
snapshot of live handles with `rule name for each x in pool { ... }`, read its
size with `count(pool)`, claim with `claim pool as x { ... }`, and release with
`release x`. A binding's `x.field` access is lexical and generation checked.
Binding names must not collide with state row names, because `x[key]` would
otherwise have two meanings.

Record fields currently carry kind, bounds, default, overflow, and Vector
space. Timed row traits such as `advance` and scheduling still belong to rows;
pool migrations that need those traits remain open until the generated field
row can inherit them without changing row semantics.

### Vector spaces, `embeds(...)`, and vector literals

The state section supports declared embedding spaces and vector rows:

```puck
state {
    spaces {
        space lore { model: "puck-fixture" revision: "1" dimensions: 256 }
    }
    world {
        table events space(lore) {
            ambush = "Bandits ambushed the caravan on the north road"
        }
        table lines embeds(lineVectors) {
            warn = "Stay close to the wagons tonight."
        }
        slot situation space(lore) = "Travellers approach the gate at dusk"
    }
}
```

- **`spaces` block**: declares one or more named vector embedding spaces with `model`, `revision`, and `dimensions`.
- **`Vector` kind**: rows of unit-normalized signed 8-bit vectors, inferred from a `space(...)` modifier's presence — a row's kind is never authored (see [world-vocabulary.md](world-vocabulary.md)). In source, values are written as plain text literals.
- **`embeds(vectorRow)` modifier**: links a `Text` table (inferred from its own string cell values) to an auto-generated or explicitly paired `Vector` table, automatically embedding each text entry into vector state.
- **Embedding literals**: `embed("text")` produces a vector operand inside rule gates and effects, resolved during transpilation against the companion `.embeddings.json` lock file produced by `puck embed`.

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

A `for` also stands inside a rule body, a workflow step's body, and any effect list (an `if` branch, a
`transaction` or its `onFailure`, a pool `claim` or `for each`), in both document vocabularies. There it repeats
locals and effects, and a local names each iteration's value with an interpolated name:

```puck
rule "count-full-rows" {
  when phase == 3
  for row in range(0, 4) {
    local $"full{row}" = match(fullRow, well, E)[(row * 12)]
  }
  cleared = full0 + full1 + full2 + full3
  phase = 4
}
```

The rule carries the unrolled statements, so each one is compiled and priced like a statement written out by
hand. A rule has one gate and at most one decision, so a `when`, a `decision` or a nested `rule` inside the loop is
refused (PUCK114), and a rule property such as `mode:` is a field assignment (PUCK046).

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

`squareRoot`, `sine`, `hexIndex`, `mortonIndex`, `greatestCommonDivisor`, and every other named scalar
function are not declared in this project. They come from `Puck.State.ExpressionVocabulary`, a projection of
`ExpressionOperators`—the SAME table the rule language (the one a compiled rule runs, over `FixedQ4816`/`Int`
state cells) is built from. One table, two evaluators, so a spelling can never mean one thing in a rule and
something else, or nothing, in a document.

The two evaluators fold a name differently, on purpose:

- A function defined over integers (the bit, lattice, grid, and combinatorial family—
  `greatestCommonDivisor`, `leastCommonMultiple`, `floorModulo`, `binomialCoefficient`, `primeAt`, `setBitCount`,
  `hexIndex`, `mortonIndex`, `hilbertIndex`, `pairMinimum`, `pairMaximum`, and the rest) is evaluated by the engine's
  own `ExpressionArithmetic` rather than reimplemented here. Integers are exact in both evaluators, so delegating
  makes a numeric disagreement between a rule and a document impossible rather than merely unlikely.
- Everything else the document language can fold—`absolute`, `ceiling`, `clamp`, `floor`, `maximum`, `minimum`,
  `round`, `sign`, `sine`, `cosine`, `squareRoot`—uses `double` for fractional operations, while whole-number
  inputs to integer-preserving functions retain their signed 64-bit precision. Routing an authored `0.7071068` through the
  rule language's `Q48.16` fixed point to agree with it bit-for-bit would quantize the authored value to 1/65536
  for no gain: the folded number is document DATA, baked into JSON at compile time, and never simulation state. The
  double fold already agrees with the fixed-point one exactly on whole numbers.
- A fractional literal lowers as the decimal its text spells, to the 28 significant digits a `decimal` holds, and
  stays exact through arithmetic that is closed over finite decimals: a sign, `+`, `-`, `*`, and a unit whose scale
  is a power of ten (`ms`, `cm`, `mm`, `%`). A result a `decimal` would overflow on or round, a product or a
  conversion too small for its scale included, is computed in `double` instead, which keeps its magnitude. A
  fractional quotient or remainder, a folded function and degrees into radians are always computed in `double`; a
  decimal field reads such a result at its 15 significant digits, the same on every runtime. One value has one
  spelling: `1.50` lowers as `1.5`, and `2.0` and `1.5e3` lower as the integers they are.
- A comparison, and the ordering and equality `sort` and `distinct` use, is decided on the
  exact value of each number, whether it is held as an integer, a decimal or a double. `1.0000000000000001 > 1`
  is true, and the double nearest `0.1` is not equal to the decimal `0.1`.
- An infix operator means what its row in `ExpressionOperators` means, over the numbers a document holds: the rule
  language's `Fixed` reading of it, carried at the document's own precision rather than quantized to `Q48.16`.
  Two whole numbers whose result is whole are handed to the engine's own `ExpressionArithmetic` Int arm, so a
  whole-number sum, difference, product, remainder or exact quotient is the rule's own answer, and a result past
  signed 64 bits is refused as a rule refuses it. The one place a value and an `Int` rule part ways is `/` of two
  whole numbers whose quotient is not whole: `7 / 2` is `3.5` in a value, as it is over `Fixed`, where an `Int`
  rule truncates to `3`. Write `floor(7 / 2)` for the whole part. A binary node whose operator the table does not
  spell has no value and is refused.
- A conditional, `condition ? whenTrue : whenFalse`, reads its condition first and lowers only the arm it
  takes—the other arm is never evaluated, which is what lets an untaken arm reference something the taken one does
  not need. Either arm may hold any value: a number, a string, an array or an object.

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

The builtin names apply in compile-time value positions. A call constructing a declared document arm uses that
arm instead: `sort([3, 1, 2])` computes an array, while `transform sort(row: scores, by: [{ row: scores }])`
constructs a state transform. Nested document calls use the same expected-type rule. A statement-position call belongs to its
own vocabulary (a cartridge's `map(row:, column:, tile:)` rule step is untouched).

## Comparisons are 1 or 0

Value expressions carry the rule language's infix operators, member access, indexing, calls, arrays, objects,
ranges and colours. The infix operators and their grouping come from one table, `ExpressionOperators`, which both
parsers read, so `1 << 2 + 3` groups the same way in a `let` as in a rule. From loosest to tightest, in C's order,
each infix operator associating to the left:

1. `condition ? whenTrue : whenFalse`, which associates to the right, so `a ? x : b ? y : z` is a chain
2. `??`
3. `|`
4. `^`
5. `&`
6. `==` `!=`
7. `<` `<=` `>` `>=`
8. `..`, a range, which only a value spells
9. `<<` `>>` `>>>`
10. `+` `-`
11. `*` `/` `%`
12. prefix `-` and `~`

The conditional is the one spelling of a choice in both languages; no call spells it. The prefix operators are the rule language's two: a value is written
without a `+` sign.

As in C, the bitwise operators bind looser than a comparison: `x & 1 == 0` reads as `x & (1 == 0)`, so write
`(x & 1) == 0`. `&`, `|`, `^`, the shifts and `~` read signed 64-bit whole numbers and are evaluated by the engine's
own `ExpressionArithmetic`, so a value and a rule cannot disagree on them; a shift counts 0 to 63, and any other count
is refused. A zero divisor under `/` or `%` is refused, as a rule faults on one. `a ?? b` is `a` unless `a` is
absent, and then `b`.

A comparison yields `1` or `0`, never a JSON boolean—the rule language has no boolean either, so
`cleared + (row > 0)` means the same thing on both sides of the compiler. `filter`'s lambda, a conditional's
condition, and a `for`'s own reads are all read back through the same truth test: a number is true when non-zero, a string
when non-empty, a present container is true, and an absent value is false.

## Names are spelled out

`absolute`, `squareRoot`, `ceiling`, `minimum`, `maximum`, `floorModulo`, `greatestCommonDivisor`,
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
`ExpressionVocabulary`/`DocumentScalars` for the reasons above. A comparison's operator and document spellings are
the same choice: `ExpressionComparisons` reads both from the expression operator table's comparison rows, so a
comparison operator can never mean one thing in a gate and another in an expression. `PuckDslVocabulary` does the
same for the cell-kind words—one table, read by the parser, the emitter, the decompiler and the language server
alike, instead of four hand-typed lists of member names.

## Unit conversion boundaries

A millisecond is a thousandth of a second wherever it is written, so the arithmetic lives here. WHICH of a
document's fields is a time is that document's own vocabulary—`WorldDocumentEmitterUnits.Classify` says
`delaySeconds` is a `Seconds`, and hands the number to `UnitConversion` to divide. A second vocabulary adds its own
classification and reuses the same arithmetic; it never restates that `ms` divides by 1000.

The one place the core itself asserts a dimension is `schedule <row> in 250ms`, where the grammar—not a field
name—says the operand is a time.

## Embedded language blocks: `sql { ... }`

```puck
sql {
    CREATE TABLE fighters (
        id   TEXT PRIMARY KEY,
        hp   INT  NOT NULL DEFAULT 100 CHECK (hp BETWEEN 0 AND 100) ON OVERFLOW SATURATE,
        mana INT  DEFAULT 0 ADVANCE 5 PER SECOND
    ) CAPACITY 32;

    INSERT INTO fighters (id, hp, mana) VALUES
        ('hero', 80, 50);

    DECLARE turnCount INT DEFAULT 0;

    CREATE RULE healHero ON ENTER AS
        UPDATE fighters SET hp = hp + 10 WHERE id = 'hero';
}
```

The core parser supports embedded language blocks through `IDocumentVocabulary.IsEmbeddedLanguage`. When
an identifier matches an admitted embedded language (such as `sql`), the parser produces an `EmbeddedBlockNode`
preserving raw statement text and token spans without running the core Parlot expression parser on its body.
The owning vocabulary lowers the embedded language directly into document JSON, and which identifiers those are
is one row each of that vocabulary's own construct table—for the world, the generated
[world vocabulary](world-vocabulary.md).

## Rewriting a source: `PuckSyntaxRewriter` and `puck migrate`

A change to the surface syntax is applied to the sources that use it by a
*migration*: one named rewrite over the syntax tree, run over a directory by
[`puck migrate`](cli.md#the-puck-dsl-verbs). Derive from
`Puck.Transpiler.Rewriting.PuckSyntaxRewriter`, override the hook for the node
kind being reshaped, and call the base method to descend:

```csharp
private sealed class CountsBecomeCalls : PuckSyntaxRewriter {
    protected override ExpressionNode RewriteExpression(ExpressionNode expression) =>
        (base.RewriteExpression(expression: expression) switch {
            IdentifierExpressionNode identifier when (identifier.Name == "zoneCount")
                => new CallExpressionNode(
                    Arguments: [new ArgumentNode(Name: null, Value: identifier)],
                    Name: "count"
                ),
            var other => other,
        });
}
```

The base hooks do nothing but descend, so a rewriter that overrides nothing is
the identity and prints its input back byte for byte. Every node is rebuilt with
the record `with` operator, so a node the rewrite did not touch keeps its own
`SyntaxTrivia`—its author's comments, blank-line runs, and line breaks. Because
the walk is over source rather than a lowered document, `let`, `template`,
`for`, `import`, units, and `sql { }` survive as themselves instead of as their
expansion; the printer writes the surviving `for` loop, not the rows it would
produce.

Two things raise `PuckRewriteException` rather than a diagnostic, because both
are defects in the rewrite instead of refusals about a source: a node kind the
descent has no arm for, which is how a newly added syntax node announces that
the rewriter has fallen behind the tree; and a hook that answers a typed
position—a `template`'s body, a `transform`'s call—with a node that position
cannot hold.

A `PuckMigration` wraps such a rewriter with the name `migrate` selects it by,
a one-line summary, and `ReshapedMembers`: the `/`-separated document member
paths the rewrite may change, where a `*` segment matches any one object key or
array index and a path covers everything beneath it. An empty list is the claim
a rewrite of the compile-time layer alone makes, since that layer is evaluated
away before the document exists. `migrate` compiles each source it would change
before and after, and holds the migration to the claim, so a rewrite that moves
an undeclared member is refused with the member named and nothing written.

A comment is not in the document, so no member declaration can speak for one. A
second verdict covers them: the comments of the migrated source, in reading
order, must equal the comments it started with, and a rewrite that drops, adds,
moves, or rewords one is refused by name unless it declares `ReshapesComments`.
The verdict is read off the tree the migrated text parses back to, so it covers
the printer as well as the rewrite. A rewrite that mints a fresh node in place
of one the author commented is the case to watch — carry the old node's
`Trivia` onto the new one:

```csharp
=> (property with { Value = new LiteralExpressionNode(Value: 480) { Trivia = property.Value.Trivia } }),
```

### What the descent reaches, and what it hands over whole

Every syntax-node child reaches a rewrite hook. Operand-bearing sugar nodes descend through
`OperandExpressionNode`; `RewriteOperandSyntax` reaches parsed names, accesses, calls and operators, and the
ordinary expression hook still reaches interpolation holes. The operand hook receives the position's form:
a bare key is literal, while a grouped key is an expression. A changed tree is printed with its rewritten atoms;
an unchanged tree keeps the author's spelling. A rewrite must keep `Syntax` and `Text` consistent.

The following members still carry their own text grammar and are handed over whole:

| Member | Carries |
|---|---|
| `EmbeddedBlockNode.Body` | the whole `sql { }` dialect |
| `CellSetDeclarationNode.Expression` | a `set`'s cell-set algebra |
| `PatternDeclarationNode.Attribute` | a pattern's source row |
| `LiteralExpressionNode.RawText` | the author's numeric spelling, which must change with its value |

`PatternDeclarationNode.Match` is the one child that is not a syntax node at all
(it is a `Puck.State.PatternNode`), so the descent has no hook for the match
algebra; a rewrite reshapes it by replacing the whole `Match` on the declaration
the statement hook hands it. Should any of these become a syntax node, the child
law fails under its node's name until the arm rewrites it.

## Verification

The [release evidence package](../plans/runtime-and-delivery.md#the-evidence-package) and [its decisions](../decisions/runtime-and-delivery.md#release-evidence) hold the semantic-consistency decisions, regression combinations, and combined World release evidence required before further language growth.

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
interpolated string contents survive formatting unchanged, including indentation and blank lines. A source has one
layout, printed by `PuckPrinter`: `PuckPrinter.IndentWidth` (two) spaces per level, every block body on its own lines, and
every array or object keeping the line breaks its author wrote. `puck format`, the language server's formatting request, and
the decompiler all print it; no option or editor setting changes it.

There is no test project of this project's own: `tests/Puck.World.Transpiler.Tests` (parser, formatter, linter,
emitter, decompiler and LSP against `puck.world.definition.v1`) and `tests/Puck.GamingBricks.Transpiler.Tests` (compile and
decompile against `puck.cartridge.v1`) are the regression net for a change here—a core change is proven by
whichever of the two vocabularies it reaches.

## Documentation

- [Engine manual](../README.md)
- [Development](../development/README.md)
- [API reference](../api/index.md)
