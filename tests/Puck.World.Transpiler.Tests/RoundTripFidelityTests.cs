using System.Text.Json.Nodes;
using Puck.World.Transpiler.Decompiler;
using Puck.Transpiler.Diagnostics;
using Puck.World.Transpiler.Lowering;
using Puck.Transpiler.Parsing;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>Decompile-then-compile fidelity for document shapes the row sugar cannot carry: basis-merge directive
/// rows, basis-partial rows, off-canonical enum spellings, contradictory predicate fields, and numbers a canonical
/// document spells in scientific notation. Each one must survive the round trip unchanged, through whatever
/// spelling carries it.</summary>
public class RoundTripFidelityTests {
    private static JsonObject RoundTrip(string json) {
        var puck = WorldDecompiler.Decompile(json);

        var parseDiagnostics = new DiagnosticBag();
        var parseResult = PuckParser.ParseDocumentWithDiagnostics(puck, diagnostics: parseDiagnostics);
        Assert.False(parseDiagnostics.HasErrors, $"{parseDiagnostics.FormatReport(puck)}{Environment.NewLine}{puck}");

        var loweringDiagnostics = new DiagnosticBag();
        var lowered = WorldDocumentEmitter.LowerWithDiagnostics(parseResult.Value!, diagnostics: loweringDiagnostics, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(loweringDiagnostics.HasErrors, $"{loweringDiagnostics.FormatReport(puck)}{Environment.NewLine}{puck}");

        return lowered.Value!;
    }

    private static void AssertRoundTripsExactly(string json) {
        var original = JsonNode.Parse(json);
        var recompiled = RoundTrip(json);
        var mismatch = JsonMismatch.Find(original, recompiled, "");
        Assert.Null(mismatch);
    }

    [Fact]
    public void BasisReplaceDirectiveRowInRulesSurvivesTheRoundTrip() {
        const string json = """
            {"schema":"puck.world.def.v1","rules":[
              {"$replace":true},
              {"name":"toggle","effects":[{"$type":"setState","state":"sky","key":"on","value":1}]}
            ]}
            """;

        var puck = WorldDecompiler.Decompile(json);
        Assert.DoesNotContain("rule \"\"", puck, StringComparison.Ordinal);
        AssertRoundTripsExactly(json);
    }

    [Fact]
    public void BasisReplaceDirectiveRowInPlacementsSurvivesTheRoundTrip() {
        const string json = """
            {"schema":"puck.world.def.v1","placements":{"rows":[
              {"$replace":true},
              {"id":"a","prototypeId":"p","position":[0,0,0],"scale":1,"yawDegrees":0}
            ]}}
            """;

        AssertRoundTripsExactly(json);
    }

    [Fact]
    public void BasisPartialPlacementRowKeepsExactlyTheKeysItAuthored() {
        // A row without its own prototypeId is completed by the basis chain; filling yawDegrees/scale here would
        // override the value composition was about to supply.
        const string json = """
            {"schema":"puck.world.def.v1","basis":"b.json","placements":{"rows":[
              {"id":"tabletop","parent":"marketCourt","position":[-8,-0.5,6],"yawDegrees":0}
            ]}}
            """;

        var recompiled = RoundTrip(json);
        var row = Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(Assert.IsType<JsonObject>(recompiled["placements"])["rows"])[0]);
        Assert.False(row.ContainsKey("scale"));
        Assert.False(row.ContainsKey("prototypeId"));
        AssertRoundTripsExactly(json);
    }

    [Fact]
    public void ScientificNotationSurvivesTheRoundTrip() {
        const string json = """
            {"schema":"puck.world.def.v1","palette":{"b":[0.01452,-0.0030928,8.57e-05]}}
            """;

        AssertRoundTripsExactly(json);
    }

