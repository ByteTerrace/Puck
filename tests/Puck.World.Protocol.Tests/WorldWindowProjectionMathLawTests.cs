using System.Numerics;
using Puck.Maths;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Protocol.Tests;

/// <summary>Laws for the window projection's float map (<see cref="WorldWindowProjectionMath"/>): it agrees with the
/// simulation's fixed-point map (<see cref="WorldFrameIsometry.MapPoint"/>) for a pair of two sizes, scaling an eye's
/// offset by the pair's scale, so a window through a door onto one a quarter its size sees from a quarter as far.</summary>
public sealed class WorldWindowProjectionMathLawTests {
    // A 2.5 by 3.5 face at the origin facing +X, and a counterpart a quarter its size at (9, 0, 2) facing -X.
    private static readonly WorldFaceFrame Door = new(
        HalfDepth: FixedQ4816.FromDouble(value: 0.0625),
        HalfHeight: FixedQ4816.FromDouble(value: 1.75),
        HalfWidth: FixedQ4816.FromDouble(value: 1.25),
        Normal: FixedVector3.UnitX,
        Origin: FixedVector3.Zero,
        Right: new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.Zero, Z: -FixedQ4816.One),
        Up: FixedVector3.UnitY
    );
    private static readonly WorldFaceFrame MouseHole = new(
        HalfDepth: FixedQ4816.FromDouble(value: 0.015625),
        HalfHeight: FixedQ4816.FromDouble(value: 0.4375),
        HalfWidth: FixedQ4816.FromDouble(value: 0.3125),
        Normal: new FixedVector3(X: -FixedQ4816.One, Y: FixedQ4816.Zero, Z: FixedQ4816.Zero),
        Origin: new FixedVector3(X: FixedQ4816.FromInteger(value: 9), Y: FixedQ4816.Zero, Z: FixedQ4816.FromInteger(value: 2)),
        Right: FixedVector3.UnitZ,
        Up: FixedVector3.UnitY
    );

    [Fact]
    public void AnEyeMapsThroughAPairOfTwoSizesAsTheSimulationMapsATraveller() {
        var source = WorldFaceGeometry.FromFrame(frame: Door);
        var destination = WorldFaceGeometry.FromFrame(frame: MouseHole);
        var eye = new Vector3(x: 3f, y: 1.5f, z: -0.5f);
        var mapped = WorldWindowProjectionMath.MapPoint(
            destination: destination,
            point: eye,
            source: source
        );
        var simulated = WorldFrameIsometry.MapPoint(
            destination: in MouseHole,
            point: FixedVector3.FromVector3(value: eye),
            source: in Door
        ).ToVector3();

        Assert.Equal(expected: 0.25f, actual: WorldWindowProjectionMath.Scale(destination: destination, source: source));
        Assert.Equal(expected: 1f, actual: WorldWindowProjectionMath.Scale(destination: source, source: source));
        Assert.True(condition: (Vector3.Distance(value1: mapped, value2: simulated) < 1e-4f), userMessage: $"{mapped} against {simulated}");
        // Three metres in front of the door is three quarters of a metre behind the mouse hole's glass, looking in.
        Assert.True(condition: (Vector3.Distance(value1: mapped, value2: new Vector3(x: 9.75f, y: 0.375f, z: 1.875f)) < 1e-5f), userMessage: $"{mapped}");
    }
}
