using System.Text.Json.Nodes;
using Puck.World.Transpiler.Decompiler;
using Puck.World.Transpiler.Diagnostics;
using Puck.World.Transpiler.Lowering;
using Puck.World.Transpiler.Parsing;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>Every sugar spelling reprints the whole node or does not apply. A key the sugar has no slot for —
/// including one present with an explicit JSON null, which <c>ActionEffect</c>/<c>ActionPredicate</c> serialize on
/// fields carrying no <c>JsonIgnore(WhenWritingNull)</c> — sends the node to call form rather than being
/// dropped.</summary>
public class SugarFallbackTests {
    private static JsonObject Recompile(string puck) {
        var parseDiagnostics = new DiagnosticBag();
        var parseResult = PuckParser.ParseDocumentWithDiagnostics(puck, diagnostics: parseDiagnostics);
        Assert.False(parseDiagnostics.HasErrors, parseDiagnostics.FormatReport(puck));

        var loweringDiagnostics = new DiagnosticBag();
        var lowered = WorldDocumentEmitter.LowerWithDiagnostics(parseResult.Value!, diagnostics: loweringDiagnostics);
        Assert.False(loweringDiagnostics.HasErrors, loweringDiagnostics.FormatReport(puck));

        return lowered.Value!;
    }

    private static string AssertRoundTripsExactly(string json) {
        var puck = WorldDecompiler.Decompile(json);
        Assert.Null(JsonMismatch.Find(JsonNode.Parse(json), Recompile(puck), ""));

        return puck;
    }

    [Fact]
    public void CompareStateWithAnExplicitNullValueKeepsTheNullThroughCallForm() {
        const string json = """
            {"schema":"puck.world.def.v1","rules":[
              {"name":"r","gate":{"$type":"compareState","comparison":"Equal","state":"a","value":null},
               "effects":[{"$type":"setState","state":"z","value":1}]}
            ]}
            """;

        var puck = AssertRoundTripsExactly(json);

        Assert.Contains("gate: compareState(comparison: \"Equal\", state: \"a\", value: null)", puck, StringComparison.Ordinal);
        Assert.DoesNotContain("when a == 0", puck, StringComparison.Ordinal);
    }

    [Fact]
    public void CompareStateWithAnExplicitNullComparandStateKeepsTheKeyThroughCallForm() {
        const string json = """
            {"schema":"puck.world.def.v1","rules":[
              {"name":"r","gate":{"$type":"compareState","comparison":"NotEqual","state":"b","comparandState":null},
               "effects":[{"$type":"setState","state":"z","value":1}]}
            ]}
            """;

        var puck = AssertRoundTripsExactly(json);

        Assert.Contains("comparandState: null", puck, StringComparison.Ordinal);
        Assert.DoesNotContain("when b != 0", puck, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"$type":"setState","state":"a","fromState":"b","value":null}""")]
    [InlineData("""{"$type":"setState","state":"c","value":null,"expression":"b + 1"}""")]
    [InlineData("""{"$type":"addState","state":"d","value":null,"fromState":"e"}""")]
    [InlineData("""{"$type":"setState","state":"f","value":1,"target":"Self"}""")]
    [InlineData("""{"$type":"pushState","state":"g","fromState":"h","value":null}""")]
    [InlineData("""{"$type":"countdownState","state":"i","target":"Self"}""")]
    [InlineData("""{"$type":"scheduleState","state":"j","delaySeconds":2,"target":"Self"}""")]
    public void AnEffectCarryingAKeyTheSugarWouldNotReprintFallsToCallForm(string effectJson) {
        var json = $$"""
            {"schema":"puck.world.def.v1","rules":[
              {"name":"r","effects":[{{effectJson}}]}
            ]}
            """;

        var puck = AssertRoundTripsExactly(json);

        Assert.Contains("(state: \"", puck, StringComparison.Ordinal);
    }

    [Fact]
    public void APrototypesRowWhoseDocumentIsNullKeepsTheKeyThroughTheGenericValuePath() {
        const string json = """
            {"schema":"puck.world.def.v1","prototypes":[{"id":"p3","document":null}]}
            """;

        var puck = AssertRoundTripsExactly(json);

        Assert.Contains("document: null", puck, StringComparison.Ordinal);
    }

    // A one-letter typo in a row keyword used to drop the whole row, its shapes included, with exit code 0.
    [Theory]
    [InlineData("prototypes", "prototpye \"typo\" { document { shape Box \"b\" { } } }", "prototpye")]
    [InlineData("prototypes", "version: 3", "version")]
    [InlineData("placements", "placemnt \"typo\" { prototypeId: \"p\" }", "placemnt")]
    public void AStatementASectionCannotCarryIsReportedRatherThanDropped(string section, string statement, string spelling) {
        var source = $"schema: \"puck.world.def.v1\"\n{section} {{\n    {statement}\n}}\n";

        var parseResult = PuckParser.ParseDocumentWithDiagnostics(source);
        Assert.False(parseResult.Diagnostics.HasErrors, parseResult.Diagnostics.FormatReport(source));

        var diagnostics = new DiagnosticBag();
        WorldDocumentEmitter.LowerWithDiagnostics(parseResult.Value!, diagnostics: diagnostics);

        var finding = Assert.Single(diagnostics, d => d.Code == PuckDiagnosticCodes.UnrecognizedSectionStatement);
        Assert.Contains(spelling, finding.Message, StringComparison.Ordinal);
        Assert.Equal(3, finding.Span.Line);
        Assert.Equal(5, finding.Span.Column);
    }

    [Fact]
    public void ExportNamesOnAnIndentedContinuationLineAreRead() {
        var lowered = Recompile("schema: \"puck.world.def.v1\"\n\nexport action\n    moveCell, moveRequest\n\nexport binding\n    bTest\n");

        var exports = Assert.IsType<JsonObject>(lowered["exports"]);
        Assert.Equal(["moveCell", "moveRequest"], Assert.IsType<JsonArray>(exports["actions"]).Select(n => n!.ToString()));
        Assert.Equal(["bTest"], Assert.IsType<JsonArray>(exports["bindings"]).Select(n => n!.ToString()));
    }

    // The empty-facet spelling the decompiler emits for an empty exported array: nothing follows the facet word on
    // its own line, and the next statement sits at the same indentation, so it is not read as a name.
    [Fact]
    public void ABareExportFacetLowersToAnEmptyArrayWithoutSwallowingTheNextStatement() {
        var lowered = Recompile("schema: \"puck.world.def.v1\"\n\nexport action\nexport binding\n\nname: \"t\"\n");

        var exports = Assert.IsType<JsonObject>(lowered["exports"]);
        Assert.Empty(Assert.IsType<JsonArray>(exports["actions"]));
        Assert.Empty(Assert.IsType<JsonArray>(exports["bindings"]));
        Assert.Equal("t", lowered["name"]?.ToString());
    }

    [Theory]
    [InlineData("    when hp > 0 // a trailing comment on the gate line\n    hp = 1")]
    [InlineData("    when hp > 0\n    bind tmp : Int = hp + 1 // after a bind\n    hp = 1")]
    public void ATrailingCommentEndsAGateOrBindOperandRatherThanJoiningIt(string body) {
        var lowered = Recompile($"schema: \"puck.world.def.v1\"\nrule \"r\" {{\n{body}\n}}\n");

        var gate = Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(lowered["rules"])[0]!["gate"]);
        Assert.Equal("compareState", gate["$type"]?.ToString());
        Assert.Equal("hp", gate["state"]?.ToString());
    }
}
