using System.Globalization;
using System.Text.Json.Nodes;
using Puck.World;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>CONTRACT UNDER TEST: <c>puck test</c> over the test worlds and `.puck` sources in
/// <c>tests/Puck.World.Verdicts</c> — it boots the real <c>Puck.World</c> executable, reports one line per verdict
/// naming the gate and the values the gate saw, prints the refusals a run recorded, and exits 0 only when every
/// verdict passes. The two worlds are a discriminating pair: one is green, and one carries a verdict that is meant
/// to fail, so a runner that reported success unconditionally fails this class.</summary>
/// <remarks>These facts boot the real host unpaced. They pass <c>--world-artifact</c> at the repository's own
/// Release output rather than letting the verb build <c>Puck.World</c> again: the test project already depends on
/// that build.</remarks>
public sealed class TestCommandLawTests {
    private static string Artifact() => Path.Combine(
        path1: RepositoryPaths.RequireRoot(),
        path2: "src",
        path3: "Puck.World",
        path4: "bin/Release/net10.0/Puck.World.dll"
    );
    // The checkout root, found the way every repository tool finds it.
    private static (int ExitCode, string Output) RunTest(params string[] arguments) =>
        ConsoleCapture.Run(run: () => PuckRootCommand.Invoke(args: [
            "test",
            .. arguments,
            "--world-artifact",
            Artifact(),
        ]));
    private static string Composition(string name) => Path.Combine(
        path1: RepositoryPaths.RequireRoot(),
        path2: "tests",
        path3: "Puck.World.Verdicts/composition",
        path4: name
    );
    private static string Proof(string name) => Path.Combine(
        path1: RepositoryPaths.RequireRoot(),
        path2: "tests",
        path3: "Puck.World.Verdicts/proofs",
        path4: name
    );
    private static string Source(string name) => Path.Combine(
        path1: RepositoryPaths.RequireRoot(),
        path2: "tests",
        path3: "Puck.World.Verdicts/sources",
        path4: name
    );
    private static string World(string name) => Path.Combine(
        path1: RepositoryPaths.RequireRoot(),
        path2: "tests",
        path3: "Puck.World.Verdicts",
        path4: name
    );

    // Package entry points must keep their authored real-host scenarios in the normal test gate after graduation
    // out of the engine's built-in assets. The manifest owns the list, so adding a game also adds its test run.
    [Fact]
    public void EveryParlorEntryPointPassesItsAuthoredTests() {
        var package = Path.Combine(path1: RepositoryPaths.RequireRoot(), path2: "worlds/parlor");
        var manifest = JsonNode.Parse(utf8Json: File.ReadAllBytes(path: Path.Combine(path1: package, path2: "manifest.json")))!;
        var worlds = manifest[propertyName: "worlds"]!.AsArray();

        Assert.NotEmpty(collection: worlds);

        var sources = worlds.Select(selector: static world => world![propertyName: "source"]!.GetValue<string>()).ToArray();
        var results = new (int ExitCode, string Output)[sources.Length];

        Parallel.For(
            body: index => results[index] = RunTest(Path.Combine(path1: package, path2: sources[index])),
            fromInclusive: 0,
            toExclusive: sources.Length
        );

        for (var index = 0; (index < sources.Length); index++) {
            Assert.True(condition: (results[index].ExitCode == 0), userMessage: $"{sources[index]}{Environment.NewLine}{results[index].Output}");
        }
    }
    // Two sources of one sweep sharing a file name generate the same world name. Writing the second would replace
    // the first source's world and run only the second one, so the sweep refuses by name before anything boots.
    [Fact]
    public void TwoSourcesGeneratingOneWorldNameAreRefusedByName() {
        var directory = Directory.CreateTempSubdirectory(prefix: "puck-test-stems-").FullName;

        try {
            foreach (var folder in ((string[])["a", "b"])) {
                _ = Directory.CreateDirectory(path: Path.Combine(
                    path1: directory,
                    path2: folder
                ));
                File.Copy(
                    destFileName: Path.Combine(
                        path1: directory,
                        path2: folder,
                        path3: "seat-writes-a-cell.puck"
                    ),
                    sourceFileName: Source(name: "seat-writes-a-cell.puck")
                );
            }

            var (exitCode, output) = RunTest(directory);

            Assert.Contains(
                actualString: output,
                comparisonType: StringComparison.Ordinal,
                expectedSubstring: "both generate the test world 'seat-writes-a-cell~a-seat-s-write-reaches-the-row'"
            );
            Assert.Equal(
                actual: exitCode,
                expected: 2
            );
        } finally {
            Directory.Delete(
                path: directory,
                recursive: true
            );
        }
    }
    // A green world and a red world with the same basename must retain their own evidence on every leg.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParallelWorldsSharingAFileNameKeepSeparatePersistenceAndVerdicts(bool reproduce) {
        var directory = Directory.CreateTempSubdirectory(prefix: "puck-test-isolation-").FullName;

