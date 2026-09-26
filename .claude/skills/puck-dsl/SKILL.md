---
name: puck-dsl
description: "Covers the `.puck` authoring language core every vocabulary shares: the one-spelling grammar, `let`/`template`/`import`/`export`, units, compile-time `for` with the collection builtins and lambdas, strings and indexing, the `Puck.State`-delegated expression vocabulary, the `.puck` CLI verbs (`puck compile`/`decompile`/`lint`/`lsp`/`migrate`/`test`, and `puck format` over `.puck`), and the PUCKnnn diagnostic family. Use whenever writing or editing any `.puck` file, running those verbs, diagnosing a PUCKnnn error, or converting a JSON world/cartridge document into idiomatic DSL. Vocabulary meaning belongs elsewhere: world-document sugar (`rule`/`decision`/`placements`), world refusals and booting `--world` to `puck-world`; SDF shape/prototype/creation authoring to `sdf-authoring`; the cartridge vocabulary and forge compile/play loop to `rom-forge`; emulator hardware behavior to `gaming-bricks`."
---

# The `.puck` authoring language

`.puck` is the one authoring language behind two document vocabularies today:
`puck.world.definition.v1` (`Puck.World.Transpiler`) and `puck.cartridge.v1`
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
not a parser rule. `.puck` authors both shipped CGB cartridges
(`tetromino.cgb.puck`, `hgb-mirror.cgb.puck`, each gated byte-for-byte against
its committed `.cartridge.json` twin — see below), the worlds under
`src/Puck.World/Assets/worlds/` (avatars, games, tools, `moth-courtyard.puck`),
the asset packages under `worlds/`, and the `Puck.World.Transpiler` test
fixtures; `git ls-files '*.puck'` is the current list. **The live game's own document,
`Assets/worlds/puck.world.json`, and the one AGB cartridge,
`Assets/cartridges/pip.agb.cartridge.json`, have no `.puck` source at all** —
they are authored and shipped as raw JSON. Say so plainly rather than treating
"DSL is the primary authoring surface" as already true everywhere.

**Cartridge sources are regeneration-gated; world sources are not paired.**
`tests/Puck.GamingBricks.Transpiler.Tests/CartridgeRoundTripTests.cs` compiles
each committed cartridge source and compares it byte-for-byte against the
`.cartridge.json` beside it; commit both halves. A world source has no committed
document: the game's build compiles every `.puck` under
`src/Puck.World/Assets/worlds` into its own output (`build/WorldAssets.targets`),
and `WorldDocumentOutputLawTests` holds that output to exactly what each source
compiles to and the source trees to tracked files only. Never write a world
document beside its source. A document reference names the document
(`basis: "avatars/moth"`, `import "klondike"`), never a file form, and resolves
to the `.puck` source that emits that name, whatever that source's stem, and to
the `.world.json` document otherwise (`WorldDocumentName`, `PuckDocumentComposer`).
A source carries exactly the names it emits (`WorldCompilation.EmittedNames`): an
ordinary source its stem, a composition the worlds it declares and not its
stem unless it declares a world of that name, and a module library (no
`schema`/`basis`, no world, only `let`/`template`/`module`/`.puck` import/`test`
at its top level) none, so a reference resolves past a library or a composition
to the document of that name. Which source emits a name is read from one index
per directory (`WorldSourceIndex`) that parses each source and compiles none
(`WorldSourceDeclaration`); the composer, the tree compile and the official
build all resolve names through it, and `EmittedNameLawTests` holds it to agree
with what every tracked source compiles to. A world is therefore declared at the
top level of its source under a written name — never inside a `for` or computed
— or the compile refuses it. A source wins only over the document of its own
exact stem; any other two files carrying one name, ignoring case, are refused
(`DocumentName.Collision`).
The decompile round trip
(`tests/Puck.State.Rebuild.Corpus`) closes on every shipped world source — the
worlds under `src/Puck.World/Assets/worlds`, the packages under `worlds/`
(compositions decompiled together, module fragments held through their
importers) and the transpiler samples — less a shrinking exemption ledger whose
every row is held to its reason.

