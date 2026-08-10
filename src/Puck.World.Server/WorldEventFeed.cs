using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>The closed world-events vocabulary — four event families; the fifth, machine-memory watches, is
/// addon-scoped — each mounted guest declares its own watch rows, so it is computed inside
/// <see cref="WorldAddonRuntime"/> directly rather than here; see that type's remarks. Mirrors
/// <see cref="Scripting.AddonAbi.ObservationVerbs"/>'s event verbs one-for-one.</summary>
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
/// Computes the four world-scoped event families once per tick — collision pairs, region enter/exit, seat
/// join/leave, and route/engagement transitions — as a flat, pinned-order edge list every mounted addon's
/// <see cref="WorldAddonRuntime"/> filters by its own grants. Deterministic by construction: every input (body
/// positions, seat occupancy, the document's region rows, route commands) is already sim-lane state, so replay
/// covers this feed by re-execution — nothing here is taped.
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
/// </remarks>
public sealed class WorldEventFeed {
    private readonly List<WorldEventEdge> m_edges = [];
    private readonly List<WorldEventEdge> m_pendingRoutes = [];
    private readonly bool[] m_seatOccupied = new bool[WorldPopulationLimits.LocalSeatCount];
    // Pairwise overlap state, keyed by the ascending (a, b) body-index pair. A HashSet rather than a dense bitset —
    // the active population is small in practice and this is human/gameplay-cadence data, not a hot allocation path
    // once warmed (Add/Remove/Contains do not allocate on an already-sized set).
    private readonly HashSet<(int A, int B)> m_overlapping = [];
    // Region containment, per (regionId, bodyIndex). Keyed by region id (== the carrying placement's Id) rather
    // than a derived ordinal, so a document edit that reorders placements does not silently reinterpret stale state
    // under a reused ordinal — a region's identity is its name, exactly as GrantSubject.Region already keys it.
    private readonly Dictionary<string, bool[]> m_regionOccupancy = new(comparer: StringComparer.Ordinal);

    /// <summary>Gets this tick's collected edges, in pinned order (seats, then regions, then collisions, then routes).
    /// Valid only between one <see cref="Collect"/> call and the next.</summary>
    public IReadOnlyList<WorldEventEdge> Edges => m_edges;

