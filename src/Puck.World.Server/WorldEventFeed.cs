using Puck.Maths;
using Puck.Physics;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>The closed world-events vocabulary; machine-memory watches are
/// addon-scoped — each mounted guest declares its own watch rows, so it is computed inside
/// <see cref="IWorldAddonHost"/>'s implementation directly rather than here; see <c>Addons.WorldAddonRuntime</c>'s
/// remarks. Mirrors <c>Puck.Scripting.AddonAbi.ObservationVerbs</c>'s event verbs one-for-one.</summary>
public enum WorldEventFamily : byte {
    /// <summary>A body entered a named region.</summary>
    RegionEnter,
    /// <summary>A body left a named region.</summary>
    RegionExit,
    /// <summary>A seat became human-occupied.</summary>
    SeatJoin,
    /// <summary>A seat stopped being human-occupied.</summary>
    SeatLeave,
    /// <summary>Two bodies began overlapping (a proximity edge — see <see cref="WorldEventFeed"/>'s own remarks;
    /// this is not the physical contact resolver).</summary>
    CollisionBegin,
    /// <summary>Two bodies stopped overlapping.</summary>
    CollisionEnd,
    /// <summary>A route (possession/mirror/machine engagement) was established.</summary>
    RouteEngaged,
    /// <summary>A route was dissolved.</summary>
    RouteDisengaged,
    /// <summary>An authored <c>adjacencies</c> row received a delivered neighbour refresh after having been dropped
    /// — the federation seam is live again.</summary>
    LinkEstablished,
    /// <summary>An authored <c>adjacencies</c> row went <see cref="WorldAdjacency.LivenessGraceSeconds"/> without a
    /// delivered neighbour refresh — the federation seam is dark.</summary>
    LinkDropped,
}
/// <summary>One world-scoped event edge for this tick, in pinned iteration order. <see cref="GateA"/> (and,
/// for the two-body families, <see cref="GateB"/>) name the <see cref="GrantSubject"/>(s) an addon must hold an
/// event-budgeted <see cref="WorldCapability.Observe"/> grant over to receive it — either gate suffices when both
/// are present (the collision/route families' own "subject-filtered by the grant" rule).</summary>
/// <param name="Family">The event family.</param>
/// <param name="GateA">The first gating subject.</param>
/// <param name="GateB">The second gating subject, for two-body families; <see langword="null"/> for a one-subject
/// family (region, seat).</param>
/// <param name="A">The wire payload's first lane (a body/seat index, or an encoded route target).</param>
/// <param name="B">The wire payload's second lane (a region ordinal, or an encoded route target); zero when unused.</param>
public readonly record struct WorldEventEdge(WorldEventFamily Family, GrantSubject GateA, GrantSubject? GateB, long A, long B);
/// <summary>
/// Computes the world-scoped event families once per tick — collision pairs, region enter/exit, seat
/// join/leave, route/engagement transitions, and federation link established/dropped — as a flat, pinned-order edge
/// list every mounted addon's host filters by its own grants. All but link liveness are deterministic by construction:
/// body positions, seat occupancy, the document's region rows, and route commands are already sim-lane state, so
/// replay covers them by re-execution and nothing about them is taped. The link family is the one exception — see
/// its own paragraph below.
/// </summary>
/// <remarks>
/// <para><b>Collision pairs are a proximity test, not the physical contact resolver.</b>
/// <see cref="IContactField"/> resolves a body against static solid scene rows, screens, and creation placements, while
/// <see cref="WorldBodyContactMode.Solid"/> separately opts a kit into bounded dynamic-body depenetration. This feed
/// does not reuse either solver's admitted pair set: it runs an honest overlap test over selected pairs of the bodies'
/// collider volumes in full 3D — a capsule/sphere pair by 3D segment-to-segment distance against the summed radii, a
/// box-involving pair by an oriented per-axis extent test on X, Y, and Z. A kit with no collider has no event volume
/// and therefore emits no collision pair. Discovery uses deterministic sweep-and-prune and the authored
/// <see cref="WorldCollisionEvents"/> limits; established pairs are retained first. A saturated crowd therefore has
/// bounded work and memory and may omit lower-priority new proximity pairs. It is deliberately not a claim about
/// physical contact response.</para>
/// <para><b>Region containment</b> re-reads the document's placements every tick (a linear scan, cheap at
/// authoring scale) rather than caching a derived structure keyed to the Placements section's install cadence —
/// simpler and correct; revisit only if profiling ever shows the scan matters.</para>
/// <para><b>Route edges are pushed, not diffed.</b> <see cref="WorldGrants"/> reports changes from the Control-route
/// storage every producer shares, including engagement, repair, and direct grant/revoke outcomes. The server resolves
/// a Seat/Peer principal to its body index and queues the corresponding edge; principals that drive no body produce no
/// fabricated source. <see cref="Collect"/> drains the queue at the pinned collection point so a route transition and
/// this tick's other edges arrive in the same batch.</para>
/// <para><b>Link edges are the one taped family.</b> Whether a federation neighbour delivered a refreshed projection
/// on a given tick is transport ingress, not sim state: nothing in the document or the population determines it, so
/// it cannot be re-derived. <see cref="ObserveLinkDelivery(string)"/> is the one input, called once per authored
/// adjacency row whose delivered snapshot tick advanced; the live path calls it from <c>WorldServer.Step</c> right
/// after the adjacency source freezes the tick's projection graph, and <c>WorldReplaySnapshot.Drive</c> calls it from
/// the tape's own <c>LinkDelivery</c> entries. Everything downstream — the per-tick count
/// <see cref="LinkStalenessTicks"/> reports, the grace comparison, and both edges — derives from that boolean plus
/// the local tick, so a taped run reproduces the identical edge sequence and the identical <c>$link:</c> values. The
/// tape does not carry the delivered CONTENT (poses, definition revisions): a replay reproduces WHEN a seam went
/// dark, never what the neighbour was showing.</para>
/// <para>Link edges gate on <see cref="GrantSubject.Adjacency"/> — the authored row's own name, <c>Region</c>'s twin
/// for the federation seam — so an addon receives them through an <c>observe adjacency:&lt;name&gt;</c> row carrying
/// an event budget, exactly as the region family gates on its placement id.</para>
/// </remarks>
public sealed class WorldEventFeed {
    private readonly List<WorldEventEdge> m_edges = [];
    private readonly List<WorldEventEdge> m_pendingRoutes = [];
    // Per-adjacency link liveness, keyed by the authored adjacency row name (the same "identity is the name, never a
    // reorderable ordinal" rule m_regionOccupancy follows).
    private readonly Dictionary<string, LinkState> m_links = new(comparer: StringComparer.Ordinal);
    private readonly bool[] m_seatOccupied = new bool[WorldBodiesLimits.LocalSeatCount];
    // Pairwise overlap state, keyed by the ascending (a, b) body-index pair. Its degree is bounded by the authored
    // collision-events policy, so a dense few-thousand-body crowd never becomes a quadratic table.
    private readonly HashSet<(int A, int B)> m_overlapping = [];
    private readonly BroadphaseBody[] m_collisionBodies;
    private readonly byte[] m_collisionDegrees;
    private readonly ulong[] m_collisionPairKeys;
    private readonly CollisionTransition[] m_collisionTransitions;
    private readonly int m_maximumTrackedPairs;
    // Region containment, per (regionId, bodyIndex). Keyed by region id (== the carrying placement's Id) rather
    // than a derived ordinal, so a document edit that reorders placements does not silently reinterpret stale state
    // under a reused ordinal — a region's identity is its name, exactly as GrantSubject.Region already keys it.
    private readonly Dictionary<string, bool[]> m_regionOccupancy = new(comparer: StringComparer.Ordinal);

