using Puck.Maths;

namespace Puck.Physics;

/// <summary>
/// Computes every distinct body pair exactly in stable input order. This is the correctness oracle for approximate
/// solvers and the preferred implementation while the admitted population is small.
/// </summary>
public sealed class PairwiseGravitySolver : IGravitySolver {
    /// <inheritdoc/>
    public GravitySolveStatistics ComputeAccelerations(
        ReadOnlySpan<GravityBody> bodies,
        Span<FixedVector3> accelerations,
        GravityParameters parameters
    ) {
        var prepared = GravityKernel.Validate(
            accelerations: accelerations,
            bodies: bodies,
            parameters: parameters
        );
        var exactSourceEvaluations = 0L;

        if (prepared.GravitationalConstant <= FixedQ4816.Zero) {
            return new GravitySolveStatistics(
                BodyCount: bodies.Length,
                TreeNodeCount: 0,
                ExactSourceEvaluations: 0L,
                ApproximatedNodeEvaluations: 0L,
                ApproximatedSourceCount: 0L
            );
        }

        // The i<j walk still accumulates each target's sources in ascending input order: sources below a target arrive
        // as i advances, then sources above it arrive through that target's own inner loop.
        for (var firstIndex = 0; (firstIndex < bodies.Length); firstIndex++) {
            for (var secondIndex = (firstIndex + 1); (secondIndex < bodies.Length); secondIndex++) {
                GravityKernel.AccumulatePair(
                    accelerations: accelerations,
                    bodies: bodies,
                    exactSourceEvaluations: ref exactSourceEvaluations,
                    firstIndex: firstIndex,
                    parameters: in prepared,
                    secondIndex: secondIndex
                );
            }
        }

        return new GravitySolveStatistics(
            BodyCount: bodies.Length,
            TreeNodeCount: 0,
            ExactSourceEvaluations: exactSourceEvaluations,
            ApproximatedNodeEvaluations: 0L,
            ApproximatedSourceCount: 0L
        );
    }
}
