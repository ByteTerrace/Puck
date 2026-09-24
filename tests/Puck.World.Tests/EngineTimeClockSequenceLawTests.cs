using Puck.Commands;
using Xunit;

using Puck.Testing;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>An <c>advance</c> cell's epoch is an engine-tick coordinate that a live simulation-rate change moves no
/// epoch of and that undo and checkpoint restore rebase from exactly (ENGINE CONTRACT C): writing an advancing
/// cell, stepping under a DIFFERENT engine-tick step width (the observable shape of a live rate change — engine
/// time keeps accruing contiguously regardless of the width), writing again, undoing both writes, and continuing
/// from a checkpoint each reproduce the value the same live session would. An administrative write
/// (<see cref="WorldMutation.UpsertStateCell"/>) and an equivalent rule <see cref="ActionEffect.AddState"/> effect
/// rebase the cell's clock identically.</summary>
public sealed class EngineTimeClockSequenceLawTests {
    private static readonly Principal Actor = Principal.Seat(slot: 0);
    private static readonly ulong OtherRateStepTicks = Puck.Hosting.EngineTicks.PerRate(ratePerSecond: 60U);

    private static WorldStateRow ClockRow(long value = 0) => new(
        Name: CellName.Parse(candidate: "clock"),
        Kind: CellKind.Int,
        Advance: new StateAdvance(
            PerSecondDenominator: 1,
            PerSecondNumerator: 1000
        ),
        Cells: [new StateCell(
                Key: WorldStateRow.SlotKey,
                Value: CellValue.Int(value: value)
            )]
    );
    private static WorldDefinition Document(params WorldStateRow[] rows) => Fixtures.BuildDocument().WithWorldState(rows: rows);
    private static long ReadLive(WorldFixture fixture) {
        Assert.True(condition: WorldStateReader.TryRead(
            definition: fixture.Server.Definition,
            engineTick: fixture.Server.CompletedEngineTicks,
            key: WorldStateRow.SlotKey.Value,
            rawValue: out var value,
            row: out _,
            rowName: "clock",
            text: out _,
            tick: (fixture.Server.NextInputTick - 1UL)
        ));

        return value!.Value;
    }
    private static StateCellClock ReadClock(WorldFixture fixture, string row = "clock", string key = "$value") =>
        (WorldDefinitionRows.FindStateRow(
            rows: fixture.Server.Definition.State,
            name: row
        )!.Cells!.Single(predicate: cell => (cell.Key.Value == key)).Clock ?? new StateCellClock());
    private static void Write(WorldFixture fixture, long value, ulong? stepTicks = null) {
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(
            Principal: Actor,
            Row: "clock",
            Key: WorldStateRow.SlotKey.Value,
            Value: value,
            Kind: WorldDocumentWriteKind.Set
        ));
        fixture.Step(stepTicks: stepTicks);
    }

    [Fact]
    public void WriteChangeRateWriteUndoBothAndContinueFromACheckpointEachReproduceTheLiveValue() {
        using var fixture = Fixtures.FreshServer(definition: Document(ClockRow()));

        Write(
            fixture: fixture,
            value: 100
        );

        // Immediately after the write, no engine time has elapsed since the rebase — the base is the whole story.
        Assert.Equal(
            expected: 100L,
            actual: ReadLive(fixture: fixture)
        );

        // The observable shape of a live simulation-rate change: subsequent steps advance engine time by a
        // DIFFERENT width per tick. Engine time still accrues contiguously — decision: a rate change moves no
        // epoch — so the value keeps climbing from 100 at the SAME per-second rate, never resetting or skewing.
        for (var index = 0; (index < 5); index++) {
            fixture.Step(stepTicks: OtherRateStepTicks);
        }

        var beforeSecondWrite = ReadLive(fixture: fixture);

        Assert.True(condition: (beforeSecondWrite > 100L));

        Write(
            fixture: fixture,
            stepTicks: OtherRateStepTicks,
            value: 200
        );

        Assert.Equal(
            expected: 200L,
            actual: ReadLive(fixture: fixture)
        );

        var beforeUndo = fixture.DefinitionBytes();

        fixture.Server.EnqueueUndo(
            count: 2,
            principal: Principal.Console
        );
        fixture.Step(stepTicks: OtherRateStepTicks);

        // Both writes undone lands exactly back on the document's original, never-written state.
        Assert.Equal(
            expected: 0L,
            actual: WorldDefinitionRows.FindStateRow(
                rows: fixture.Server.Definition.State,
                name: "clock"
            )!.Cells!.Single().Value.AsInt
        );
        Assert.NotEqual(
            expected: beforeUndo,
            actual: fixture.DefinitionBytes()
        ); // the control: the undo genuinely changed the document.

        var afterUndo = fixture.DefinitionBytes();

        Assert.True(
            condition: fixture.Server.TryCaptureCheckpoint(
                hostRow: WorldAuthorityHostRowCheckpoint.Empty,
                checkpoint: out var checkpoint,
                reason: out var captureReason
            ),
            userMessage: captureReason
        );

        var restoredDefinition = WorldDefinitionSerialization.Deserialize(utf8Json: checkpoint!.Server.DefinitionJson);

        using var machines = new WorldMachineHost(
            engines: [],
            screens: restoredDefinition.Screens
        );
        using var profilesDirectory = new TemporaryDirectory(prefix: "puck-engine-time-sequence-tests-");

        var (restored, _) = WorldServer.FromCheckpoint(
            checkpoint: checkpoint,
            instanceIdentity: "engine-time-sequence-restore",
            machines: machines,
            profiles: new WorldOwnedWorlds(
                directory: profilesDirectory.RootPath,
                machineId: Guid.NewGuid(),
                template: restoredDefinition
            )
        );

        Assert.Equal(
            expected: afterUndo,
            actual: WorldDefinitionSerialization.Serialize(definition: restored.Definition)
        );

        // Continuing from the checkpoint reproduces the SAME live trajectory an uninterrupted session would have:
        // step both the original (post-undo) fixture and the restored server forward by the identical engine-tick
        // width and confirm they read the identical live value.
        restored.Advance(stepTicks: Fixtures.StepTicks);
        fixture.Step();

        Assert.True(condition: WorldStateReader.TryRead(
            definition: restored.Definition,
            engineTick: restored.CompletedEngineTicks,
            key: WorldStateRow.SlotKey.Value,
            rawValue: out var restoredValue,
            row: out _,
            rowName: "clock",
            text: out _,
            tick: (restored.NextInputTick - 1UL)
        ));
        Assert.Equal(
            expected: ReadLive(fixture: fixture),
            actual: restoredValue
        );
    }
    [Fact]
    public void AnAdministrativeWriteAndAnEquivalentRuleWriteRebaseTheClockIdentically() {
        using var admin = Fixtures.FreshServer(definition: Document(ClockRow()));

        var trigger = new WorldStateRow(
            Name: CellName.Parse(candidate: "trigger"),
            Kind: CellKind.Int,
            Cells: [new StateCell(
                    Key: WorldStateRow.SlotKey,
                    Value: CellValue.Int(value: 0)
                )]
        );
        using var ruled = Fixtures.FreshServer(definition: Document(ClockRow(), trigger) with {
            Rules = [new WorldRule(
                Name: CellName.Parse(candidate: "push"),
                Gate: new ActionPredicate.CompareState(
                    State: "trigger",
                    Comparison: ExpressionOp.Equal,
                    Value: 1m
                ),
                Mode: ActionTriggerMode.Edge,
                Effects: [new ActionEffect.SetState(
                        State: "clock",
                        Value: 300m
                    )]
            )],
        });

        Write(
            fixture: admin,
            value: 300
        );

        ruled.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(
            Principal: Actor,
            Row: "trigger",
            Key: WorldStateRow.SlotKey.Value,
            Value: 1,
            Kind: WorldDocumentWriteKind.Set
        ));
        ruled.Step();

        Assert.Equal(
            expected: 300L,
            actual: ReadLive(fixture: admin)
        );
        Assert.Equal(
            expected: 300L,
            actual: ReadLive(fixture: ruled)
        );
        Assert.Equal(
            expected: admin.Server.CompletedEngineTicks,
            actual: ruled.Server.CompletedEngineTicks
        );

        // Advance's own clock coordinate (EpochEngineTick, ENGINE CONTRACT C) rebases identically for the two
        // write paths. EpochTick — the SIMULATION-tick coordinate Dynamics/Cycle read, irrelevant to Advance — is
        // not compared here: a rule effect settles one simulation tick after an equivalent direct mutation
        // composed in the same Step() call, since rule evaluation runs against the tick Step just advanced to.
        Assert.Equal(
            expected: ReadClock(fixture: admin).EpochEngineTick,
            actual: ReadClock(fixture: ruled).EpochEngineTick
        );
    }

}
