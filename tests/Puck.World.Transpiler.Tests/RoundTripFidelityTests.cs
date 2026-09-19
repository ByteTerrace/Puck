using System.Text.Json.Nodes;
using Puck.World.Transpiler.Decompiler;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>Decompile-then-compile fidelity for document shapes the row sugar cannot carry: basis-merge directive
/// rows, basis-partial rows, off-canonical enum spellings, contradictory predicate fields, and numbers a canonical
/// document spells in scientific notation. Each one must survive the round trip unchanged, through whatever
/// spelling carries it.</summary>
public class RoundTripFidelityTests {
    private static void AssertRoundTripsExactly(string json) {
        // The fixture spells its expressions as infix text, exactly as an author does; the document holds the IR,
        // so the expected side is lowered the same way a compile lowers it.
        var original = JsonNode.Parse(json);

        WorldExpressionJson.Lower(node: original);
        var recompiled = RoundTrip(json: json);
        var mismatch = JsonMismatch.Find(
            actual: recompiled,
            expected: original,
            path: ""
        );

        Assert.Null(@object: mismatch);
    }
    private static JsonObject RoundTrip(string json) {
        var source = JsonNode.Parse(json);

        WorldExpressionJson.Lower(node: source);

        var puck = WorldDecompiler.Decompile(jsonText: source!.ToJsonString());
        var lowered = WorldCompiler.Compile(
            cancellationToken: TestContext.Current.CancellationToken,
            source: puck
        );

        Assert.False(
            condition: lowered.Diagnostics.HasErrors,
            userMessage: $"{lowered.Diagnostics.FormatReport(puck)}{Environment.NewLine}{puck}"
        );

        return lowered.RequireJson();
    }

