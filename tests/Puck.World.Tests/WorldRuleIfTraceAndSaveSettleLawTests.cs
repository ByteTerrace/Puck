using Xunit;

using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>A world rule's conditional <see cref="ActionEffect.If"/> fires the branch its condition selects and
/// <c>world.rule.trace</c> names which one; a cell whose value-over-time clock is rebased to the tick a save would
/// settle it at (epoch zero, the live sampled value as the new base — the shape <c>world.save</c> writes, exercised
/// here through the server-level primitives this project can reach) round-trips through serialize/deserialize and
/// then continues its live trajectory identically to an uninterrupted session.</summary>
public sealed class WorldRuleIfTraceAndSaveSettleLawTests {
    private static readonly WorldPrincipal Actor = WorldPrincipal.Seat(slot: 0);

    private static WorldDefinition Document() {
        var flag = new WorldStateRow(
            Name: CellName.Parse(candidate: "flag"),
            Kind: CellKind.Int,
            Cells: [new StateCell(
                    Key: WorldStateRow.SlotKey,
                    Value: 0
                )]
        );
        var always = new WorldStateRow(
            Name: CellName.Parse(candidate: "always"),
            Kind: CellKind.Int,
            Cells: [new StateCell(
                    Key: WorldStateRow.SlotKey,
                    Value: 1
                )]
        );
        var a = new WorldStateRow(
            Name: CellName.Parse(candidate: "a"),
            Kind: CellKind.Int,
            Cells: [new StateCell(
                    Key: WorldStateRow.SlotKey,
                    Value: 0
                )]
        );
        var b = new WorldStateRow(
            Name: CellName.Parse(candidate: "b"),
            Kind: CellKind.Int,
            Cells: [new StateCell(
                    Key: WorldStateRow.SlotKey,
                    Value: 0
                )]
        );

        return Fixtures.BuildDocument().WithWorldState(rows: [flag, always, a, b]) with {
            Rules = [new WorldRule(
                Name: CellName.Parse(candidate: "branch"),
                Gate: new ActionPredicate.CompareState(
                    State: "always",
                    Comparison: ActionStateComparison.Equal,
                    Value: 1m
                ),
                Mode: ActionTriggerMode.Level,
                Effects: [new ActionEffect.If(
                        Condition: new ActionPredicate.CompareState(
                            State: "flag",
                            Comparison: ActionStateComparison.Equal,
                            Value: 1m
                        ),
                        Then: [new ActionEffect.SetState(
                                State: "a",
                                Value: 1m
                            )],
                        Else: [new ActionEffect.SetState(
                                State: "b",
                                Value: 1m
                            )]
                    )]
            )],
        };
    }
    private static long Value(WorldFixture fixture, string row) =>
        WorldDefinitionRows.FindStateRow(
            rows: fixture.Server.Definition.State,
            name: row
        )!.Cells!.Single().Value;

    [Fact]
    public void TheUnmetBranchFiresElseAndTheMetBranchFiresThenAndBothNameTheChoiceInTheTrace() {
        using var fixture = Fixtures.FreshServer(definition: Document());

        Assert.True(
            condition: fixture.Server.TryArmRuleTrace(
                evaluations: 2,
                refusal: out var refusal,
                rule: "branch"
            ),
            userMessage: refusal
        );

        // Tick 1: flag is 0 — the condition fails, so 'else' fires and 'a' stays untouched.
        fixture.Step();

        Assert.Equal(
            expected: 0L,
            actual: Value(
                fixture: fixture,
                row: "a"
            )
        );
        Assert.Equal(
            expected: 1L,
            actual: Value(
                fixture: fixture,
                row: "b"
            )
        );

        // Tick 2: flag flips to 1 — the condition holds, so 'then' fires and 'b' is left as 'else' set it.
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(
            Principal: Actor,
            Row: "flag",
            Key: WorldStateRow.SlotKey.Value,
            Value: 1,
            Kind: WorldDocumentWriteKind.Set
        ));
        fixture.Step();

        Assert.Equal(
            expected: 1L,
            actual: Value(
                fixture: fixture,
                row: "a"
            )
        );

        var lines = fixture.Server.DescribeRuleTrace().Split(separator: Environment.NewLine);