    [Theory]
    [InlineData("1.5e3", 1500.0)]
    [InlineData("2E-2", 0.02)]
    [InlineData("-4.25e+2", -425.0)]
    public void ExponentIsPartOfTheNumberNotAUnitSuffix(string literal, double expected) {
        var diagnostics = new DiagnosticBag();
        var parseResult = PuckParser.ParseDocumentWithDiagnostics($"schema: \"s\"\n\ntuning {{\n    gain: {literal}\n}}", diagnostics: diagnostics);
        Assert.False(diagnostics.HasErrors, diagnostics.FormatReport());

        var loweringDiagnostics = new DiagnosticBag();
        var lowered = WorldDocumentEmitter.LowerWithDiagnostics(parseResult.Value!, diagnostics: loweringDiagnostics, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(loweringDiagnostics.HasErrors, loweringDiagnostics.FormatReport());

        var gain = Assert.IsType<JsonObject>(lowered.Value!["tuning"])["gain"];
        Assert.Equal(expected, gain!.GetValue<double>(), 9);
    }

    [Fact]
    public void ComparisonSpelledOffCanonicalStaysCallFormRatherThanChangingOperator() {
        // The engine's enum converter accepts any casing; the sugar can only reprint the canonical spelling, so a
        // document that spelled it otherwise must not be folded onto a different operator.
        const string json = """
            {"schema":"puck.world.def.v1","rules":[
              {"name":"r","gate":{"$type":"compareState","state":"hp","comparison":"greaterOrEqual","value":5},
               "effects":[{"$type":"setState","state":"hp","value":0}]}
            ]}
            """;

        var puck = WorldDecompiler.Decompile(json);
        Assert.DoesNotContain("when hp == 5", puck, StringComparison.Ordinal);
        AssertRoundTripsExactly(json);
    }

    [Fact]
    public void CompareValueKindSpelledOffCanonicalKeepsItsKind() {
        const string json = """
            {"schema":"puck.world.def.v1","rules":[
              {"name":"r","gate":{"$type":"compareValue","comparison":"NotEqual","kind":"int","left":"a[from]","right":"a[to]"},
               "effects":[{"$type":"setState","state":"hp","value":0}]}
            ]}
            """;

        AssertRoundTripsExactly(json);
    }

    [Fact]
    public void CompareStateCarryingBothValueAndComparandLosesNeither() {
        const string json = """
            {"schema":"puck.world.def.v1","rules":[
              {"name":"r","gate":{"$type":"compareState","state":"a","key":"k","comparison":"Equal","value":1,"comparandState":"b","comparandKey":"j"},
               "effects":[{"$type":"setState","state":"hp","value":0}]}
            ]}
            """;

        AssertRoundTripsExactly(json);
    }

    [Fact]
    public void BindingWithoutAKindIsNotGivenOne() {
        // RuleBinding.Kind has no default; inventing one would author a binding the source never asked for.
        const string json = """
            {"schema":"puck.world.def.v1","rules":[
              {"name":"r","bindings":[{"name":"x","expression":"$each"}],
               "effects":[{"$type":"setState","state":"hp","value":0}]}
            ]}
            """;

        var puck = WorldDecompiler.Decompile(json);
        Assert.DoesNotContain("bind x : Int", puck, StringComparison.Ordinal);
        AssertRoundTripsExactly(json);
    }

    [Fact]
    public void OptionGateAndDecisionInterruptFallBackWhenTheGateGrammarCannotCarryThem() {
        // A compareValue whose `left` opens with '(' is read by the gate grammar as a parenthesized sub-gate, and a
        // single-child `all` has no `and`/`or` spelling at all.
        const string json = """
            {"schema":"puck.world.def.v1","rules":[
              {"name":"r","forEach":"seat","effects":[],
               "decision":{"periodSeconds":1,
                 "interrupt":{"$type":"all","predicates":[{"$type":"compareState","state":"a","comparison":"Equal","value":1}]},
                 "options":[{"name":"o1","score":"1",
                   "gate":{"$type":"compareValue","comparison":"Equal","kind":"Int","left":"(a | b) & c","right":"1"},
                   "effects":[{"$type":"setState","state":"hp","value":0}]}]}}
            ]}
            """;

        AssertRoundTripsExactly(json);
    }

    [Fact]
    public void EmptyExportFacetArraySurvivesTheRoundTrip() {
        // An exported facet with zero names is a present-but-empty array, not an absent facet; losing it would
        // silently drop the export from the composed document.
        const string json = """
            {"schema":"puck.world.def.v1","exports":{"actions":[]}}
            """;

        var puck = WorldDecompiler.Decompile(json);
        Assert.Contains("export action", puck, StringComparison.Ordinal);
        AssertRoundTripsExactly(json);
    }

    [Fact]
    public void NullValuedCallFormArgumentSurvivesTheRoundTrip() {
        // A call-form argument can be explicitly authored as JSON null (distinct from the argument being absent
        // entirely); the printer must reprint it rather than silently dropping the argument.
        const string json = """
            {"schema":"puck.world.def.v1","views":{"seatRig":{"operations":[
              {"$type":"lookAt","focusDistance":6,"targetOffset":null,"worldAxes":true}
            ]}}}
            """;

        var puck = WorldDecompiler.Decompile(json);
        Assert.Contains("targetOffset: null", puck, StringComparison.Ordinal);
        AssertRoundTripsExactly(json);
    }

    [Fact]
    public void DegreesNativeFieldsAllPrintTheirUnitNotJustYawDegrees() {
        const string json = """
            {"schema":"puck.world.def.v1","placements":{"rows":[
              {"id":"a","prototypeId":"p","position":[0,0,0],"scale":1,"yawDegrees":30,"outwardYawDegrees":15}
            ]}}
            """;

        var puck = WorldDecompiler.Decompile(json);
        Assert.Contains("outwardYawDegrees: 15deg", puck, StringComparison.Ordinal);
        AssertRoundTripsExactly(json);
    }
}
