using System.Text.Json.Nodes;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Parsing;
using Puck.World.Transpiler.Lowering;
using Xunit;

namespace Puck.World.Transpiler.Tests;

// Where generation is allowed to stand. A `for` and a template invocation are flattened to the statements they
// produce before a section's own dispatcher reads them, so a generated row sits wherever a written one may, and a
// gate operand resolves the `let` bindings in scope rather than reading every bare name as a state row.
public class GenerationPositionTests {
    private static JsonObject Lower(string body) {
        var diagnostics = new DiagnosticBag();
        var lowered = WorldDocumentEmitter.LowerWithDiagnostics(
            PuckParser.ParseDocument($"schema: \"puck.world.def.v1\"\n\n{body}"),
            diagnostics: diagnostics, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics.Select(d => $"{d.Code}: {d.Message}")));

        return lowered.Value!;
    }

    private static DiagnosticBag LowerForDiagnostics(string body) {
        var diagnostics = new DiagnosticBag();

        WorldDocumentEmitter.LowerWithDiagnostics(
            PuckParser.ParseDocument($"schema: \"puck.world.def.v1\"\n\n{body}"),
            diagnostics: diagnostics, cancellationToken: TestContext.Current.CancellationToken);

        return diagnostics;
    }

    [Fact]
    public void TestATemplateStandsInPlacementPosition() {
        var rows = Assert.IsType<JsonArray>(Assert.IsType<JsonObject>(Lower("""
            template marker(id, at) {
                placement id {
                    prototype: "post"
                    position: at
                }
            }

            placements {
                marker(id: "north", at: [0, 0, 5])
                marker(id: "south", at: [0, 0, -5])
            }
            """)["placements"])["rows"]);

        Assert.Equal(2, rows.Count);
        Assert.Equal("north", Assert.IsType<JsonObject>(rows[0])["id"]?.GetValue<string>());
        Assert.Equal("south", Assert.IsType<JsonObject>(rows[1])["id"]?.GetValue<string>());
    }

    [Fact]
    public void TestAForMayInvokeATemplateAndKeepDocumentOrder() {
        var rows = Assert.IsType<JsonArray>(Assert.IsType<JsonObject>(Lower("""
            template marker(id, at) {
                placement id {
                    prototype: "post"
                    position: at
                }
            }

            placements {
                placement "first" {
                    prototype: "post"
                }

                for i in range(0, 3) {
                    marker(id: $"gen-{i}", at: [i, 0, 0])
                }
            }
            """)["placements"])["rows"]);

        Assert.Equal(
            ["first", "gen-0", "gen-1", "gen-2"],
            rows.Select(row => Assert.IsType<JsonObject>(row)["id"]!.GetValue<string>()).ToArray());
    }

    [Fact]
    public void TestATemplateThatInvokesItselfIsRefusedRatherThanExpandingForever() {
        Assert.Contains(
            LowerForDiagnostics("""
                template loop(n) {
                    loop(n: n)
                }

                placements {
                    loop(n: 1)
                }
                """),
            diagnostic => diagnostic.Code == PuckDiagnosticCodes.TemplateExpansionTooDeep);
    }

    [Fact]
    public void TestAForRefusesToAssignAFieldAndNamesMapInstead() {
        // `for` emits rows; building a value out of a sequence is `map`'s job. Both readings this refuses used to be
        // silent: a scalar kept the last iteration, and an array was spliced element-wise into whatever the field
        // already held — flattening it and corrupting an array the document authored above the loop.
        Assert.Contains(
            LowerForDiagnostics("""
                let points = [1, 2, 3]

                for p in points {
                    scalars: p
                }
                """),
            diagnostic => diagnostic.Code == PuckDiagnosticCodes.ForAssignsAField);

        Assert.Contains(
            LowerForDiagnostics("""
                let points = [1, 2, 3]

                for p in points {
                    curve [p, 0]
                }
                """),
            diagnostic => diagnostic.Code == PuckDiagnosticCodes.ForAssignsAField);
    }

    [Fact]
    public void TestMapIsHowASequenceBecomesAValue() {
        // The other half of the same rule: the refusal above is only honest because `map` covers the case.
        var lowered = Lower("""
            let points = [1, 2, 3]

            curve: map(points, p => [p, 0])
            """);

        Assert.Equal(
            "[[1,0],[2,0],[3,0]]",
            Assert.IsType<JsonArray>(lowered["curve"]).ToJsonString());
    }

    [Fact]
    public void TestAGateOperandReadsALetBindingRatherThanAStateRowOfThatName() {
        var rules = Assert.IsType<JsonArray>(Lower("""
            let wellFloor = 17

            rule "settle" {
                when row == wellFloor
                mode: "Level"
                done = 1
            }
            """)["rules"]);

        var gate = Assert.IsType<JsonObject>(Assert.IsType<JsonObject>(rules[0])["gate"]);

        Assert.Equal("compareState", gate["$type"]?.GetValue<string>());
        Assert.Equal("row", gate["state"]?.GetValue<string>());
        // The bound value, not a second state read named `wellFloor`.
        Assert.Equal(17m, gate["value"]?.GetValue<decimal>());
        Assert.Null(gate["comparandState"]);
    }
}