        Assert.Equal(
            expected: 3,
            actual: lines.Length
        ); // header + 2 captured evaluations.
        Assert.Contains(
            actualString: lines[1],
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "= else: applied"
        );
        Assert.Contains(
            actualString: lines[2],
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "= then: applied"
        );
    }
    [Fact]
    public void AFaultingConditionRunsNeitherBranchAndIsReportedRatherThanTreatedAsFalse() {
        var flag = new WorldStateRow(
            Name: CellName.Parse(candidate: "flag"),
            Kind: CellKind.Int,
            Cells: [new StateCell(
                    Key: WorldStateRow.SlotKey,
                    Value: 0
                )]
        );
        var always = new WorldStateRow(
            Name: CellName.Parse(candidate: "always"),
            Kind: CellKind.Int,
            Cells: [new StateCell(
                    Key: WorldStateRow.SlotKey,
                    Value: 1
                )]
        );
        var a = new WorldStateRow(
            Name: CellName.Parse(candidate: "a"),
            Kind: CellKind.Int,
            Cells: [new StateCell(
                    Key: WorldStateRow.SlotKey,
                    Value: 5
                )]
        );

        using var fixture = Fixtures.FreshServer(definition: Fixtures.BuildDocument().WithWorldState(rows: [flag, always, a]) with {
            Rules = [new WorldRule(
                Name: CellName.Parse(candidate: "branch"),
                Gate: new ActionPredicate.CompareState(
                    State: "always",
                    Comparison: ActionStateComparison.Equal,
                    Value: 1m
                ),
                Mode: ActionTriggerMode.Level,
                Effects: [new ActionEffect.If(
                        // 1 / flag with flag == 0 faults the condition (division by zero) at runtime rather than
                        // reading as false, so this must run NEITHER branch.
                        Condition: new ActionPredicate.CompareValue(
                            Kind: CellKind.Int,
                            Left: new ValueExpression(Tokens: [
                                new ValueToken.Constant(Value: 1m),
                                new ValueToken.State(Name: "flag"),
                                new ValueToken.Divide(),
                            ]),
                            Comparison: ActionStateComparison.Equal,
                            Right: new ValueExpression(Tokens: [new ValueToken.Constant(Value: 1m)])
                        ),
                        Then: [new ActionEffect.SetState(
                                State: "a",
                                Value: 100m
                            )],
                        Else: [new ActionEffect.SetState(
                                State: "a",
                                Value: 200m
                            )]
                    )]
            )],
        });

        fixture.Step();

        Assert.Equal(
            expected: 5L,
            actual: Value(
                fixture: fixture,
                row: "a"
            )
        );
        Assert.NotEmpty(collection: fixture.Server.RuleRuntimeDiagnostics());
    }
    [Fact]
    public void ASavedRowsSettledClockReloadsToTheSameLiveValueAndContinuesIdenticallyToAnUninterruptedSession() {
        var row = new WorldStateRow(
            Name: CellName.Parse(candidate: "gauge"),
            Kind: CellKind.Int,
            // Exactly 1 unit per engine tick — no fractional accumulation, so settling to the nearest displayed
            // unit loses nothing and the post-reload trajectory matches the uninterrupted one exactly rather than
            // merely up to the rate's own rounding.
            Advance: new StateAdvance(
                PerSecondNumerator: unchecked((long)Puck.Hosting.EngineTicks.PerSecond),
                PerSecondDenominator: 1
            ),
            Cells: [new StateCell(
                    Key: WorldStateRow.SlotKey,
                    Value: 0
                )]
        );
        using var live = Fixtures.FreshServer(definition: Fixtures.BuildDocument().WithWorldState(rows: [row]));

        for (var index = 0; (index < 5); index++) {
            live.Step();
        }

        var engineTickAtSave = live.Server.CompletedEngineTicks;

        Assert.True(condition: WorldStateReader.TryRead(
            definition: live.Server.Definition,
            engineTick: engineTickAtSave,
            key: WorldStateRow.SlotKey.Value,
            rawValue: out var liveValueAtSave,
            row: out _,
            rowName: "gauge",
            text: out _,
            tick: (live.Server.NextInputTick - 1UL)
        ));

        // The settle a world.save performs: the stored base becomes the live sampled value and the clock's engine
        // epoch returns to zero, so the same value reads back at engine tick zero after a fresh reload.
        var settledRow = WorldDefinitionRows.FindStateRow(
            rows: live.Server.Definition.State,
            name: "gauge"
        )! with {
            Cells = [new StateCell(
                    Key: WorldStateRow.SlotKey,
                    Value: liveValueAtSave!.Value
                )],
        };
        var savedDocument = live.Server.Definition.WithWorldState(rows: [settledRow]);
        var reloadedBytes = WorldDefinitionSerialization.Serialize(definition: savedDocument);
        var reloadedDefinition = WorldDefinitionSerialization.Deserialize(utf8Json: reloadedBytes);

        using var machines = new WorldMachineHost(
            engines: [],
            screens: reloadedDefinition.Screens
        );
        var population = new WorldPopulation(definition: reloadedDefinition);
        var reloaded = new WorldServer(
            definition: reloadedDefinition,
            envelope: new WorldRenderEnvelope(),
            machines: machines,
            narrationSink: new WorldConsoleNarrationSink(),
            population: population,
            profiles: new WorldOwnedWorlds(
                directory: Directory.CreateTempSubdirectory(prefix: "puck-save-settle-tests-").FullName,
                machineId: Guid.NewGuid(),
                template: reloadedDefinition
            )
        );

        Assert.True(condition: WorldStateReader.TryRead(
            definition: reloaded.Definition,
            engineTick: 0UL,
            key: WorldStateRow.SlotKey.Value,
            rawValue: out var reloadedValueAtBoot,
            row: out _,
            rowName: "gauge",
            text: out _,
            tick: 0UL
        ));

        Assert.Equal(
            expected: liveValueAtSave,
            actual: reloadedValueAtBoot
        );

        // Continuing both sessions by the same real engine-tick width from here on reproduces the identical
        // trajectory: the settle is a re-basing of the epoch, never a discontinuity in the accumulated value.
        for (var index = 0; (index < 5); index++) {
            live.Step();
            reloaded.Advance(stepTicks: Fixtures.StepTicks);
        }

        Assert.True(condition: WorldStateReader.TryRead(
            definition: live.Server.Definition,
            engineTick: live.Server.CompletedEngineTicks,
            key: WorldStateRow.SlotKey.Value,
            rawValue: out var liveValueLater,
            row: out _,
            rowName: "gauge",
            text: out _,
            tick: (live.Server.NextInputTick - 1UL)
        ));
        Assert.True(condition: WorldStateReader.TryRead(
            definition: reloaded.Definition,
            engineTick: reloaded.CompletedEngineTicks,
            key: WorldStateRow.SlotKey.Value,
            rawValue: out var reloadedValueLater,
            row: out _,
            rowName: "gauge",
            text: out _,
            tick: (reloaded.NextInputTick - 1UL)
        ));
        Assert.Equal(
            expected: liveValueLater,
            actual: reloadedValueLater
        );
    }
}