    [Fact]
    public void BasisPartialPlacementRowKeepsExactlyTheKeysItAuthored() {
        // A row without its own prototypeId is completed by the basis chain; filling yawDegrees/scale here would
        // override the value composition was about to supply.
        const string Json = """
            {"schema":"puck.world.definition.v1","basis":"b.json","placements":{"rows":[
              {"id":"tabletop","parent":"marketCourt","position":[-8,-0.5,6],"yawDegrees":0}
            ]}}
            """;

        var recompiled = RoundTrip(json: Json);
        var row = Assert.IsType<JsonObject>(@object: Assert.IsType<JsonArray>(@object: Assert.IsType<JsonObject>(@object: recompiled["placements"])["rows"])[0]);

        Assert.False(condition: row.ContainsKey(propertyName: "scale"));
        Assert.False(condition: row.ContainsKey(propertyName: "prototypeId"));
        AssertRoundTripsExactly(json: Json);
    }
    [Fact]
    public void BasisReplaceDirectiveRowInPlacementsSurvivesTheRoundTrip() {
        const string Json = """
            {"schema":"puck.world.definition.v1","placements":{"rows":[
              {"$replace":true},
              {"id":"a","prototypeId":"p","position":[0,0,0],"scale":1,"yawDegrees":0}
            ]}}
            """;

        AssertRoundTripsExactly(json: Json);
    }
    [Fact]
    public void BasisReplaceDirectiveRowInRulesSurvivesTheRoundTrip() {
        const string Json = """
            {"schema":"puck.world.definition.v1","rules":[
              {"$replace":true},
              {"name":"toggle","effects":[{"$type":"setState","state":"sky","key":"on","value":1}]}
            ]}
            """;

        var puck = WorldDecompiler.Decompile(jsonText: Json);

        Assert.DoesNotContain(
            actualString: puck,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "rule \"\""
        );
        AssertRoundTripsExactly(json: Json);
    }
    [Fact]
    public void BindingWithoutAKindIsNotGivenOne() {
        // RuleLocal.Kind has no default; inventing one would author a local the source never asked for.
        const string Json = """
            {"schema":"puck.world.definition.v1","rules":[
              {"name":"r","bindings":[{"name":"x","expression":"$each"}],
               "effects":[{"$type":"setState","state":"hp","value":0}]}
            ]}
            """;

        var puck = WorldDecompiler.Decompile(jsonText: Json);

        Assert.DoesNotContain(
            actualString: puck,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "bind x : Int"
        );
        AssertRoundTripsExactly(json: Json);
    }
    [Fact]
    public void CompareStateCarryingBothValueAndComparandLosesNeither() {
        const string Json = """
            {"schema":"puck.world.definition.v1","rules":[
              {"name":"r","gate":{"$type":"compareState","state":"a","key":"k","comparison":"Equal","value":1,"comparandState":"b","comparandKey":"j"},
               "effects":[{"$type":"setState","state":"hp","value":0}]}
            ]}
            """;

        AssertRoundTripsExactly(json: Json);
    }
    [Fact]
    public void CompareValueKindSpelledOffCanonicalKeepsItsKind() {
        const string Json = """
            {"schema":"puck.world.definition.v1","rules":[
              {"name":"r","gate":{"$type":"compareValue","comparison":"NotEqual","kind":"int","left":"a[from]","right":"a[to]"},
               "effects":[{"$type":"setState","state":"hp","value":0}]}
            ]}
            """;

        AssertRoundTripsExactly(json: Json);
    }
    [Fact]
    public void ComparisonSpelledOffCanonicalStaysCallFormRatherThanChangingOperator() {
        // The engine's enum converter accepts any casing; the sugar can only reprint the canonical spelling, so a
        // document that spelled it otherwise must not be folded onto a different operator.
        const string Json = """
            {"schema":"puck.world.definition.v1","rules":[
              {"name":"r","gate":{"$type":"compareState","state":"hp","comparison":"greaterOrEqual","value":5},
               "effects":[{"$type":"setState","state":"hp","value":0}]}
            ]}
            """;

        var puck = WorldDecompiler.Decompile(jsonText: Json);

        Assert.DoesNotContain(
            actualString: puck,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "when hp == 5"
        );
        AssertRoundTripsExactly(json: Json);
    }
    [Fact]
    public void DegreesNativeFieldsAllPrintTheirUnitNotJustYawDegrees() {
        const string Json = """
            {"schema":"puck.world.definition.v1","placements":{"rows":[
              {"id":"a","prototypeId":"p","position":[0,0,0],"scale":1,"yawDegrees":30,"outwardYawDegrees":15}
            ]}}
            """;

        var puck = WorldDecompiler.Decompile(jsonText: Json);

        Assert.Contains(
            actualString: puck,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "outwardYawDegrees: 15deg"
        );
        AssertRoundTripsExactly(json: Json);
    }
    [Fact]
    public void EmptyExportFacetArraySurvivesTheRoundTrip() {
        // An exported facet with zero names is a present-but-empty array, not an absent facet; losing it would
        // silently drop the export from the composed document.
        const string Json = """
            {"schema":"puck.world.definition.v1","exports":{"actions":[]}}
            """;

        var puck = WorldDecompiler.Decompile(jsonText: Json);

        Assert.Contains(
            actualString: puck,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "export action"
        );
        AssertRoundTripsExactly(json: Json);
    }
    [InlineData("1.5e3", 1500.0)]
    [InlineData("2E-2", 0.02)]
    [InlineData("-4.25e+2", -425.0)]
    [Theory]
    public void ExponentIsPartOfTheNumberNotAUnitSuffix(string literal, double expected) {
        var lowered = WorldCompiler.Compile(
            cancellationToken: TestContext.Current.CancellationToken,
            source: $"schema: \"s\"\n\ntuning {{\n    gain: {literal}\n}}"
        );

        Assert.False(
            condition: lowered.Diagnostics.HasErrors,
            userMessage: lowered.Diagnostics.FormatReport()
        );

        var gain = Assert.IsType<JsonObject>(@object: lowered.RequireJson()["tuning"])["gain"];

        Assert.Equal(
            expected,
            gain.AsNumber()!.Value,
            9
        );
    }
    [Fact]
    public void NullValuedCallFormArgumentSurvivesTheRoundTrip() {
        // A call-form argument can be explicitly authored as JSON null (distinct from the argument being absent
        // entirely); the printer must reprint it rather than silently dropping the argument.
        const string Json = """
            {"schema":"puck.world.definition.v1","views":{"seatRig":{"operations":[
              {"$type":"lookAt","focusDistance":6,"targetOffset":null,"worldAxes":true}
            ]}}}
            """;

        var puck = WorldDecompiler.Decompile(jsonText: Json);

        Assert.Contains(
            actualString: puck,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "targetOffset: null"
        );
        AssertRoundTripsExactly(json: Json);
    }
    [Fact]
    public void OptionGateAndDecisionInterruptFallBackWhenTheGateGrammarCannotCarryThem() {
        // A compareValue whose `left` opens with '(' is read by the gate grammar as a parenthesized sub-gate, and a
        // single-child `all` has no `and`/`or` spelling at all.
        const string Json = """
            {"schema":"puck.world.definition.v1","rules":[
              {"name":"r","forEach":"seat","effects":[],
               "decision":{"periodSeconds":1,
                 "interrupt":{"$type":"all","predicates":[{"$type":"compareState","state":"a","comparison":"Equal","value":1}]},
                 "options":[{"name":"o1","score":"1",
                   "gate":{"$type":"compareValue","comparison":"Equal","kind":"Int","left":"(a | b) & c","right":"1"},
                   "effects":[{"$type":"setState","state":"hp","value":0}]}]}}
            ]}
            """;

        AssertRoundTripsExactly(json: Json);
    }
    [Fact]
    public void ScientificNotationSurvivesTheRoundTrip() {
        const string Json = """
            {"schema":"puck.world.definition.v1","palette":{"b":[0.01452,-0.0030928,8.57e-05]}}
            """;

        AssertRoundTripsExactly(json: Json);
    }
}
