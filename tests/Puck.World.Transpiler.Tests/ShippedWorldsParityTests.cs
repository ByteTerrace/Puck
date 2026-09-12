using System.Text.Json.Nodes;
using Puck.Abstractions.Documents;
using Puck.World.Transpiler.Decompiler;
using Puck.Transpiler.Diagnostics;
using Puck.World.Transpiler.Lowering;
using Puck.Transpiler.Parsing;
using Xunit;

namespace Puck.World.Transpiler.Tests;

public class ShippedWorldsParityTests {
    public static TheoryData<string> GetShippedWorldFiles() => ShippedWorlds.Files();

    // Carries a `{"$replace": true}` basis-merge directive row in `rules`, which is not a rule.
    [Fact]
    public void TestMothCourtyardParity() {
        TestShippedWorldRoundTripParity("moth-courtyard.world.json");
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

        var originalJsonText = File.ReadAllText(fullPath);
        var originalNode = JsonNode.Parse(originalJsonText);
        Assert.NotNull(originalNode);

        // 1. Decompile JSON -> .puck
        var decompiledPuck = WorldDecompiler.Decompile(originalJsonText);
        Assert.False(string.IsNullOrWhiteSpace(decompiledPuck), $"Decompiled source was empty for {relativePath}");

        // 2. Parse .puck -> AST
        var diagnostics = new DiagnosticBag();
        var parseResult = PuckParser.ParseDocumentWithDiagnostics(decompiledPuck, diagnostics: diagnostics);

        Assert.False(
            diagnostics.HasErrors,
            $"Parse errors for {relativePath}:{Environment.NewLine}{diagnostics.FormatReport(decompiledPuck)}"
        );
        Assert.NotNull(parseResult.Value);

        // 3. Lower AST -> JSON
        var loweringDiagnostics = new DiagnosticBag();
        var baseDir = Path.GetDirectoryName(fullPath);
        var loweringResult = WorldDocumentEmitter.LowerWithDiagnostics(
            parseResult.Value,
            basePath: baseDir,
            diagnostics: loweringDiagnostics
        );

        Assert.False(
            loweringDiagnostics.HasErrors,
            $"Lowering errors for {relativePath}:{Environment.NewLine}{loweringDiagnostics.FormatReport(decompiledPuck)}"
        );
        Assert.NotNull(loweringResult.Value);

        // 4. Parity check: Canonical serialization structural equivalence
        var originalCanonicalBytes = CanonicalJsonDocument.Serialize(originalNode);
        var roundtripCanonicalBytes = CanonicalJsonDocument.Serialize(loweringResult.Value);

        var originalCanonicalJson = System.Text.Encoding.UTF8.GetString(originalCanonicalBytes);
        var roundtripCanonicalJson = System.Text.Encoding.UTF8.GetString(roundtripCanonicalBytes);

        var originalRoundtripNode = JsonNode.Parse(originalCanonicalJson);
        var recompiledNode = JsonNode.Parse(roundtripCanonicalJson);

        var mismatch = JsonMismatch.Find(originalRoundtripNode, recompiledNode, $"{relativePath}:");
        Assert.Null(mismatch);
    }

}
