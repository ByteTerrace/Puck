using System.Numerics;
using Xunit;

using Puck.Assets.Documents;
using Puck.Commands;
using Puck.Maths;
using Puck.Physics.Motion;
using Puck.SignedDistance;
using Puck.Testing;
using Puck.World.Authoring;
using Puck.World.Protocol;

namespace Puck.World.Server.Tests;

/// <summary>Laws for <c>bodies.scale</c> — the world naming the <c>state.body</c> Counter register that carries each
/// body's live scale multiplier: the register's envelope must be a positive range inside the render capacity, and the
/// register's value, initial or written, survives the document's serialize/deserialize round trip and a checkpoint
/// restore into <see cref="Server.WorldBody.Scale"/>.</summary>
public sealed class BodyScaleLawTests {
    private const double EnvelopeMax = 1d;
    private const double EnvelopeMin = 0.05d;

    private static readonly ulong StepWidth = Puck.Maths.FixedTickConversion.PerRate(ratePerSecond: SimulationRateHz);

    // The shipped world's own platform/walker/gravity recipe (see ConstantUpLipContactLawTests.PlatformDocument),
    // with a bodies.scale register layered on top so the contact resolve runs against a real collider-bearing
    // ground contact at the exact radius/skin ratio a shrunk garden body reaches.
    private static WorldDefinition PlatformDocumentWithScale(FixedQ4816 cellValue) {
        var source = Fixtures.BuildGradientUpDocument(gradientUp: false);
        var shape = new ShapeDocument(
            Id: 0,
            Name: "platform",
            Type: SdfSolidPrimitive.Box,
            Position: new Vector3(
                x: 0f,
                y: -1f,
                z: 0f
            ),
            Rotation: Quaternion.Identity,
            Scale: new Vector3(
                x: 10f,
                y: 0.5f,
                z: 10f
            ),
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
        var canonical = CreationCanonicalizer.Canonicalize(
            document: document,
            source: "platform"
        );
        var creation = new WorldPrototype(
            Id: "platform",
            Document: canonical.Document,
            HashRaw: canonical.Hash
        );

        return Fixtures.WithBodyScale(
            initial: cellValue,
            source: source with {
                Simulation = new WorldSimulationDefaults(RateHz: ((int)SimulationRateHz)),
                GravityRaw = new WorldGravity(
            Attractors: [],
            GravitationalConstant: 0f,
            SofteningLength: 0.5f,
            Solver: WorldGravitySolver.Pairwise,
            Uniform: new DocumentVector3(
                x: 0f,
                y: -46f,
                z: 0f
            )
        ),
                KitRowsRaw = source.Kits.Select(selector: kit => kit with {
                    Motion = kit.Motion with {
                        Speed = kit.Motion.Speed with { Value = 4f },
                        Holds = [
                            kit.Motion.Holds![0] with { Envelope = new WorldHoldEnvelope(SinkSpeed: 40f), Gravity = new WorldHoldGravity(
                Fall: 46f,
                Rise: 28f
            ) },
                    ],
                    },
                }).ToArray(),
                CreationsRaw = [creation],
                PlacementRowsRaw = [new WorldPlacement(
                Id: "platform",
                PrototypeId: creation.Id,
                Position: Vector3.Zero,
                YawDegrees: 0f,
                Scale: 1f,
                Solid: new WorldSolid(Margin: 0f)
            )],
            },
            minimum: EnvelopeMin,
            maximum: EnvelopeMax
        );
    }
    private static WorldDefinition WithScaleRegister(FixedQ4816 initial) => Fixtures.WithBodyScale(
        initial: initial,
        maximum: EnvelopeMax,
        minimum: EnvelopeMin,
        source: Fixtures.BuildDocument()
    );

    // Control for the case below: a world naming no scale register restores at the unscaled default, so the
    // assertion that follows is discriminating a real resync rather than every restored body reading 0.15 regardless.
    [Fact]
    public void ScaleRegister_Absent_CheckpointRestoreLeavesBodyScaleAtOne() {
        using var fixture = Fixtures.FreshServer();

        fixture.JoinSeat();
        fixture.Step();

        Assert.True(
            condition: fixture.Server.TryCaptureCheckpoint(
                checkpoint: out var checkpoint,
                hostRow: WorldAuthorityHostRowCheckpoint.Empty,
                reason: out var refusal
            ),
            userMessage: refusal
        );

        var restoredDefinition = WorldDefinitionSerialization.Deserialize(utf8Json: checkpoint!.Server.DefinitionJson);
        using var restoredMachines = new WorldMachineHost(
            engines: [],
            screens: restoredDefinition.Screens
        );
        using var profilesDirectory = new TemporaryDirectory(prefix: "puck-body-scale-tests-");

        var (restoredServer, _) = WorldServer.FromCheckpoint(
            checkpoint: checkpoint,
            instanceIdentity: "boot",
            machines: restoredMachines,
            profiles: new WorldOwnedWorlds(directory: profilesDirectory.RootPath, machineId: Guid.NewGuid(), template: restoredDefinition)
        );

        Assert.Equal(
            expected: FixedQ4816.One,
            actual: restoredServer.Body(index: 0)!.Scale
        );
    }
    [Fact]
    public void ScaleRegister_Absent_BodyScaleDefaultsToOne() {
        using var fixture = Fixtures.FreshServer();
        var body = fixture.JoinSeat();

        Assert.Equal(
            expected: FixedQ4816.One,
            actual: body.Scale
        );
    }
    // Both the synchronized geometry and its register survive restore. The register is written away from its
    // initial value, so a restore that rebirthed it would change the body's scale on catch-up.
    [Fact]
    public void ScaleRegister_WrittenValue_SurvivesCheckpointRestoreIntoBodyScale() {
        var authored = FixedQ4816.FromDouble(value: 0.15);
        var document = WithScaleRegister(initial: FixedQ4816.One);

        using var fixture = Fixtures.FreshServer(definition: document);

        fixture.JoinSeat();
        Fixtures.WriteBodyScale(
            body: 0,
            server: fixture.Server,
            value: authored
        );
        fixture.Step();

        Assert.Equal(
            expected: authored,
            actual: fixture.Server.Body(index: 0)!.Scale
        );

        Assert.True(
            condition: fixture.Server.TryCaptureCheckpoint(
                checkpoint: out var checkpoint,
                hostRow: WorldAuthorityHostRowCheckpoint.Empty,
                reason: out var refusal
            ),
            userMessage: refusal
        );

        var restoredDefinition = WorldDefinitionSerialization.Deserialize(utf8Json: checkpoint!.Server.DefinitionJson);
        using var restoredMachines = new WorldMachineHost(
            engines: [],
            screens: restoredDefinition.Screens
        );
        using var profilesDirectory = new TemporaryDirectory(prefix: "puck-body-scale-tests-");

        var (restoredServer, _) = WorldServer.FromCheckpoint(
            checkpoint: checkpoint,
            instanceIdentity: "boot",
            machines: restoredMachines,
            profiles: new WorldOwnedWorlds(directory: profilesDirectory.RootPath, machineId: Guid.NewGuid(), template: restoredDefinition)
        );

        Assert.Equal(
            expected: authored,
            actual: restoredServer.Body(index: 0)!.Scale
        );
    }
    [Fact]
    public void ScaleRegister_Initial_SurvivesSerializationRoundTripIntoBodyScale() {
        var authored = FixedQ4816.FromDouble(value: 0.4);
        var document = WithScaleRegister(initial: authored);

        using var fixture = Fixtures.FreshServer(definition: document);
        var body = fixture.JoinSeat();

        Assert.Equal(
            expected: authored,
            actual: body.Scale
        );
    }
    [Fact]
    public void ScaleRegister_InitialAboveItsEnvelope_Refused() {
        var denied = WithScaleRegister(initial: FixedQ4816.FromDouble(value: 1.5));

        Assert.False(
            condition: WorldDefinitionValidator.TryValidate(
                definition: denied,
                neighbours: null,
                reason: out var deniedReason
            ),
            userMessage: "a scale register initial above its own envelope was expected to refuse"
        );
        Assert.Contains(
            actualString: deniedReason,
            expectedSubstring: "inside its envelope"
        );
    }
    [Fact]
    public void ScaleRegister_EnvelopeNotPositiveOrPastTheRenderCapacity_RefusedByName() {
        var source = Fixtures.BuildDocument();
        var zero = Fixtures.WithBodyScale(
            initial: FixedQ4816.Zero,
            maximum: 1d,
            minimum: 0d,
            source: source
        );

        Assert.False(
            condition: WorldDefinitionValidator.TryValidate(
                definition: zero,
                neighbours: null,
                reason: out var zeroReason
            ),
            userMessage: "a scale envelope reaching zero was expected to refuse"
        );
        Assert.Contains(
            actualString: zeroReason,
            expectedSubstring: "bodies.scale names register 'scale'"
        );

        var tooBig = Fixtures.WithBodyScale(
            initial: FixedQ4816.One,
            maximum: (source.Authoring.MaxPlacementScale + 1d),
            minimum: 0.5d,
            source: source
        );

        Assert.False(
            condition: WorldDefinitionValidator.TryValidate(
                definition: tooBig,
                neighbours: null,
                reason: out var tooBigReason
            ),
            userMessage: "a scale envelope past authoring.maxPlacementScale was expected to refuse"
        );
        Assert.Contains(
            actualString: tooBigReason,
            expectedSubstring: "authoring.maxPlacementScale"
        );
    }
    // THE LAW: the size travels with the body. An anonymous traveller lands with the scale register it carried, and the
    // destination, whose own register would be born at one, scales the body by the carried value before it first
    // advances there; a size past the destination's own envelope settles to the envelope's ceiling.
    [InlineData(0.25d, 0.25d)]
    [InlineData(1d, 1d)]
    [InlineData(5d, 1d)]
    [Theory]
    public void ACrossingCarriesTheScaleRegisterAndTheDestinationScalesTheBodyByIt(double carried, double expected) {
        using var fixture = Fixtures.FreshServer(definition: WithScaleRegister(initial: FixedQ4816.One));
        const string SourceAuthority = "origin/world";
        var address = new WorldEntityAddress(
            Authority: SourceAuthority,
            Generation: 7,
            Index: 0
        );
        var request = new WorldTransferReservationRequest(
            TransferId: 1,
            SourceAuthority: SourceAuthority,
            SourceRateHz: 30,
            SourceTick: 0,
            DeadlineSourceTick: 60,
            Border: "painting",
            BorderCapacity: null,
            PartyAllOrNothing: true,
            PeerAdmission: false,
            Members: [new WorldTransferReservationMember(
                Principal: Principal.Console,
                PreferredSlot: 0,
                Identity: null,
                Source: default,
                BodyColor: default,
                CatalogRig: 0,
                Mobility: new WorldMobilityIdentity(
                    DepartedFrom: address,
                    Epoch: 0,
                    Incarnation: address
                )
            )]
        );
        var reservation = fixture.Server.ReserveTransfer(request: request);

        Assert.True(
            condition: reservation.Accepted,
            userMessage: reservation.Reason
        );
        Assert.True(
            condition: (fixture.Server.CommitTransfer(
                members: [new WorldTransferCommitMember(
                    Profile: null,
                    HasMappedArrival: true,
                    BodyMotionProgramName: "grounded",
                    Position: default,
                    YawRadians: default,
                    PlanarVelocity: default,
                    VerticalVelocity: default,
                    ActionContinuity: new WorldTransferActionContinuity(
                        Registers: [new WorldTransferActionRegister(
                            Kind: ActionStateKind.Counter,
                            Name: Fixtures.BodyScaleRegister,
                            TimerTicks: 0UL,
                            Value: FixedQ4816.FromDouble(value: carried)
                        )]
                    )
                )],
                reason: out var reason,
                sourceAuthority: SourceAuthority,
                transferId: request.TransferId
            ) == WorldTransferStatus.Committed),
            userMessage: reason
        );

        var slot = Assert.Single(collection: reservation.BodyIndices);

        // On landing, before the body first advances here, and again after a step keeps it.
        for (var step = 0; (step < 2); step++) {
            Assert.Equal(
                actual: fixture.Server.Body(index: slot)!.Scale,
                expected: FixedQ4816.FromDouble(value: expected)
            );

            fixture.Step();
        }
    }
    [Fact]
    public void ScaleRegister_Undeclared_Refused() {
        var source = Fixtures.BuildDocument();
        var denied = source with { PopulationRaw = (source.Population with { Scale = "scale" }) };

        Assert.False(
            condition: WorldDefinitionValidator.TryValidate(
                definition: denied,
                neighbours: null,
                reason: out var reason
            ),
            userMessage: "a bodies.scale naming no register was expected to refuse"
        );
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "which state.body does not declare"
        );
    }
    [Fact]
    public void ScaleRegister_InitialBelowItsEnvelope_RefusedAndInsideAdmitted() {
        var denied = WithScaleRegister(initial: FixedQ4816.FromDouble(value: 0.01));

        Assert.False(
            condition: WorldDefinitionValidator.TryValidate(
                definition: denied,
                neighbours: null,
                reason: out var deniedReason
            ),
            userMessage: "a scale register initial below its own envelope was expected to refuse"
        );
        Assert.Contains(
            actualString: deniedReason,
            expectedSubstring: "inside its envelope"
        );

        var admitted = WithScaleRegister(initial: FixedQ4816.FromDouble(value: 0.4));

        Assert.True(
            condition: WorldDefinitionValidator.TryValidate(
                definition: admitted,
                neighbours: null,
                reason: out var controlReason
            ),
            userMessage: controlReason
        );
    }
    [InlineData(1.0, 4.0)]
    [InlineData(0.15, 0.6)]
    [InlineData(0.05, 0.2)]
    [Theory]
    public void ScaleRegister_GroundedBodyCoversItsFullScaledSpeedPerSecond(double scaleValue, double expectedMetersPerSecond) {
        var scale = FixedQ4816.FromDouble(value: scaleValue);

        using var fixture = Fixtures.FreshServer(definition: PlatformDocumentWithScale(cellValue: scale));
        var body = fixture.JoinSeat();

        body.Pose(
            pitchRadians: 0f,
            rollRadians: 0f,
            x: 0f,
            y: -0.5f,
            yawRadians: 0f,
            z: 0f
        );

        for (var tick = 0; (tick < SettleTicks); tick++) {
            fixture.Step(stepTicks: StepWidth);
        }

        Assert.True(
            condition: body.Grounded,
            userMessage: $"the body did not settle grounded on the platform; y={body.Position.Y:0.###}"
        );
        Assert.Equal(
            expected: scale,
            actual: body.Scale
        );

        var settledZ = body.FixedPosition.Z;

        for (var tick = 0; (tick < SimulationRateHz); tick++) {
            body.SubmitIntent(intent: default(PlayerIntent).WithChannel(
                ordinal: ForwardOrdinal,
                value: FixedQ4816.One
            ));
            fixture.Step(stepTicks: StepWidth);
        }

        var traveled = ((double)(settledZ - body.FixedPosition.Z));

        Assert.True(
            condition: (Math.Abs(value: (traveled - expectedMetersPerSecond)) < 0.02),
            userMessage: $"scale {scaleValue}: expected ~{expectedMetersPerSecond:0.###}m over 1s, covered {traveled:0.###}m"
        );
    }

    // A body's own gravity fall/rise/envelope (including a medium's idle/settle target), its wall hold's travel
    // speed, and its pull rate are all
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
    private const int SettleTicks = ((int)SimulationRateHz);
    private const uint SimulationRateHz = 30;
}
