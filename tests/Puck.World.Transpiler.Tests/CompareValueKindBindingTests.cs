using System.Text.Json.Nodes;
using Puck.World.Transpiler.Decompiler;
using Puck.Transpiler.Diagnostics;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>A `compareValue` kind annotation binds to the ONE comparison it follows, never to an enclosing
/// `and`/`or` chain — parenthesizing that comparison is how both the author and `WorldDecompiler` make the binding
/// visible rather than letting the suffix trail the whole chain.</summary>
public class CompareValueKindBindingTests {
    private static void AssertRoundTripsExactly(string json) {
        var puck = WorldDecompiler.Decompile(jsonText: Document(json: json));

        var loweringDiagnostics = new DiagnosticBag();
        var recompiled = WorldCompiler.Compile(
            cancellationToken: TestContext.Current.CancellationToken,
            diagnostics: loweringDiagnostics,
            source: puck
        ).RequireJson();

        Assert.False(
            condition: loweringDiagnostics.HasErrors,
            userMessage: loweringDiagnostics.FormatReport(puck)
        );

        Assert.Null(@object: JsonMismatch.Find(
            JsonNode.Parse(Document(json: json)),
            recompiled,
            ""
        ));
    }
    // The fixture spells its expressions as infix text, exactly as an author does; the document holds the IR, so
    // the fixture is lowered the same way a compile lowers it before either side reads it.
    private static string Document(string json) {
        var node = JsonNode.Parse(json);

        WorldExpressionJson.Lower(node: node);
        WorldChannelNodes.Lower(
            document: node,
            type: typeof(WorldDefinition)
        );
        return node!.ToJsonString();
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

        var puck = WorldDecompiler.Decompile(jsonText: Document(json: Json));

        Assert.Contains(
            actualString: puck,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "gate: compareValue(comparison: Greater, left: a, right: b)"
        );
        AssertRoundTripsExactly(json: Json);
    }
    // A comparison with no kind infers one, so its bare text re-lowers to the same kindless node wherever a
    // multi-token operand keeps it a `compareValue`.
    [Fact]
    public void DecompilerLeavesAKindlessCompareValueUnannotatedWhenTheBareTextKeepsCompareValue() {
        const string Json = """
            {"schema":"puck.world.definition.v1","rules":[
              {"name":"r","gate":{"$type":"compareValue","comparison":"NotEqual","left":"b + 1","right":"c"},
               "effects":[{"$type":"setState","state":"hp","value":0}]}
            ]}
            """;

        var puck = WorldDecompiler.Decompile(jsonText: Document(json: Json));

        Assert.Contains(
            actualString: puck,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "when b + 1 != c"
        );
        AssertRoundTripsExactly(json: Json);
    }
    // A declared kind is printed even where a kindless comparison would print bare: eliding it would re-lower to an
    // inferred kind, which is a different node.
    [Fact]
    public void DecompilerPrintsADeclaredFixedKindWhereTheBareTextKeepsCompareValue() {
        const string Json = """
            {"schema":"puck.world.definition.v1","rules":[
              {"name":"r","gate":{"$type":"compareValue","comparison":"NotEqual","kind":"Fixed","left":"b + 1","right":"c"},
               "effects":[{"$type":"setState","state":"hp","value":0}]}
            ]}
            """;

        var puck = WorldDecompiler.Decompile(jsonText: Document(json: Json));

        Assert.Contains(
            actualString: puck,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "when (b + 1 != c : Fixed)"
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

        var puck = WorldDecompiler.Decompile(jsonText: Document(json: Json));

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

        var puck = WorldDecompiler.Decompile(jsonText: Document(json: Json));

        Assert.Contains(
            actualString: puck,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "when a == 1 and (b != c : Int)"
        );

        // The annotation must survive the round trip attached to exactly the compareValue predicate, not the chain.
        var loweringDiagnostics = new DiagnosticBag();
        var recompiled = WorldCompiler.Compile(
            cancellationToken: TestContext.Current.CancellationToken,
            diagnostics: loweringDiagnostics,
            source: puck
        ).RequireJson();

        Assert.False(
            condition: loweringDiagnostics.HasErrors,
            userMessage: loweringDiagnostics.FormatReport(puck)
        );

        var mismatch = JsonMismatch.Find(
            JsonNode.Parse(Document(json: Json)),
            recompiled,
            ""
        );

        Assert.Null(@object: mismatch);
    }
    [Fact]
    public void ParenthesizedFixedKindInsideAndChainLowersExactCompareValueJson() {
        var json = WorldSources.LowerClean(body: """
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
        var json = WorldSources.LowerClean(body: """
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
            WorldExpressionJson.Text(node: right["left"])
        );
        Assert.Equal(
            "c",
            WorldExpressionJson.Text(node: right["right"])
        );
    }
}
