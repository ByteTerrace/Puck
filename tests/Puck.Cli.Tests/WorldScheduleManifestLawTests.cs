using Puck.World;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>CONTRACT UNDER TEST: <c>schedule.json</c> is evidence, not a log. Ten armed runs of the same document
/// record byte-identical manifests, because the manifest carries only facts a rerun reproduces exactly — a
/// submission's own authored tick, its outcome and its detail — and nothing stamped when the host happened to
/// narrate it.</summary>
/// <remarks>Ten boots of the real executable, so this fact costs about a minute. It is the gate the two-run
/// comparison inside <c>puck test</c> rests on: that comparison can only refuse a difference it is able to see.</remarks>
public sealed class WorldScheduleManifestLawTests {
    private const int Runs = 10;
    private const string ScheduledWorld = "phase-advance.world.json";

    // The document's own export tick is 12; fencing two past it lands the export before the pipe closes.
    private const string Script = "world.wait 14\nquit\n";

    [Fact]
    public void TenArmedRunsOfTheSameWorldRecordByteIdenticalManifests() {
        byte[]? first = null;

        for (var run = 1; (run <= Runs); run++) {
            var legDirectory = ScheduledWorldBoot.Leg(name: $"manifest{run}");
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

            Assert.False(
                condition: process.TimedOut,
                userMessage: $"run {run} timed out: {process.Stderr}"
            );

            var manifest = File.ReadAllBytes(path: Path.Combine(
                path1: scheduleDirectory,
                path2: WorldScheduleSection.ManifestFileName
            ));

            if (first is null) {
                first = manifest;

                continue;
            }

            Assert.True(
                condition: first.AsSpan().SequenceEqual(other: manifest.AsSpan()),
                userMessage: $"run {run} recorded a different manifest from run 1:{Environment.NewLine}{System.Text.Encoding.UTF8.GetString(bytes: first)}{Environment.NewLine}---{Environment.NewLine}{System.Text.Encoding.UTF8.GetString(bytes: manifest)}"
            );
        }

        Assert.NotNull(@object: first);
    }
}
