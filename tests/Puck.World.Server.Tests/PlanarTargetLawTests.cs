using Puck.Commands;
using Xunit;

using Puck.Maths;
using Puck.Physics.Motion;
using Puck.World.Protocol;

namespace Puck.World.Server.Tests;

/// <summary>
/// A body motion program's <c>planarTarget</c> parameters shape what <c>ComputePlanarTargetVelocity</c> commands:
/// the target decomposes against the body's heading, travel across it reaches <c>strafe</c> of the move speed and
/// travel against it <c>backward</c>, while forward travel keeps the full speed. The same program without the
/// parameters is the control, and the validator refuses the parameters by name where the program cannot run them.
/// </summary>
public sealed class PlanarTargetLawTests {
    private const int ForwardOrdinal = 0;
    private const int StrafeOrdinal = 1;

    private static readonly BodyMotionOp[] Walk = [
        BodyMotionOp.ResolveYawAttitudeAndPlanarFrame,
        BodyMotionOp.ResolveHold,
        BodyMotionOp.ComputePlanarTargetVelocity,
        BodyMotionOp.ShapeVelocity,
        BodyMotionOp.IntegratePlanarAndVerticalVelocity,
        BodyMotionOp.CommitPose,
    ];

    private static WorldDefinition Document(BodyPlanarTarget? planarTarget, IReadOnlyList<BodyMotionOp>? operations = null) {
        var channels = new WorldChannel[] {
            new(
                Name: "forward",
                Shape: ChannelShape.Bipolar,
                Role: ChannelRole.MoveAdvance
            ),
            new(
                Name: "strafe",
                Shape: ChannelShape.Bipolar,
                Role: ChannelRole.MoveStrafe
            ),
            new(
                Name: "turn",
                Shape: ChannelShape.Bipolar,
                Role: ChannelRole.Turn
            ),
        };
        var walk = new BodyMotionProgram(
            Name: "walk",
            Version: BodyMotionProgram.CurrentVersion,
            Kind: BodyProgramKind.Motion,
            Operations: (operations ?? Walk),
            PlanarTarget: planarTarget
        );
        var kit = new WorldKit(
            Name: "walker-test",
            BodyMotionProgram: "walk",
            Motion: new WorldMotion(
                Speed: new WorldSpeed(Value: 4f),
                Turn: new WorldTurn(Rate: 2.5f),
                Holds: [
                    new WorldHold(
                        Bond: BodyHoldBond.Free,
                        Envelope: new WorldHoldEnvelope(SinkSpeed: 20f),
                        Gravity: new WorldHoldGravity(
                            Fall: 23f,
                            Rise: 14f
                        ),
                        Hold: BodyHoldKind.Gravity,
                        Name: "air"
                    ),
                ],
                Shaping: [new WorldShaping(Along: new WorldShapingAlong())]
            ),
            Collider: null
        );

        return Fixtures.BuildDocument() with {
            ChannelsRaw = channels,
            BodyMotionProgramsRaw = [walk],
            KitRowsRaw = [kit],
            DefaultSeatKitRaw = "walker-test",
        };
    }
    // One tick of the given stick, from rest, read back as the body's planar velocity.
    private static (double X, double Z) OneTick(WorldDefinition definition, FixedQ4816 forward, FixedQ4816 strafe) {
        using var fixture = Fixtures.FreshServer(definition: definition);
        var actor = Principal.Seat(slot: 0);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(
            Principal: actor,
            Slot: actor.Index,
            IdentityName: null,
            WireProtocolKey: WorldProtocol.WireProtocolKey
        )).Accepted);

        var body = fixture.Server.Body(index: actor.Index)!;

        body.SubmitIntent(intent: default(PlayerIntent).WithChannel(
            ordinal: ForwardOrdinal,
            value: forward
        ).WithChannel(
            ordinal: StrafeOrdinal,
            value: strafe
        ));
        fixture.Step();

        var velocity = body.CaptureTransferState().PlanarVelocity;

        return (((double)velocity.X), ((double)velocity.Z));
    }

    [Fact]
    public void StrafeAndBackpedalReachTheirFractionsWhileForwardKeepsTheFullSpeed() {
        var shaped = Document(planarTarget: new BodyPlanarTarget(
            Backward: 0.25f,
            Strafe: 0.5f
        ));
        var control = Document(planarTarget: null);

        // Heading yaw 0 faces -Z: forward is -Z, strafe right is +X, backward is +Z.
        Assert.Equal(expected: -4d, actual: OneTick(definition: shaped, forward: FixedQ4816.One, strafe: FixedQ4816.Zero).Z, tolerance: 1e-3);
        Assert.Equal(expected: 1d, actual: OneTick(definition: shaped, forward: -FixedQ4816.One, strafe: FixedQ4816.Zero).Z, tolerance: 1e-3);
        Assert.Equal(expected: 2d, actual: OneTick(definition: shaped, forward: FixedQ4816.Zero, strafe: FixedQ4816.One).X, tolerance: 1e-3);
        Assert.Equal(expected: 4d, actual: OneTick(definition: control, forward: -FixedQ4816.One, strafe: FixedQ4816.Zero).Z, tolerance: 1e-3);
        Assert.Equal(expected: 4d, actual: OneTick(definition: control, forward: FixedQ4816.Zero, strafe: FixedQ4816.One).X, tolerance: 1e-3);
    }
    [Fact]
    public void TheValidatorRefusesParametersTheProgramCannotRunOrThatLeaveTheirRange() {
        Assert.True(condition: WorldDefinitionValidator.TryValidate(
            definition: Document(planarTarget: new BodyPlanarTarget(Strafe: 0.85f, Backward: 0.7f)),
            neighbours: null,
            reason: out var accepted
        ), userMessage: accepted);

        Assert.False(condition: WorldDefinitionValidator.TryValidate(
            definition: Document(planarTarget: new BodyPlanarTarget(Strafe: 1.5f)),
            neighbours: null,
            reason: out var range
        ));
        Assert.Contains(expectedSubstring: "bodyMotionPrograms[0].planarTarget.strafe", actualString: range, comparisonType: StringComparison.Ordinal);

        Assert.False(condition: WorldDefinitionValidator.TryValidate(
            definition: Document(planarTarget: new BodyPlanarTarget(Backward: -0.1f)),
            neighbours: null,
            reason: out var backward
        ));
        Assert.Contains(expectedSubstring: "bodyMotionPrograms[0].planarTarget.backward", actualString: backward, comparisonType: StringComparison.Ordinal);

        Assert.False(condition: WorldDefinitionValidator.TryValidate(
            definition: Document(
                operations: [BodyMotionOp.ResolveYawAttitudeAndPlanarFrame, BodyMotionOp.ResolveHold, BodyMotionOp.IntegratePlanarAndVerticalVelocity, BodyMotionOp.CommitPose],
                planarTarget: new BodyPlanarTarget(Strafe: 0.5f)
            ),
            neighbours: null,
            reason: out var unselected
        ));
        Assert.Contains(expectedSubstring: "planarTarget parameterizes 'ComputePlanarTargetVelocity'", actualString: unselected, comparisonType: StringComparison.Ordinal);
    }
}
