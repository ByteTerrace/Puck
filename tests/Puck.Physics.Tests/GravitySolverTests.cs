using Puck.Maths;

namespace Puck.Physics.Tests;

public sealed class GravitySolverTests {
    private static readonly GravityParameters s_parameters = new(
        GravitationalConstant: Scalar(value: 1d),
        SofteningLength: Scalar(value: 0.05d)
    );

    [Fact]
    public void PairwiseEqualMassPairProducesExactOpposites() {
        GravityBody[] bodies = [
            new(Position: Vector(x: -1d, y: 0d, z: 0d), Mass: Scalar(value: 2d)),
            new(Position: Vector(x: 1d, y: 0d, z: 0d), Mass: Scalar(value: 2d)),
        ];
        var accelerations = new FixedVector3[bodies.Length];

        var statistics = new PairwiseGravitySolver().ComputeAccelerations(
            accelerations: accelerations,
            bodies: bodies,
            parameters: s_parameters
        );

        Assert.True(condition: (accelerations[0].X > FixedQ4816.Zero));
        Assert.Equal(expected: FixedQ4816.Zero, actual: accelerations[0].Y);
        Assert.Equal(expected: FixedQ4816.Zero, actual: accelerations[0].Z);
        Assert.Equal(expected: GravityKernelForTests.Negate(vector: accelerations[0]), actual: accelerations[1]);
        Assert.Equal(expected: 2L, actual: statistics.ExactSourceEvaluations);
        Assert.Equal(expected: 0L, actual: statistics.ApproximatedNodeEvaluations);
    }
    [Fact]
    public void ZeroMassBodyFeelsFieldWithoutBecomingASource() {
        GravityBody[] bodies = [
            new(Position: Vector(x: 0d, y: 0d, z: 0d), Mass: FixedQ4816.Zero),
            new(Position: Vector(x: 2d, y: 0d, z: 0d), Mass: FixedQ4816.One),
        ];
        var accelerations = new FixedVector3[bodies.Length];

        var statistics = new PairwiseGravitySolver().ComputeAccelerations(
            accelerations: accelerations,
            bodies: bodies,
            parameters: s_parameters
        );

        Assert.True(condition: (accelerations[0].X > FixedQ4816.Zero));
        Assert.Equal(expected: FixedVector3.Zero, actual: accelerations[1]);
        Assert.Equal(expected: 1L, actual: statistics.ExactSourceEvaluations);
    }
    [Fact]
    public void ZeroOpeningAngleIsTheBitExactPairwiseOracle() {
        var bodies = BuildBodies(count: 96);
        var pairwise = new FixedVector3[bodies.Length];
        var monopole = new FixedVector3[bodies.Length];
        var exactSolver = new PairwiseGravitySolver();
        var selectedSolver = new FastMonopoleGravitySolver(options: new FastMonopoleOptions(
            leafCapacity: 4,
            maximumDepth: 24,
            openingAngle: UnitInterval32.Zero
        ));

        var exactStatistics = exactSolver.ComputeAccelerations(accelerations: pairwise, bodies: bodies, parameters: s_parameters);
        var selectedStatistics = selectedSolver.ComputeAccelerations(accelerations: monopole, bodies: bodies, parameters: s_parameters);

        Assert.Equal(actual: monopole, expected: pairwise);
        Assert.Equal(actual: selectedStatistics, expected: exactStatistics);
    }
    [Fact]
    public void FastMonopoleIsBitDeterministicAcrossWorkspaceReuse() {
        var bodies = BuildBodies(count: 384);
        var first = new FixedVector3[bodies.Length];
        var second = new FixedVector3[bodies.Length];
        var solver = new FastMonopoleGravitySolver(options: new FastMonopoleOptions(
            leafCapacity: 8,
            maximumDepth: 32,
            openingAngle: Angle(value: 0.5d)
        ));

        var firstStatistics = solver.ComputeAccelerations(accelerations: first, bodies: bodies, parameters: s_parameters);
        var secondStatistics = solver.ComputeAccelerations(accelerations: second, bodies: bodies, parameters: s_parameters);

        Assert.Equal(actual: second, expected: first);
        Assert.Equal(actual: secondStatistics, expected: firstStatistics);
        Assert.True(condition: (firstStatistics.ApproximatedNodeEvaluations > 0L));
    }
    [Fact]
    public void FastMonopoleTracksPairwiseWithinMeasuredGlobalErrorEnvelope() {
        var bodies = BuildBodies(count: 512);
        var exact = new FixedVector3[bodies.Length];
        var approximate = new FixedVector3[bodies.Length];

        _ = new PairwiseGravitySolver().ComputeAccelerations(accelerations: exact, bodies: bodies, parameters: s_parameters);
        var statistics = new FastMonopoleGravitySolver(options: new FastMonopoleOptions(
            leafCapacity: 8,
            maximumDepth: 32,
            openingAngle: Angle(value: 0.4d)
        )).ComputeAccelerations(accelerations: approximate, bodies: bodies, parameters: s_parameters);

        var relativeRootMeanSquareError = RelativeRootMeanSquareError(actual: approximate, expected: exact);

        Assert.True(
            condition: (relativeRootMeanSquareError < 0.03d),
            userMessage: $"Measured relative RMS error {relativeRootMeanSquareError:P4} exceeded the frozen 3% envelope."
        );
        Assert.True(condition: (statistics.ApproximatedNodeEvaluations > 0L));
        Assert.True(condition: (statistics.ApproximatedSourceCount > statistics.ApproximatedNodeEvaluations));
    }
    [Fact]
    public void FastMonopoleLargePopulationPerformsSubquadraticStructuralWork() {
        const int bodyCount = 4096;
        var bodies = BuildBodies(count: bodyCount);
        var accelerations = new FixedVector3[bodyCount];
        var statistics = new FastMonopoleGravitySolver(options: new FastMonopoleOptions(
            leafCapacity: 8,
            maximumDepth: 32,
            openingAngle: Angle(value: 0.6d)
        )).ComputeAccelerations(accelerations: accelerations, bodies: bodies, parameters: s_parameters);
        var evaluatedTerms = checked((statistics.ExactSourceEvaluations + statistics.ApproximatedNodeEvaluations));
        var directedPairwiseTerms = checked((((long)bodyCount) * (bodyCount - 1L)));

        Assert.True(
            condition: (evaluatedTerms < (directedPairwiseTerms / 4L)),
            userMessage: $"Fast path evaluated {evaluatedTerms:N0} terms versus {directedPairwiseTerms:N0} directed pairwise terms."
        );
        Assert.True(condition: (statistics.TreeNodeCount > 1));
        Assert.DoesNotContain(collection: accelerations, filter: static acceleration => (acceleration == FixedVector3.Zero));
    }
    [Fact]
    public void AdaptiveFmmZeroOpeningAngleIsTheBitExactPairwiseOracle() {
        var bodies = BuildBodies(count: 96);
        var pairwise = new FixedVector3[bodies.Length];
        var fmm = new FixedVector3[bodies.Length];
        var exactStatistics = new PairwiseGravitySolver().ComputeAccelerations(accelerations: pairwise, bodies: bodies, parameters: s_parameters);
        var fmmStatistics = new AdaptiveFmmGravitySolver(options: new AdaptiveFmmOptions(
            leafCapacity: 4,
            maximumDepth: 24,
            openingAngle: UnitInterval32.Zero
        )).ComputeAccelerations(accelerations: fmm, bodies: bodies, parameters: s_parameters);

        Assert.Equal(actual: fmm, expected: pairwise);
        Assert.Equal(actual: fmmStatistics, expected: exactStatistics);
    }
    [Fact]
    public void AdaptiveFmmRunsDeterministicTranslationAndDownwardPasses() {
        var bodies = BuildBodies(count: 512);
        var first = new FixedVector3[bodies.Length];
        var second = new FixedVector3[bodies.Length];
        var solver = new AdaptiveFmmGravitySolver(options: new AdaptiveFmmOptions(
            leafCapacity: 8,
            maximumDepth: 32,
            openingAngle: Angle(value: 0.4d)
        ));

        var firstStatistics = solver.ComputeAccelerations(accelerations: first, bodies: bodies, parameters: s_parameters);
        var secondStatistics = solver.ComputeAccelerations(accelerations: second, bodies: bodies, parameters: s_parameters);

        Assert.Equal(actual: second, expected: first);
        Assert.Equal(actual: secondStatistics, expected: firstStatistics);
        Assert.True(condition: (firstStatistics.MultipoleToMultipoleTranslations > 0L));
        Assert.True(condition: (firstStatistics.MultipoleToLocalTranslations > 0L));
        Assert.True(condition: (firstStatistics.LocalToLocalTranslations > 0L));
        Assert.Equal(expected: bodies.Length, actual: firstStatistics.LocalExpansionEvaluations);
        Assert.Equal(expected: 0L, actual: firstStatistics.ApproximatedNodeEvaluations);
    }
    [Fact]
    public void AdaptiveFmmReusesItsHighWaterWorkspaceWithoutAllocating() {
        var bodies = BuildBodies(count: 512);
        var accelerations = new FixedVector3[bodies.Length];
        var solver = new AdaptiveFmmGravitySolver();

        _ = solver.ComputeAccelerations(accelerations: accelerations, bodies: bodies, parameters: s_parameters);
        _ = solver.ComputeAccelerations(accelerations: accelerations, bodies: bodies, parameters: s_parameters);

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

        _ = solver.ComputeAccelerations(accelerations: accelerations, bodies: bodies, parameters: s_parameters);
        var allocated = (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);

        Assert.Equal(actual: allocated, expected: 0L);
    }
    [Fact]
    public void AdaptiveFmmTracksPairwiseAndBarnesHutWithinMeasuredErrorEnvelope() {
        var bodies = BuildBodies(count: 512);
        var exact = new FixedVector3[bodies.Length];
        var barnesHut = new FixedVector3[bodies.Length];
        var fmm = new FixedVector3[bodies.Length];

        _ = new PairwiseGravitySolver().ComputeAccelerations(accelerations: exact, bodies: bodies, parameters: s_parameters);
        _ = new FastMonopoleGravitySolver(options: new FastMonopoleOptions(
            leafCapacity: 8,
            maximumDepth: 32,
            openingAngle: Angle(value: 0.4d)
        )).ComputeAccelerations(accelerations: barnesHut, bodies: bodies, parameters: s_parameters);
        _ = new AdaptiveFmmGravitySolver(options: new AdaptiveFmmOptions(
            leafCapacity: 8,
            maximumDepth: 32,
            openingAngle: Angle(value: 0.4d)
        )).ComputeAccelerations(accelerations: fmm, bodies: bodies, parameters: s_parameters);

        var barnesHutError = RelativeRootMeanSquareError(actual: barnesHut, expected: exact);
        var fmmError = RelativeRootMeanSquareError(actual: fmm, expected: exact);

        Assert.True(
            condition: (fmmError < 0.03d),
            userMessage: $"Measured adaptive FMM relative RMS error {fmmError:P4} exceeded the frozen 3% envelope."
        );
        Assert.True(
            condition: (fmmError <= (barnesHutError + 0.01d)),
            userMessage: $"Adaptive FMM error {fmmError:P4} diverged from Barnes-Hut's {barnesHutError:P4} by more than one percentage point."
        );
    }
    [Fact]
    public void AdaptiveFmmKeepsADistantMasslessProbeInTheTargetTree() {
        var clustered = BuildBodies(count: 64);
        var bodies = new GravityBody[(clustered.Length + 1)];

        clustered.CopyTo(array: bodies, index: 0);
        bodies[^1] = new GravityBody(
            Position: Vector(x: 20d, y: 3d, z: -2d),
            Mass: FixedQ4816.Zero
        );
        var exact = new FixedVector3[bodies.Length];
        var fmm = new FixedVector3[bodies.Length];

        _ = new PairwiseGravitySolver().ComputeAccelerations(accelerations: exact, bodies: bodies, parameters: s_parameters);
        var statistics = new AdaptiveFmmGravitySolver(options: new AdaptiveFmmOptions(
            leafCapacity: 4,
            maximumDepth: 32,
            openingAngle: Angle(value: 0.5d)
        )).ComputeAccelerations(accelerations: fmm, bodies: bodies, parameters: s_parameters);
        var probeIndex = (bodies.Length - 1);
        var exactMagnitude = Math.Abs(value: exact[probeIndex].X.Value);
        var xError = Math.Abs(value: (fmm[probeIndex].X.Value - exact[probeIndex].X.Value));

        Assert.True(
            condition: (fmm[probeIndex] != FixedVector3.Zero),
            userMessage: $"Probe exact={exact[probeIndex]}, fmm={fmm[probeIndex]}, M2L={statistics.MultipoleToLocalTranslations}."
        );
        Assert.True(condition: (statistics.MultipoleToLocalTranslations > 0L));
        Assert.True(
            condition: (xError <= ((exactMagnitude / 20L) + 2L)),
            userMessage: $"The massless probe's FMM x error was {xError} raw against an exact magnitude of {exactMagnitude} raw."
        );
    }
    [Fact]
    public void AdaptiveFmmLargePopulationUsesCellTranslationsInsteadOfTargetTreeWalks() {
        const int bodyCount = 4096;
        var smallBodies = BuildBodies(count: 512);
        var smallAccelerations = new FixedVector3[smallBodies.Length];
        var bodies = BuildBodies(count: bodyCount);
        var barnesHutAccelerations = new FixedVector3[bodyCount];
        var fmmAccelerations = new FixedVector3[bodyCount];
        var barnesHutStatistics = new FastMonopoleGravitySolver(options: new FastMonopoleOptions(
            leafCapacity: 8,
            maximumDepth: 32,
            openingAngle: Angle(value: 0.6d)
        )).ComputeAccelerations(accelerations: barnesHutAccelerations, bodies: bodies, parameters: s_parameters);
        var fmmStatistics = new AdaptiveFmmGravitySolver(options: new AdaptiveFmmOptions(
            leafCapacity: 8,
            maximumDepth: 32,
            openingAngle: Angle(value: 0.6d)
        )).ComputeAccelerations(accelerations: fmmAccelerations, bodies: bodies, parameters: s_parameters);
        var smallStatistics = new AdaptiveFmmGravitySolver(options: new AdaptiveFmmOptions(
            leafCapacity: 8,
            maximumDepth: 32,
            openingAngle: Angle(value: 0.6d)
        )).ComputeAccelerations(accelerations: smallAccelerations, bodies: smallBodies, parameters: s_parameters);
        var fmmInteractionTerms = checked((fmmStatistics.ExactSourceEvaluations + fmmStatistics.MultipoleToLocalTranslations));
        var smallInteractionTerms = checked((smallStatistics.ExactSourceEvaluations + smallStatistics.MultipoleToLocalTranslations));
        var barnesHutInteractionTerms = checked((barnesHutStatistics.ExactSourceEvaluations + barnesHutStatistics.ApproximatedNodeEvaluations));
        var directedPairwiseTerms = checked((((long)bodyCount) * (bodyCount - 1L)));

        Assert.True(
            condition: (fmmInteractionTerms < (directedPairwiseTerms / 8L)),
            userMessage: $"Adaptive FMM used {fmmInteractionTerms:N0} interaction terms versus {directedPairwiseTerms:N0} directed pairwise terms."
        );
        Assert.True(
            condition: (fmmStatistics.MultipoleToLocalTranslations < barnesHutInteractionTerms),
            userMessage: $"Adaptive FMM used {fmmStatistics.MultipoleToLocalTranslations:N0} M2L translations versus {barnesHutInteractionTerms:N0} Barnes-Hut target-walk terms."
        );
        Assert.True(
            condition: (fmmInteractionTerms < checked((smallInteractionTerms * 32L))),
            userMessage: $"An 8x population increase grew adaptive FMM interaction work from {smallInteractionTerms:N0} to {fmmInteractionTerms:N0}, too close to the quadratic 64x rate."
        );
        Assert.DoesNotContain(collection: fmmAccelerations, filter: static acceleration => (acceleration == FixedVector3.Zero));
    }
    [Fact]
    public void FastMonopoleKeepsANearSourceOutOfADistantMonopole() {
        // A heavy cluster far from the probe skews the root's center of mass away from a light source sitting right
        // beside the probe; an opening test measured against the center of mass alone would fold that near source
        // into the distant monopole.
        var bodies = new GravityBody[11];

        for (var index = 0; (index < 9); index++) {
            var jitter = (((index % 3) - 1) * 0.0625d);

            bodies[index] = new GravityBody(
                Position: Vector(
                    x: (2d + jitter),
                    y: (2d - (jitter * 0.5d)),
                    z: (2d + (jitter * 0.25d))
                ),
                Mass: Scalar(value: 100d)
            );
        }

        bodies[9] = new GravityBody(
            Position: Vector(x: 0.001d, y: 0.001d, z: 0.001d),
            Mass: Scalar(value: 1d)
        );
        bodies[10] = new GravityBody(
            Position: Vector(x: -0.01d, y: -0.01d, z: -0.01d),
            Mass: FixedQ4816.Zero
        );

        var exact = new FixedVector3[bodies.Length];
        var approximate = new FixedVector3[bodies.Length];

        _ = new PairwiseGravitySolver().ComputeAccelerations(accelerations: exact, bodies: bodies, parameters: s_parameters);
        _ = new FastMonopoleGravitySolver(options: new FastMonopoleOptions(
            leafCapacity: 8,
            maximumDepth: 32,
            openingAngle: Angle(value: 0.6d)
        )).ComputeAccelerations(accelerations: approximate, bodies: bodies, parameters: s_parameters);

        var probeIndex = (bodies.Length - 1);
        var exactMagnitude = Math.Abs(value: exact[probeIndex].X.Value);
        var xError = Math.Abs(value: (approximate[probeIndex].X.Value - exact[probeIndex].X.Value));

        Assert.True(
            condition: (xError <= (exactMagnitude / 10L)),
            userMessage: $"The outside probe's x error was {xError} raw against an exact magnitude of {exactMagnitude} raw."
        );
    }
    [Fact]
    public void AdaptiveFmmOpensCellPairsWhoseGradientLeavesTheGrid() {
        // Two compact heavy clusters: the cluster-level translation's G·M/R³ leaves the Q32.32 gradient grid, so the
        // solver must open that pair and translate (or evaluate directly) at finer cells instead of aborting.
        var bodies = new GravityBody[10];

        for (var index = 0; (index < 5); index++) {
            bodies[index] = new GravityBody(
                Position: Vector(x: (index * 0.02d), y: 0d, z: 0d),
                Mass: Scalar(value: 100_000_000d)
            );
            bodies[(index + 5)] = new GravityBody(
                Position: Vector(x: (0.5d + (index * 0.02d)), y: 0.01d, z: -0.01d),
                Mass: Scalar(value: 100_000_000d)
            );
        }

        var exact = new FixedVector3[bodies.Length];
        var fmm = new FixedVector3[bodies.Length];

        _ = new PairwiseGravitySolver().ComputeAccelerations(accelerations: exact, bodies: bodies, parameters: s_parameters);
        var statistics = new AdaptiveFmmGravitySolver(options: new AdaptiveFmmOptions(
            leafCapacity: 1,
            maximumDepth: 32,
            openingAngle: Angle(value: 0.6d)
        )).ComputeAccelerations(accelerations: fmm, bodies: bodies, parameters: s_parameters);

        var relativeRootMeanSquareError = RelativeRootMeanSquareError(actual: fmm, expected: exact);

        Assert.True(
            condition: (relativeRootMeanSquareError < 0.03d),
            userMessage: $"Measured relative RMS error {relativeRootMeanSquareError:P4} exceeded the 3% envelope."
        );
        Assert.True(condition: (statistics.MultipoleToLocalTranslations > 0L));
    }
    [Fact]
    public void AdaptiveFmmDefersAnAncestorExpansionWhoseGradientCannotBeCombined() {
        GravityBody[] bodies = [
            new(Position: Vector(x: 0d, y: 0d, z: 0d), Mass: FixedQ4816.Zero),
            new(Position: Vector(x: 1d, y: 0d, z: 0d), Mass: Scalar(value: 700_000_000d)),
            new(Position: Vector(x: 4d, y: 0d, z: 0d), Mass: Scalar(value: 20_000_000_000d)),
        ];
        var exact = new FixedVector3[bodies.Length];
        var fmm = new FixedVector3[bodies.Length];

        _ = new PairwiseGravitySolver().ComputeAccelerations(
            accelerations: exact,
            bodies: bodies,
            parameters: s_parameters
        );
        var statistics = new AdaptiveFmmGravitySolver(options: new AdaptiveFmmOptions(
            leafCapacity: 1,
            maximumDepth: 32,
            openingAngle: Angle(value: 0.6d)
        )).ComputeAccelerations(accelerations: fmm, bodies: bodies, parameters: s_parameters);

        Assert.True(condition: (statistics.DeferredLocalExpansionEvaluations > 0L));
        Assert.True(condition: (fmm[0] != FixedVector3.Zero));
        Assert.True(
            condition: (RelativeRootMeanSquareError(actual: fmm, expected: exact) < 0.2d),
            userMessage: "The overflow fallback must preserve the accepted ancestor expansion."
        );
    }
    [Fact]
    public void FactoryCreatesTheRequestedKindWithForwardedOptions() {
        var monopoleOptions = new FastMonopoleOptions(
            leafCapacity: 2,
            maximumDepth: 8,
            openingAngle: Angle(value: 0.25d)
        );
        var fmmOptions = new AdaptiveFmmOptions(
            leafCapacity: 3,
            maximumDepth: 9,
            openingAngle: Angle(value: 0.125d)
        );

        _ = Assert.IsType<PairwiseGravitySolver>(@object: GravitySolvers.Create(kind: GravitySolverKind.Pairwise));
        var monopole = Assert.IsType<FastMonopoleGravitySolver>(@object: GravitySolvers.Create(
            adaptiveFmmOptions: fmmOptions,
            fastMonopoleOptions: monopoleOptions,
            kind: GravitySolverKind.FastMonopole
        ));
        var fmm = Assert.IsType<AdaptiveFmmGravitySolver>(@object: GravitySolvers.Create(
            adaptiveFmmOptions: fmmOptions,
            fastMonopoleOptions: monopoleOptions,
            kind: GravitySolverKind.AdaptiveFmm
        ));

        Assert.Same(expected: monopoleOptions, actual: monopole.Options);
        Assert.Same(expected: fmmOptions, actual: fmm.Options);
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => GravitySolvers.Create(kind: ((GravitySolverKind)255)));
    }
    [Fact]
    public void InvalidPhysicalDomainsRefuseByName() {
        GravityBody[] negativeMass = [
            new(Position: FixedVector3.Zero, Mass: Scalar(value: -1d)),
        ];
        var accelerations = new FixedVector3[1];

        var massException = Assert.Throws<ArgumentOutOfRangeException>(testCode: () =>
            new PairwiseGravitySolver().ComputeAccelerations(accelerations: accelerations, bodies: negativeMass, parameters: s_parameters));
        var softeningException = Assert.Throws<ArgumentOutOfRangeException>(testCode: () =>
            new PairwiseGravitySolver().ComputeAccelerations(
                bodies: [],
                accelerations: [],
                parameters: new GravityParameters(
                    GravitationalConstant: FixedQ4816.One,
                    SofteningLength: FixedQ4816.Epsilon
                )
            ));

        Assert.Equal(expected: "bodies", actual: massException.ParamName);
        Assert.Equal(expected: "parameters", actual: softeningException.ParamName);
    }

    private static GravityBody[] BuildBodies(int count) {
        var side = 1;

        while (checked(((side * side) * side)) < count) {
            side++;
        }

        var bodies = new GravityBody[count];
        var half = ((side - 1) * 0.5d);

        for (var index = 0; (index < bodies.Length); index++) {
            var x = (index % side);
            var y = ((index / side) % side);
            var z = (index / (side * side));
            var offset = ((((index * 17) % 5) - 2) * 0.03125d);

            bodies[index] = new GravityBody(
                Position: Vector(
                    x: (((x - half) * 0.75d) + offset),
                    y: (((y - half) * 0.75d) - (offset * 0.5d)),
                    z: (((z - half) * 0.75d) + (offset * 0.25d))
                ),
                Mass: Scalar(value: (0.75d + ((index % 7) * 0.125d)))
            );
        }

        return bodies;
    }
    private static double RelativeRootMeanSquareError(ReadOnlySpan<FixedVector3> expected, ReadOnlySpan<FixedVector3> actual) {
        var squaredError = 0d;
        var squaredSignal = 0d;

        for (var index = 0; (index < expected.Length); index++) {
            Accumulate(componentExpected: expected[index].X, componentActual: actual[index].X, squaredError: ref squaredError, squaredSignal: ref squaredSignal);
            Accumulate(componentExpected: expected[index].Y, componentActual: actual[index].Y, squaredError: ref squaredError, squaredSignal: ref squaredSignal);
            Accumulate(componentExpected: expected[index].Z, componentActual: actual[index].Z, squaredError: ref squaredError, squaredSignal: ref squaredSignal);
        }

        return Math.Sqrt(d: (squaredError / squaredSignal));
    }
    private static void Accumulate(FixedQ4816 componentExpected, FixedQ4816 componentActual, ref double squaredError, ref double squaredSignal) {
        var expected = (componentExpected.Value / 65536d);
        var error = ((componentActual.Value - componentExpected.Value) / 65536d);

        squaredError += (error * error);
        squaredSignal += (expected * expected);
    }
    private static FixedQ4816 Scalar(double value) => FixedQ4816.FromDouble(value: value);
    private static UnitInterval32 Angle(double value) => UnitInterval32.FromDouble(value: value);
    private static FixedVector3 Vector(double x, double y, double z) =>
        new(X: Scalar(value: x), Y: Scalar(value: y), Z: Scalar(value: z));

    private static class GravityKernelForTests {
        public static FixedVector3 Negate(FixedVector3 vector) =>
            new(X: -vector.X, Y: -vector.Y, Z: -vector.Z);
    }
}
