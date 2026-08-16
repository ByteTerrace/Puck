namespace Puck.Physics;

/// <summary>Constructs gravity solvers behind the shared <see cref="IGravitySolver"/> contract.</summary>
public static class GravitySolvers {
    /// <summary>Creates a solver of the requested kind.</summary>
    /// <param name="kind">The evaluation strategy.</param>
    /// <param name="fastMonopoleOptions">Options used only by <see cref="GravitySolverKind.FastMonopole"/>.</param>
    /// <param name="adaptiveFmmOptions">Options used only by <see cref="GravitySolverKind.AdaptiveFmm"/>.</param>
    /// <returns>A reusable solver instance.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is not declared.</exception>
    public static IGravitySolver Create(
        GravitySolverKind kind,
        FastMonopoleOptions? fastMonopoleOptions = null,
        AdaptiveFmmOptions? adaptiveFmmOptions = null
    ) =>
        kind switch {
            GravitySolverKind.Pairwise => new PairwiseGravitySolver(),
            GravitySolverKind.FastMonopole => new FastMonopoleGravitySolver(options: fastMonopoleOptions),
            GravitySolverKind.AdaptiveFmm => new AdaptiveFmmGravitySolver(options: adaptiveFmmOptions),
            _ => throw new ArgumentOutOfRangeException(
            paramName: nameof(kind),
            actualValue: kind,
            message: "Unknown gravity solver kind."
        ),
        };
}
