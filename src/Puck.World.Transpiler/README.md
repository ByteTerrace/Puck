# Puck.World.Transpiler

The `puck.world.definition.v1` VOCABULARY for the `.puck` authoring language: it compiles a parsed document to world JSON
and decompiles the other way. The language itself—parser, syntax tree, diagnostics, formatter, import resolver,
unit arithmetic—is [`Puck.Transpiler`](../Puck.Transpiler/README.md), which knows no schema at all. `.puck` is an
authoring layer: JSON stays the wire form and the checked-in source of every shipped world.

## Pipeline

`Puck.Transpiler`'s `PuckParser` (source → syntax tree) → `Lowering/WorldDocumentEmitter.cs` (tree → JSON) /
`Decompiler/WorldDecompiler.cs` (JSON → tree → source) → `Validation/{PuckLinter,WorldSemanticValidator}.cs` →
`Lsp/PuckLanguageServer.cs` (editor integration). The core's `ModuleResolver` composes `import`s.

## Grammar

### Documents and blocks

```puck
schema: "puck.world.definition.v1"
basis: "worlds/base.puck"

let name = expression                 // compile-time constant
template name(param, param2 = default) { ... }
import "path" [as alias]
export read|action|binding name, name2       // names may wrap onto lines indented under the keyword;
                                             // a facet word alone on its line is an empty exported array

identifier [target] [name] { statements }   // a structural block; up to two leading tokens name it
identifier [ elem, elem2 ]                    // an array property (outside a rule body — see below)
identifier: expression                       // a property whose value is a SCALAR
identifier(k: v, k2: v2)                      // a call expression statement
identifier                                    // a bare flag statement, only where nothing else could follow on
                                               // the line (e.g. a placement row's bare `solid`, standing in for
                                               // `solid { margin: 0 }`)
```

A container value is written as a block and a scalar takes a colon—`host { }`, `cameras [ ]`, `documentId: "puck"`
—at every depth and inside an object literal too. There is exactly one spelling per shape: a colon in front of a
`{` or a `[` is PUCK040. A name that merely stands for a container keeps its colon (`origin: bounds`), because the
rule is about the punctuation in front of a literal, not about what the value turns out to be; a call's named
argument keeps its colon too (`worldPoint(point: [0, 1, 0])`), being call syntax rather than a statement.

