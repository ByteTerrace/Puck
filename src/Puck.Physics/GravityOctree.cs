using System.Numerics;
using Puck.Maths;

namespace Puck.Physics;

/// <summary>The scaffolding shared by the hierarchical gravity solvers: the solve entry guard, child geometry,
/// stable octant partitioning, power-of-two root sizing, workspace growth, and the opening-angle comparison.</summary>
internal static class GravityOctree {
    internal interface IBuilder<TNode> {
        TNode CreateNode(
            int start,
            int count,
            FixedVector3 center,
            FixedQ4816 halfSize,
            ReadOnlySpan<GravityBody> bodies
        );
        void CompleteLeaf(
            int nodeIndex,
            in TNode node,
            int start,
            int count,
            FixedVector3 center,
            ReadOnlySpan<GravityBody> bodies
        );
        TNode WithFirstChild(in TNode node, int firstChild);
        void CompleteBranch(int nodeIndex, in TNode node, int firstChild, FixedVector3 center);
    }
    internal interface ICounters {
        void Reset();
        GravitySolveStatistics Statistics(int bodyCount);
    }

    public readonly record struct RootCube(FixedVector3 Center, FixedQ4816 HalfSize);

    public static void BuildNode<TNode, TBuilder>(
        ref TBuilder builder,
        ref TNode[] nodes,
        ref int nodeSlotCount,
        ref int liveNodeCount,
        int[] indices,
        int[] partitionScratch,
        int leafCapacity,
        int maximumDepth,
        int nodeIndex,
        int start,
        int count,
        FixedVector3 center,
        FixedQ4816 halfSize,
        int depth,
        ReadOnlySpan<GravityBody> bodies
    )
        where TBuilder : struct, IBuilder<TNode> {
        var node = builder.CreateNode(
            bodies: bodies,
            center: center,
            count: count,
            halfSize: halfSize,
            start: start
        );

        nodes[nodeIndex] = node;
        liveNodeCount++;

        if (
            (count <= leafCapacity) ||
            (depth >= maximumDepth) ||
            (halfSize.Value <= 1L)
        ) {
            builder.CompleteLeaf(
                bodies: bodies,
                center: center,
                count: count,
                node: in node,
                nodeIndex: nodeIndex,
                start: start
            );

            return;
        }

        Span<int> counts = stackalloc int[8];
        Span<int> offsets = stackalloc int[8];

        PartitionOctants(
            bodies: bodies,
            center: center,
            count: count,
            counts: counts,
            indices: indices,
            offsets: offsets,
            partitionScratch: partitionScratch,
            start: start
        );

        var firstChild = ReserveChildren(
            nodeSlotCount: ref nodeSlotCount,
            nodes: ref nodes
        );

        node = builder.WithFirstChild(
            firstChild: firstChild,
            node: in node
        );
        nodes[nodeIndex] = node;
        var childHalf = FixedQ4816.FromRawBits(value: (halfSize.Value >> 1));

        for (var octant = 0; (octant < counts.Length); octant++) {
            if (counts[octant] == 0) {
                continue;
            }

            BuildNode(
                builder: ref builder,
                nodes: ref nodes,
                nodeSlotCount: ref nodeSlotCount,
                liveNodeCount: ref liveNodeCount,
                indices: indices,
                partitionScratch: partitionScratch,
                leafCapacity: leafCapacity,
                maximumDepth: maximumDepth,
                nodeIndex: (firstChild + octant),
                start: (start + offsets[octant]),
                count: counts[octant],
                center: ChildCenter(
                    childHalf: childHalf,
                    octant: octant,
                    parent: center
                ),
                halfSize: childHalf,
                depth: (depth + 1),
                bodies: bodies
            );
        }

        builder.CompleteBranch(
            center: center,
            firstChild: firstChild,
            node: in node,
            nodeIndex: nodeIndex
        );
    }
    public static FixedVector3 ChildCenter(FixedVector3 parent, FixedQ4816 childHalf, int octant) =>
        new(
            X: (((octant & 1) == 0)
            ? checked((parent.X - childHalf))
            : checked((parent.X + childHalf))),
            Y: (((octant & 2) == 0)
            ? checked((parent.Y - childHalf))
            : checked((parent.Y + childHalf))),
            Z: (((octant & 4) == 0)
            ? checked((parent.Z - childHalf))
            : checked((parent.Z + childHalf)))
        );
    public static RootCube ComputeRootBounds(ReadOnlySpan<GravityBody> bodies, ReadOnlySpan<int> indices, int count) {
        var first = bodies[indices[0]].Position;
        var minX = first.X.Value;
        var minY = first.Y.Value;
        var minZ = first.Z.Value;
        var maxX = minX;
        var maxY = minY;
        var maxZ = minZ;

        for (var offset = 1; (offset < count); offset++) {
            var position = bodies[indices[offset]].Position;

            minX = Math.Min(
                val1: minX,
                val2: position.X.Value
            );
            minY = Math.Min(
                val1: minY,
                val2: position.Y.Value
            );
            minZ = Math.Min(
                val1: minZ,
                val2: position.Z.Value
            );
            maxX = Math.Max(
                val1: maxX,
                val2: position.X.Value
            );
            maxY = Math.Max(
                val1: maxY,
                val2: position.Y.Value
            );
            maxZ = Math.Max(
                val1: maxZ,
                val2: position.Z.Value
            );
        }

        var spanX = checked((((Int128)maxX) - minX));
        var spanY = checked((((Int128)maxY) - minY));
        var spanZ = checked((((Int128)maxZ) - minZ));
        var maximumSpan = Int128.Max(
            x: spanX,
            y: Int128.Max(
                x: spanY,
                y: spanZ
            )
        );

        if (maximumSpan > long.MaxValue) {
            throw new OverflowException(message: "The position span exceeds the octree's Q48.16 root range.");
        }

        var requiredHalf = Math.Max(
            val1: 1UL,
            val2: ((unchecked((ulong)maximumSpan) + 1UL) >> 1)
        );
        var halfRaw = BitOperations.RoundUpToPowerOf2(value: requiredHalf);

        if (
            (halfRaw == 0UL) ||
            (halfRaw > long.MaxValue)
        ) {
            throw new OverflowException(message: "The position span cannot be enclosed by a representable power-of-two octree root.");
        }

        return new RootCube(
            Center: new FixedVector3(
                X: FixedQ4816.FromRawBits(value: Midpoint(
                    maximum: maxX,
                    minimum: minX
                )),
                Y: FixedQ4816.FromRawBits(value: Midpoint(
                    maximum: maxY,
                    minimum: minY
                )),
                Z: FixedQ4816.FromRawBits(value: Midpoint(
                    maximum: maxZ,
                    minimum: minZ
                ))
            ),
            HalfSize: FixedQ4816.FromRawBits(value: unchecked((long)halfRaw))
        );
    }
    public static void EnsureIndexCapacity(ref int[] indices, ref int[] partitionScratch, int required) {
        if (indices.Length < required) {
            var capacity = GrowthCapacity(
                current: indices.Length,
                required: required
            );

            Array.Resize(
                array: ref indices,
                newSize: capacity
            );
            Array.Resize(
                array: ref partitionScratch,
                newSize: capacity
            );
        }
    }
    public static void EnsureNodeCapacity<TNode>(ref TNode[] nodes, int required) {
        if (nodes.Length < required) {
            Array.Resize(
                array: ref nodes,
                newSize: GrowthCapacity(
                    current: nodes.Length,
                    required: required
                )
            );
        }
    }
    public static int GrowthCapacity(int current, int required) {
        var capacity = Math.Max(
            val1: 16,
            val2: current
        );

        while (capacity < required) {
            capacity = checked((capacity * 2));
        }

        return capacity;
    }
    public static bool IsSideWithinOpeningAngle(UInt128 sideRaw, UnitInterval32 openingAngle, FixedQ4816 distanceSquared) {
        if (distanceSquared <= FixedQ4816.Zero) {
            return false;
        }

        // side² < theta²·distance². The side and distance are Q16; theta is UQ0.32. Restoring their common scale
        // shifts side² by 48. With theta <= 1, the right side fits UInt128; a side at least 2^40 raw makes the left
        // at least 2^128 and therefore cannot pass.
        if (sideRaw >= (UInt128.One << 40)) {
            return false;
        }

        var thetaRaw = openingAngle.Value;
        var left = ((sideRaw * sideRaw) << (UnitInterval32.FractionBitCount + FixedQ4816.FractionBitCount));
        var right = ((((UInt128)thetaRaw) * thetaRaw) * unchecked((ulong)distanceSquared.Value));

        return (left < right);
    }
    public static long Midpoint(long minimum, long maximum) =>
        checked((long)(((Int128)minimum) + ((((Int128)maximum) - minimum) / 2)));
    public static int Octant(FixedVector3 position, FixedVector3 center) =>
        ((position.X >= center.X)
            ? 1
            : 0) |
        ((position.Y >= center.Y)
            ? 2
            : 0) |
        ((position.Z >= center.Z)
            ? 4
            : 0
        );
    /// <summary>Partitions <paramref name="indices"/>[start..start+count] into stable octant runs around
    /// <paramref name="center"/>, filling <paramref name="counts"/> and <paramref name="offsets"/> (both length 8).</summary>
    public static void PartitionOctants(
        ReadOnlySpan<GravityBody> bodies,
        int[] indices,
        int[] partitionScratch,
        int start,
        int count,
        FixedVector3 center,
        Span<int> counts,
        Span<int> offsets
    ) {
        Span<int> cursors = stackalloc int[8];

        for (var offset = 0; (offset < count); offset++) {
            var bodyIndex = indices[(start + offset)];

            counts[Octant(
                position: bodies[bodyIndex].Position,
                center: center
            )]++;
        }

        var running = 0;

        for (var octant = 0; (octant < counts.Length); octant++) {
            offsets[octant] = running;
            cursors[octant] = running;
            running += counts[octant];
        }

        for (var offset = 0; (offset < count); offset++) {
            var bodyIndex = indices[(start + offset)];
            var octant = Octant(
                position: bodies[bodyIndex].Position,
                center: center
            );

            partitionScratch[(start + cursors[octant]++)] = bodyIndex;
        }

        partitionScratch.AsSpan(
            length: count,
            start: start
        ).CopyTo(destination: indices.AsSpan(
            length: count,
            start: start
        ));
    }
    public static int ReserveChildren<TNode>(ref TNode[] nodes, ref int nodeSlotCount) {
        var firstChild = nodeSlotCount;
        var required = checked((firstChild + 8));

        EnsureNodeCapacity(
            nodes: ref nodes,
            required: required
        );
        Array.Clear(
            array: nodes,
            index: firstChild,
            length: 8
        );
        nodeSlotCount = required;

        return firstChild;
    }
    /// <summary>Runs the entry guard the hierarchical solvers share: a zero opening angle accepts no cell, so the
    /// whole solve is delegated to <paramref name="pairwise"/>, which is what makes a zero angle bit-identical to
    /// the exact oracle. Validation precedes the counter reset, so a refused call leaves the previous solve's
    /// counters intact.</summary>
    /// <returns><see langword="true"/> when the caller should build its tree and <paramref name="prepared"/> holds
    /// the validated parameters; <see langword="false"/> when the solve is already complete and
    /// <paramref name="statistics"/> holds its result.</returns>
    public static bool TryBeginSolve<TCounters>(
        TCounters counters,
        UnitInterval32 openingAngle,
        PairwiseGravitySolver pairwise,
        ReadOnlySpan<GravityBody> bodies,
        Span<FixedVector3> accelerations,
        GravityParameters parameters,
        out PreparedGravityParameters prepared,
        out GravitySolveStatistics statistics
    )
        where TCounters : struct, ICounters {
        if (openingAngle == UnitInterval32.Zero) {
            prepared = default;
            statistics = pairwise.ComputeAccelerations(
                accelerations: accelerations,
                bodies: bodies,
                parameters: parameters
            );

            return false;
        }

        prepared = GravityKernel.Validate(
            accelerations: accelerations,
            bodies: bodies,
            parameters: parameters
        );

        counters.Reset();

        if (
            (bodies.Length == 0) ||
            (prepared.GravitationalConstant <= FixedQ4816.Zero)
        ) {
            statistics = counters.Statistics(bodyCount: bodies.Length);

            return false;
        }

        statistics = default;

        return true;
    }
}
