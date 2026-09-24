using System.Text.Json.Nodes;
using Puck.Abstractions.Documents;
using Puck.World.Transpiler.Decompiler;
using Puck.Transpiler.Diagnostics;
using Puck.World.Transpiler.Lowering;
using Puck.Transpiler.Parsing;
using Xunit;

namespace Puck.World.Transpiler.Tests;

public class ShippedWorldsParityTests {
    private static void AssertDecompilationRoundTrips(
        string originalJsonText,
        string? basePath,
        string label,
        bool sql = false
    ) {
        var originalNode = JsonNode.Parse(originalJsonText);

        Assert.NotNull(@object: originalNode);

        // 1. Decompile JSON -> .puck
        var decompiledPuck = WorldDecompiler.Decompile(jsonText: originalJsonText, sql: sql);

        Assert.False(
            condition: string.IsNullOrWhiteSpace(value: decompiledPuck),
            userMessage: $"Decompiled source was empty for {label}"
        );

        // 2. Parse .puck -> AST
        var diagnostics = new DiagnosticBag();
        var parseResult = PuckParser.ParseDocumentWithDiagnostics(
            source: decompiledPuck,
            diagnostics: diagnostics,
            vocabulary: WorldDocumentVocabulary.Instance
        );

        Assert.False(
            condition: diagnostics.HasErrors,
            userMessage: $"Parse errors for {label}:{Environment.NewLine}{diagnostics.FormatReport(decompiledPuck)}"
        );
        Assert.NotNull(@object: parseResult.Value);

        // 3. Lower AST -> JSON
        var loweringDiagnostics = new DiagnosticBag();
        var loweringResult = WorldCompiler.Compile(
            basePath: basePath,
            cancellationToken: TestContext.Current.CancellationToken,
            diagnostics: loweringDiagnostics,
            source: decompiledPuck
        );

        Assert.False(
            condition: loweringDiagnostics.HasErrors,
            userMessage: $"Lowering errors for {label}:{Environment.NewLine}{loweringDiagnostics.FormatReport(decompiledPuck)}"
        );
        if (sql) {
            Assert.Empty(collection: diagnostics);
            Assert.Empty(collection: loweringDiagnostics);
        }
        Assert.NotNull(@object: loweringResult.Json);

        // 4. Parity check: Canonical serialization structural equivalence
        var originalCanonicalBytes = CanonicalJsonDocument.Serialize(node: originalNode);
        var roundtripCanonicalBytes = CanonicalJsonDocument.Serialize(node: loweringResult.Json);

        var originalCanonicalJson = System.Text.Encoding.UTF8.GetString(bytes: originalCanonicalBytes);
        var roundtripCanonicalJson = System.Text.Encoding.UTF8.GetString(bytes: roundtripCanonicalBytes);

        var originalRoundtripNode = JsonNode.Parse(originalCanonicalJson);
        var recompiledNode = JsonNode.Parse(roundtripCanonicalJson);

        var mismatch = JsonMismatch.Find(
            actual: recompiledNode,
            expected: originalRoundtripNode,
            path: $"{label}:"
        );

        Assert.True(condition: (mismatch is null), userMessage: mismatch);
    }

    public static TheoryData<string> GetShippedWorldFiles() => ShippedWorlds.Files();
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
              "basis": "avatars/moth",
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
    [MemberData(nameof(GetShippedWorldFiles))]
    [Theory]
    public void TestShippedWorldRoundTripParityWithSql(string relativePath) {
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
            label: $"{relativePath} (sql)",
            sql: true
        );
    }
}