Expressions: `+ - * /` (C precedence via additive/multiplicative), `.` member access, `(...)` calls,
`[a, b]` arrays, `{ k: v }` objects, `a..b` ranges, `#rrggbb[aa]` colors, and number literals with an optional unit
suffix (`s ms hz rad deg m mm cm % pct`), every one of which is checked against the field-dimension table in
`Lowering/WorldDocumentEmitterUnits` (which says which field is a length or a time; what the suffix is worth is
the core's `UnitConversion`)—a unit on a field the table does not cover is PUCK024 and a unit the field's
own dimension does not admit is PUCK025, so no suffix ever converts a value silently. A call argument classifies by
its qualified `call.argument` key, an ordinary property by its bare name: `orbit(yaw: 45deg)` is radians-native and
converts, a `yaw:` property on any other block is not in the table at all and is refused. A call `name(k: v, ...)` lowers to `{"$type":"name","k":v,...}`—the
universal escape hatch for any `$type` object; positional args are special-cased only for `orbit`/`fov`.

The core language's `[a]`/`["k"]` indexing, `for`, and `$"..."`/`"""..."""` string forms are ordinary parts of this
grammar too—a world document is where they earn their keep: a rigged part authors one `positions`/`rotations`
array per bone and a `for i in range(0, n) { shape ... $"name-{i}" { position: positions[i] } }` to place one shape
per element, rather than n hand-written blocks. They are the core's, not this vocabulary's own, so their rules
(compile-time-only sequences, no escapes in a raw string, `[` adjacency) are [`Puck.Transpiler`](../Puck.Transpiler/README.md)'s to state.

### `when` gates

```puck
when Operand cmp Operand [ (":"|"as") (Int|Fixed) ]     // cmp: == != < <= > >=
when Gate and Gate and Gate                              // -> one flat `all`
when Gate or Gate                                         // -> one flat `any`
when not Gate
when (Gate)                                                // one opaque child; never flattened into its parent
```

`and`/`or`/`not`/`as`/`when` are reserved only inside a gate. An `Operand` is opaque text handed whole to
`Puck.State.ExpressionSpelling`—the DSL never re-implements that grammar; `row`/`row[key]`/`row.key` reads,
`$name:segment` reserved channels, and a `$zones[...]`-folded selector all pass through untouched. A bare comparison between two
single state reads lowers to `compareState`; anything else, or an explicit `: Int`/`: Fixed`/`as Int`/`as Fixed`
suffix (which always forces it, even for two simple reads), lowers to `compareValue`. The suffix binds to the one
comparison it follows, never to an enclosing `and`/`or` chain—but printed bare at the end of a chain it reads as
though it scoped the whole thing, so wrap the annotated comparison in its own `(Operand cmp Operand : Kind)` (the
generic `when (Gate)` grouping above) whenever it sits beside `and`/`or`. `WorldDecompiler` always wraps and prints
an explicit `Int`. `Fixed` is the default and is elided only where the bare text re-lowers to `compareValue` on its
own; over two plain row reads the annotation is what keeps the node a `compareValue`, so it is printed there too,
and a `compareValue` carrying no `kind` at all over two plain reads has no sugar spelling and prints call-form.

### Rules

```puck
rule "name" {
    when Gate
    bind localName : Int|Fixed = <operand>
    mode: Edge|Level
    forEach: "rowName"
    zones [...]
    decision { ... }                          // see below
    <effect statement>*
}
```

Effect statements:

```puck
row[key] = rhs           // setState
row[key] += rhs          // addState
push row = rhs           // pushState (no key)
countdown row[key]        // countdownState
remove row[key]            // removeStateCell
schedule row in Ns          // scheduleState — the 's' suffix is required
transform local = call(...)  // wraps transformState around a StateTransform call-form value
transaction {
    <effect statement>*
} [onFailure {
    <effect statement>*
}]
name(k: v, ...)             // generate(...), or any Puck.World.Schema extension arm — call-form only
if Gate {
    <effect statement>*
} [else if Gate {
    <effect statement>*
}]* [else {
    <effect statement>*
}]
```

`if`/`else if`/`else` lowers to the state engine's conditional effect (`"$type": "if"`): `condition` through
the same predicate lowering `when` uses, `then` for the branch a true condition fires, and `else` (omitted when
absent) for the alternative. An `else if` chain is one nested `if` node per level—the parser's own shape for both
an authored `else if Gate { }` and an authored `else { if Gate { } }`—so a chain of any length lowers, formats, and
decompiles the same way a single branch does, with no separate case anywhere. A `transaction` may sit inside an
`if`'s branch when the `if` is not itself already inside one (transactions still never nest—PUCK019); `repeat` and
`break` still have nothing to lower onto in a straight-line rule body and stay refused as PUCK037.

`row[key]` (a *row reference*) is read as one span—a name plus zero or more adjacent `[...]` groups—and
resolved through `ExpressionSpelling` to exactly one state-read token; a literal key may equally be spelled
`row.key` (one dot on an unreserved, unquoted name—more than one is a parse error), which parses to the identical
token. `row[key] = rhs`/`row.key = rhs` only means a cell
assignment inside a rule/transaction/option/onFailure/onNoChoice body—the same `identifier[...]` outside such a
body still means the existing inline-array-property sugar. An `rhs` is a string literal (`Text`), a number carrying
the `s` unit (`ValueSeconds`), or opaque operand text the lowering stage classifies into `Value` /
`FromState`+`FromKey` / a verbatim `Expression`.

### Decisions

```puck
decision {
    periodSeconds: 1s
    mode: HighestScore
    scoreKind: Fixed
    commitmentSeconds: 0s
    incumbentBonus: 0
    seed: 0
    interrupt Gate
    onNoChoice { <effect statement>* }
    option "name" {
        when Gate
        score: <operand>
        neighbors { range: 10, candidateBudget: 12, maxCandidates: 4, halfAngleDegrees: 180, requiresLineOfSight: false, retainCurrent: true }
        <effect statement>*
    }
}
```

`periodSeconds` and each option's `score` are required.

### Shapes, placements, and prototypes

`shape Type "name" { ... }`, `placements { policy { ... } placement "id" { ... } }`, and
`prototypes { prototype "id" { document { ... } } }` are ordinary named/targeted blocks—no dedicated grammar
beyond the bare flag statement above (`solid` inside a `placement` row). `prototypes` admits `prototype` rows only
(it lowers to a bare array, with no object for a property to land on) and `placements` admits `placement` rows and
plain properties; anything else inside either is PUCK036, never dropped. The values each elides, and the values the
emitter fills back in, are one table: `Lowering/WorldDocumentRowDefaults`.

A `prototype`'s `document` is an ordinary nested block: `Lowering/WorldDocumentEmitter.LowerBlock`'s general case
lowers it like any other block, so a `shape` statement inside it reaches `LowerShapeBlock` the same way one at a
creation document's own root does, and `Decompiler/WorldDecompiler.DecompileNamedBlock` routes a nested `shapes`
array back through the same shape sugar on the way out. A `prototypes` row's identity (`WorldPrototype.Id`) is the
block's quoted name, the same way `WorldPlacement.Id` is a `placement` row's; a row with no `id`, or a
`document.shapes` row with no `type` (a basis-merge patch that only names the facets it overrides on a base
document's shape, by `id` alone), cannot round-trip through the block grammar and falls back to the generic value
path for the whole `prototypes` array or the whole `shapes` array respectively, rather than guessing.

`ShapeDocument.Group` carries no default in that table: the authored corpus omits the key on some shapes and
carries it on others, so filling one on lowering would add a key the source does not have. (The engine cannot tell
the two apart—`CreationCanonicalizer` normalizes an absent `group` and an explicit `0` to the same value—but
round-trip fidelity is the constraint here, not engine semantics.) It passes through both directions
unconditionally, like `material` or `parent`.

### State declarations

`state.world` is authored either as today's array (`world [ {...}, {...} ]`) or as a declaration block
(`world { ... }`), never both authoring the same section twice — the second authoring of `world` (either form)
is PUCK050. Only `state.world` accepts declarations; `state.body`/`state.identity` are unrelated per-participant
slot lanes, and `table`/`slot`/`pile`/`grid` anywhere else in the document (including `state.body`/`state.identity`)
is PUCK049. Inside the block, five statements are admitted:

```puck
state {
    world {
        table name : Kind [capacity(n)] [bounds(minimum: v, maximum: v, overflow: Refuse|Saturate)] [advance(perSecond: rate)] {
            key = value [advance(perSecond: rate)] [behavior(none)]
            ...
        }

        slot name : Kind [= value] [bounds(minimum: v, maximum: v, overflow: Refuse|Saturate)] [advance(perSecond: rate)]

        pile name of tokenRow [capacity(n)] {
            token
            ...
        }

        grid name : Int|Bool dimensions(width: w, depth: d) [wrap(None|X|Y|Both)] [cellSize(v)] [origin(x, y, z)]
            [band(v)] [empty(v)] [positions(tokenRow)] [inverse(tokens: tokensRow, codes: codesRow)]
            [bounds(minimum: v, maximum: v, overflow: Refuse|Saturate)] {
            "cellOrdinal" = value
            ...
        }

        row { <explicit row fields, exactly as the array form authors one> }
    }
}
```

Author an explicit `state.lattices` array, if any, before `world { }` in the same `state { }` block: a `grid`
declaration's duplicate-topology check (PUCK066) only sees `state.lattices` entries already present at its own
point in the document, and appends its own topology there.

`Kind` is `Int`, `Fixed`, `Bool`, or `Text` for `table`/`slot`; `grid` admits only `Int`/`Bool` (a board's own
`StateRow.Kind` ceiling), and `pile` carries no `Kind` at all (a pile is always `Bool`, presence-in-pile). An
unrecognized `Kind` is PUCK005. All four are core grammar (`Ast/StateDeclarationNodes.cs`,
`Parsing/PuckParser.StateDeclarations.cs`) — the parser knows only the shape (a name, an optional kind, zero or more
`name(args)` modifier calls, and an optional `{ ... }*` body: `key = value modifiers*` cell entries for `table`/
`grid`, bare `token` entries for `pile`); it assigns no meaning to a modifier, kind, or keyword name, so a second
document vocabulary could reuse the same grammar for its own row-shaped declarations without touching the core.
`Lowering/WorldDocumentEmitter.State.cs` is the one place that interprets `table`/`slot`/`pile`/`grid` for
`puck.world.definition.v1`, and refuses every case below by name. `table`/`slot`'s own keyword-vs-property
disambiguation (a name and `:` on the same line) also covers `grid`; `pile` disambiguates on a name and the bare
keyword `of` on the same line instead, since a pile carries no `:` kind.

**Lowering, one declaration to one row.** A `table` emits `"domain": {"$type": "keys"}` only when it has no cells
and no capacity, the one shape `StateRow.InferDomain` would otherwise read as a slot; any other table's cells or
capacity already infer a keyed row, exactly as the explicit row form does. A `table`'s cells populate `"cells"` (omitted when the table has none); a
`slot`'s `= value` populates the row-level `"value"` sugar (omitted when the slot has none, leaving the row to gain
its cell from a later write, exactly as an authored `value`-less explicit row does). `capacity(n)` (table only —
PUCK055 on a `slot`, since a slot is always exactly one cell) emits `"capacity"`. `bounds(minimum:, maximum:,
overflow:)` — every argument optional — emits `"min"`/`"max"`/`"overflow"` (the last only when `Saturate`; `Refuse`
is the wire default and, matching how an explicit row would spell it, is never written). `advance(perSecond: rate)`
emits `"advance": {"perSecondNumerator": n, "perSecondDenominator": d}`, the exact reduced fraction — see the rate
rule below. `bounds`/`advance` are refused (PUCK055) on a `Bool`/`Text` row or cell, matching `StateRow`'s own
Int/Fixed-only envelope and behavior. `behavior(none)` (cell-only) emits `"behavior": "None"`; combined with the
same cell's own `advance`, or repeated, it is PUCK056. A value/bound literal is converted per the row's kind exactly
as the explicit form spells it: a plain number for `Int`, `true`/`false` for `Bool`, a plain string for `Text`, and
— `Fixed`'s convention throughout the engine — a decimal-text JSON STRING, never raw Q48.16 bits (parsed through
`FixedQ4816.TryParse`, reprinted through `FixedQ4816.ToString()`, so `10.0` and `10` both emit `"10"`). A literal
that does not fit the row's kind (a fraction on `Int`, a non-boolean on `Bool`, an unparseable decimal on `Fixed`)
is PUCK053.

