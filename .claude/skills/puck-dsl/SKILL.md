---
name: puck-dsl
description: "Covers the `.puck` authoring language's core: the one-spelling grammar (PUCK040), `let`/`template`/`import`/`export`, the units table, compile-time `for` and the collection builtins (`map`/`filter`/`reduce`/`range`/lambdas, PUCK041/042), string/indexing forms, the `Puck.State`-delegated expression vocabulary, and the `puck compile`/`decompile`/`lint`/`fmt`/`lsp`/`migrate` CLI verbs and PUCKnnn diagnostic family. Use whenever writing or editing any `.puck` file, running those verbs, diagnosing a PUCKnnn error, or converting a hand-written JSON world/cartridge document into idiomatic DSL. Does not teach vocabulary-specific semantics: world-document sugar (`rule`/`decision`/`shape`/`placements`/`prototypes`, world refusals, booting `--world`) belongs to `puck-world`; SDF shape/prototype/creation authoring belongs to `sdf-authoring`; the cartridge vocabulary and forge compile/play loop belong to `rom-forge`; emulator hardware behavior belongs to `gaming-bricks`."
---

# The `.puck` authoring language

`.puck` is the one authoring language behind two document vocabularies today:
`puck.world.def.v1` (`Puck.World.Transpiler`) and `puck.cartridge.v1`
(`Puck.GamingBricks.Transpiler`). Both are peers riding one schema-agnostic
core, [`Puck.Transpiler`](../../../src/Puck.Transpiler/README.md) — the
parser, formatter, diagnostics, module resolver, and unit arithmetic that
knows nothing about what a document's sections mean. This skill owns that
core, plus the source-of-truth facts and CLI loop that apply to `.puck`
regardless of vocabulary. What a block MEANS is the owning vocabulary skill's
job — see **Route adjacent work** below.

The current instruction outranks this skill. A fact here that argues against a
change you were asked to make is stale; correct it in the same change.

## What is and isn't DSL-authored today

A document's own `schema:` field selects its vocabulary — never the filename.
Every tracked `.puck` file is spelled as a bare `*.puck`; the two shipped
cartridges additionally carry `.cgb.` in their name as an author convention,
not a parser rule. Today, `.puck` authors: both shipped CGB cartridges
(`tetris.cgb.puck`, `hgb-mirror.cgb.puck`, each gated byte-for-byte against
its committed `.cartridge.json` twin — see below), the avatar and tool worlds
under `src/Puck.World/Assets/worlds/` (`avatars/moth.puck`,
`moth-courtyard.puck`, `tools/hgb-mirror.puck`, `tools/hgb-compare.puck`), and
the `Puck.World.Transpiler` test fixtures. **The live game's own document,
`Assets/worlds/puck.world.json`, and the one AGB cartridge,
`Assets/cartridges/pip.agb.cartridge.json`, have no `.puck` source at all** —
they are authored and shipped as raw JSON. Say so plainly rather than treating
"DSL is the primary authoring surface" as already true everywhere.

**Every committed source is regeneration-gated.** `tests/Puck.GamingBricks.Transpiler.Tests/CartridgeRoundTripTests.cs`
(cartridges) and `tests/Puck.World.Transpiler.Tests/ShippedWorldsParityTests.cs`
(every `*.puck` under `src/Puck.World/Assets/worlds`) compile each committed
source and compare it byte-for-byte against the document beside it. After
editing a `.puck`, recompile it. Commit both halves for cartridges. World JSON listed in `build/WorldAssets.targets` is generated during the CLI build and ignored; commit only its source. A world document with a
`.puck` source is gated only through that source; the decompile round-trip
corpus covers the JSON-authored worlds.

## A runtime hazard: reload and save don't know `.puck`

`Puck.World`'s boot path transparently compiles a `.puck` world in memory
(`PuckWorldLoader.TryResolveWorld`, `src/Puck.World/PuckWorldLoader.cs:33`) —
`--world foo.puck` boots directly off DSL source. Nothing else in the running
console knows `.puck` exists:

- `world.reload` re-reads the current document's `SourcePath` through
  `WorldDefinitionFileSource.TryLoad`, which is JSON-only, and fails on a
  `.puck` source.
- `world.save` with no argument writes canonical JSON back over `SourcePath`
  — if that path is the `.puck` file, the save destroys every `let`,
  `template`, and comment in it.

