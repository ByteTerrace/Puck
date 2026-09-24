using Puck.World;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>CONTRACT UNDER TEST: <c>schedule.json</c> is evidence, not a log. Ten armed runs of the same document
/// record byte-identical manifests, because the manifest carries only facts a rerun reproduces exactly — a
/// submission's own authored tick, its outcome and its detail — and nothing stamped when the host happened to
/// narrate it.</summary>
/// <remarks>Ten concurrent boots of the real executable, each in its own leg directory. It is the gate the two-run
/// comparison inside <c>puck test</c> rests on: that comparison can only refuse a difference it is able to see.</remarks>
public sealed class WorldScheduleManifestLawTests {
    private const int Runs = 10;
    private const string ScheduledWorld = "phase-advance.world.json";
    // The document's own export tick is 12; fencing two past it lands the export before the pipe closes.
    private const string Script = "world.wait 14\nquit\n";

    [Fact]
    public void TenArmedRunsOfTheSameWorldRecordByteIdenticalManifests() {
        var manifests = new byte[Runs][];

        Parallel.For(
            body: index => {
                using var leg = ScheduledWorldBoot.Leg();
                var legDirectory = leg.PathOf(name: $"manifest{(index + 1)}");
                var scheduleDirectory = Path.Combine(
                    path1: legDirectory,
                    path2: "out"
                );

                _ = ScheduledWorldBoot.Boot(
                    legDirectory: legDirectory,
                    scheduleDirectory: scheduleDirectory,
                    script: Script,
                    world: ScheduledWorld
                );
                manifests[index] = File.ReadAllBytes(path: Path.Combine(
                    path1: scheduleDirectory,
                    path2: WorldScheduleSection.ManifestFileName
                ));
            },
            fromInclusive: 0,
            toExclusive: Runs
        );

        for (var index = 1; (index < Runs); index++) {
            Assert.True(
                condition: manifests[0].AsSpan().SequenceEqual(other: manifests[index].AsSpan()),
                userMessage: $"run {(index + 1)} recorded a different manifest from run 1:{Environment.NewLine}{System.Text.Encoding.UTF8.GetString(bytes: manifests[0])}{Environment.NewLine}---{Environment.NewLine}{System.Text.Encoding.UTF8.GetString(bytes: manifests[index])}"
            );
        }
    }
}
