using Puck.Cli.Canary;
using Puck.Commands;

using Xunit;

namespace Puck.Cli.Tests;

/// <summary>
/// Proves <c>CanaryCommand</c>'s per-verb response accounting against synthetic transcripts — no real
/// <c>Puck.World</c> boot. Each fact pins one shape the runner must read correctly. A transcript is written the way the
/// World writes it: every result and narration is one <see cref="ConsoleRecord"/>, whose lines after the first are
/// indented, so a record opens on an unindented line and the runner pairs the verb's i-th record with its i-th
/// authored occurrence.
/// </summary>
public sealed class CanaryAccountingLawTests {
    private static CliProcessOutputLine Line(string text, long sequence, CliProcessOutputStream stream = CliProcessOutputStream.Stdout) =>
        new(
            ElapsedMilliseconds: sequence,
            Line: text,
            Sequence: sequence,
            Stream: stream
        );
    // The lines a record reaches its stream as, through the World's own framing.
    private static string[] Framed(string record) {
        using var writer = new StringWriter();

        ConsoleRecord.Write(
            record: record,
            writer: writer
        );

        return writer.ToString().ReplaceLineEndings(replacementText: "\n").TrimEnd(trimChar: '\n').Split('\n');
    }
    private static CanaryCommandClaim Accepted(string verb, int occurrence) => new(
        Occurrence: occurrence,
        Outcome: CanaryCommandOutcome.Accepted,
        StreamOverride: null,
        Verb: verb
    );