This is today's behavior, not a design choice; a fix is pending. Until then:
save a `.puck` world to an **explicit** JSON path (`world.save <path>.json`),
edit the `.puck` source directly, and restart to pick up the change — never
`world.reload` or a bare `world.save` against a `.puck`-booted world.

## The author verify loop

```
puck fmt <file> --check       # DSL formatter — see disambiguation below
puck lint <file> --strict
puck compile <file> --validate
```

Then, for a world, run it (`dotnet run --project src/Puck.World -c Release --
--exit-after-seconds 2`, per `CLAUDE.md` rule 3); for a cartridge, verify
through the emulator battery, never a standalone driver (`gaming-bricks`,
`rom-forge`). A committed source additionally owes the byte-for-byte
regeneration gate above before merge.

**`puck fmt` is not `puck format`.** `puck fmt` (`src/Puck.Cli/Transpiler/FormatCommand.cs`,
class `PuckFmtCommand`) is this language's own formatter and touches only
`.puck` files. `puck format` (`src/Puck.Cli/Format/FormatCommand.cs`) is the
unrelated repo-wide C#/whitespace sweep `CLAUDE.md` and `boy-scout` already
own. They share a name fragment and nothing else — never call one "the format
command" without saying which.

| Verb | Real flags | Does |
|---|---|---|
| `puck compile <path>` | `-o/--output`, `-w/--watch`, `--strict`, `--validate`, `--bundle` | Parses, resolves/bundles imports, lowers via the vocabulary the parsed `schema:` names, optionally validates the engine schema, writes `<source>.cartridge.json`/`.world.json` by default. `--validate` self-installs the machine catalog first — no separate registration step. `--watch` recompiles on `*.puck` changes (250 ms debounce). |
| `puck decompile <path>` | `-o/--output`, `--overwrite` | Reads `schema` from the JSON, routes to the matching decompiler. **One-way**: `let`/`template`/`for` never reproduced; output opens with a one-time-import header comment. |
| `puck lint <path-or-dir>` | `-s/--strict` | Recurses `*.puck` for a directory. Runs cartridge diagnosis first, then `PuckLinter.Lint` (syntax), lowers, then (root documents only) semantic validation and reference-resolution lint. Self-installs the machine catalog like `compile --validate`. |
| `puck fmt <path-or-dir>` | `-c/--check` | Recurses `*.puck`. Parses and prints the tree. Reader and printer share one escape grammar (`PuckStrings`), so the printer writes only what the reader reads back and what a document compiles to cannot move. What is the author's and survives: every comment, wherever the grammar admits one; a blank-line run's length; the line breaks inside an array, object or argument list; a `,` at a line end; a numeric literal's base; a raw fence; and a name's bare-or-quoted spelling. The one comment that moves is one written inside a construct's header, between its first word and its `{`: a `//` there prints above the statement, because in place it would swallow the brace, and a `/* */` written after the header's name prints right after the first word. A source it cannot parse is refused by name and left alone — exit 2, in directory mode as well; `--check` exits 1 for a file that merely needs formatting. |
| `puck migrate <name> <path>` | `-c/--check` | Applies one registered rewrite — a `PuckMigration` over `Puck.Transpiler.Rewriting.PuckSyntaxRewriter` — to every `*.puck` under the path (or to one file) and prints each through `PuckPrinter`. Two verdicts per changed source: its two compiled documents must be equal excluding the members the migration declares in `ReshapedMembers` (`/`-separated paths, `*` matching one key or index, a path covering everything beneath it), and its comments in reading order must be unchanged unless the migration declares `ReshapesComments` (trivia never reaches the document, so the member declaration cannot speak for it). Blank-line runs and the compile-time layer surviving rest on the printer `fmt` is gated on, not on a verdict of the run's own. All-or-nothing: every source is parsed, rewritten and printed before anything is written. A source the migration leaves alone is neither compiled nor written. A difference outside the declared members, an undeclared comment change, a source that does not parse, one whose migrated text does not parse back, one it would change that does not compile, or one it would change that cannot be opened for writing refuses the whole run (exit 2, nothing written); `--check` exits 1 for work outstanding. Each write stages a sibling `.migrate-tmp` and moves it over the destination, so no file is ever left truncated; the one thing not atomic is the *set* — a move failing after earlier moves leaves those destinations migrated and the refusal names them. The registry carries only whatever reshape is mid-flight — a migration lands with the reshape it serves and is deleted once it has run — and an unknown name lists what is registered. |
| `puck lsp` | none | `PuckLanguageServer` over stdio: completion, hover, `documentSymbol`. |
| `puck test <path-or-dir>` | `--host`, `--world-artifact`, `--keep <dir>` | Compiles a `.puck` source, generates one test world per `test` block (`<stem>--<slug>`), boots each through the real `Puck.World` executable headless twice, refuses a world whose two exports or manifests differ, and prints one line per verdict (name, pass/fail, the gate as written, the values it read, the firing tick). A `*.world.json` document runs as it stands; a directory contributes both and skips a source with no test block. Exit 0 all passed, 1 a failing verdict or an unexpected step outcome, 2 usage. The construct: [Testing a world](../../../docs/authoring/testing-a-world.md). |
| `puck vocabulary` | `-c/--check` | Writes [the world vocabulary](../../../docs/reference/world-vocabulary.md) from `Puck.World.Transpiler`'s construct table — every `puck.world.def.v1` construct's keyword, members, the document member it lowers to, and what the printer requires before it prints a node back as that construct. `--check` exits non-zero and names the first differing line when the page disagrees with the table. The cartridge vocabulary has no such page. |