        try {
            var input = Path.Combine(path1: directory, path2: "input");
            var kept = Path.Combine(path1: directory, path2: "kept");
            var fixtures = new[] { "refused-command.world.json", "phase-advance.world.json" };
            var verdicts = new[] { "wrongGuardRefused", "trickPhaseAdvanced" };
            ulong[] ticks = [14UL, 12UL];

            for (var index = 0; (index < fixtures.Length); index++) {
                var folder = Directory.CreateDirectory(path: Path.Combine(
                    path1: input,
                    path2: index.ToString(provider: CultureInfo.InvariantCulture)
                ));
                var document = JsonNode.Parse(utf8Json: File.ReadAllBytes(path: World(name: fixtures[index])))!.AsObject();

                document[propertyName: "basis"] = World(name: "phase-fixture").Replace(newChar: '/', oldChar: '\\');
                File.WriteAllText(
                    path: Path.Combine(path1: folder.FullName, path2: "foo.world.json"),
                    contents: document.ToJsonString()
                );
            }

            var (exitCode, output) = RunTest([
                input, "--jobs", "2", "--keep", kept,
                .. (reproduce ? (string[])["--reproduce"] : []),
            ]);

            Assert.True(condition: (exitCode == 1), userMessage: output);
            Assert.Contains(actualString: output, comparisonType: StringComparison.Ordinal, expectedSubstring: "wrongGuardRefused: pass");
            Assert.Contains(actualString: output, comparisonType: StringComparison.Ordinal, expectedSubstring: "trickPhaseAdvanced: never evaluated");
            Assert.Equal(expected: 2, actual: Directory.GetDirectories(path: Path.Combine(path1: kept, path2: "worlds")).Length);

            for (var index = 0; (index < fixtures.Length); index++) {
                var worldDirectory = Path.Combine(
                    path1: kept,
                    path2: "worlds",
                    path3: index.ToString(format: "D6", provider: CultureInfo.InvariantCulture)
                );

                for (var run = 1; (run <= (reproduce ? 2 : 1)); run++) {
                    var leg = Path.Combine(path1: worldDirectory, path2: $"run{run.ToString(provider: CultureInfo.InvariantCulture)}");
                    var manifest = JsonNode.Parse(utf8Json: File.ReadAllBytes(path: Path.Combine(
                        path1: leg, path2: "out", path3: WorldScheduleSection.ManifestFileName
                    )))!;
                    var export = File.ReadAllText(path: Path.Combine(
                        path1: leg, path2: "out", path3: WorldScheduleSection.ExportFileName
                    ));

                    Assert.Equal(expected: ticks[index], actual: manifest[propertyName: "exportTick"]!.GetValue<ulong>());
                    Assert.Contains(expectedSubstring: verdicts[index], actualString: export, comparisonType: StringComparison.Ordinal);
                    Assert.DoesNotContain(expectedSubstring: verdicts[(1 - index)], actualString: export, comparisonType: StringComparison.Ordinal);
                    Assert.True(condition: Directory.Exists(path: Path.Combine(path1: leg, path2: "state")));
                    Assert.True(condition: File.Exists(path: Path.Combine(path1: leg, path2: "stdout.log")));
                    Assert.True(condition: File.Exists(path: Path.Combine(path1: leg, path2: "stderr.log")));
                }
            }
        } finally {
            Directory.Delete(path: directory, recursive: true);
        }
    }
    // A test at a composition root drives the composed set: one generated document per world, the first declared one
    // arming the rest, a step landing in the world it addressed, and a verdict read out of each world's own export.
    // The report names the world a far verdict came from, and the determinism leg compares every world's bytes.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AComposedRunArmsExportsAndJudgesEveryWorld(bool reproduce) {
        var (exitCode, output) = RunTest([
            Composition(name: "composition.puck"),
            .. (reproduce ? (string[])["--reproduce"] : []),
        ]);

        Assert.Contains(
            actualString: output,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "expect$2: pass gate=\"lit == 1\" saw=[lit=1]"
        );
        Assert.Contains(
            actualString: output,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "south/expect$4: pass gate=\"lit == 0\" saw=[lit=0]"
        );
        Assert.Contains(
            actualString: output,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "4/4 verdict(s) passed at export tick 7 across 2 world(s)."
        );
        Assert.Equal(
            actual: exitCode,
            expected: 0
        );
    }
    // The crossing: the body standing in the booted world is flown over the border, and the verdict that says so is
    // one the far world's own rule wrote from its own region occupancy.
    [Fact]
    public void ABodyCrossingTheBorderIsJudgedByTheFarWorldsOwnRule() {
        var (exitCode, output) = RunTest(Composition(name: "composition.puck"));

        Assert.Contains(
            actualString: output,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "south/expect$2: pass gate=\"visited == 1\" saw=[visited=1]"
        );
        Assert.Equal(
            actual: exitCode,
            expected: 0
        );
    }
    // The red half of the crossing: the same body, never sent across the seam. The far world's verdict fails by name
    // and carries the occupancy its rule read.
    [Fact]
    public void ABodyThatNeverCrossesFailsTheFarWorldsVerdictByName() {
        var (exitCode, output) = RunTest(Proof(name: "uncrossed-border.puck"));

        Assert.Contains(
            actualString: output,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "expect$1: pass gate=\"visited == 1\" saw=[visited=1]"
        );
        Assert.Contains(
            actualString: output,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "south/expect$2: fail gate=\"visited == 1\" saw=[visited=0]"
        );
        Assert.Equal(
            actual: exitCode,
            expected: 1
        );
    }
    // Every document a composed test generates is written, and `--keep` leaves each of them on disk: the booted
    // world, and one per world its schedule arms. Only the booted document is a world this verb runs.
    [Fact]
    public void KeepWritesEveryDocumentAComposedTestGenerates() {
        var kept = Directory.CreateTempSubdirectory(prefix: "puck-test-composed-").FullName;

        try {
            var (exitCode, output) = RunTest(
                Composition(name: "composition.puck"),
                "--keep",
                kept
            );

            Assert.True(condition: (exitCode == 0), userMessage: output);

            var generated = Path.Combine(path1: kept, path2: "generated");

            foreach (var test in ((string[])["a-body-that-walks-across-the-border-is-seen-by-the-far-world", "a-charge-lands-in-the-world-its-step-addressed"])) {
                var boot = Path.Combine(path1: generated, path2: $"composition~{test}.world.json");
                var sibling = Path.Combine(path1: generated, path2: $"composition~{test}~south.world.json");

                Assert.True(condition: File.Exists(path: boot), userMessage: boot);
                Assert.True(condition: File.Exists(path: sibling), userMessage: sibling);

                var document = JsonNode.Parse(utf8Json: File.ReadAllBytes(path: boot))!.AsObject();
                var instance = Assert.Single(collection: document[propertyName: "schedule"]![propertyName: "instances"]!.AsArray());

                Assert.Equal(expected: "south", actual: instance![propertyName: "name"]!.GetValue<string>());
                Assert.Equal(
                    actual: instance[propertyName: "document"]!.GetValue<string>(),
                    expected: $"composition~{test}~south"
                );
                // The far world's own document carries no schedule: one grid arms the run, and the far world is
                // stepped and exported beside the world that declared it.
                Assert.Null(@object: JsonNode.Parse(utf8Json: File.ReadAllBytes(path: sibling))!.AsObject()[propertyName: "schedule"]);
            }
            // Two worlds, two runs apiece, is still two worlds this verb boots.
            Assert.Equal(expected: 2, actual: Directory.GetDirectories(path: Path.Combine(path1: kept, path2: "worlds")).Length);
        } finally {
            Directory.Delete(path: kept, recursive: true);
        }
    }
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
    // A generated verdict row is an Int row: it records what the gate read of an Int row, and what the gate read of
    // a Fixed or a Bool row is recorded by a witness of that kind and listed the way a source spells the value.
    [Fact]
    public void ATestGivesAndExpectsOnARowOfEveryKind() {
        var (exitCode, output) = RunTest(Source(name: "every-kind-is-given.puck"));

        Assert.Contains(
            actualString: output,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "expect$1: pass gate=\"speed > 2.0\" saw=[speed=2.25]"
        );
        Assert.Contains(
            actualString: output,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "expect$2: pass gate=\"hp == 3\" saw=[hp=3]"
        );
        Assert.Contains(
            actualString: output,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "expect$1: pass gate=\"open == 0\" saw=[open=false]"
        );
        Assert.Equal(
            actual: exitCode,
            expected: 0
        );
    }
    [Fact]
    public void ASourcesTestBlockGeneratesTheWorldThatRunsAndPasses() {
        var (exitCode, output) = RunTest(Source(name: "seat-writes-a-cell.puck"));

        Assert.Contains(
            actualString: output,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "seat-writes-a-cell~a-seat-s-write-reaches-the-row <-"
        );
        Assert.Contains(
            actualString: output,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "expect$1: pass gate=\"armour == 7\" saw=[armour=7]"
        );
        Assert.Equal(
            actual: exitCode,
            expected: 0
        );
    }
    // A read step is judged by what the real host's ingress answered: the owner's read submits, and the other seat's
    // read of the same cell is refused with the text the step declared.
    [Fact]
    public void ASeatsReadStepIsAnsweredThroughItsOwnDisclosure() {
        var (exitCode, output) = RunTest(Source(name: "a-seat-reads-only-what-it-is-shown.puck"));

        Assert.Contains(
            actualString: output,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "expect$1: pass gate=\"vault == 41\" saw=[vault=41]"
        );
        Assert.Equal(
            actual: exitCode,
            expected: 0
        );
    }
    // A module's own test runs at the use that brought it, under that use's arguments, and a test written with a
    // module stands the module up on its own under the arguments the test chose.
    [Fact]
    public void AModulesTestRunsAtItsUseAndATestWithAModuleStandsItUpAlone() {
        var (exitCode, output) = RunTest(Source(name: "a-module-and-its-use.puck"));

        Assert.Contains(
            actualString: output,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "a-module-and-its-use~armoury~a-seat-s-write-reaches-the-plating <-"
        );
        Assert.Contains(
            actualString: output,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "test \"the module boots at the plating it is given\" with armoury(plating: 5)"
        );
        Assert.Contains(
            actualString: output,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "expect$1: pass gate=\"armour == 7\" saw=[armour=7]"
        );
        Assert.Contains(
            actualString: output,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "expect$1: pass gate=\"armour == 5\" saw=[armour=5]"
        );
        Assert.Equal(
            actual: exitCode,
            expected: 0
        );
    }
    // A failing module test names the module and the arguments it was stood up with, the expectation as written,
    // and the value the gate read.
    [Fact]
    public void AModuleTestWhoseExpectationIsWrongFailsAndNamesTheModuleAndTheArguments() {
        var (exitCode, output) = RunTest(Proof(name: "wrong-module-expectation.puck"));

        Assert.Contains(
            actualString: output,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "test \"the module boots at a plating it was not given\" with armoury(plating: 5)"
        );
        Assert.Contains(
            actualString: output,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "expect$1: fail gate=\"armour == 9\" saw=[armour=5]"
        );
        Assert.Equal(
            actual: exitCode,
            expected: 1
        );
    }
    // A source that emits several worlds runs each world's own module tests, named for the world they ran for, and
    // the tests its root writes with a module beside them.
    [Fact]
    public void ACompositionSourceRunsEachWorldsModuleTestsAndItsOwn() {
        var (exitCode, output) = RunTest(Path.Combine(
            path1: RepositoryPaths.RequireRoot(),
            path2: "worlds/beacons"
        ), "--jobs", "3");

        foreach (var world in ((string[])["north", "south"])) {
            Assert.Contains(
                actualString: output,
                comparisonType: StringComparison.Ordinal,
                expectedSubstring: $"{world}~a-charge-past-every-admitted-threshold-lights-the-beacon <-"
            );
        }
        Assert.Contains(
            actualString: output,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "expect$1: pass gate=\"lit == 0\" saw=[lit=0]"
        );
        Assert.Contains(
            actualString: output,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "PASS: every verdict in 6 test world(s) passed."
        );
        Assert.Equal(
            actual: exitCode,
            expected: 0
        );
    }
    [Fact]
    public void ASourceWhoseExpectationIsWrongFailsAndNamesTheGateAndTheValue() {
        var (exitCode, output) = RunTest(Proof(name: "wrong-expectation.puck"));

        Assert.Contains(
            actualString: output,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "expect$1: fail gate=\"armour == 9\" saw=[armour=7]"
        );
        Assert.Equal(
            actual: exitCode,
            expected: 1
        );
    }
    [Fact]
    public void ASourceWithNoTestBlockIsAUsageError() {
        var (exitCode, output) = RunTest(World(name: "no-test-block.puck"));

        Assert.Equal(
            actual: exitCode,
            expected: 2
        );
        Assert.Contains(
            actualString: output,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "authors no test block"
        );
    }
    [Fact]
    public void AHostThisVerbCannotBootIsRefusedByName() {
        var (exitCode, output) = RunTest(
            World(name: "refused-command.world.json"),
            "--host",
            "browser"
        );

        Assert.Equal(
            actual: exitCode,
            expected: 2
        );
        Assert.Contains(
            actualString: output,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "--host browser is not implemented"
        );
    }
    [Fact]
    public void AWorldWhoseScheduledCommandIsRefusedPassesAndPrintsTheRecordedRefusal() {
        var (exitCode, output) = RunTest(World(name: "refused-command.world.json"));

        Assert.Equal(
            actual: exitCode,
            expected: 0
        );
        Assert.Contains(
            actualString: output,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "wrongGuardRefused: pass gate=\"a guarded transform naming generation 7 when passPhase stands at 0 is refused, so the generation never advances\" saw=[generation=0]"
        );
        Assert.Contains(
            actualString: output,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "TransformState rejected: phase admission refused"
        );
        Assert.Contains(
            actualString: output,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "1/1 verdict(s) passed at export tick 14."
        );
        Assert.Contains(
            actualString: output,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "PASS: every verdict in 1 test world(s) passed"
        );
    }
    [Fact]
    public void AScheduledGuardedTransformAdvancesAShippedPhaseAndAnUnprovenVerdictFailsByName() {
        var (exitCode, output) = RunTest(World(name: "phase-advance.world.json"));

        Assert.Equal(
            actual: exitCode,
            expected: 1
        );
        Assert.Contains(
            actualString: output,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "passPhaseAdvanced: pass gate=\"passPhase's generation advanced past 0 once the scheduled guarded transform landed\" saw=[generation=1]"
        );
        Assert.Contains(
            actualString: output,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "trickPhaseAdvanced: never evaluated gate=\"idlePhase's generation advanced past 0 — deliberately unproven: nothing is scheduled against it\" saw=[generation=0]"
        );
        Assert.Contains(
            actualString: output,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "1/2 verdict(s) passed at export tick 12."
        );
        Assert.Contains(
            actualString: output,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "FAIL: one or more worlds did not pass — a failing verdict, or a step whose recorded outcome was not the one it declared."
        );
    }
    [Fact]
    public void AScheduledRowRefusedWithNoExpectationFailsTheWorldWhileItsPassingVerdictStands() {
        var (exitCode, output) = RunTest(Proof(name: "unexpected-outcome.world.json"));

        Assert.Equal(
            actual: exitCode,
            expected: 1
        );
        Assert.Contains(
            actualString: output,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "passPhaseStillZero: pass"
        );
        Assert.Contains(
            actualString: output,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "schedule.rows[0] expects 'submitted' and the run recorded 'refused'"
        );
    }
    [Fact]
    public void TheSameRowDeclaringTheRefusalItExpectsPasses() {
        var (exitCode, output) = RunTest(Proof(name: "expected-outcome.world.json"));

        Assert.Equal(
            actual: exitCode,
            expected: 0
        );
        Assert.Contains(
            actualString: output,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "PASS: every verdict in 1 test world(s) passed"
        );
    }
}
