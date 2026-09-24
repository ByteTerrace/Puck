using Puck.World;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>CONTRACT UNDER TEST: the <c>schedule</c> section is inert unless the boot armed it. A bare
/// <c>--world … --headless true</c> boot of a scheduled world submits no row, writes no export, and says so; the
/// same boot with <c>--schedule-dir</c> runs the schedule and the verdict its rows decide turns over.</summary>
/// <remarks>The discriminating pair is the point: the unarmed leg reaches the same tick and the verdict its rows
/// decide still reads <c>never evaluated</c>, which is only possible if the scheduled guarded transform never
/// landed.</remarks>
public sealed class WorldScheduleArmingLawTests {
    private const string ScheduledWorld = "phase-advance.world.json";
    // The document's own export tick (two rows, the last at tick 6, settleTicks 6), fenced two ticks past so the
    // export lands before the pipe closes.
    private const string Script = "world.wait 14\nworld.schedule\nworld.verdicts\nquit\n";

    [Fact]
    public void ABareBootOfAScheduledWorldSubmitsNothingAndSaysSo() {
        using var leg = ScheduledWorldBoot.Leg();
        var legDirectory = leg.PathOf(name: "unarmed");
        var process = ScheduledWorldBoot.Boot(
            legDirectory: legDirectory,
            script: Script,
            world: ScheduledWorld
        );
        var transcript = (process.Stdout + process.Stderr);

        Assert.Contains(
            actualString: transcript,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "authors a schedule (2 row(s), export tick 12) and this boot did not arm it"
        );
        Assert.Contains(
            actualString: transcript,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "[world.schedule: unarmed"
        );
        Assert.Contains(
            actualString: transcript,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "passPhaseAdvanced: never evaluated"
        );
        Assert.DoesNotContain(
            actualString: transcript,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "[schedule] tick "
        );
        Assert.Empty(collection: Directory.GetFiles(
            path: legDirectory,
            searchOption: SearchOption.AllDirectories,
            searchPattern: WorldScheduleSection.ExportFileName
        ));
    }
    [Fact]
    public void TheSameBootArmedRunsTheScheduleAndTurnsTheVerdictOver() {
        using var leg = ScheduledWorldBoot.Leg();
        var legDirectory = leg.PathOf(name: "armed");
        var scheduleDirectory = Path.Combine(
            path1: legDirectory,
            path2: "out"
        );
        var process = ScheduledWorldBoot.Boot(
            legDirectory: legDirectory,
            scheduleDirectory: scheduleDirectory,
            script: Script,
            world: ScheduledWorld
        );
        var transcript = (process.Stdout + process.Stderr);

        Assert.Contains(
            actualString: transcript,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "[world.schedule: armed"
        );
        Assert.Contains(
            actualString: transcript,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "passPhaseAdvanced: pass"
        );
        Assert.True(condition: File.Exists(path: Path.Combine(
            path1: scheduleDirectory,
            path2: WorldScheduleSection.ExportFileName
        )));
    }
}
