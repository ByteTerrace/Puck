using System.Text.Json.Nodes;
using Puck.Abstractions.Documents;
using Puck.World.Transpiler.Decompiler;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Modules;
using Puck.World.Transpiler.Lowering;
using Puck.Transpiler.Parsing;
using Xunit;

namespace Puck.World.Transpiler.Tests;

public class ShippedWorldsParityTests {
    public static TheoryData<string> GetShippedWorldFiles() => ShippedWorlds.Files();

    public static TheoryData<string> GetShippedWorldSources() => ShippedWorlds.Sources();

    // A `{"$replace": true}` basis-merge directive row in `rules` is not a rule.
    [Fact]
    public void TestReplaceDirectiveRuleRowParity() {
        const string document = """
            {
              "basis": "avatars/moth.world.json",
              "rules": [
                { "$replace": true }
              ],
              "schema": "puck.world.def.v1"
            }
            """;

        AssertDecompilationRoundTrips(originalJsonText: document, basePath: ShippedWorlds.FindDirectory(), label: "replace-directive");
    }

    // Carries the same directive as its first `placements` row, plus placement rows a basis completes.
    [Fact]
    public void TestQuiltShardParity() {
        TestShippedWorldRoundTripParity("shards/quilt-ne.world.json");
    }

    [Theory]
    [MemberData(nameof(GetShippedWorldFiles))]
    public void TestShippedWorldRoundTripParity(string relativePath) {
        var worldsDir = ShippedWorlds.FindDirectory();
        var fullPath = Path.Combine(worldsDir, relativePath);
        Assert.True(File.Exists(fullPath), $"Shipped world file not found: {fullPath}");

        AssertDecompilationRoundTrips(originalJsonText: File.ReadAllText(fullPath), basePath: Path.GetDirectoryName(fullPath), label: relativePath);
    }

    [Theory]
    [MemberData(nameof(GetShippedWorldSources))]
    public void TestSourceCompilesToTheGeneratedDocument(string relativePath) {
        var worldsDir = ShippedWorlds.FindDirectory();
        var sourcePath = Path.Combine(worldsDir, relativePath);
        var documentPath = Path.Combine(worldsDir, ShippedWorlds.DocumentOf(relativePath));
        Assert.True(File.Exists(documentPath), $"{relativePath} has no generated document at {documentPath}");

        var source = File.ReadAllText(sourcePath);
        var diagnostics = new DiagnosticBag();
        var parseResult = PuckParser.ParseDocumentWithDiagnostics(source, diagnostics: diagnostics);
        Assert.NotNull(parseResult.Value);

        ModuleResolver.ValidateImportGraph(diagnostics: diagnostics, rootDoc: parseResult.Value, rootPath: sourcePath);

        var loweringResult = WorldDocumentEmitter.LowerWithDiagnostics(
            parseResult.Value,
            basePath: Path.GetDirectoryName(sourcePath),
            diagnostics: diagnostics
        , cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(diagnostics.HasErrors, $"Compile errors for {relativePath}:{Environment.NewLine}{diagnostics.FormatReport(source)}");
        Assert.NotNull(loweringResult.Value);

        // The regeneration gate: the source is canonical and the document is its generated output, so the two can
        // never drift apart without failing here. Byte identity is what `puck compile` writes.
        Assert.Equal(File.ReadAllBytes(documentPath), CanonicalJsonDocument.Serialize(loweringResult.Value));
    }

    private static void AssertDecompilationRoundTrips(string originalJsonText, string? basePath, string label) {
        var originalNode = JsonNode.Parse(originalJsonText);
        Assert.NotNull(originalNode);

        // 1. Decompile JSON -> .puck
        var decompiledPuck = WorldDecompiler.Decompile(originalJsonText);
        Assert.False(string.IsNullOrWhiteSpace(decompiledPuck), $"Decompiled source was empty for {label}");

        // 2. Parse .puck -> AST
        var diagnostics = new DiagnosticBag();
        var parseResult = PuckParser.ParseDocumentWithDiagnostics(decompiledPuck, diagnostics: diagnostics);

        Assert.False(
            diagnostics.HasErrors,
            $"Parse errors for {label}:{Environment.NewLine}{diagnostics.FormatReport(decompiledPuck)}"
        );
        Assert.NotNull(parseResult.Value);

        // 3. Lower AST -> JSON
        var loweringDiagnostics = new DiagnosticBag();
        var loweringResult = WorldDocumentEmitter.LowerWithDiagnostics(
            parseResult.Value,
            basePath: basePath,
            diagnostics: loweringDiagnostics
        , cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(
            loweringDiagnostics.HasErrors,
            $"Lowering errors for {label}:{Environment.NewLine}{loweringDiagnostics.FormatReport(decompiledPuck)}"
        );
        Assert.NotNull(loweringResult.Value);

        // 4. Parity check: Canonical serialization structural equivalence
        var originalCanonicalBytes = CanonicalJsonDocument.Serialize(originalNode);
        var roundtripCanonicalBytes = CanonicalJsonDocument.Serialize(loweringResult.Value);

        var originalCanonicalJson = System.Text.Encoding.UTF8.GetString(originalCanonicalBytes);
        var roundtripCanonicalJson = System.Text.Encoding.UTF8.GetString(roundtripCanonicalBytes);

        var originalRoundtripNode = JsonNode.Parse(originalCanonicalJson);
        var recompiledNode = JsonNode.Parse(roundtripCanonicalJson);

        var mismatch = JsonMismatch.Find(originalRoundtripNode, recompiledNode, $"{label}:");
        Assert.Null(mismatch);
    }
}
