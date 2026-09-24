using System.Text.Json.Nodes;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>Decompile-then-compile fidelity for document shapes the row sugar cannot carry: basis-merge directive
/// rows, basis-partial rows, off-canonical enum spellings, contradictory predicate fields, and numbers a canonical
/// document spells in scientific notation. Each one must survive the round trip unchanged, through whatever
/// spelling carries it.</summary>
public class RoundTripFidelityTests {
    /// <summary>A document, spelled with infix expressions as an author writes it, and what its print must and must
    /// not say.</summary>
    private sealed record Fidelity(string Json, string[] Printed, string[] NotPrinted);

    private static readonly Dictionary<string, Fidelity> Cases = new(comparer: StringComparer.Ordinal) {
        // A row without its own prototypeId is completed by the basis chain; filling yawDegrees/scale here would
        // override the value composition was about to supply.
        ["a basis-partial placement row keeps exactly the keys it authored"] = new(
            Json: """
                {"schema":"puck.world.definition.v1","basis":"b.json","placements":{"rows":[
                  {"id":"tabletop","parent":"marketCourt","position":[-8,-0.5,6],"yawDegrees":0}
                ]}}
                """,
            NotPrinted: [],
            Printed: []
        ),
        ["a $replace directive row in placements"] = new(
            Json: """
                {"schema":"puck.world.definition.v1","placements":{"rows":[
                  {"$replace":true},
                  {"id":"a","prototypeId":"p","position":[0,0,0],"scale":1,"yawDegrees":0}
                ]}}
                """,
            NotPrinted: [],
            Printed: []
        ),
        ["a $replace directive row in rules is not a rule"] = new(
            Json: """
                {"schema":"puck.world.definition.v1","rules":[
                  {"$replace":true},
                  {"name":"toggle","effects":[{"$type":"setState","state":"sky","key":"on","value":1}]}
                ]}
                """,
            NotPrinted: ["rule \"\""],
            Printed: []
        ),
        // RuleLocal.Kind has no default; inventing one would author a local the source never asked for.
        ["a binding without a kind is not given one"] = new(
            Json: """
                {"schema":"puck.world.definition.v1","rules":[
                  {"name":"r","bindings":[{"name":"x","expression":"$each"}],
                   "effects":[{"$type":"setState","state":"hp","value":0}]}
                ]}
                """,
            NotPrinted: ["bind x : Int"],
            Printed: []
        ),
        ["a compareState carrying both a value and a comparand loses neither"] = new(
            Json: """
                {"schema":"puck.world.definition.v1","rules":[
                  {"name":"r","gate":{"$type":"compareState","state":"a","key":"k","comparison":"Equal","value":1,"comparandState":"b","comparandKey":"j"},
                   "effects":[{"$type":"setState","state":"hp","value":0}]}
                ]}
                """,
            NotPrinted: [],
            Printed: []
        ),
        ["a compareValue kind spelled off canonical keeps its kind"] = new(
            Json: """
                {"schema":"puck.world.definition.v1","rules":[
                  {"name":"r","gate":{"$type":"compareValue","comparison":"NotEqual","kind":"int","left":"a[from]","right":"a[to]"},
                   "effects":[{"$type":"setState","state":"hp","value":0}]}
                ]}
                """,
            NotPrinted: [],
            Printed: []
        ),
        // The sugar can only reprint the canonical spelling, so a document that spelled it otherwise must not be
        // folded onto an operator.
        ["a comparison spelled off canonical stays call form"] = new(
            Json: """
                {"schema":"puck.world.definition.v1","rules":[
                  {"name":"r","gate":{"$type":"compareState","state":"hp","comparison":"greaterOrEqual","value":5},
                   "effects":[{"$type":"setState","state":"hp","value":0}]}
                ]}
                """,
            NotPrinted: ["when hp == 5"],
            Printed: []
        ),
        ["every degrees field prints its unit"] = new(
            Json: """
                {"schema":"puck.world.definition.v1","placements":{"rows":[
                  {"id":"a","prototypeId":"p","position":[0,0,0],"scale":1,"yawDegrees":30,"outwardYawDegrees":15}
                ]}}
                """,
            NotPrinted: [],
            Printed: ["outwardYawDegrees: 15deg"]
        ),
        // An exported facet with zero names is a present-but-empty array, not an absent facet; losing it would
        // silently drop the export from the composed document.
        ["an empty export facet"] = new(
            Json: """
                {"schema":"puck.world.definition.v1","exports":{"actions":[]}}
                """,
            NotPrinted: [],
            Printed: ["export action"]
        ),
        // A call-form argument can be explicitly authored as JSON null, distinct from the argument being absent.
        ["a null call-form argument"] = new(
            Json: """
                {"schema":"puck.world.definition.v1","views":{"seatRig":{"operations":[
                  {"$type":"lookAt","focusDistance":6,"targetOffset":null,"worldAxes":true}
                ]}}}
                """,
            NotPrinted: [],
            Printed: ["targetOffset: null"]
        ),
        // A compareValue whose `left` opens with '(' is read by the gate grammar as a parenthesized sub-gate, and a
        // single-child `all` has no `and`/`or` spelling at all.
        ["an option gate and a decision interrupt the gate grammar cannot carry"] = new(
            Json: """
                {"schema":"puck.world.definition.v1","rules":[
                  {"name":"r","forEach":"seat","effects":[],
                   "decision":{"periodSeconds":1,
                     "interrupt":{"$type":"all","predicates":[{"$type":"compareState","state":"a","comparison":"Equal","value":1}]},
                     "options":[{"name":"o1","score":"1",
                       "gate":{"$type":"compareValue","comparison":"Equal","kind":"Int","left":"(a | b) & c","right":"1"},
                       "effects":[{"$type":"setState","state":"hp","value":0}]}]}}
                ]}
                """,
            NotPrinted: [],
            Printed: []
        ),
        ["scientific notation"] = new(
            Json: """
                {"schema":"puck.world.definition.v1","palette":{"b":[0.01452,-0.0030928,8.57e-05]}}
                """,
            NotPrinted: [],
            Printed: []
        ),
    };

    public static TheoryData<string> CaseNames() => new(values: Cases.Keys);
    [MemberData(nameof(CaseNames))]
    [Theory]
    public void ADocumentSurvivesTheRoundTripExactly(string name) {
        var fidelity = Cases[name];
        var printed = WorldSources.AssertRoundTrips(original: WorldSources.Canonical(json: fidelity.Json));
        var missing = fidelity.Printed.Where(predicate: text => !printed.Contains(comparisonType: StringComparison.Ordinal, value: text)).ToArray();
        var present = fidelity.NotPrinted.Where(predicate: text => printed.Contains(comparisonType: StringComparison.Ordinal, value: text)).ToArray();

        Assert.True(
            condition: ((missing.Length == 0) && (present.Length == 0)),
            userMessage: $"{name}: missing [{string.Join(separator: " | ", values: missing)}], present [{string.Join(separator: " | ", values: present)}]{Environment.NewLine}{printed}"
        );
    }
    [InlineData("1.5e3", 1500.0)]
    [InlineData("2E-2", 0.02)]
    [InlineData("-4.25e+2", -425.0)]
    [Theory]
    public void ExponentIsPartOfTheNumberNotAUnitSuffix(string literal, double expected) {
        var lowered = WorldSources.LowerSourceClean(source: $"schema: \"s\"\n\ntuning {{\n    gain: {literal}\n}}");

        Assert.Equal(
            expected,
            Assert.IsType<JsonObject>(@object: lowered["tuning"])["gain"].AsNumber()!.Value,
            9
        );
    }
}
