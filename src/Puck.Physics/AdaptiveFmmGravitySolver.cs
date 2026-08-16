using Puck.Maths;

namespace Puck.Physics;

/// <summary>
/// A deterministic adaptive fast multipole solver with source monopoles and first-order Cartesian local expansions.
/// </summary>
/// <remarks>
/// The solver builds an occupancy-adaptive octree, forms mass monopoles in an upward pass, performs mutual
/// multipole-to-local translations through a dual-tree interaction walk, and propagates acceleration-plus-gradient
/// local expansions downward before evaluating bodies. Near leaf pairs remain exact, and a translation whose tidal
/// gradient would leave the Q32.32 grid opens the cell pair instead of failing the solve. When two valid local
/// gradients cannot be combined during the downward pass, the ancestor expansion is deferred to leaf evaluation.
/// Every body participates in
/// tree construction, massless targets included: adding or moving a zero-mass probe changes which cell pairs are
/// accepted and therefore perturbs other bodies' results at approximation order, where
/// <see cref="FastMonopoleGravitySolver"/> builds from positive-mass sources only and is unaffected by probes.
/// Expected work is <c>O(N)</c> for well-distributed populations under a fixed opening rule, with an <c>O(N²)</c>
/// worst case for inseparable or depth-limited inputs. The instance reuses its arrays and is not safe for concurrent
/// calls.
/// </remarks>
public sealed class AdaptiveFmmGravitySolver : IGravitySolver {
    private readonly DeferredLocalExpansion[] m_deferredLocalExpansions;
    private readonly AdaptiveFmmOptions m_options;

    private long m_approximatedSourceCount;
    private long m_deferredLocalExpansionEvaluations;
    private long m_exactSourceEvaluations;
    private int m_liveNodeCount;
    private long m_localExpansionEvaluations;
    private long m_localToLocalTranslations;
    private long m_multipoleToLocalTranslations;
    private long m_multipoleToMultipoleTranslations;
    private int m_nodeSlotCount;

    private readonly PairwiseGravitySolver m_pairwise = new();
    private int[] m_bodyIndices = [];
    private int[] m_partitionScratch = [];
    private FmmNode[] m_nodes = [];

    /// <summary>Creates a solver with the supplied options, or <see cref="AdaptiveFmmOptions"/>' defaults.</summary>
    /// <param name="options">The adaptive tree and cell-pair opening controls.</param>
    public AdaptiveFmmGravitySolver(AdaptiveFmmOptions? options = null) {
        m_options = (options ?? new AdaptiveFmmOptions());
        m_deferredLocalExpansions = new DeferredLocalExpansion[m_options.MaximumDepth];
    }

    /// <summary>Gets this solver's immutable option set.</summary>
    public AdaptiveFmmOptions Options => m_options;