    /// <summary>Initializes a bounded event feed for one population.</summary>
    /// <param name="capacity">The population's authored body capacity.</param>
    public WorldEventFeed(int capacity) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: capacity);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: capacity, other: WorldBodiesLimits.CapacityCeiling);

        m_maximumTrackedPairs = MaximumTrackedPairsForCapacity(capacity);
        m_collisionBodies = new BroadphaseBody[capacity];
        m_collisionDegrees = new byte[capacity];
        m_collisionPairKeys = new ulong[m_maximumTrackedPairs];
        m_collisionTransitions = new CollisionTransition[checked(m_maximumTrackedPairs * 2)];
        _ = m_overlapping.EnsureCapacity(capacity: m_maximumTrackedPairs);
    }

    /// <summary>Gets this tick's collected edges, in pinned order (seats, then regions, then collisions, then links,
    /// then routes). Valid only between one <see cref="Collect"/> call and the next.</summary>
    public IReadOnlyList<WorldEventEdge> Edges => m_edges;
    /// <summary>Gets the new-pair broadphase candidates inspected by the latest collection pass.</summary>
    public int CollisionCandidates { get; private set; }
    /// <summary>Gets the bodies whose new-pair search exhausted its candidate budget in the latest pass.</summary>
    public int CollisionLimitedBodies { get; private set; }
    /// <summary>Gets the established overlap pairs retained after the latest pass.</summary>
    public int CollisionTrackedPairs => m_overlapping.Count;

    /// <summary>Returns the absolute overlap-pair storage ceiling for a population capacity.</summary>
    internal static int MaximumTrackedPairsForCapacity(int capacity) =>
        checked((capacity * WorldCollisionEvents.MaximumPairsPerBody) / 2);

    /// <summary>One authored adjacency row's checkpointed link-liveness state.</summary>
    /// <param name="Adjacency">The authored <c>adjacencies</c> row name.</param>
    /// <param name="DeliveredTick">The highest neighbour snapshot tick observed on this edge; <c>0</c> when nothing
    /// has ever been delivered.</param>
    /// <param name="StaleTicks">Simulation ticks since the last delivered refresh.</param>
    /// <param name="PendingRefresh">Whether a refresh has been observed since the last <see cref="Collect"/>.</param>
    /// <param name="Dropped">Whether the last edge emitted for this row was <see cref="WorldEventFamily.LinkDropped"/>.</param>
    public readonly record struct WorldEventLinkState(string Adjacency, ulong DeliveredTick, long StaleTicks, bool PendingRefresh, bool Dropped);
    /// <summary>One <see cref="WorldEventFeed"/>'s checkpointed state — the edge-detection tables
    /// (<see cref="m_overlapping"/>/<see cref="m_regionOccupancy"/>/<see cref="m_seatOccupied"/>/
    /// <see cref="m_links"/>) a later tick's enter/exit comparison reads, plus the two buffers that must reproduce
    /// exactly for a checkpoint taken mid-episode to resume the same edges.</summary>
    public sealed record WorldEventFeedCheckpoint(
        IReadOnlyList<WorldEventEdge> Edges,
        IReadOnlyList<WorldEventEdge> PendingRoutes,
        bool[] SeatOccupied,
        IReadOnlyList<(int A, int B)> Overlapping,
        IReadOnlyList<(string Region, bool[] Occupancy)> RegionOccupancy,
        IReadOnlyList<WorldEventLinkState> Links
    );

    /// <summary>Captures this feed's live state.</summary>
    public WorldEventFeedCheckpoint Capture() => new(
        Edges: [.. m_edges],
        PendingRoutes: [.. m_pendingRoutes],
        SeatOccupied: [.. m_seatOccupied],
        Overlapping: [.. m_overlapping.OrderBy(static pair => pair.A).ThenBy(static pair => pair.B)],
        RegionOccupancy: [.. m_regionOccupancy.Select(selector: static pair => (pair.Key, ((bool[])pair.Value.Clone())))],
        Links: [.. m_links.Select(selector: static pair => new WorldEventLinkState(
                Adjacency: pair.Key,
                DeliveredTick: pair.Value.DeliveredTick,
                Dropped: pair.Value.Dropped,
                PendingRefresh: pair.Value.PendingRefresh,
                StaleTicks: pair.Value.StaleTicks
            ))]
    );
    /// <summary>Preflights a checkpoint without changing this feed.</summary>
    internal void ValidateCheckpoint(WorldEventFeedCheckpoint checkpoint) {
        ArgumentNullException.ThrowIfNull(argument: checkpoint);
        if (
            checkpoint.Edges is null ||
            checkpoint.PendingRoutes is null ||
            checkpoint.SeatOccupied is null ||
            checkpoint.Overlapping is null ||
            checkpoint.RegionOccupancy is null ||
            checkpoint.Links is null
        ) {
            throw new InvalidOperationException(message: "event feed checkpoint contains a null collection.");
        }
        if (checkpoint.SeatOccupied.Length != m_seatOccupied.Length) {
            throw new InvalidOperationException(message: $"event feed checkpoint carries {checkpoint.SeatOccupied.Length} seat latches; this world requires {m_seatOccupied.Length}.");
        }
        if (checkpoint.Overlapping.Count > m_maximumTrackedPairs) {
            throw new InvalidOperationException(message: $"event feed checkpoint carries {checkpoint.Overlapping.Count} overlap pairs; this population admits at most {m_maximumTrackedPairs}.");
        }

        Array.Clear(array: m_collisionDegrees);
        var overlapCount = 0;
        foreach (var pair in checkpoint.Overlapping) {
            if (
                (pair.A < 0) ||
                (pair.A >= pair.B) ||
                (((uint)pair.B) >= ((uint)m_collisionDegrees.Length)) ||
                (++m_collisionDegrees[pair.A] > WorldCollisionEvents.MaximumPairsPerBody) ||
                (++m_collisionDegrees[pair.B] > WorldCollisionEvents.MaximumPairsPerBody)
            ) {
                throw new InvalidOperationException(message: $"event feed checkpoint carries invalid or over-capacity overlap pair ({pair.A}, {pair.B}).");
            }
            m_collisionPairKeys[overlapCount++] = CollisionPairKey(a: pair.A, b: pair.B);
        }
        Array.Sort(array: m_collisionPairKeys, index: 0, length: overlapCount);
        for (var index = 1; index < overlapCount; index++) {
            if (m_collisionPairKeys[index] == m_collisionPairKeys[(index - 1)]) {
                var key = m_collisionPairKeys[index];
                throw new InvalidOperationException(message: $"event feed checkpoint duplicates overlap pair ({(int)(key >> 32)}, {(int)(uint)key}).");
            }
        }

        var regionNames = new HashSet<string>(comparer: StringComparer.Ordinal);
        foreach (var row in checkpoint.RegionOccupancy) {
            if (string.IsNullOrWhiteSpace(value: row.Region) || !regionNames.Add(item: row.Region)) {
                throw new InvalidOperationException(message: $"event feed checkpoint carries an empty or repeated region name '{row.Region}'.");
            }
            if (row.Occupancy is null || row.Occupancy.Length != m_collisionBodies.Length) {
                throw new InvalidOperationException(message: $"event feed checkpoint region '{row.Region}' carries {row.Occupancy?.Length ?? -1} body latches; this world requires {m_collisionBodies.Length}.");
            }
        }

        var linkNames = new HashSet<string>(comparer: StringComparer.Ordinal);
        foreach (var link in checkpoint.Links) {
            if (string.IsNullOrWhiteSpace(value: link.Adjacency) || !linkNames.Add(item: link.Adjacency) || link.StaleTicks < 0L) {
                throw new InvalidOperationException(message: $"event feed checkpoint carries invalid or repeated link state '{link.Adjacency}'.");
            }
        }
        foreach (var edge in checkpoint.Edges.Concat(second: checkpoint.PendingRoutes)) {
            if (!Enum.IsDefined(value: edge.Family)) {
                throw new InvalidOperationException(message: $"event feed checkpoint carries unknown event family '{edge.Family}'.");
            }
        }
    }

    /// <summary>Restores this feed's live state from a previously captured checkpoint.</summary>
    public void Restore(WorldEventFeedCheckpoint checkpoint) {
        ValidateCheckpoint(checkpoint: checkpoint);

        m_edges.Clear();
        m_edges.AddRange(collection: checkpoint.Edges);
        m_pendingRoutes.Clear();
        m_pendingRoutes.AddRange(collection: checkpoint.PendingRoutes);
        m_links.Clear();
        foreach (var link in checkpoint.Links) {
            m_links[link.Adjacency] = new LinkState {
                DeliveredTick = link.DeliveredTick,
                Dropped = link.Dropped,
                PendingRefresh = link.PendingRefresh,
                StaleTicks = link.StaleTicks,
            };
        }
        checkpoint.SeatOccupied.CopyTo(array: m_seatOccupied, index: 0);

        m_overlapping.Clear();
        foreach (var pair in checkpoint.Overlapping) {
            _ = m_overlapping.Add(item: pair);
        }
        m_regionOccupancy.Clear();
        foreach (var row in checkpoint.RegionOccupancy) {
            m_regionOccupancy[row.Region] = ((bool[])row.Occupancy.Clone());
        }
    }

    private static (FixedVector3 Center, FixedVector3 Extent) Bounds(FixedVector3 position, FixedQuaternion orientation,
        in FixedBodyColliderVolume volume) {
        if (volume.Kind == FixedBodyColliderKind.Sphere) {
            var radius = new FixedVector3(
                X: volume.Radius,
                Y: volume.Radius,
                Z: volume.Radius
            );

            return (Center: (position + orientation.Rotate(vector: volume.Center)), Extent: radius);
        }

        if (volume.Kind == FixedBodyColliderKind.Capsule) {
            var (start, end) = Segment(
                orientation: orientation,
                position: position,
                volume: in volume
            );
            var delta = (end - start);
            var radius = new FixedVector3(
                X: volume.Radius,
                Y: volume.Radius,
                Z: volume.Radius
            );
            var extent = new FixedVector3(
                X: (FixedQ4816.Abs(value: delta.X) / FixedQ4816.FromInteger(value: 2L)),
                Y: (FixedQ4816.Abs(value: delta.Y) / FixedQ4816.FromInteger(value: 2L)),
                Z: (FixedQ4816.Abs(value: delta.Z) / FixedQ4816.FromInteger(value: 2L))
            );

            return (Center: ((start + end) / FixedQ4816.FromInteger(value: 2L)), Extent: (extent + radius));
        }

        var center = (position + orientation.Rotate(vector: volume.Center));
        var rotation = (orientation * volume.Rotation).Normalize();
        var unitX = new FixedVector3(
            X: FixedQ4816.One,
            Y: FixedQ4816.Zero,
            Z: FixedQ4816.Zero
        );
        var unitY = new FixedVector3(
            X: FixedQ4816.Zero,
            Y: FixedQ4816.One,
            Z: FixedQ4816.Zero
        );
        var unitZ = new FixedVector3(
            X: FixedQ4816.Zero,
            Y: FixedQ4816.Zero,
            Z: FixedQ4816.One
        );
        var axisX = rotation.Rotate(vector: unitX);
        var axisY = rotation.Rotate(vector: unitY);
        var axisZ = rotation.Rotate(vector: unitZ);
        var boxExtent = new FixedVector3(
            X: (((FixedQ4816.Abs(value: axisX.X) * volume.HalfExtents.X) + (FixedQ4816.Abs(value: axisY.X) * volume.HalfExtents.Y)) + (FixedQ4816.Abs(value: axisZ.X) * volume.HalfExtents.Z)),
            Y: (((FixedQ4816.Abs(value: axisX.Y) * volume.HalfExtents.X) + (FixedQ4816.Abs(value: axisY.Y) * volume.HalfExtents.Y)) + (FixedQ4816.Abs(value: axisZ.Y) * volume.HalfExtents.Z)),
            Z: (((FixedQ4816.Abs(value: axisX.Z) * volume.HalfExtents.X) + (FixedQ4816.Abs(value: axisY.Z) * volume.HalfExtents.Y)) + (FixedQ4816.Abs(value: axisZ.Z) * volume.HalfExtents.Z))
        );

        return (Center: center, Extent: boxExtent);
    }
    private static ulong CollisionPairKey(int a, int b) => (((ulong)(uint)a << 32) | (uint)b);
    private void AddCollisionTransition(int a, int b, bool now, ref int count) {
        m_collisionTransitions[count++] = new CollisionTransition(
            Key: CollisionPairKey(a: a, b: b),
            Now: now
        );
    }
    private void CollectCollisions(WorldPopulation population, WorldCollisionEvents policy) {
        Array.Clear(array: m_collisionDegrees);
        CollisionCandidates = 0;
        CollisionLimitedBodies = 0;

        var pairCount = 0;
        foreach (var pair in m_overlapping) {
            m_collisionPairKeys[pairCount++] = CollisionPairKey(a: pair.A, b: pair.B);
        }
        Array.Sort(array: m_collisionPairKeys, index: 0, length: pairCount);

        // Existing overlap relationships have continuity priority over new discoveries. Validate them in pair order,
        // retaining the first pairs admitted by a newly-lowered degree limit and ending the rest deterministically.
        var transitionCount = 0;
        for (var ordinal = 0; ordinal < pairCount; ordinal++) {
            var key = m_collisionPairKeys[ordinal];
            var a = (int)(key >> 32);
            var b = (int)(uint)key;
            var retained = (
                (policy.MaxPairsPerBody > 0) &&
                (m_collisionDegrees[a] < policy.MaxPairsPerBody) &&
                (m_collisionDegrees[b] < policy.MaxPairsPerBody) &&
                population.IsActive(index: a) &&
                population.IsActive(index: b) &&
                (population.EntryBody(index: a) is { Collider: not null } bodyA) &&
                (population.EntryBody(index: b) is { Collider: not null } bodyB) &&
                Overlaps(a: bodyA, b: bodyB)
            );
            if (retained) {
                m_collisionDegrees[a]++;
                m_collisionDegrees[b]++;
            } else {
                _ = m_overlapping.Remove(item: (a, b));
                AddCollisionTransition(a: a, b: b, now: false, count: ref transitionCount);
            }
        }

        var bodyCount = 0;
        if (policy.MaxPairsPerBody > 0) {
            // Hoisted out of the loop below (CA2014): one reused stack buffer, fully overwritten by
            // ScaledColliderVolumes on every call, never read across bodies.
            Span<FixedBodyColliderVolume> broadphaseScratch = stackalloc FixedBodyColliderVolume[WorldCollider.MaxVolumes];

            for (var index = 0; index < population.Capacity; index++) {
                if (
                    !population.IsActive(index: index) ||
                    (population.EntryBody(index: index) is not { Collider: { } collider } body)
                ) {
                    continue;
                }

                var radius = FixedDynamicBodyContacts.BroadphaseRadius(volumes: body.ScaledColliderVolumes(
                    volumes: collider.Volumes,
                    scratch: broadphaseScratch
                ));
                m_collisionBodies[bodyCount++] = new BroadphaseBody(
                    Index: index,
                    MinimumX: (body.FixedPosition.X - radius),
                    MaximumX: (body.FixedPosition.X + radius),
                    Radius: radius
                );
            }
            Array.Sort(array: m_collisionBodies, index: 0, length: bodyCount);
        }

        // Discover new pairs. The x sweep removes distant bodies; Y/Z enclosing-sphere checks remove the other two
        // axes before the exact compound-volume test. Both the inspected-candidate and retained-degree bounds hold
        // even when every body occupies the same point.
        var begins = 0;
        for (var leftOrdinal = 0; leftOrdinal < bodyCount; leftOrdinal++) {
            if (begins >= policy.BeginBudget) {
                break;
            }
            var left = m_collisionBodies[leftOrdinal];
            if (m_collisionDegrees[left.Index] >= policy.MaxPairsPerBody) {
                continue;
            }

            var inspected = 0;
            for (var rightOrdinal = (leftOrdinal + 1);
                (rightOrdinal < bodyCount) && (m_collisionBodies[rightOrdinal].MinimumX <= left.MaximumX);
                rightOrdinal++) {
                if (inspected >= policy.CandidateBudget) {
                    CollisionLimitedBodies++;
                    break;
                }
                inspected++;
                CollisionCandidates++;

                var right = m_collisionBodies[rightOrdinal];
                if (
                    (m_collisionDegrees[left.Index] >= policy.MaxPairsPerBody) ||
                    (m_collisionDegrees[right.Index] >= policy.MaxPairsPerBody)
                ) {
                    continue;
                }

                var a = Math.Min(left.Index, right.Index);
                var b = Math.Max(left.Index, right.Index);
                if (m_overlapping.Contains(item: (a, b))) {
                    continue;
                }

                var leftBody = population.EntryBody(index: left.Index)!;
                var rightBody = population.EntryBody(index: right.Index)!;
                var radius = (left.Radius + right.Radius);
                var delta = (leftBody.FixedPosition - rightBody.FixedPosition);
                if (
                    (FixedQ4816.Abs(value: delta.Y) > radius) ||
                    (FixedQ4816.Abs(value: delta.Z) > radius) ||
                    !Overlaps(a: leftBody, b: rightBody)
                ) {
                    continue;
                }

                _ = m_overlapping.Add(item: (a, b));
                m_collisionDegrees[a]++;
                m_collisionDegrees[b]++;
                begins++;
                AddCollisionTransition(a: a, b: b, now: true, count: ref transitionCount);
                if (begins >= policy.BeginBudget) {
                    break;
                }
            }
        }

        Array.Sort(array: m_collisionTransitions, index: 0, length: transitionCount);
        for (var ordinal = 0; ordinal < transitionCount; ordinal++) {
            var transition = m_collisionTransitions[ordinal];
            var a = (int)(transition.Key >> 32);
            var b = (int)(uint)transition.Key;
            m_edges.Add(item: new WorldEventEdge(
                Family: (transition.Now ? WorldEventFamily.CollisionBegin : WorldEventFamily.CollisionEnd),
                GateA: GrantSubject.Body(index: a),
                GateB: GrantSubject.Body(index: b),
                A: a,
                B: b
            ));
        }
    }
    // The shared latched-transition body every per-tick edge-detection pass (collision overlap, region containment,
    // seat occupancy) follows: skip when `now` agrees with `latch`, else store `now` into `latch` and emit
    // `onSet`/`onClear`. `latch` names a bool[] slot at every call site except the collision pass's HashSet-backed
    // membership, which reads its latch into a local first and writes the set back only on a reported change.
    private bool EmitTransition(ref bool latch, bool now, WorldEventEdge onSet, WorldEventEdge onClear) {
        if (now == latch) {
            return false;
        }

        latch = now;
        m_edges.Add(item: (now
            ? onSet
            : onClear
        ));

        return true;
    }

    private readonly record struct BroadphaseBody(int Index, FixedQ4816 MinimumX, FixedQ4816 MaximumX, FixedQ4816 Radius) : IComparable<BroadphaseBody> {
        public int CompareTo(BroadphaseBody other) {
            var minimum = MinimumX.CompareTo(other.MinimumX);
            return (minimum != 0 ? minimum : Index.CompareTo(other.Index));
        }
    }
    private readonly record struct CollisionTransition(ulong Key, bool Now) : IComparable<CollisionTransition> {
        public int CompareTo(CollisionTransition other) => Key.CompareTo(other.Key);
    }

    // One authored adjacency row's liveness. Reference type deliberately: the collection pass mutates in place, and a
    // struct in a Dictionary would need a re-store per field write.
    private sealed class LinkState {
        public ulong DeliveredTick;
        public bool Dropped;
        public bool PendingRefresh;
        public long StaleTicks;
    }

    // The link pass. Sign/unit: StaleTicks counts SIMULATION ticks since the last delivered refresh, 0 on the tick a
    // refresh landed. A row whose compiled grace is zero (unauthored) is held at 0 and emits nothing, so an
    // unauthored world is unchanged; a grace with no tick mapping (a positive value at rate 0) accumulates staleness
    // but never drops.
    private void CollectLinks(WorldDefinition definition) {
        var ordinal = 0;

        foreach (var row in (definition.Adjacencies ?? [])) {
            if (row is null) {
                continue;
            }

            var thisOrdinal = ordinal++;
            var name = row.Name.Value;

            if (!m_links.TryGetValue(
                key: name,
                value: out var state
            )) {
                state = new LinkState();
                m_links[name] = state;
            }

            var grace = definition.AdjacencyLivenessGraceTicks(adjacency: row);

            if (grace.IsZero) {
                state.Dropped = false;
                state.PendingRefresh = false;
                state.StaleTicks = 0L;

                continue;
            }
            if (state.PendingRefresh) {
                state.PendingRefresh = false;
                state.StaleTicks = 0L;

                if (state.Dropped) {
                    state.Dropped = false;
                    m_edges.Add(item: new WorldEventEdge(
                        Family: WorldEventFamily.LinkEstablished,
                        GateA: GrantSubject.Adjacency(name: name),
                        GateB: null,
                        A: thisOrdinal,
                        B: 0L
                    ));
                }

                continue;
            }
            if (state.StaleTicks < long.MaxValue) {
                state.StaleTicks++;
            }
            if (
                grace.IsNever ||
                state.Dropped ||
                (state.StaleTicks < grace.Ticks)
            ) {
                continue;
            }

            state.Dropped = true;
            m_edges.Add(item: new WorldEventEdge(
                Family: WorldEventFamily.LinkDropped,
                GateA: GrantSubject.Adjacency(name: name),
                GateB: null,
                A: thisOrdinal,
                B: state.StaleTicks
            ));
        }
    }
    private void CollectRegions(WorldDefinition definition, WorldPopulation population) {
        var ordinal = 0;

        foreach (var placement in definition.Placements) {
            if (
                (placement is null) ||
                (placement.Region is not { } region)
            ) {
                continue;
            }

            var thisOrdinal = ordinal++;

            if (!m_regionOccupancy.TryGetValue(
                key: placement.Id,
                value: out var occupancy
            )) {
                occupancy = new bool[population.Capacity];
                m_regionOccupancy[placement.Id] = occupancy;
            }

            // An ATTACHED region's sensing sphere centers on the resolved live pose (the SAME fixed-point resolve
            // world.attachments answers), never the row's inert static Position — an inactive target body resolves
            // no center, so the region contributes nothing this tick (every occupant exits, mirroring the row's
            // established "contributes nothing" verdict) rather than sensing at a stale last-known point.
            var hasCenter = true;
            var center = default(FixedVector3);

            if (placement.Attach is { } attach) {
                hasCenter = WorldPlacementAttachment.TryResolve(
                    attach: attach,
                    population: population,
                    position: out center,
                    reason: out _,
                    yawRadians: out _
                );
            } else {
                center = FixedVector3.FromVector3(value: WorldDefinitionRows.ResolvedPosition(definition: definition, placement: placement));
            }

            var radius = FixedQ4816.FromDouble(value: region.Radius);

            for (var body = 0; (body < population.Capacity); body++) {
                var inside = (
                    hasCenter &&
                    population.IsActive(index: body) &&
                    (population.EntryBody(index: body) is { } entry) &&
                    ((entry.FixedPosition - center).Length <= radius)
                );

                _ = EmitTransition(
                    latch: ref occupancy[body],
                    now: inside,
                    onClear: new WorldEventEdge(
                        Family: WorldEventFamily.RegionExit,
                        GateA: GrantSubject.Region(name: placement.Id),
                        GateB: null,
                        A: body,
                        B: thisOrdinal
                    ),
                    onSet: new WorldEventEdge(
                        Family: WorldEventFamily.RegionEnter,
                        GateA: GrantSubject.Region(name: placement.Id),
                        GateB: null,
                        A: body,
                        B: thisOrdinal
                    )
                );
            }
        }
    }
    private void CollectSeats(WorldPopulation population) {
        for (var seat = 0; (seat < WorldBodiesLimits.LocalSeatCount); seat++) {
            _ = EmitTransition(
                latch: ref m_seatOccupied[seat],
                now: population.IsHumanOccupied(bodyIndex: seat),
                onClear: new WorldEventEdge(
                    Family: WorldEventFamily.SeatLeave,
                    GateA: GrantSubject.Seat(index: seat),
                    GateB: null,
                    A: seat,
                    B: 0L
                ),
                onSet: new WorldEventEdge(
                    Family: WorldEventFamily.SeatJoin,
                    GateA: GrantSubject.Seat(index: seat),
                    GateB: null,
                    A: seat,
                    B: 0L
                )
            );
        }
    }
    private static bool Overlaps(WorldBody a, WorldBody b) {
        var aCollider = a.Collider;
        var bCollider = b.Collider;

        if (
            (aCollider is not { } aVolume) ||
            (bCollider is not { } bVolume)
        ) {
            return false;
        }

        Span<FixedBodyColliderVolume> leftScratch = stackalloc FixedBodyColliderVolume[WorldCollider.MaxVolumes];
        Span<FixedBodyColliderVolume> rightScratch = stackalloc FixedBodyColliderVolume[WorldCollider.MaxVolumes];
        var leftVolumes = a.ScaledColliderVolumes(
            volumes: aVolume.Volumes,
            scratch: leftScratch
        );
        var rightVolumes = b.ScaledColliderVolumes(
            volumes: bVolume.Volumes,
            scratch: rightScratch
        );

        foreach (var left in leftVolumes) {
            foreach (var right in rightVolumes) {
                if (Overlaps(
                    leftPosition: a.FixedPosition,
                    leftOrientation: a.FixedOrientation,
                    left: in left,
                    rightPosition: b.FixedPosition,
                    rightOrientation: b.FixedOrientation,
                    right: in right
                )) {
                    return true;
                }
            }
        }

        return false;
    }
    private static bool Overlaps(FixedVector3 leftPosition, FixedQuaternion leftOrientation, in FixedBodyColliderVolume left,
        FixedVector3 rightPosition, FixedQuaternion rightOrientation, in FixedBodyColliderVolume right) {
        if (
            (left.Kind != FixedBodyColliderKind.Box) &&
            (right.Kind != FixedBodyColliderKind.Box)
        ) {
            var (leftStart, leftEnd) = Segment(
                orientation: leftOrientation,
                position: leftPosition,
                volume: in left
            );
            var (rightStart, rightEnd) = Segment(
                orientation: rightOrientation,
                position: rightPosition,
                volume: in right
            );
            var radius = (left.Radius + right.Radius);

            return (SegmentDistanceSquared(
                p1: leftStart,
                p2: rightStart,
                q1: leftEnd,
                q2: rightEnd
            ) < (radius * radius));
        }

        var (leftCenter, leftExtent) = Bounds(
            orientation: leftOrientation,
            position: leftPosition,
            volume: in left
        );
        var (rightCenter, rightExtent) = Bounds(
            orientation: rightOrientation,
            position: rightPosition,
            volume: in right
        );
        var delta = (leftCenter - rightCenter);

        return (
            (FixedQ4816.Abs(value: delta.X) < (leftExtent.X + rightExtent.X)) &&
            (FixedQ4816.Abs(value: delta.Y) < (leftExtent.Y + rightExtent.Y)) &&
            (FixedQ4816.Abs(value: delta.Z) < (leftExtent.Z + rightExtent.Z))
        );
    }
    private void QueueRoute(WorldEventFamily family, int sourceBody, GrantSubject target) {
        // Encoding (see AddonAbi.ObservationVerbs.EventRouteEngaged's own doc): B >= 0 is a screen index; B < 0 is
        // -(bodyIndex + 1). A screen index is always >= 0 and a body index is always >= 0, so the sign alone
        // disambiguates without a third payload lane.
        var encodedTarget = ((target.Kind == GrantSubjectKind.Body)
            ? -(target.Value + 1)
            : target.Value
        );
        var targetGate = ((target.Kind is GrantSubjectKind.Body or GrantSubjectKind.Screen)
            ? (GrantSubject?)target
            : null
        );

        m_pendingRoutes.Add(item: new WorldEventEdge(
            Family: family,
            GateA: GrantSubject.Body(index: sourceBody),
            GateB: targetGate,
            A: sourceBody,
            B: encodedTarget
        ));
    }
    private static (FixedVector3 Start, FixedVector3 End) Segment(FixedVector3 position, FixedQuaternion orientation,
        in FixedBodyColliderVolume volume) {
        var start = (position + orientation.Rotate(vector: volume.Center));
        var end = ((volume.Kind == FixedBodyColliderKind.Capsule)
            ? (position + orientation.Rotate(vector: volume.Endpoint))
            : start
        );

        return (Start: start, End: end);
    }
    private static FixedQ4816 SegmentDistanceSquared(FixedVector3 p1, FixedVector3 q1, FixedVector3 p2, FixedVector3 q2) {
        var d1 = (q1 - p1);
        var d2 = (q2 - p2);
        var r = (p1 - p2);
        var a = FixedVector3.Dot(
            left: d1,
            right: d1
        );
        var e = FixedVector3.Dot(
            left: d2,
            right: d2
        );
        var f = FixedVector3.Dot(
            left: d2,
            right: r
        );
        FixedQ4816 s;
        FixedQ4816 t;

        if (
            (a <= FixedQ4816.Zero) &&
            (e <= FixedQ4816.Zero)
        ) {
            return FixedVector3.Dot(
                left: r,
                right: r
            );
        }

        if (a <= FixedQ4816.Zero) {
            s = FixedQ4816.Zero;
            t = FixedQ4816.Clamp(
                value: (f / e),
                minimum: FixedQ4816.Zero,
                maximum: FixedQ4816.One
            );
        } else {
            var c = FixedVector3.Dot(
                left: d1,
                right: r
            );

            if (e <= FixedQ4816.Zero) {
                t = FixedQ4816.Zero;
                s = FixedQ4816.Clamp(
                    value: (-c / a),
                    minimum: FixedQ4816.Zero,
                    maximum: FixedQ4816.One
                );
            } else {
                var b = FixedVector3.Dot(
                    left: d1,
                    right: d2
                );
                var denominator = ((a * e) - (b * b));

                s = ((denominator > FixedQ4816.Zero)
                    ? FixedQ4816.Clamp(
                        value: (((b * f) - (c * e)) / denominator),
                        minimum: FixedQ4816.Zero,
                        maximum: FixedQ4816.One
                    )
                    : FixedQ4816.Zero
                );
                t = (((b * s) + f) / e);
                if (t < FixedQ4816.Zero) {
                    t = FixedQ4816.Zero;
                    s = FixedQ4816.Clamp(
                        value: (-c / a),
                        minimum: FixedQ4816.Zero,
                        maximum: FixedQ4816.One
                    );
                } else if (t > FixedQ4816.One) {
                    t = FixedQ4816.One;
                    s = FixedQ4816.Clamp(
                        value: ((b - c) / a),
                        minimum: FixedQ4816.Zero,
                        maximum: FixedQ4816.One
                    );
                }
            }
        }

        var closest = ((p1 + (d1 * s)) - (p2 + (d2 * t)));

        return FixedVector3.Dot(
            left: closest,
            right: closest
        );
    }

    /// <summary>Computes this tick's edge list — the one call site, from <see cref="WorldServer.Step"/> after the
    /// population advances (so positions/occupancy reflect this tick's settled state) and before the addon runtime's
    /// read pump stages them for delivery.</summary>
    /// <param name="definition">The live world definition (its placements, for region rows).</param>
    /// <param name="population">The live population (positions, activity, seat occupancy).</param>
    public void Collect(WorldDefinition definition, WorldPopulation population) {
        m_edges.Clear();

        CollectSeats(population: population);
        CollectRegions(
            definition: definition,
            population: population
        );
        CollectCollisions(
            population: population,
            policy: definition.Collision.Events
        );
        CollectLinks(definition: definition);

        m_edges.AddRange(collection: m_pendingRoutes);
        m_pendingRoutes.Clear();
    }
    /// <summary>Returns the simulation ticks since the adjacency row named <paramref name="adjacencyName"/> last
    /// received a delivered neighbour refresh, as of the most recent <see cref="Collect"/> — the live quantity a
    /// world rule's <c>$link:&lt;adjacencyName&gt;</c> reserved channel reads (see <c>WorldRuleFacts.LinkPrefix</c>).
    /// </summary>
    /// <param name="adjacencyName">The authored <c>adjacencies</c> row name.</param>
    /// <returns>The staleness in simulation ticks; <c>0</c> for a fresh edge, for an edge whose
    /// <c>livenessGraceSeconds</c> is unauthored, and for a row this feed has never seen.</returns>
    public long LinkStalenessTicks(string adjacencyName) => (m_links.TryGetValue(
        key: adjacencyName,
        value: out var state
    )
        ? state.StaleTicks
        : 0L
    );
    /// <summary>Returns whether the adjacency row named <paramref name="adjacencyName"/> is currently dropped — the
    /// link pass's own latched verdict as of the most recent <see cref="Collect"/> (set on the tick the staleness
    /// reaches the row's compiled <c>livenessGraceSeconds</c>, cleared on the next delivered refresh), so a consumer
    /// asks the one comparison this feed owns instead of re-spelling it from <see cref="LinkStalenessTicks"/> and the
    /// compiled grace. <see langword="false"/> for a row whose sensing is disabled (an unauthored grace), a row with
    /// no tick mapping (a positive grace at simulation rate 0), and a row this feed has never seen — exactly the
    /// cases the pass itself never drops.</summary>
    /// <param name="adjacencyName">The authored <c>adjacencies</c> row name.</param>
    /// <returns>Whether the row's last emitted edge state is dropped.</returns>
    public bool LinkDropped(string adjacencyName) => (m_links.TryGetValue(
        key: adjacencyName,
        value: out var state
    ) && state.Dropped);
    /// <summary>Records that the adjacency row named <paramref name="adjacencyName"/> received a delivered neighbour
    /// refresh, to be consumed by the next <see cref="Collect"/>. The one link-liveness input — see this type's own
    /// remarks for why it is taped rather than re-derived.</summary>
    /// <param name="adjacencyName">The authored <c>adjacencies</c> row name.</param>
    public void ObserveLinkDelivery(string adjacencyName) {
        ArgumentException.ThrowIfNullOrEmpty(argument: adjacencyName);

        if (!m_links.TryGetValue(
            key: adjacencyName,
            value: out var state
        )) {
            state = new LinkState();
            m_links[adjacencyName] = state;
        }

        state.PendingRefresh = true;
    }
    /// <summary>Records a delivered neighbour image for <paramref name="adjacencyName"/> and reports whether it is a
    /// REFRESH — a strictly higher neighbour snapshot tick than any previously observed on this edge. The live
    /// path's entry point: a repeated or reordered delivery of the same neighbour tick is not a refresh and must not
    /// re-arm the link (nor reach the tape).</summary>
    /// <param name="adjacencyName">The authored <c>adjacencies</c> row name.</param>
    /// <param name="deliveredTick">The neighbour's own simulation tick on the delivered image.</param>
    /// <returns><see langword="true"/> when this delivery advanced the edge.</returns>
    public bool ObserveLinkDelivery(string adjacencyName, ulong deliveredTick) {
        ArgumentException.ThrowIfNullOrEmpty(argument: adjacencyName);

        if (!m_links.TryGetValue(
            key: adjacencyName,
            value: out var state
        )) {
            state = new LinkState();
            m_links[adjacencyName] = state;
        }
        if (deliveredTick <= state.DeliveredTick) {
            return false;
        }

        state.DeliveredTick = deliveredTick;
        state.PendingRefresh = true;

        return true;
    }
    /// <summary>Reports whether one body is inside a placement's region as of this tick's collection.</summary>
    /// <param name="placementId">The placement carrying the region facet.</param>
    /// <param name="body">The body's 0-based entity index.</param>
    /// <returns><see langword="true"/> when the body is an occupant.</returns>
    public bool IsOccupant(string placementId, int body) => (
        m_regionOccupancy.TryGetValue(
            key: placementId,
            value: out var occupancy
        ) &&
        (((uint)body) < ((uint)occupancy.Length)) &&
        occupancy[body]
    );
    /// <summary>Returns the live occupant count of the placement region named <paramref name="placementId"/> as of the most
    /// recent <see cref="Collect"/> — the same per-(region, body) occupancy the region pass already tracks for the
    /// addon events feed, read directly for a world rule's <c>$region:&lt;id&gt;</c> reserved channel (see
    /// <see cref="WorldRuleFacts"/>) rather than duplicated.</summary>
    /// <param name="placementId">The region-carrying placement's stable id.</param>
    /// <returns>The occupant count, or zero for a placement this tick has never seen carry a region.</returns>
    public int OccupantCount(string placementId) {
        if (!m_regionOccupancy.TryGetValue(
            key: placementId,
            value: out var occupancy
        )) {
            return 0;
        }

        var count = 0;

        foreach (var occupied in occupancy) {
            if (occupied) {
                count++;
            }
        }

        return count;
    }
    /// <summary>Queues a route-disengaged edge after <see cref="WorldGrants"/> reports an effective route change.</summary>
    /// <param name="sourceBody">The routed body's entity index.</param>
    /// <param name="target">The route target that was cleared.</param>
    public void QueueRouteDisengaged(int sourceBody, GrantSubject target) {
        QueueRoute(
            family: WorldEventFamily.RouteDisengaged,
            sourceBody: sourceBody,
            target: target
        );
    }
    /// <summary>Queues a route-engaged edge after <see cref="WorldGrants"/> reports an effective route change.
    /// Drained by the next <see cref="Collect"/> call.</summary>
    /// <param name="sourceBody">The routed body's entity index.</param>
    /// <param name="target">The route target (a screen or a body).</param>
    public void QueueRouteEngaged(int sourceBody, GrantSubject target) {
        QueueRoute(
            family: WorldEventFamily.RouteEngaged,
            sourceBody: sourceBody,
            target: target
        );
    }
}
