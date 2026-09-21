using System.Text.Json.Nodes;
using Puck.Transpiler.Diagnostics;
using Xunit;

namespace Puck.World.Transpiler.Tests;

// Where generation is allowed to stand. A `for` and a template invocation are flattened to the statements they
// produce before a section's own dispatcher reads them, so a generated row sits wherever a written one may, and a
// gate operand resolves the `let` bindings in scope rather than reading every bare name as a state row.
public class GenerationPositionTests {
    private static WorldCompilation Compile(string body) =>
        WorldCompiler.Compile(
            cancellationToken: TestContext.Current.CancellationToken,
            source: $"schema: \"puck.world.definition.v1\"\n\n{body}"
        );
    private static JsonObject Lower(string body) {
        var compilation = Compile(body: body);

        Assert.False(
            condition: compilation.Diagnostics.HasErrors,
            userMessage: string.Join(
                separator: "\n",
                values: compilation.Diagnostics.Select(selector: d => $"{d.Code}: {d.Message}")
            )
        );

        return compilation.RequireJson();
    }
    private static DiagnosticBag LowerForDiagnostics(string body) => Compile(body: body).Diagnostics;

    [Fact]
    public void TestAForMayInvokeATemplateAndKeepDocumentOrder() {
        var rows = Assert.IsType<JsonArray>(@object: Assert.IsType<JsonObject>(@object: Lower(body: """
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
            rows.Select(selector: row => Assert.IsType<JsonObject>(@object: row)["id"]!.GetValue<string>()).ToArray()
        );
    }
    [Fact]
    public void TestAForRefusesToAssignAFieldAndNamesMapInstead() {
        // `for` emits rows; building a value out of a sequence is `map`'s job. Both readings this refuses used to be
        // silent: a scalar kept the last iteration, and an array was spliced element-wise into whatever the field
        // already held — flattening it and corrupting an array the document authored above the loop.
        Assert.Contains(
            collection: LowerForDiagnostics(body: """
                let points = [1, 2, 3]

                for p in points {
                    scalars: p
                }
                """),
            filter: diagnostic => (diagnostic.Code == PuckDiagnosticCodes.ForAssignsAField)
        );

        Assert.Contains(
            collection: LowerForDiagnostics(body: """
                let points = [1, 2, 3]

                for p in points {
                    curve [p, 0]
                }
                """),
            filter: diagnostic => (diagnostic.Code == PuckDiagnosticCodes.ForAssignsAField)
        );
    }
    [Fact]
    public void TestAGateOperandReadsALetBindingRatherThanAStateRowOfThatName() {
        var rules = Assert.IsType<JsonArray>(@object: Lower(body: """
            let wellFloor = 17

            rule "settle" {
                when row == wellFloor
                mode: Level
                done = 1
            }
            """)["rules"]);

        var gate = Assert.IsType<JsonObject>(@object: Assert.IsType<JsonObject>(@object: rules[0])["gate"]);

        Assert.Equal(
            "compareState",
            gate["$type"]?.GetValue<string>()
        );
        Assert.Equal(
            "row",
            gate["state"]?.GetValue<string>()
        );
        // The bound value, not a second state read named `wellFloor`.
        Assert.Equal(
            17m,
            gate["value"]?.GetValue<decimal>()
        );
        Assert.Null(@object: gate["comparandState"]);
    }
    [Fact]
    public void TestATemplateStandsInPlacementPosition() {
        var rows = Assert.IsType<JsonArray>(@object: Assert.IsType<JsonObject>(@object: Lower(body: """
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

        Assert.Equal(
            2,
            rows.Count
        );
        Assert.Equal(
            "north",
            Assert.IsType<JsonObject>(@object: rows[0])["id"]?.GetValue<string>()
        );
        Assert.Equal(
            "south",
            Assert.IsType<JsonObject>(@object: rows[1])["id"]?.GetValue<string>()
        );
    }
    [Fact]
    public void TestATemplateThatInvokesItselfIsRefusedRatherThanExpandingForever() {
        Assert.Contains(
            collection: LowerForDiagnostics(body: """
                template loop(n) {
                    loop(n: n)
                }

                placements {
                    loop(n: 1)
                }
                """),
            filter: diagnostic => (diagnostic.Code == PuckDiagnosticCodes.TemplateExpansionTooDeep)
        );
    }
    [Fact]
    public void TestMapIsHowASequenceBecomesAValue() {
        // The other half of the same rule: the refusal above is only honest because `map` covers the case.
        var lowered = Lower(body: """
            let points = [1, 2, 3]

            curve: map(points, p => [p, 0])
            """);

        Assert.Equal(
            "[[1,0],[2,0],[3,0]]",
            Assert.IsType<JsonArray>(@object: lowered["curve"]).ToJsonString()
        );
    }
    [Fact]
    public void TestARuleNameSubstitutesATemplateParameterAndInterpolates() {
        var rules = Assert.IsType<JsonArray>(@object: Lower(body: """
            template flipper(n) {
                rule n {
                    phase = 2
                }
            }

            flipper(n: "flip")
            flipper(n: "flop")

            rule $"gen-{1 + 1}" {
                phase = 3
            }
            """)["rules"]);

        Assert.Equal(
            ["flip", "flop", "gen-2"],
            rules.Select(selector: rule => Assert.IsType<JsonObject>(@object: rule)["name"]!.GetValue<string>()).ToArray()
        );
    }
    [Fact]
    public void TestARuleNamedAfterAnUnsubstitutedTemplateParameterReportsPuck100() {
        // Substitution reaches a template's own top-level statements, so a rule nested inside a `rules` scope keeps
        // the parameter's spelling and every instantiation would mint the same name.
        Assert.Contains(
            collection: LowerForDiagnostics(body: """
                template flipper(n) {
                    rules group {
                        rule n {
                            phase = 2
                        }
                    }
                }

                flipper(n: "flip")
                """),
            filter: diagnostic => (diagnostic.Code == PuckDiagnosticCodes.RuleNameNotLiteral)
        );
    }
}
