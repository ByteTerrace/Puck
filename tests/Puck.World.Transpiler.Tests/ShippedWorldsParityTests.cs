using System.Text.Json.Nodes;
using Puck.Abstractions.Documents;
using Puck.World.Transpiler.Decompiler;
using Puck.World.Transpiler.Diagnostics;
using Puck.World.Transpiler.Lowering;
using Puck.World.Transpiler.Parsing;
using Xunit;

namespace Puck.World.Transpiler.Tests;

public class ShippedWorldsParityTests {
    private static readonly string[] ShippedWorldFiles = [
        "games/backgammon.world.json",
        "games/billiards.world.json",
        "games/bowling.world.json",
        "games/chess.world.json",
        "games/chinese-checkers.world.json",
        "games/dominoes.world.json",
        "games/freecell.world.json",
        "games/hexlines.world.json",
        "games/klondike.world.json",
        "games/mancala.world.json",
        "games/poker.world.json",
        "games/solitaire.world.json",
        "games/spider.world.json",
        "games/tictactoe.world.json",
        "study.world.json",
    ];

    public static TheoryData<string> GetShippedWorldFiles() {
        var data = new TheoryData<string>();

        foreach (var file in ShippedWorldFiles) {
            data.Add(file);
        }

        return data;
    }

    [Fact]
    public void TestBackgammonParity() {
        TestShippedWorldRoundTripParity("games/backgammon.world.json");
    }

    [Fact]
    public void TestBilliardsParity() {
        TestShippedWorldRoundTripParity("games/billiards.world.json");
    }

    [Fact]
    public void TestChineseCheckersParity() {
        TestShippedWorldRoundTripParity("games/chinese-checkers.world.json");
    }

    [Fact]
    public void TestDominoesParity() {
        TestShippedWorldRoundTripParity("games/dominoes.world.json");
    }

    [Fact]
    public void TestHexlinesParity() {
        TestShippedWorldRoundTripParity("games/hexlines.world.json");
    }

    [Fact]
    public void TestTictactoeParity() {
        TestShippedWorldRoundTripParity("games/tictactoe.world.json");
    }

    [Theory]
    [MemberData(nameof(GetShippedWorldFiles))]
    public void TestShippedWorldRoundTripParity(string relativePath) {
        var worldsDir = FindWorldsDirectory();
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

        var mismatch = FindFirstMismatch(originalRoundtripNode, recompiledNode, $"{relativePath}:");
        Assert.Null(mismatch);
    }

    private static string? FindFirstMismatch(JsonNode? a, JsonNode? b, string path) {
        if (a is null && b is null) {
            return null;
        }
        if (a is null) {
            return $"{path}: expected null, actual '{b?.ToJsonString()}'";
        }
        if (b is null) {
            return $"{path}: expected '{a.ToJsonString()}', actual null";
        }
        if (a.GetValueKind() != b.GetValueKind()) {
            return $"{path}: kinds differ: expected {a.GetValueKind()}, actual {b.GetValueKind()}";
        }

        if (a is JsonObject objA && b is JsonObject objB) {
            foreach (var kvp in objA) {
                if (!objB.ContainsKey(kvp.Key)) {
                    return $"{path}: missing property '{kvp.Key}'";
                }
                var diff = FindFirstMismatch(kvp.Value, objB[kvp.Key], $"{path}/{kvp.Key}");
                if (diff is not null) {
                    return diff;
                }
            }
            foreach (var kvp in objB) {
                if (!objA.ContainsKey(kvp.Key)) {
                    return $"{path}: unexpected extra property '{kvp.Key}'";
                }
            }
            return null;
        }

        if (a is JsonArray arrA && b is JsonArray arrB) {
            if (arrA.Count != arrB.Count) {
                return $"{path}: array length expected {arrA.Count}, actual {arrB.Count}";
            }
            for (var i = 0; i < arrA.Count; i++) {
                var diff = FindFirstMismatch(arrA[i], arrB[i], $"{path}[{i}]");
                if (diff is not null) {
                    return diff;
                }
            }
            return null;
        }

        if (a is JsonValue valA && b is JsonValue valB) {
            if (!JsonNode.DeepEquals(valA, valB)) {
                return $"{path}: value expected '{valA.ToJsonString()}', actual '{valB.ToJsonString()}'";
            }
            return null;
        }

        return null;
    }

    private static string FindWorldsDirectory() {
        var dir = AppContext.BaseDirectory;

        while (dir is not null) {
            var candidate = Path.Combine(dir, "src", "Puck.World", "Assets", "worlds");

            if (Directory.Exists(candidate)) {
                return candidate;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException("Could not locate src/Puck.World/Assets/worlds directory from test runner.");
    }
}
