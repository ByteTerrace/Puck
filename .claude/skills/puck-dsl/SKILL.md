---
name: puck-dsl
description: "Covers the `.puck` authoring language core every vocabulary shares: the one-spelling grammar, `let`/`template`/`import`/`export`, units, compile-time `for` with the collection builtins and lambdas, strings and indexing, the `Puck.State`-delegated expression vocabulary, the `.puck` CLI verbs (`puck compile`/`decompile`/`lint`/`lsp`/`migrate`/`test`, and `puck format` over `.puck`), and the PUCKnnn diagnostic family. Use whenever writing or editing any `.puck` file, running those verbs, diagnosing a PUCKnnn error, or converting a JSON world/cartridge document into idiomatic DSL. Vocabulary meaning belongs elsewhere: world-document sugar (`rule`/`decision`/`placements`), world refusals and booting `--world` to `puck-world`; SDF shape/prototype/creation authoring to `sdf-authoring`; the cartridge vocabulary and forge compile/play loop to `rom-forge`; emulator hardware behavior to `gaming-bricks`."
---

# The `.puck` authoring language

`.puck` is the one authoring language behind two document vocabularies:
`puck.world.definition.v1` (`Puck.World.Transpiler`) and `puck.cartridge.v1`
(`Puck.GamingBricks.Transpiler`). Both ride one schema-agnostic core,
[`Puck.Transpiler`](../../../src/Puck.Transpiler/README.md): parser, printer,
diagnostics, module resolver and unit arithmetic. This skill maps that core and
its CLI loop; what a block means is the vocabulary skill's (see the routing
table). When this file and the code disagree, the code wins and this file is
corrected in the same change.

## Where things live

| Thing | Where |
|---|---|
| Core parser, printer, diagnostics | `src/Puck.Transpiler` (`PuckPrinter`, `PuckLinter`, `Diagnostics/PuckDiagnosticCodes.cs`) |
| World vocabulary | `src/Puck.World.Transpiler` (`Vocabulary/` describes every construct once) |
| Cartridge vocabulary | `src/Puck.GamingBricks.Transpiler` |
| Expression vocabulary (operators, named functions) | `Puck.State.ExpressionOperators`, `Puck.State.ExpressionVocabulary` |
| CLI verbs | `src/Puck.Cli.Worlds` (compile, decompile, lint, test), `src/Puck.Cli.Format` (format) |
| Shipped sources | `src/Puck.World/Assets/worlds/**/*.puck`, `src/Puck.World/Assets/cartridges/*.puck`, `worlds/`; `git ls-files '*.puck'` is the list |

A document's `schema:` field selects its vocabulary, never the filename.

## Grammar that bites

- **One spelling per shape, at every depth.** A container is always a block
  with no colon (`host { }`, `cameras [ ]`, `variables [ ]`, `arrays [ ]`); a
  scalar always takes a colon (`name: "cursor"`, `initial: 0`). A colon in
  front of `{` or `[` is **PUCK040**, the most common mistake; the fix is to
  drop the colon, at any depth, object literals included.
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
  call **PUCK041**.
- A `for` inside a rule body unrolls into the body: `local $"name{i}" = …`
  produces one local per iteration, each priced like a hand-written local and
  read later by its name. A rule has one gate and one decision, so a `when`, `decision` or nested
  `rule` inside a rule-body `for` is **PUCK114**; use the rule's own `when` or
  an `if` inside the loop.
- Units (`s ms m mm cm hz % pct deg rad`) convert in the core; a unit on a field
  the vocabulary does not dimension is **PUCK024**, an inadmissible unit
  **PUCK025**.
- Strings: plain `"..."`, interpolated `$"...{expr}..."`, raw `"""..."""`, raw
  interpolated `$"""..."""`. An interpolated string computes a name, key, number
  or text, never an expression (**PUCK112**). Backquote a name the bare form
  cannot carry (`` `seat-1` ``).
- Generated names use `$` inside a document and `~` in file names
  (`Puck.State.GeneratedName`); an author name using them is **PUCK113**.
- **Control flow is core-parsed, but each vocabulary decides what its rule
  shape can carry.** A world rule lowers `if`/`else` to a conditional effect;
  `repeat`/`break` have nothing to lower onto there (**PUCK037**). A call-form
  gate where only comparisons are legal is **PUCK038**; an assignment operator
  the effects cannot carry is **PUCK039**. Cartridge rules admit `if`,
  `repeat`, `break`, `key(...)` gates and compound assignment.
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
puck compile <file> --validate        # writes <source>.world.json or .cartridge.json
puck test <path-or-dir>               # boots each test block headless, one verdict a line
```

- `puck compile --validate` runs the engine's own validation over the lowered
  document; lowering alone proves only that the source was well formed.
  `--output` names the destination; a world source declaring `world` entries
  writes one document per declaration and reads `--output` as a directory.
- `puck decompile` is **one-way**: `let`, `template` and `for` never come back.
  Decompile once to import a JSON document, then edit the `.puck`.
- `puck format` prints `.puck` through `PuckPrinter` (two-space indent, no
  options); the language server formats through the same printer.
- `puck lsp` serves completion, hover, symbols, formatting and diagnostics.
- `puck migrate <name> <path>` applies one registered source rewrite.
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
| `puck-world` | World-document sugar (`rule`/`decision`/effects), section rules, world refusals, booting `--world`. |
| `sdf-authoring` | Creation shapes, materials and rigs in `.puck`. |
| `rom-forge` | The cartridge vocabulary and the compile/`forge.play` loop. |
| `gaming-bricks` | Emulator behavior once a cartridge is compiled. |
| `symbol-analysis`, `content-search` | C# semantics and text search; `.puck` name resolution goes through `puck lint` or the language server. |
