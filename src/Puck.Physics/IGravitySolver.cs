using Puck.Maths;

namespace Puck.Physics;

/// <summary>Computes deterministic gravitational acceleration for a snapshot of point bodies.</summary>
public interface IGravitySolver {
    /// <summary>Computes one acceleration per input body.</summary>
    /// <param name="bodies">The bodies in stable simulation order.</param>
    /// <param name="accelerations">The destination; its first <paramref name="bodies"/> length entries are overwritten.</param>
    /// <param name="parameters">The constants governing every interaction.</param>
    /// <returns>Structural work counters for diagnostics and performance assertions.</returns>
    /// <exception cref="ArgumentException"><paramref name="accelerations"/> is shorter than <paramref name="bodies"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A mass or parameter is outside its admitted domain.</exception>
    /// <exception cref="OverflowException">The supplied physical envelope exceeds Q48.16 intermediate range.</exception>
    GravitySolveStatistics ComputeAccelerations(
        ReadOnlySpan<GravityBody> bodies,
        Span<FixedVector3> accelerations,
        GravityParameters parameters
    );
}
