using Puck.World.Protocol;
using Puck.World.Server;
using System.Numerics;
using Xunit;

namespace Puck.World.Tests;

/// <summary>The MoveAdvance/MoveStrafe pair's authored frame (<see cref="ChannelFrame"/>): meaningful only on those
/// two roles, declared identically by both (the pair rotates together), and refused beside a kit on the sim's own
/// Heading arm (which would rotate an already-composed pair a second time); the table exposes it as one value.</summary>
public sealed class ChannelFrameLawTests {
    private static readonly WorldChannel Turn = new(Name: "turn", Shape: ChannelShape.Bipolar, Role: ChannelRole.Turn);

    private static WorldChannel Forward(ChannelFrame frame) => new(Name: "forward", Shape: ChannelShape.Bipolar, Role: ChannelRole.MoveAdvance, Frame: frame);
    private static WorldChannel Strafe(ChannelFrame frame) => new(Name: "strafe", Shape: ChannelShape.Bipolar, Role: ChannelRole.MoveStrafe, Frame: frame);
    // The sim's yaw convention: facing (-sin yaw, -cos yaw), read back from the attitude the body is drawn in.
    private static float AttitudeYaw(Quaternion orientation) {
        var facing = Vector3.Transform(
            value: new Vector3(x: 0f, y: 0f, z: -1f),
            rotation: orientation
        );

        return MathF.Atan2(
            y: -facing.X,
            x: -facing.Z
        );
    }
    private static bool TryValidate(WorldDefinition definition) => WorldDefinitionValidator.TryValidate(
        definition: definition,
        neighbours: null,
        reason: out _
    );

    [Fact]
    public void FrameOnANonMoveRole_Refuses_ControlOnThePairClean() {
        Laws.RefusalWithControl(
            lawId: "channels.frame-move-roles-only",
            deniedOutcome: static () => TryValidate(definition: (Fixtures.BuildDocument() with {
                ChannelsRaw = [Forward(frame: ChannelFrame.World), Strafe(frame: ChannelFrame.World), Turn with { Frame = ChannelFrame.Heading }],
            })),
            controlOutcome: static () => TryValidate(definition: (Fixtures.BuildDocument() with {
                ChannelsRaw = [Forward(frame: ChannelFrame.Heading), Strafe(frame: ChannelFrame.Heading), Turn],
            }))
        );
    }
    [Fact]
    public void PairDeclaringDifferentFrames_Refuses_ControlSameFrameClean() {
        Laws.RefusalWithControl(
            lawId: "channels.frame-pair-agrees",
            deniedOutcome: static () => TryValidate(definition: (Fixtures.BuildDocument() with {
                ChannelsRaw = [Forward(frame: ChannelFrame.Camera), Strafe(frame: ChannelFrame.Heading), Turn],
            })),
            controlOutcome: static () => TryValidate(definition: (Fixtures.BuildDocument() with {
                ChannelsRaw = [Forward(frame: ChannelFrame.Camera), Strafe(frame: ChannelFrame.Camera), Turn],
            }))
        );
    }
    [Fact]
    public void FramedPairUnderAHeadingKit_Refuses_ControlWorldKitClean() {
        static WorldDefinition WithKitFrame(MotionMoveFrame kitFrame) {
            var document = Fixtures.BuildDocument();
            var kit = document.Kits[0];

            return (document with {
                ChannelsRaw = [Forward(frame: ChannelFrame.Heading), Strafe(frame: ChannelFrame.Heading), Turn],
                KitsRaw = [kit with { Motion = ((WorldMotionModel.Grounded)kit.Motion) with { MoveFrame = kitFrame } }],
            });
        }

        Laws.RefusalWithControl(
            lawId: "channels.frame-needs-world-kit",
            deniedOutcome: static () => TryValidate(definition: WithKitFrame(kitFrame: MotionMoveFrame.Heading)),
            controlOutcome: static () => TryValidate(definition: WithKitFrame(kitFrame: MotionMoveFrame.World))
        );
    }
    /// <summary>Under a World-frame kit with facing snap, a strafe angles the body's drawn attitude toward its
    /// travel while the HEADING (<see cref="WorldBody.FixedYaw"/> — the Turn role's integral, the frame a
    /// heading-framed pair moves in) holds; the tick movement stops, the body KEEPS the way it was facing — the
    /// heading adopts the attitude rather than the attitude swinging back.</summary>
    [Fact]
    public void FacingSnap_AnglesTheAttitudeTowardTravel_LeavesTheHeading_UntilMovementStops() {
        var document = Fixtures.BuildDocument();
        var kit = document.Kits[0];

        using var fixture = Fixtures.FreshServer(definition: (document with {
            KitsRaw = [kit with { Motion = ((WorldMotionModel.Grounded)kit.Motion) with { MoveFrame = MotionMoveFrame.World, FacingSnap = true } }],
        }));
        var actor = WorldPrincipal.Seat(slot: 0);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(Principal: actor, Slot: actor.Index, IdentityName: null, WireProtocolKey: WorldProtocol.WireProtocolKey)).Accepted);

        var body = fixture.Server.Body(index: actor.Index)!;
        var strafeOrdinal = 1;
        var heading = body.FixedYaw;

        for (var tick = 0; (tick < 24); tick++) {
            body.SubmitIntent(intent: default(PlayerIntent).WithChannel(ordinal: strafeOrdinal, value: Puck.Maths.FixedQ4816.One));
            fixture.Step();
        }

        var attitudeYaw = AttitudeYaw(orientation: body.Orientation);

        Assert.Equal(expected: heading, actual: body.FixedYaw);
        Assert.True(condition: (MathF.Abs(x: attitudeYaw) > 1f), userMessage: $"a full strafe should angle the attitude toward its travel; attitude yaw {attitudeYaw}");

        for (var tick = 0; (tick < 4); tick++) {
            body.SubmitIntent(intent: default);
            fixture.Step();
        }

        Assert.True(condition: (MathF.Abs(x: (((float)((double)body.FixedYaw)) - attitudeYaw)) < 1e-3f), userMessage: $"at rest the heading adopts the way the body faced; heading {((double)body.FixedYaw):0.###}, attitude {attitudeYaw:0.###}");
        Assert.True(condition: (MathF.Abs(x: (AttitudeYaw(orientation: body.Orientation) - attitudeYaw)) < 1e-3f), userMessage: "at rest the attitude holds");
    }
    [Fact]
    public void TableReadsThePairsFrame_WorldWhenUndeclared() {
        var framed = WorldChannelTable.Compile(channels: [Forward(frame: ChannelFrame.Heading), Strafe(frame: ChannelFrame.Heading), Turn]);
        var bare = WorldChannelTable.Compile(channels: [Forward(frame: ChannelFrame.World), Strafe(frame: ChannelFrame.World), Turn]);

        Assert.Equal(expected: ChannelFrame.Heading, actual: framed.MoveFrame);
        Assert.Equal(expected: ChannelFrame.Heading, actual: framed.Frame(ordinal: framed.RoleOrdinals.MoveStrafe));
        Assert.Equal(expected: ChannelFrame.World, actual: framed.Frame(ordinal: framed.RoleOrdinals.Turn));
        Assert.Equal(expected: ChannelFrame.World, actual: bare.MoveFrame);
        Assert.Equal(expected: ChannelFrame.World, actual: WorldChannelTable.Empty.MoveFrame);
    }
}