    private AggregateResult AggregateBodies(int start, int count, FixedVector3 fallbackCenter, ReadOnlySpan<GravityBody> bodies) {
        var firstPosition = bodies[m_bodyIndices[start]].Position;
        var minimumX = firstPosition.X.Value;
        var minimumY = firstPosition.Y.Value;
        var minimumZ = firstPosition.Z.Value;
        var maximumX = minimumX;
        var maximumY = minimumY;
        var maximumZ = minimumZ;
        var totalMassRaw = 0L;
        var sourceCount = 0;
        var weightedX = Int128.Zero;
        var weightedY = Int128.Zero;
        var weightedZ = Int128.Zero;

        for (var offset = 0; (offset < count); offset++) {
            ref readonly var body = ref bodies[m_bodyIndices[(start + offset)]];

            minimumX = Math.Min(
                val1: minimumX,
                val2: body.Position.X.Value
            );
            minimumY = Math.Min(
                val1: minimumY,
                val2: body.Position.Y.Value
            );
            minimumZ = Math.Min(
                val1: minimumZ,
                val2: body.Position.Z.Value
            );
            maximumX = Math.Max(
                val1: maximumX,
                val2: body.Position.X.Value
            );
            maximumY = Math.Max(
                val1: maximumY,
                val2: body.Position.Y.Value
            );
            maximumZ = Math.Max(
                val1: maximumZ,
                val2: body.Position.Z.Value
            );

            if (body.Mass <= FixedQ4816.Zero) {
                continue;
            }

            sourceCount++;
            totalMassRaw = checked((totalMassRaw + body.Mass.Value));
            weightedX = checked((weightedX + (((Int128)body.Position.X.Value) * body.Mass.Value)));
            weightedY = checked((weightedY + (((Int128)body.Position.Y.Value) * body.Mass.Value)));
            weightedZ = checked((weightedZ + (((Int128)body.Position.Z.Value) * body.Mass.Value)));
        }

        if (totalMassRaw == 0L) {
            return CreateAggregate(
                fallbackCenterOfMass: fallbackCenter,
                totalMassRaw: 0L,
                sourceCount: 0,
                FirstMomentX: Int128.Zero,
                FirstMomentY: Int128.Zero,
                FirstMomentZ: Int128.Zero,
                minimumX: minimumX,
                minimumY: minimumY,
                minimumZ: minimumZ,
                maximumX: maximumX,
                maximumY: maximumY,
                maximumZ: maximumZ
            );
        }

        return CreateAggregate(
            FirstMomentX: weightedX,
            FirstMomentY: weightedY,
            FirstMomentZ: weightedZ,
            fallbackCenterOfMass: fallbackCenter,
            maximumX: maximumX,
            maximumY: maximumY,
            maximumZ: maximumZ,
            minimumX: minimumX,
            minimumY: minimumY,
            minimumZ: minimumZ,
            sourceCount: sourceCount,
            totalMassRaw: totalMassRaw
        );
    }
    private AggregateResult AggregateChildren(int firstChild, FixedVector3 fallbackCenter) {
        var totalMassRaw = 0L;
        var sourceCount = 0;
        var firstMomentX = Int128.Zero;
        var firstMomentY = Int128.Zero;
        var firstMomentZ = Int128.Zero;
        var hasChild = false;
        var minimumX = 0L;
        var minimumY = 0L;
        var minimumZ = 0L;
        var maximumX = 0L;
        var maximumY = 0L;
        var maximumZ = 0L;

        for (var octant = 0; (octant < 8); octant++) {
            ref readonly var child = ref m_nodes[(firstChild + octant)];

            if (child.Count == 0) {
                continue;
            }

            if (!hasChild) {
                minimumX = child.MinimumX;
                minimumY = child.MinimumY;
                minimumZ = child.MinimumZ;
                maximumX = child.MaximumX;
                maximumY = child.MaximumY;
                maximumZ = child.MaximumZ;
                hasChild = true;
            } else {
                minimumX = Math.Min(
                    val1: minimumX,
                    val2: child.MinimumX
                );
                minimumY = Math.Min(
                    val1: minimumY,
                    val2: child.MinimumY
                );
                minimumZ = Math.Min(
                    val1: minimumZ,
                    val2: child.MinimumZ
                );
                maximumX = Math.Max(
                    val1: maximumX,
                    val2: child.MaximumX
                );
                maximumY = Math.Max(
                    val1: maximumY,
                    val2: child.MaximumY
                );
                maximumZ = Math.Max(
                    val1: maximumZ,
                    val2: child.MaximumZ
                );
            }

            totalMassRaw = checked((totalMassRaw + child.TotalMass.Value));
            sourceCount = checked((sourceCount + child.SourceCount));
            firstMomentX = checked((firstMomentX + child.FirstMomentX));
            firstMomentY = checked((firstMomentY + child.FirstMomentY));
            firstMomentZ = checked((firstMomentZ + child.FirstMomentZ));
            m_multipoleToMultipoleTranslations++;
        }

        if (totalMassRaw == 0L) {
            return CreateAggregate(
                fallbackCenterOfMass: fallbackCenter,
                totalMassRaw: 0L,
                sourceCount: 0,
                FirstMomentX: Int128.Zero,
                FirstMomentY: Int128.Zero,
                FirstMomentZ: Int128.Zero,
                minimumX: minimumX,
                minimumY: minimumY,
                minimumZ: minimumZ,
                maximumX: maximumX,
                maximumY: maximumY,
                maximumZ: maximumZ
            );
        }

        return CreateAggregate(
            FirstMomentX: firstMomentX,
            FirstMomentY: firstMomentY,
            FirstMomentZ: firstMomentZ,
            fallbackCenterOfMass: fallbackCenter,
            maximumX: maximumX,
            maximumY: maximumY,
            maximumZ: maximumZ,
            minimumX: minimumX,
            minimumY: minimumY,
            minimumZ: minimumZ,
            sourceCount: sourceCount,
            totalMassRaw: totalMassRaw
        );
    }
    private void BuildTree(ReadOnlySpan<GravityBody> bodies) {
        GravityOctree.EnsureIndexCapacity(
            indices: ref m_bodyIndices,
            partitionScratch: ref m_partitionScratch,
            required: bodies.Length
        );
        for (var index = 0; (index < bodies.Length); index++) {
            m_bodyIndices[index] = index;
        }

        var root = GravityOctree.ComputeRootBounds(
            bodies: bodies,
            indices: m_bodyIndices,
            count: bodies.Length
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
            indices: m_bodyIndices,
            partitionScratch: m_partitionScratch,
            leafCapacity: m_options.LeafCapacity,
            maximumDepth: m_options.MaximumDepth,
            nodeIndex: 0,
            start: 0,
            count: bodies.Length,
            center: root.Center,
            halfSize: root.HalfSize,
            depth: 0,
            bodies: bodies
        );
    }
    private static FixedVector3 CheckedDifference(FixedVector3 left, FixedVector3 right) =>
        new(
            X: checked((left.X - right.X)),
            Y: checked((left.Y - right.Y)),
            Z: checked((left.Z - right.Z))
        );
    private void CommitTranslation(int targetNodeIndex, FixedVector3 acceleration, GravityGradient combinedGradient, int representedSourceCount) {
        ref var node = ref m_nodes[targetNodeIndex];

        node.LocalAcceleration = GravityKernel.AddChecked(
            left: node.LocalAcceleration,
            right: acceleration
        );
        node.LocalGradient = combinedGradient;
        m_multipoleToLocalTranslations++;
        m_approximatedSourceCount += representedSourceCount;
    }
    private static AggregateResult CreateAggregate(
        FixedVector3 fallbackCenterOfMass,
        long totalMassRaw,
        int sourceCount,
        Int128 FirstMomentX,
        Int128 FirstMomentY,
        Int128 FirstMomentZ,
        long minimumX,
        long minimumY,
        long minimumZ,
        long maximumX,
        long maximumY,
        long maximumZ
    ) {
        var spanX = checked((UInt128)(((Int128)maximumX) - minimumX));
        var spanY = checked((UInt128)(((Int128)maximumY) - minimumY));
        var spanZ = checked((UInt128)(((Int128)maximumZ) - minimumZ));
        var maximumSpan = UInt128.Max(
            x: spanX,
            y: UInt128.Max(
                x: spanY,
                y: spanZ
            )
        );

        if (maximumSpan > long.MaxValue) {
            throw new OverflowException(message: "An occupied adaptive FMM cell exceeds Q48.16 range.");
        }

        var targetCenter = new FixedVector3(
            X: FixedQ4816.FromRawBits(value: GravityOctree.Midpoint(
                maximum: maximumX,
                minimum: minimumX
            )),
            Y: FixedQ4816.FromRawBits(value: GravityOctree.Midpoint(
                maximum: maximumY,
                minimum: minimumY
            )),
            Z: FixedQ4816.FromRawBits(value: GravityOctree.Midpoint(
                maximum: maximumZ,
                minimum: minimumZ
            ))
        );
        var centerOfMass = ((totalMassRaw == 0L)
            ? fallbackCenterOfMass
            : new FixedVector3(
                X: FixedQ4816.FromRawBits(value: GravityKernel.RoundDivide(
                    numerator: FirstMomentX,
                    positiveDenominator: unchecked((ulong)totalMassRaw)
                )),
                Y: FixedQ4816.FromRawBits(value: GravityKernel.RoundDivide(
                    numerator: FirstMomentY,
                    positiveDenominator: unchecked((ulong)totalMassRaw)
                )),
                Z: FixedQ4816.FromRawBits(value: GravityKernel.RoundDivide(
                    numerator: FirstMomentZ,
                    positiveDenominator: unchecked((ulong)totalMassRaw)
                ))
            )
        );

        return new AggregateResult(
            ExpansionCenter: targetCenter,
            OccupiedHalfSize: FixedQ4816.FromRawBits(value: unchecked((long)((maximumSpan + UInt128.One) >> 1))),
            CenterOfMass: centerOfMass,
            TotalMass: FixedQ4816.FromRawBits(value: totalMassRaw),
            SourceCount: sourceCount,
            FirstMomentX: FirstMomentX,
            FirstMomentY: FirstMomentY,
            FirstMomentZ: FirstMomentZ,
            MinimumX: minimumX,
            MinimumY: minimumY,
            MinimumZ: minimumZ,
            MaximumX: maximumX,
            MaximumY: maximumY,
            MaximumZ: maximumZ
        );
    }
    private void DirectBetween(
        in FmmNode first,
        in FmmNode second,
        ReadOnlySpan<GravityBody> bodies,
        Span<FixedVector3> accelerations,
        PreparedGravityParameters parameters
    ) {
        for (var firstOffset = 0; (firstOffset < first.Count); firstOffset++) {
            var firstIndex = m_bodyIndices[(first.Start + firstOffset)];

            for (var secondOffset = 0; (secondOffset < second.Count); secondOffset++) {
                GravityKernel.AccumulatePair(
                    bodies: bodies,
                    firstIndex: firstIndex,
                    secondIndex: m_bodyIndices[(second.Start + secondOffset)],
                    accelerations: accelerations,
                    parameters: in parameters,
                    exactSourceEvaluations: ref m_exactSourceEvaluations
                );
            }
        }
    }
    private void DirectWithin(
        in FmmNode node,
        ReadOnlySpan<GravityBody> bodies,
        Span<FixedVector3> accelerations,
        PreparedGravityParameters parameters
    ) {
        for (var firstOffset = 0; (firstOffset < node.Count); firstOffset++) {
            var firstIndex = m_bodyIndices[(node.Start + firstOffset)];

            for (var secondOffset = (firstOffset + 1); (secondOffset < node.Count); secondOffset++) {
                GravityKernel.AccumulatePair(
                    bodies: bodies,
                    firstIndex: firstIndex,
                    secondIndex: m_bodyIndices[(node.Start + secondOffset)],
                    accelerations: accelerations,
                    parameters: in parameters,
                    exactSourceEvaluations: ref m_exactSourceEvaluations
                );
            }
        }
    }
    private void InteractPair(
        int firstNodeIndex,
        int secondNodeIndex,
        ReadOnlySpan<GravityBody> bodies,
        Span<FixedVector3> accelerations,
        PreparedGravityParameters parameters
    ) {
        ref readonly var first = ref m_nodes[firstNodeIndex];
        ref readonly var second = ref m_nodes[secondNodeIndex];

        if (
            IsWellSeparated(
            first: in first,
            second: in second,
            openingAngle: m_options.OpeningAngle
        ) &&
            TryTranslateMutual(
            first: in first,
            firstNodeIndex: firstNodeIndex,
            parameters: parameters,
            second: in second,
            secondNodeIndex: secondNodeIndex
        )
        ) {
            return;
        }

        var firstIsLeaf = (first.FirstChild < 0);
        var secondIsLeaf = (second.FirstChild < 0);

        if (
            firstIsLeaf &&
            secondIsLeaf
        ) {
            DirectBetween(
                accelerations: accelerations,
                bodies: bodies,
                first: in first,
                parameters: parameters,
                second: in second
            );

            return;
        }

        if (
            secondIsLeaf ||
            (!firstIsLeaf && (first.HalfSize >= second.HalfSize))
        ) {
            for (var octant = 0; (octant < 8); octant++) {
                var child = (first.FirstChild + octant);

                if (m_nodes[child].Count > 0) {
                    InteractPair(
                        accelerations: accelerations,
                        bodies: bodies,
                        firstNodeIndex: child,
                        parameters: parameters,
                        secondNodeIndex: secondNodeIndex
                    );
                }
            }
        } else {
            for (var octant = 0; (octant < 8); octant++) {
                var child = (second.FirstChild + octant);

                if (m_nodes[child].Count > 0) {
                    InteractPair(
                        accelerations: accelerations,
                        bodies: bodies,
                        firstNodeIndex: firstNodeIndex,
                        parameters: parameters,
                        secondNodeIndex: child
                    );
                }
            }
        }
    }
    private void InteractSelf(
        int nodeIndex,
        ReadOnlySpan<GravityBody> bodies,
        Span<FixedVector3> accelerations,
        PreparedGravityParameters parameters
    ) {
        ref readonly var node = ref m_nodes[nodeIndex];

        if (node.FirstChild < 0) {
            DirectWithin(
                accelerations: accelerations,
                bodies: bodies,
                node: in node,
                parameters: parameters
            );

            return;
        }

        for (var firstOctant = 0; (firstOctant < 8); firstOctant++) {
            var firstChild = (node.FirstChild + firstOctant);

            if (m_nodes[firstChild].Count == 0) {
                continue;
            }

            InteractSelf(
                accelerations: accelerations,
                bodies: bodies,
                nodeIndex: firstChild,
                parameters: parameters
            );

            for (var secondOctant = (firstOctant + 1); (secondOctant < 8); secondOctant++) {
                var secondChild = (node.FirstChild + secondOctant);

                if (m_nodes[secondChild].Count == 0) {
                    continue;
                }

                InteractPair(
                    accelerations: accelerations,
                    bodies: bodies,
                    firstNodeIndex: firstChild,
                    parameters: parameters,
                    secondNodeIndex: secondChild
                );
            }
        }
    }
    private static bool IsWellSeparated(in FmmNode first, in FmmNode second, UnitInterval32 openingAngle) {
        var displacement = GravityKernel.PrepareDisplacement(
            target: first.ExpansionCenter,
            source: second.ExpansionCenter
        );
        var combinedHalf = checked((((UInt128)((ulong)first.OccupiedHalfSize.Value)) + unchecked((ulong)second.OccupiedHalfSize.Value)));

        return GravityOctree.IsSideWithinOpeningAngle(
            sideRaw: (combinedHalf << 1),
            openingAngle: openingAngle,
            distanceSquared: displacement.DistanceSquared
        );
    }
    private void PropagateAndEvaluate(
        int nodeIndex,
        ReadOnlySpan<GravityBody> bodies,
        Span<FixedVector3> accelerations,
        int deferredExpansionCount
    ) {
        ref readonly var node = ref m_nodes[nodeIndex];

        if (node.FirstChild < 0) {
            for (var offset = 0; (offset < node.Count); offset++) {
                var bodyIndex = m_bodyIndices[(node.Start + offset)];
                var bodyOffset = CheckedDifference(
                    left: bodies[bodyIndex].Position,
                    right: node.ExpansionCenter
                );
                var local = GravityKernel.AddChecked(
                    left: node.LocalAcceleration,
                    right: node.LocalGradient.Apply(offset: bodyOffset)
                );

                for (var deferredIndex = 0; (deferredIndex < deferredExpansionCount); deferredIndex++) {
                    ref readonly var deferred = ref m_deferredLocalExpansions[deferredIndex];
                    var deferredOffset = CheckedDifference(
                        left: bodies[bodyIndex].Position,
                        right: deferred.Center
                    );
                    var deferredAcceleration = GravityKernel.AddChecked(
                        left: deferred.Acceleration,
                        right: deferred.Gradient.Apply(offset: deferredOffset)
                    );

                    local = GravityKernel.AddChecked(
                        left: local,
                        right: deferredAcceleration
                    );
                    m_deferredLocalExpansionEvaluations++;
                }

                accelerations[bodyIndex] = GravityKernel.AddChecked(
                    left: accelerations[bodyIndex],
                    right: local
                );
                m_localExpansionEvaluations++;
            }

            return;
        }

        for (var octant = 0; (octant < 8); octant++) {
            var childIndex = (node.FirstChild + octant);

            if (m_nodes[childIndex].Count == 0) {
                continue;
            }

            {
                ref var child = ref m_nodes[childIndex];
                var childDeferredExpansionCount = deferredExpansionCount;

                if (GravityGradient.TryAdd(
                    left: child.LocalGradient,
                    right: node.LocalGradient,
                    sum: out var combinedGradient
                )) {
                    var childOffset = CheckedDifference(
                        left: child.ExpansionCenter,
                        right: node.ExpansionCenter
                    );
                    var shiftedAcceleration = GravityKernel.AddChecked(
                        left: node.LocalAcceleration,
                        right: node.LocalGradient.Apply(offset: childOffset)
                    );

                    child.LocalAcceleration = GravityKernel.AddChecked(
                        left: child.LocalAcceleration,
                        right: shiftedAcceleration
                    );
                    child.LocalGradient = combinedGradient;
                } else {
                    m_deferredLocalExpansions[childDeferredExpansionCount] = new DeferredLocalExpansion(
                        Center: node.ExpansionCenter,
                        Acceleration: node.LocalAcceleration,
                        Gradient: node.LocalGradient
                    );
                    childDeferredExpansionCount++;
                }

                m_localToLocalTranslations++;
                PropagateAndEvaluate(
                    accelerations: accelerations,
                    bodies: bodies,
                    deferredExpansionCount: childDeferredExpansionCount,
                    nodeIndex: childIndex
                );
            }
        }
    }
    private void ResetCounters() {
        m_nodeSlotCount = 0;
        m_liveNodeCount = 0;
        m_exactSourceEvaluations = 0L;
        m_multipoleToMultipoleTranslations = 0L;
        m_multipoleToLocalTranslations = 0L;
        m_approximatedSourceCount = 0L;
        m_localToLocalTranslations = 0L;
        m_localExpansionEvaluations = 0L;
        m_deferredLocalExpansionEvaluations = 0L;
    }
    private GravitySolveStatistics Statistics(int bodyCount) =>
        new(
            ApproximatedNodeEvaluations: 0L,
            ApproximatedSourceCount: m_approximatedSourceCount,
            BodyCount: bodyCount,
            DeferredLocalExpansionEvaluations: m_deferredLocalExpansionEvaluations,
            ExactSourceEvaluations: m_exactSourceEvaluations,
            LocalExpansionEvaluations: m_localExpansionEvaluations,
            LocalToLocalTranslations: m_localToLocalTranslations,
            MultipoleToLocalTranslations: m_multipoleToLocalTranslations,
            MultipoleToMultipoleTranslations: m_multipoleToMultipoleTranslations,
            TreeNodeCount: m_liveNodeCount
        );
    private bool TryTranslateMutual(
        int firstNodeIndex,
        int secondNodeIndex,
        in FmmNode first,
        in FmmNode second,
        PreparedGravityParameters parameters
    ) {
        var translateFirst = (second.TotalMass > FixedQ4816.Zero);
        var translateSecond = (first.TotalMass > FixedQ4816.Zero);
        var firstAcceleration = FixedVector3.Zero;
        var secondAcceleration = FixedVector3.Zero;
        var firstCombinedGradient = first.LocalGradient;
        var secondCombinedGradient = second.LocalGradient;

        if (translateFirst) {
            var interaction = GravityKernel.PrepareInteraction(
                target: first.ExpansionCenter,
                source: second.CenterOfMass,
                softeningSquared: parameters.SofteningSquared
            );

            firstAcceleration = GravityKernel.Acceleration(
                interaction: in interaction,
                sourceMass: second.TotalMass,
                gravitationalConstant: parameters.GravitationalConstant
            );

            if (
                !GravityGradient.TryFromInteraction(
                interaction: in interaction,
                sourceMass: second.TotalMass,
                gravitationalConstant: parameters.GravitationalConstant,
                gradient: out var firstGradient
            ) ||
                !GravityGradient.TryAdd(
                left: first.LocalGradient,
                right: firstGradient,
                sum: out firstCombinedGradient
            )
            ) {
                return false;
            }
        }

        if (translateSecond) {
            var interaction = GravityKernel.PrepareInteraction(
                target: second.ExpansionCenter,
                source: first.CenterOfMass,
                softeningSquared: parameters.SofteningSquared
            );

            secondAcceleration = GravityKernel.Acceleration(
                interaction: in interaction,
                sourceMass: first.TotalMass,
                gravitationalConstant: parameters.GravitationalConstant
            );

            if (
                !GravityGradient.TryFromInteraction(
                interaction: in interaction,
                sourceMass: first.TotalMass,
                gravitationalConstant: parameters.GravitationalConstant,
                gradient: out var secondGradient
            ) ||
                !GravityGradient.TryAdd(
                left: second.LocalGradient,
                right: secondGradient,
                sum: out secondCombinedGradient
            )
            ) {
                return false;
            }
        }

        if (translateFirst) {
            CommitTranslation(
                targetNodeIndex: firstNodeIndex,
                acceleration: firstAcceleration,
                combinedGradient: firstCombinedGradient,
                representedSourceCount: second.SourceCount
            );
        }

        if (translateSecond) {
            CommitTranslation(
                targetNodeIndex: secondNodeIndex,
                acceleration: secondAcceleration,
                combinedGradient: secondCombinedGradient,
                representedSourceCount: first.SourceCount
            );
        }

        return true;
    }
    private static FmmNode WithAggregate(in FmmNode node, AggregateResult aggregate) =>
        node with {
            ExpansionCenter = aggregate.ExpansionCenter,
            OccupiedHalfSize = aggregate.OccupiedHalfSize,
            CenterOfMass = aggregate.CenterOfMass,
            TotalMass = aggregate.TotalMass,
            SourceCount = aggregate.SourceCount,
            FirstMomentX = aggregate.FirstMomentX,
            FirstMomentY = aggregate.FirstMomentY,
            FirstMomentZ = aggregate.FirstMomentZ,
            MinimumX = aggregate.MinimumX,
            MinimumY = aggregate.MinimumY,
            MinimumZ = aggregate.MinimumZ,
            MaximumX = aggregate.MaximumX,
            MaximumY = aggregate.MaximumY,
            MaximumZ = aggregate.MaximumZ,
        };

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

