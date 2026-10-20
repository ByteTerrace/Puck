---
name: puck-dsl
description: "Maps the .puck language: grammar, let/template/for, map/range builtins, puck compile/decompile/lint/format, PUCKnnn errors. Use for any .puck edit or JSON-to-.puck conversion. Shapes: sdf-authoring."
---

# The `.puck` authoring language

`.puck` is the one authoring language behind two document vocabularies:
`puck.world.definition.v1` (`Puck.World.Transpiler`) and `puck.cartridge.v1`
(`Puck.GamingBricks.Transpiler`). Both ride one schema-agnostic core,
[`Puck.Transpiler`](../../../src/Puck.Transpiler/README.md): parser, printer,
lowering, diagnostics, module resolver and unit arithmetic. This skill maps that
core and its CLI loop; what a block means is the vocabulary skill's (see the
routing table). When this file and the code disagree, the code wins and this
file is corrected in the same change.

## Where things live

| Thing | Where |
|---|---|
| Parser, printer, lowering, diagnostics | `src/Puck.Transpiler` (`Parsing/PuckParser*.cs`, `Formatting/PuckPrinter*.cs`, `Lowering/DocumentLowering*.cs` and `DocumentBuiltins.cs`, `Diagnostics/PuckDiagnosticCodes.cs`) |
| World vocabulary, its linter and language server | `src/Puck.World.Transpiler` (`Vocabulary/` describes every construct once; `Validation/PuckLinter*.cs`; `Lsp/`) |
| Cartridge vocabulary | `src/Puck.GamingBricks.Transpiler` |
| Expression vocabulary (operators, named functions) | `Puck.State.ExpressionOperators`, `Puck.State.ExpressionVocabulary` |
| CLI verbs | `src/Puck.Cli.Worlds` (`Transpiler/`: compile, decompile, lint, lsp, migrate, embed; `Test/`; `Vocabulary/`), `src/Puck.Cli.Format` (format) |
| Shipped sources | `src/Puck.World/Assets/worlds/**/*.puck`, `src/Puck.World/Assets/cartridges/*/*.puck`, `src/Puck.World.Transpiler/Samples/`; the quarantined worlds under `experimental/` are prior art |

A document's `schema:` field selects its vocabulary, never the filename.

## Grammar that bites

- **One spelling per shape, at every depth.** A literal container is always a
  block with no colon (`host { }`, `cameras [ ]`, `variables [ ]`,
  `arrays [ ]`), inside an object literal too
  (`{ name: "lit", pixels ["00000000", "11111111"] }`). A scalar always takes a colon
  (`name: "cursor"`, `initial: 0`). A colon in front of `{` or `[` is
  **PUCK040**, the most common mistake; the fix is to drop the colon. A value
  written as a call or a name keeps its colon even when it produces a
  container: `pixels: map(range(0, 8), row => "00000000")`, `origin: bounds`.
- `let name = expr` is a compile-time constant; `template name(p, q = d) { }` a
  parametric block; `import "path" [as alias]`, `export` and
  `use m as a(args)` compose modules.
- **Compile-time `for` generates rows; builtins generate values.**
  `for i in range(0, n) { }` and `for (item, index) in [...] { }` expand at
  compile time, and the document carries the rows they produce. Inside one
  value, use the collection builtins: `range`, `length`, `concat`, `map`,
  `filter`, `reduce`, `distinct`, `sort`, `groupBy`, each taking a lambda
  (`i => 0`). An array of n zeros is `map(range(0, n), i => 0)`; it runs once
  at compile time, the compiled JSON still holds every value, and play-time
  behavior is unchanged. A lambda anywhere else is **PUCK042**; an unevaluable
  call **PUCK041**; a `for` over something not known at compile time
  **PUCK044**.
- **A `for` inside a rule body unrolls into the body.** A loop
  over an n-element array that declares `local $"name{i}" = …` compiles to a
  rule carrying n separate locals, `name0` through `name<n-1>`, each with its
  element substituted as a literal; the document holds no loop. Later
  statements read each local by its resolved name, and each is priced like a
  hand-written local. A `for` reads any compile-time array: a `let`, a
  literal, or a builtin's result.
  A rule has one gate and one decision, so a `when`, `decision` or nested
  `rule` inside a rule-body `for` is **PUCK114**; write the condition in the
  rule's own `when`, or in an `if` inside the loop.