**Piles, one row over another row's keys.** A `pile name of tokenRow` emits `"kind": "Bool"` and
`"domain": {"$type": "keysOf", "row": tokenRow, "ordered": true}` — the ordered-membership shape a `deck`/`hand`
row already carries explicitly. Each bare `token` in the body becomes one cell `{"key": token, "value": true}`, in
written order — a pile's cells are never anything but presence flags, so there is no `= value` to spell. `of`
names the row supplying the token domain; the reference is checked once the whole `state.world` block is known
(so a pile may name a `table` declared later in the same block), refusing an unknown row (PUCK059) or one that is
not a plain token-domain row — `EffectiveDomain is StateDomain.Keys`, the shape an ordinary `table` with cells
infers (PUCK060). `capacity(n)` behaves exactly as a table's own: smaller than the authored token count is PUCK054;
greater than the token domain's own declared `capacity` is PUCK062, since a pile can never hold more members than its
domain provides — a domain `table` with no `capacity` of its own has no such ceiling to check against (its own true
ceiling is `StateCapacity.MaxCellsPerRow`, the same default every uncapped row carries; its currently authored cells
are only its initial population, never a ceiling). A repeated token is PUCK061.

**Grids, a board and its topology in one declaration.** A `grid name : Kind` mints TWO artifacts from one
statement: a `state.lattices` `Grid` topology named identically to the row (`dimensions(width:, depth:)` is
required; `wrap`/`cellSize`/`origin`/`band` map straight onto `LatticeTopology.Grid`'s own fields, each omitted
from the JSON at its default exactly as an explicit topology would be hand-authored), and the row itself, over
`"domain": {"$type": "cellsOf", "topology": name, "empty": v}` (`empty` omitted at its kind's zero — a raw `long`
on the wire regardless of `Kind`, so a `Bool` grid's `empty(true)` still lowers to the numeric `1`
`StateDomain.CellsOf.Empty` requires). `dimensions`/`wrap`/`cellSize`/`origin`/`band`/`empty` are the only fields a
`grid` declaration ever generates on the topology or the domain — nothing else `LatticeTopology.Grid` or
`StateDomain.CellsOf` carries is reachable from this sugar; author a `row { }` alongside a hand-authored
`state.lattices` entry for anything past that (custom `directions`/`elementAliases`, a second board over the same
topology). A `{ "cellOrdinal" = value ... }` body populates `"cells"` with literal topology-ordinal keys (quoted,
since a bare identifier can't spell a number); a key that is not a whole number inside `0..width*depth-1` is
PUCK064, and a repeated key is PUCK061. `positions(tokenRow)` sets `"valuesFrom"` on `tokenRow` — checked the same
way `pile`'s `of` is, refusing an unknown row (PUCK059) or one that is not an integer `keysOf` row (PUCK060).
`inverse(tokens:, codes:)` sets `"inverse": {"tokens":, "codes":}` on the grid's own row (see
[`StateInverse`](../Puck.State/StateInverse.cs) — the derived-board shape the garden's `chessBoard` `board` row
uses), refusing an authored cell body in the same declaration (PUCK065, since a derived board is never also
authored) and, once both rows are known, an unknown tokens/codes row (PUCK059) or one of the wrong shape or whose
keys disagree in count or order (PUCK060). `bounds(...)` behaves exactly as a table/slot's own, legal only when
`Kind` is `Int`. A `Kind` other than `Int`/`Bool` is PUCK063. Reusing a topology name a `state.lattices` entry
already carries at this point in the document (an earlier `grid`, or an explicit array authored earlier in the
same `state { }` block) is PUCK066.

