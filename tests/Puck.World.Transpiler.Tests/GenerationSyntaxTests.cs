using System.Text.Json.Nodes;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Parsing;
using Puck.World.Transpiler.Lowering;
using Xunit;

namespace Puck.World.Transpiler.Tests;

// Interpolation, raw strings, `for`, indexing and the number builtins: the pieces that let a document say how its
// data is generated instead of listing it.
public class GenerationSyntaxTests {
    private static JsonObject Lower(string body) {
        var diagnostics = new DiagnosticBag();
        var lowered = WorldDocumentEmitter.LowerWithDiagnostics(
            PuckParser.ParseDocument($"schema: \"puck.world.def.v1\"\n\n{body}"),
            diagnostics: diagnostics);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics.Select(d => $"{d.Code}: {d.Message}")));

        return lowered.Value!;
    }

    private static DiagnosticBag LowerForDiagnostics(string body) {
        var diagnostics = new DiagnosticBag();

        WorldDocumentEmitter.LowerWithDiagnostics(
            PuckParser.ParseDocument($"schema: \"puck.world.def.v1\"\n\n{body}"),
            diagnostics: diagnostics);

        return diagnostics;
    }

    private static string Text(JsonNode? node) => Assert.IsAssignableFrom<JsonValue>(node).GetValue<string>();

    [Fact]
    public void TestInterpolationSubstitutesItsHoles() {
        var lowered = Lower("""
            let strand = 1
            let leg = "braid"

            documentId: $"{leg}-{strand}-{2 + 3}"
            """);

        Assert.Equal("braid-1-5", Text(lowered["documentId"]));
    }

    [Fact]
    public void TestAWholeNumberHolePrintsWithoutADecimalPoint() {
        Assert.Equal("root-1", Text(Lower("""documentId: $"root-{floor(7 / 4)}" """)["documentId"]));
    }

    [Fact]
    public void TestDoubledBracesAreOneLiteralBrace() {
        Assert.Equal("{x}", Text(Lower("""documentId: $"{{x}}" """)["documentId"]));
    }

    [Fact]
    public void TestAPlainStringNeverInterpolates() {
        Assert.Equal("{leg}", Text(Lower("""
            let leg = "braid"

            documentId: "{leg}"
            """)["documentId"]));
    }

    [Fact]
    public void TestARawStringCarriesItsTextVerbatim() {
        const string fence = "\"\"\"";
        var lowered = Lower("documentId: " + fence + "\n    first\n      second\n    " + fence);

        // The closing fence sets the indent that comes off every line; relative indentation survives.
        Assert.Equal("first\n  second", Text(lowered["documentId"]));
    }

    [Fact]
    public void TestARawStringTakesNoEscapes() {
        const string fence = "\"\"\"";
        var lowered = Lower("documentId: " + fence + "\n    a\\nb\n    " + fence);

        Assert.Equal("a\\nb", Text(lowered["documentId"]));
    }

    [Fact]
    public void TestARawStringInterpolatesUnderTheDollarPrefix() {
        const string fence = "\"\"\"";
        var lowered = Lower("let leg = \"braid\"\n\ndocumentId: $" + fence + "\n    {leg}-1\n    " + fence);

        Assert.Equal("braid-1", Text(lowered["documentId"]));
    }

    [Fact]
    public void TestForEmitsItsBodyOncePerElement() {
        var rows = Assert.IsType<JsonArray>(Lower("""
            prototypes {
                for i in range(0, 3) {
                    prototype $"view-{i}" {
                        span: 100 + i
                    }
                }
            }
            """)["prototypes"]);

        Assert.Equal(3, rows.Count);
        Assert.Equal("view-0", Text(Assert.IsType<JsonObject>(rows[0])["id"]));
        Assert.Equal("view-2", Text(Assert.IsType<JsonObject>(rows[2])["id"]));
        Assert.Equal(102L, Assert.IsType<JsonObject>(rows[2])["span"]?.GetValue<long>());
    }

    [Fact]
    public void TestForBindsTheIndexWhenAsked() {
        var rows = Assert.IsType<JsonArray>(Lower("""
            prototypes {
                for (name, i) in ["near", "far"] {
                    prototype $"{name}-{i}" {
                    }
                }
            }
            """)["prototypes"]);

        Assert.Equal("near-0", Text(Assert.IsType<JsonObject>(rows[0])["id"]));
        Assert.Equal("far-1", Text(Assert.IsType<JsonObject>(rows[1])["id"]));
    }

    [Fact]
    public void TestForKeepsDocumentOrderAroundIt() {
        var rows = Assert.IsType<JsonArray>(Lower("""
            prototypes {
                prototype "first" {
                }

                for i in range(0, 2) {
                    prototype $"gen-{i}" {
                    }
                }

                prototype "last" {
                }
            }
            """)["prototypes"]);

        Assert.Equal(
            ["first", "gen-0", "gen-1", "last"],
            rows.Select(row => Text(Assert.IsType<JsonObject>(row)["id"])).ToArray());
    }

    [Fact]
    public void TestForBindingIsGoneAfterTheLoop() {
        // The binding is a local: it shadows nothing permanently and does not leak past its own iteration.
        var lowered = Lower("""
            let i = "outer"

            for i in range(0, 1) {
            }

            documentId: i
            """);

        Assert.Equal("outer", Text(lowered["documentId"]));
    }

    [Fact]
    public void TestIndexingReadsArraysAndObjects() {
        var lowered = Lower("""
            let rows = [10, 20, 30]
            let named = { alpha: "a", beta: "b" }

            width: rows[1]
            documentId: named["beta"]
            """);

        Assert.Equal(20L, lowered["width"]?.GetValue<long>());
        Assert.Equal("b", Text(lowered["documentId"]));
    }

    [Fact]
    public void TestAnIndexOnTheNextLineIsTheNextElement() {
        // An array's elements are newline-separated, so a '[' opening a line is never an index on the line above.
        var rows = Assert.IsType<JsonArray>(Lower("""
            curve [
                [0, 0]
                [1, 0.5]
            ]
            """)["curve"]);

        Assert.Equal(2, rows.Count);
        Assert.Equal(2, Assert.IsType<JsonArray>(rows[1]).Count);
    }

    [Fact]
    public void TestNumberBuiltins() {
        var lowered = Lower("""
            a: floor(7 / 2)
            b: ceiling(7 / 2)
            c: absolute(0 - 3)
            d: minimum(4, 9)
            e: maximum(4, 9)
            f: round(5 / 2)
            """);

        Assert.Equal(3L, lowered["a"]?.GetValue<long>());
        Assert.Equal(4L, lowered["b"]?.GetValue<long>());
        Assert.Equal(3L, lowered["c"]?.GetValue<long>());
        Assert.Equal(4L, lowered["d"]?.GetValue<long>());
        Assert.Equal(9L, lowered["e"]?.GetValue<long>());
        Assert.Equal(2L, lowered["f"]?.GetValue<long>());
    }

    [Fact]
    public void TestRefusalsAreNamed() {
        Assert.Contains(LowerForDiagnostics("""
            let rows = [1, 2]

            width: rows[5]
            """), diagnostic => diagnostic.Code == PuckDiagnosticCodes.IndexRefused);

        Assert.Contains(LowerForDiagnostics("""
            for i in 3 {
            }
            """), diagnostic => diagnostic.Code == PuckDiagnosticCodes.ForSequenceRefused);
    }
}
