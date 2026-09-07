using Puck.Cli.Canary;

using Xunit;

namespace Puck.Cli.Tests;

/// <summary>
/// Proves <c>CanaryCommand</c>'s per-verb response accounting against synthetic transcripts — no real
/// <c>Puck.World</c> boot. Each fact pins one shape the runner must read correctly: a bare colon answer, a
/// space- or dot-led multi-line answer, two adjacent calls of the same verb that must NOT merge, and a verb whose
/// only signal is the universal mutation narration rather than its own bracket.
/// </summary>
public sealed class CanaryAccountingLawTests {
    private static CliProcessOutputLine Line(string text, long sequence, CliProcessOutputStream stream = CliProcessOutputStream.Stdout) =>
        new(Line: text, Sequence: sequence, Stream: stream);

    [Fact]
    public void AnExactColonAnswerIsOneEvent() {
        var lines = new[] { Line(text: "[world.status: kits 2]", sequence: 1) };
        var events = CanaryCommand.ResponseEvents(outputLines: lines, verb: "world.status");

        Assert.Single(collection: events);
    }
    [Fact]
    public void AnExactAnswerAbsorbsTheFacetLinesThatImmediatelyFollowIt() {
        // world.state's own no-argument dump: one exact header, then one row-header line per row, with nothing
        // else between them — all one answer.
        var lines = new[] {
            Line(text: "[world.state: rows 2/100]", sequence: 1),
            Line(text: "[world.state.row 'a' kind=Int value=1 domain=slot]", sequence: 2),
            Line(text: "[world.state.row 'b' kind=Int value=2 domain=slot]", sequence: 3),
        };
        var events = CanaryCommand.ResponseEvents(outputLines: lines, verb: "world.state");

        Assert.Single(collection: events);
    }
    [Fact]
    public void ASpaceLedAnswerContinuesOverFurtherSpaceLedLinesWithNoGap() {
        // world.rule.trace's own shape: "[verb name: armed …]" then "[verb name tick=…]" lines, none of them a
        // bare "[verb:" — a space, never a colon, follows the verb.
        var lines = new[] {
            Line(text: "[world.rule.trace r: 2/2 captured, complete]", sequence: 1),
            Line(text: "[world.rule.trace r tick=1 gate=closed: …]", sequence: 2),
            Line(text: "[world.rule.trace r tick=2 gate=open: …]", sequence: 3),
        };
        var events = CanaryCommand.ResponseEvents(outputLines: lines, verb: "world.rule.trace");

        Assert.Single(collection: events);
    }
    [Fact]
    public void TwoSeparateSpaceLedCallsOfAGenericVerbWithNoGapBetweenThemStillMergeAsOneEvent() {
        // The general bracket reading cannot tell two back-to-back calls of the SAME generic space-led verb apart
        // from one call's own multi-line answer — only a verb the runner keys on (world.state, world.symmetry)
        // gets that disambiguation. No shipped script calls world.rule.trace twice with nothing between the two
        // calls (arena-attack's own two occurrences are separated by body.press/world.wait), so this stays a
        // known, deliberate boundary rather than an unfixed defect.
        var lines = new[] {
            Line(text: "[world.rule.trace r: armed for 1 evaluation(s) …]", sequence: 1),
            Line(text: "[world.rule.trace r: 1/1 captured, complete]", sequence: 2),
        };
        var events = CanaryCommand.ResponseEvents(outputLines: lines, verb: "world.rule.trace");

        Assert.Single(collection: events);
    }
    [Fact]
    public void AWorldStateRowHeaderAlwaysOpensFreshEvenRightAfterAnotherRowsCells() {
        // Two row-only "world.state <row>" calls back to back — each row header can only ever be the FIRST line
        // of its own answer, so the second must never be read as a continuation of the first row's cell run.
        var lines = new[] {
            Line(text: "[world.state.row 'a' kind=Int value=1 domain=slot]", sequence: 1),
            Line(text: "[world.state.cell 'a'.'$value' value=1]", sequence: 2),
            Line(text: "[world.state.row 'b' kind=Int value=2 domain=slot]", sequence: 3),
            Line(text: "[world.state.cell 'b'.'$value' value=2]", sequence: 4),
        };
        var events = CanaryCommand.ResponseEvents(outputLines: lines, verb: "world.state");

        Assert.Equal(expected: 2, actual: events.Count);
    }
    [Fact]
    public void AWorldStateSingleCellAnswerNeverMergesIntoADifferentRowsSingleCellAnswer() {
        // Two "world.state <row> <key>" calls back to back — neither has a row header at all, so the only
        // discriminator is the cell's own row identity.
        var lines = new[] {
            Line(text: "[world.state.cell 'x'.'k' value=1]", sequence: 1),
            Line(text: "[world.state.cell 'y'.'k' value=2]", sequence: 2),
        };
        var events = CanaryCommand.ResponseEvents(outputLines: lines, verb: "world.state");

        Assert.Equal(expected: 2, actual: events.Count);
    }
    [Fact]
    public void AWorldSymmetryTwoArgumentCallsTwoLinesStayOneEventOnTheSharedNode() {
        var lines = new[] {
            Line(text: "[world.symmetry node=5 ring=2 antipode=18 …]", sequence: 1),
            Line(text: "[world.symmetry node=5 other=17 reflect=16 …]", sequence: 2),
        };
        var events = CanaryCommand.ResponseEvents(outputLines: lines, verb: "world.symmetry");

        Assert.Single(collection: events);
    }
    [Fact]
    public void TwoWorldSymmetryCallsOnDifferentNodesWithNoGapAreTwoEvents() {
        var lines = new[] {
            Line(text: "[world.symmetry node=5 ring=2 …]", sequence: 1),
            Line(text: "[world.symmetry node=87 ring=4 …]", sequence: 2),
        };
        var events = CanaryCommand.ResponseEvents(outputLines: lines, verb: "world.symmetry");

        Assert.Equal(expected: 2, actual: events.Count);
    }
    [Fact]
    public void ANarratedMutationVerbReadsTheUniversalNarrationByItsDescribePrefix() {
        var lines = new[] {
            Line(text: "[world.mutation: Generate 'tiles' applied]", sequence: 1, stream: CliProcessOutputStream.Stderr),
            Line(text: "[world.mutation rejected: Generate 'tiles' - no draw paint]", sequence: 2, stream: CliProcessOutputStream.Stderr),
        };
        var events = CanaryCommand.ResponseEvents(outputLines: lines, verb: "world.generate");

        Assert.Equal(expected: 2, actual: events.Count);
    }
    [Fact]
    public void ANarratedMutationVerbNeverMatchesADifferentMutationKindsNarration() {
        var lines = new[] {
            Line(text: "[world.mutation: UpsertKit 'wren' applied]", sequence: 1, stream: CliProcessOutputStream.Stderr),
        };
        var events = CanaryCommand.ResponseEvents(outputLines: lines, verb: "world.generate");

        Assert.Empty(collection: events);
    }
    [Fact]
    public void ANarratedVerbsAcceptedClaimDefaultsToStderrNotStdout() {
        var commands = new[] { new CanaryCommandClaim(Verb: "world.generate", Occurrence: 1, Outcome: CanaryCommandOutcome.Accepted, StreamOverride: null) };
        var lines = new[] { Line(text: "[world.mutation: Generate 'tiles' applied]", sequence: 1, stream: CliProcessOutputStream.Stderr) };
        var results = CanaryCommand.EvaluateCommandAccounting(commands: commands, outputLines: lines);

        Assert.Contains(collection: results, filter: result => (result.Detail == "world.generate occurrence 1 was accepted") && result.Passed);
    }
    [Fact]
    public void ARegisteredVerbsAcceptedClaimDefaultsToStdout() {
        var commands = new[] { new CanaryCommandClaim(Verb: "world.row.set", Occurrence: 1, Outcome: CanaryCommandOutcome.Accepted, StreamOverride: null) };
        var lines = new[] { Line(text: "[world.row.set: UpsertKit 'wren' applied]", sequence: 1, stream: CliProcessOutputStream.Stdout) };
        var results = CanaryCommand.EvaluateCommandAccounting(commands: commands, outputLines: lines);

        Assert.Contains(collection: results, filter: result => (result.Detail == "world.row.set occurrence 1 was accepted") && result.Passed);
    }
}
