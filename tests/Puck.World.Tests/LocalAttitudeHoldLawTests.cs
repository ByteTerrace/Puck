using System.Globalization;
using System.Numerics;

using Xunit;

using Puck.Maths;
using Puck.Physics.Motion;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>
/// Contract under test: a program combining <see cref="BodyMotionOp.IntegrateLocalAttitude"/>/
/// <see cref="BodyMotionOp.ComputeLocalTargetVelocity"/>/<see cref="BodyMotionOp.IntegrateScratchVelocity"/> (a
/// body-frame 6DOF flight program) owns its whole velocity and orientation channel even when its own hold row also
/// selects <see cref="BodyMotionOp.ResolveHold"/>/<see cref="BodyMotionOp.ApplyHold"/> for a full-Lift decay arc —
/// <see cref="CompiledBodyMotionProgram.OwnsVerticalContactState"/> stays <see langword="false"/> for such a
/// program (contact resolution never folds its resolved velocity back into the channel), and
/// <c>WorldBody.Hold.cs</c>'s <c>SetFreeAttitude</c> never composes a yaw-only snap over the orientation
/// <c>IntegrateLocalAttitude</c> already built.
/// </summary>
public sealed class LocalAttitudeHoldLawTests {
    private const int PitchOrdinal = 2;
    private const int RollOrdinal = 3;
    private const int DashOrdinal = 6;

    // Per tick: orientation W/X/Y/Z, then position Y — the raw FixedQ4816 storage in hex. A body spinning under
    // IntegrateLocalAttitude while a one-tick dash kicks its Lift row's own carried residual (m_verticalVelocity):
    // X/Z carry the integrated pitch/roll, and Y (the position) bleeds the dash back to rest at the row's own Rise
    // rate, undisturbed by contact resolution.
    private static readonly string[] RecordedTrace = [
        "000000000000fffe 0000000000000155 fffffffffffffffe 0000000000000155 00000000000005dd",
        "000000000000fff9 00000000000002aa fffffffffffffffc 00000000000002aa 0000000000000bab",
        "000000000000fff0 00000000000003ff fffffffffffffffa 00000000000003ff 0000000000001169",
        "000000000000ffe4 0000000000000554 fffffffffffffff8 0000000000000554 0000000000001717",
        "000000000000ffd4 00000000000006a9 fffffffffffffff6 00000000000006a9 0000000000001cb6",
        "000000000000ffc0 00000000000007fe fffffffffffffff4 00000000000007fe 0000000000002244",
        "000000000000ffa9 0000000000000953 fffffffffffffff2 0000000000000953 00000000000027c2",
        "000000000000ff8e 0000000000000aa7 fffffffffffffff0 0000000000000aa7 0000000000002d30",
        "000000000000ff70 0000000000000bfb ffffffffffffffee 0000000000000bfb 000000000000328f",
        "000000000000ff4f 0000000000000d4f ffffffffffffffec 0000000000000d4f 00000000000037dd",
        "000000000000ff29 0000000000000ea3 ffffffffffffffea 0000000000000ea3 0000000000003d1c",
        "000000000000ff01 0000000000000ff7 ffffffffffffffe8 0000000000000ff7 000000000000424b",
    ];

