using Puck.Cli.Automation;
using Puck.Testing;
using Xunit;

namespace Puck.Cli.Release.Tests;

/// <summary>Laws for the shape fingerprint a qualification fixture's marker file names after its token: the fixture builder
/// writes the one the ledger records, and a leg refuses a fixture marked with the same token and another shape by the
/// marker, before it restores anything.</summary>
public sealed class QualificationMarkerShapeLawTests {
    [Fact]
    public void TheMarkerFileNamesTheTokenThenTheShapeTheLedgerRecords() =>
        Assert.Equal(
            actual: $"puck.world.qualification.v1 {FormatLedgerShapes.Of(id: "WorldReleaseQualificationRunner.Marker")}",
            expected: WorldReleaseQualificationRunner.MarkerFileContent
        );
    [Fact]
    public async Task AFixtureMarkedUnderAnotherShapeIsRefusedBeforeAnythingIsRestored() {
        using var directory = new TemporaryDirectory(prefix: "puck-qualification-marker-");

        File.WriteAllText(
            contents: "puck.world.qualification.v1 0000000000000000",
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