    [Fact]
    public void ANarratedMutationVerbNeverMatchesADifferentMutationKindsNarration() {
        var lines = new[] {
            Line(
            sequence: 1,
            stream: CliProcessOutputStream.Stderr,
            text: "[world.mutation: UpsertKit 'wren' applied]"
        ),
        };
        var events = CanaryCommand.ResponseEvents(
            outputLines: lines,
            verb: "world.generate"
        );

        Assert.Empty(collection: events);
    }
    [Fact]
    public void ANarratedMutationVerbReadsTheUniversalNarrationByItsDescribePrefix() {
        var lines = new[] {
            Line(
            sequence: 1,
            stream: CliProcessOutputStream.Stderr,
            text: "[world.mutation: Generate 'tiles' applied]"
        ),
            Line(
            sequence: 2,
            stream: CliProcessOutputStream.Stderr,
            text: "[world.mutation rejected: Generate 'tiles' - no draw paint]"
        ),
        };
        var events = CanaryCommand.ResponseEvents(
            outputLines: lines,
            verb: "world.generate"
        );

        Assert.Equal(
            expected: 2,
            actual: events.Count
        );
    }
    [Fact]
    public void ANarratedVerbsAcceptedClaimDefaultsToStderrNotStdout() {
        var commands = new[] { Accepted(
            occurrence: 1,
            verb: "world.generate"
        ) };
        var lines = new[] { Line(
            sequence: 1,
            stream: CliProcessOutputStream.Stderr,
            text: "[world.mutation: Generate 'tiles' applied]"
        ) };
        var results = CanaryCommand.EvaluateCommandAccounting(
            commands: commands,
            outputLines: lines
        );

        Assert.Contains(
            collection: results,
            filter: result => ((result.Detail == "world.generate occurrence 1 was accepted") && result.Passed)
        );
    }
    [Fact]
    public void ARegisteredVerbsAcceptedClaimDefaultsToStdout() {
        var commands = new[] { Accepted(
            occurrence: 1,
            verb: "world.row.set"
        ) };
        var lines = new[] { Line(
            sequence: 1,
            stream: CliProcessOutputStream.Stdout,
            text: "[world.row.set: UpsertKit 'wren' applied]"
        ) };
        var results = CanaryCommand.EvaluateCommandAccounting(
            commands: commands,
            outputLines: lines
        );

        Assert.Contains(
            collection: results,
            filter: result => ((result.Detail == "world.row.set occurrence 1 was accepted") && result.Passed)
        );
    }
    // The World writes a multi-line answer to one stream while the other stream carries narration from the same frame,
    // and the runner reads the two pipes concurrently, so a stderr line may be sequenced between an answer's lines.
    [Fact]
    public void AStderrLineSequencedInsideAMultiLineAnswerNeverSplitsIt() {
        var dump = Framed(record: string.Join(
            separator: "\n",
            values: [
                "[world.state: rows 3/1024, arena 96/67108864 bytes]",
                "[world.state.row 'a' kind=Int value=1 domain=slot]",
                "[world.state.row 'b' kind=Int value=2 domain=slot]",
                "[world.state.row 'c' kind=Int value=3 domain=slot]",
            ]
        ));
        var lines = new[] {
            Line(
            text: dump[0],
            sequence: 1
        ),
            Line(
            sequence: 2,
            stream: CliProcessOutputStream.Stderr,
            text: "[world.mutation: UpsertStateCell 'a' applied]"
        ),
            Line(
            text: dump[1],
            sequence: 3
        ),
            Line(
            text: dump[2],
            sequence: 4
        ),
            Line(
            text: dump[3],
            sequence: 5
        ),
        };
        var results = CanaryCommand.EvaluateCommandAccounting(
            commands: [Accepted(
                occurrence: 1,
                verb: "world.state"
            )],
            outputLines: lines
        );

        Assert.Contains(
            collection: results,
            filter: static result => ((result.Detail == "accounted world.state: 1 response(s) for 1 authored occurrence(s)") && result.Passed)
        );
    }
    // go-capture's shape: back-to-back row reads, each a header plus its cells, while the tick-boundary narration of
    // the script's earlier world.state.cell.set lines lands on stderr and is sequenced between a header and its cells.
    [Fact]
    public void StderrNarrationSequencedInsideBackToBackRowAnswersNeverOpensAnExtraResponse() {
        string[][] rows = [
            Framed(record: "[world.state.row 'captured' kind=Int cells=2/8 domain=keys]\n[world.state.cell 'captured'.'1' value=0]\n[world.state.cell 'captured'.'2' value=1]"),
            Framed(record: "[world.state.row 'prisoners' kind=Int cells=2/2 domain=keys]\n[world.state.cell 'prisoners'.'1' value=0]\n[world.state.cell 'prisoners'.'2' value=1]"),
            Framed(record: "[world.state.row 'moves' kind=Int value=7 domain=slot]\n[world.state.cell 'moves'.'$value' value=7]"),
            Framed(record: "[world.state.row 'turn' kind=Int value=2 domain=slot]\n[world.state.cell 'turn'.'$value' value=2]"),
        ];
        var lines = new List<CliProcessOutputLine>();
        var sequence = 0L;

        foreach (var row in rows) {
            lines.Add(item: Line(
                sequence: ++sequence,
                text: row[0]
            ));
            lines.Add(item: Line(
                sequence: ++sequence,
                stream: CliProcessOutputStream.Stderr,
                text: "[world.mutation: UpsertStateCell 'stone' applied]"
            ));

            foreach (var cell in row[1..]) {
                lines.Add(item: Line(
                    sequence: ++sequence,
                    text: cell
                ));
            }
        }

        var results = CanaryCommand.EvaluateCommandAccounting(
            commands: [.. Enumerable.Range(count: 4, start: 1).Select(selector: static occurrence => Accepted(
                occurrence: occurrence,
                verb: "world.state"
            ))],
            outputLines: lines
        );

        Assert.Contains(
            collection: results,
            filter: static result => ((result.Detail == "accounted world.state: 4 response(s) for 4 authored occurrence(s)") && result.Passed)
        );
    }
    [Fact]
    public void AnIndentedLineContinuesTheRecordItFollows() {
        var lines = Framed(record: "[world.rule.trace r: 2/2 captured, complete]\n[world.rule.trace r tick=1 gate=closed: …]\n[world.rule.trace r tick=2 gate=open: …]")
            .Select(selector: static (text, index) => Line(
                sequence: index,
                text: text
            ))
            .ToArray();
        var events = CanaryCommand.ResponseEvents(
            outputLines: lines,
            verb: "world.rule.trace"
        );

        Assert.Single(collection: events);
    }
    [Fact]
    public void AnExactAnswerAbsorbsTheFacetLinesItFrames() {
        var lines = Framed(record: "[world.state: rows 2/100]\n[world.state.row 'a' kind=Int value=1 domain=slot]\n[world.state.row 'b' kind=Int value=2 domain=slot]")
            .Select(selector: static (text, index) => Line(
                sequence: index,
                text: text
            ))
            .ToArray();
        var events = CanaryCommand.ResponseEvents(
            outputLines: lines,
            verb: "world.state"
        );

        Assert.Single(collection: events);
    }
    [Fact]
    public void AnExactColonAnswerIsOneEvent() {
        var lines = new[] { Line(
            text: "[world.status: kits 2]",
            sequence: 1
        ) };
        var events = CanaryCommand.ResponseEvents(
            outputLines: lines,
            verb: "world.status"
        );

        Assert.Single(collection: events);
    }
    [Fact]
    public void ASubNamedVerbsRecordsNeverCountForTheVerbItExtends() {
        var lines = new[] {
            Line(
            text: "[world.state.hash: 0123456789abcdef]",
            sequence: 1
        ),
            Line(
            text: "[world.state.cell 'x'.'k' value=1]",
            sequence: 2
        ),
        };
        var records = CanaryCommand.ResponseRecords(
            outputLines: lines,
            verbs: ["world.state", "world.state.hash"]
        );

        Assert.Single(collection: records["world.state"]);
        Assert.Single(collection: records["world.state.hash"]);
    }
    // Two reads of the same cell, one after the other, answer with the same text; each is still its own record.
    [Fact]
    public void TwoBackToBackIdenticalSingleCellAnswersAreTwoResponses() {
        var lines = new[] {
            Line(
            text: "[world.state.cell 'x'.'k' value=1]",
            sequence: 1
        ),
            Line(
            text: "[world.state.cell 'x'.'k' value=1]",
            sequence: 2
        ),
        };
        var results = CanaryCommand.EvaluateCommandAccounting(
            commands: [
                Accepted(
                    occurrence: 1,
                    verb: "world.state"
                ),
                Accepted(
                    occurrence: 2,
                    verb: "world.state"
                ),
            ],
            outputLines: lines
        );

        Assert.Contains(
            collection: results,
            filter: static result => ((result.Detail == "accounted world.state: 2 response(s) for 2 authored occurrence(s)") && result.Passed)
        );
    }
    [Fact]
    public void TwoBackToBackCallsOfAMultiLineVerbAreTwoRecords() {
        var lines = Framed(record: "[world.rule.trace r: 1/1 captured, complete]\n[world.rule.trace r tick=1 gate=open: …]")
            .Concat(second: Framed(record: "[world.rule.trace r: 1/1 captured, complete]\n[world.rule.trace r tick=2 gate=open: …]"))
            .Select(selector: static (text, index) => Line(
                sequence: index,
                text: text
            ))
            .ToArray();
        var events = CanaryCommand.ResponseEvents(
            outputLines: lines,
            verb: "world.rule.trace"
        );

        Assert.Equal(
            expected: 2,
            actual: events.Count
        );
    }
    [Fact]
    public void TwoWorldStateRowAnswersBackToBackAreTwoRecords() {
        var lines = Framed(record: "[world.state.row 'a' kind=Int value=1 domain=slot]\n[world.state.cell 'a'.'$value' value=1]")
            .Concat(second: Framed(record: "[world.state.row 'b' kind=Int value=2 domain=slot]\n[world.state.cell 'b'.'$value' value=2]"))
            .Select(selector: static (text, index) => Line(
                sequence: index,
                text: text
            ))
            .ToArray();
        var events = CanaryCommand.ResponseEvents(
            outputLines: lines,
            verb: "world.state"
        );

        Assert.Equal(
            expected: 2,
            actual: events.Count
        );
    }
    [Fact]
    public void TwoWorldSymmetryCallsOnDifferentNodesAreTwoRecords() {
        var lines = new[] {
            Line(
            text: "[world.symmetry node=5 ring=2 …]",
            sequence: 1
        ),
            Line(
            text: "[world.symmetry node=87 ring=4 …]",
            sequence: 2
        ),
        };
        var events = CanaryCommand.ResponseEvents(
            outputLines: lines,
            verb: "world.symmetry"
        );

        Assert.Equal(
            expected: 2,
            actual: events.Count
        );
    }
    [Fact]
    public void WorldSymmetrysTwoLineAnswerIsOneRecord() {
        var lines = Framed(record: "[world.symmetry node=5 ring=2 antipode=18 …]\n[world.symmetry node=5 other=17 reflect=16 …]")
            .Select(selector: static (text, index) => Line(
                sequence: index,
                text: text
            ))
            .ToArray();
        var events = CanaryCommand.ResponseEvents(
            outputLines: lines,
            verb: "world.symmetry"
        );

        Assert.Single(collection: events);
    }
}
