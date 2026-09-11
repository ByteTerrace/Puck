# Puck.World.Transpiler

Compiles the `.puck` authoring language to `puck.world.def.v1` JSON, and decompiles the other way. `.puck` is an
authoring layer: JSON stays the wire form and the checked-in source of every shipped world.

## Pipeline

`Parsing/PuckParser.cs` (source → `Ast/*` syntax tree) → `Lowering/WorldDocumentEmitter.cs` (tree → JSON) /
`Decompiler/WorldDecompiler.cs` (JSON → tree → source) → `Validation/{PuckLinter,WorldSemanticValidator}.cs` →
`Lsp/PuckLanguageServer.cs` (editor integration). `Modules/ModuleResolver.cs` composes `import`s.

## Grammar

### Documents and blocks

```
schema: "puck.world.def.v1"
basis: "worlds/standard.basis.json"

let name = expression                 // compile-time constant
template name(param, param2 = default) { ... }
import "path" [as alias]
export read|action|binding name, name2

identifier [target] [name] { statements }   // a structural block; up to two leading tokens name it
identifier: expression                       // a property
identifier [ elem, elem2 ]                    // an inline array property (outside a rule body — see below)
identifier(k: v, k2: v2)                      // a call expression statement
identifier                                    // a bare flag statement, only where nothing else could follow on
                                               // the line (e.g. a placement row's bare `solid`, standing in for
                                               // `solid: { margin: 0 }`)
```

Expressions: `+ - * /` (C precedence via additive/multiplicative), `.` member access, `(...)` calls,
`[a, b]` arrays, `{ k: v }` objects, `a..b` ranges, `#rrggbb[aa]` colors, and number literals with an optional unit
suffix (`s ms hz rad deg m mm cm % pct`). A call `name(k: v, ...)` lowers to `{"$type":"name","k":v,...}` — the
universal escape hatch for any `$type` object; positional args are special-cased only for `orbit`/`fov`.

### `when` gates

```
when Operand cmp Operand [ (":"|"as") (Int|Fixed) ]     // cmp: == != < <= > >=
when Gate and Gate and Gate                              // -> one flat `all`
when Gate or Gate                                         // -> one flat `any`
when not Gate
when (Gate)                                                // one opaque child; never flattened into its parent
```

`and`/`or`/`not`/`as`/`when` are reserved only inside a gate. An `Operand` is opaque text handed whole to
`Puck.State.ExpressionSpelling` — the DSL never re-implements that grammar; `row`/`row[key]` reads, `$name:segment`
reserved channels, and a `$zones[...]`-folded selector all pass through untouched. A bare comparison between two
single state reads lowers to `compareState`; anything else, or an explicit `: Int`/`: Fixed`/`as Int`/`as Fixed`
suffix (which always forces it, even for two simple reads), lowers to `compareValue`.

### Rules

```
rule "name" {
    when Gate
    bind localName : Int|Fixed = <operand>
    mode: Edge|Level
    forEach: "rowName"
    zones: [...]
    decision { ... }                          // see below
    <effect statement>*
}
```

Effect statements:

```
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
```

`row[key]` (a *row reference*) is read as one span — a name plus zero or more adjacent `[...]` groups — and
resolved through `ExpressionSpelling` to exactly one state-read token; `row[key] = rhs` only means a cell
assignment inside a rule/transaction/option/onFailure/onNoChoice body — the same `identifier[...]` outside such a
body still means the existing inline-array-property sugar. An `rhs` is a string literal (`Text`), a number carrying
the `s` unit (`ValueSeconds`), or opaque operand text the lowering stage classifies into `Value` /
`FromState`+`FromKey` / a verbatim `Expression`.

### Decisions

```
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
        neighbors: { range: 10, candidateBudget: 12, maxCandidates: 4, halfAngleDegrees: 180, requiresLineOfSight: false, retainCurrent: true }
        <effect statement>*
    }
}
```

`periodSeconds` and each option's `score` are required.

### Shapes and placements

`shape Type "name" { ... }` and `placements { policy: { ... } placement "id" { ... } }` are ordinary named/targeted
blocks — no dedicated grammar beyond the bare flag statement above (`solid` inside a `placement` row).

## Decompiling

`WorldDecompiler.Decompile` opens every file with a one-time-import header comment (`let`/`template` cannot be
recovered on a re-run) and inverts `rules`/`shapes`/`placements` into the sugar above, eliding a `shape`'s
`blend`/`smooth`/`rotation`/`scale`/`group`/`id` and a `placement`'s `yawDegrees`/`scale`/`solid` only on an exact
match to their emitter-side defaults. Anywhere else in the document, any object carrying a `$type` key — every
`ActionPredicate`/`ActionEffect`/`StateTransform` shape the dedicated sugar does not cover, and any
`Puck.World.Schema` extension arm — prints as `type(k: v, ...)` call-form rather than a brace object with a literal
`$type` key.