**Rates reduce exactly.** An integer `perSecond` rate is numerator `n`, denominator `1`. A directly authored decimal
rate reduces from the author's own digits, never from a `double`'s raw bits: the lexer keeps a fractional literal's
exact source text (sign, digits, exponent) alongside the `double` it also parses for ordinary arithmetic, and that
text is parsed straight into an unscaled `BigInteger` and a base-10 scale with no intermediate `double` rounding
step in between. That unscaled value and scale are reduced by their GCD, and the result kept only when both the
reduced numerator and denominator fit in 64 bits — otherwise PUCK058. Because a finite decimal's denominator is
always a power of 2 and 5, this succeeds for every rate an author would plausibly type as a literal, at whatever
precision they typed it; it can only fail for a magnitude whose scale pushes the reduced denominator past
`long.MaxValue` (roughly beyond 18 decimal digits of scale). A rate reached only as an already-folded `double` — an
identifier, or any other compile-time expression rather than a bare literal — reduces from that double's own
shortest round-trip text instead, since no more precise source survives once the value has actually been computed
in `double`.

**Refusals**, each a source-spanned diagnostic in `PuckDiagnosticCodes` (declared in `Puck.Transpiler`, since the
shape is core, though every one of these particular messages is raised by the world vocabulary):

| Code | Refuses |
|---|---|
| PUCK005 | An unrecognized `Kind` (`UnknownKindAnnotation`, shared with the rest of the language). |
| PUCK049 | `table`/`slot`/`pile`/`grid` outside `state.world`. |
| PUCK050 | `state.world` authored more than once (array + block, block + block, or array + array). |
| PUCK051 | A duplicate row name in one `state.world`, or a duplicate cell key in one table/grid. |
| PUCK052 | A row name or cell/token key carrying the reserved `$` prefix. |
| PUCK053 | A default/bound literal that does not fit the row's kind. |
| PUCK054 | `capacity(n)` smaller than the table's/pile's own authored cell/token count. |
| PUCK055 | A modifier the declaration's shape or kind refuses — `bounds`/`advance` on `Bool`/`Text`, `capacity` on a `slot`. |
| PUCK056 | More than one behavior — a repeated `bounds`/`advance`/`capacity`, or a cell combining `advance` with `behavior(none)`. |
| PUCK057 | An unrecognized modifier name, or an argument shape the modifier itself refuses. |
| PUCK058 | An `advance(perSecond: ...)` rate that does not reduce to an exact 64-bit fraction. |
| PUCK059 | A `pile`'s `of`, or a `grid`'s `positions`/`inverse`, naming a row that does not exist. |
| PUCK060 | A `pile`'s `of`, or a `grid`'s `positions`/`inverse`, naming a row of the wrong shape. |
| PUCK061 | A repeated token in a `pile`'s body, or a repeated cell key in a `grid`'s body. |
| PUCK062 | A `pile`'s `capacity(n)` greater than its token domain's own token count. |
| PUCK063 | A `grid` declared with a `Kind` other than `Int`/`Bool`. |
| PUCK064 | A `grid` cell key that is not a whole-number topology cell ordinal inside `0..width*depth-1`. |
| PUCK065 | A `grid` combining `inverse(...)` with an authored cell body. |
| PUCK066 | A `grid` reusing a topology name `state.lattices` already carries at that point in the document. |

