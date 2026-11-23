using Puck.Maths;

namespace Puck.Physics.Navigation;

/// <summary>The work one domain bake did, counted from the queries it asked of its geometry.</summary>
/// <param name="Cells">The cells the bake sampled.</param>
/// <param name="Walkable">The cells it proved walkable.</param>
/// <param name="Grounds">The ground searches it asked (one support-distance query each).</param>
/// <param name="Occupancies">The occupancy proofs it asked (one resolve each).</param>
/// <param name="EdgeProofs">The edge proofs it asked (one swept resolve each).</param>
/// <param name="Edges">The edges it proved clear, each counted once.</param>
/// <param name="AllocatedBytes">The managed bytes the bake allocated, its workspace included.</param>
public readonly record struct NavigationBakeWork(long Cells, long Walkable, long Grounds, long Occupancies, long EdgeProofs, long Edges, long AllocatedBytes) {
    /// <summary>Returns the sum of two bakes' work.</summary>
    /// <param name="left">The first bake's work.</param>
    /// <param name="right">The second bake's work.</param>
    /// <returns>The field-by-field sum.</returns>
    public static NavigationBakeWork operator +(NavigationBakeWork left, NavigationBakeWork right) => new(
        AllocatedBytes: (left.AllocatedBytes + right.AllocatedBytes),
        Cells: (left.Cells + right.Cells),
        EdgeProofs: (left.EdgeProofs + right.EdgeProofs),
        Edges: (left.Edges + right.Edges),
        Grounds: (left.Grounds + right.Grounds),
        Occupancies: (left.Occupancies + right.Occupancies),
        Walkable: (left.Walkable + right.Walkable)
    );
}

/// <summary>One domain's occupancy and static edges, proved against the contact geometry its bodies collide with: a
/// derived product identified by the domain's tuning and the part of the geometry it read
/// (<see cref="IRegionalContactField.Within"/>), never changed once built and never kept as truth.</summary>
public sealed class NavigationDomainBake {
    internal NavigationDomainBake(bool[] walkable, FixedQ4816[] ground, uint[] edges, NavigationBakeWork work) {
        Walkable = walkable;
        Ground = ground;
        Edges = edges;
        Work = work;
    }

    internal bool[] Walkable { get; }
    internal FixedQ4816[] Ground { get; }
    internal uint[] Edges { get; }
    /// <summary>Gets the work the bake did.</summary>
    public NavigationBakeWork Work { get; }
}

public sealed partial class NavigationRuntime {
    public sealed partial class Domain {
        // The ground search's probe: a sphere this small stands in for a ray, so a cell's ground is the surface under
        // its center rather than wherever the agent's full radius would first touch. It hangs below the probe's start,
        // its top at the start, so geometry just over the start (an author's next flight) does not touch it.
        private static readonly FixedQ4816 GroundProbeRadius = (FixedQ4816.One / FixedQ4816.FromInteger(value: 64));

        // The volumes every proof stands the agent in, compiled once from the tuning. A surface agent proves the capsule
        // of its height and radius with its underside raised by the step height (Lift): the step rule admits anything
        // lower, so a tread's next riser beside a cell does not refuse it. A volume or medium agent is a sphere.
        private readonly FixedBodyColliderVolume[] m_agent;
        private readonly FixedBodyColliderVolume[] m_groundProbe;
        private readonly FixedQ4816 m_lift;

        private static FixedBodyColliderVolume[] AgentVolumes(in NavigationDomainInput tuning, out FixedQ4816 lift) {
            var radius = tuning.AgentRadius;

            if (tuning.Kind != NavigationKind.Surface) {
                lift = FixedQ4816.Zero;
                return [FixedBodyColliderVolume.Sphere(center: FixedVector3.Zero, radius: radius)];
            }
            var core = FixedQ4816.Max(x: FixedQ4816.Zero, y: (tuning.AgentHeight - (radius * FixedQ4816.FromInteger(value: 2))));

            lift = FixedQ4816.Min(x: core, y: FixedQ4816.Max(x: FixedQ4816.Zero, y: tuning.MaxStepHeight));
            var bottom = new FixedVector3(X: FixedQ4816.Zero, Y: radius, Z: FixedQ4816.Zero);
            var top = bottom with { Y = (radius + (core - lift)) };

            return [(top == bottom)
                ? FixedBodyColliderVolume.Sphere(center: bottom, radius: radius)
                : FixedBodyColliderVolume.Capsule(lower: bottom, upper: top, radius: radius)];
        }

