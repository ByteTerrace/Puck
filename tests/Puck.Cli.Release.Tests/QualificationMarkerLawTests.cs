using Puck.Cli.Automation;
using Puck.Testing;
using Xunit;

namespace Puck.Cli.Release.Tests;

/// <summary>Laws for a qualification fixture's marker file: a leg refuses a fixture marked with another token by the
/// marker, before it restores anything.</summary>
public sealed class QualificationMarkerLawTests {
    [Fact]
    public async Task AFixtureMarkedWithAnotherTokenIsRefusedBeforeAnythingIsRestored() {
        using var directory = new TemporaryDirectory(prefix: "puck-qualification-marker-");

        File.WriteAllText(
            contents: "puck.world.qualification.v0",
            path: Path.Combine(
                path1: directory.RootPath,
                path2: "qualification.fixture"
            )
        );

        var (exitCode, _, error) = await ConsoleCapture.RunSplitAsync(run: () => WorldReleaseExerciseCommand.Create().Parse(args: [directory.RootPath]).InvokeAsync(cancellationToken: TestContext.Current.CancellationToken));

        Assert.NotEqual(
            actual: exitCode,
            expected: 0
        );
        Assert.Contains(
            actualString: error,
            expectedSubstring: "requires a marked disposable qualification fixture"
        );
    }
}
