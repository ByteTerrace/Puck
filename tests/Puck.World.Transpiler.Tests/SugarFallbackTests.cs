using System.Text.Json.Nodes;
using Puck.World.Transpiler.Decompiler;
using Puck.Transpiler.Diagnostics;
using Puck.World.Transpiler.Lowering;
using Puck.Transpiler.Parsing;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>Every sugar spelling reprints the whole node or does not apply. A key the sugar has no slot for —
/// including one present with an explicit JSON null, which <c>ActionEffect</c>/<c>ActionPredicate</c> serialize on
/// fields carrying no <c>JsonIgnore(WhenWritingNull)</c> — sends the node to call form rather than being
/// dropped.</summary>
public class SugarFallbackTests {
    private static string AssertRoundTripsExactly(string json) {
        var puck = WorldDecompiler.Decompile(jsonText: json);

        Assert.Null(@object: JsonMismatch.Find(
            JsonNode.Parse(json),
            Recompile(puck: puck),
            ""
        ));

        return puck;
    }
    private static JsonObject Recompile(string puck) {
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
        var lowered = WorldDocumentEmitter.LowerWithDiagnostics(
            parseResult.Value!,
            diagnostics: loweringDiagnostics,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.False(
            condition: loweringDiagnostics.HasErrors,
            userMessage: loweringDiagnostics.FormatReport(puck)
        );

        return lowered.Value!;
    }

    // The empty-facet spelling the decompiler emits for an empty exported array: nothing follows the facet word on
    // its own line, and the next statement sits at the same indentation, so it is not read as a name.
    [Fact]
    public void ABareExportFacetLowersToAnEmptyArrayWithoutSwallowingTheNextStatement() {
        var lowered = Recompile(puck: "schema: \"puck.world.definition.v1\"\n\nexport action\nexport binding\n\nname: \"t\"\n");

        var exports = Assert.IsType<JsonObject>(@object: lowered["exports"]);

        Assert.Empty(collection: Assert.IsType<JsonArray>(@object: exports["actions"]));
        Assert.Empty(collection: Assert.IsType<JsonArray>(@object: exports["bindings"]));
        Assert.Equal(
            "t",
            lowered["name"]?.ToString()
        );
    }
    [Fact]
    public void APrototypesRowWhoseDocumentIsNullKeepsTheKeyThroughTheGenericValuePath() {
        const string Json = """
            {"schema":"puck.world.definition.v1","prototypes":[{"id":"p3","document":null}]}
            """;

        var puck = AssertRoundTripsExactly(json: Json);

        Assert.Contains(
            actualString: puck,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "document: null"
        );
    }
    // A one-letter typo in a row keyword used to drop the whole row, its shapes included, with exit code 0.
    [Theory]
    [InlineData("prototypes", "prototpye \"typo\" { document { shape Box \"b\" { } } }", "prototpye")]
    [InlineData("prototypes", "version: 3", "version")]
    [InlineData("placements", "placemnt \"typo\" { prototypeId: \"p\" }", "placemnt")]
    public void AStatementASectionCannotCarryIsReportedRatherThanDropped(string section, string statement, string spelling) {
        var source = $"schema: \"puck.world.definition.v1\"\n{section} {{\n    {statement}\n}}\n";

        var parseResult = PuckParser.ParseDocumentWithDiagnostics(source);

        Assert.False(
            condition: parseResult.Diagnostics.HasErrors,
            userMessage: parseResult.Diagnostics.FormatReport(source)
        );

        var diagnostics = new DiagnosticBag();

        WorldDocumentEmitter.LowerWithDiagnostics(
            parseResult.Value!,
            diagnostics: diagnostics,
            cancellationToken: TestContext.Current.CancellationToken
        );

        var finding = Assert.Single(
            collection: diagnostics,
            predicate: d => (d.Code == PuckDiagnosticCodes.UnrecognizedSectionStatement)
        );

        Assert.Contains(
            spelling,
            finding.Message,
            StringComparison.Ordinal
        );
        Assert.Equal(
            3,
            finding.Span.Line
        );
        Assert.Equal(
            5,
            finding.Span.Column
        );
    }
    [InlineData("    when hp > 0 // a trailing comment on the gate line\n    hp = 1")]
    [InlineData("    when hp > 0\n    bind tmp : Int = hp + 1 // after a bind\n    hp = 1")]
    [Theory]
    public void ATrailingCommentEndsAGateOrBindOperandRatherThanJoiningIt(string body) {
        var lowered = Recompile(puck: $"schema: \"puck.world.definition.v1\"\nrule \"r\" {{\n{body}\n}}\n");

        var gate = Assert.IsType<JsonObject>(@object: Assert.IsType<JsonArray>(@object: lowered["rules"])[0]!["gate"]);

        Assert.Equal(
            "compareState",
            gate["$type"]?.ToString()
        );
        Assert.Equal(
            "hp",
            gate["state"]?.ToString()
        );
    }
    [InlineData("""{"$type":"setState","state":"a","fromState":"b","value":null}""")]
    [InlineData("""{"$type":"setState","state":"c","value":null,"expression":"b + 1"}""")]
    [InlineData("""{"$type":"addState","state":"d","value":null,"fromState":"e"}""")]
    [InlineData("""{"$type":"setState","state":"f","value":1,"target":"Self"}""")]
    [InlineData("""{"$type":"pushState","state":"g","fromState":"h","value":null}""")]
    [InlineData("""{"$type":"countdownState","state":"i","target":"Self"}""")]
    [InlineData("""{"$type":"scheduleState","state":"j","delaySeconds":2,"target":"Self"}""")]
    [Theory]
    public void AnEffectCarryingAKeyTheSugarWouldNotReprintFallsToCallForm(string effectJson) {
        var json = $$"""
            {"schema":"puck.world.definition.v1","rules":[
              {"name":"r","effects":[{{effectJson}}]}
            ]}
            """;

        var puck = AssertRoundTripsExactly(json: json);

        Assert.Contains(
            actualString: puck,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "(state: \""
        );
    }
    [Fact]
    public void CompareStateWithAnExplicitNullComparandStateKeepsTheKeyThroughCallForm() {
        const string Json = """
            {"schema":"puck.world.definition.v1","rules":[
              {"name":"r","gate":{"$type":"compareState","comparison":"NotEqual","state":"b","comparandState":null},
               "effects":[{"$type":"setState","state":"z","value":1}]}
            ]}
            """;

        var puck = AssertRoundTripsExactly(json: Json);

        Assert.Contains(
            actualString: puck,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "comparandState: null"
        );
        Assert.DoesNotContain(
            actualString: puck,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "when b != 0"
        );
    }
    [Fact]
    public void CompareStateWithAnExplicitNullValueKeepsTheNullThroughCallForm() {
        const string Json = """
            {"schema":"puck.world.definition.v1","rules":[
              {"name":"r","gate":{"$type":"compareState","comparison":"Equal","state":"a","value":null},
               "effects":[{"$type":"setState","state":"z","value":1}]}
            ]}
            """;

        var puck = AssertRoundTripsExactly(json: Json);

        Assert.Contains(
            actualString: puck,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "gate: compareState(comparison: \"Equal\", state: \"a\", value: null)"
        );
        Assert.DoesNotContain(
            actualString: puck,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "when a == 0"
        );
    }
    [Fact]
    public void ExportNamesOnAnIndentedContinuationLineAreRead() {
        var lowered = Recompile(puck: "schema: \"puck.world.definition.v1\"\n\nexport action\n    moveCell, moveRequest\n\nexport binding\n    bTest\n");

        var exports = Assert.IsType<JsonObject>(@object: lowered["exports"]);

        Assert.Equal(
            ["moveCell", "moveRequest"],
            Assert.IsType<JsonArray>(@object: exports["actions"]).Select(selector: n => n!.ToString())
        );
        Assert.Equal(
            ["bTest"],
            Assert.IsType<JsonArray>(@object: exports["bindings"]).Select(selector: n => n!.ToString())
        );
    }
}
