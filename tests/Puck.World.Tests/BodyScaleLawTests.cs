using System.Numerics;
using Xunit;

using Puck.Assets.Documents;
using Puck.Maths;
using Puck.SignedDistance;
using Puck.World.Authoring;
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

    // A body's own gravity fall/rise/terminal, its wall hold's travel speed, and its grip pull rate are all
    // authored at full body scale; WorldBody.Hold.cs multiplies each by m_scale so a shrunk body settles onto and
    // depenetrates from the ground at a proportionally gentler rate. Without that scaling the UNSCALED one-tick
    // fall a grounded body's contact resolve routinely catches and corrects every tick overshoots a small enough
    // collider's own skin margin, and the correction eats into the SAME tick's commanded planar movement — a
    // shrunk body's measured walking speed falls well short of ResolveMoveSpeed's own scaled echo even though
    // nothing ever leaves the grounded state. Stepped at the shipped garden's own 30 Hz (the suite's other fixture
    // documents default to 240 Hz, whose finer per-tick fall never approaches this margin) so the reproduction
    // matches the real world exactly. 1.0 is the discriminating control: an unscaled collider's skin margin is
    // never in question at ordinary gravity, so it must cover its full speed with or without the scaling.
    private const int ForwardOrdinal = 0;
    private const uint SimulationRateHz = 30;
    private static readonly ulong StepWidth = Puck.Hosting.EngineTicks.PerRate(ratePerSecond: SimulationRateHz);
    private const int SettleTicks = (int)SimulationRateHz;

    [Theory]
    [InlineData(1.0, 4.0)]
    [InlineData(0.15, 0.6)]
    [InlineData(0.05, 0.2)]
    public void ScaleRow_GroundedBodyCoversItsFullScaledSpeedPerSecond(double scaleValue, double expectedMetersPerSecond) {
        var scale = FixedQ4816.FromDouble(value: scaleValue);

        using var fixture = Fixtures.FreshServer(definition: PlatformDocumentWithScale(cellValue: scale));
        var body = JoinBody(fixture: fixture);

        body.Pose(x: 0f, y: -0.5f, z: 0f, yawRadians: 0f, pitchRadians: 0f, rollRadians: 0f);

        for (var tick = 0; (tick < SettleTicks); tick++) {
            fixture.Step(stepTicks: StepWidth);
        }

        Assert.True(condition: body.Grounded, userMessage: $"the body did not settle grounded on the platform; y={body.Position.Y:0.###}");
        Assert.Equal(expected: scale, actual: body.Scale);

        var settledZ = body.FixedPosition.Z;

        for (var tick = 0; (tick < SimulationRateHz); tick++) {
            body.SubmitIntent(intent: default(PlayerIntent).WithChannel(ordinal: ForwardOrdinal, value: FixedQ4816.One));
            fixture.Step(stepTicks: StepWidth);
        }

        var traveled = ((double)(settledZ - body.FixedPosition.Z));

        Assert.True(
            condition: (Math.Abs(traveled - expectedMetersPerSecond) < 0.02),
            userMessage: $"scale {scaleValue}: expected ~{expectedMetersPerSecond:0.###}m over 1s, covered {traveled:0.###}m"
        );
    }

    // The shipped world's own platform/walker/gravity recipe (see ConstantUpLipContactLawTests.PlatformDocument),
    // with a bodies.scaleRow cell layered on top so the contact resolve runs against a real collider-bearing
    // ground contact at the exact radius/skin ratio a shrunk garden body reaches.
    private static WorldDefinition PlatformDocumentWithScale(FixedQ4816 cellValue) {
        var source = Fixtures.BuildGradientUpDocument(gradientUp: false);
        var shape = new ShapeDocument(
            Id: 0,
            Name: "platform",
            Type: SdfSolidPrimitive.Box,
            Position: new Vector3(x: 0f, y: -1f, z: 0f),
            Rotation: Quaternion.Identity,
            Scale: new Vector3(x: 10f, y: 0.5f, z: 10f),
            Material: 0,
            Blend: SdfBlendOp.Union,
            Smooth: 0f,
            Group: 0
        );
        var document = new CreationDocument(
            Schema: CreationDocument.CurrentSchema,
            Name: "platform",
            Palette: null,
            Shapes: [shape],
            Frames: null
        );
        var canonical = CreationCanonicalizer.Canonicalize(document: document, source: "platform");
        var creation = new WorldPrototype(Id: "platform", Document: canonical.Document, HashRaw: canonical.Hash);
        var scaleRow = new WorldStateRow(
            Name: WorldCellName.Parse(candidate: "scale"),
            Kind: CellKind.Fixed,
            Min: EnvelopeMin.Value,
            Max: EnvelopeMax.Value,
            Capacity: 8,
            Cells: [new WorldStateCell(Key: WorldCellName.Parse(candidate: "0"), Value: cellValue.Value)]
        );

        return source with {
            Simulation = new WorldSimulationDefaults(RateHz: (int)SimulationRateHz),
            GravityRaw = new WorldGravity(
                Attractors: [],
                GravitationalConstant: 0f,
                SofteningLength: 0.5f,
                Solver: WorldGravitySolver.Pairwise,
                Uniform: new DocumentVector3(x: 0f, y: -46f, z: 0f)
            ),
            KitRowsRaw = source.Kits.Select(selector: kit => kit with {
                Motion = kit.Motion with {
                    Speed = kit.Motion.Speed with { Value = 4f },
                    Holds = [
                        kit.Motion.Holds![0] with { Envelope = new WorldHoldEnvelope(SinkSpeed: 40f), Gravity = new WorldHoldGravity(Fall: 46f, Rise: 28f) },
                    ],
                },
            }).ToArray(),
            CreationsRaw = [creation],
            PlacementRowsRaw = [new WorldPlacement(Id: "platform", PrototypeId: creation.Id, Position: Vector3.Zero, YawDegrees: 0f, Scale: 1f, Solid: new WorldSolid(Margin: 0f))],
            PopulationRaw = (source.Population with { ScaleRow = "scale" }),
            StateRaw = ((source.StateRaw ?? new WorldStateSection()) with {
                World = [.. (source.StateRaw?.World ?? []), scaleRow],
            }),
        };
    }
}
