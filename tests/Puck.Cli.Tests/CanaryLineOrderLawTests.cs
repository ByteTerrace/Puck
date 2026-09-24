using Puck.Cli.Canary;

using Xunit;

namespace Puck.Cli.Tests;

/// <summary>
/// Laws for the ordered-lines canary assertion (<c>"type": "lines"</c>): every listed line must match past the previous
/// one's match, so a line present only before its predecessor, or missing, fails the order.
/// </summary>
public sealed class CanaryLineOrderLawTests {
    private static readonly string[] Transcript = [
        "[world.counters: gpu",
        "node world work submission=5 revision=1",
        "work upload executed: dispatches=3",
        "work sky skipped",
        "work composite executed: dispatches.indirect=1",
        "work outside: command-buffers=1",
    ];

    [Fact]
    public void LinesInTheirOrderPass() =>
        Assert.True(condition: Evaluate(
            lines: ["work upload executed: dispatches=3", "work sky skipped", "work outside: command-buffers=1"],
            match: CanaryLineMatch.Exact
        ));
    [Fact]
    public void ContainedLinesInTheirOrderPass() =>
        Assert.True(condition: Evaluate(
            lines: ["submission=5", "dispatches.indirect=1"],
            match: CanaryLineMatch.Contains
        ));
    [Fact]
    public void ALineOnlyBeforeItsPredecessorFails() =>
        Assert.False(condition: Evaluate(
            lines: ["work sky skipped", "work upload executed: dispatches=3"],
            match: CanaryLineMatch.Exact
        ));
    [Fact]
    public void AMissingLineFails() =>
        Assert.False(condition: Evaluate(
            lines: ["work upload executed: dispatches=3", "work mask skipped"],
            match: CanaryLineMatch.Exact
        ));

    private static bool Evaluate(CanaryLineMatch match, string[] lines) =>
        CanaryAssertions.Evaluate(
            leg: new CanaryLeg(
                Assertions: [new CanaryLineOrderAssertion(
                    Authority: null,
                    Lines: lines,
                    Match: match,
                    Name: "order",
                    Stream: CanaryStream.Stdout
                )],
                Authorities: [],
                AuthorityWorldPath: null,
                Commands: [],
                Connect: false,
                Name: "positive",
                ScriptPath: "script.txt",
                WorldPath: "world.json"
            ),
            primaryTranscript: new CanaryTranscript(
                RunDirectory: ".",
                Stderr: [],
                Stdout: Transcript
            )
        ).Passed;
}
