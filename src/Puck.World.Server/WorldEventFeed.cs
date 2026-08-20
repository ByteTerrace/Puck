using Puck.Maths;
using Puck.Physics;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>The closed world-events vocabulary — five event families; the sixth, machine-memory watches, is
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
/// Computes the five world-scoped event families once per tick — collision pairs, region enter/exit, seat
/// join/leave, route/engagement transitions, and federation link established/dropped — as a flat, pinned-order edge
/// list every mounted addon's host filters by its own grants. Four of the five are deterministic by construction:
/// body positions, seat occupancy, the document's region rows, and route commands are already sim-lane state, so
/// replay covers them by re-execution and nothing about them is taped. The link family is the one exception — see
/// its own paragraph below.
/// </summary>
/// <remarks>
/// <para><b>Collision pairs are a proximity test, not the physical contact resolver.</b>
/// <see cref="IContactField"/> resolves a body against static solid scene rows, screens, and creation placements — there is no
/// body-vs-body physical resolution in this engine today, and adding one is a physics feature far outside this
/// lane's mission. This feed instead runs a cheap, honest overlap test over every pair of the two bodies' collider
/// volumes in full 3D — a capsule/sphere pair by 3D segment-to-segment distance against the summed radii, a
/// box-involving pair by an oriented per-axis extent test on X, Y, and Z. A kit with no collider has no event volume
/// and therefore emits no collision pair. It is deliberately not a claim about physical contact response.</para>
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
/// <para>Link edges gate on <see cref="GrantSubject.All"/>. Today's <c>GrantSubjectKind</c> vocabulary carries no
/// adjacency-row subject — the tight analogue would be a <c>Region</c> twin keyed by adjacency name — so the gate is
/// the wildcard plus a nonzero event budget rather than a narrower subject that does not exist.</para>
/// </remarks>
public sealed class WorldEventFeed {
    private readonly List<WorldEventEdge> m_edges = [];
    private readonly List<WorldEventEdge> m_pendingRoutes = [];
    // Per-adjacency link liveness, keyed by the authored adjacency row name (the same "identity is the name, never a
    // reorderable ordinal" rule m_regionOccupancy follows).
    private readonly Dictionary<string, LinkState> m_links = new(comparer: StringComparer.Ordinal);
    private readonly bool[] m_seatOccupied = new bool[WorldPopulationLimits.LocalSeatCount];
    // Pairwise overlap state, keyed by the ascending (a, b) body-index pair. A HashSet rather than a dense bitset —
    // the active population is small in practice and this is human/gameplay-cadence data, not a hot allocation path
    // once warmed (Add/Remove/Contains do not allocate on an already-sized set).
    private readonly HashSet<(int A, int B)> m_overlapping = [];
    // Region containment, per (regionId, bodyIndex). Keyed by region id (== the carrying placement's Id) rather
    // than a derived ordinal, so a document edit that reorders placements does not silently reinterpret stale state
    // under a reused ordinal — a region's identity is its name, exactly as GrantSubject.Region already keys it.
    private readonly Dictionary<string, bool[]> m_regionOccupancy = new(comparer: StringComparer.Ordinal);