        // The box every query a bake or a locomotion check asks stays inside: the footprint grown by the agent and a
        // metre, from below the ground search's reach to over the agent standing on its highest ground.
        private static FixedAabb Region(in NavigationDomainInput tuning, in FixedQuaternion rotation) {
            var last = new FixedVector3(
                X: (tuning.CellSize * FixedQ4816.FromInteger(value: (tuning.Width - 1))),
                Y: FixedQ4816.Zero,
                Z: (tuning.CellSize * FixedQ4816.FromInteger(value: (tuning.Depth - 1)))
            );
            var box = FixedAabb.Empty;

            foreach (var corner in (ReadOnlySpan<FixedVector3>)[FixedVector3.Zero, last with { Z = FixedQ4816.Zero }, last with { X = FixedQ4816.Zero }, last]) {
                box = box.Enclose(point: (tuning.Origin + ((tuning.YawRadians == FixedQ4816.Zero) ? corner : rotation.Rotate(vector: corner))));
            }
            var margin = ((tuning.AgentRadius + tuning.CellSize) + FixedQ4816.One);
            var below = ((tuning.Kind == NavigationKind.Surface) ? tuning.ProbeDown : FixedQ4816.Zero);
            var above = ((tuning.Kind == NavigationKind.Surface)
                ? (tuning.ProbeUp + tuning.AgentHeight)
                : (tuning.CellSize * FixedQ4816.FromInteger(value: (tuning.Layers - 1))));

            return new FixedAabb(
                Maximum: new FixedVector3(X: (box.Maximum.X + margin), Y: ((box.Maximum.Y + above) + margin), Z: (box.Maximum.Z + margin)),
                Minimum: new FixedVector3(X: (box.Minimum.X - margin), Y: ((box.Minimum.Y - below) - margin), Z: (box.Minimum.Z - margin))
            );
        }

        // The occupancy sweep and the static edges, against one geometry part. Reads only the domain's immutable tuning
        // and the part, so it runs on any thread; the owner adopts the result.
        private NavigationDomainBake Bake(IContactField geometry, CancellationToken cancellation) {
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var count = CellCount;
            var walkable = new bool[count];
            var ground = new FixedQ4816[count];
            var edges = new uint[count];
            var work = new BakeCounts();

            for (var node = 0; (node < count); node++) {
                if ((node & 1023) == 0) { cancellation.ThrowIfCancellationRequested(); }
                if (TrySampleCell(geometry: geometry, node: node, routeY: out var routeY, work: ref work)) {
                    ground[node] = routeY;
                    walkable[node] = true;
                    work.Walkable++;
                }
            }
            for (var current = 0; (current < count); current++) {
                if (!walkable[current]) {
                    continue;
                }
                if ((current & 1023) == 0) { cancellation.ThrowIfCancellationRequested(); }
                foreach (var step in Neighbors(node: current)) {
                    if (step.Node > current) {
                        ProveEdge(current: current, edges: edges, geometry: geometry, ground: ground, step: in step, walkable: walkable, work: ref work);
                    }
                }
            }
            return new NavigationDomainBake(
                edges: edges,
                ground: ground,
                walkable: walkable,
                work: new NavigationBakeWork(
                    AllocatedBytes: (GC.GetAllocatedBytesForCurrentThread() - allocated),
                    Cells: count,
                    EdgeProofs: work.EdgeProofs,
                    Edges: work.Edges,
                    Grounds: work.Grounds,
                    Occupancies: work.Occupancies,
                    Walkable: work.Walkable
                )
            );
        }

        /// <summary>Starts a partial bake on the thread pool when a predecessor's immutable bake and a bounded
        /// geometry difference are available. Only the cells and edges the change can reach are proved again.</summary>
        /// <param name="previous">The domain this one replaces.</param>
        /// <returns><see langword="true"/> when a partial bake was started.</returns>
        internal bool TryBeginRebakeFrom(Domain previous) {
            var geometry = m_geometry;

            if (
                !previous.SameBake(row: Tuning) ||
                (Tuning.Kind == NavigationKind.Medium) ||
                (geometry is not IRegionalContactField part)
            ) {
                return false;
            }
            _ = previous.IsBaked;
            var basis = ((previous.m_bake is { } baked) ? (Bake: baked, Geometry: previous.m_geometry) : previous.m_rebakeBasis);

            if ((basis is not { } source) || !part.TryDifference(changed: out var changed, other: source.Geometry) || changed.IsEverywhere) {
                return false;
            }

            m_rebakeBasis = source;
            StartBake(bake: cancellation => Rebake(baked: source.Bake, cancellation: cancellation, changed: changed, geometry: geometry));
            return true;
        }

