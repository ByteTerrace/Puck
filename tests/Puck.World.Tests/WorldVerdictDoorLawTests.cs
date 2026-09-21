using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

/// <summary>CONTRACT UNDER TEST: only a rule's own effect writes a verdict row. The effect door stamps the firing's
/// tick into the row's reserved <c>$firedTick</c> cell, so a status that moved without one moved through another
/// door; a failing verdict is sticky, so the export carries the first failing tick and the values the gate saw
/// then; and the cell-mutation door refuses the write by the one text the boot loader refuses an authored pass
/// with.</summary>
[Collection(name: ConsoleRedirectionCollection.Name)]
public sealed class WorldVerdictDoorLawTests {
    private const string Counter = "counter";
    private const string Flag = "flag";
    private const string Verdict = "answer";
    private const string Witness = "answerSeen";

    private static WorldDefinition Document(long passWhen, long failWhen) => (Fixtures.BuildDocument().WithWorldState(rows: [
        new WorldStateRow(
            Name: CellName.Parse(candidate: Counter),
            Kind: CellKind.Int,
            Cells: [new StateCell(
                    Key: CellName.Parse(candidate: "n"),
                    Value: CellValue.Int(value: 0L)
                )]
        ),
        new WorldStateRow(
            Name: CellName.Parse(candidate: Verdict),
            Kind: CellKind.Int,
            Cells: [
                new StateCell(
                    Key: CellName.Parse(candidate: "ok"),
                    Value: CellValue.Int(value: 0L)
                ),
                new StateCell(
                    Key: CellName.Parse(candidate: "saw"),
                    Value: CellValue.Int(value: 0L)
                ),
            ],
            Verdict: new WorldVerdictTrait(
                Gate: "the counter reached its mark",
                Status: CellName.Parse(candidate: "ok")
            )
        ),
        new WorldStateRow(
            Name: CellName.Parse(candidate: Flag),
            Kind: CellKind.Bool,
            Cells: [new StateCell(
                    Key: CellName.Parse(candidate: "f"),
                    Value: CellValue.Bool(value: true)
                )]
        ),
        new WorldStateRow(
            Name: CellName.Parse(candidate: Witness),
            Kind: CellKind.Bool,
            Cells: [new StateCell(
                    Key: CellName.Parse(candidate: "f"),
                    Value: CellValue.Bool(value: false)
                )],
            Witness: CellName.Parse(candidate: Verdict)
        ),
    ]) with {
        Rules = [
            Decide(
                name: "verdict-fails",
                status: WorldVerdict.Fail,
                when: failWhen
            ),
            Decide(
                name: "verdict-passes",
                status: WorldVerdict.Pass,
                when: passWhen
            ),
        ],
    });
    private static WorldRule Decide(string name, long when, long status) => new(
        Name: CellName.Parse(candidate: name),
        Gate: new ActionPredicate.CompareState(
            Comparison: ActionStateComparison.Equal,
            Key: "n",
            State: Counter,
            Value: when
        ),
        Mode: ActionTriggerMode.Level,
        Effects: [
            new ActionEffect.SetState(
                Key: "ok",
                State: Verdict,
                Value: status
            ),
            new ActionEffect.SetState(
                FromKey: "n",
                FromState: Counter,
                Key: "saw",
                State: Verdict
            ),
            new ActionEffect.SetState(
                FromKey: "f",
                FromState: Flag,
                Key: "f",
                State: Witness
            ),
        ]
    );
    private static long? Seen(WorldFixture fixture) => StateRows.FindCell(
        cells: WorldDefinitionRows.FindStateRow(
            rows: fixture.Server.Definition.State,
            name: Witness
        )!.Cells,
        key: CellName.Parse(candidate: "f")
    )?.Value.Raw;
    private static void Lower(WorldFixture fixture) => fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(
        Key: "f",
        Kind: WorldDocumentWriteKind.Set,
        Principal: WorldPrincipal.Console,
        Row: Flag,
        Value: 0L
    ));
    private static long? Cell(WorldFixture fixture, string key) => StateRows.FindCell(
        cells: WorldDefinitionRows.FindStateRow(
            rows: fixture.Server.Definition.State,
            name: Verdict
        )!.Cells,
        key: CellName.Parse(candidate: key)
    )?.Value.Raw;
    private static void Set(WorldFixture fixture, long value) => fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(
        Key: "n",
        Kind: WorldDocumentWriteKind.Set,
        Principal: WorldPrincipal.Console,
        Row: Counter,
        Value: value
    ));

    [Fact]
    public void AVerdictNoRuleWroteCarriesNoFiringStampAndAFiringStampsItsOwnTick() {
        using var fixture = Fixtures.FreshServer(definition: Document(
            failWhen: 9L,
            passWhen: 5L
        ));

        fixture.Step();
        fixture.Step();

        Assert.Null(value: Cell(
            fixture: fixture,
            key: WorldVerdict.FiredTickKey.Value
        ));
        Assert.Equal(
            actual: Cell(
                fixture: fixture,
                key: "ok"
            ),
            expected: WorldVerdict.NotEvaluated
        );

        Set(
            fixture: fixture,
            value: 5L
        );
        fixture.Step();
        fixture.Step();

        var stamp = Cell(
            fixture: fixture,
            key: WorldVerdict.FiredTickKey.Value
        );

        Assert.Equal(
            actual: Cell(
                fixture: fixture,
                key: "ok"
            ),
            expected: WorldVerdict.Pass
        );
        Assert.Equal(
            actual: Cell(
                fixture: fixture,
                key: "saw"
            ),
            expected: 5L
        );
        Assert.NotNull(value: stamp);
        Assert.True(
            condition: (stamp!.Value > 0L),
            userMessage: "a firing stamped tick 0, which reads as never evaluated"
        );
        Assert.True(
            condition: (((ulong)stamp.Value) < fixture.Server.NextInputTick),
            userMessage: "the stamp names a tick the run has not completed"
        );
    }
    [Fact]
    public void AVerdictThatEverFiredFailStaysFailedWithTheValuesItsGateSawThen() {
        using var fixture = Fixtures.FreshServer(definition: Document(
            failWhen: 3L,
            passWhen: 7L
        ));

        Set(
            fixture: fixture,
            value: 3L
        );
        fixture.Step();
        fixture.Step();

        var settled = Cell(
            fixture: fixture,
            key: WorldVerdict.FiredTickKey.Value
        );

        Assert.Equal(
            actual: Cell(
                fixture: fixture,
                key: "ok"
            ),
            expected: WorldVerdict.Fail
        );
        Assert.NotNull(value: settled);
        Assert.Equal(
            actual: Seen(fixture: fixture),
            expected: 1L
        );

        Lower(fixture: fixture);
        Set(
            fixture: fixture,
            value: 7L
        );
        fixture.Step();
        fixture.Step();
        fixture.Step();

        Assert.Equal(
            actual: Seen(fixture: fixture),
            expected: 1L
        );

        // The control: the pass rule's gate is open now, and a verdict that had NOT failed would read Pass here.
        Assert.Equal(
            actual: Cell(
                fixture: fixture,
                key: "ok"
            ),
            expected: WorldVerdict.Fail
        );
        Assert.Equal(
            actual: Cell(
                fixture: fixture,
                key: "saw"
            ),
            expected: 3L
        );
        Assert.Equal(
            actual: Cell(
                fixture: fixture,
                key: WorldVerdict.FiredTickKey.Value
            ),
            expected: settled
        );
    }
    [Fact]
    public void AVerdictThatNeverFailedReachesPassOnTheSameSchedule() {
        using var fixture = Fixtures.FreshServer(definition: Document(
            failWhen: 99L,
            passWhen: 7L
        ));

        Set(
            fixture: fixture,
            value: 3L
        );
        fixture.Step();
        fixture.Step();
        Lower(fixture: fixture);
        Set(
            fixture: fixture,
            value: 7L
        );
        fixture.Step();
        fixture.Step();
        fixture.Step();

        Assert.Equal(
            actual: Cell(
                fixture: fixture,
                key: "ok"
            ),
            expected: WorldVerdict.Pass
        );
        Assert.Equal(
            actual: Seen(fixture: fixture),
            expected: 0L
        );
    }
    [Fact]
    public void TheCellMutationDoorRefusesAWriteToAWitnessByName() {
        var previous = Console.Error;
        var captured = new StringWriter();

        try {
            Console.SetError(newError: captured);

            using var fixture = Fixtures.FreshServer(definition: Document(
                failWhen: 9L,
                passWhen: 5L
            ));

            fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(
                Key: "f",
                Kind: WorldDocumentWriteKind.Set,
                Principal: WorldPrincipal.Console,
                Row: Witness,
                Value: 1L
            ));
            fixture.Step();
            fixture.Step();

            Assert.Equal(
                actual: Seen(fixture: fixture),
                expected: 0L
            );
        } finally {
            Console.SetError(newError: previous);
        }

        Assert.Contains(
            actualString: captured.ToString(),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: $"'{Witness}' is a witness of the verdict '{Verdict}'"
        );
    }
    [Fact]
    public void TheCellMutationDoorRefusesAWriteToAVerdictRowByName() {
        var previous = Console.Error;
        var captured = new StringWriter();

        try {
            Console.SetError(newError: captured);

            using var fixture = Fixtures.FreshServer(definition: Document(
                failWhen: 9L,
                passWhen: 5L
            ));

            fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(
                Key: "ok",
                Kind: WorldDocumentWriteKind.Set,
                Principal: WorldPrincipal.Console,
                Row: Verdict,
                Value: WorldVerdict.Pass
            ));
            fixture.Step();
            fixture.Step();

            Assert.Equal(
                actual: Cell(
                    fixture: fixture,
                    key: "ok"
                ),
                expected: WorldVerdict.NotEvaluated
            );
            Assert.Null(value: Cell(
                fixture: fixture,
                key: WorldVerdict.FiredTickKey.Value
            ));
        } finally {
            Console.SetError(newError: previous);
        }

        Assert.Contains(
            actualString: captured.ToString(),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: WorldVerdict.RefuseWrite(row: new WorldStateRow(
                Name: CellName.Parse(candidate: Verdict),
                Kind: CellKind.Int,
                Verdict: new WorldVerdictTrait(
                    Gate: "the counter reached its mark",
                    Status: CellName.Parse(candidate: "ok")
                )
            ))
        );
    }
}
