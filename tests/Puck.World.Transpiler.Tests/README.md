# Puck.World.Transpiler.Tests

This xUnit v3 suite targets `net10.0` and checks the `.puck` world vocabulary pipeline. Parser, lowering, emitter, formatter, decompiler, linter, diagnostics, LSP, lambda, machine, shape, sugar, source-position, and round-trip tests cover canonical source fidelity and compiled world output; shipped-world tests compare committed sources with their generated JSON. A build-only CLI dependency generates those documents before the tests run; see [generated assets](../../build/README.md).

`ModuleExpansionTests` covers typed source modules, aliased and nested `use` expansion, export requirements,
recursive namespace rewriting, collision refusals, bounded recursion, source origins, and formatter preservation.
Imports carry module declarations and their lexical constants; a module contributes document rows only when a `use`
instantiates it.

`MultiWorldCompilationTests` and the composition suites cover independent named
world outputs, reciprocal borders and doors, geometric module arguments, shared
expansion budgets, source origins, and malformed-input refusals. CLI publication
and lock refresh behavior are checked in `Puck.Cli.Tests`; host crossing behavior
is checked in `Puck.World.Tests`.

`AssetLockTests` covers `asset "path"` parsing and compilation, deterministic
full-SHA-256 locks, explicit refresh without compile-time writes, stale and
malformed lock refusals, bounded paths and bytes, imported-module-relative
resolution, pin pruning, and the rule that a failed compile cannot replace the
lock.

`GraphParameterStatementTests` holds the `parameter pass.member = value`
statement to the graph row's `parameters` member it lowers to, the formatter
and the decompiler to printing it back, and PUCK120 to refusing every other
spelling at its line.

`FileReferenceRelocationLawTests` holds a module's plain relative file path to
the file beside the module, whichever directory the using source stands in,
and holds the relocated document to decompile and recompile to itself.

`MemberSpellingRegressionTests` covers computed pool-field references and decompile/recompile preservation of
literal names containing spaces, punctuation, or a word also used by the language.
It also checks that an interaction supplied through a binding reads pool fields even when that binding was
previously evaluated as plain data.

`OperandAtomLawTests` covers an interpolated string as an atom of a bare expression: the refusal of an expression
built as text and the bare spelling it names, the one token an atom computes, a key word that reads no binding,
and an import alias reaching a binding read inside an atom.
`OperandTreeLawTests` pins sugar/call/member equivalence, live-key grouping, family gaps, structural rewrites,
usage linting and refusal of malformed operands.

`ProjectionLawTests` is the projection law: compile, print, compile again and the two documents are equal; format,
compile and the document is unchanged. It runs over every shipped `.puck` source and over `ConstructCorpus`, one
generated source per construct `ConstructRegistry` reads out of the registries themselves — the polymorphic `$type`
tables in `Puck.State`, `ExpressionComparisons`' comparison operators and `PuckDslVocabulary`'s kinds — so a construct the generator does
not cover, or one the printer cannot reproduce, fails under its own name. `OneDoorTests` keeps every harness on
`WorldCompiler`, the vocabulary's one compile entry point.

`SyntaxRewriterLawTests` is the rewrite law over `Puck.Transpiler.Rewriting`: over every shipped source and every
registered construct, printing the identity rewrite's tree equals printing the unrewritten one, so a descent arm
that drops a node's trivia or a child fails there; and the descent is walked over an instance of every concrete
syntax node type, so a node kind with no arm fails under its own name. (The comparison is print against print, not
print against the source: the construct corpus is authored with four-space indentation, and the point is to isolate
the rewrite from the printer.) `DescentChildLawTests` asks the stronger question the reach law cannot — that each
arm rebuilds every syntax-node child its node declares — and `SyntaxNodeKinds` is the one type enumeration both run
over. `MigrationDeclarationLawTests` holds a migration to what it declares it reshapes.

## Writing a test

Compile world source through `WorldSources`: `LowerClean` for a body under the world schema header that must compile
clean, `Lower` when the diagnostics matter too, `Parse` for the tree alone, and `AssertRoundTrips` for the
decompile-and-recompile law. A refusal is a `Refusal` row — body, code, and a needle whose line the diagnostic must
name — that holds the code to exactly one report at that line (`Alone` further holds it to be the source's only error),
added to its file's table and held by `WorldSources.AssertRefused` (compile) or `AssertRefusedBy` (parse), so a
failure names the case. Every committed source compiles once per run through `ShippedWorlds.Compile`. Drive the
language server through `LanguageServerClient`, which frames with the product's own `LspFraming` and returns
everything the server wrote; `MarkedSource` reads a `|` in a source as the cursor.

## Verification

Run the focused suite from the repository root:

```powershell
dotnet test tests/Puck.World.Transpiler.Tests/Puck.World.Transpiler.Tests.csproj -c Release
```

The project references `Puck.Transpiler`, `Puck.World.Transpiler`, and `Puck.World.Schema`; grammar and CLI behavior remain owned by the transpiler projects.

## Documentation

`UnifiedSortLawTests` checks that own-value and attribute sorting use the same
authored key list and the same document discriminator.

`WorldCostSourceTests` checks that cost contributors from two module instances
retain distinct names and instance paths while pointing to their shared defining
source line.

📚 [Worlds and federation](../../docs/architecture/worlds.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