        private NavigationDomainBake Rebake(NavigationDomainBake baked, IContactField geometry, FixedAabb changed, CancellationToken cancellation) {
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var walkable = (bool[])baked.Walkable.Clone();
            var ground = (FixedQ4816[])baked.Ground.Clone();
            var edges = (uint[])baked.Edges.Clone();
            var work = new BakeCounts();

            if (!changed.IsEmpty) {
                // A cell is re-proved where its own sample or an edge sweep from it could meet a changed collider: its
                // center within the agent's radius and two diagonal grid steps (up to three cells along a world axis)
                // of the change across, inside the band its probe and
                // agent span vertically. Its neighbours' edges are re-proved with it, since a diagonal edge is admitted
                // through the walkability of the cells beside it.
                var across = (Tuning.AgentRadius + (Tuning.CellSize * FixedQ4816.FromInteger(value: 3)));
                var reach = new FixedAabb(
                    Maximum: new FixedVector3(X: (changed.Maximum.X + across), Y: (changed.Maximum.Y + Tuning.ProbeDown + across), Z: (changed.Maximum.Z + across)),
                    Minimum: new FixedVector3(X: (changed.Minimum.X - across), Y: (changed.Minimum.Y - (Tuning.ProbeUp + Tuning.AgentHeight + across)), Z: (changed.Minimum.Z - across))
                );
                var sampled = new bool[CellCount];
                var touched = new bool[CellCount];

                for (var node = 0; (node < CellCount); node++) {
                    if ((node & 1023) == 0) { cancellation.ThrowIfCancellationRequested(); }
                    Coordinates(node: node, x: out var x, y: out var y, z: out var z);

                    var center = GridPosition(x: x, y: y, z: z);

                    if ((center.X < reach.Minimum.X) || (center.X > reach.Maximum.X) || (center.Z < reach.Minimum.Z) || (center.Z > reach.Maximum.Z) ||
                        (center.Y < reach.Minimum.Y) || (center.Y > reach.Maximum.Y)) {
                        continue;
                    }

                    sampled[node] = true;
                    walkable[node] = false;
                    ground[node] = FixedQ4816.Zero;
                    if (TrySampleCell(geometry: geometry, node: node, routeY: out var routeY, work: ref work)) {
                        ground[node] = routeY;
                        walkable[node] = true;
                    }
                }
                for (var node = 0; (node < CellCount); node++) {
                    if (!sampled[node]) {
                        continue;
                    }

                    touched[node] = true;
                    foreach (var step in Neighbors(node: node)) {
                        touched[step.Node] = true;
                    }
                }
                // Use the same direction as a whole bake, including when only the higher ordinal was touched.
                for (var current = 0; (current < CellCount); current++) {
                    if ((current & 1023) == 0) { cancellation.ThrowIfCancellationRequested(); }
                    if (!touched[current]) { continue; }

                    foreach (var step in Neighbors(node: current)) {
                        var next = step.Node;

                        if (next > current) {
                            ProveEdge(current: current, edges: edges, geometry: geometry, ground: ground, step: in step, walkable: walkable, work: ref work);
                        } else if (!touched[next]) {
                            var reversed = new Step(Node: current, X: (step.X + step.Dx), Y: (step.Y + step.Dy), Z: (step.Z + step.Dz),
                                Dx: -step.Dx, Dy: -step.Dy, Dz: -step.Dz, Axes: step.Axes);

                            ProveEdge(current: next, edges: edges, geometry: geometry, ground: ground, step: in reversed, walkable: walkable, work: ref work);
                        }
                    }
                }
            }

            work.Walkable = walkable.LongCount(predicate: static cell => cell);
            return new NavigationDomainBake(
                edges: edges,
                ground: ground,
                walkable: walkable,
                work: new NavigationBakeWork(
                    AllocatedBytes: (GC.GetAllocatedBytesForCurrentThread() - allocated),
                    Cells: CellCount,
                    EdgeProofs: work.EdgeProofs,
                    Edges: work.Edges,
                    Grounds: work.Grounds,
                    Occupancies: work.Occupancies,
                    Walkable: work.Walkable
                )
            );
        }

        private struct BakeCounts {
            public long Walkable;
            public long Grounds;
            public long Occupancies;
            public long EdgeProofs;
            public long Edges;
        }

