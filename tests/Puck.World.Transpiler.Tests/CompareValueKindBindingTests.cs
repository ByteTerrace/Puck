using System.Text.Json.Nodes;
using Puck.World.Transpiler.Decompiler;
using Puck.Transpiler.Diagnostics;
using Puck.World.Transpiler.Lowering;
using Puck.Transpiler.Parsing;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>A `compareValue` kind annotation binds to the ONE comparison it follows, never to an enclosing
/// `and`/`or` chain — parenthesizing that comparison is how both the author and `WorldDecompiler` make the binding
/// visible rather than letting the suffix trail the whole chain.</summary>
public class CompareValueKindBindingTests {
    private static void AssertRoundTripsExactly(string json) {
        var puck = WorldDecompiler.Decompile(jsonText: json);

        var parseDiagnostics = new DiagnosticBag();
        var parseResult = PuckParser.ParseDocumentWithDiagnostics(
            puck,
            diagnostics: parseDiagnostics
        );

        Assert.False(
            condition: parseDiagnostics.HasErrors,
            userMessage: parseDiagnostics.FormatReport(puck)
        );

        var loweringDiagnostics = new DiagnosticBag();
        var recompiled = WorldDocumentEmitter.LowerWithDiagnostics(
            parseResult.Value!,
            diagnostics: loweringDiagnostics,
            cancellationToken: TestContext.Current.CancellationToken
        ).Value!;

        Assert.False(
            condition: loweringDiagnostics.HasErrors,
            userMessage: loweringDiagnostics.FormatReport(puck)
        );

        Assert.Null(@object: JsonMismatch.Find(
            JsonNode.Parse(json),
            recompiled,
            ""
        ));
    }
    private static JsonObject Lower(string body) {
        var source = $"schema: \"puck.world.definition.v1\"\n\n{body}";
        var parseResult = PuckParser.ParseDocumentWithDiagnostics(source);

        Assert.False(
            condition: parseResult.Diagnostics.HasErrors,
            userMessage: parseResult.Diagnostics.FormatReport(source)
        );

        var diagnostics = new DiagnosticBag();
        var loweringResult = WorldDocumentEmitter.LowerWithDiagnostics(
            parseResult.Value!,
            diagnostics: diagnostics,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.False(
            condition: diagnostics.HasErrors,
            userMessage: diagnostics.FormatReport(source)
        );
        return loweringResult.Value!;
    }

    // With no `kind` at all there is no annotation to print, so the node has no sugar spelling and must fall to
    // the call form, which carries `$type` verbatim.
    [Fact]
    public void AKindlessCompareValueOverSimpleReadsFallsToCallForm() {
        const string Json = """
            {"schema":"puck.world.definition.v1","rules":[
              {"name":"r","gate":{"$type":"compareValue","comparison":"Greater","left":"a","right":"b"},
               "effects":[{"$type":"setState","state":"hp","value":0}]}
            ]}
            """;

        var puck = WorldDecompiler.Decompile(jsonText: Json);

        Assert.Contains(
            actualString: puck,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "gate: compareValue(comparison: \"Greater\", left: \"a\", right: \"b\")"
        );
        AssertRoundTripsExactly(json: Json);
    }
    // `Fixed` stays elided only where the bare text re-lowers to a `compareValue` on its own, which a
    // multi-token operand guarantees.
    [Fact]
    public void DecompilerLeavesDefaultFixedKindUnannotatedWhenTheBareTextKeepsCompareValue() {
        const string Json = """
            {"schema":"puck.world.definition.v1","rules":[
              {"name":"r","gate":{"$type":"compareValue","comparison":"NotEqual","kind":"Fixed","left":"b + 1","right":"c"},
               "effects":[{"$type":"setState","state":"hp","value":0}]}
            ]}
            """;

        var puck = WorldDecompiler.Decompile(jsonText: Json);

        Assert.Contains(
            actualString: puck,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "when b + 1 != c"
        );
        Assert.DoesNotContain(
            actualString: puck,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "Fixed"
        );
        AssertRoundTripsExactly(json: Json);
    }
    // Two plain row reads re-lower to `compareState`, so eliding the default kind here would silently change the
    // predicate's own type: the annotation is what forces `compareValue` back, and must be printed.
    [Fact]
    public void DecompilerPrintsTheDefaultFixedKindWhenTheBareTextWouldReLowerToCompareState() {
        const string Json = """
            {"schema":"puck.world.definition.v1","rules":[
              {"name":"r","gate":{"$type":"all","predicates":[
                 {"$type":"compareValue","comparison":"Greater","kind":"Fixed","left":"a","right":"b"},
                 {"$type":"compareState","comparison":"NotEqual","state":"c","comparandState":"d"}]},
               "effects":[{"$type":"setState","state":"hp","value":0}]}
            ]}
            """;

        var puck = WorldDecompiler.Decompile(jsonText: Json);

        Assert.Contains(
            actualString: puck,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "when (a > b : Fixed) and c != d"
        );
        AssertRoundTripsExactly(json: Json);
    }
    [Fact]
    public void DecompilerWrapsIntKindComparisonInParensWithinAMixedChain() {
        const string Json = """
            {"schema":"puck.world.definition.v1","rules":[
              {"name":"r","gate":{"$type":"all","predicates":[
                {"$type":"compareState","state":"a","comparison":"Equal","value":1},
                {"$type":"compareValue","comparison":"NotEqual","kind":"Int","left":"b","right":"c"}
              ]},
               "effects":[{"$type":"setState","state":"hp","value":0}]}
            ]}
            """;

        var puck = WorldDecompiler.Decompile(jsonText: Json);

        Assert.Contains(
            actualString: puck,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "when a == 1 and (b != c : Int)"
        );

        // The annotation must survive the round trip attached to exactly the compareValue predicate, not the chain.
        var parseDiagnostics = new DiagnosticBag();
        var parseResult = PuckParser.ParseDocumentWithDiagnostics(
            puck,
            diagnostics: parseDiagnostics
        );

        Assert.False(
            condition: parseDiagnostics.HasErrors,
            userMessage: parseDiagnostics.FormatReport(puck)
        );
        var loweringDiagnostics = new DiagnosticBag();
        var recompiled = WorldDocumentEmitter.LowerWithDiagnostics(
            parseResult.Value!,
            diagnostics: loweringDiagnostics,
            cancellationToken: TestContext.Current.CancellationToken
        ).Value!;

        Assert.False(
            condition: loweringDiagnostics.HasErrors,
            userMessage: loweringDiagnostics.FormatReport(puck)
        );

        var mismatch = JsonMismatch.Find(
            JsonNode.Parse(Json),
            recompiled,
            ""
        );

        Assert.Null(@object: mismatch);
    }
    [Fact]
    public void ParenthesizedFixedKindInsideAndChainLowersExactCompareValueJson() {
        var json = Lower(body: """
            rule "r" {
                when a >= 0 and (b != c : Fixed)
                effectRow = 1
            }
            """);

        var predicates = Assert.IsType<JsonArray>(@object: Assert.IsType<JsonObject>(@object: Assert.IsType<JsonArray>(@object: json["rules"])[0]!["gate"])["predicates"]);
        var right = Assert.IsType<JsonObject>(@object: predicates[1]);

        Assert.Equal(
            "compareValue",
            right["$type"]?.ToString()
        );
        Assert.Equal(
            "Fixed",
            right["kind"]?.ToString()
        );
    }
    [Fact]
    public void ParenthesizedIntKindInsideAndChainLowersExactCompareValueJson() {
        var json = Lower(body: """
            rule "r" {
                when a >= 0 and (b != c : Int)
                effectRow = 1
            }
            """);

        var gate = Assert.IsType<JsonObject>(@object: Assert.IsType<JsonArray>(@object: json["rules"])[0]!["gate"]);

        Assert.Equal(
            "all",
            gate["$type"]?.ToString()
        );
        var predicates = Assert.IsType<JsonArray>(@object: gate["predicates"]);

        Assert.Equal(
            2,
            predicates.Count
        );

        var left = Assert.IsType<JsonObject>(@object: predicates[0]);

        Assert.Equal(
            "compareState",
            left["$type"]?.ToString()
        );
        Assert.Equal(
            "GreaterOrEqual",
            left["comparison"]?.ToString()
        );
        Assert.Equal(
            "a",
            left["state"]?.ToString()
        );

        var right = Assert.IsType<JsonObject>(@object: predicates[1]);

        Assert.Equal(
            "compareValue",
            right["$type"]?.ToString()
        );
        Assert.Equal(
            "NotEqual",
            right["comparison"]?.ToString()
        );
        Assert.Equal(
            "Int",
            right["kind"]?.ToString()
        );
        Assert.Equal(
            "b",
            right["left"]?.ToString()
        );
        Assert.Equal(
            "c",
            right["right"]?.ToString()
        );
    }
}
