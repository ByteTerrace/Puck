using Puck.Cli.Test;
using Puck.World;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>CONTRACT UNDER TEST: <c>puck test</c> refuses a leg that did not measure the document — a run that
/// stopped before the tick the schedule declares, a declared row the manifest cannot account for, and a line that
/// never reached a handler — and fails the world by name when a row answered something other than the outcome it
/// declared. A truncated run's export is a real export of a real tick, so nothing downstream can tell it apart;
/// this is the only check that can.</summary>
public sealed class TestReconciliationLawTests {
    private static TestReading Reading(ulong exportTick, bool truncated, params TestSubmission[] submissions) => new(
        Echoes: [],
        ExportBytes: [],
        ExportTick: exportTick,
        ManifestBytes: [],
        Submissions: submissions,
        Truncated: truncated,
        Worlds: []
    );
    private static TestSchedule Schedule(ulong exportTick, params TestScheduleRow[] rows) => new(
        ExportTick: exportTick,
        RateHz: 30,
        Rows: rows
    );
    private static TestSubmission Recorded(ulong tick, string command, string outcome = WorldScheduleSection.OutcomeSubmitted, string? detail = null) => new(
        Command: command,
        Detail: detail,
        Outcome: outcome,
        Principal: "seat1",
        Tick: tick
    );
    private static TestScheduleRow Row(ulong tick, string command, WorldScheduleExpectation expect = WorldScheduleExpectation.Submitted, string? refusal = null) => new(
        Command: command,
        Expect: expect,
        Principal: "seat1",
        Refusal: refusal,
        Tick: tick
    );
    private static (TestReconciliationVerdict Verdict, string? Reason) Judge(string name, TestSchedule schedule, TestReading reading) {
        var verdict = TestReconciliation.Judge(
            name: name,
            reading: reading,
            reason: out var reason,
            schedule: schedule
        );

        return (verdict, reason);
    }

