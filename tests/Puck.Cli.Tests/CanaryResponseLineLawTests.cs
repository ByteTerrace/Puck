using Puck.Cli.Canary;

using Xunit;

namespace Puck.Cli.Tests;

/// <summary>
/// Laws for a response extraction's <c>line</c> member: the field is read from the first indented line of the selected
/// response's record that starts with the prefix, never from another response's record or from outside the record.
/// </summary>
public sealed class CanaryResponseLineLawTests {
    private static readonly string[] Transcript = [
        "[world.counters: gpu",
        "  node world work submission=5 revision=1",
        "  work upload executed: dispatches=1",
        "  node overlay work submission=9 revision=1",
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
    private static bool Evaluate(string? line, int occurrence, double expected) =>
        CanaryAssertions.Evaluate(
            leg: new CanaryLeg(
                Assertions: [
                    new CanaryResponseAssertion(
                        Authority: null,
                        Count: 2,
                        Extractions: [new CanaryValueExtraction(
                            Component: null,
                            Field: "submission",
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
                Stdout: Transcript
            )
        ).Passed;
}