        BuildTree(bodies: bodies);
        InteractSelf(
            accelerations: accelerations,
            bodies: bodies,
            nodeIndex: 0,
            parameters: prepared
        );
        PropagateAndEvaluate(
            accelerations: accelerations,
            bodies: bodies,
            deferredExpansionCount: 0,
            nodeIndex: 0
        );

        return Statistics(bodyCount: bodies.Length);
    }

    private readonly record struct AggregateResult(
        FixedVector3 ExpansionCenter,
        FixedQ4816 OccupiedHalfSize,
        FixedVector3 CenterOfMass,
        FixedQ4816 TotalMass,
        int SourceCount,
        Int128 FirstMomentX,
        Int128 FirstMomentY,
        Int128 FirstMomentZ,
        long MinimumX,
        long MinimumY,
        long MinimumZ,
        long MaximumX,
        long MaximumY,
        long MaximumZ
    );
    private readonly record struct DeferredLocalExpansion(
        FixedVector3 Center,
        FixedVector3 Acceleration,
        GravityGradient Gradient
    );
    private readonly struct OctreeBuilder(AdaptiveFmmGravitySolver owner) : GravityOctree.IBuilder<FmmNode> {
        public void CompleteBranch(int nodeIndex, in FmmNode node, int firstChild, FixedVector3 center) {
            owner.m_nodes[nodeIndex] = WithAggregate(
                node: in owner.m_nodes[nodeIndex],
                aggregate: owner.AggregateChildren(
                    fallbackCenter: center,
                    firstChild: firstChild
                )
            );
        }
        public void CompleteLeaf(
            int nodeIndex,
            in FmmNode node,
            int start,
            int count,
            FixedVector3 center,
            ReadOnlySpan<GravityBody> bodies
        ) {
            owner.m_nodes[nodeIndex] = WithAggregate(
                node: in node,
                aggregate: owner.AggregateBodies(
                    bodies: bodies,
                    count: count,
                    fallbackCenter: center,
                    start: start
                )
            );
        }
        public FmmNode CreateNode(
            int start,
            int count,
            FixedVector3 center,
            FixedQ4816 halfSize,
            ReadOnlySpan<GravityBody> bodies
        ) =>
            new(
                Center: center,
                HalfSize: halfSize,
                ExpansionCenter: center,
                OccupiedHalfSize: halfSize,
                CenterOfMass: center,
                TotalMass: FixedQ4816.Zero,
                FirstMomentX: Int128.Zero,
                FirstMomentY: Int128.Zero,
                FirstMomentZ: Int128.Zero,
                Start: start,
                Count: count,
                SourceCount: 0,
                MinimumX: center.X.Value,
                MinimumY: center.Y.Value,
                MinimumZ: center.Z.Value,
                MaximumX: center.X.Value,
                MaximumY: center.Y.Value,
                MaximumZ: center.Z.Value,
                FirstChild: -1,
                LocalAcceleration: FixedVector3.Zero,
                LocalGradient: default
            );
        public FmmNode WithFirstChild(in FmmNode node, int firstChild) =>
            node with { FirstChild = firstChild };
    }
    private readonly struct SolveCounters(AdaptiveFmmGravitySolver owner) : GravityOctree.ICounters {
        public void Reset() =>
            owner.ResetCounters();
        public GravitySolveStatistics Statistics(int bodyCount) =>
            owner.Statistics(bodyCount: bodyCount);
    }
    private record struct FmmNode(
        FixedVector3 Center,
        FixedQ4816 HalfSize,
        FixedVector3 ExpansionCenter,
        FixedQ4816 OccupiedHalfSize,
        FixedVector3 CenterOfMass,
        FixedQ4816 TotalMass,
        Int128 FirstMomentX,
        Int128 FirstMomentY,
        Int128 FirstMomentZ,
        int Start,
        int Count,
        int SourceCount,
        long MinimumX,
        long MinimumY,
        long MinimumZ,
        long MaximumX,
        long MaximumY,
        long MaximumZ,
        int FirstChild,
        FixedVector3 LocalAcceleration,
        GravityGradient LocalGradient
    );
}
