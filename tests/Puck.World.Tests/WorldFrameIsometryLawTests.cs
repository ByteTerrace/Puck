using Puck.Maths;

using Xunit;

namespace Puck.World.Tests;

/// <summary>Pins the angle composition carried by every mapped portal and invisible adjacency isometry.</summary>
public sealed class WorldFrameIsometryLawTests {
    private static readonly FixedQ4816 s_pi = FixedQ4816.FromDouble(value: Math.PI);
    private static readonly FixedQ4816 s_twoPi = (s_pi + s_pi);

    [Fact]
    public void RotationDelta_PreservesTheAuthoritativeUnwrappedMappedYaw() {
        var source = Radians(degrees: 30.0);
        var destination = Radians(degrees: 120.0);
        var delta = WorldFrameIsometry.RotationDelta(
            sourceYaw: source,
            destinationYaw: destination);

        Assert.Equal(expected: ((destination - source) + s_pi), actual: delta);
    }

    [Fact]
    public void RotationDelta_FullTurnEquivalentDestination_StaysInsideMeasuredRotationBudget() {
        var source = Radians(degrees: 31.0);
        var destination = Radians(degrees: -127.0);
        var canonical = WorldFrameIsometry.RotationDelta(sourceYaw: source, destinationYaw: destination);
        var fullTurnEquivalent = WorldFrameIsometry.RotationDelta(sourceYaw: source, destinationYaw: (destination + s_twoPi));
        var witness = new FixedVector3(X: FixedQ4816.FromDouble(value: 0.75), Y: FixedQ4816.Zero, Z: FixedQ4816.FromDouble(value: -1.25));

        var expected = WorldFrameIsometry.Rotate(value: witness, deltaYaw: canonical);
        var actual = WorldFrameIsometry.Rotate(value: witness, deltaYaw: fullTurnEquivalent);

        Assert.InRange(actual: Math.Abs(actual.X.Value - expected.X.Value), low: 0L, high: 4L);
        Assert.InRange(actual: Math.Abs(actual.Y.Value - expected.Y.Value), low: 0L, high: 4L);
        Assert.InRange(actual: Math.Abs(actual.Z.Value - expected.Z.Value), low: 0L, high: 4L);
    }

    private static FixedQ4816 Radians(double degrees) =>
        FixedQ4816.FromDouble(value: (degrees * (Math.PI / 180.0)));
}
