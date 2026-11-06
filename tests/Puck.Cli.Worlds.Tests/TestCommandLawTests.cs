using Puck.Testing;
using Xunit;

namespace Puck.Cli.Worlds.Tests;

/// <summary>CONTRACT UNDER TEST: <c>puck test</c> refuses a path that is neither a world document, a <c>.puck</c>
/// source, nor a directory as a usage error.</summary>
/// <remarks>The run passes <c>--world-artifact</c> at the repository's own Release output rather than letting the verb
/// build <c>Puck.World</c> again: the test project already depends on that build.</remarks>
public sealed class TestCommandLawTests {
    [Fact]
    public void FilterSelectsAuthoredNamesAndGeneratedGlobsForReproduceRecordAndCompare() {
        using var directory = new TemporaryDirectory(prefix: "puck-test-filter-");
        directory.WriteText(name: "selection.puck", text: """
            schema: "puck.world.definition.v1"
            documentId: "selection"
            state { world { table value { "0" = 7 } } }
            test "chosen scenario" { expect { value[0] == 7 } }
            test "unselected failure" { expect { value[0] == 99 } }
            """);
        var source = directory.PathOf("selection.puck");
        var recorded = directory.PathOf("recorded");
        var (exit, output) = RunTest(source, "--filter", "chosen scenario", "--reproduce", "--record", recorded);
        Assert.True(exit == 0, output);
        Assert.Contains("selected 1 world(s); skipped 1 world(s) by --filter", output);
        Assert.Contains("processes=1 host-boots=1 world-runs=1", output);
        Assert.Single(Directory.GetDirectories(recorded));
        var (compared, comparedOutput) = RunTest(source, "--filter", "*~chosen-*", "--reproduce", "--compare", recorded);
        Assert.True(compared == 0, comparedOutput);
        var (missing, missingOutput) = RunTest(source, "--filter", "absent");
        Assert.Equal(2, missing);
        Assert.Contains("no test worlds matched", missingOutput);
    }
    [Fact]
    public void AScopedInputDeniedAuthorityIsRecordedInTheManifestAndRefusalReport() {
        using var directory = new TemporaryDirectory(prefix: "puck-test-scope-refusal-");

        directory.WriteText(name: "scope.puck", text: """
            schema: "puck.world.definition.v1"
            state { world {
              slot counter = 0
              table control { request = 0, status = 0, cursor = 0, completed = 0 }
            } }
            replays [{
              name: "scope", rows [counter], bodies [], seats ["seat1"], control: control, durationTicks: 6
              inputs [{ tick: 1, principal: "seat1", cells [{ row: counter, key: $value, value: 7 }] }]
            }]
            rule "drive" {
              when $tick == 3
              control.request = 1
            }
            test "denied scope" {
              given { control.request = 4 }
              when { ticks 12 }
              expect { counter == 0 }
            }
            """);
        var kept = directory.PathOf(name: "kept");

        var (exit, output) = RunTest(directory.PathOf(name: "scope.puck"), "--keep", kept);

        Assert.True(condition: (exit == 0), userMessage: output);
        Assert.Contains(actualString: output, comparisonType: StringComparison.Ordinal, expectedSubstring: "refusal:");
        var manifest = Directory.GetFiles(path: kept, searchOption: SearchOption.AllDirectories, searchPattern: "test-run.json").Single();
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(path: manifest));

