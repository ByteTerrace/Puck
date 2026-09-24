using Puck.Commands;
using Xunit;

using Puck.Testing;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>An out-of-range write refuses or saturates identically through the administrative mutation door
/// (<see cref="WorldMutation.UpsertStateCell"/>) and through a world rule's <see cref="ActionEffect.AddState"/>
/// effect, since both compose through the one <see cref="StateRow.TryAdmitWrite"/> decision (ENGINE CONTRACT A);
/// <c>world.undo</c> and a checkpoint restore each reproduce a saturated value and every cell's own clock (an
/// advance epoch, a dynamics follower's sampled position/velocity, a cycle's settled phase and substep) bit-exactly.</summary>
public sealed class StateEnvelopeMutationAndRuleLawTests {
    private static readonly Principal Actor = Principal.Seat(slot: 0);
    private static readonly DynamicsRow Kick = new(
        Damping: 1f,
        Frequency: 1f,
        Name: "kick",
        Response: 1f
    );

    private static WorldStateRow SaturatingRow(long value) => new(
        Name: CellName.Parse(candidate: "saturating"),
        Kind: CellKind.Int,
        Min: 0,
        Max: 10,
        Overflow: StateOverflow.Saturate,
        Cells: [new StateCell(
                Key: WorldStateRow.SlotKey,
                Value: CellValue.Int(value: value)
            )]
    );
    private static WorldStateRow RefusingRow(long value) => new(
        Name: CellName.Parse(candidate: "refusing"),
        Kind: CellKind.Int,
        Min: 0,
        Max: 10,
        Cells: [new StateCell(
                Key: WorldStateRow.SlotKey,
                Value: CellValue.Int(value: value)
            )]
    );
    private static WorldStateRow TriggerRow() => StateFixtures.IntSlot(name: "trigger");
    private static WorldStateRow TraitsRow(long advanceValue, long dynamicsValue, long cycleValue) => new(
        Name: CellName.Parse(candidate: "traits"),
        Kind: CellKind.Int,
        Capacity: 4,
        Cycle: new StateCycle(TicksPerStep: 5),
        Cells: [new StateCell(
                Key: CellName.Parse(candidate: "adv"),
                Value: CellValue.Int(value: advanceValue),
                Advance: new StateAdvance(
                    PerSecondDenominator: 1,
                    PerSecondNumerator: 1
                )
            ), new StateCell(
                Key: CellName.Parse(candidate: "dyn"),
                Value: CellValue.Int(value: dynamicsValue),
                Dynamics: new StateDynamics(Row: "kick")
            ), new StateCell(
                Key: CellName.Parse(candidate: "cyc"),
                Value: CellValue.Int(value: cycleValue)
            )]
    );
    private static WorldDefinition Document(params WorldStateRow[] rows) => (Fixtures.BuildDocument().WithWorldState(rows: rows) with {
        DynamicsRaw = [.. Fixtures.StandardDynamics, Kick],
    });
    private static StateCell Cell(WorldFixture fixture, string row, string key = "$value") =>
        WorldDefinitionRows.FindStateRow(
            rows: fixture.Server.Definition.State,
            name: row
        )!.Cells!.Single(predicate: cell => (cell.Key.Value == key));
    private static void AdminWrite(WorldFixture fixture, string row, long value, WorldDocumentWriteKind kind = WorldDocumentWriteKind.Add, string key = "$value") {
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(
            Principal: Principal.Console,
            Row: row,
            Key: key,
            Value: value,
            Kind: kind
        ));
        fixture.Step();
    }

    [Fact]
    public void AdministrativeAddSaturatesAtTheDeclaredCeilingAndFloor() {
        using var fixture = Fixtures.FreshServer(definition: Document(SaturatingRow(value: 8)));
        var before = fixture.DefinitionBytes();

        AdminWrite(
            fixture: fixture,
            row: "saturating",
            value: 5
        ); // 8 + 5 = 13 -> clamped to 10.

        Assert.Equal(
            expected: 10L,
            actual: Cell(
                fixture: fixture,
                row: "saturating"
            ).Value.Raw
        );

        // world.undo restores the pre-write value exactly — a saturating write is an ordinary journaled mutation.
        fixture.Server.EnqueueUndo(
            count: 1,
            principal: Principal.Console
        );
        fixture.Step();

        Assert.Equal(
            expected: before,
            actual: fixture.DefinitionBytes()
        );

        AdminWrite(
            fixture: fixture,
            row: "saturating",
            value: -20
        ); // 8 - 20 = -12 -> clamped to 0.

        Assert.Equal(
            expected: 0L,
            actual: Cell(
                fixture: fixture,
                row: "saturating"
            ).Value.Raw
        );
    }
    [Fact]
    public void AdministrativeAddPastAnUnboundedRowsSixtyFourBitLimitRefusesByNameAndLeavesTheValueUntouched() {
        var row = new WorldStateRow(
            Name: CellName.Parse(candidate: "unbounded"),
            Kind: CellKind.Int,
            Cells: [new StateCell(
                    Key: WorldStateRow.SlotKey,
                    Value: CellValue.Int(value: (long.MaxValue - 2))
                )]
        );
        using var fixture = Fixtures.FreshServer(definition: Document(row));
        var before = fixture.DefinitionBytes();
        var refusals = new List<string>();

        fixture.Server.EchoTap = echo => { if (echo.Rejected) { refusals.Add(item: echo.Message); } };

        AdminWrite(
            fixture: fixture,
            row: "unbounded",
            value: 10
        );

        Assert.Contains(
            collection: refusals,
            filter: reason => reason.Contains(
                comparisonType: StringComparison.Ordinal,
                value: "overflow 64-bit storage"
            )
        );
        Assert.Equal(
            expected: before,
            actual: fixture.DefinitionBytes()
        );
    }
    [Fact]
    public void ARuleWriteThatWouldLeaveTheDeclaredRangeIsRefusedByNameAndLeavesEarlierEffectsApplied() {
        var document = Document(RefusingRow(value: 8), TriggerRow()) with {
            Rules = [new WorldRule(
                Name: CellName.Parse(candidate: "push"),
                Gate: new ActionPredicate.CompareState(
                    State: "trigger",
                    Comparison: ExpressionOp.Equal,
                    Value: 1m
                ),
                Mode: ActionTriggerMode.Edge,
                Effects: [
                    new ActionEffect.AddState(
                        State: "refusing",
                        Value: 5m
                    ), // 8 + 5 = 13 -> past Max 10, refused by name (Overflow defaults to Refuse).
                ]
            )],
        };
        using var fixture = Fixtures.FreshServer(definition: document);

        AdminWrite(
            fixture: fixture,
            row: "trigger",
            value: 1,
            kind: WorldDocumentWriteKind.Set
        );

        Assert.Equal(
            expected: 8L,
            actual: Cell(
                fixture: fixture,
                row: "refusing"
            ).Value.Raw
        );

        var diagnostic = Assert.Single(collection: fixture.Server.RuleRuntimeDiagnostics());

        Assert.Equal<Enum>(
            expected: RuleEffectRefusal.MutationRejected,
            actual: diagnostic.Refusal
        );
        Assert.Equal(
            expected: "push",
            actual: diagnostic.Rule
        );
    }
    [Fact]
    public void ARuleAddThatSaturatesStoresTheClampedValueAndNeverFaultsTheRule() {
        var document = Document(SaturatingRow(value: 8), TriggerRow()) with {
            Rules = [new WorldRule(
                Name: CellName.Parse(candidate: "push"),
                Gate: new ActionPredicate.CompareState(
                    State: "trigger",
                    Comparison: ExpressionOp.Equal,
                    Value: 1m
                ),
                Mode: ActionTriggerMode.Edge,
                Effects: [new ActionEffect.AddState(
                        State: "saturating",
                        Value: 5m
                    )]
            )],
        };
        using var fixture = Fixtures.FreshServer(definition: document);

        AdminWrite(
            fixture: fixture,
            row: "trigger",
            value: 1,
            kind: WorldDocumentWriteKind.Set
        );

        Assert.Equal(
            expected: 10L,
            actual: Cell(
                fixture: fixture,
                row: "saturating"
            ).Value.Raw
        );
        Assert.Empty(collection: fixture.Server.RuleRuntimeDiagnostics());
    }
    [Fact]
    public void CheckpointRestoreReproducesASaturatedValueAndEveryCellsOwnClockBitExactly() {
        using var fixture = Fixtures.FreshServer(definition: Document(
            SaturatingRow(value: 8),
            TraitsRow(
                advanceValue: 0,
                cycleValue: 0,
                dynamicsValue: 0
            )
        ));

        AdminWrite(
            fixture: fixture,
            row: "saturating",
            value: 100
        ); // saturates to 10, carrying the raw +100 operand in the mutation.

        // Rebase the advance and dynamics cells' clocks off their initial rest state with a real write, and let a
        // few ticks pass first so the rebase is genuinely mid-flight rather than a trivial zero-elapsed one.
        for (var index = 0; (index < 3); index++) {
            fixture.Step();
        }

        AdminWrite(
            fixture: fixture,
            key: "adv",
            kind: WorldDocumentWriteKind.Set,
            row: "traits",
            value: 40
        );
        AdminWrite(
            fixture: fixture,
            key: "dyn",
            kind: WorldDocumentWriteKind.Set,
            row: "traits",
            value: 40
        );

        // A behavior-parameter change is the one thing that settles (and so moves the clock of) a cycling cell:
        // re-author the row's own default TicksPerStep after some ticks have already accumulated toward the old
        // period, so the settle carries a genuinely nonzero substep remainder forward.
        for (var index = 0; (index < 3); index++) {
            fixture.Step();
        }

        var traitsRow = WorldDefinitionRows.FindStateRow(
            rows: fixture.Server.Definition.State,
            name: "traits"
        )!;

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateRow(
            Principal: Principal.Console,
            Row: (traitsRow with { Cycle = (traitsRow.Cycle! with { TicksPerStep = 7 }) })
        ));
        fixture.Step();

        for (var index = 0; (index < 3); index++) {
            fixture.Step();
        }

        var before = fixture.DefinitionBytes();
        var advBefore = Cell(
            fixture: fixture,
            key: "adv",
            row: "traits"
        );
        var dynBefore = Cell(
            fixture: fixture,
            key: "dyn",
            row: "traits"
        );
        var cycBefore = Cell(
            fixture: fixture,
            key: "cyc",
            row: "traits"
        );

        // The control: the settle actually moved something on every trait, so the round trip below is proving a
        // real, nonzero clock — never a trivially-zero default that would pass by accident.
        Assert.NotEqual(
            expected: 0L,
            actual: advBefore.Clock!.EpochEngineTick
        );
        Assert.NotEqual(
            expected: 0L,
            actual: dynBefore.Clock!.V0
        );
        Assert.Equal(
            expected: 10L,
            actual: Cell(
                fixture: fixture,
                row: "saturating"
            ).Value.Raw
        );

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
        using var profilesDirectory = new TemporaryDirectory(prefix: "puck-envelope-clock-tests-");

        var (restored, _) = WorldServer.FromCheckpoint(
            checkpoint: checkpoint,
            instanceIdentity: "envelope-clock-restore",
            machines: machines,
            profiles: new WorldOwnedWorlds(
                directory: profilesDirectory.RootPath,
                machineId: Guid.NewGuid(),
                template: restoredDefinition
            )
        );

        Assert.Equal(
            expected: before,
            actual: WorldDefinitionSerialization.Serialize(definition: restored.Definition)
        );

        var advAfter = WorldDefinitionRows.FindStateRow(
            rows: restored.Definition.State,
            name: "traits"
        )!.Cells!.Single(predicate: cell => (cell.Key.Value == "adv"));
        var dynAfter = WorldDefinitionRows.FindStateRow(
            rows: restored.Definition.State,
            name: "traits"
        )!.Cells!.Single(predicate: cell => (cell.Key.Value == "dyn"));
        var cycAfter = WorldDefinitionRows.FindStateRow(
            rows: restored.Definition.State,
            name: "traits"
        )!.Cells!.Single(predicate: cell => (cell.Key.Value == "cyc"));

        Assert.Equal(
            expected: advBefore.Clock,
            actual: advAfter.Clock
        );
        Assert.Equal(
            expected: dynBefore.Clock,
            actual: dynAfter.Clock
        );
        Assert.Equal(
            expected: cycBefore.Clock,
            actual: cycAfter.Clock
        );
        Assert.Equal(
            expected: advBefore.Value,
            actual: advAfter.Value
        );
        Assert.Equal(
            expected: dynBefore.Value,
            actual: dynAfter.Value
        );
        Assert.Equal(
            expected: cycBefore.Value,
            actual: cycAfter.Value
        );
        Assert.Equal(
            expected: 10L,
            actual: WorldDefinitionRows.FindStateRow(
                rows: restored.Definition.State,
                name: "saturating"
            )!.Cells!.Single().Value.Raw
        );
    }
    [Fact]
    public void ARecordedRunAgreesWithItselfOnASecondVerifyOfTheSameRecording() {
        Fixtures.SkipIfReplayDirectoryUnwritable();

        using var fixture = Fixtures.FreshServer(definition: Document(
            SaturatingRow(value: 8),
            RefusingRow(value: 8)
        ));
        var transport = new LoopbackTransport(server: fixture.Server);
        var tape = new WorldReplayTape(
            liveServer: fixture.Server,
            profiles: fixture.Server.Profiles,
            transport: transport,
            engines: [],
            machineHostFactory: Fixtures.MachineHostFactory,
            addonHostFactory: static (_, _) => new NullAddonHost()
        );
        var name = $"envelope-determinism-{Guid.NewGuid():N}";

        Assert.True(
            condition: tape.TryBeginRecording(
                name: name,
                refusal: out var refusal
            ),
            userMessage: refusal
        );

        transport.SubmitWorldMutation(mutation: new WorldMutation.UpsertStateCell(
            Principal: Principal.Console,
            Row: "saturating",
            Key: WorldStateRow.SlotKey.Value,
            Value: 100,
            Kind: WorldDocumentWriteKind.Add
        ));
        fixture.Step();
        tape.NoteTick();

        transport.SubmitWorldMutation(mutation: new WorldMutation.UpsertStateCell(
            Principal: Principal.Console,
            Row: "refusing",
            Key: WorldStateRow.SlotKey.Value,
            Value: 100,
            Kind: WorldDocumentWriteKind.Add
        ));
        fixture.Step();
        tape.NoteTick();

        transport.SubmitUndo(
            count: 1,
            principal: Principal.Console
        );
        fixture.Step();
        tape.NoteTick();

        var stop = tape.StopRecording();

        Assert.Null(@object: stop.VerifyFault);
        Assert.True(
            condition: stop.Verdict!.Value.Match,
            userMessage: stop.Verdict.Value.Describe()
        );

        // Decision 10: running the SAME recording's verify a second time is an additional determinism check, not
        // itself the proof of correctness — both runs must agree bit-for-bit on where (if anywhere) they diverge.
        var first = tape.Verify(name: name);
        var second = tape.Verify(name: name);

        Assert.True(
            condition: first.Match,
            userMessage: first.Describe()
        );
        Assert.True(
            condition: second.Match,
            userMessage: second.Describe()
        );
        Assert.Equal(
            expected: first.DivergedAt,
            actual: second.DivergedAt
        );
    }

}