    /// <summary>Returns the live occupant count of the placement region named <paramref name="placementId"/> as of the most
    /// recent <see cref="Collect"/> — the same per-(region, body) occupancy the region pass already tracks for the
    /// addon events feed, read directly for a world rule's <c>$region:&lt;id&gt;</c> reserved channel (see
    /// <see cref="WorldRuleFacts"/>) rather than duplicated.</summary>
    /// <param name="placementId">The region-carrying placement's stable id.</param>
    /// <returns>The occupant count, or zero for a placement this tick has never seen carry a region.</returns>
    public int OccupantCount(string placementId) {
        if (!m_regionOccupancy.TryGetValue(key: placementId, value: out var occupancy)) {
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

    /// <summary>Queues a route-engaged edge after <see cref="WorldGrants"/> reports an effective route change.
    /// Drained by the next <see cref="Collect"/> call.</summary>
    /// <param name="sourceBody">The routed body's entity index.</param>
    /// <param name="target">The route target (a screen or a body).</param>
    public void QueueRouteEngaged(int sourceBody, GrantSubject target) {
        QueueRoute(family: WorldEventFamily.RouteEngaged, sourceBody: sourceBody, target: target);
    }

    /// <summary>Queues a route-disengaged edge after <see cref="WorldGrants"/> reports an effective route change.</summary>
    /// <param name="sourceBody">The routed body's entity index.</param>
    /// <param name="target">The route target that was cleared.</param>
    public void QueueRouteDisengaged(int sourceBody, GrantSubject target) {
        QueueRoute(family: WorldEventFamily.RouteDisengaged, sourceBody: sourceBody, target: target);
    }

    private void QueueRoute(WorldEventFamily family, int sourceBody, GrantSubject target) {
        // Encoding (see AddonAbi.ObservationVerbs.EventRouteEngaged's own doc): B >= 0 is a screen index; B < 0 is
        // -(bodyIndex + 1). A screen index is always >= 0 and a body index is always >= 0, so the sign alone
        // disambiguates without a third payload lane.
        var encodedTarget = ((target.Kind == GrantSubjectKind.Body) ? -(target.Value + 1) : target.Value);
        var targetGate = ((target.Kind is GrantSubjectKind.Body or GrantSubjectKind.Screen) ? (GrantSubject?)target : null);

        m_pendingRoutes.Add(item: new WorldEventEdge(
            Family: family,
            GateA: GrantSubject.Body(index: sourceBody),
            GateB: targetGate,
            A: sourceBody,
            B: encodedTarget
        ));
    }

    /// <summary>Computes this tick's edge list — the one call site, from <see cref="WorldServer.Step"/> after the
    /// population advances (so positions/occupancy reflect this tick's settled state) and before the addon runtime's
    /// read pump stages them for delivery.</summary>
    /// <param name="definition">The live world definition (its placements, for region rows).</param>
    /// <param name="population">The live population (positions, activity, seat occupancy).</param>
    public void Collect(WorldDefinition definition, WorldPopulation population) {
        m_edges.Clear();

        CollectSeats(population: population);
        CollectRegions(definition: definition, population: population);
        CollectCollisions(population: population);

        m_edges.AddRange(collection: m_pendingRoutes);
        m_pendingRoutes.Clear();
    }

    private void CollectSeats(WorldPopulation population) {
        for (var seat = 0; (seat < WorldPopulationLimits.LocalSeatCount); seat++) {
            var occupied = population.IsHumanOccupied(bodyIndex: seat);

            if (occupied == m_seatOccupied[seat]) {
                continue;
            }

            m_seatOccupied[seat] = occupied;
            m_edges.Add(item: new WorldEventEdge(
                Family: (occupied ? WorldEventFamily.SeatJoin : WorldEventFamily.SeatLeave),
                GateA: GrantSubject.Seat(index: seat),
                GateB: null,
                A: seat,
                B: 0L
            ));
        }
    }

    private void CollectRegions(WorldDefinition definition, WorldPopulation population) {
        var ordinal = 0;

        foreach (var placement in definition.Placements) {
            if ((placement is null) || (placement.Region is not { } region)) {
                continue;
            }

            var thisOrdinal = ordinal++;

            if (!m_regionOccupancy.TryGetValue(key: placement.Id, value: out var occupancy)) {
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
                hasCenter = WorldPlacementAttachment.TryResolve(attach: attach, population: population, position: out center, yawRadians: out _, reason: out _);
            } else {
                center = FixedVector3.FromVector3(value: placement.Position);
            }

            var radius = FixedQ4816.FromDouble(value: region.Radius);

            for (var body = 0; (body < population.Capacity); body++) {
                if (!hasCenter || !population.IsActive(index: body) || (population.EntryBody(index: body) is not { } entry)) {
                    if (occupancy[body]) {
                        occupancy[body] = false;
                        m_edges.Add(item: new WorldEventEdge(Family: WorldEventFamily.RegionExit, GateA: GrantSubject.Region(name: placement.Id), GateB: null, A: body, B: thisOrdinal));
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
                    Family: (inside ? WorldEventFamily.RegionEnter : WorldEventFamily.RegionExit),
                    GateA: GrantSubject.Region(name: placement.Id),
                    GateB: null,
                    A: body,
                    B: thisOrdinal
                ));
            }
        }
    }

    private void CollectCollisions(WorldPopulation population) {
        for (var a = 0; (a < population.Capacity); a++) {
            if (!population.IsActive(index: a) || (population.EntryBody(index: a) is not { } bodyA)) {
                continue;
            }

            for (var b = (a + 1); (b < population.Capacity); b++) {
                if (!population.IsActive(index: b) || (population.EntryBody(index: b) is not { } bodyB)) {
                    continue;
                }

                var overlapping = Overlaps(a: bodyA, b: bodyB);
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
                    Family: (overlapping ? WorldEventFamily.CollisionBegin : WorldEventFamily.CollisionEnd),
                    GateA: GrantSubject.Body(index: a),
                    GateB: GrantSubject.Body(index: b),
                    A: a,
                    B: b
                ));
            }
        }
    }

