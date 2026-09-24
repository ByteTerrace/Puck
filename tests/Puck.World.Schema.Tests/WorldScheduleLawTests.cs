using System.Text;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>CONTRACT UNDER TEST: the <c>schedule</c> section — the tick-scheduled command rows a test world's
/// <c>when</c> clause lowers to. Its refusals are by name (tick 0, a descending tick, a principal that is not a
/// seat the wire admits, a command outside the scheduled-step vocabulary, a blank or multi-line or commented
/// command, a zero settle margin, and each capacity ceiling), its export tick is the last scheduled tick plus the
/// settle margin, and a document declaring no schedule round-trips with no <c>schedule</c> member at all.</summary>
public sealed class WorldScheduleLawTests {
    private static WorldDefinition BuildDefinition(WorldScheduleSection? schedule) => new(
        Schedule: schedule,
        Simulation: new WorldSimulationDefaults(RateHz: 240)
    );
    private static WorldScheduleRow Row(ulong tick, string principal = "seat1", string command = "world.state.cell.set counter n 1", WorldScheduleExpectation expect = WorldScheduleExpectation.Submitted, string? refusal = null) => new(
        Command: command,
        Expect: expect,
        Principal: principal,
        Refusal: refusal,
        Tick: tick
    );
    private static WorldScheduleSection Schedule(int settleTicks = 4, params WorldScheduleRow[] rows) => new(
        Rows: rows,
        SettleTicks: settleTicks
    );
    private static string Validate(WorldDefinition definition) =>
        (WorldDefinitionValidator.TryValidateLocally(
            definition: definition,
            reason: out var reason
        )
            ? string.Empty
            : reason
        );

