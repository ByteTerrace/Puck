using Puck.Physics.Motion;

namespace Puck.Physics.Tests;

/// <summary>
/// A compiled slot envelope's two shapes admit differently and repair differently: an interval CLAMPS an
/// out-of-bound request to its nearest endpoint, a closed set has no nearest endpoint to clamp to and so falls back
/// to the slot's authored initial value. Both are total — every raw slot-domain value maps to an admitted one.
/// </summary>
public sealed class CompiledActionStateEnvelopeLawTests {
    [Fact]
    public void ClosedSetAdmitsOnlyItsMembersAndRepairsToTheInitialValue() {
        var envelope = new CompiledActionStateEnvelope(
            Minimum: 0L,
            Maximum: 0L,
            Values: [-4L, 0L, 7L]
        );

        foreach (var value in new[] { -4L, 0L, 7L, }) {
            Assert.True(condition: envelope.Contains(value: value));
            Assert.Equal(
                expected: value,
                actual: envelope.Clamp(
                    value: value,
                    initial: 7L
                )
            );
        }
        foreach (var value in new[] { -5L, -3L, 1L, 6L, 8L, long.MinValue, long.MaxValue, }) {
            Assert.False(condition: envelope.Contains(value: value));
            Assert.Equal(
                expected: 0L,
                actual: envelope.Clamp(
                    value: value,
                    initial: 0L
                )
            );
        }
    }
    [Fact]
    public void RangeClampIsIdempotentAndLandsInsideTheBound() {
        var envelope = new CompiledActionStateEnvelope(
            Minimum: -3L,
            Maximum: 11L,
            Values: null
        );

        foreach (var value in new[] { long.MinValue, -4L, -3L, 0L, 11L, 12L, long.MaxValue, }) {
            var clamped = envelope.Clamp(
                value: value,
                initial: 5L
            );

            Assert.True(condition: envelope.Contains(value: clamped));
            Assert.Equal(
                expected: clamped,
                actual: envelope.Clamp(
                    value: clamped,
                    initial: 5L
                )
            );
        }
    }
}
