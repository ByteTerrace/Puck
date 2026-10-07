using Puck.Cli.Canary;

using Xunit;

namespace Puck.Cli.Tests;

/// <summary>
/// Laws for a response extraction's <c>line</c> member: the field is read from the first indented line of the selected
/// response's record that starts with the prefix, never from another response's record or from outside the record; with
/// <c>after</c>, from the first such line past the record's first line that starts with that heading; and a line naming a
/// whole counter kind reads that <c>&lt;kind&gt; &lt;value&gt;</c> line's value under the kind.
/// </summary>
public sealed class CanaryResponseLineLawTests {
    private static readonly string[] Transcript = [
        "[world.counters: gpu",
        "  node world work submission=5 revision=1",
        "  work upload executed: dispatches=1",
        "  work lifetime: created.images=2",
        "  node overlay work submission=9 revision=1",
        "  work lifetime: created.images=6",
        "  gpu.created.pipelines 17",
        "]",
        "[world.counters: gpu",
        "  node world work submission=8 revision=1",
        "]",
        "node world work submission=99 revision=1",
    ];

    [Fact]
    public void TheSelectedResponsesLineCarriesTheField() {
        Assert.True(condition: Evaluate(
            expected: 5,
            line: "node world work",
            occurrence: 1
        ));
        Assert.True(condition: Evaluate(
            expected: 8,
            line: "node world work",
            occurrence: 2
        ));
        Assert.True(condition: Evaluate(
            expected: 9,
            line: "node overlay work",
            occurrence: 1
        ));
    }
    [Fact]
    public void ALineOutsideTheRecordIsNotRead() {
        Assert.False(condition: Evaluate(
            expected: 99,
            line: "node world work",
            occurrence: 2
        ));
        Assert.False(condition: Evaluate(
            expected: 9,
            line: "node overlay work",
            occurrence: 2
        ));
    }
    [Fact]
    public void TheFirstLineIsReadWithoutALinePrefix() =>
        Assert.False(condition: Evaluate(
            expected: 5,
            line: null,
            occurrence: 1
        ));
    [Fact]
    public void AHeadingSelectsWhichOfSeveralLinesAlikeIsRead() {
        Assert.True(condition: Evaluate(
            expected: 2,
            field: "created.images",
            line: "work lifetime",
            occurrence: 1
        ));
        Assert.True(condition: Evaluate(
            after: "node overlay work",
            expected: 6,
            field: "created.images",
            line: "work lifetime",
            occurrence: 1
        ));
        // A heading the record does not carry finds nothing, rather than falling back to the first line alike.
        Assert.False(condition: Evaluate(
            after: "node sky work",
            expected: 2,
            field: "created.images",
            line: "work lifetime",
            occurrence: 1
        ));
    }
    [Fact]
    public void ALineNamingACounterKindReadsItsValue() {
        Assert.True(condition: Evaluate(
            expected: 17,
            field: "gpu.created.pipelines",
            line: "gpu.created.pipelines",
            occurrence: 1
        ));
        // Only under the kind's own name: a prefix of the kind is not the kind.
        Assert.False(condition: Evaluate(
            expected: 17,
            field: "gpu.created",
            line: "gpu.created",
            occurrence: 1
        ));
    }
    [Fact]
    public void AFieldNamedInThePrefixKeepsItsSelectedRecordAndComponents() {
        var transcript = ((string[])[
            "[world.counters: gpu",
            "  cpu-reference=(1,2,3) emission=7,8,9",
            "]",
            "[world.counters: gpu",
            "  cpu-reference=(4,5,6) emission=10,11,12",
            "]",
            "cpu-reference=(99,99,99) emission=99,99,99",
        ]);

        Assert.True(condition: Evaluate(
            line: "cpu-reference=",
            occurrence: 1,
            expected: 2,
            field: "cpu-reference",
            component: 1,
            transcript: transcript
        ));
        Assert.True(condition: Evaluate(
            line: "cpu-reference=",
            occurrence: 2,
            expected: 4,
            field: "cpu-reference",
            component: 0,
            transcript: transcript
        ));
        Assert.True(condition: Evaluate(
            line: "cpu-reference=",
            occurrence: 2,
            expected: 12,
            field: "emission",
            component: 2,
            transcript: transcript
        ));
        Assert.False(condition: Evaluate(
            line: "cpu-reference=",
            occurrence: 2,
            expected: 99,
            field: "cpu-reference",
            component: 0,
            transcript: transcript
        ));
        Assert.False(condition: Evaluate(
            line: "cpu-reference=",
            occurrence: 2,
            expected: 2,
            field: "cpu-reference",
            component: 1,
            transcript: transcript
        ));
        Assert.False(condition: Evaluate(
            line: "cpu-reference=",
            occurrence: 2,
            expected: 4,
            field: "missing",
            transcript: transcript
        ));
        Assert.False(condition: Evaluate(
            line: "missing=",
            occurrence: 2,
            expected: 4,
            field: "cpu-reference",
            component: 0,
            transcript: transcript
        ));
    }
    /// <summary>An operand with <c>minus</c> is the numeric difference of its two extracted values: the world node moved
    /// from submission 5 to 8 between the two reads, a change of 3 and not their sum or either read.</summary>
    [InlineData(3, true)]
    [InlineData(13, false)]
    [InlineData(8, false)]
    [Theory]
    public void AMinusOperandIsTheChangeBetweenTwoReads(double expected, bool passes) =>
        Assert.Equal(
            expected: passes,
            actual: CanaryAssertions.Evaluate(
                leg: Leg(
                    Read(
                        name: "first",
                        occurrence: 1
                    ),
                    Read(
                        name: "second",
                        occurrence: 2
                    ),
                    new CanaryRelationAssertion(
                        Left: new CanaryOperand(
                            Minus: "first",
                            NumberLiteral: null,
                            StringLiteral: null,
                            ValueName: "second"
                        ),
                        Margin: null,
                        Maximum: null,
                        Minimum: null,
                        Name: "change",
                        Operator: CanaryRelationOperator.Equal,
                        Right: new CanaryOperand(
                            NumberLiteral: expected,
                            StringLiteral: null,
                            ValueName: null
                        )
                    )
                ),
                primaryTranscript: new CanaryTranscript(
                    RunDirectory: ".",
                    Stderr: [],
                    Stdout: Transcript
                )
            ).Passed
        );