**Every shipped source is lint- and format-gated.** `tests/Puck.Cli.Tests/ShippedSourceLintLawTests.cs`
runs `puck lint --strict` over every tracked source under `worlds/`,
`src/Puck.World/Assets` (cartridges included) and `tests/Puck.World.Verdicts`, and
`FormatProjectionLawTests` holds every tracked source to what `puck format` prints. A
shared `basis` that declares `schema` is a root and lints as a whole world, so it
must be valid alone: Parlor's basis declares no bodies and each game owns its seat.

## Reload and save against a `.puck` world

`--world foo.puck` boots directly off DSL source (`PuckWorldLoader`), and the
running console reads `.puck` the same way:

- `world.reload` re-reads the current origin through
  `WorldDefinitionLoader.TryLoadFileForAdmission` with the `PuckDocumentComposer`
  document source, so an edited `.puck` world (and its basis chain) recompiles in place.
  The composer compiles through `WorldCompileCache`, so only the sources whose
  recorded file facts (their bytes, their modules', their locks' and assets',
  every probed path, and the listing and sources of each directory a basis
  name resolved in) moved recompile; an unchanged one is served as held.
  A re-read that no longer compiles or validates leaves the running world
  untouched. `world.load <path>.puck` reads through the same path.
- `world.save` never writes canonical JSON over `.puck` source: with no
  argument against a `.puck` origin, or with a `.puck` target, it is refused by
  name and nothing is written. Save a live edit to an explicit JSON path
  (`world.save <path>.world.json`) and port it back into the `.puck` source by
  hand, or edit the `.puck` source directly and `world.reload`.

## The author verify loop

```
puck format <file> --check    # one formatter for C# and .puck; see below
puck lint <file> --strict
puck compile <file> --validate
```

Then, for a world, run it (`dotnet run --project src/Puck.World -c Release --
--exit-after-seconds 2`, per `CLAUDE.md` rule 3); for a cartridge, verify
through the emulator battery, never a standalone driver (`gaming-bricks`,
`rom-forge`). A committed source additionally owes the byte-for-byte
regeneration gate above before merge.

`puck format` (`src/Puck.Cli/Format/FormatCommand.cs`) formats every source kind Puck owns: a `.puck` file goes
through `PuckPrinter` (`src/Puck.Cli/Format/PuckSourcePhase.cs`, the `puck` pass), a `.cs` file through the C#
phases `boy-scout` describes. The language server's formatting request prints through the same `PuckPrinter`, so
the editor and the CLI write identical text. The printer has one layout: `PuckPrinter.IndentWidth` (2) spaces a
level, with no indent or tab option anywhere, and the editor's `tabSize`/`insertSpaces` are ignored.

