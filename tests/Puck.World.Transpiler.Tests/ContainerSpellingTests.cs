using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Parsing;
using Xunit;

namespace Puck.World.Transpiler.Tests;

// A container value is written as a block, a scalar takes a colon. One spelling per shape, at every depth.
public class ContainerSpellingTests {
    private static void AssertRefusesColon(string source, string spelling) {
        Assert.Contains(
            collection: ParseForDiagnostics(source: source),
            filter: diagnostic =>
            ((diagnostic.Code == PuckDiagnosticCodes.ColonBeforeContainer) && diagnostic.Message.Contains(value: spelling))
        );
    }
    private static DiagnosticBag ParseForDiagnostics(string source) {
        var diagnostics = new DiagnosticBag();

        PuckParser.ParseDocumentWithDiagnostics(
            source: source,
            diagnostics: diagnostics
        );

        return diagnostics;
    }

    [Fact]
    public void TestABareFlagIsNotSwallowedByTheBlockStatementBelowIt() {
        var document = PuckParser.ParseDocument("""
            schema: "puck.world.definition.v1"

            placements {
                placement "debugRoom" {
                    solid
                    grip { holdable: true }
                }
            }
            """);
        var placements = Assert.IsType<BlockNode>(@object: document.Statements[0]);
        var placement = Assert.IsType<BlockNode>(@object: placements.Statements[0]);

        // `identifier name { }` spans newlines, so without the flag test running first these two statements read
        // as one `solid grip { }` header.
        Assert.Equal(
            "solid",
            Assert.IsType<FlagStatementNode>(@object: placement.Statements[0]).Name
        );
        Assert.Equal(
            "grip",
            Assert.IsType<BlockNode>(@object: placement.Statements[1]).Identifier
        );
    }
    [Fact]
    public void TestACallArgumentKeepsItsColon() {
        var diagnostics = ParseForDiagnostics(source: """
            schema: "puck.world.definition.v1"

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
        Assert.DoesNotContain(
            collection: diagnostics,
            filter: diagnostic => (diagnostic.Code == PuckDiagnosticCodes.ColonBeforeContainer)
        );
    }
    [Fact]
    public void TestAColonBeforeAContainerIsRefusedAtEveryDepth() {
        AssertRefusesColon(
            source: """
            schema: "puck.world.definition.v1"

            host: {
                width: 1280
            }
            """,
            spelling: "host:"
        );

        AssertRefusesColon(
            source: """
            schema: "puck.world.definition.v1"

            cameras: [
                { name: "a" }
            ]
            """,
            spelling: "cameras:"
        );

        AssertRefusesColon(
            source: """
            schema: "puck.world.definition.v1"

            collision {
                requirements: [
                    "SmoothUnionContact"
                ]
            }
            """,
            spelling: "requirements:"
        );
    }
    [Fact]
    public void TestANameStandingForAContainerKeepsItsColon() {
        var diagnostics = ParseForDiagnostics(source: """
            schema: "puck.world.definition.v1"

            let bounds = [0, 1, 0]

            screens [
                { origin: bounds }
            ]
            """);

        // The rule is about the punctuation in front of a literal container, never about what the value turns out
        // to be: only '{' and '[' are refused after a colon.
        Assert.DoesNotContain(
            collection: diagnostics,
            filter: diagnostic => (diagnostic.Code == PuckDiagnosticCodes.ColonBeforeContainer)
        );
    }
    [Fact]
    public void TestANestedFieldBlockLowersLikeItsPropertySpelling() {
        var lowered = WorldCompiler.Compile(
            cancellationToken: TestContext.Current.CancellationToken,
            source: """
            schema: "puck.world.definition.v1"

            placements {
                policy {
                    candidateCap: 16
                }
            }
            """).RequireJson();
        var placements = Assert.IsType<System.Text.Json.Nodes.JsonObject>(@object: lowered["placements"]);
        var policy = Assert.IsType<System.Text.Json.Nodes.JsonObject>(@object: placements["policy"]);

        Assert.Equal(
            16L,
            policy["candidateCap"]?.GetValue<long>()
        );
    }
    [Fact]
    public void TestAnObjectLiteralFollowsTheSameRule() {
        AssertRefusesColon(
            source: """
            schema: "puck.world.definition.v1"

            screens [
                { index: 0, origin: [0, 1, 0] }
            ]
            """,
            spelling: "origin:"
        );
    }
    [Fact]
    public void TestBlockAndArraySectionsCarryNoColon() {
        var document = PuckParser.ParseDocument("""
            schema: "puck.world.definition.v1"

            host {
                width: 1280
            }

            cameras [
                { name: "a" }
            ]
            """);

        Assert.IsType<BlockNode>(@object: document.Statements[0]);

        var cameras = Assert.IsType<PropertyNode>(@object: document.Statements[1]);

        Assert.Equal(
            "cameras",
            cameras.Name
        );
        Assert.IsType<ArrayExpressionNode>(@object: cameras.Value);
    }
    [Fact]
    public void TestFormattingNeverReintroducesTheColon() {
        var formatted = PuckFormat.Format("""
            schema: "puck.world.definition.v1"
            host:
            {
            width: 1280
            }
            cells:
            [
            1
            ]
            """);

        Assert.Contains(
            actualString: formatted,
            expectedSubstring: "host {"
        );
        Assert.Contains(
            actualString: formatted,
            expectedSubstring: "cells ["
        );
        Assert.DoesNotContain(
            actualString: formatted,
            expectedSubstring: "host: {"
        );
        Assert.DoesNotContain(
            actualString: formatted,
            expectedSubstring: "cells: ["
        );
    }
    [Fact]
    public void TestRuleBodyContainersWithoutAColonAndScalarsWithOneParseClean() {
        var diagnostics = ParseForDiagnostics(source: """
            schema: "puck.world.definition.v1"

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

        Assert.DoesNotContain(
            collection: diagnostics,
            filter: diagnostic => (diagnostic.Code == PuckDiagnosticCodes.ColonBeforeContainer)
        );
    }
    [Fact]
    public void TestRuleDecisionAndOptionBodiesFollowTheSameRule() {
        AssertRefusesColon(
            source: """
            schema: "puck.world.definition.v1"

            rule "zoned" {
                when score[0] > 0
                zones: ["a"]
                score[0] += 1
            }
            """,
            spelling: "'zones: [' - a array is written without the ':': use 'zones ['"
        );

        AssertRefusesColon(
            source: """
            schema: "puck.world.definition.v1"

            rule "chooser" {
                decision {
                    periodSeconds: 1s
                    tieBreak: { seed: 0 }
                    option "o" {
                        score: 1
                    }
                }
            }
            """,
            spelling: "'tieBreak: {' - a block is written without the ':': use 'tieBreak {'"
        );

        AssertRefusesColon(
            source: """
            schema: "puck.world.definition.v1"

            rule "chooser" {
                decision {
                    periodSeconds: 1s
                    option "o" {
                        score: 1
                        neighbors: { range: 10 }
                    }
                }
            }
            """,
            spelling: "'neighbors: {' - a block is written without the ':': use 'neighbors {'"
        );
    }
}
