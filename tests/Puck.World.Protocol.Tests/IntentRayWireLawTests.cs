using Puck.Commands;
using Puck.Maths;
using Puck.Networking;
using Xunit;

namespace Puck.World.Protocol.Tests;

/// <summary>
/// The pointer-ray laws for the one intent layout every intent path shares (<see cref="WorldWireCodec.WriteIntent"/>):
/// the sixteen channel lanes cross unchanged, an absent ray costs one flag byte, a present ray adds its six raw
/// fixed-point values, and a ray quantized once at the host crosses the wire bit for bit, so the server maps exactly
/// the ray the seat produced.
/// </summary>
public sealed class IntentRayWireLawTests {
    private const int LaneBytes = (ChannelLimits.MaxChannels * sizeof(long));

    private static byte[] Encode(PlayerIntent intent) {
        var writer = new WireWriter();

        WorldWireCodec.WriteIntent(
            intent: intent,
            writer: writer
        );

        return writer.ToArray();
    }
    private static PlayerIntent Decode(byte[] bytes) {
        var reader = new WireReader(bytes: bytes);
        var intent = WorldWireCodec.ReadIntent(reader: ref reader);

        Assert.True(
            condition: reader.TryFinish(failure: out var failure),
            userMessage: $"decode refused: {failure}"
        );

        return intent;
    }
    private static PlayerIntent Channels() => default(PlayerIntent)
        .WithChannel(
            ordinal: 0,
            value: FixedQ4816.One
        )
        .WithChannel(
            ordinal: (ChannelLimits.MaxChannels - 1),
            value: -FixedQ4816.Epsilon
        );

    [Fact]
    public void AnAbsentRayCostsOneByte_AndAPresentRayAddsItsSixFixedValues() {
        var absent = Encode(intent: Channels());
        var present = Encode(intent: (Channels() with {
            SourceRay = new SourceRay(
                Direction: FixedVector3.UnitZ,
                Origin: FixedVector3.Zero
            ),
        }));

        Assert.Equal(
            actual: absent.Length,
            expected: (LaneBytes + 1)
        );
        Assert.Equal(
            actual: present.Length,
            expected: ((LaneBytes + 1) + (6 * sizeof(long)))
        );
        // The sixteen lanes are unchanged by the ray: both encodings open with the same lane bytes.
        Assert.Equal(
            actual: present.AsSpan(
                length: LaneBytes,
                start: 0
            ).ToArray(),
            expected: absent.AsSpan(
                length: LaneBytes,
                start: 0
            ).ToArray()
        );
        Assert.Null(@object: Decode(bytes: absent).SourceRay);
    }
    [Fact]
    public void AQuantizedRayCrossesTheWireLosslessly() {
        // Quantized once, as the host does, from values no fixed-point spelling carries exactly; then extremes of the
        // raw range, which no float produces, to show the layout carries every representable ray.
        var quantized = new SourceRay(
            Direction: CommandValueQuantization.QuantizeAxis3D(value: new System.Numerics.Vector3(
                x: 0.1f,
                y: -0.7071068f,
                z: 1e-5f
            )),
            Origin: CommandValueQuantization.QuantizeAxis3D(value: new System.Numerics.Vector3(
                x: -1234.5678f,
                y: 3.3333333f,
                z: 0.000123f
            ))
        );
        var extreme = new SourceRay(
            Direction: new FixedVector3(
                X: FixedQ4816.MaxValue,
                Y: FixedQ4816.MinValue,
                Z: FixedQ4816.Epsilon
            ),
            Origin: new FixedVector3(
                X: FixedQ4816.FromRawBits(value: long.MinValue),
                Y: FixedQ4816.FromRawBits(value: -1L),
                Z: FixedQ4816.FromRawBits(value: long.MaxValue)
            )
        );

        foreach (var ray in new[] { quantized, extreme }) {
            var intent = (Channels() with { SourceRay = ray });
            var decoded = Decode(bytes: Encode(intent: intent));

            Assert.Equal(
                actual: decoded,
                expected: intent
            );
            Assert.Equal(
                actual: decoded.SourceRay,
                expected: ray
            );
        }
    }
    [Fact]
    public void AnUndeclaredRayFlagIsRefused_ControlTheDeclaredFlagsDecode() {
        var bytes = Encode(intent: Channels());

        bytes[LaneBytes] = 2;

        var reader = new WireReader(bytes: bytes);

        _ = WorldWireCodec.ReadIntent(reader: ref reader);

        Assert.False(condition: reader.TryFinish(failure: out var failure));
        Assert.Equal(
            actual: failure.Refusal,
            expected: WireRefusal.EnumValueUnknown
        );
        Assert.Contains(
            actualString: failure.Detail,
            expectedSubstring: nameof(SourceRay)
        );

        bytes[LaneBytes] = 0;

        Assert.Equal(
            actual: Decode(bytes: bytes),
            expected: Channels()
        );
    }
    [Fact]
    public void TwoIntentsDifferingOnlyInTheirRayAreUnequal() {
        var withRay = (Channels() with {
            SourceRay = new SourceRay(
                Direction: FixedVector3.UnitZ,
                Origin: FixedVector3.Zero
            ),
        });

        Assert.NotEqual(
            actual: withRay,
            expected: Channels()
        );
        Assert.Equal(
            actual: withRay.WithChannel(
                ordinal: 1,
                value: FixedQ4816.One
            ).SourceRay,
            expected: withRay.SourceRay
        );
        Assert.Equal(
            actual: (withRay with { }).GetHashCode(),
            expected: withRay.GetHashCode()
        );
    }
}
