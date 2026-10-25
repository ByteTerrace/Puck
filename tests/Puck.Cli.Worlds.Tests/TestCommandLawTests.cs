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
        directory.WriteText(name: "worlds/away-base.puck", text: """
            schema: "puck.world.definition.v1"
            documentId: "away-base"

            state {
              world {
                table probe { "0" = 1 }
              }
            }
            """);
        directory.WriteText(name: "worlds/away.puck", text: """
            schema: "puck.world.definition.v1"
            basis: "away-base"
            documentId: "away"
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
    /// <summary>An instance the host starts beside the booted world is exported when its document carries a verdict
    /// row, and its verdicts are reported under the document's name. A portal's window onto <c>away</c> starts it
    /// without any body crossing, so the claim that holds passes with the values it saw, a claim that never holds
    /// reports never evaluated, and both runs of a reproduction export the same bytes.</summary>
    [Fact]
    public void AnInstanceTheHostStartsIsExportedAndJudgedUnderItsDocument() {
        using var directory = new TemporaryDirectory(prefix: "puck-test-started-");

        directory.WriteText(name: "worlds/home.puck", text: """
            schema: "puck.world.definition.v1"
            documentId: "home"

            references [
              { document: "away", name: "away" }
            ]

            destinations [
              {
                durability: persisted
                name: "away"
                reference: "away"
                scope: global
              }
            ]

            placements {
              policy {
                authoringHeadroomInstances: 0
                authoringHeadroomScreens: 0
                candidateCap: 1
                candidateRadius: 1
                derivedFaceScreens: 2
                maxPlacementScale: 1
                minPlacementScale: 1
                previewDeadlineFrames: 1
              }
              placement "door" {
                prototype: "frame"
                position [0, 0, -3]
                yawDegrees: 0
                faceSources [
                  {
                    face: "glass"
                    filter: linear
                    portal {
                      arrival: "mapped"
                      counterpart: "gate/glass"
                      destination: "away"
                      travel: "body"
                    }
                    source {
                      $type: "session"
                      destination: "away"
                      fallback: "#FF00FF"
                      projection: window
                    }
                  }
                ]
              }
            }

            prototypes {
              prototype "frame" {
                document {
                  behavior {
                    faces [{ name: "glass", shapeId: 0 }]
                  }
                  name: "frame"
                  palette [{ color: "#C9A43A", roughness: 0.4, specular: 0.4 }]
                  schema: "puck.creation.v1"
                  shape Box "rim" {
                    material: 0
                    position [0, 1.3, 0]
                    scale [1.12, 1.42, 0.04]
                  }
                }
              }
            }

            test "the window starts it" {
              when {
                ticks 10
              }
              expect {
                away {
                  probe[0] == 1
                }
              }
            }

            test "a claim that never holds" {
              when {
                ticks 10
              }
              expect {
                away {
                  probe[0] == 2
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

            placements {
              policy {
                authoringHeadroomInstances: 0
                authoringHeadroomScreens: 0
                candidateCap: 1
                candidateRadius: 1
                derivedFaceScreens: 2
                maxPlacementScale: 1
                minPlacementScale: 1
                previewDeadlineFrames: 1
              }
              placement "gate" {
                prototype: "frame"
                position [3, 0, -8]
                yawDegrees: 0
              }
            }

            prototypes {
              prototype "frame" {
                document {
                  behavior {
                    faces [{ name: "glass", shapeId: 0 }]
                  }
                  name: "frame"
                  palette [{ color: "#C9A43A", roughness: 0.4, specular: 0.4 }]
                  schema: "puck.creation.v1"
                  shape Box "rim" {
                    material: 0
                    position [0, 1.3, 0]
                    scale [1.12, 1.42, 0.04]
                  }
                }
              }
            }
            """);

        var (exitCode, output) = RunTest(directory.PathOf(name: "worlds/home.puck"), "--reproduce");

        Assert.Equal(
            actual: exitCode,
            expected: 1
        );
        Assert.Contains(
            actualString: output,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "away/expect$1: pass gate=\"probe[0] == 1\" saw=[probe$0=1]"
        );
        Assert.Contains(
            actualString: output,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "away/expect$1: never evaluated gate=\"probe[0] == 2\""
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
