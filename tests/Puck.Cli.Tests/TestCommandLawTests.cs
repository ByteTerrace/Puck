using Xunit;

namespace Puck.Cli.Tests;

/// <summary>CONTRACT UNDER TEST: <c>puck test</c> over the test worlds and `.puck` sources in
/// <c>tests/Puck.World.Verdicts</c> — it boots the real <c>Puck.World</c> executable, reports one line per verdict
/// naming the gate and the values the gate saw, prints the refusals a run recorded, and exits 0 only when every
/// verdict passes. The two worlds are a discriminating pair: one is green, and one carries a verdict that is meant
/// to fail, so a runner that reported success unconditionally fails this class.</summary>
/// <remarks>These facts boot a real process twice per world, so each takes tens of seconds. They pass
/// <c>--world-artifact</c> at the repository's own Release output rather than letting the verb build
/// <c>Puck.World</c> again: the test project already depends on that build.</remarks>
public sealed class TestCommandLawTests {
    private static string Artifact() => Path.Combine(
        path1: Root(),
        path2: "src",
        path3: "Puck.World",
        path4: "bin/Release/net10.0/Puck.World.dll"
    );
    // The checkout root: ascend from the test assembly until the solution file appears, the same walk the
    // browser suite uses to reach the shipped world assets.
    private static string Root() {
        for (var directory = new DirectoryInfo(path: AppContext.BaseDirectory); (directory is not null); directory = directory.Parent) {
            if (File.Exists(path: Path.Combine(
                path1: directory.FullName,
                path2: "Puck.slnx"
            ))) {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException(message: "the repository root (the directory holding Puck.slnx) was not found above the test assembly");
    }
    private static (int ExitCode, string Output) RunTest(params string[] arguments) {
        var previousOut = Console.Out;
        var previousError = Console.Error;
        var captured = new StringWriter();

        try {
            Console.SetOut(newOut: captured);
            Console.SetError(newError: captured);

            var exitCode = PuckRootCommand.Invoke(args: [
                "test",
                .. arguments,
                "--world-artifact",
                Artifact(),
            ]);

            return (exitCode, captured.ToString());
        } finally {
            Console.SetOut(newOut: previousOut);
            Console.SetError(newError: previousError);
        }
    }
    private static string Proof(string name) => Path.Combine(
        path1: Root(),
        path2: "tests",
        path3: "Puck.World.Verdicts/proofs",
        path4: name
    );
    private static string Source(string name) => Path.Combine(
        path1: Root(),
        path2: "tests",
        path3: "Puck.World.Verdicts/sources",
        path4: name
    );
    private static string World(string name) => Path.Combine(
        path1: Root(),
        path2: "tests",
        path3: "Puck.World.Verdicts",
        path4: name
    );

    // Two sources of one sweep sharing a file name generate the same world name. Writing the second would replace
    // the first source's world and run the second one's twice, so the sweep refuses by name before anything boots.
    [Fact]
    public void TwoSourcesGeneratingOneWorldNameAreRefusedByName() {
        var directory = Directory.CreateTempSubdirectory(prefix: "puck-test-stems-").FullName;

        try {
            foreach (var folder in (string[])["a", "b"]) {
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
                expectedSubstring: "both generate the test world 'seat-writes-a-cell--a-seat-s-write-reaches-the-row'"
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
    [Fact]
    public void ADirectoryWithNoWorldDocumentIsAUsageError() {
        var (exitCode, output) = RunTest(Path.Combine(
            path1: Root(),
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
    // A generated verdict row is an Int row: it records what the gate read of an Int row, and a gate over a row of
    // another kind still boots, fires and passes.
    [Fact]
    public void ATestGivesAndExpectsOnARowOfEveryKind() {
        var (exitCode, output) = RunTest(Source(name: "every-kind-is-given.puck"));

        Assert.Contains(
            actualString: output,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "a-fixed-row-is-given-and-expected-on-1: pass gate=\"speed > 2.0\" saw=[]"
        );
        Assert.Contains(
            actualString: output,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "a-fixed-row-is-given-and-expected-on-2: pass gate=\"hp == 3\" saw=[hp=3]"
        );
        Assert.Contains(
            actualString: output,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "a-bool-row-is-given-and-expected-on-1: pass gate=\"open == 0\""
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
            expectedSubstring: "seat-writes-a-cell--a-seat-s-write-reaches-the-row <-"
        );
        Assert.Contains(
            actualString: output,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "a-seat-s-write-reaches-the-row-1: pass gate=\"armour == 7\" saw=[armour=7]"
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
            expectedSubstring: "a-seat-s-write-reaches-the-wrong-row-1: fail gate=\"armour == 9\" saw=[armour=7]"
        );
        Assert.Equal(
            actual: exitCode,
            expected: 1
        );
    }
    // A shipped world states its own behaviour in `test` blocks, and this sweep is what runs them: every test of
    // every shipped source, each generated world booted twice through the real executable.
    [Fact]
    public void EveryTestAShippedWorldAuthorsPasses() {
        var (exitCode, output) = RunTest(Path.Combine(
            path1: Root(),
            path2: "src/Puck.World/Assets/worlds"
        ));

        Assert.Contains(
            actualString: output,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "chinese-checkers--the-win-condition-fires-when-the-last-piece-lands <-"
        );
        Assert.True(
            condition: (exitCode == 0),
            userMessage: output
        );
    }
    [Fact]
    public void ASourceWithNoTestBlockIsAUsageError() {
        var (exitCode, output) = RunTest(Path.Combine(
            path1: Root(),
            path2: "src/Puck.World/Assets/worlds/games/reversi.puck"
        ));

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
            expectedSubstring: "wrongGuardRefused: pass gate=\"a guarded transform naming generation 7 when heartsPassPhase stands at 0 is refused, so the generation never advances\" saw=[generation=0]"
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
            expectedSubstring: "passPhaseAdvanced: pass gate=\"heartsPassPhase's generation advanced past 0 once the scheduled guarded transform landed\" saw=[generation=1]"
        );
        Assert.Contains(
            actualString: output,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "trickPhaseAdvanced: never evaluated gate=\"heartsTrickPhase's generation advanced past 0 — deliberately unproven: nothing is scheduled against it\" saw=[generation=0]"
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