        // Both full and partial bakes record the result at both endpoints of the same canonical sweep.
        private void ProveEdge(int current, in Step step, IContactField geometry, bool[] walkable, FixedQ4816[] ground, uint[] edges, ref BakeCounts work) {
            var forward = NeighborBit(dx: step.Dx, dy: step.Dy, dz: step.Dz);
            var backward = NeighborBit(dx: -step.Dx, dy: -step.Dy, dz: -step.Dz);

            edges[current] &= ~forward;
            edges[step.Node] &= ~backward;
            if (walkable[current] && CanTraverseStatic(current: current, dx: step.Dx, dy: step.Dy, dz: step.Dz, geometry: geometry, ground: ground,
                next: step.Node, walkable: walkable, work: ref work, x: step.X, y: step.Y, z: step.Z)) {
                edges[current] |= forward;
                edges[step.Node] |= backward;
                work.Edges++;
            }
        }

        // A surface cell's ground is where the ground probe, lowered from probeUp over the domain's origin height, first
        // comes to rest; the cell is walkable where the agent standing there, its underside raised by the step height,
        // meets nothing. A volume or medium cell is walkable where the agent's sphere at its center meets nothing.
        private bool TrySampleCell(IContactField geometry, int node, out FixedQ4816 routeY, ref BakeCounts work) {
            Coordinates(node: node, x: out var x, y: out var y, z: out var z);
            var center = GridPosition(x: x, y: y, z: z);

            routeY = center.Y;
            if (Tuning.Kind != NavigationKind.Surface) {
                work.Occupancies++;
                return FixedContactProbe.IsClear(field: geometry, start: center, end: center, orientation: FixedQuaternion.Identity, up: FixedVector3.UnitY, volumes: m_agent);
            }
            var top = center with { Y = (center.Y + Tuning.ProbeUp) };

            work.Grounds++;
            // A probe that starts touching proves no ground under it: it stands inside or against geometry.
            if (
                !geometry.TrySupportDistance(descent: out var descent, orientation: FixedQuaternion.Identity, position: in top,
                    reach: (Tuning.ProbeUp + Tuning.ProbeDown), up: FixedVector3.UnitY, volumes: m_groundProbe) ||
                (descent <= FixedQ4816.Zero)
            ) {
                return false;
            }
            var ground = ((top.Y - descent) - (GroundProbeRadius * FixedQ4816.FromInteger(value: 2)));

            // Route positions are the agent's lower clearance-sphere center, never the floor itself.
            routeY = (ground + Tuning.AgentRadius);
            work.Occupancies++;
            var lifted = center with { Y = (ground + m_lift) };

            return FixedContactProbe.IsClear(field: geometry, start: lifted, end: lifted, orientation: FixedQuaternion.Identity, up: FixedVector3.UnitY, volumes: m_agent);
        }
        private bool CanTraverseStatic(IContactField geometry, bool[] walkable, FixedQ4816[] ground, int current, int next, int x, int y, int z, int dx, int dy, int dz, ref BakeCounts work) {
            if (!walkable[next]) {
                return false;
            }
            if (Tuning.Kind == NavigationKind.Surface) {
                var rise = FixedQ4816.Abs(value: (ground[next] - ground[current]));
                var maximumSlopeRise = (((dx != 0) && (dz != 0))
                    ? (Tuning.MaximumSlopeRise * SquareRootTwo)
                    : Tuning.MaximumSlopeRise
                );

                if (
                    (rise > Tuning.MaxStepHeight) ||
                    (rise > maximumSlopeRise)
                ) {
                    return false;
                }
            }
            if (StepAxes(dx: dx, dy: dy, dz: dz) > 1) {
                if ((dx != 0) && !walkable[Index(x: (x + dx), y: y, z: z)]) { return false; }
                if ((dy != 0) && !walkable[Index(x: x, y: (y + dy), z: z)]) { return false; }
                if ((dz != 0) && !walkable[Index(x: x, y: y, z: (z + dz))]) { return false; }
            }
            work.EdgeProofs++;
            return FixedContactProbe.IsClear(field: geometry, start: EdgeFoot(ground: ground, node: current, other: next),
                end: EdgeFoot(ground: ground, node: next, other: current), orientation: FixedQuaternion.Identity, up: FixedVector3.UnitY, volumes: m_agent);
        }
        // Where an edge's sweep stands the agent at one end. A surface agent crosses at the higher end's ground, raised
        // by the step height, so the whole edge proves the volume a body standing on either end needs over the step it
        // may climb; a volume or medium agent crosses between the cell centers.
        private FixedVector3 EdgeFoot(FixedQ4816[] ground, int node, int other) {
            Coordinates(node: node, x: out var x, y: out var y, z: out var z);
            var center = GridPosition(x: x, y: y, z: z);

            return ((Tuning.Kind == NavigationKind.Surface)
                ? center with { Y = ((FixedQ4816.Max(x: ground[node], y: ground[other]) - Tuning.AgentRadius) + m_lift) }
                : center);
        }
    }
}