| Verb | Real flags | Does |
|---|---|---|
| `puck compile <path>` | `-o/--output`, `-w/--watch`, `--strict`, `--validate`, `--bundle`, `--update-assets`, `--tree`, `--written` | Parses, resolves/bundles imports, lowers via the vocabulary the parsed `schema:` names, optionally validates the engine schema, writes `<source>.cartridge.json`/`.world.json` by default, and beside each world document its compiled world `<name>.puckb` (none for a module fragment); a `.world.json` path compiles to its compiled world alone. A world source with `world` declarations emits one `<name>.world.json` per declaration and reads `--output` as their destination directory. `--update-assets` explicitly refreshes the root source's sibling asset lock after successful semantic validation; it cannot combine with `--watch`. `--validate` self-installs the machine catalog first — no separate registration step. `--watch` recompiles on `*.puck` changes (250 ms debounce). |
| `puck decompile <path>...` | `-o/--output`, `--overwrite` | Reads `schema` from the JSON, routes to the matching decompiler. **One-way**: `let`/`template`/`for` never reproduced; output opens with a one-time-import header comment. Several `<world>.world.json` paths are one composition's worlds, decompiled together into the one source that emits them (`--output` required). A generated name prints back as the construct that generated it or is refused by name (`WorldDecompileRefusedException`). |
| `puck lint <path-or-dir>` | `-s/--strict` | Recurses `*.puck` for a directory. Runs cartridge diagnosis first, then `PuckLinter.Lint` (syntax), lowers, then (root documents only) semantic validation and reference-resolution lint. Self-installs the machine catalog like `compile --validate`. |
| `puck format [<path-or-dir>=.]` | `--check`, `--file-list <json>`, `--only puck` | Recurses `*.puck` (and `*.cs`) outside `bin`/`obj`/`artifacts` and `experimental`; `--only puck` formats the `.puck` sources alone. Parses and prints the tree. Reader and printer share one escape grammar (`PuckStrings`), so the printer writes only what the reader reads back and what a document compiles to cannot move: `StringGrammarTests` pins the shared grammar over a generated value corpus, and `ProjectionLawTests`/`FormatProjectionLawTests` prove format-then-compile equals the original over every shipped source in both vocabularies. What is the author's and survives: every comment, wherever the grammar admits one; a blank-line run's length; the line breaks inside an array, object or argument list; a `,` at a line end; a numeric literal's base; a raw fence; and a name's bare-or-quoted spelling. The one comment that moves is one written inside a construct's header, between its first word and its `{`: a `//` there prints above the statement, because in place it would swallow the brace, and a `/* */` written after the header's name prints right after the first word. A source it cannot parse is refused by name and left alone — exit 2, in directory mode as well; `--check` writes nothing and exits 1 for a file that merely needs formatting. A cell key is not the author's: a key read inside an operand prints the way an assignment target's key prints (`PuckPrinter.OperandKeys` over `ExpressionSpelling.AppendSourceKey`), so `well[(py + i)]` prints `well[py + i]` on both sides of `=` while `well[(level)]`, which is not the literal key `level`, keeps its parentheses (`KeySpellingLawTests`). |
| `puck migrate <name> <path>` | `-c/--check` | Applies one registered rewrite — a `PuckMigration` over `Puck.Transpiler.Rewriting.PuckSyntaxRewriter` — to every `*.puck` under the path (or to one file) and prints each through `PuckPrinter`. Two verdicts per changed source: its two compiled documents must be equal excluding the members the migration declares in `ReshapedMembers` (`/`-separated paths, `*` matching one key or index, a path covering everything beneath it), and its comments in reading order must be unchanged unless the migration declares `ReshapesComments` (trivia never reaches the document, so the member declaration cannot speak for it). Blank-line runs and the compile-time layer surviving rest on the printer `format` is gated on, not on a verdict of the run's own. All-or-nothing: every source is parsed, rewritten and printed before anything is written. A source the migration leaves alone is neither compiled nor written. A difference outside the declared members, an undeclared comment change, a source that does not parse, one whose migrated text does not parse back, one it would change that does not compile, or one it would change that cannot be opened for writing refuses the whole run (exit 2, nothing written); `--check` exits 1 for work outstanding. Each write stages a sibling `.migrate-tmp` and moves it over the destination, so no file is ever left truncated; the one thing not atomic is the *set* — a move failing after earlier moves leaves those destinations migrated and the refusal names them. The registry carries only whatever reshape is mid-flight — a migration lands with the reshape it serves and is deleted once it has run — and an unknown name lists what is registered. |
| `puck lsp` | none | `PuckLanguageServer` over stdio: completion, hover, `documentSymbol`, formatting, semantic tokens, and diagnostics published once the input goes quiet (the browser engine hosts the same server; see the transpiler README's editor tooling). |
| `puck test <path-or-dir>` | `--host`, `--world-artifact`, `--keep <dir>`, `--jobs`, `--reproduce` | Compiles a `.puck` source, generates one test world per `test` block (`<stem>~<slug>`, or `<world>~<slug>` for a world a composition declares; a stem carrying `~` is PUCK113), boots each through the real `Puck.World` executable headless, and prints one line per verdict (name, pass/fail, the gate as written, the values it read, the firing tick); `--reproduce` runs each world twice and refuses one whose exports or manifests differ. A `*.world.json` document runs as it stands; a directory contributes the file carrying each document name (`PuckDocumentComposer.TryCarriers`: the source that emits it where one does) plus every module library, skips a source with no test block, and refuses two names that differ only in case. A module's own tests run here, once per distinct instantiation — this verb is the only door that runs them. Exit 0 all passed, 1 a failing verdict or an unexpected step outcome, 2 usage. The construct: [Testing a world](../../../docs/authoring/testing-a-world.md). |
| `puck vocabulary` | `-c/--check` | Writes [the world vocabulary](../../../docs/reference/world-vocabulary.md) from `Puck.World.Transpiler`'s construct table — every `puck.world.definition.v1` construct's keyword, members, the document member it lowers to, and what the printer requires before it prints a node back as that construct. `--check` exits non-zero and names the first differing line when the page disagrees with the table. The cartridge vocabulary has no such page. |

Without `--output`, `compile` writes `<source>.cartridge.json` for a cartridge
document and `<source>.world.json` otherwise, selected by the parsed `schema:`.
A world source containing `world name = module(arguments)` declarations instead
writes `<name>.world.json` for each declaration. Its `--output` is a directory,
not a filename; one declaration may be written `entry world name = …`, the world
`Puck.World --world <source>.puck` boots (a composition with no entry is refused
at boot by name). A world source that declares no world and lowers to an empty
document (a module library, `WorldCompilation.EmitsDocument` false) writes
nothing, prints nothing, and exits 0. `--tree <root> --output <directory> [--written <report>] <sources…>` mirrors every
source under `<root>` into `<directory>` and removes any other `*.world.json` or
`*.puckb` there. Every name under `<root>` resolves through the name index
before anything is written; a hand-authored `.world.json` ships as it stands
with its compiled world unless the `.puck` of its exact name emits that name, so
one beside a module library or beside a composition declaring other worlds
ships, and one beside an ordinary world source does not. It is the build's
generation step, and it refuses (exit 1), in `DocumentName.Collision`'s words
and before writing anything, any other two files carrying one name ignoring
case: a document whose name a source of another stem declares, or two sources
emitting one name. `--written` reports the files the
run left under `<directory>`, one forward-slashed relative path per line, only
for a run that succeeded; `build/WorldAssets.targets` ships exactly that report
(`TreeCompileReportLawTests`), since the run is what wrote them. `asset "path"` references use one `<stem>.assets.json` lock
beside the root source; ordinary compilation verifies its full SHA-256 pins,
while `--update-assets` is the only compile mode that replaces them. Every
relative file path a module writes, `asset "…"` or plain (a member
`WorldDocumentPaths.IsFileField` names), resolves beside the module and is
re-expressed for the document that uses it (`WorldDocumentVocabulary.RelocateFileReference`).
Relocation follows the value: a string is re-expressed from the directory of
the source whose literal produced it (a `let`'s own file, a module argument's
call site), whatever expression carries it to the file-path member
(`IDocumentVocabulary.NoteWrittenString`). `--output` elsewhere re-expresses
the written document's paths from where it lands. Each destination and the lock is replaced
atomically on its own, but publication of the whole set is not transactional.

## Grammar, in brief

One spelling per shape, at every depth, including inside object literals: a
container is always a block (`host { }`, `cameras [ ]`), a scalar always takes
a colon (`documentId: "puck"`); a colon in front of `{`/`[` is **PUCK040**, the
single most common mistake. `let name = expr` is a compile-time constant;
`template name(param, param2 = default) { }` is a parametric block; `import
"path" [as alias]` and `export read|action|binding name, name2` compose
modules. `use m as a(args)` makes an instance whose every declaration (rows, rules, placements, prototypes,
grounds) is the generated name `a$name`; the using scope writes it `a.name` in every name position, nested
instances read through each alias (`box.child.score`), and resolution is static from the `use` lines
(`DocumentScope.TryQualify`), so order does not matter. An alias spelled like a row, pool, `let` or module
parameter of the using scope is **PUCK117**, since `alias.name` would also spell that row's cell. Spawn points, kits, looks and cameras stay flat. Units (`s ms m mm cm hz % pct deg rad`) convert in the core; *which*
field is dimensioned is the vocabulary's own table (unit on an uncovered field
is **PUCK024**, an inadmissible unit is **PUCK025**). A value expression takes
the rule language's infix operators with the rule language's binding, both read
from `Puck.State.ExpressionOperators` (`1 << 3` folds to 8 in a `let`), its two
prefix operators (`-`, `~`), and its conditional, `c ? a : b`, the one spelling
of a choice in both languages. A
comparison yields `1` or `0`, never a JSON boolean. Strings come in four forms: plain `"..."`
(never interpolates), interpolated `$"...{expr}..."`, raw `"""..."""` (no
escapes), and raw+interpolated `$"""..."""`. An interpolated string computes
one name, key, number or text, never an expression: inside a bare expression
it is an atom (`pieceCell[$"piece{i}"]`), and an expression written as one is
**PUCK112**, which names the bare spelling. A name the bare form cannot carry is backquoted in an expression
(`` `seat-1` ``); backquotes have no escape, so a state row or cell key refuses a
backquote and an operand naming nothing is refused where it is built. A name Puck
generates rather than an author writes is spelled with a character no author name may carry
(`Puck.State.GeneratedName`, [Generated names](../../../docs/reference/dsl.md#generated-names)):
document names join their parts with `$` (`turn$east`, `expect$1`, `$pool$pieces$live`), file-backed
names with `~` (`rulepush~push-block`). An author name carrying `$` past its first character or `~` anywhere,
or a world or source file name carrying `~`, is **PUCK113**; the loader refuses a document name carrying `~` by
name, and a document name reaching a directory spells its `$` as `~` (`GeneratedName.ToFile`: `link$west` starts
the instance `…link~west`); mint a new generated name through `GeneratedName.Join`/
`Append` and record it with the emitter's `Generated`, never by string concatenation. Sugar lines and block members bind parsed operand trees; do not add identifier substitution or enum/family regex passes. Use `RewriteOperandSyntax` for source rewrites and preserve literal-key versus expression positions.

Behaviour is written beside what it is about, as `test "name" [with
module(arguments)] { given { } when { } expect { } }` at the root of a document
or of a `module` body: `given` writes boot cells, `when` lays `ticks n` and
`seat<n>: <command line>` steps on a tick grid (`seat<n> refused ["text"]: …` claims
the world refuses the line, and a step may be a read answered through the seat's
own visibility), and `expect` carries one rule-gate expression per line. Each block lowers to a generated test world and
the enclosing document carries no trace of it. Without `with`, the world is the
document the test stands in; with it, the named module expanded with those
arguments, alone. A `test` inside a module body is carried to every `use` and
every `world name = module(arguments)` and runs there under that
instantiation's arguments, once per distinct instantiation, named for the
instance; `puck test` is the only door that runs it, since `puck compile` does
not boot. Without `with` at the root of a source that emits worlds by name, the
block is the composed run: every line inside `given`/`when`/`expect` stands in a
`<world> { … }` block naming one of them (`ticks` stays outside, since one grid
carries the run), the block lowers to one document per world, and the first
declared world boots and arms the rest. Refusals are **PUCK104** (the
declaration's shape, including a `with` naming a template) and **PUCK105** (a
line a generated world cannot carry, a world-addressing fault included); an
unknown module or a bad argument refuses through the expander's own
**PUCK048**.

`for i in range(0, n) { ... }` and `for (item, index) in [...] { }` expand at
compile time — the document carries the rows they produce, never the loop
(distinct from a cartridge rule's play-time `repeat`). The collection builtins
— `range`, `length`, `concat`, `map`, `filter`, `reduce`, `distinct`, `sort`,
`groupBy` — are the document language's own vocabulary, evaluated while
lowering, and take a lambda (`item => expr`, `(item, index) => expr`) as an
argument to exactly one of them and nothing else (a lambda elsewhere is
**PUCK042**; an unevaluable call is **PUCK041**). Named scalar functions
(`squareRoot`, `sine`, `hexIndex`, ...) are not this project's own:
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
uses for the condition; `repeat`/`break` have nothing to lower onto there, so
PUCK037 fires for them. Cartridge rules support all three —
`if`/`repeat`/`break` (see `rom-forge`).

## Known traps, verified against the current tree

| Trap | What holds |
|---|---|
| Expecting lint to report a name missing from a *module* (no `schema`/`basis` of its own) | It never does: a fragment cannot know what an unknown root will supply. For a root, `PuckLinter.References.cs` composes the whole basis/import graph before building its name catalog. |
| A `let` referenced inside a `when`/effect operand | Parsed operand trees participate in the usage scan. Literal cell keys and backquoted names do not count as binding reads. |
| A `: Kind`/`as Kind` comparison-kind suffix beside `and`/`or` | Binds only to the ONE comparison it follows, never the enclosing chain, though it prints as if it scoped the whole thing. Wrap the annotated comparison in its own parens. |

## Diagnostics

Every code is declared exactly once, in
[`PuckDiagnosticCodes.cs`](../../../src/Puck.Transpiler/Diagnostics/PuckDiagnosticCodes.cs)
(the `PUCKnnn` and `PUCK_LINT_nnn` families; a deleted code's number is free
for a new one), with the meaning as its XML doc comment. A fault is reported once, at
the text that carries it: a refusal row in `tests/Puck.World.Transpiler.Tests`
holds its code to exactly one report on the needle's line, so a cascade or a
second home for the same check fails there. A statement the parser cannot read
is reported once, as the parse fault: it stands in the tree as an
`ErrorStatementNode` that no section reports again (`WorldDocumentEmitter.ExpandOne`
drops it), and the unused-declaration lint (`PUCK_LINT_001`) reports nothing
while one stands, since the unread text may hold the use
(`ParseFaultReportLawTests`). The two vocabulary READMEs narrate the same codes with
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

What decompiling does recover is every construct whose expansion wrote a
generated name: a `ground`'s prototype and placement, a rule scope's joined
member names, a generated test world's verdict machinery, and a composition's
borders and doors (decompiled from all its worlds together). It never prints a
generated name as an authored one; a document spelling one no construct prints
back is refused by name. `GeneratedNameReversalLawTests` holds every minting
site under `src/` to this and fails on a minting call no row reads back.

## Route adjacent work

| Skill | Route there for |
|---|---|
| `puck-world` | World-document sugar meaning — `rule`/`decision`/`when`/effect semantics, `placements`/`prototypes` section rules, world-specific refusals, booting `--world`, the mutation/console surface. |
| `sdf-authoring` | Sculpting a creation's shapes/materials/rig in `.puck` — primitive choice, blends, scope rules, the shape budget. |
| `rom-forge` | The cartridge vocabulary's own sections and opcodes, `puck compile`/`forge.open`/`forge.play` verification loop, ROM/save semantics. |
| `gaming-bricks` | Emulator hardware behavior once a cartridge is compiled — CPU/PPU/APU/timer accuracy, the Post batteries. |
| `symbol-analysis` | A `.puck` name/state-row/prototype-id resolution question is **not** covered by `references`/`declarations` (C#-only) — use `puck lint`'s `PUCK_LINT_00x` family or the LSP instead. |
| `content-search` | A `.puck`-only fact (a `let` name, a comment, an unexpanded `for` body) may not appear in the compiled JSON and vice versa — search whichever form the question actually needs. |

## Records, pools, and retained turns

Use the current [records and pools](../../../docs/reference/state/records-and-pools.md)
and [rule groups](../../../docs/reference/state/rule-groups.md) references when editing records,
pools, token knowledge, or retained turns. Pool lifetime tests must include
release/reclaim and export/reload; a static slot lookup follows the current
occupant, while a held handle checks its generation. Exercise module read/action
exports through lexical bindings and include lossless compile/decompile laws.
After changing this surface, regenerate schema, vocabulary, and name-registry
outputs with their CLI owners and run the affected State, Rules, World, and
World.Transpiler suites. A generated projection probe must cover every new arm.