- Units (`s ms min h m cm mm hz % pct deg rad`) convert in the core; a unit on
  a field the vocabulary does not dimension is **PUCK024**, an inadmissible
  unit **PUCK025**.
- Strings: plain `"..."`, interpolated `$"...{expr}..."`, raw `"""..."""`, raw
  interpolated `$"""..."""`. An interpolated string computes a name, key,
  number or text, never an expression (**PUCK112**). Backquote a name the bare
  form cannot carry (`` `seat-1` ``).
- Generated names use `$` inside a document and `~` in file names
  (`Puck.State.GeneratedName`); an author name using them is **PUCK113**.
- **Control flow is core-parsed; each vocabulary decides what its rule shape
  carries.** The parser accepts `if`/`else`, `repeat`/`break`, call-form gates
  and compound assignment for every vocabulary. A world rule lowers `if`/`else`
  to a conditional effect, but its body is straight-line, so `repeat` and
  `break` have nothing to lower onto (**PUCK037**). The refusal names the
  alternative: a rule already runs once per matching subject, so iterate with
  the rule's `forEach`; a fixed count unrolls with a compile-time `for`, or
  folds into one effect (`x += 3`). A world rule carries `=` (`setState`) and
  `+=` (`addState`); any other compound operator is **PUCK039**, written out as
  `x = x - …`. A call-form gate where only comparisons are legal is
  **PUCK038**. Cartridge rules admit `if`, `repeat`, `break`, `key(...)` gates
  and compound assignment.
- A `: Kind`/`as Kind` comparison-kind suffix binds only to the one comparison
  it follows; wrap it in parentheses beside `and`/`or`.

[references/grammar.md](references/grammar.md) has the full grammar with
examples; [references/diagnostics.md](references/diagnostics.md) the codes
authors hit most. `PuckDiagnosticCodes.cs` declares every code once, with its
meaning as the XML doc comment.

## The verbs

```text
puck format <file> --check
puck lint <file> --strict
puck compile <file> --validate        # writes <source>.world.json (and its .puckb) or <source>.cartridge.json
puck test <path-or-dir>               # boots each test block headless, one verdict a line
```

- `puck compile --validate` runs the engine's own validation over the lowered
  document; lowering alone proves only that the source was well formed.
  `--output` names the destination; a world source declaring `world` entries
  writes one document per declaration and reads `--output` as a directory.
  A world document also gets its compiled world (`.puckb`) beside it.
  `--watch` recompiles on change; `--tree` mirrors a directory of sources.
- `puck decompile` is **one-way**: `let`, `template` and `for` never come back.
  Decompile once to import a JSON document, then edit the `.puck`.
- `puck format` prints `.puck` through `PuckPrinter` (two-space indent, no
  options); the language server formats through the same printer.
- `puck lsp` serves completion, hover, symbols, formatting and diagnostics.
- `puck migrate <name> <path> [--check]` applies one registered source rewrite
  and proves each rewritten source compiles to the document it did before.
- `puck embed <path>` resolves authored `embed(...)` text into committed
  `.embeddings.json` lock files.
- `puck vocabulary [--check]` regenerates the world vocabulary page.

**Source and generated documents.** A committed cartridge source has its
`.cartridge.json` twin committed beside it and regenerated in the same change
(`CartridgeRoundTripTests` byte-compares them). A world source has no committed
document: the build compiles it. `--world foo.puck` boots straight from source,
`world.reload` recompiles it, and `world.save` writes edits back through the
printer (generated rows refuse by name).

After editing a world, run it (`dotnet run --project src/Puck.World -c Release
-- --exit-after-seconds 2`); verify a cartridge in a world (`rom-forge`).

## Route adjacent work

| Skill | Route there for |
|---|---|
| `puck-world` | World-document sugar (`rule`/`decision`/effects, `forEach`), section rules, world refusals, booting `--world`. |
| `sdf-authoring` | Creation shapes, materials and rigs in `.puck`. |
| `rom-forge` | The cartridge vocabulary and the compile/`forge.play` loop. |
| `gaming-bricks` | Emulator behavior once a cartridge is compiled. |
| `symbol-analysis`, `content-search` | C# semantics and text search; `.puck` name resolution goes through `puck lint` or the language server. |