    private static WorldDefinition BuildLocalAttitudeHoldDocument(float rise = 14f) {
        var channels = new WorldChannel[] {
            new(Name: "forward", Shape: ChannelShape.Bipolar, Role: ChannelRole.MoveAdvance),
            new(Name: "strafe", Shape: ChannelShape.Bipolar, Role: ChannelRole.MoveStrafe),
            new(Name: "pitch", Shape: ChannelShape.Bipolar, Role: ChannelRole.Pitch),
            new(Name: "roll", Shape: ChannelShape.Bipolar, Role: ChannelRole.Roll),
            new(Name: "turn", Shape: ChannelShape.Bipolar, Role: ChannelRole.Turn),
            new(Name: "up", Shape: ChannelShape.Bipolar, Role: ChannelRole.MoveUp),
            new(Name: "dash", Shape: ChannelShape.Binary, Composition: true),
        };
        var free = new BodyMotionProgram(
            Name: "free",
            Version: "puck.body-motion.v1",
            Kind: BodyProgramKind.Motion,
            Operations: [
                BodyMotionOp.IntegrateLocalAttitude,
                BodyMotionOp.ResolveHold,
                BodyMotionOp.ComputeLocalTargetVelocity,
                BodyMotionOp.RunActionTriggers,
                BodyMotionOp.ApplyHold,
                BodyMotionOp.IntegrateScratchVelocity,
                BodyMotionOp.CommitPose,
            ]
        );
        var wander = new BodyMotionProgram(Name: "wander", Version: "puck.body-motion.v1", Kind: BodyProgramKind.Producer, Operations: [BodyMotionOp.ProduceWanderIntent]);
        var kit = new WorldKit(
            Name: "flyer-test",
            BodyMotionProgram: "free",
            Motion: new WorldMotion(
                Speed: new WorldSpeed(Value: 4f),
                Turn: new WorldTurn(Rate: 2.5f),
                Holds: [
                    new WorldHold(
                        Bond: BodyHoldBond.Free,
                        Gravity: new WorldHoldGravity(Fall: 23f, Rise: rise),
                        Hold: BodyHoldKind.Lift,
                        Lift: 1f,
                        Name: "air"
                    ),
                ]
            ),
            ActionsRaw: new Dictionary<string, ActionSpec> {
                ["dash"] = new ActionSpec(OnPress: new ActionTrigger(Effects: [new ActionEffect.SetVerticalVelocity(Velocity: 5.5f)])),
            },
            ProducersRaw: new Dictionary<string, BodyProgramParameters> {
                ["wander"] = Fixtures.TravelerWanderParameters,
            },
            Collider: new WorldCollider.Capsule(Endpoint: new Vector3(x: 0f, y: 1f, z: 0f), Radius: 0.35f)
        );

        return Fixtures.BuildDocument() with {
            BodyMotionProgramsRaw = [free, wander],
            ChannelsRaw = channels,
            DefaultSeatKitRaw = "flyer-test",
            KitRowsRaw = [kit],
        };
    }
    private static string Hex(FixedQ4816 value) => value.Value.ToString(format: "x16", provider: CultureInfo.InvariantCulture);
    private static string TraceLine(WorldBody body) {
        var orientation = body.FixedOrientation;

        return string.Join(separator: ' ', values: [
            Hex(value: orientation.W), Hex(value: orientation.X), Hex(value: orientation.Y), Hex(value: orientation.Z),
            Hex(value: body.FixedPosition.Y),
        ]);
    }
    private static string[] LocalAttitudeTrace(WorldDefinition definition, int ticks) {
        using var fixture = Fixtures.FreshServer(definition: definition);
        var actor = WorldPrincipal.Seat(slot: 0);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(Principal: actor, Slot: actor.Index, IdentityName: null, WireProtocolKey: WorldProtocol.WireProtocolKey)).Accepted);

        var body = fixture.Server.Body(index: actor.Index)!;
        var trace = new string[ticks];

        for (var tick = 0; (tick < ticks); tick++) {
            // The one-tick dash press fires exactly once (RunActionTriggers reads the rising edge), giving
            // m_verticalVelocity a real residual for the Lift row's own decay to bleed — pitch/roll keep integrating
            // every tick alongside it.
            var intent = default(PlayerIntent)
                .WithChannel(ordinal: PitchOrdinal, value: FixedQ4816.One)
                .WithChannel(ordinal: RollOrdinal, value: FixedQ4816.One);

            if (tick == 0) {
                intent = intent.WithChannel(ordinal: DashOrdinal, value: FixedQ4816.One);
            }

            body.SubmitIntent(intent: intent);
            fixture.Step();

            trace[tick] = TraceLine(body: body);
        }

        return trace;
    }

    [Fact]
    public void OwnsVerticalContactState_ExcludesALocalAttitudeProgram_WhereAnOrdinaryHoldProgramOwnsIt() {
        var localAttitude = BodyMotionProgramFactory.Compile(program: new BodyMotionProgram(
            Name: "free-test",
            Version: "puck.body-motion.v1",
            Kind: BodyProgramKind.Motion,
            Operations: [
                BodyMotionOp.IntegrateLocalAttitude,
                BodyMotionOp.ResolveHold,
                BodyMotionOp.ComputeLocalTargetVelocity,
                BodyMotionOp.ApplyHold,
                BodyMotionOp.IntegrateScratchVelocity,
                BodyMotionOp.CommitPose,
            ]
        ));
        var ordinary = BodyMotionProgramFactory.Compile(program: new BodyMotionProgram(
            Name: "grounded-test",
            Version: "puck.body-motion.v1",
            Kind: BodyProgramKind.Motion,
            Operations: [
                BodyMotionOp.ResolveYawAttitudeAndPlanarFrame,
                BodyMotionOp.ResolveHold,
                BodyMotionOp.ComputePlanarTargetVelocity,
                BodyMotionOp.ShapeVelocity,
                BodyMotionOp.ApplyHold,
                BodyMotionOp.IntegratePlanarAndVerticalVelocity,
                BodyMotionOp.CommitPose,
            ]
        ));

        Assert.False(condition: localAttitude.OwnsVerticalContactState);
        Assert.True(condition: ordinary.OwnsVerticalContactState);
    }

    [Fact]
    public void ALocalAttitudeProgramWithALiveCollider_ReproducesTheRecordedTrace_WhereRiseChangedDiverges() {
        var ticks = RecordedTrace.Length;
        var trace = LocalAttitudeTrace(definition: BuildLocalAttitudeHoldDocument(), ticks: ticks);

        Assert.Equal(expected: RecordedTrace, actual: trace);
        Assert.NotEqual(expected: RecordedTrace, actual: LocalAttitudeTrace(definition: BuildLocalAttitudeHoldDocument(rise: 20f), ticks: ticks));

        // The composed orientation carries the integrated pitch/roll (a nonzero X and Z) rather than the yaw-only
        // shape SnapFacing would have left it in — the fold this trace exists to prove.
        Assert.NotEqual(expected: FixedQ4816.Zero, actual: FixedQ4816.FromRawBits(value: Convert.ToInt64(value: trace[^1].Split(separator: ' ')[1], fromBase: 16)));
    }
}
