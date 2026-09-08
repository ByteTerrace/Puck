using Puck.Maths;

namespace Puck.Physics;

/// <summary>The world-axis unit vectors and axis-sign arithmetic the analytic collider queries share, and the
/// box-interior nearest-exit-face rule <see cref="FixedStaticCollider"/>'s sphere push and
/// <see cref="FixedSurfaceQuery"/>'s box query both resolve an interior probe with. One home so a correction to the
/// exit-face tie-break reaches every caller in the same change.</summary>
internal static class FixedAxisMath {
    /// <summary>The world X axis.</summary>
    internal static readonly FixedVector3 UnitX = new(
        X: FixedQ4816.One,
        Y: FixedQ4816.Zero,
        Z: FixedQ4816.Zero
    );
    /// <summary>The world Y axis.</summary>
    internal static readonly FixedVector3 UnitY = new(
        X: FixedQ4816.Zero,
        Y: FixedQ4816.One,
        Z: FixedQ4816.Zero
    );
    /// <summary>The world Z axis.</summary>
    internal static readonly FixedVector3 UnitZ = new(
        X: FixedQ4816.Zero,
        Y: FixedQ4816.Zero,
        Z: FixedQ4816.One
    );

    /// <summary>Resolves a probe inside (or exactly on the boundary of) an axis-aligned box to its nearest exit
    /// face: the axis with the smallest gap to its half-extent, ties breaking X, then Y, then Z.</summary>
    /// <param name="local">The probe relative to the box center, in box axes.</param>
    /// <param name="halfExtents">The box half-extents.</param>
    /// <returns>The exit face's signed unit normal, the box-local surface point (<paramref name="local"/> with the
    /// chosen axis snapped to the face), and the chosen axis's gap.</returns>
    internal static (FixedVector3 Normal, FixedVector3 SurfaceLocal, FixedQ4816 Gap) BoxInteriorExit(FixedVector3 local, FixedVector3 halfExtents) {
        var gapX = (halfExtents.X - FixedQ4816.Abs(value: local.X));
        var gapY = (halfExtents.Y - FixedQ4816.Abs(value: local.Y));
        var gapZ = (halfExtents.Z - FixedQ4816.Abs(value: local.Z));

        if ((gapX <= gapY) && (gapX <= gapZ)) {
            var axisSign = Sign(value: local.X);

            return (Normal: (UnitX * axisSign), SurfaceLocal: new FixedVector3(X: (halfExtents.X * axisSign), Y: local.Y, Z: local.Z), Gap: gapX);
        }

        if (gapY <= gapZ) {
            var axisSign = Sign(value: local.Y);

            return (Normal: (UnitY * axisSign), SurfaceLocal: new FixedVector3(X: local.X, Y: (halfExtents.Y * axisSign), Z: local.Z), Gap: gapY);
        }

        var signZ = Sign(value: local.Z);

        return (Normal: (UnitZ * signZ), SurfaceLocal: new FixedVector3(X: local.X, Y: local.Y, Z: (halfExtents.Z * signZ)), Gap: gapZ);
    }
    /// <summary>Maps a value to its axis sign: negative to <c>-1</c>, zero and positive to <c>+1</c> — never zero,
    /// so the result always scales a unit axis to a usable direction.</summary>
    /// <param name="value">The value whose sign selects the direction.</param>
    internal static FixedQ4816 Sign(FixedQ4816 value) => ((value < FixedQ4816.Zero)
        ? -FixedQ4816.One
        : FixedQ4816.One
    );
}
