# Puck.World.Transpiler.Tests

This xUnit v3 suite targets `net10.0` and checks the `.puck` world vocabulary pipeline. Parser, lowering, emitter, formatter, decompiler, linter, diagnostics, LSP, lambda, machine, shape, sugar, source-position, and round-trip tests cover canonical source fidelity and compiled world output; shipped-world tests compare committed sources with their generated JSON. A build-only CLI dependency generates those documents before the tests run; see [generated assets](../../build/README.md).

`ProjectionLawTests` is the projection law: compile, print, compile again and the two documents are equal; format,
compile and the document is unchanged. It runs over every shipped `.puck` source and over `ConstructCorpus`, one
generated source per construct `ConstructRegistry` reads out of the registries themselves — the polymorphic `$type`
tables in `Puck.State` and `PuckDslVocabulary`'s comparison operators and kinds — so a construct the generator does
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

## Verification

Run the focused suite from the repository root:

```powershell
dotnet test tests/Puck.World.Transpiler.Tests/Puck.World.Transpiler.Tests.csproj -c Release
```

The project references `Puck.Transpiler`, `Puck.World.Transpiler`, and `Puck.World.Schema`; grammar and CLI behavior remain owned by the transpiler projects.

## Documentation

📚 [Worlds and federation](../../docs/architecture/worlds.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