    [Fact]
    public void AnAbsentScheduleLeavesTheDocumentWithNoScheduleMember() {
        var definition = BuildDefinition(schedule: null);

        Assert.Equal(
            actual: Validate(definition: definition),
            expected: string.Empty
        );
        Assert.Null(@object: definition.Schedule);
        Assert.DoesNotContain(
            actualString: Encoding.UTF8.GetString(bytes: WorldDefinitionSerialization.Serialize(definition: definition)),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "\"schedule\""
        );
    }
    [Fact]
    public void ABlankOrMultiLineOrCommentedCommandIsRefusedByName() {
        Assert.Contains(
            actualString: Validate(definition: BuildDefinition(schedule: Schedule(
                rows: [Row(
                    command: "   ",
                    tick: 3UL
                )]
            ))),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "schedule.rows[0].command is required"
        );
        Assert.Contains(
            actualString: Validate(definition: BuildDefinition(schedule: Schedule(
                rows: [Row(
                    command: "world.status\nworld.grants console",
                    tick: 3UL
                )]
            ))),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "carries a line break"
        );
        Assert.Contains(
            actualString: Validate(definition: BuildDefinition(schedule: Schedule(
                rows: [Row(
                    command: "# not a command",
                    tick: 3UL
                )]
            ))),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "is a '#' comment"
        );
    }
    [Fact]
    public void ACommandPastTheLengthCeilingIsRefusedByName() {
        var line = ("world.chat.say " + new string(
            c: 'x',
            count: WorldScheduleCapacity.MaxCommandLength
        ));

        Assert.Contains(
            actualString: Validate(definition: BuildDefinition(schedule: Schedule(
                rows: [Row(
                    command: line,
                    tick: 3UL
                )]
            ))),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: $"exceeds {WorldScheduleCapacity.MaxCommandLength}"
        );
    }
    [Fact]
    public void ADescendingTickIsRefusedByNameAndASharedTickIsAdmitted() {
        Assert.Contains(
            actualString: Validate(definition: BuildDefinition(schedule: Schedule(
                rows: [Row(tick: 9UL), Row(tick: 4UL)]
            ))),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "is earlier than the preceding row's 9"
        );
        Assert.Equal(
            actual: Validate(definition: BuildDefinition(schedule: Schedule(
                rows: [Row(tick: 4UL), Row(tick: 4UL)]
            ))),
            expected: string.Empty
        );
    }
    [Fact]
    public void ARowCountPastTheCeilingIsRefusedByName() {
        var rows = new WorldScheduleRow[(WorldScheduleCapacity.MaxRows + 1)];

        for (var index = 0; (index < rows.Length); index++) {
            rows[index] = Row(tick: 1UL);
        }

        Assert.Contains(
            actualString: Validate(definition: BuildDefinition(schedule: Schedule(rows: rows))),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: $"schedule.rows count {rows.Length} exceeds {WorldScheduleCapacity.MaxRows}"
        );
    }
    [Fact]
    public void ASettleMarginBelowOneOrPastTheCeilingIsRefusedByName() {
        Assert.Contains(
            actualString: Validate(definition: BuildDefinition(schedule: Schedule(
                rows: [Row(tick: 2UL)],
                settleTicks: 0
            ))),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "schedule.settleTicks 0 must be at least 1"
        );
        Assert.Contains(
            actualString: Validate(definition: BuildDefinition(schedule: Schedule(
                rows: [Row(tick: 2UL)],
                settleTicks: (WorldScheduleCapacity.MaxSettleTicks + 1)
            ))),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: $"exceeds {WorldScheduleCapacity.MaxSettleTicks}"
        );
    }
    [Fact]
    public void AnEmptyRowSetStillExportsAtTheSettleMargin() {
        var schedule = Schedule(settleTicks: 7);

        Assert.Equal(
            actual: Validate(definition: BuildDefinition(schedule: schedule)),
            expected: string.Empty
        );
        Assert.Equal(
            actual: schedule.LastScheduledTick,
            expected: 0UL
        );
        Assert.Equal(
            actual: schedule.ExportTick,
            expected: 7UL
        );
    }
    [Fact]
    public void TheExportTickIsTheLastScheduledTickPlusTheSettleMargin() {
        var schedule = Schedule(
            rows: [Row(tick: 4UL), Row(tick: 30UL)],
            settleTicks: 12
        );

        Assert.Equal(
            actual: schedule.LastScheduledTick,
            expected: 30UL
        );
        Assert.Equal(
            actual: schedule.ExportTick,
            expected: 42UL
        );
    }
    [Fact]
    public void TickZeroIsRefusedByName() {
        Assert.Contains(
            actualString: Validate(definition: BuildDefinition(schedule: Schedule(rows: [Row(tick: 0UL)]))),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "schedule.rows[0].tick is 0"
        );
    }
    [InlineData("seat1")]
    [InlineData("seat4")]
    [Theory]
    public void AnAdmittedPrincipalLabelValidates(string principal) {
        Assert.Equal(
            actual: Validate(definition: BuildDefinition(schedule: Schedule(
                rows: [Row(
                    principal: principal,
                    tick: 2UL
                )]
            ))),
            expected: string.Empty
        );
    }
    [InlineData("peer:2:1", "carries a live admission generation")]
    [InlineData("addon:mirror", "has no text ingress door of its own")]
    [InlineData("seat0", "is not a principal label")]
    [InlineData("seat", "is not a principal label")]
    [InlineData("seat-1", "is not a principal label")]
    [InlineData("world", "names a World principal, which has no text ingress door")]
    [InlineData("", "is required")]
    [Theory]
    public void AnUnadmittedPrincipalLabelIsRefusedByName(string principal, string expected) {
        Assert.Contains(
            actualString: Validate(definition: BuildDefinition(schedule: Schedule(
                rows: [Row(
                    principal: principal,
                    tick: 2UL
                )]
            ))),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: expected
        );
    }
    // The label's own grammar is the wire's, so its case-insensitive spellings are refused for the same reason.
    [Theory]
    [InlineData("console")]
    [InlineData("Console")]
    public void TheConsolePrincipalIsRefusedByName(string principal) {
        Assert.Contains(
            actualString: Validate(definition: BuildDefinition(schedule: Schedule(
                rows: [Row(
                    principal: principal,
                    tick: 2UL
                )]
            ))),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "names the console, which is trusted at every gate"
        );
    }
    // The validator and the wire codec must agree about the seat ceiling: a seat the document admits and the wire
    // discards would be recorded as submitted and never reach the world.
    [Fact]
    public void ASeatPastTheWireCeilingIsRefusedByName() {
        Assert.Contains(
            actualString: Validate(definition: BuildDefinition(schedule: Schedule(
                rows: [Row(
                    principal: $"seat{(WorldBodiesLimits.LocalSeatCount + 1)}",
                    tick: 2UL
                )]
            ))),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "is not a principal label"
        );
        Assert.False(condition: PrincipalTokens.TryParse(
            principal: out _,
            token: $"seat{(WorldBodiesLimits.LocalSeatCount + 1)}"
        ));
    }
    [InlineData("world.state.cell.set counter n 1")]
    [InlineData("world.state.transform {\"$type\":\"observe\"}")]
    [InlineData("world.state.act phase 0 {\"$type\":\"observe\"}")]
    [InlineData("body.pose 1 0 1 90 0 0 0")]
    [InlineData("player.join 2")]
    [InlineData("player.leave 2")]
    [Theory]
    public void AScheduledStepCommandValidates(string command) {
        Assert.Equal(
            actual: Validate(definition: BuildDefinition(schedule: Schedule(
                rows: [Row(
                    command: command,
                    tick: 2UL
                )]
            ))),
            expected: string.Empty
        );
    }
    [InlineData("world.rate pause")]
    [InlineData("world.save out/snapshot.world.json")]
    [InlineData("world.load out/snapshot.world.json")]
    [InlineData("world.reload")]
    [InlineData("quit")]
    [InlineData("world.grant seat1 mutate section:state")]
    [InlineData("world.revoke seat1 mutate section:state")]
    [InlineData("world.capture")]
    [InlineData("world.screenshot")]
    [InlineData("world.control start")]
    [InlineData("replay.record on")]
    [InlineData("world.state.cel.set counter n 7")]
    [Theory]
    public void ACommandOutsideTheScheduledStepVocabularyIsRefusedByNameWithItsRowIndex(string command) {
        var reason = Validate(definition: BuildDefinition(schedule: Schedule(
            rows: [Row(tick: 2UL), Row(
                command: command,
                tick: 4UL
            )]
        )));

        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: $"schedule.rows[1].command names '{WorldScheduleCommands.LeadingVerb(command: command)}', which is not a scheduled step"
        );
    }
    [Fact]
    public void TheAdmittedStepVocabularyIsTheOneRecordedHere() {
        Assert.Equal(
            actual: string.Join(
                separator: " ",
                values: WorldScheduleCommands.Reads
            ),
            expected: "world.hud.template world.match world.observe world.row world.state world.state.observe world.state.similar world.tabletop"
        );
        Assert.Equal(
            actual: WorldScheduleCommands.Admitted,
            expected: [.. WorldScheduleCommands.Steps.Concat(second: WorldScheduleCommands.Reads).Order(comparer: StringComparer.Ordinal)]
        );
        Assert.Equal(
            actual: string.Join(
                separator: " ",
                values: WorldScheduleCommands.Steps
            ),
            // KEEP IN SYNC with tests/Puck.Cli.Tests' ScheduledStepVocabularyLawTests, which ties this set to the
            // live registry rather than to another copy of the list.
            expected: "body.carry body.control body.disengage body.engage body.fly body.impulse body.motion body.pose body.press body.release body.state-load body.stop player.identity player.join player.leave player.row.set player.state.cell.set player.state.cell.toggle world.row.add world.row.remove world.row.set world.row.step world.state.act world.state.cell.remove world.state.cell.set world.state.transform"
        );
    }
    [Fact]
    public void AnExpectedRefusalsTextIsLegitimateOnlyBesideARowThatExpectsARefusal() {
        Assert.Contains(
            actualString: Validate(definition: BuildDefinition(schedule: Schedule(
                rows: [Row(
                    refusal: "no grant names it",
                    tick: 3UL
                )]
            ))),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "schedule.rows[0].refusal names text beside expect 'Submitted'"
        );
        Assert.Contains(
            actualString: Validate(definition: BuildDefinition(schedule: Schedule(
                rows: [Row(
                    expect: WorldScheduleExpectation.Refused,
                    refusal: "  ",
                    tick: 3UL
                )]
            ))),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "schedule.rows[0].refusal is blank"
        );
        Assert.Contains(
            actualString: Validate(definition: BuildDefinition(schedule: Schedule(
                rows: [Row(
                    expect: WorldScheduleExpectation.Refused,
                    refusal: new string(
                    c: 'r',
                    count: (WorldScheduleCapacity.MaxCommandLength + 1)
                ),
                    tick: 3UL
                )]
            ))),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: $"exceeds {WorldScheduleCapacity.MaxCommandLength}"
        );
        Assert.Equal(
            actual: Validate(definition: BuildDefinition(schedule: Schedule(
                rows: [Row(
                    expect: WorldScheduleExpectation.Refused,
                    refusal: "no grant names it",
                    tick: 3UL
                )]
            ))),
            expected: string.Empty
        );
    }
    [Fact]
    public void ARowExpectsASubmissionUnlessItSaysOtherwise() {
        var row = Row(tick: 3UL);

        Assert.Equal(
            actual: row.Expect,
            expected: WorldScheduleExpectation.Submitted
        );
        Assert.Null(@object: row.Refusal);
        Assert.DoesNotContain(
            actualString: Encoding.UTF8.GetString(bytes: WorldDefinitionSerialization.Serialize(definition: BuildDefinition(schedule: Schedule(rows: [row])))),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "\"refusal\""
        );
    }
}
