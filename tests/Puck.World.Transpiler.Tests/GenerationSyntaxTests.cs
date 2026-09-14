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
            PuckParser.ParseDocument($"schema: \"puck.world.definition.v1\"\n\n{body}"),
            diagnostics: diagnostics,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.False(
            condition: diagnostics.HasErrors,
            userMessage: string.Join(
                separator: "\n",
                values: diagnostics.Select(selector: d => $"{d.Code}: {d.Message}")
            )
        );

        return lowered.Value!;
    }
    private static DiagnosticBag LowerForDiagnostics(string body) {
        var diagnostics = new DiagnosticBag();

        WorldDocumentEmitter.LowerWithDiagnostics(
            PuckParser.ParseDocument($"schema: \"puck.world.definition.v1\"\n\n{body}"),
            diagnostics: diagnostics,
            cancellationToken: TestContext.Current.CancellationToken
        );

        return diagnostics;
    }
    private static string Text(JsonNode? node) => Assert.IsAssignableFrom<JsonValue>(@object: node).GetValue<string>();

    [Fact]
    public void TestAConstantCannotReadALoopBinding() {
        // What makes the kept value safe to keep: a `let` is a document-level value, lowered with no locals in
        // scope, so it reads the same at every reference rather than picking up whichever loop happens to enclose
        // the one that lowered it first.
        var rows = Assert.IsType<JsonArray>(@object: Lower(body: """
            let i = 99
            let fixed = i

            prototypes {
                for i in range(0, 2) {
                    prototype $"p-{i}" {
                        span: fixed
                    }
                }
            }
            """)["prototypes"]);

        Assert.Equal(
            2,
            rows.Count
        );
        Assert.Equal(
            99L,
            Assert.IsType<JsonObject>(@object: rows[0])["span"]?.GetValue<long>()
        );
        Assert.Equal(
            99L,
            Assert.IsType<JsonObject>(@object: rows[1])["span"]?.GetValue<long>()
        );
    }
    [Fact]
    public void TestAConstantIsComputedOnceNotPerReference() {
        // A `let` names a value. Re-lowering its expression at every reference made a CHAIN of them exponential:
        // each layer rebuilt the whole of the layer beneath it, once per element. Six layers over 64 elements is
        // 64^6 rebuilds if the value is not kept, and finishes immediately if it is — so this stands as a
        // complexity guard, not merely a correctness one.
        var lowered = Lower(body: """
            let a0 = map(range(0, 64), i => i)
            let a1 = map(range(0, 64), i => a0[i] + 1)
            let a2 = map(range(0, 64), i => a1[i] + 1)
            let a3 = map(range(0, 64), i => a2[i] + 1)
            let a4 = map(range(0, 64), i => a3[i] + 1)
            let a5 = map(range(0, 64), i => a4[i] + 1)

            width: a5[10]
            """);

        Assert.Equal(
            15L,
            lowered["width"]?.GetValue<long>()
        );
    }
    [Fact]
    public void TestAPlainStringNeverInterpolates() {
        Assert.Equal(
            "{leg}",
            Text(node: Lower(body: """
            let leg = "braid"

            documentId: "{leg}"
            """)["documentId"])
        );
    }
    [Fact]
    public void TestARawStringCarriesItsTextVerbatim() {
        const string Fence = "\"\"\"";
        var lowered = Lower(body: ((("documentId: " + Fence) + "\n    first\n      second\n    ") + Fence));

        // The closing fence sets the indent that comes off every line; relative indentation survives.
        Assert.Equal(
            "first\n  second",
            Text(node: lowered["documentId"])
        );
    }
    [Fact]
    public void TestARawStringInterpolatesUnderTheDollarPrefix() {
        const string Fence = "\"\"\"";
        var lowered = Lower(body: ((("let leg = \"braid\"\n\ndocumentId: $" + Fence) + "\n    {leg}-1\n    ") + Fence));

        Assert.Equal(
            "braid-1",
            Text(node: lowered["documentId"])
        );
    }
    [Fact]
    public void TestARawStringTakesNoEscapes() {
        const string Fence = "\"\"\"";
        var lowered = Lower(body: ((("documentId: " + Fence) + "\n    a\\nb\n    ") + Fence));

        Assert.Equal(
            "a\\nb",
            Text(node: lowered["documentId"])
        );
    }
    [Fact]
    public void TestAWholeNumberHolePrintsWithoutADecimalPoint() {
        Assert.Equal(
            "root-1",
            Text(node: Lower(body: """documentId: $"root-{floor(7 / 4)}" """)["documentId"])
        );
    }
    [Fact]
    public void TestAnIndexOnTheNextLineIsTheNextElement() {
        // An array's elements are newline-separated, so a '[' opening a line is never an index on the line above.
        var rows = Assert.IsType<JsonArray>(@object: Lower(body: """
            curve [
                [0, 0]
                [1, 0.5]
            ]
            """)["curve"]);

        Assert.Equal(
            2,
            rows.Count
        );
        Assert.Equal(
            2,
            Assert.IsType<JsonArray>(@object: rows[1]).Count
        );
    }
    [Fact]
    public void TestDoubledBracesAreOneLiteralBrace() {
        Assert.Equal(
            "{x}",
            Text(node: Lower(body: """documentId: $"{{x}}" """)["documentId"])
        );
    }
    [Fact]
    public void TestForBindingIsGoneAfterTheLoop() {
        // The binding is a local: it shadows nothing permanently and does not leak past its own iteration.
        var lowered = Lower(body: """
            let i = "outer"

            for i in range(0, 1) {
            }

            documentId: i
            """);

        Assert.Equal(
            "outer",
            Text(node: lowered["documentId"])
        );
    }
    [Fact]
    public void TestForBindsTheIndexWhenAsked() {
        var rows = Assert.IsType<JsonArray>(@object: Lower(body: """
            prototypes {
                for (name, i) in ["near", "far"] {
                    prototype $"{name}-{i}" {
                    }
                }
            }
            """)["prototypes"]);

        Assert.Equal(
            "near-0",
            Text(node: Assert.IsType<JsonObject>(@object: rows[0])["id"])
        );
        Assert.Equal(
            "far-1",
            Text(node: Assert.IsType<JsonObject>(@object: rows[1])["id"])
        );
    }
    [Fact]
    public void TestForEmitsItsBodyOncePerElement() {
        var rows = Assert.IsType<JsonArray>(@object: Lower(body: """
            prototypes {
                for i in range(0, 3) {
                    prototype $"view-{i}" {
                        span: 100 + i
                    }
                }
            }
            """)["prototypes"]);

        Assert.Equal(
            3,
            rows.Count
        );
        Assert.Equal(
            "view-0",
            Text(node: Assert.IsType<JsonObject>(@object: rows[0])["id"])
        );
        Assert.Equal(
            "view-2",
            Text(node: Assert.IsType<JsonObject>(@object: rows[2])["id"])
        );
        Assert.Equal(
            102L,
            Assert.IsType<JsonObject>(@object: rows[2])["span"]?.GetValue<long>()
        );
    }
    [Fact]
    public void TestForKeepsDocumentOrderAroundIt() {
        var rows = Assert.IsType<JsonArray>(@object: Lower(body: """
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
            rows.Select(selector: row => Text(node: Assert.IsType<JsonObject>(@object: row)["id"])).ToArray()
        );
    }
    [Fact]
    public void TestIndexingReadsArraysAndObjects() {
        var lowered = Lower(body: """
            let rows = [10, 20, 30]
            let named = { alpha: "a", beta: "b" }

            width: rows[1]
            documentId: named["beta"]
            """);

        Assert.Equal(
            20L,
            lowered["width"]?.GetValue<long>()
        );
        Assert.Equal(
            "b",
            Text(node: lowered["documentId"])
        );
    }
    [Fact]
    public void TestInterpolationSubstitutesItsHoles() {
        var lowered = Lower(body: """
            let strand = 1
            let leg = "braid"

            documentId: $"{leg}-{strand}-{2 + 3}"
            """);

        Assert.Equal(
            "braid-1-5",
            Text(node: lowered["documentId"])
        );
    }
    [Fact]
    public void TestNumberBuiltins() {
        var lowered = Lower(body: """
            a: floor(7 / 2)
            b: ceiling(7 / 2)
            c: absolute(0 - 3)
            d: minimum(4, 9)
            e: maximum(4, 9)
            f: round(5 / 2)
            """);

        Assert.Equal(
            3L,
            lowered["a"]?.GetValue<long>()
        );
        Assert.Equal(
            4L,
            lowered["b"]?.GetValue<long>()
        );
        Assert.Equal(
            3L,
            lowered["c"]?.GetValue<long>()
        );
        Assert.Equal(
            4L,
            lowered["d"]?.GetValue<long>()
        );
        Assert.Equal(
            9L,
            lowered["e"]?.GetValue<long>()
        );
        Assert.Equal(
            2L,
            lowered["f"]?.GetValue<long>()
        );
    }
    [Fact]
    public void TestRefusalsAreNamed() {
        Assert.Contains(
            collection: LowerForDiagnostics(body: """
            let rows = [1, 2]

            width: rows[5]
            """),
            filter: diagnostic => (diagnostic.Code == PuckDiagnosticCodes.IndexRefused)
        );

        Assert.Contains(
            collection: LowerForDiagnostics(body: """
            for i in 3 {
            }
            """),
            filter: diagnostic => (diagnostic.Code == PuckDiagnosticCodes.ForSequenceRefused)
        );
    }
}
