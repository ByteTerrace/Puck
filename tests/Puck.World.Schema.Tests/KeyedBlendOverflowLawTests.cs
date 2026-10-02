using System.Numerics;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>
/// CONTRACT UNDER TEST: a keyed value blends inside the interval its two keys span, whatever finite keys they are.
/// Two keys of opposite sign near the largest float differ by more than a float holds, and the blend between them
/// is still their midpoint, never an infinity.
/// </summary>
public sealed class KeyedBlendOverflowLawTests {
    private const double Span = 100d;

    private static WorldKeyTrack<T> Track<T>(T first, T second) => new(
        clock: "day",
        keys: [
            new WorldKey<T>(At: 0d, Ease: WorldEase.Linear, Value: first),
            new WorldKey<T>(At: 50d, Ease: WorldEase.Linear, Value: second),
        ]
    );

    [InlineData(0.25d, -1.5e38f)]
    [InlineData(0.5d, 0f)]
    [InlineData(0.75d, 1.5e38f)]
    [Theory]
    public void A_scalar_blends_between_keys_of_opposite_extremes_without_overflowing(double along, float expected) {
        var blended = WorldKeyResolver.Scalar(phase: (0.5d * along), span: Span, track: Track(first: -3e38f, second: 3e38f));

        Assert.Equal(expected: expected, actual: blended, tolerance: (MathF.Abs(x: expected) * 1e-6f));
    }
    [Fact]
    public void A_vector_blends_each_component_between_keys_of_opposite_extremes_without_overflowing() {
        var two = WorldKeyResolver.Vector(phase: 0.25d, span: Span, track: Track(first: new Vector2(x: -3e38f, y: 3e38f), second: new Vector2(x: 3e38f, y: -3e38f)));
        var three = WorldKeyResolver.Vector(phase: 0.25d, span: Span, track: Track(first: new Vector3(x: -3e38f, y: 3e38f, z: -3e38f), second: new Vector3(x: 3e38f, y: -3e38f, z: 3e38f)));

        Assert.Equal(expected: Vector2.Zero, actual: two);
        Assert.Equal(expected: Vector3.Zero, actual: three);
    }
}
