using Puck.Maths;

namespace Puck.Testing;

/// <summary>Fixed-point values a law builds from decimal literals, so a law reads its geometry in the units it
/// reasons in. Every component goes through <see cref="FixedQ4816.FromDouble"/>.</summary>
internal static class FixedTestValues {
    /// <summary>Builds a vector from three decimal components.</summary>
    /// <param name="x">The X component.</param>
    /// <param name="y">The Y component.</param>
    /// <param name="z">The Z component.</param>
    public static FixedVector3 Vector(double x, double y, double z) => new(
        X: FixedQ4816.FromDouble(value: x),
        Y: FixedQ4816.FromDouble(value: y),
        Z: FixedQ4816.FromDouble(value: z)
    );
    /// <summary>Builds a position in the local frame from three decimal components.</summary>
    /// <param name="x">The X component.</param>
    /// <param name="y">The Y component.</param>
    /// <param name="z">The Z component.</param>
    public static FixedPosition Position(double x, double y, double z) => FixedPosition.FromLocal(local: Vector(
        x: x,
        y: y,
        z: z
    ));
}
