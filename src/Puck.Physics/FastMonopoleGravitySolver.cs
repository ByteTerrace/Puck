using Puck.Maths;

namespace Puck.Physics;

/// <summary>
/// A deterministic Barnes-Hut-style octree solver. Distant cells contribute a monopole consisting of their total
/// mass at their fixed-point center of mass; nearby cells open until individual sources are evaluated.
/// </summary>
/// <remarks>
/// This is a hierarchical monopole treecode, not the higher-order Greengard-Rokhlin fast multipole method: it has
/// expected <c>O(N log N)</c> evaluation on well-distributed inputs and an <c>O(N²)</c> worst case. The instance owns
/// reusable scratch arrays and is allocation-free after it has reached a workload's high-water mark. It is not safe
/// for concurrent calls.
/// </remarks>
public sealed class FastMonopoleGravitySolver : IGravitySolver {
    private readonly FastMonopoleOptions m_options;

    private long m_approximatedNodeEvaluations;
    private long m_approximatedSourceCount;
    private long m_exactSourceEvaluations;
    private int m_liveNodeCount;
    private int m_nodeSlotCount;

    private readonly PairwiseGravitySolver m_pairwise = new();
    private int[] m_sourceIndices = [];
    private int[] m_partitionScratch = [];
    private MonopoleNode[] m_nodes = [];

    /// <summary>Creates a solver with the supplied options, or <see cref="FastMonopoleOptions"/>' defaults.</summary>
    /// <param name="options">The tree shape and opening rule.</param>
    public FastMonopoleGravitySolver(FastMonopoleOptions? options = null) {
        m_options = (options ?? new FastMonopoleOptions());
    }

    /// <summary>Gets this solver's immutable option set.</summary>
    public FastMonopoleOptions Options => m_options;