**Composition.** `let`, `template`, and compile-time `for` compose exactly as they do everywhere else in the
language, because a declaration's name, kind, and every modifier argument are ordinary expressions the core
evaluates before `WorldDocumentEmitter.State.cs` ever sees them: `let cap = 8` then `capacity(cap)`, a `template`
whose body is a `table`/`slot`/`row` statement expanded once per call, or `for (item, index) in rows { slot
$"meter-{index}" : Int = item }` to mint one slot per element — the emitted rows are indistinguishable from ones
written by hand. A `let` or `template` parameter referenced only inside a modifier argument or a cell's value is
recognized as used by the linter's reference scan, not reported as dead (`Validation/PuckLinter.cs`).

**`row { }`** is the escape hatch: an ordinary nested block, lowered exactly like any other (`LowerBlockToObject`),
so every explicit `StateRow` field — `dynamics`, `cycle`, `draw`, `visibility`, `knowledge`, `phase`, `phaseOf`,
`valuesFrom`, `inverse`, `evicts`, `gatesDrive`, `field`, or any combination `table`/`slot`/`pile`/`grid` do not
sugar (a second board over a topology a `grid` already declared, or a `pile`/board with a non-identifier name) — is
authored the same way it always has been, nested one level deeper inside `world { }` instead of as one element of
the `world [ ]` array.

### State SQL dialect (`sql { ... }`)

```puck
sql {
    CREATE TABLE characters (
        id   TEXT PRIMARY KEY,
        hp   INT  NOT NULL DEFAULT 100 CHECK (hp BETWEEN 0 AND 100) ON OVERFLOW SATURATE,
        mana INT  DEFAULT 0 ADVANCE 5 PER SECOND
    ) CAPACITY 32;

    INSERT INTO characters (id, hp, mana) VALUES
        ('hero', 80, 50),
        ('goblin', 30, 0);

    DECLARE turnCount INT DEFAULT 0 CHECK (turnCount >= 0) ADVANCE 1 PER SECOND;

    CREATE TABLE deck (id TEXT PRIMARY KEY REFERENCES cardNames) ORDERED CAPACITY 3;

    CREATE RULE healHero ON ENTER AS
        UPDATE characters SET hp = hp + 10 WHERE id = 'hero';

    CREATE RULE spendMana EVERY TICK AS
        BEGIN ATOMIC
            UPDATE characters SET mana = mana - 1 WHERE id = 'hero';
        END;
}
```

A hermetic SQL authoring dialect embedded in `.puck` world documents via `sql { ... }` blocks.
It compiles directly to native Puck state rows and rules without external or embedded SQLite dependencies.

- **Multi-column tables**: `CREATE TABLE fighters (id TEXT PRIMARY KEY, hp INT, mana INT) CAPACITY n;` decomposes
  into prefix-named state rows (`fightersHp`, `fightersMana`) sharing the primary key domain and capacity.
  Columns admit `NOT NULL`, `DEFAULT <val>`, `CHECK (<col> BETWEEN min AND max | >= min | <= max)`,
  `ON OVERFLOW SATURATE`, `ADVANCE <rate> PER SECOND`, and `DYNAMICS <row>`.
- **Scalar slots**: `DECLARE name TYPE [DEFAULT v] [CHECK ...] [ADVANCE r PER SECOND];` compiles directly into
  a single scalar state row.
- **Piles**: `CREATE TABLE name (id TEXT PRIMARY KEY REFERENCES target) ORDERED CAPACITY n;` spells an ordered
  sequence table (native pile).
- **Rules**: `CREATE RULE name EVERY TICK | ON ENTER AS <statement>` compiles directly into native rules JSON.
- **Transactions**: `BEGIN ATOMIC <statement>* [EXCEPTION <statement>*] END;` compiles to atomic transaction
  effects (`transaction` with optional `onFailure`).

| Code | Severity | Refusal |
|---|---|---|
| PUCK070 | Error | Floating-point column type (`REAL`, `FLOAT`, `DOUBLE`) in a SQL table declaration — state holds no floats, use `FIXED`. |
| PUCK071 | Error | Composite `PRIMARY KEY (a, b)` in a SQL table declaration — a state cell has exactly one key. |
| PUCK072 | Error | `CHECK` constraint shape outside `BETWEEN a AND b`, `>= a`, or `<= b`. |
| PUCK073 | Error | Unsupported SQL clause or construct (`GROUP BY`, `HAVING`, `WINDOW`, `LIMIT`, cross-key `JOIN`, `UNION`, `TRIGGER`, etc.). |
| PUCK074 | Error | Set-based `UPDATE` reading a column it writes at other keys — self-referential multi-key updates are refused. |
| PUCK075 | Error | Inserted row missing a `NOT NULL` column that declares no default value. |
| PUCK076 | Error | Syntax or grammatical refusal inside a `sql { ... }` block. |

Shared state declaration and semantic codes also apply:
- **PUCK051**: Duplicate table/slot name or duplicate inserted primary key.
- **PUCK053**: Default value or SET assignment literal that does not fit the target column or slot's kind.
- **PUCK054**: Table `CAPACITY` smaller than inserted row count.
- **PUCK058**: `ADVANCE` rate that does not reduce to an exact 64-bit fraction.
- **PUCK059**: Unknown table, column, or slot reference.

## Vector embeddings and spaces

`state.world` supports vector state, semantic embedding spaces, and companion offline lock files (`.embeddings.json`).

