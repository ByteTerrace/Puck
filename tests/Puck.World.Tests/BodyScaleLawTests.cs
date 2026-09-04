using Xunit;

using Puck.Maths;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>Laws for <c>bodies.scaleRow</c> — the world declaring which keyed <c>state.world</c> row carries each
/// body's live scale multiplier: the declared envelope refuses an out-of-range cell, and an admitted cell survives
/// the document's own serialize/deserialize round trip into <see cref="Server.WorldBody.Scale"/>.</summary>
public sealed class BodyScaleLawTests {
    private static readonly FixedQ4816 EnvelopeMin = FixedQ4816.FromDouble(value: 0.05);
    private static readonly FixedQ4816 EnvelopeMax = FixedQ4816.One;

    private static WorldDefinition WithScaleRow(FixedQ4816 cellValue) {
        var baseDocument = Fixtures.BuildDocument();
        var scaleRow = new WorldStateRow(
            Name: WorldCellName.Parse(candidate: "scale"),
            Kind: CellKind.Fixed,
            Min: EnvelopeMin.Value,
            Max: EnvelopeMax.Value,
            Capacity: 8,
            Cells: [new WorldStateCell(Key: WorldCellName.Parse(candidate: "0"), Value: cellValue.Value)]
        );

        return (baseDocument with {
            PopulationRaw = (baseDocument.Population with { ScaleRow = "scale" }),
            StateRaw = ((baseDocument.StateRaw ?? new WorldStateSection()) with {
                World = [.. (baseDocument.StateRaw?.World ?? []), scaleRow],
            }),
        });
    }

    [Fact]
    public void ScaleRow_CellBelowDeclaredMinimum_Refused() {
        var denied = WithScaleRow(cellValue: FixedQ4816.FromDouble(value: 0.01));

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: denied, neighbours: null, reason: out var deniedReason), userMessage: "a scaleRow cell below its own declared min was expected to refuse");
        Assert.Contains(expectedSubstring: "scale", actualString: deniedReason);

        var admitted = WithScaleRow(cellValue: FixedQ4816.FromDouble(value: 0.4));

        Assert.True(condition: WorldDefinitionValidator.TryValidate(definition: admitted, neighbours: null, reason: out var controlReason), userMessage: controlReason);
    }

    [Fact]
    public void ScaleRow_CellAboveDeclaredMaximum_Refused() {
        var denied = WithScaleRow(cellValue: FixedQ4816.FromDouble(value: 1.5));

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: denied, neighbours: null, reason: out var deniedReason), userMessage: "a scaleRow cell above its own declared max was expected to refuse");
        Assert.Contains(expectedSubstring: "scale", actualString: deniedReason);
    }

    private static WorldBody JoinBody(WorldFixture fixture, int slot = 0) {
        var actor = WorldPrincipal.Seat(slot: slot);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(
            Principal: actor,
            Slot: actor.Index,
            IdentityName: null,
            WireProtocolKey: WorldProtocol.WireProtocolKey
        )).Accepted);

        return fixture.Server.Body(index: actor.Index)!;
    }

    [Fact]
    public void ScaleRow_AdmittedCell_SurvivesSerializationRoundTripIntoBodyScale() {
        var authored = FixedQ4816.FromDouble(value: 0.4);
        var document = WithScaleRow(cellValue: authored);

        using var fixture = Fixtures.FreshServer(definition: document);
        var body = JoinBody(fixture: fixture);

        Assert.Equal(expected: authored, actual: body.Scale);
    }

    [Fact]
    public void ScaleRow_Absent_BodyScaleDefaultsToOne() {
        using var fixture = Fixtures.FreshServer();
        var body = JoinBody(fixture: fixture);

        Assert.Equal(expected: FixedQ4816.One, actual: body.Scale);
    }

    // RestoreCheckpoint rebuilds every WorldBody at the constructed default (WorldPopulationCheckpoint carries no
    // Scale field of its own — bodies.scaleRow is document state, restored with the definition), so this proves the
    // catch-up resync — not merely that the document round-trips through serialization.
    [Fact]
    public void ScaleRow_AdmittedCell_SurvivesCheckpointRestoreIntoBodyScale() {
        var authored = FixedQ4816.FromDouble(value: 0.15);
        var document = WithScaleRow(cellValue: authored);

        using var fixture = Fixtures.FreshServer(definition: document);
        JoinBody(fixture: fixture);
        fixture.Step();

        Assert.True(condition: fixture.Server.TryCaptureCheckpoint(
            checkpoint: out var checkpoint,
            hostRow: EmptyHostRow(),
            reason: out var refusal
        ), userMessage: refusal);

        var restoredDefinition = WorldDefinitionSerialization.Deserialize(utf8Json: checkpoint!.Server.DefinitionJson);
        using var restoredMachines = new WorldMachineHost(engines: [], screens: restoredDefinition.Screens);
        var (restoredServer, _) = WorldServer.FromCheckpoint(
            checkpoint: checkpoint,
            instanceIdentity: "boot",
            machines: restoredMachines,
            profiles: FreshProfiles(definition: restoredDefinition)
        );

        Assert.Equal(expected: authored, actual: restoredServer.Body(index: 0)!.Scale);
    }

    // Control for the case above: a body carrying no scaleRow cell restores at the unscaled default, so the prior
    // assertion is discriminating a real resync rather than every restored body reading 0.15 regardless.
    [Fact]
    public void ScaleRow_AbsentCell_CheckpointRestoreLeavesBodyScaleAtOne() {
        using var fixture = Fixtures.FreshServer();
        JoinBody(fixture: fixture);
        fixture.Step();

        Assert.True(condition: fixture.Server.TryCaptureCheckpoint(
            checkpoint: out var checkpoint,
            hostRow: EmptyHostRow(),
            reason: out var refusal
        ), userMessage: refusal);

        var restoredDefinition = WorldDefinitionSerialization.Deserialize(utf8Json: checkpoint!.Server.DefinitionJson);
        using var restoredMachines = new WorldMachineHost(engines: [], screens: restoredDefinition.Screens);
        var (restoredServer, _) = WorldServer.FromCheckpoint(
            checkpoint: checkpoint,
            instanceIdentity: "boot",
            machines: restoredMachines,
            profiles: FreshProfiles(definition: restoredDefinition)
        );

        Assert.Equal(expected: FixedQ4816.One, actual: restoredServer.Body(index: 0)!.Scale);
    }

    private static WorldAuthorityHostRowCheckpoint EmptyHostRow() => new(
        AnnouncedCrossingHolds: [],
        AppliedTransferHighWater: null,
        AppliedTransferIds: [],
        ElapsedEngineTicks: 0,
        ForwardedBodies: [],
        FreshCounter: 0,
        InDoubtTransfers: [],
        IsPaused: false,
        NextTransferId: 1,
        PortalOccupancy: [],
        Retained: false,
        ScheduleAccumulatorTicks: 0,
        SeededArrivals: []
    );

    private static WorldOwnedWorlds FreshProfiles(WorldDefinition definition) => new(
        directory: Directory.CreateTempSubdirectory(prefix: "puck-body-scale-tests-").FullName,
        machineId: Guid.NewGuid(),
        template: definition
    );
}
