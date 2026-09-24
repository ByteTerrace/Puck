using Puck.Commands;
using System.Buffers.Binary;
using System.Numerics;
using Xunit;

namespace Puck.World.Protocol.Tests;

/// <summary>
/// The finite-vector law for the submission wire: a command's presentation vector — a <c>RigidImpulse</c>'s impulse,
/// a <c>SnapPose</c>'s position — crosses only when every lane is finite. The decoder reads it through
/// <c>WireReader.ReadFiniteVector</c> and refuses a NaN or infinite lane by name; the encoder refuses the same vector
/// before writing it, so the codec never produces bytes it would refuse. Each refusal is paired with its finite
/// control, and the sabotaged bytes differ from the control only in the one lane under test.
/// </summary>
public sealed class NonFiniteVectorCodecLawTests {
    private static readonly Principal Actor = Principal.Seat(slot: 0);

    private static byte[] Encode(WorldCommand command) {
        Assert.True(
            condition: WorldSubmissionCodec.TryEncodeCommand(
                bytes: out var bytes,
                command: command,
                failure: out var failure
            ),
            userMessage: $"encode refused: {failure}"
        );

        return bytes;
    }
    private static void AssertRefused(byte[] bytes) {
        Assert.False(
            condition: WorldSubmissionCodec.TryDecodeCommand(
                bytes: bytes,
                command: out var command,
                failure: out var failure
            ),
            userMessage: "a non-finite vector lane must never decode"
        );
        Assert.Null(@object: command);
        Assert.Equal(
            actual: failure.Refusal,
            expected: WorldCodecRefusal.PayloadMalformed
        );
        Assert.Contains(
            actualString: failure.Detail,
            expectedSubstring: "is not finite"
        );
    }
    private static void AssertDecodes(byte[] bytes) => Assert.True(
        condition: WorldSubmissionCodec.TryDecodeCommand(
            bytes: bytes,
            command: out _,
            failure: out var failure
        ),
        userMessage: $"the finite control was expected to decode: {failure}"
    );
    private static byte[] WithLane(byte[] bytes, int offset, float value) {
        var sabotaged = bytes.ToArray();

        BinaryPrimitives.WriteSingleLittleEndian(
            destination: sabotaged.AsSpan(start: offset),
            value: value
        );

        return sabotaged;
    }

    public static TheoryData<float> NonFinite => [float.NaN, float.PositiveInfinity, float.NegativeInfinity];

    [MemberData(nameof(NonFinite))]
    [Theory]
    public void RigidImpulseWithANonFiniteLane_IsRefusedAtDecode(float lane) {
        var control = Encode(command: new WorldCommand.RigidImpulse(
            EntityIndex: 1,
            Impulse: new Vector3(
                x: 1F,
                y: 2F,
                z: 3F
            ),
            Principal: Actor
        ));

        AssertDecodes(bytes: control);

        // The impulse is the leaf's last field: its three float lanes are the final twelve bytes.
        for (var component = 0; (component < 3); component++) {
            AssertRefused(bytes: WithLane(
                bytes: control,
                offset: ((control.Length - 12) + (component * sizeof(float))),
                value: lane
            ));
        }
    }
    [MemberData(nameof(NonFinite))]
    [Theory]
    public void SnapPoseWithANonFinitePosition_IsRefusedAtDecode(float lane) {
        var control = Encode(command: new WorldCommand.SnapPose(
            EntityIndex: 0,
            Mode: SnapPoseMode.Pose,
            PitchRadians: 0F,
            Position: new Vector3(
                x: 4F,
                y: 5F,
                z: 6F
            ),
            Principal: Actor,
            RollRadians: 0F,
            YawRadians: 0.5F
        ));

        AssertDecodes(bytes: control);

        // The position precedes the three trailing angle lanes: bytes [length - 24, length - 12).
        for (var component = 0; (component < 3); component++) {
            AssertRefused(bytes: WithLane(
                bytes: control,
                offset: ((control.Length - 24) + (component * sizeof(float))),
                value: lane
            ));
        }
    }
    [MemberData(nameof(NonFinite))]
    [Theory]
    public void NonFiniteVector_IsRefusedAtEncode(float lane) {
        Assert.False(
            condition: WorldSubmissionCodec.TryEncodeCommand(
                bytes: out var bytes,
                command: new WorldCommand.RigidImpulse(
                    EntityIndex: 1,
                    Impulse: new Vector3(
                        x: 0F,
                        y: lane,
                        z: 0F
                    ),
                    Principal: Actor
                ),
                failure: out var failure
            ),
            userMessage: "the encoder must refuse the vector its own decoder refuses"
        );
        Assert.Empty(collection: bytes);
        Assert.Equal(
            actual: failure.Refusal,
            expected: WorldCodecRefusal.PayloadMalformed
        );
    }
}