    private static CanaryResponseAssertion Read(string name, int occurrence) => new(
        Authority: null,
        Count: 2,
        Extractions: [new CanaryValueExtraction(
            Component: null,
            Field: "submission",
            Line: "node world work",
            Name: name
        )],
        Name: $"read-{name}",
        Occurrence: occurrence,
        Stream: CanaryStream.Stdout,
        Verb: "world.counters"
    );
    private static CanaryLeg Leg(params CanaryAssertion[] assertions) => new(
        Assertions: assertions,
        Authorities: [],
        AuthorityWorldPath: null,
        Commands: [],
        Connect: false,
        Name: "positive",
        ScriptPath: "script.txt",
        WorldPath: "world.json"
    );
    private static bool Evaluate(string? line, int occurrence, double expected, string field = "submission", string? after = null, int? component = null, IReadOnlyList<string>? transcript = null) =>
        CanaryAssertions.Evaluate(
            leg: new CanaryLeg(
                Assertions: [
                    new CanaryResponseAssertion(
                        Authority: null,
                        Count: 2,
                        Extractions: [new CanaryValueExtraction(
                            After: after,
                            Component: component,
                            Field: field,
                            Line: line,
                            Name: "submission"
                        )],
                        Name: "read",
                        Occurrence: occurrence,
                        Stream: CanaryStream.Stdout,
                        Verb: "world.counters"
                    ),
                    new CanaryRelationAssertion(
                        Left: new CanaryOperand(
                            NumberLiteral: null,
                            StringLiteral: null,
                            ValueName: "submission"
                        ),
                        Margin: null,
                        Maximum: null,
                        Minimum: null,
                        Name: "value",
                        Operator: CanaryRelationOperator.Equal,
                        Right: new CanaryOperand(
                            NumberLiteral: expected,
                            StringLiteral: null,
                            ValueName: null
                        )
                    ),
                ],
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
                Stdout: (transcript ?? Transcript)
            )
        ).Passed;
}