- **Embedding spaces**: declared via `spaces { space <name> { model: "...", revision: "...", dimensions: <d> } }`. Dimensions must be in `[8, 1024]`.
- **Vector tables and slots**: `table <name> : Vector [space(<name>)] [capacity(<n>)] [evicts]` and `slot <name> : Vector = "literal text"`.
- **`embeds(targetRow)`**: applied to Text tables (`table lines : Text embeds(lineVectors)`), automatically coupling text entries to companion vector rows sharing the same keys.
- **Vector literals & lock resolution**: authored string values in vector slots or `embed("...")` rule expressions are resolved into unit-normalized signed 8-bit vectors (`sbyte[]`, radius 127) using committed companion `.embeddings.json` lock files generated by `puck embed`.
- **SQL vector columns**: in `sql { ... }` blocks, columns can declare `VECTOR [REFERENCES <space>]` or `TEXT EMBEDS (<vectorTable>)`.

### Embedding diagnostics

| Code | Severity | Refusal |
|---|---|---|
| PUCK077 | Error | Embedding space is malformed: unknown or missing property, dimensions outside `[8, 1024]`, duplicate space name, or exceeds 16 spaces limit. |
| PUCK078 | Error | Vector row names no space and document has no default, names an undeclared space, or a non-Vector row declares a space. |
| PUCK079 | Error | Authored text in a vector slot or `embed(...)` has no lock entry; run `puck embed`. |
| PUCK080 | Error | Lock file's model, revision, or dimensions differ from the declared space; run `puck embed`. |
| PUCK081 | Error | Vector literal is not valid base64url, has wrong dimension count, contains prohibited -128, or fails unit sphere admission. |
| PUCK082 | Error | Vector literal or `embed(...)` appears where no vector value is admitted. |
| PUCK083 | Error | Vector row capacity × dimensions exceeds per-row cell ceiling or section budget. |
| PUCK084 | Error | Vector operation names a non-vector operand, mixes spaces, or provides invalid arguments to `nearest` / `remember`. |
| PUCK085 | Error | An `embed(...)` expression cannot determine its embedding space because no operand, destination, or default space is available. |
| PUCK086 | Error | `embeds(...)` appears on something other than a Text table, or names a colliding row name. |
| PUCK087 | Error | `mix` term count or weight is out of range, or a term weight is zero. |
| PUCK088 | Error | Vector filter `where` is not a keyed Bool row, or `exclude` condition is malformed. |
| PUCK_LINT_010 | Warning | A literal `mix` term weight's share is below 1/64 (weight stall); consider `mean` over a history table instead. |

## Decompiling