    private void AccumulateNode(
        int nodeIndex,
        int targetIndex,
        FixedVector3 targetPosition,
        ReadOnlySpan<GravityBody> bodies,
        PreparedGravityParameters parameters,
        ref FixedVector3 acceleration
    ) {
        ref readonly var node = ref m_nodes[nodeIndex];

        if (node.Count == 0) {
            return;
        }

        if (node.FirstChild < 0) {
            for (var offset = 0; (offset < node.Count); offset++) {
                var sourceIndex = m_sourceIndices[(node.Start + offset)];

                if (sourceIndex == targetIndex) {
                    continue;
                }

                var interaction = GravityKernel.PrepareInteraction(
                    target: targetPosition,
                    source: bodies[sourceIndex].Position,
                    softeningSquared: parameters.SofteningSquared
                );
                var contribution = GravityKernel.Acceleration(
                    interaction: in interaction,
                    sourceMass: bodies[sourceIndex].Mass,
                    gravitationalConstant: parameters.GravitationalConstant
                );

                acceleration = GravityKernel.AddChecked(
                    left: acceleration,
                    right: contribution
                );
                m_exactSourceEvaluations++;
            }

            return;
        }

        if (!Contains(
            node: in node,
            position: targetPosition
        )) {
            // The opening test measures the target's distance to the cell's cube, never to its center of mass: a
            // mass-skewed center can sit far across the cell from a source adjacent to the target, and a test against
            // the center alone would fold that near source into a distant monopole.
            if (
                TryGetCubeDistanceSquared(
                distanceSquared: out var cubeDistanceSquared,
                node: in node,
                position: targetPosition
            ) &&
                GravityOctree.IsSideWithinOpeningAngle(
                sideRaw: (((UInt128)((ulong)node.HalfSize.Value)) << 1),
                openingAngle: m_options.OpeningAngle,
                distanceSquared: cubeDistanceSquared
            )
            ) {
                var interaction = GravityKernel.PrepareInteraction(
                    target: targetPosition,
                    source: node.CenterOfMass,
                    softeningSquared: parameters.SofteningSquared
                );
                var contribution = GravityKernel.Acceleration(
                    interaction: in interaction,
                    sourceMass: node.TotalMass,
                    gravitationalConstant: parameters.GravitationalConstant
                );

                acceleration = GravityKernel.AddChecked(
                    left: acceleration,
                    right: contribution
                );
                m_approximatedNodeEvaluations++;
                m_approximatedSourceCount += node.Count;

                return;
            }
        }

        for (var octant = 0; (octant < 8); octant++) {
            var childIndex = (node.FirstChild + octant);

            if (m_nodes[childIndex].Count > 0) {
                AccumulateNode(
                    acceleration: ref acceleration,
                    bodies: bodies,
                    nodeIndex: childIndex,
                    parameters: parameters,
                    targetIndex: targetIndex,
                    targetPosition: targetPosition
                );
            }
        }
    }
    private AggregateResult Aggregate(int start, int count, ReadOnlySpan<GravityBody> bodies) {
        var totalMassRaw = 0L;
        var weightedX = Int128.Zero;
        var weightedY = Int128.Zero;
        var weightedZ = Int128.Zero;

        for (var offset = 0; (offset < count); offset++) {
            ref readonly var body = ref bodies[m_sourceIndices[(start + offset)]];

            totalMassRaw = checked((totalMassRaw + body.Mass.Value));
            weightedX = checked((weightedX + (((Int128)body.Position.X.Value) * body.Mass.Value)));
            weightedY = checked((weightedY + (((Int128)body.Position.Y.Value) * body.Mass.Value)));
            weightedZ = checked((weightedZ + (((Int128)body.Position.Z.Value) * body.Mass.Value)));
        }

        if (totalMassRaw <= 0L) {
            throw new InvalidOperationException(message: "A non-empty monopole node has no positive mass.");
        }

        return new AggregateResult(
            CenterOfMass: new FixedVector3(
                X: FixedQ4816.FromRawBits(value: GravityKernel.RoundDivide(
                    numerator: weightedX,
                    positiveDenominator: unchecked((ulong)totalMassRaw)
                )),
                Y: FixedQ4816.FromRawBits(value: GravityKernel.RoundDivide(
                    numerator: weightedY,
                    positiveDenominator: unchecked((ulong)totalMassRaw)
                )),
                Z: FixedQ4816.FromRawBits(value: GravityKernel.RoundDivide(
                    numerator: weightedZ,
                    positiveDenominator: unchecked((ulong)totalMassRaw)
                ))
            ),
            TotalMass: FixedQ4816.FromRawBits(value: totalMassRaw)
        );
    }
    private void BuildTree(ReadOnlySpan<GravityBody> bodies, int sourceCount) {
        var root = GravityOctree.ComputeRootBounds(
            bodies: bodies,
            count: sourceCount,
            indices: m_sourceIndices
        );

        GravityOctree.EnsureNodeCapacity(
            nodes: ref m_nodes,
            required: 1
        );
        m_nodeSlotCount = 1;
        var builder = new OctreeBuilder(owner: this);

        GravityOctree.BuildNode(
            builder: ref builder,
            nodes: ref m_nodes,
            nodeSlotCount: ref m_nodeSlotCount,
            liveNodeCount: ref m_liveNodeCount,
            indices: m_sourceIndices,
            partitionScratch: m_partitionScratch,
            leafCapacity: m_options.LeafCapacity,
            maximumDepth: m_options.MaximumDepth,
            nodeIndex: 0,
            start: 0,
            count: sourceCount,
            center: root.Center,
            halfSize: root.HalfSize,
            depth: 0,
            bodies: bodies
        );
    }
    private int CollectSources(ReadOnlySpan<GravityBody> bodies) {
        GravityOctree.EnsureIndexCapacity(
            indices: ref m_sourceIndices,
            partitionScratch: ref m_partitionScratch,
            required: bodies.Length
        );
        var sourceCount = 0;

        for (var index = 0; (index < bodies.Length); index++) {
            if (bodies[index].Mass > FixedQ4816.Zero) {
                m_sourceIndices[sourceCount++] = index;
            }
        }

        return sourceCount;
    }
    private static bool Contains(FixedVector3 position, in MonopoleNode node) {
        var half = node.HalfSize.Value;

        return (
            (MagnitudeDifference(
            left: position.X.Value,
            right: node.Center.X.Value
        ) <= half) &&
            (MagnitudeDifference(
            left: position.Y.Value,
            right: node.Center.Y.Value
        ) <= half) &&
            (MagnitudeDifference(
            left: position.Z.Value,
            right: node.Center.Z.Value
        ) <= half)
        );
    }
    private static long MagnitudeDifference(long left, long right) {
        var difference = (((Int128)left) - right);
        var magnitude = ((difference < Int128.Zero)
            ? -difference
            : difference
        );

        return ((magnitude > long.MaxValue)
            ? long.MaxValue
            : unchecked((long)magnitude)
        );
    }
    private void ResetCounters() {
        m_nodeSlotCount = 0;
        m_liveNodeCount = 0;
        m_exactSourceEvaluations = 0L;
        m_approximatedNodeEvaluations = 0L;
        m_approximatedSourceCount = 0L;
    }
    private GravitySolveStatistics Statistics(int bodyCount) =>
        new(
            BodyCount: bodyCount,
            TreeNodeCount: m_liveNodeCount,
            ExactSourceEvaluations: m_exactSourceEvaluations,
            ApproximatedNodeEvaluations: m_approximatedNodeEvaluations,
            ApproximatedSourceCount: m_approximatedSourceCount
        );
    private static bool TryGetCubeDistanceSquared(FixedVector3 position, in MonopoleNode node, out FixedQ4816 distanceSquared) {
        var half = node.HalfSize.Value;
        var offset = new FixedVector3(
            X: FixedQ4816.FromRawBits(value: Math.Max(
                val1: 0L,
                val2: (MagnitudeDifference(
                    left: position.X.Value,
                    right: node.Center.X.Value
                ) - half)
            )),
            Y: FixedQ4816.FromRawBits(value: Math.Max(
                val1: 0L,
                val2: (MagnitudeDifference(
                    left: position.Y.Value,
                    right: node.Center.Y.Value
                ) - half)
            )),
            Z: FixedQ4816.FromRawBits(value: Math.Max(
                val1: 0L,
                val2: (MagnitudeDifference(
                    left: position.Z.Value,
                    right: node.Center.Z.Value
                ) - half)
            ))
        );

        return offset.TryLengthSquared(squaredLength: out distanceSquared);
    }

