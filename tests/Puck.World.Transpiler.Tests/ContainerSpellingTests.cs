using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Formatting;
using Puck.Transpiler.Parsing;
using Puck.World.Transpiler.Lowering;
using Xunit;

namespace Puck.World.Transpiler.Tests;

// A container value is written as a block, a scalar takes a colon. One spelling per shape, at every depth.
public class ContainerSpellingTests {
    private static DiagnosticBag ParseForDiagnostics(string source) {
        var diagnostics = new DiagnosticBag();

        PuckParser.ParseDocumentWithDiagnostics(source: source, diagnostics: diagnostics);

        return diagnostics;
    }

    private static void AssertRefusesColon(string source, string spelling) {
        Assert.Contains(ParseForDiagnostics(source), diagnostic =>
            (diagnostic.Code == PuckDiagnosticCodes.ColonBeforeContainer) && diagnostic.Message.Contains(spelling));
    }

    [Fact]
    public void TestBlockAndArraySectionsCarryNoColon() {
        var document = PuckParser.ParseDocument("""
            schema: "puck.world.def.v1"

            host {
                width: 1280
            }

            cameras [
                { name: "a" }
            ]
            """);

        Assert.IsType<BlockNode>(document.Statements[0]);

        var cameras = Assert.IsType<PropertyNode>(document.Statements[1]);

        Assert.Equal("cameras", cameras.Name);
        Assert.IsType<ArrayExpressionNode>(cameras.Value);
    }

    [Fact]
    public void TestAColonBeforeAContainerIsRefusedAtEveryDepth() {
        AssertRefusesColon("""
            schema: "puck.world.def.v1"

            host: {
                width: 1280
            }
            """, "host:");

        AssertRefusesColon("""
            schema: "puck.world.def.v1"

            cameras: [
                { name: "a" }
            ]
            """, "cameras:");

        AssertRefusesColon("""
            schema: "puck.world.def.v1"

            collision {
                requirements: [
                    "SmoothUnionContact"
                ]
            }
            """, "requirements:");
    }

    [Fact]
    public void TestAnObjectLiteralFollowsTheSameRule() {
        AssertRefusesColon("""
            schema: "puck.world.def.v1"

            screens [
                { index: 0, origin: [0, 1, 0] }
            ]
            """, "origin:");
    }

    [Fact]
    public void TestRuleDecisionAndOptionBodiesFollowTheSameRule() {
        AssertRefusesColon("""
            schema: "puck.world.def.v1"

            rule "zoned" {
                when score[0] > 0
                zones: ["a"]
                score[0] += 1
            }
            """, "'zones: [' - a array is written without the ':': use 'zones ['");

        AssertRefusesColon("""
            schema: "puck.world.def.v1"

            rule "chooser" {
                decision {
                    periodSeconds: 1s
                    tieBreak: { seed: 0 }
                    option "o" {
                        score: 1
                    }
                }
            }
            """, "'tieBreak: {' - a block is written without the ':': use 'tieBreak {'");

        AssertRefusesColon("""
            schema: "puck.world.def.v1"

            rule "chooser" {
                decision {
                    periodSeconds: 1s
                    option "o" {
                        score: 1
                        neighbors: { range: 10 }
                    }
                }
            }
            """, "'neighbors: {' - a block is written without the ':': use 'neighbors {'");
    }

    [Fact]
    public void TestRuleBodyContainersWithoutAColonAndScalarsWithOneParseClean() {
        var diagnostics = ParseForDiagnostics("""
            schema: "puck.world.def.v1"

            rule "zoned" {
                when score[0] > 0
                mode: Edge
                zones ["a"]
                score[0] += 1
            }

            rule "chooser" {
                decision {
                    periodSeconds: 1s
                    option "o" {
                        score: 1
                        neighbors { range: 10 }
                    }
                }
            }
            """);

        Assert.DoesNotContain(diagnostics, diagnostic => (diagnostic.Code == PuckDiagnosticCodes.ColonBeforeContainer));
    }

    [Fact]
    public void TestACallArgumentKeepsItsColon() {
        var diagnostics = ParseForDiagnostics("""
            schema: "puck.world.def.v1"

            cameras [
                {
                    rig {
                        operations [
                            anchor(subject: worldPoint(point: [0, 1, 0]))
                        ]
                    }
                }
            ]
            """);

        // A named argument is call syntax, not a statement or an object-literal field.
        Assert.DoesNotContain(diagnostics, diagnostic => (diagnostic.Code == PuckDiagnosticCodes.ColonBeforeContainer));
    }

    [Fact]
    public void TestANameStandingForAContainerKeepsItsColon() {
        var diagnostics = ParseForDiagnostics("""
            schema: "puck.world.def.v1"

            let bounds = [0, 1, 0]

            screens [
                { origin: bounds }
            ]
            """);

        // The rule is about the punctuation in front of a literal container, never about what the value turns out
        // to be: only '{' and '[' are refused after a colon.
        Assert.DoesNotContain(diagnostics, diagnostic => (diagnostic.Code == PuckDiagnosticCodes.ColonBeforeContainer));
    }

    [Fact]
    public void TestABareFlagIsNotSwallowedByTheBlockStatementBelowIt() {
        var document = PuckParser.ParseDocument("""
            schema: "puck.world.def.v1"

            placements {
                placement "debugRoom" {
                    solid
                    grip { holdable: true }
                }
            }
            """);
        var placements = Assert.IsType<BlockNode>(document.Statements[0]);
        var placement = Assert.IsType<BlockNode>(placements.Statements[0]);

        // `identifier name { }` spans newlines, so without the flag test running first these two statements read
        // as one `solid grip { }` header.
        Assert.Equal("solid", Assert.IsType<FlagStatementNode>(placement.Statements[0]).Name);
        Assert.Equal("grip", Assert.IsType<BlockNode>(placement.Statements[1]).Identifier);
    }

    [Fact]
    public void TestANestedFieldBlockLowersLikeItsPropertySpelling() {
        var lowered = WorldDocumentEmitter.Lower(PuckParser.ParseDocument("""
            schema: "puck.world.def.v1"

            placements {
                policy {
                    candidateCap: 16
                }
            }
            """));
        var placements = Assert.IsType<System.Text.Json.Nodes.JsonObject>(lowered["placements"]);
        var policy = Assert.IsType<System.Text.Json.Nodes.JsonObject>(placements["policy"]);

        Assert.Equal(16L, policy["candidateCap"]?.GetValue<long>());
    }

    [Fact]
    public void TestFormattingNeverReintroducesTheColon() {
        var formatted = PuckFormatter.Format("""
            schema: "puck.world.def.v1"
            host:
            {
            width: 1280
            }
            cells:
            [
            1
            ]
            """);

        Assert.Contains("host {", formatted);
        Assert.Contains("cells [", formatted);
        Assert.DoesNotContain("host: {", formatted);
        Assert.DoesNotContain("cells: [", formatted);
    }
}