        Assert.Contains(collection: json.RootElement.GetProperty(propertyName: "echoes").EnumerateArray(), filter: echo => echo.GetProperty(propertyName: "rejected").GetBoolean());
    }
    [Fact]
    public void DifferentStateAndRulesOwnTheirHostsAndKeepTheirOwnVerdicts() {
        using var directory = new TemporaryDirectory(prefix: "puck-test-shapes-");

        directory.WriteText(name: "worlds/first.puck", text: """
            schema: "puck.world.definition.v1"
            documentId: "same"
            state { world { table value { "0" = 7 } } }
            test "first" { expect { value[0] == 7 } }
            """);
        directory.WriteText(name: "worlds/second.puck", text: """
            schema: "puck.world.definition.v1"
            documentId: "same"
            state { world { table value { "0" = 11 } } }
            rule "change" {
              when value[0] == 11
              value[0] = 13
            }
            test "second" { expect { value[0] == 13 } }
            """);
        var (exit, output) = RunTest(directory.PathOf(name: "worlds"), "--reproduce");
        Assert.True(condition: (exit == 0), userMessage: output);
        Assert.Contains(actualString: output, comparisonType: StringComparison.Ordinal, expectedSubstring: "processes=1 host-boots=2 world-runs=2");
    }
    [Fact]
    public void EveryScenarioStartsPristineInItsOwnHost() {
        using var directory = new TemporaryDirectory(prefix: "puck-test-isolation-");

        directory.WriteText(name: "isolation.puck", text: """
            schema: "puck.world.definition.v1"
            documentId: "same"
            grants [
              { capability: Mutate, grantee: "seat1", subject: "section:state" }
              { capability: Edit, grantee: "seat1", subject: "all" }
            ]
            state { world {
              table value { "0" = 7 }
              table marker { "0" = 7 }
            } }
            test "pristine" {
              when {
                seat1: world.state.cell.set marker 0 7
              }
              expect { value[0] >= 7 }
            }
            test "changed" {
              given { value[0] = 11 }
              when {
                seat1: world.state.cell.set value 0 99
              }
              expect {
                value[0] == 99
                marker[0] == 7
              }
            }
            test "pristine again" {
              when {
                seat1: world.state.cell.set marker 0 7
              }
              expect { value[0] >= 7 }
            }
            test "identical boot" {
              when {
                seat1: world.state.cell.set marker 0 7
              }
              expect { value[0] >= 7 }
            }
            """);
        var kept = directory.PathOf(name: "kept");

        var (exit, output) = RunTest(directory.PathOf(name: "isolation.puck"), "--reproduce", "--keep", kept);
        Assert.Equal(actual: exit, expected: 0);
        Assert.Contains(actualString: output, comparisonType: StringComparison.Ordinal, expectedSubstring: "processes=1 host-boots=4 world-runs=4");
        for (var index = 0; (index < 4); index++) {
            Assert.Contains("\"divergence\":-1", File.ReadAllText(path: Path.Combine(path1: kept, path2: $"worlds/{index:D6}/run1/reproduction.json")), StringComparison.Ordinal);
        }
        Assert.Equal(expected: File.ReadAllBytes(path: Path.Combine(path1: kept, path2: "worlds/000000/run1/out/state-export.json")),
            actual: File.ReadAllBytes(path: Path.Combine(path1: kept, path2: "worlds/000002/run1/out/state-export.json")));
        Assert.NotEqual(File.ReadAllBytes(path: Path.Combine(path1: kept, path2: "worlds/000000/run1/out/state-export.json")),
            File.ReadAllBytes(path: Path.Combine(path1: kept, path2: "worlds/000001/run1/out/state-export.json")));
        // Also compare a worker's boot to a truly fresh process, using the same staged world and ordinary boot.
        var generated = Path.Combine(path1: kept, path2: "generated/isolation~pristine/same.world.json");
        var freshDirectory = directory.PathOf(name: "fresh");
        using var error = new StringWriter();

        Assert.True(Test.TestCommand.TryRunLeg(world: generated, artifact: Artifact(), legDirectory: freshDirectory,
            exportTick: 7, rateHz: 60, error: error, reading: out var fresh), userMessage: error.ToString());
        Assert.Equal(expected: File.ReadAllBytes(path: Path.Combine(path1: kept, path2: "worlds/000003/run1/out/state-export.json")), actual: fresh!.ExportBytes);
        Assert.Equal(expected: File.ReadAllBytes(path: Path.Combine(path1: kept, path2: "worlds/000003/run1/out/test-run.json")), actual: fresh.ManifestBytes);

        static ulong[] ReadProbeHashes(string reproductionPath) {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(path: reproductionPath));

            return [.. document.RootElement[0].GetProperty(propertyName: "hashes").EnumerateArray().Select(selector: static item => item.GetUInt64())];
        }

        var pristineHashes = ReadProbeHashes(reproductionPath: Path.Combine(path1: kept, path2: "worlds/000000/run1/reproduction.json"));
        var changedHashes = ReadProbeHashes(reproductionPath: Path.Combine(path1: kept, path2: "worlds/000001/run1/reproduction.json"));
        var reusedHashes = ReadProbeHashes(reproductionPath: Path.Combine(path1: kept, path2: "worlds/000003/run1/reproduction.json"));

        Assert.False(condition: File.Exists(path: Path.Combine(path1: freshDirectory, path2: "reproduction.json")));
        // An ordinary scheduled boot records no proof. A separate reproduction must actually compare it.
        var freshReproduction = directory.PathOf(name: "fresh-reproduction");

        var (freshExit, freshOutput) = RunTest(generated, "--reproduce", "--keep", freshReproduction);

        Assert.True(condition: (freshExit == 0), userMessage: freshOutput);
        var freshHashes = ReadProbeHashes(reproductionPath: Path.Combine(path1: freshReproduction, path2: "worlds/000000/run1/reproduction.json"));

        Assert.Equal(expected: 7, actual: pristineHashes.Length);
        Assert.Equal(actual: reusedHashes, expected: pristineHashes);
        Assert.Equal(actual: freshHashes, expected: pristineHashes);
        Assert.NotEqual(actual: changedHashes, expected: pristineHashes);
    }
    [Fact]
    public void ABatchLegWhoseTimingOrHostBootsWriteFailsFailsPromptlyWithItsReason() {
        using var directory = new TemporaryDirectory(prefix: "puck-test-batch-io-");
        var worldPath = directory.WriteText(
            name: "generated/scenario/test.world.json",
            text: """{"schema":"puck.world.definition.v1","schedule":{"exportTick":1,"settleTicks":1,"rows":[]}}"""
        );
        var leg0 = directory.PathOf(name: "worlds/000000/run1");
        var leg1 = directory.PathOf(name: "worlds/000001/run1");

        _ = Directory.CreateDirectory(path: Path.Combine(path1: leg0, path2: "timing.json"));
        _ = Directory.CreateDirectory(path: Path.Combine(path1: leg1, path2: "host-boots"));

        var batchPath = directory.WriteText(
            name: "batch.json",
            text: System.Text.Json.JsonSerializer.Serialize(value: new[] {
                new {
                    World = worldPath,
                    Directory = leg0,
                    ExitAfterSeconds = 120,
                    Reproduce = false,
                },
                new {
                    World = worldPath,
                    Directory = leg1,
                    ExitAfterSeconds = 120,
                    Reproduce = false,
                },
            })
        );
        var process = CliProcess.RunCaptured(
            fileName: "dotnet",
            arguments: [Artifact(), "--devices", "none", "--test-batch", batchPath],
            cancellationToken: TestContext.Current.CancellationToken,
            input: string.Empty,
            timeout: TimeSpan.FromSeconds(value: 30)
        );

        Assert.Equal(expected: 1, actual: process.ExitCode);
        Assert.Equal(expected: "1", actual: File.ReadAllText(path: Path.Combine(path1: leg0, path2: "exit-code")));
        Assert.Contains(
            expectedSubstring: "timing.json",
            actualString: File.ReadAllText(path: Path.Combine(path1: leg0, path2: "stderr.log")),
            comparisonType: StringComparison.OrdinalIgnoreCase
        );
        Assert.Equal(expected: "1", actual: File.ReadAllText(path: Path.Combine(path1: leg1, path2: "exit-code")));
        Assert.Contains(
            expectedSubstring: "host-boots",
            actualString: File.ReadAllText(path: Path.Combine(path1: leg1, path2: "stderr.log")),
            comparisonType: StringComparison.OrdinalIgnoreCase
        );
    }
    [Fact]
    public void AFailedLegDoesNotStopTheLegsThatNeedAFreshHost() {
        using var directory = new TemporaryDirectory(prefix: "puck-test-batch-continue-");
        var worldPath = directory.WriteText(
            name: "generated/scenario/test.world.json",
            text: """{"schema":"puck.world.definition.v1","schedule":{"exportTick":1,"settleTicks":1,"rows":[]}}"""
        );
        var failing = directory.PathOf(name: "worlds/000000/run1");
        var following = directory.PathOf(name: "worlds/000001/run1");

        // The first leg fails as a reproduction divergence would, after it ran; the next needs a host of its own.
        _ = Directory.CreateDirectory(path: Path.Combine(path1: failing, path2: "timing.json"));
        var batchPath = directory.WriteText(
            name: "batch.json",
            text: System.Text.Json.JsonSerializer.Serialize(value: new[] {
                new { World = worldPath, Directory = failing, ExitAfterSeconds = 120, Reproduce = false, },
                new { World = worldPath, Directory = following, ExitAfterSeconds = 120, Reproduce = false, },
            })
        );
        var process = CliProcess.RunCaptured(
            fileName: "dotnet",
            arguments: [Artifact(), "--devices", "none", "--test-batch", batchPath],
            cancellationToken: TestContext.Current.CancellationToken,
            input: string.Empty,
            timeout: TimeSpan.FromSeconds(value: 60)
        );

        Assert.Equal(expected: 1, actual: process.ExitCode);
        Assert.Equal(expected: "1", actual: File.ReadAllText(path: Path.Combine(path1: failing, path2: "exit-code")));
        Assert.Equal(expected: "0", actual: File.ReadAllText(path: Path.Combine(path1: following, path2: "exit-code")));
    }

    private static string Artifact() => (File.Exists(path: Path.GetFullPath(path: Path.Combine(path1: AppContext.BaseDirectory, path2: "../../Puck.World/release/Puck.World.dll")))
        ? Path.GetFullPath(path: Path.Combine(path1: AppContext.BaseDirectory, path2: "../../Puck.World/release/Puck.World.dll"))
        : Path.Combine(
        path1: RepositoryPaths.RequireRoot(),
        path2: "src",
        path3: "Puck.World",
        path4: "bin/Release/net11.0/Puck.World.dll"
    ));
    private static (int ExitCode, string Output) RunTest(params string[] arguments) =>
        ConsoleCapture.Run(run: () => SuiteRoot.Invoke(args: [
            "test",
            .. arguments,
            "--world-artifact",
            Artifact(),
            "--jobs", "1",
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

        var kept = directory.PathOf(name: "kept");

        var (exitCode, output) = RunTest(directory.PathOf(name: "worlds/home.puck"), "--reproduce", "--keep", kept);

        foreach (var proof in Directory.EnumerateFiles(path: kept, searchOption: SearchOption.AllDirectories, searchPattern: "reproduction.json")) {
            var authorities = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path: proof))!.AsArray();

            Assert.Contains(collection: authorities, filter: row => (row?["world"]?.GetValue<string>() != "boot"));
            Assert.All(authorities, row => Assert.Equal(-1, row!["divergence"]!.GetValue<int>()));
        }

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