    [Fact]
    public void AReachedRunWithEveryRowAccountedForIsAccepted() {
        var (verdict, reason) = Judge(
            name: "w",
            reading: Reading(
                exportTick: 12UL,
                truncated: false,
                Recorded(
                command: "world.state.cell.set counter n 1",
                tick: 3UL
            ),
                Recorded(
                command: "world.state.cell.set counter n 2",
                tick: 6UL
            )
            ),
            schedule: Schedule(
                exportTick: 12UL,
                Row(
                command: "world.state.cell.set counter n 1",
                tick: 3UL
            ),
                Row(
                command: "world.state.cell.set counter n 2",
                tick: 6UL
            )
            )
        );

        Assert.Equal(
            actual: verdict,
            expected: TestReconciliationVerdict.Accepted
        );
        Assert.Null(@object: reason);
    }
    [Fact]
    public void ARunThatStoppedShortOfTheAuthoredExportTickIsRefusedNamingBothTicks() {
        var (verdict, reason) = Judge(
            name: "p-truncate",
            reading: Reading(
                exportTick: 2UL,
                truncated: true,
                Recorded(
                command: "world.state.cell.set counter n 7",
                outcome: WorldScheduleSection.OutcomeUnreached,
                tick: 50UL
            )
            ),
            schedule: Schedule(
                exportTick: 56UL,
                Row(
                command: "world.state.cell.set counter n 7",
                tick: 50UL
            )
            )
        );

        Assert.Equal(
            actual: verdict,
            expected: TestReconciliationVerdict.Unmeasured
        );
        Assert.Contains(
            actualString: reason!,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "p-truncate exported at tick 2, authored 56"
        );
    }
    [Fact]
    public void ADeclaredRowTheRunNeverReachedIsRefusedByNameEvenAtTheAuthoredTick() {
        var (verdict, reason) = Judge(
            name: "w",
            reading: Reading(
                exportTick: 12UL,
                truncated: false,
                Recorded(
                command: "world.state.cell.set counter n 7",
                outcome: WorldScheduleSection.OutcomeUnreached,
                tick: 6UL
            )
            ),
            schedule: Schedule(
                exportTick: 12UL,
                Row(
                command: "world.state.cell.set counter n 7",
                tick: 6UL
            )
            )
        );

        Assert.Equal(
            actual: verdict,
            expected: TestReconciliationVerdict.Unmeasured
        );
        Assert.Contains(
            actualString: reason!,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "schedule.rows[0] at tick 6 was never reached"
        );
    }
    [Fact]
    public void ADeclaredRowMissingFromTheManifestIsRefusedByCount() {
        var (verdict, reason) = Judge(
            name: "w",
            reading: Reading(
                exportTick: 12UL,
                truncated: false,
                Recorded(
                command: "world.state.cell.set counter n 1",
                tick: 3UL
            )
            ),
            schedule: Schedule(
                exportTick: 12UL,
                Row(
                command: "world.state.cell.set counter n 1",
                tick: 3UL
            ),
                Row(
                command: "world.state.cell.set counter n 2",
                tick: 6UL
            )
            )
        );

        Assert.Equal(
            actual: verdict,
            expected: TestReconciliationVerdict.Unmeasured
        );
        Assert.Contains(
            actualString: reason!,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "declares 2 scheduled row(s) and its manifest records 1"
        );
    }
    [Fact]
    public void AManifestEntryThatIsNotTheDeclaredRowAtThatPositionIsRefusedByName() {
        var (verdict, reason) = Judge(
            name: "w",
            reading: Reading(
                exportTick: 12UL,
                truncated: false,
                Recorded(
                command: "world.state.cell.set counter n 9",
                tick: 4UL
            )
            ),
            schedule: Schedule(
                exportTick: 12UL,
                Row(
                command: "world.state.cell.set counter n 1",
                tick: 3UL
            )
            )
        );

        Assert.Equal(
            actual: verdict,
            expected: TestReconciliationVerdict.Unmeasured
        );
        Assert.Contains(
            actualString: reason!,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "schedule.rows[0] declares tick 3 seat1: world.state.cell.set counter n 1, and the manifest's entry at that position is tick 4"
        );
    }
    [Fact]
    public void ARefusalNobodyExpectedFailsTheWorldNamingTheRowTheCommandAndTheOutcome() {
        var (verdict, reason) = Judge(
            name: "w",
            reading: Reading(
                exportTick: 12UL,
                truncated: false,
                Recorded(
                command: "body.press nosuchchannel",
                detail: "[body.press: unknown channel 'nosuchchannel']",
                outcome: WorldScheduleSection.OutcomeRefused,
                tick: 3UL
            )
            ),
            schedule: Schedule(
                exportTick: 12UL,
                Row(
                command: "body.press nosuchchannel",
                tick: 3UL
            )
            )
        );

        Assert.Equal(
            actual: verdict,
            expected: TestReconciliationVerdict.Unexpected
        );
        Assert.Contains(
            actualString: reason!,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "schedule.rows[0] expects 'submitted' and the run recorded 'refused' — [body.press: unknown channel 'nosuchchannel']: body.press nosuchchannel"
        );
    }
    [Fact]
    public void ARefusalTheRowDeclaredIsAcceptedAndOneCarryingOtherTextIsNot() {
        var reading = Reading(
            exportTick: 12UL,
            truncated: false,
            Recorded(
            command: "body.press nosuchchannel",
            detail: "[body.press: unknown channel 'nosuchchannel']",
            outcome: WorldScheduleSection.OutcomeRefused,
            tick: 3UL
        )
        );
        var accepted = Judge(
            name: "w",
            reading: reading,
            schedule: Schedule(
                exportTick: 12UL,
                Row(
                command: "body.press nosuchchannel",
                expect: WorldScheduleExpectation.Refused,
                refusal: "unknown channel",
                tick: 3UL
            )
            )
        );
        var mismatched = Judge(
            name: "w",
            reading: reading,
            schedule: Schedule(
                exportTick: 12UL,
                Row(
                command: "body.press nosuchchannel",
                expect: WorldScheduleExpectation.Refused,
                refusal: "no grant names it",
                tick: 3UL
            )
            )
        );

        Assert.Equal(
            actual: accepted.Verdict,
            expected: TestReconciliationVerdict.Accepted
        );
        Assert.Equal(
            actual: mismatched.Verdict,
            expected: TestReconciliationVerdict.Unexpected
        );
        Assert.Contains(
            actualString: mismatched.Reason!,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "expects a refusal carrying 'no grant names it'"
        );
    }
    [Fact]
    public void ASubmissionTheRowExpectedToBeRefusedFailsTheWorld() {
        var (verdict, reason) = Judge(
            name: "w",
            reading: Reading(
                exportTick: 12UL,
                truncated: false,
                Recorded(
                command: "world.state.cell.set counter n 1",
                tick: 3UL
            )
            ),
            schedule: Schedule(
                exportTick: 12UL,
                Row(
                command: "world.state.cell.set counter n 1",
                expect: WorldScheduleExpectation.Refused,
                tick: 3UL
            )
            )
        );

        Assert.Equal(
            actual: verdict,
            expected: TestReconciliationVerdict.Unexpected
        );
        Assert.Contains(
            actualString: reason!,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "expects 'refused' and the run recorded 'submitted'"
        );
    }
    [InlineData(WorldScheduleSection.OutcomeFaulted, "crashed its handler")]
    [InlineData(WorldScheduleSection.OutcomePending, "was still awaiting its answer")]
    [InlineData(WorldScheduleSection.OutcomeUnroutable, "had no ingress")]
    [Theory]
    public void AnOutcomeTheHostRatherThanTheWorldAnsweredIsUnmeasuredWhateverTheRowExpected(string outcome, string named) {
        foreach (var expect in new[] { WorldScheduleExpectation.Submitted, WorldScheduleExpectation.Refused }) {
            var (verdict, reason) = Judge(
                name: "w",
                reading: Reading(
                    exportTick: 12UL,
                    truncated: false,
                    Recorded(
                    command: "body.press jump",
                    detail: "[body.press: handler threw InvalidOperationException: broke]",
                    outcome: outcome,
                    tick: 3UL
                )
                ),
                schedule: Schedule(
                    exportTick: 12UL,
                    Row(
                    command: "body.press jump",
                    expect: expect,
                    tick: 3UL
                )
                )
            );

            Assert.Equal(
                actual: verdict,
                expected: TestReconciliationVerdict.Unmeasured
            );
            Assert.Contains(
                actualString: reason!,
                comparisonType: StringComparison.Ordinal,
                expectedSubstring: named
            );
        }
    }
    [Fact]
    public void ARefusalThatNeverReachedAHandlerIsUnmeasuredEvenWhenTheRowExpectedARefusal() {
        var (verdict, reason) = Judge(
            name: "w",
            reading: Reading(
                exportTick: 12UL,
                truncated: false,
                Recorded(
                command: "world.state.cell.set counter n 1",
                detail: "[wire.reject: unknown command 'world.state.cel.set' — run `help` for the registered verbs]",
                outcome: WorldScheduleSection.OutcomeRefused,
                tick: 3UL
            )
            ),
            schedule: Schedule(
                exportTick: 12UL,
                Row(
                command: "world.state.cell.set counter n 1",
                expect: WorldScheduleExpectation.Refused,
                tick: 3UL
            )
            )
        );

        Assert.Equal(
            actual: verdict,
            expected: TestReconciliationVerdict.Unmeasured
        );
        Assert.Contains(
            actualString: reason!,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "never reached a handler"
        );
    }
}