    /// <inheritdoc/>
    public GravitySolveStatistics ComputeAccelerations(
        ReadOnlySpan<GravityBody> bodies,
        Span<FixedVector3> accelerations,
        GravityParameters parameters
    ) {
        if (!GravityOctree.TryBeginSolve(
            accelerations: accelerations,
            bodies: bodies,
            counters: new SolveCounters(owner: this),
            openingAngle: m_options.OpeningAngle,
            pairwise: m_pairwise,
            parameters: parameters,
            prepared: out var prepared,
            statistics: out var statistics
        )) {
            return statistics;
        }

        var sourceCount = CollectSources(bodies: bodies);

        if (sourceCount == 0) {
            return Statistics(bodyCount: bodies.Length);
        }

        BuildTree(
            bodies: bodies,
            sourceCount: sourceCount
        );

        for (var targetIndex = 0; (targetIndex < bodies.Length); targetIndex++) {
            var acceleration = FixedVector3.Zero;

            AccumulateNode(
                nodeIndex: 0,
                targetIndex: targetIndex,
                targetPosition: bodies[targetIndex].Position,
                bodies: bodies,
                parameters: prepared,
                acceleration: ref acceleration
            );
            accelerations[targetIndex] = acceleration;
        }

        return Statistics(bodyCount: bodies.Length);
    }

    private readonly record struct AggregateResult(FixedVector3 CenterOfMass, FixedQ4816 TotalMass);
    private readonly struct OctreeBuilder(FastMonopoleGravitySolver owner) : GravityOctree.IBuilder<MonopoleNode> {
        public void CompleteBranch(int nodeIndex, in MonopoleNode node, int firstChild, FixedVector3 center) {
        }
        public void CompleteLeaf(
            int nodeIndex,
            in MonopoleNode node,
            int start,
            int count,
            FixedVector3 center,
            ReadOnlySpan<GravityBody> bodies
        ) {
        }
        public MonopoleNode CreateNode(
            int start,
            int count,
            FixedVector3 center,
            FixedQ4816 halfSize,
            ReadOnlySpan<GravityBody> bodies
        ) {
            var aggregate = owner.Aggregate(
                bodies: bodies,
                count: count,
                start: start
            );

            return new MonopoleNode(
                Center: center,
                HalfSize: halfSize,
                CenterOfMass: aggregate.CenterOfMass,
                TotalMass: aggregate.TotalMass,
                Start: start,
                Count: count,
                FirstChild: -1
            );
        }
        public MonopoleNode WithFirstChild(in MonopoleNode node, int firstChild) =>
            node with { FirstChild = firstChild };
    }
    private readonly struct SolveCounters(FastMonopoleGravitySolver owner) : GravityOctree.ICounters {
        public void Reset() =>
            owner.ResetCounters();
        public GravitySolveStatistics Statistics(int bodyCount) =>
            owner.Statistics(bodyCount: bodyCount);
    }
    private readonly record struct MonopoleNode(
        FixedVector3 Center,
        FixedQ4816 HalfSize,
        FixedVector3 CenterOfMass,
        FixedQ4816 TotalMass,
        int Start,
        int Count,
        int FirstChild
    );
}