    /// <summary>Gets this tick's collected edges, in pinned order (seats, then regions, then collisions, then links,
    /// then routes). Valid only between one <see cref="Collect"/> call and the next.</summary>
    public IReadOnlyList<WorldEventEdge> Edges => m_edges;

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
        Overlapping: [.. m_overlapping],
        RegionOccupancy: [.. m_regionOccupancy.Select(selector: static pair => (pair.Key, ((bool[])pair.Value.Clone())))],
        Links: [.. m_links.Select(selector: static pair => new WorldEventLinkState(
                Adjacency: pair.Key,
                DeliveredTick: pair.Value.DeliveredTick,
                Dropped: pair.Value.Dropped,
                PendingRefresh: pair.Value.PendingRefresh,
                StaleTicks: pair.Value.StaleTicks
            ))]
    );
    /// <summary>Restores this feed's live state from a previously captured checkpoint.</summary>
    public void Restore(WorldEventFeedCheckpoint checkpoint) {
        ArgumentNullException.ThrowIfNull(argument: checkpoint);

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
        Array.Copy(
            sourceArray: checkpoint.SeatOccupied,
            destinationArray: m_seatOccupied,
            length: Math.Min(
                val1: checkpoint.SeatOccupied.Length,
                val2: m_seatOccupied.Length
            )
        );
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
    private void CollectCollisions(WorldPopulation population) {
        for (var a = 0; (a < population.Capacity); a++) {
            if (
                !population.IsActive(index: a) ||
                (population.EntryBody(index: a) is not { } bodyA)
            ) {
                continue;
            }

            for (var b = (a + 1); (b < population.Capacity); b++) {
                if (
                    !population.IsActive(index: b) ||
                    (population.EntryBody(index: b) is not { } bodyB)
                ) {
                    continue;
                }

                var overlapping = Overlaps(
                    a: bodyA,
                    b: bodyB
                );
                var key = (a, b);
                var was = m_overlapping.Contains(item: key);

                if (overlapping == was) {
                    continue;
                }

                if (overlapping) {
                    _ = m_overlapping.Add(item: key);
                } else {
                    _ = m_overlapping.Remove(item: key);
                }

                m_edges.Add(item: new WorldEventEdge(
                    Family: (overlapping
                    ? WorldEventFamily.CollisionBegin
                    : WorldEventFamily.CollisionEnd),
                    GateA: GrantSubject.Body(index: a),
                    GateB: GrantSubject.Body(index: b),
                    A: a,
                    B: b
                ));
            }
        }
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
                        GateA: GrantSubject.All,
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
                GateA: GrantSubject.All,
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
                center = FixedVector3.FromVector3(value: placement.Position);
            }

            var radius = FixedQ4816.FromDouble(value: region.Radius);

            for (var body = 0; (body < population.Capacity); body++) {
                if (
                    !hasCenter ||
                    !population.IsActive(index: body) ||
                    (population.EntryBody(index: body) is not { } entry)
                ) {
                    if (occupancy[body]) {
                        occupancy[body] = false;
                        m_edges.Add(item: new WorldEventEdge(
                            Family: WorldEventFamily.RegionExit,
                            GateA: GrantSubject.Region(name: placement.Id),
                            GateB: null,
                            A: body,
                            B: thisOrdinal
                        ));
                    }

                    continue;
                }

                var delta = (entry.FixedPosition - center);
                var inside = (delta.Length <= radius);

                if (inside == occupancy[body]) {
                    continue;
                }

                occupancy[body] = inside;
                m_edges.Add(item: new WorldEventEdge(
                    Family: (inside
                    ? WorldEventFamily.RegionEnter
                    : WorldEventFamily.RegionExit),
                    GateA: GrantSubject.Region(name: placement.Id),
                    GateB: null,
                    A: body,
                    B: thisOrdinal
                ));
            }
        }
    }
    private void CollectSeats(WorldPopulation population) {
        for (var seat = 0; (seat < WorldPopulationLimits.LocalSeatCount); seat++) {
            var occupied = population.IsHumanOccupied(bodyIndex: seat);

            if (occupied == m_seatOccupied[seat]) {
                continue;
            }

            m_seatOccupied[seat] = occupied;
            m_edges.Add(item: new WorldEventEdge(
                Family: (occupied
                ? WorldEventFamily.SeatJoin
                : WorldEventFamily.SeatLeave),
                GateA: GrantSubject.Seat(index: seat),
                GateB: null,
                A: seat,
                B: 0L
            ));
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

        foreach (var left in aVolume.Volumes) {
            foreach (var right in bVolume.Volumes) {
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
        CollectCollisions(population: population);
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
