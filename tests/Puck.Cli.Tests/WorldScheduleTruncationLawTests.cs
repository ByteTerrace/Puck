using Puck.World;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>CONTRACT UNDER TEST: a run that ends before the tick its schedule declares records a TRUNCATED run —
/// the manifest carries the authored tick beside the tick reached and says which it is, and every declared row the
/// run never got to records itself as unreached rather than going missing.</summary>
/// <remarks>The lever is a piped <c>quit</c> before the export tick. The world's own export file is a valid export
/// of a real tick, so the manifest's own account is the only thing that can tell a stopped run from a finished
/// one.</remarks>
public sealed class WorldScheduleTruncationLawTests {
    private const string ScheduledWorld = "phase-advance.world.json";

    [Fact]
    public void ARunThatQuitsBeforeTheExportTickRecordsATruncatedRunAndItsUnreachedRows() {
        var legDirectory = ScheduledWorldBoot.Leg(name: "truncated");
        var scheduleDirectory = Path.Combine(
            path1: legDirectory,
            path2: "out"
        );
        var process = ScheduledWorldBoot.Boot(
            legDirectory: legDirectory,
            scheduleDirectory: scheduleDirectory,
            script: "quit\n",
            world: ScheduledWorld
        );

        Assert.False(condition: process.TimedOut);

        var manifest = ScheduledWorldBoot.Manifest(scheduleDirectory: scheduleDirectory);

        Assert.True(condition: manifest[propertyName: "truncated"]!.GetValue<bool>());
        Assert.Equal(
            actual: manifest[propertyName: "authoredExportTick"]!.GetValue<ulong>(),
            expected: 12UL
        );
        Assert.True(condition: (manifest[propertyName: "exportTick"]!.GetValue<ulong>() < 12UL));

        var submissions = manifest[propertyName: "submissions"]!.AsArray();

        Assert.Equal(
            actual: submissions.Count,
            expected: 2
        );
        foreach (var submission in submissions) {
            Assert.Equal(
                actual: submission![propertyName: "outcome"]!.GetValue<string>(),
                expected: WorldScheduleSection.OutcomeUnreached
            );
        }
    }
    [Fact]
    public void ARunThatReachesTheExportTickRecordsNoTruncationAndEveryRowSubmitted() {
        var legDirectory = ScheduledWorldBoot.Leg(name: "whole");
        var scheduleDirectory = Path.Combine(
            path1: legDirectory,
            path2: "out"
        );
        var process = ScheduledWorldBoot.Boot(
            legDirectory: legDirectory,
            scheduleDirectory: scheduleDirectory,
            script: "world.wait 14\nquit\n",
            world: ScheduledWorld
        );

        Assert.False(condition: process.TimedOut);

        var manifest = ScheduledWorldBoot.Manifest(scheduleDirectory: scheduleDirectory);

        Assert.False(condition: manifest[propertyName: "truncated"]!.GetValue<bool>());
        Assert.Equal(
            actual: manifest[propertyName: "exportTick"]!.GetValue<ulong>(),
            expected: manifest[propertyName: "authoredExportTick"]!.GetValue<ulong>()
        );
        foreach (var submission in manifest[propertyName: "submissions"]!.AsArray()) {
            Assert.Equal(
                actual: submission![propertyName: "outcome"]!.GetValue<string>(),
                expected: WorldScheduleSection.OutcomeSubmitted
            );
        }
    }
}
