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
    private static void AssertDecompilationRoundTrips(string originalJsonText, string? basePath, string label) {
        var originalNode = JsonNode.Parse(originalJsonText);

        Assert.NotNull(@object: originalNode);

        // 1. Decompile JSON -> .puck
        var decompiledPuck = WorldDecompiler.Decompile(jsonText: originalJsonText);

        Assert.False(
            condition: string.IsNullOrWhiteSpace(value: decompiledPuck),
            userMessage: $"Decompiled source was empty for {label}"
        );

        // 2. Parse .puck -> AST
        var diagnostics = new DiagnosticBag();
        var parseResult = PuckParser.ParseDocumentWithDiagnostics(
            decompiledPuck,
            diagnostics: diagnostics
        );

        Assert.False(
            condition: diagnostics.HasErrors,
            userMessage: $"Parse errors for {label}:{Environment.NewLine}{diagnostics.FormatReport(decompiledPuck)}"
        );
        Assert.NotNull(@object: parseResult.Value);

        // 3. Lower AST -> JSON
        var loweringDiagnostics = new DiagnosticBag();
        var loweringResult = WorldDocumentEmitter.LowerWithDiagnostics(
            parseResult.Value,
            basePath: basePath,
            diagnostics: loweringDiagnostics,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.False(
            condition: loweringDiagnostics.HasErrors,
            userMessage: $"Lowering errors for {label}:{Environment.NewLine}{loweringDiagnostics.FormatReport(decompiledPuck)}"
        );
        Assert.NotNull(@object: loweringResult.Value);

        // 4. Parity check: Canonical serialization structural equivalence
        var originalCanonicalBytes = CanonicalJsonDocument.Serialize(node: originalNode);
        var roundtripCanonicalBytes = CanonicalJsonDocument.Serialize(node: loweringResult.Value);

        var originalCanonicalJson = System.Text.Encoding.UTF8.GetString(bytes: originalCanonicalBytes);
        var roundtripCanonicalJson = System.Text.Encoding.UTF8.GetString(bytes: roundtripCanonicalBytes);

        var originalRoundtripNode = JsonNode.Parse(originalCanonicalJson);
        var recompiledNode = JsonNode.Parse(roundtripCanonicalJson);

        var mismatch = JsonMismatch.Find(
            actual: recompiledNode,
            expected: originalRoundtripNode,
            path: $"{label}:"
        );

        Assert.Null(@object: mismatch);
    }

    public static TheoryData<string> GetShippedWorldFiles() => ShippedWorlds.Files();
    public static TheoryData<string> GetShippedWorldSources() => ShippedWorlds.Sources();
    // Carries the same directive as its first `placements` row, plus placement rows a basis completes.
    [Fact]
    public void TestQuiltShardParity() {
        TestShippedWorldRoundTripParity(relativePath: "shards/quilt-ne.world.json");
    }
    // A `{"$replace": true}` basis-merge directive row in `rules` is not a rule.
    [Fact]
    public void TestReplaceDirectiveRuleRowParity() {
        const string Document = """
            {
              "basis": "avatars/moth.world.json",
              "rules": [
                { "$replace": true }
              ],
              "schema": "puck.world.definition.v1"
            }
            """;

        AssertDecompilationRoundTrips(
            originalJsonText: Document,
            basePath: ShippedWorlds.FindDirectory(),
            label: "replace-directive"
        );
    }
    [MemberData(nameof(GetShippedWorldFiles))]
    [Theory]
    public void TestShippedWorldRoundTripParity(string relativePath) {
        var worldsDir = ShippedWorlds.FindDirectory();
        var fullPath = Path.Combine(
            path1: worldsDir,
            path2: relativePath
        );

        Assert.True(
            condition: File.Exists(path: fullPath),
            userMessage: $"Shipped world file not found: {fullPath}"
        );

        AssertDecompilationRoundTrips(
            originalJsonText: File.ReadAllText(path: fullPath),
            basePath: Path.GetDirectoryName(path: fullPath),
            label: relativePath
        );
    }
    [MemberData(nameof(GetShippedWorldSources))]
    [Theory]
    public void TestSourceCompilesToTheGeneratedDocument(string relativePath) {
        var worldsDir = ShippedWorlds.FindDirectory();
        var sourcePath = Path.Combine(
            path1: worldsDir,
            path2: relativePath
        );
        var documentPath = Path.Combine(
            path1: worldsDir,
            path2: ShippedWorlds.DocumentOf(sourcePath: relativePath)
        );

        Assert.True(
            condition: File.Exists(path: documentPath),
            userMessage: $"{relativePath} has no generated document at {documentPath}"
        );

        var source = File.ReadAllText(path: sourcePath);
        var diagnostics = new DiagnosticBag();
        var parseResult = PuckParser.ParseDocumentWithDiagnostics(
            source,
            diagnostics: diagnostics
        );

        Assert.NotNull(@object: parseResult.Value);

        ModuleResolver.ValidateImportGraph(
            diagnostics: diagnostics,
            rootDoc: parseResult.Value,
            rootPath: sourcePath
        );

        var loweringResult = WorldDocumentEmitter.LowerWithDiagnostics(
            parseResult.Value,
            basePath: Path.GetDirectoryName(path: sourcePath),
            diagnostics: diagnostics,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.False(
            condition: diagnostics.HasErrors,
            userMessage: $"Compile errors for {relativePath}:{Environment.NewLine}{diagnostics.FormatReport(source)}"
        );
        Assert.NotNull(@object: loweringResult.Value);

        // The regeneration gate: the source is canonical and the document is its generated output, so the two can
        // never drift apart without failing here. Byte identity is what `puck compile` writes.
        Assert.Equal(
            File.ReadAllBytes(path: documentPath),
            CanonicalJsonDocument.Serialize(node: loweringResult.Value)
        );
    }
}
