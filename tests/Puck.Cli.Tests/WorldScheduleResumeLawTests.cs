using Xunit;

namespace Puck.Cli.Tests;

/// <summary>CONTRACT UNDER TEST: a scheduled run is not resumable. Inside an armed run the verbs that write,
/// re-read or rewind the running document — <c>world.save</c>, <c>world.load</c>, <c>world.reload</c>,
/// <c>world.undo</c> — are refused by name, because a schedule carries no cursor and a restored world would submit
/// every row again from tick 1. The same lines on an unarmed boot of the same document are not refused by that
/// text, which is the discriminating half.</summary>
public sealed class WorldScheduleResumeLawTests {
    private const string ScheduledWorld = "phase-advance.world.json";

    private static readonly string[] Verbs = ["world.save", "world.load", "world.reload", "world.undo"];

    private static string Script(string savePath) => string.Join(
        separator: Environment.NewLine,
        "world.wait 2",
        $"world.save {savePath}",
        "world.load nowhere.world.json",
        "world.reload",
        "world.undo",
        "quit",
        string.Empty
    );

    [Fact]
    public void EveryDocumentRestoringVerbIsRefusedByNameInsideAnArmedRun() {
        var legDirectory = ScheduledWorldBoot.Leg(name: "armed-resume");
        var savePath = Path.Combine(
            path1: legDirectory,
            path2: "saved.world.json"
        );
        var run = ScheduledWorldBoot.Boot(
            legDirectory: legDirectory,
            scheduleDirectory: Path.Combine(
                path1: legDirectory,
                path2: "out"
            ),
            script: Script(savePath: savePath),
            world: ScheduledWorld
        );
        var transcript = (run.Stdout + run.Stderr);

        Assert.False(condition: run.TimedOut);

        foreach (var verb in Verbs) {
            Assert.Contains(
                actualString: transcript,
                comparisonType: StringComparison.Ordinal,
                expectedSubstring: $"[{verb}: refused — this boot armed the document's schedule"
            );
        }

        Assert.False(
            condition: File.Exists(path: savePath),
            userMessage: "an armed run wrote a snapshot that claims to be resumable"
        );
    }
    [Fact]
    public void TheSameVerbsOnAnUnarmedBootAreNotRefusedForThatReason() {
        var legDirectory = ScheduledWorldBoot.Leg(name: "unarmed-resume");
        var savePath = Path.Combine(
            path1: legDirectory,
            path2: "saved.world.json"
        );
        var run = ScheduledWorldBoot.Boot(
            legDirectory: legDirectory,
            script: Script(savePath: savePath),
            world: ScheduledWorld
        );
        var transcript = (run.Stdout + run.Stderr);

        Assert.False(condition: run.TimedOut);
        Assert.DoesNotContain(
            actualString: transcript,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "this boot armed the document's schedule"
        );
        Assert.True(
            condition: File.Exists(path: savePath),
            userMessage: "the control did not save, so the armed refusal discriminates nothing"
        );
    }
}
