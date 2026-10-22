using Puck.Testing;
using Xunit;

namespace Puck.Cli.Worlds.Tests;

/// <summary>CONTRACT UNDER TEST: <c>puck test</c> refuses a path that is neither a world document, a <c>.puck</c>
/// source, nor a directory as a usage error.</summary>
/// <remarks>The run passes <c>--world-artifact</c> at the repository's own Release output rather than letting the verb
/// build <c>Puck.World</c> again: the test project already depends on that build.</remarks>
public sealed class TestCommandLawTests {
    private static string Artifact() => Path.Combine(
        path1: RepositoryPaths.RequireRoot(),
        path2: "src",
        path3: "Puck.World",
        path4: "bin/Release/net11.0/Puck.World.dll"
    );
    private static (int ExitCode, string Output) RunTest(params string[] arguments) =>
        ConsoleCapture.Run(run: () => SuiteRoot.Invoke(args: [
            "test",
            .. arguments,
            "--world-artifact",
            Artifact(),
        ]));

    [Fact]
    public void ADirectoryWithNoWorldDocumentIsAUsageError() {
        var (exitCode, output) = RunTest(Path.Combine(
            path1: RepositoryPaths.RequireRoot(),
            path2: "tests",
            path3: "Puck.World.Verdicts",
            path4: "nowhere"
        ));

        Assert.Equal(
            actual: exitCode,
            expected: 2
        );
        Assert.Contains(
            actualString: output,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "is neither a world document, a .puck source, nor a directory"
        );
    }
    /// <summary>An expectation addressed to a sibling document is judged by the instance a crossing starts: a run that
    /// started none fails the claim by name, and a document no reference of the world reaches has nothing to carry it,
    /// which is a usage error.</summary>
    [Fact]
    public void AnExpectationAboutASiblingNoCrossingStartedFailsByName() {
        using var directory = new TemporaryDirectory(prefix: "puck-test-sibling-");

        directory.WriteText(name: "worlds/home.puck", text: """
            schema: "puck.world.definition.v1"
            documentId: "home"

            references [
              { document: "away", name: "away" }
            ]

            state {
              world {
                table probe { "0" = 1 }
              }
            }

            test "nobody crosses" {
              expect {
                probe[0] == 1
                away {
                  probe[0] == 1
                }
              }
            }

            """);
        directory.WriteText(name: "worlds/lost.puck", text: """
            schema: "puck.world.definition.v1"
            documentId: "lost"

            state {
              world {
                table probe { "0" = 1 }
              }
            }

            test "nothing reaches it" {
              expect {
                nowhere {
                  probe[0] == 1
                }
              }
            }
            """);
        directory.WriteText(name: "worlds/away.puck", text: """
            schema: "puck.world.definition.v1"
            documentId: "away"

            state {
              world {
                table probe { "0" = 1 }
              }
            }
            """);

        var (notCrossed, notCrossedOutput) = RunTest(directory.PathOf(name: "worlds/home.puck"));

        Assert.Equal(
            actual: notCrossed,
            expected: 1
        );
        Assert.Contains(
            actualString: notCrossedOutput,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "away/expect$2: fail"
        );
        Assert.Contains(
            actualString: notCrossedOutput,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "started no instance of 'away'"
        );

        var (unreached, unreachedOutput) = RunTest(directory.PathOf(name: "worlds/lost.puck"));

        Assert.Equal(
            actual: unreached,
            expected: 2
        );
        Assert.Contains(
            actualString: unreachedOutput,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "expects verdicts from the document 'nowhere', which no reference of the world reaches"
        );
    }
    /// <summary>A test world stands where the document it tests stands: staged as the file its documentId names, beside
    /// every document its references reach, and every document those reach, so a door onto a sibling crosses and the
    /// sibling's way back names the test world's own file. A name nothing beside it answers as one document stays
    /// unstaged, and a world with no documentId keeps its generated name.</summary>
    [Fact]
    public void ATestWorldIsStagedAsItsDocumentIdBesideEveryDocumentItReaches() {
        using var directory = new TemporaryDirectory(prefix: "puck-test-staging-");

        directory.WriteText(name: "worlds/home.puck", text: """
            schema: "puck.world.definition.v1"
            documentId: "home"

            references [
              { document: "home", name: "self" }
              { document: "away", name: "away" }
              { document: "far", name: "far" }
              { document: "gone", name: "gone" }
              { document: "broken", name: "broken" }
              { document: "torn", name: "torn" }
            ]

            state {
              world {
                table probe { "0" = 1 }
              }
            }

            test "it stands beside what it reaches" {
              expect {
                probe[0] == 1
              }
            }
            """);
        directory.WriteText(name: "worlds/plain.puck", text: """
            schema: "puck.world.definition.v1"

            state {
              world {
                table probe { "0" = 1 }
              }
            }

            test "it keeps its name" {
              expect {
                probe[0] == 1
              }
            }
            """);
        directory.WriteText(name: "worlds/away.puck", text: """
            schema: "puck.world.definition.v1"
            documentId: "away"

            references [
              { document: "home", name: "home" }
              { document: "beyond", name: "beyond" }
            ]
            """);
        directory.WriteText(name: "worlds/far.world.json", text: "{ \"schema\": \"puck.world.definition.v1\", \"documentId\": \"far\" }");
        directory.WriteText(name: "worlds/beyond.world.json", text: "{ \"schema\": \"puck.world.definition.v1\", \"documentId\": \"beyond\" }");
        directory.WriteText(name: "worlds/broken.puck", text: "schema: \"puck.world.definition.v1\"\nreferences [\n");
        directory.WriteText(name: "worlds/torn.world.json", text: "{ \"schema\":");

        foreach (var (source, world) in ((ReadOnlySpan<(string, string)>)[("home", "home~it-stands-beside-what-it-reaches"), ("plain", "plain~it-keeps-its-name")])) {
            var kept = directory.PathOf(name: $"run-{source}");
            var (exitCode, output) = RunTest(directory.PathOf(name: $"worlds/{source}.puck"), "--keep", kept);

            Assert.True(
                condition: (exitCode == 0),
                userMessage: output
            );

            var staged = Directory.GetFiles(path: Path.Combine(path1: kept, path2: "generated", path3: world)).Select(selector: Path.GetFileName).Order(comparer: StringComparer.Ordinal);

            Assert.Equal(
                actual: staged,
                expected: ((source == "home")
                    ? ["away.world.json", "beyond.world.json", "far.world.json", "home.world.json"]
                    : [$"{world}.world.json"])
            );
        }
    }
}
