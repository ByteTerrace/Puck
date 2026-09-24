using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Parsing;
using Xunit;

namespace Puck.World.Transpiler.Tests;

// A container value is written as a block, a scalar takes a colon. One spelling per shape, at every depth.
public class ContainerSpellingTests {
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

    // Sources whose colons are all legal: only a '{' or '[' literal after a colon is refused.
    private static readonly Dictionary<string, string> KeptColons = new(comparer: StringComparer.Ordinal) {
        // A named argument is call syntax, not a statement or an object-literal field.
        ["a call argument keeps its colon"] = """
            cameras [
                {
                    rig {
                        operations [
                            anchor(subject: worldPoint(point: [0, 1, 0]))
                        ]
                    }
                }
            ]
            """,
        // The rule is about the punctuation in front of a literal container, never about what the value turns out
        // to be.
        ["a name standing for a container keeps its colon"] = """
            let bounds = [0, 1, 0]

            screens [
                { origin: bounds }
            ]
            """,
        ["rule-body containers without a colon and scalars with one"] = """
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
            """,
    };

    public static TheoryData<string> KeptColonNames() => new(values: KeptColons.Keys);
    [MemberData(nameof(KeptColonNames))]
    [Theory]
    public void ALegalColonIsNotRefused(string name) => Assert.DoesNotContain(
        collection: WorldSources.Parse(body: KeptColons[name]).Diagnostics,
        filter: diagnostic => (diagnostic.Code == PuckDiagnosticCodes.ColonBeforeContainer)
    );

    // A colon in front of a literal container, at every depth: each refusal names the spelling it refuses.
    private static readonly Dictionary<string, Refusal> RefusedColons = new(comparer: StringComparer.Ordinal) {
        ["a document block"] = new(
            Body: "host: {\n    width: 1280\n}\n",
            Code: PuckDiagnosticCodes.ColonBeforeContainer,
            Needle: "host: {"
        ) { Mentions = "host:" },
        ["a document array"] = new(
            Body: "cameras: [\n    { name: \"a\" }\n]\n",
            Code: PuckDiagnosticCodes.ColonBeforeContainer,
            Needle: "cameras: ["
        ) { Mentions = "cameras:" },
        ["an array inside a block"] = new(
            Body: "collision {\n    requirements: [\n        \"SmoothUnionContact\"\n    ]\n}\n",
            Code: PuckDiagnosticCodes.ColonBeforeContainer,
            Needle: "requirements: ["
        ) { Mentions = "requirements:" },
        ["an array inside an object literal"] = new(
            Body: "screens [\n    { index: 0, origin: [0, 1, 0] }\n]\n",
            Code: PuckDiagnosticCodes.ColonBeforeContainer,
            Needle: "origin: ["
        ) { Mentions = "origin:" },
        ["an array in a rule body"] = new(
            Body: "rule \"zoned\" {\n    when score[0] > 0\n    zones: [\"a\"]\n    score[0] += 1\n}\n",
            Code: PuckDiagnosticCodes.ColonBeforeContainer,
            Needle: "zones: ["
        ) { Mentions = "'zones: [' - a array is written without the ':': use 'zones ['" },
        ["a block in a decision body"] = new(
            Body: "rule \"chooser\" {\n    decision {\n        periodSeconds: 1s\n        tieBreak: { seed: 0 }\n        option \"o\" {\n            score: 1\n        }\n    }\n}\n",
            Code: PuckDiagnosticCodes.ColonBeforeContainer,
            Needle: "tieBreak: {"
        ) { Mentions = "'tieBreak: {' - a block is written without the ':': use 'tieBreak {'" },
        ["a block in an option body"] = new(
            Body: "rule \"chooser\" {\n    decision {\n        periodSeconds: 1s\n        option \"o\" {\n            score: 1\n            neighbors: { range: 10 }\n        }\n    }\n}\n",
            Code: PuckDiagnosticCodes.ColonBeforeContainer,
            Needle: "neighbors: {"
        ) { Mentions = "'neighbors: {' - a block is written without the ':': use 'neighbors {'" },
    };

    public static TheoryData<string> RefusedColonNames() => new(values: RefusedColons.Keys);
    [MemberData(nameof(RefusedColonNames))]
    [Theory]
    public void AColonBeforeAContainerIsRefusedByName(string name) => WorldSources.AssertRefusedBy(
        diagnostics: WorldSources.Parse(body: RefusedColons[name].Body).Diagnostics,
        label: name,
        refusal: RefusedColons[name]
    );
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
        var formatted = PuckFormat.Format(source: """
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
}