    private static bool Overlaps(WorldBody a, WorldBody b) {
        var aCollider = a.Collider;
        var bCollider = b.Collider;
        if ((aCollider is not { } aVolume) || (bCollider is not { } bVolume)) {
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
        if ((left.Kind != FixedBodyColliderKind.Box) && (right.Kind != FixedBodyColliderKind.Box)) {
            var (leftStart, leftEnd) = Segment(position: leftPosition, orientation: leftOrientation, volume: in left);
            var (rightStart, rightEnd) = Segment(position: rightPosition, orientation: rightOrientation, volume: in right);
            var radius = (left.Radius + right.Radius);

            return (SegmentDistanceSquared(p1: leftStart, q1: leftEnd, p2: rightStart, q2: rightEnd) < (radius * radius));
        }

        var (leftCenter, leftExtent) = Bounds(position: leftPosition, orientation: leftOrientation, volume: in left);
        var (rightCenter, rightExtent) = Bounds(position: rightPosition, orientation: rightOrientation, volume: in right);
        var delta = (leftCenter - rightCenter);

        return ((FixedQ4816.Abs(value: delta.X) < (leftExtent.X + rightExtent.X)) &&
                (FixedQ4816.Abs(value: delta.Y) < (leftExtent.Y + rightExtent.Y)) &&
                (FixedQ4816.Abs(value: delta.Z) < (leftExtent.Z + rightExtent.Z)));
    }

    private static (FixedVector3 Start, FixedVector3 End) Segment(FixedVector3 position, FixedQuaternion orientation,
        in FixedBodyColliderVolume volume) {
        var start = (position + orientation.Rotate(vector: volume.Center));
        var end = (volume.Kind == FixedBodyColliderKind.Capsule)
            ? (position + orientation.Rotate(vector: volume.Endpoint))
            : start;

        return (Start: start, End: end);
    }

    private static FixedQ4816 SegmentDistanceSquared(FixedVector3 p1, FixedVector3 q1, FixedVector3 p2, FixedVector3 q2) {
        var d1 = (q1 - p1);
        var d2 = (q2 - p2);
        var r = (p1 - p2);
        var a = FixedVector3.Dot(left: d1, right: d1);
        var e = FixedVector3.Dot(left: d2, right: d2);
        var f = FixedVector3.Dot(left: d2, right: r);
        FixedQ4816 s;
        FixedQ4816 t;

        if ((a <= FixedQ4816.Zero) && (e <= FixedQ4816.Zero)) {
            return FixedVector3.Dot(left: r, right: r);
        }

        if (a <= FixedQ4816.Zero) {
            s = FixedQ4816.Zero;
            t = FixedQ4816.Clamp(value: (f / e), minimum: FixedQ4816.Zero, maximum: FixedQ4816.One);
        } else {
            var c = FixedVector3.Dot(left: d1, right: r);
            if (e <= FixedQ4816.Zero) {
                t = FixedQ4816.Zero;
                s = FixedQ4816.Clamp(value: (-c / a), minimum: FixedQ4816.Zero, maximum: FixedQ4816.One);
            } else {
                var b = FixedVector3.Dot(left: d1, right: d2);
                var denominator = ((a * e) - (b * b));
                s = (denominator > FixedQ4816.Zero)
                    ? FixedQ4816.Clamp(value: (((b * f) - (c * e)) / denominator), minimum: FixedQ4816.Zero, maximum: FixedQ4816.One)
                    : FixedQ4816.Zero;
                t = (((b * s) + f) / e);
                if (t < FixedQ4816.Zero) {
                    t = FixedQ4816.Zero;
                    s = FixedQ4816.Clamp(value: (-c / a), minimum: FixedQ4816.Zero, maximum: FixedQ4816.One);
                } else if (t > FixedQ4816.One) {
                    t = FixedQ4816.One;
                    s = FixedQ4816.Clamp(value: ((b - c) / a), minimum: FixedQ4816.Zero, maximum: FixedQ4816.One);
                }
            }
        }

        var closest = ((p1 + (d1 * s)) - (p2 + (d2 * t)));
        return FixedVector3.Dot(left: closest, right: closest);
    }

    private static (FixedVector3 Center, FixedVector3 Extent) Bounds(FixedVector3 position, FixedQuaternion orientation,
        in FixedBodyColliderVolume volume) {
        if (volume.Kind == FixedBodyColliderKind.Sphere) {
            var radius = new FixedVector3(X: volume.Radius, Y: volume.Radius, Z: volume.Radius);
            return (Center: (position + orientation.Rotate(vector: volume.Center)), Extent: radius);
        }

        if (volume.Kind == FixedBodyColliderKind.Capsule) {
            var (start, end) = Segment(position: position, orientation: orientation, volume: in volume);
            var delta = (end - start);
            var radius = new FixedVector3(X: volume.Radius, Y: volume.Radius, Z: volume.Radius);
            var extent = new FixedVector3(
                X: (FixedQ4816.Abs(value: delta.X) / FixedQ4816.FromInteger(value: 2L)),
                Y: (FixedQ4816.Abs(value: delta.Y) / FixedQ4816.FromInteger(value: 2L)),
                Z: (FixedQ4816.Abs(value: delta.Z) / FixedQ4816.FromInteger(value: 2L))
            );
            return (Center: ((start + end) / FixedQ4816.FromInteger(value: 2L)), Extent: (extent + radius));
        }

        var center = (position + orientation.Rotate(vector: volume.Center));
        var rotation = (orientation * volume.Rotation).Normalize();
        var unitX = new FixedVector3(X: FixedQ4816.One, Y: FixedQ4816.Zero, Z: FixedQ4816.Zero);
        var unitY = new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.One, Z: FixedQ4816.Zero);
        var unitZ = new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.Zero, Z: FixedQ4816.One);
        var axisX = rotation.Rotate(vector: unitX);
        var axisY = rotation.Rotate(vector: unitY);
        var axisZ = rotation.Rotate(vector: unitZ);
        var boxExtent = new FixedVector3(
            X: ((FixedQ4816.Abs(value: axisX.X) * volume.HalfExtents.X) + (FixedQ4816.Abs(value: axisY.X) * volume.HalfExtents.Y) + (FixedQ4816.Abs(value: axisZ.X) * volume.HalfExtents.Z)),
            Y: ((FixedQ4816.Abs(value: axisX.Y) * volume.HalfExtents.X) + (FixedQ4816.Abs(value: axisY.Y) * volume.HalfExtents.Y) + (FixedQ4816.Abs(value: axisZ.Y) * volume.HalfExtents.Z)),
            Z: ((FixedQ4816.Abs(value: axisX.Z) * volume.HalfExtents.X) + (FixedQ4816.Abs(value: axisY.Z) * volume.HalfExtents.Y) + (FixedQ4816.Abs(value: axisZ.Z) * volume.HalfExtents.Z))
        );

        return (Center: center, Extent: boxExtent);
    }
}