`WorldDecompiler.Decompile` opens every file with a one-time-import header comment (`let`/`template` cannot be
recovered on a re-run) and inverts `rules`/`shapes`/`placements`/`prototypes` into the sugar above, eliding a
`shape`'s `blend`/`smooth`/`rotation`/`scale`/`id` and a `placement`'s `yawDegrees`/`scale`/`solid` only on an exact
match to their emitter-side defaults. A shape's `name` elides onto the block's quoted-name syntax only when it is a
string; an authored JSON `null` (distinct from an absent key—a shape's own bounding box, say) has no such spelling
and prints as an ordinary `name: null` property instead, alongside the unnamed header form. Anywhere else in the
document, any object carrying a `$type` key—every
`ActionPredicate`/`ActionEffect`/`StateTransform` shape the dedicated sugar does not cover, and any
`Puck.World.Schema` extension arm—prints as `type(k: v, ...)` call-form rather than a brace object with a literal
`$type` key.

`state.world` always decompiles as the declaration block, printing each row as `table`/`slot` sugar when the WHOLE
row is representable without loss (`Decompiler/WorldDecompiler.State.cs`'s `CanSugarStateRow`: only the members
`table`/`slot` can spell, an `advance` rate that reduces to one literal, and — for a table's cells — only `key`,
`value`, `advance`, `behavior`) and falling back to `row { }` (the explicit form, printed through the same
`DecompileNamedBlock` any other nested block uses) otherwise — a partial sugar is never printed, matching the
"whole row or the escape hatch" rule every other sugar in this file follows.

`CanSugarPileRow` sugars an ordered `keysOf` row of boolean-`true` cells as `pile`, printing each cell's key as a
bare `token`. A pile's body always lowers to a `cells` array (`{ }` to `"cells": []`), so a row with no `cells`
member falls back to `row { }`. `CanSugarGridRow` sugars a `cellsOf` board as `grid` only when it is the SOLE `state.world` row lying
over its topology (a topology two board rows share, such as the garden's `board`/`lastLegal`/`plan` over
`chessBoard`, falls every one of those rows back to `row { }` — one `grid` statement mints one topology and one
occupancy row) and that topology carries nothing outside `name`/`origin`/`cellSize`/`width`/`depth`/`wrap`/`band`
(a topology authoring `directions`/`elementAliases` never sugars). `positions(...)` reprints the first OTHER row
whose `valuesFrom` names this topology, in document order; a topology more than one row's `valuesFrom` reaches
prints only that first one this way, and every other such row keeps its own `valuesFrom` and decompiles
independently (never lost, never duplicated). `lattices` itself prints only the topologies no `grid` sugar
consumed, omitted entirely when every entry was consumed.

The conditional effect (`"$type": "if"`) decompiles as `if Gate { }`, `then`/`else` printed through the same
per-effect dispatch as a rule's own body. `if`'s condition position is mandatory Gate grammar with no property-style
fallback the way a rule's own `gate` has (below): the core parser reads `if(...)` at statement position as this same
if-statement grammar, never as a generic call the way every other effect verb is, so there is no text a condition the
sugar cannot safely reproduce could be printed as that would both preserve its shape and reparse at all. Decompiling
such a document fails outright rather than emitting a `.puck` source that recompiles to a different predicate or does
not reparse. An `else` holding exactly one nested `if` prints as `else if Gate { }` rather than opening a fresh
`else { }` around it; either spelling recompiles to the identical JSON, so this is a readability choice, not a
correctness one.

A `when`/`gate:` predicate or a `row[key]`-shaped effect target that would not parse back to the same tree falls
back to the safer spelling instead of guessing: a rule's `gate` (an ordinary rule-body property) prints as
`gate: <call-form>` whenever any `compareValue` in it has a `left` starting with `(` (`ParseAtom` would otherwise
read that opening paren as a parenthesized sub-gate, not the start of the predicate's own operand—a real shape in
this corpus's own board-legality binds) or any `all`/`any` in it has fewer than two children (the parser's own
`and`/`or` accumulation never produces that wrapper from source text, so it has no bare-sugar spelling at all); a
`setState`/`addState`/`push`/`countdown`/`remove`/`schedule` effect prints as call-form whenever its `state`/`key`
target does not round-trip through `ExpressionSpelling.Print`/`TryParse` unchanged (a `key` shaped like `$expr:X[Y]`
is the confirmed case—printing strips the `$expr:` prefix, but reparsing then reads `X[Y]` as a `$cell:`
indirection instead), or whenever its `expression` field is a single token `ExpressionSpelling` would classify as a
bare `value`/`fromState` and the compiler's own RHS classifier would therefore never leave under `expression`.
An option's gate and a decision's `interrupt` take the same test and the same way out, spelled `gate: <call-form>`
and `interrupt: <call-form>`, both of which the option and decision bodies accept as ordinary properties.

A `compareState` carrying both `value` and `comparandState` (`ActionPredicate.CompareState` admits exactly one)
also stays call-form rather than having one of the two silently dropped, and so does a `comparison` or `kind`
spelled in any casing but its own enum member name—the engine's converter accepts those, but the sugar can only
reprint the canonical spelling, which would change the document.

Every sugar spelling reprints the WHOLE node or does not apply: any key it has no slot for—an effect's `target`,
a second right-hand-side key, a prototype row's non-object `document`—sends the node to call-form. A key present
with an explicit JSON null counts (`SetState.Value` and `CompareState.Value` carry no
`JsonIgnore(WhenWritingNull)`, so an engine-serialized document does carry `"value": null` beside a `fromState` or
an `expression`), and a `compareState` whose only comparand key is an explicit null likewise, since the sugar has
no text that recompiles to a null comparand.

## Diagnostics

New codes: PUCK002 (operand failed `ExpressionSpelling.TryParse`), PUCK003 (a row reference wasn't exactly one
state read), PUCK004 (chained comparison), PUCK005 (bad `: Kind`/`as Kind` word), PUCK006 (`bind` missing its kind),
PUCK007 (`bind` missing its initializer), PUCK009 (an `rhs` shape the target effect's fields can't carry—a string
on `addState`/`push`, seconds on `push`), PUCK010 (`schedule ... in` missing a time unit, or carrying one the seconds dimension does not admit), PUCK011 (`rule`
missing its name), PUCK012 (a second `when` in one rule/option), PUCK013 (`option`/`decision` structure: a missing
name or a missing `score`), PUCK014 (`onFailure` used more than once on one `transaction`), PUCK019 (nested
`transaction`), PUCK026 (a rule with no effect statements), PUCK028 (a placement authoring both the bare `solid`
flag and an explicit `solid { }`), PUCK029 (`decision` missing `periodSeconds`), PUCK035 (basis/import composition
refused), PUCK036 (a statement inside `prototypes`/`placements` that the section's grammar cannot carry), PUCK037
(`repeat`/`break`—not `if`, which lowers to the conditional effect above—has nothing to lower onto in this
vocabulary's straight-line rule body), PUCK039 (a compound-assignment operator; this vocabulary carries only
`setState`/`addState`). The
`table`/`slot`/`pile`/`grid` declaration refusals (PUCK049–PUCK066) are listed in
[State declarations](#state-declarations) above.

`PUCK008`/`PUCK013`/`PUCK014` also cover a rule-body-only keyword found where an ordinary statement belongs
(`option "x" { }` outside a `decision`, `push x = 1` outside a rule), named at the keyword's own span.

Every code is declared once in `Puck.Transpiler`'s `Diagnostics/PuckDiagnosticCodes`; report sites name a constant, never a literal.

Unit-dimension validation (PUCK024/PUCK025), the shape-type check (PUCK027), and the reference-resolution lint
family (PUCK_LINT_005 onward) live in the lowering/lint stages, not the parser.

## Reference-resolution lint

`Validation/PuckLinter.LintReferences` walks the LOWERED JSON (not the AST) for candidate reference sites, but a
document declaring `basis` builds its name catalog from the WHOLE composed basis/import graph
(`Composition/PuckDocumentComposer`, rooted beside the `sourcePath` argument—the same composition
`WorldSemanticValidator.ValidateComposedWorld` and the game boot path run) rather than from the document alone, so a
name only a basis or import supplies resolves correctly. It still only ever WALKS the document's own tree, never
the composed one (whose merged array order no longer lines up with the caller's `SourceMap`), so a reported
finding's JSON pointer always belongs to the document's own source. Whether an unresolvable name is REPORTED turns
on `WorldSemanticValidator.IsRootDocument` instead: a ROOT declares `schema: "puck.world.definition.v1"`, a `basis`, or
both, and every other document is a MODULE—a fragment some other, unknown root may import—so a name a module
cannot resolve standalone is never a finding; it may be a name that root supplies. (`imports` alone does not make a
root: a module may import sibling modules.) `puck lint` and `compile --validate` apply the same test before
composing and validating a document as a world. The one check that ignores this distinction entirely is shape-parent resolution: it is
scoped to sibling shapes in the same `shapes` array, a purely local scope no basis or importer could change, so it
always runs and always reports. Every other check is Information severity; the shape-parent check is Warning:

- `PUCK_LINT_005`—a `state`/`comparandState`/`fromState` name, or a `State` token inside a `compareValue`
  predicate's `left`/`right` operand or any `expression`/`score` operand, that resolves to no declared
  `state.*[].name` row. `left`/`right` are checked ONLY on a `compareValue`-discriminated object—every other
  `left`/`right` pair in the document model (e.g. an interaction row's property/placement-id pair) is a different
  name kind and is never read as an expression.
- `PUCK_LINT_006`—a `prototypeId` that resolves to no `prototypes[].id`.
- `PUCK_LINT_007`—a placement row's `parent` that resolves to no `placements.rows[].id`.
- `PUCK_LINT_008`—a `camera`/`spawnPoint` reference that resolves to no `cameras[].name`/`spawnPoints[].id`.
- `PUCK_LINT_009`—a `$`-prefixed operand name one edit apart from a `RuleFacts` channel prefix (`$tabl:` for
  `$table:`)—deliberately narrow: a real extension prefix (`$board`, `$physics`, ...) sits far from every
  `RuleFacts` name and is never flagged. Purely local (a fixed known-prefix list, never the document's own
  catalog), so it runs for a module exactly as it does for a basis-declaring document.
- `PUCK034` (Warning)—a `shape`'s `parent` that resolves to no sibling `name` in the SAME `shapes` array.

`$`-prefixed and dotted (import-alias) names are never checked against a declared-row set.

## Editor tooling

The [VS Code extension](../../editors/vscode/README.md) starts this server through `puck lsp` when a Puck document opens. Its setup guide covers CLI paths, packaging, and restarting the server.

`Lsp/PuckLanguageServer.cs` offers completion for the gate/effect/rule keywords (`if`/`else` included), `table`/
`slot`/`pile`/`grid`/`row` and their `bounds`/`advance`/`capacity`/`behavior`/`dimensions`/`wrap`/`cellSize`/
`origin`/`band`/`empty`/`positions`/`inverse` modifiers, and the `Puck.State` predicate/effect/`CellKind`
discriminators; hover on a declared `state` row name (its `kind`, and `capacity`/`domain` when present, via a
best-effort lower of the open document) and on each declaration keyword and modifier itself; and `documentSymbol`
entries for `rule` blocks (with `when`/`bind`/`decision` children) and for a `state.world` declaration block (with
`table`/`slot`/`pile`/`grid`/`row` children — a table's own cell keys and a pile's own tokens as their children in
turn).

A completion request positioned right after a dot-access read (`vitals.`, or a partly-typed `vitals.he`) completes
the named table's own cell keys instead of the generic keyword list. The row name comes from the raw line text, not
a clean parse—the surrounding statement is very often still incomplete while this fires—and the lookup itself first
tries an ordinary parse and falls back to truncating the buffer at the cursor and synthesizing the closing
braces/brackets/parens it is still owed, so an unclosed rule or block being typed for the first time still resolves
far enough to see its declared rows.

Hover also shows declarations for document-level `let` constants, templates (including parameter defaults),
import aliases, template and lambda parameters, loop variables, and rule bindings. Local parameters and bindings
are resolved within their enclosing scope. Contiguous `//` comments immediately above a declaration accompany
its popup. Collection functions show their signatures and behavior; scalar function arity, domain, and operation
come from `Puck.State.ExpressionVocabulary`. Declaration cards quote source rather than evaluating it, so units
and expressions remain as authored and recoverable declarations still work while the document has syntax errors.
Operation names (including `worldPoint`, `anchor`, and `lookAt`), fields, and named-operation-argument hover follow the generated World schema, including embedded creation documents,
palette arrays, noise blocks, and shape rows. Popups carry the owning field description, type, declared default,
enum choices, and accepted DSL units when applicable. Shape primitive and blend names show their enum documentation.
Creation descriptions come from the authoring model XML comments; no editor-specific copy of the field list is maintained.
Comments, string contents, and whitespace do not trigger symbol hover. Imported member definitions and inferred
expression result types are not resolved by these cards.

`Formatting/PuckFormatter.cs` is a meaning-preserving pass: formatting a document never changes what it compiles
to. A `$name:segment` reserved channel—including a folded `$zones[...]` selector—is one opaque token, so its
internal colons never splice `$physics:quiescent` into `$physics: quiescent`; a backquoted name (`` `N,S,E,W` ``,
`` `seat-1` ``, per `Puck.State.ExpressionSpelling`) is opaque the same way a `"..."` string literal is, so its
commas never gain the space the comma rule inserts everywhere else. A colon that already carries a single space
before it—`bind name : Kind`, a `when Gate : Kind`/`as Kind` suffix, and a ternary's `? a : b`, all
`ExpressionSpelling`'s own spacing—keeps that space rather than being squeezed into an unspaced property colon;
only a run of two or more spaces before a colon is ever collapsed. The decompiler's one-time-import header comment,
and every other comment, survives formatting in place.

Unused-binding analysis follows references in loops, array indexing, lambda bodies, interpolated strings, and exports.

## Documentation

📚 [Worlds and federation](../../docs/architecture/worlds.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