Without `--output`, `compile` writes `<source>.cartridge.json` for a cartridge
document and `<source>.world.json` otherwise, selected by the parsed `schema:`.

## Grammar, in brief

One spelling per shape, at every depth, including inside object literals: a
container is always a block (`host { }`, `cameras [ ]`), a scalar always takes
a colon (`documentId: "puck"`); a colon in front of `{`/`[` is **PUCK040**, the
single most common mistake. `let name = expr` is a compile-time constant;
`template name(param, param2 = default) { }` is a parametric block; `import
"path" [as alias]` and `export read|action|binding name, name2` compose
modules. Units (`s ms m mm cm hz % pct deg rad`) convert in the core; *which*
field is dimensioned is the vocabulary's own table (unit on an uncovered field
is **PUCK024**, an inadmissible unit is **PUCK025**). A comparison yields `1`
or `0`, never a JSON boolean. Strings come in four forms: plain `"..."`
(never interpolates), interpolated `$"...{expr}..."`, raw `"""..."""` (no
escapes), and raw+interpolated `$"""..."""`.

A world's own behaviour is written beside it, as `test "name" { given { } when { }
expect { } }` at the document's root: `given` writes boot cells, `when` lays
`ticks n` and `seat<n>: <command line>` steps on a tick grid, and `expect`
carries one rule-gate expression per line. Each block lowers to a generated test
world and the enclosing document carries no trace of it. `with module(arguments)`
is refused by name until `use` exists. Refusals are **PUCK104** (the
declaration's shape) and **PUCK105** (a line a generated world cannot carry).

`for i in range(0, n) { ... }` and `for (item, index) in [...] { }` expand at
compile time — the document carries the rows they produce, never the loop
(distinct from a cartridge rule's play-time `repeat`). The collection builtins
— `range`, `length`, `concat`, `map`, `filter`, `reduce`, `distinct`, `sort`,
`groupBy` — are the document language's own vocabulary, evaluated while
lowering, and take a lambda (`item => expr`, `(item, index) => expr`) as an
argument to exactly one of them and nothing else (a lambda elsewhere is
**PUCK042**; an unevaluable call is **PUCK041**). Named scalar functions
(`squareRoot`, `sine`, `hexIndex`, `select`, ...) are not this project's own:
they come from `Puck.State.ExpressionVocabulary`, the same table a compiled
rule runs against, so a spelling can never mean one thing in a rule and
another in a document. Full detail, examples, and the indexing/nesting rules:
[references/grammar.md](references/grammar.md).

**Control flow, call-form gates, and compound assignment are core-parsed for
every vocabulary, but whether a vocabulary's rule shape can CARRY them is its
own answer** — refused as **PUCK037** (control flow the vocabulary's rule
shape can't carry), **PUCK038** (call-form gate where only comparisons are
legal), or **PUCK039** (an assignment operator the vocabulary's effects don't
carry). The world vocabulary's rule body lowers `if`/`else if`/`else` to the
state engine's conditional effect, reusing the same predicate lowering `when`
uses for the condition; `repeat`/`break` still have nothing to lower onto
there and PUCK037 still fires for them. Cartridge rules support all three —
`if`/`repeat`/`break` (see `rom-forge`).

## Known traps, verified against the current tree

| Trap | Status |
|---|---|
| `puck compile --validate` needing a separate engine-registration step | Not real. `CliWorldVocabulary.EnsureInstalled()` runs inside `compile --validate` and `lint` themselves. |
| `fmt` mutating what a document compiles to | Not real. `StringGrammarTests` pins reader and printer to one escape grammar over a generated value corpus, and `ProjectionLawTests`/`FormatProjectionLawTests` prove format-then-compile equals the original over every shipped source in both vocabularies. |
| Lint skipping a name only a `basis`/import supplies | Fixed. `PuckLinter.References` composes the whole basis/import graph before building its name catalog. A *module* (no `schema`/`basis` of its own) still never gets such names reported missing — a fragment cannot know what an unknown root will supply, and that is by design, not a bug. |
| A `let` referenced only inside a `when`/effect operand | Reported as unused by **PUCK_LINT_001** anyway. Operand text is opaque to the usage scan (it is handed whole to `Puck.State.ExpressionSpelling`), so a constant used only there looks unreferenced to the linter even though it is live. Verified by compiling a `let` used solely in a `when` gate. |
| A `: Kind`/`as Kind` comparison-kind suffix beside `and`/`or` | Binds only to the ONE comparison it follows, never the enclosing chain, though it prints as if it scoped the whole thing. Wrap the annotated comparison in its own parens. |
| Shapes/prototypes having "no sugar" | Stale. `shape Type "name" { }` and `prototypes { prototype "id" { document { } } }` are ordinary named blocks with full sculpting support — `avatars/moth.puck` authors 230 `shape` statements this way. |

## Diagnostics

Every code is declared exactly once, in
[`PuckDiagnosticCodes.cs`](../../../src/Puck.Transpiler/Diagnostics/PuckDiagnosticCodes.cs)
(`PUCK001`–`PUCK098`, `PUCK_LINT_001`–`PUCK_LINT_010`), with the meaning as its
XML doc comment. The two vocabulary READMEs narrate the same codes with
examples: [`Puck.Transpiler/README.md`](../../../src/Puck.Transpiler/README.md)
for core codes, [`Puck.World.Transpiler/README.md`](../../../src/Puck.World.Transpiler/README.md)
for world-specific ones and the reference-resolution lint family. Read the
source rather than a copy here — a catalog that can drift is worse than
asking the code. The codes an author hits most, and what to do about each, are
in [references/diagnostics.md](references/diagnostics.md).

## Decompiling is one-way, always

`let`, `template`, and unexpanded `for`/lambda bodies exist only in source and
are gone from the compiled JSON; decompiling recovers none of them and opens
the file with a header comment saying so. Decompiling is for one-time import
of a JSON document into DSL source, never a round-trip step in an ongoing
edit loop — after the first decompile, edit the `.puck`, not the JSON.

## Route adjacent work

| Skill | Route there for |
|---|---|
| `puck-world` | World-document sugar meaning — `rule`/`decision`/`when`/effect semantics, `placements`/`prototypes` section rules, world-specific refusals, booting `--world`, the mutation/console surface. |
| `sdf-authoring` | Sculpting a creation's shapes/materials/rig in `.puck` — primitive choice, blends, scope rules, the shape budget. |
| `rom-forge` | The cartridge vocabulary's own sections and opcodes, `puck compile`/`forge.open`/`forge.play` verification loop, ROM/save semantics. |
| `gaming-bricks` | Emulator hardware behavior once a cartridge is compiled — CPU/PPU/APU/timer accuracy, the Post batteries. |
| `symbol-analysis` | A `.puck` name/state-row/prototype-id resolution question is **not** covered by `references`/`declarations` (C#-only) — use `puck lint`'s `PUCK_LINT_00x` family or the LSP instead. |
| `content-search` | A `.puck`-only fact (a `let` name, a comment, an unexpanded `for` body) may not appear in the compiled JSON and vice versa — search whichever form the question actually needs. |