A `when`/`gate:` predicate or a `row[key]`-shaped effect target that would not parse back to the same tree falls
back to the safer spelling instead of guessing: a rule's `gate` (an ordinary rule-body property) prints as
`gate: <call-form>` whenever any `compareValue` in it has a `left` starting with `(` (`ParseAtom` would otherwise
read that opening paren as a parenthesized sub-gate, not the start of the predicate's own operand — a real shape in
this corpus's own board-legality binds) or any `all`/`any` in it has fewer than two children (the parser's own
`and`/`or` accumulation never produces that wrapper from source text, so it has no bare-sugar spelling at all); a
`setState`/`addState`/`push`/`countdown`/`remove`/`schedule` effect prints as call-form whenever its `state`/`key`
target does not round-trip through `ExpressionSpelling.Print`/`TryParse` unchanged (a `key` shaped like `$expr:X[Y]`
is the confirmed case — printing strips the `$expr:` prefix, but reparsing then reads `X[Y]` as a `$cell:`
indirection instead), or whenever its `expression` field is a single token `ExpressionSpelling` would classify as a
bare `value`/`fromState` and the compiler's own RHS classifier would therefore never leave under `expression`.
`option`/`interrupt` have no such fallback (their only grammar is a bare `Gate`), so they print through regardless.

## Diagnostics

New codes: PUCK002 (operand failed `ExpressionSpelling.TryParse`), PUCK003 (a row reference wasn't exactly one
state read), PUCK004 (chained comparison), PUCK005 (bad `: Kind`/`as Kind` word), PUCK006 (`bind` missing its kind),
PUCK007 (`bind` missing its initializer), PUCK009 (an `rhs` shape the target effect's fields can't carry — a string
on `addState`/`push`, seconds on `push`), PUCK010 (`schedule ... in` missing the `s` suffix), PUCK011 (`rule`
missing its name), PUCK012 (a second `when` in one rule/option), PUCK013 (`option`/`decision` structure: a missing
name or a missing `score`), PUCK014 (`onFailure` used more than once on one `transaction`), PUCK019 (nested
`transaction`), PUCK026 (a rule with no effect statements), PUCK028 (a placement authoring both the bare `solid`
flag and an explicit `solid: { }`), PUCK029 (`decision` missing `periodSeconds`).

Unit-dimension validation (PUCK024/PUCK025), the shape-type check (PUCK027), and the reference-resolution lint
family (PUCK_LINT_005 onward) live in the lowering/lint stages, not the parser.

## Reference-resolution lint

`Validation/PuckLinter.LintReferences` walks the LOWERED JSON (not the AST): a document declaring `basis` is
skipped outright (one `PUCK_LINT_005` note, not a real finding) since a basis-composed document's own rows may be
inherited, invisible to a single-file pass. Otherwise it checks, all Information severity except the last:

- `PUCK_LINT_005` — a `state`/`comparandState`/`fromState` name, or a `State` token inside a `left`/`right`/
  `expression`/`score` operand, that resolves to no declared `state.*[].name` row.
- `PUCK_LINT_006` — a `prototypeId` that resolves to no `prototypes[].id`.
- `PUCK_LINT_007` — a placement row's `parent` that resolves to no `placements.rows[].id`.
- `PUCK_LINT_008` — a `camera`/`spawnPoint` reference that resolves to no `cameras[].name`/`spawnPoints[].id`.
- `PUCK_LINT_009` — a `$`-prefixed operand name one edit apart from a `RuleFacts` channel prefix (`$tabl:` for
  `$table:`) — deliberately narrow: a real extension prefix (`$board`, `$physics`, ...) sits far from every
  `RuleFacts` name and is never flagged.
- `PUCK034` (Warning) — a `shape`'s `parent` that resolves to no sibling `name` in the SAME `shapes` array.

`$`-prefixed and dotted (import-alias) names are never checked against a declared-row set. A leaf world document
composed only by import (a game module a parent's `imports` pulls in, or that imports siblings of its own, e.g.
`games/{klondike,spider,freecell}.world.json` under `games/solitaire.world.json`) can reference a row/prototype a
sibling or parent declares — invisible to this pass, which resolves only within the one document it was given. That
shows up as a true, if unhelpful, `PUCK_LINT_005`/`PUCK_LINT_006` note on such a document linted standalone; it is
not a false positive in the basis sense, just the limit of single-file lexical resolution.

## Editor tooling

`Lsp/PuckLanguageServer.cs` offers completion for the gate/effect/rule keywords and the `Puck.State`
predicate/effect/`CellKind` discriminators, hover on a declared `state` row name (its `kind`, and `capacity`/`domain`
when present, via a best-effort lower of the open document), and `documentSymbol` entries for `rule` blocks (with
`when`/`bind`/`decision` children). `Formatting/PuckFormatter.cs` treats a `$name:segment` reserved channel —
including a folded `$zones[...]` selector — as one opaque token, so its colon-spacing rule never splices
`$physics:quiescent` into `$physics: quiescent`.
