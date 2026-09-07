using Puck.Maths;

namespace Puck.Physics.Navigation;

/// <summary>Result of one bounded deterministic route search.</summary>
public enum NavigationStatus : byte {
    None,
    Active,
    Arrived,
    NoTarget,
    OutsideDomain,
    Unreachable,
    SearchLimit,
    PathLimit,
    Pending,
    CapacityLimited,
}

/// <summary>A deterministic, bounded A*/shared-search kernel over a set of navigation domains compiled from a
/// host's own solid-field query and optional live-medium field. Reads no document; every domain's tuning arrives
/// pre-compiled as a <see cref="NavigationDomainInput"/>.</summary>
public sealed partial class NavigationRuntime {
    private readonly Domain[] m_domains;
    private readonly bool[] m_retained;

    /// <summary>Compiles a runtime over the given domains.</summary>
    /// <param name="domains">The compiled domain tunings, in stable authored order.</param>
    /// <param name="query">The deterministic solid-field query provider; required when <paramref name="domains"/>
    /// is non-empty.</param>
    /// <param name="fields">The live-medium field a medium-kind domain reads through; optional when no domain is
    /// medium-kind.</param>
    /// <param name="capacity">Representation ceilings the host's own authoring vocabulary declares.</param>
    /// <param name="previous">The prior runtime, when a host is replacing its document-derived query provider.
    /// A domain is reused only after it proves that every compile-time occupancy, ground, clearance, and static-edge
    /// query has the same result against the candidate provider; the reused domain then forwards all future queries
    /// to <paramref name="query"/>.</param>
    public NavigationRuntime(IReadOnlyList<NavigationDomainInput> domains, IWorldQuery? query, INavigationMediumField? fields, NavigationCapacity capacity, NavigationRuntime? previous = null) {
        if (domains.Count != 0 && query is null) {
            throw new InvalidOperationException(message: "navigation domains require the deterministic solid-field query provider.");
        }

        m_domains = new Domain[domains.Count];
        m_retained = new bool[domains.Count];
        var retained = 0;
        for (var index = 0; index < domains.Count; index++) {
            Domain? reused = null;
            if (previous is not null) {
                for (var oldIndex = 0; oldIndex < previous.m_domains.Length; oldIndex++) {
                    var candidate = previous.m_domains[oldIndex];
                    if (candidate.Name == domains[index].Name && candidate.TryRebind(row: domains[index], query: query!, fields: fields, capacity: capacity)) {
                        reused = candidate;
                        break;
                    }
                }
            }
            m_domains[index] = reused ?? new Domain(row: domains[index], query: query!, fields: fields, capacity: capacity);
            if (reused is not null) { m_retained[index] = true; retained++; }
        }
        RetainedDomainCount = retained;
        RebuiltDomainCount = domains.Count - retained;
    }

    public int Count => m_domains.Length;
    /// <summary>Gets the number of domain workspaces retained from the prior runtime installation.</summary>
    public int RetainedDomainCount { get; }
    /// <summary>Gets the number of domain workspaces compiled for this runtime installation.</summary>
    public int RebuiltDomainCount { get; }
    public long CellCount => m_domains.Sum(selector: static domain => (long)domain.CellCount);
    public long WalkableCellCount => m_domains.Sum(selector: static domain => (long)domain.WalkableCellCount);
    public long WorkspaceBytes => m_domains.Sum(selector: static domain => domain.WorkspaceBytes);
    public Domain this[int index] => m_domains[index];
    /// <summary>Gets whether the domain at <paramref name="index"/> retained its compiled workspace from the prior install.</summary>
    public bool WasRetained(int index) => m_retained[index];

    public sealed partial class Domain {
        private const int StraightCost = 1_000;
        private const int DiagonalCost = 1_414;
        private const int SpaceDiagonalCost = 1_732;
        // The SDF provider accepts a cast within 0.001 units of contact. Keep the route's clearance centers one
        // representable margin beyond that band so a sphere proven non-overlapping is not immediately reported as
        // a conservative time-zero sweep hit by the same provider.
        private static readonly FixedQ4816 ClearanceEpsilon = FixedQ4816.FromDouble(value: 0.002);
        private static readonly FixedQ4816 SquareRootTwo = FixedQ4816.FromDouble(value: 1.4142135623730951);

        private readonly int[] m_closedStamp;
        private readonly int[] m_cost;
        private readonly uint[] m_edges;
        private readonly FixedQ4816[] m_ground;
        private readonly NodeHeap m_open;
        private readonly int[] m_openStamp;
        private readonly int[] m_parent;
        private int m_searchStamp;
        private readonly bool[] m_walkable;

        private INavigationMediumField? m_fields;
        private readonly int m_mediumField;
        private IWorldQuery m_query;
        private NavigationCapacity m_capacity;
        private readonly FixedQuaternion m_rotation;
        private readonly FixedQuaternion m_inverseRotation;

        public Domain(NavigationDomainInput row, IWorldQuery query, INavigationMediumField? fields, NavigationCapacity capacity) {
            Name = row.Name;
            Tuning = row;
            var up = new FixedVector3(FixedQ4816.Zero, FixedQ4816.One, FixedQ4816.Zero);
            m_rotation = FixedQuaternion.FromAxisAngle(up, row.YawRadians);
            m_inverseRotation = FixedQuaternion.FromAxisAngle(up, -row.YawRadians);
            m_fields = fields;
            m_query = query;
            m_capacity = capacity;
            m_mediumField = ((Tuning.Kind == NavigationKind.Medium) && (fields is not null) && fields.TryFieldIndex(name: Tuning.Medium!, field: out var mediumField)
                ? mediumField
                : -1
            );
            var count = checked(Tuning.Width * Tuning.Depth * Tuning.Layers);
            m_ground = new FixedQ4816[count];
            m_walkable = new bool[count];
            m_edges = new uint[count];
            m_cost = new int[count];
            m_parent = new int[count];
            m_openStamp = new int[count];
            m_closedStamp = new int[count];
            m_open = new NodeHeap(cells: count);

            for (var node = 0; node < count; node++) {
                if (TrySampleCell(query, node, out var ground)) {
                    m_ground[node] = ground;
                    m_walkable[node] = true;
                    WalkableCellCount++;
                }
            }
            BuildEdges();
            Sharing = row.Shared;
            InitializeSharing();
        }

        // A retained workspace is safe only when its fixed occupancy and edge bake means the same thing under the
        // replacement provider. Dynamic off-grid locomotion is deliberately forwarded to the replacement provider;
        // retaining a workspace never retains a stale geometry query. Medium revisions are part of the proof because
        // IsWalkable/edge admission consults the live field in addition to the solid provider.
        internal bool TryRebind(NavigationDomainInput row, IWorldQuery query, INavigationMediumField? fields, NavigationCapacity capacity) {
            if (row != Tuning || capacity != m_capacity || (Tuning.Kind == NavigationKind.Medium && !SameMediumRevision(fields))) {
                return false;
            }
            if (!ReferenceEquals(m_query, query) && !HasSameStaticGeometry(query)) {
                return false;
            }
            m_query = query;
            m_fields = fields;
            m_capacity = capacity;
            return true;
        }

        private bool SameMediumRevision(INavigationMediumField? fields) {
            if (Tuning.Kind != NavigationKind.Medium || m_fields is null || fields is null || m_mediumField < 0 ||
                !fields.TryFieldIndex(Tuning.Medium!, out var field) || field != m_mediumField) {
                return false;
            }
            // Revision numbers belong to one provider; matching numbers from independent lattices prove nothing.
            return ReferenceEquals(m_fields, fields) && m_sharedFieldRevision == fields.ValueRevision(field);
        }

        // Both initial baking and retention proof sample exactly the same clearance geometry.
        private bool TrySampleCell(IWorldQuery query, int node, out FixedQ4816 routeY) {
            Coordinates(node, out var x, out var y, out var z);
            var probe = GridPosition(x, y, z);
            routeY = probe.Y;
            if (Tuning.Kind != NavigationKind.Surface) {
                return !query.Overlap(FixedPosition.FromLocal(probe), Tuning.AgentRadius);
            }
            if (!query.TryGroundHeight(FixedPosition.FromLocal(probe), Tuning.ProbeUp, Tuning.ProbeDown, out var ground)) {
                return false;
            }
            // Routes use the lower clearance-sphere center, never the floor surface itself.
            routeY = ground + Tuning.AgentRadius + ClearanceEpsilon;
            var foot = new FixedVector3(probe.X, routeY, probe.Z);
            var headY = ground + Tuning.AgentHeight - Tuning.AgentRadius - ClearanceEpsilon;
            if (query.Overlap(FixedPosition.FromLocal(foot), Tuning.AgentRadius)) { return false; }
            if (headY <= routeY) { return true; }
            var head = new FixedVector3(probe.X, headY, probe.Z);
            var core = head - foot;
            return !query.Overlap(FixedPosition.FromLocal(head), Tuning.AgentRadius) &&
                !query.SphereCast(FixedPosition.FromLocal(foot), core, Tuning.AgentRadius, core.Length, out _);
        }

        private bool HasSameStaticGeometry(IWorldQuery query) {
            for (var node = 0; node < CellCount; node++) {
                var walkable = TrySampleCell(query, node, out var ground);
                if (walkable != m_walkable[node] || (walkable && ground != m_ground[node])) { return false; }
            }
            for (var current = 0; current < CellCount; current++) {
                Coordinates(current, out var x, out var y, out var z);
                var minY = (Tuning.Kind == NavigationKind.Surface ? 0 : -1);
                var maxY = (Tuning.Kind == NavigationKind.Surface ? 0 : 1);
                for (var dy = minY; dy <= maxY; dy++) {
                    for (var dz = -1; dz <= 1; dz++) {
                        for (var dx = -1; dx <= 1; dx++) {
                            var axes = (dx == 0 ? 0 : 1) + (dy == 0 ? 0 : 1) + (dz == 0 ? 0 : 1);
                            var nx = x + dx; var ny = y + dy; var nz = z + dz;
                            if (axes == 0 || !AdmitsAxes(axes) || (uint)nx >= (uint)Tuning.Width ||
                                (uint)ny >= (uint)Tuning.Layers || (uint)nz >= (uint)Tuning.Depth) { continue; }
                            var next = Index(nx, ny, nz);
                            if (next <= current) { continue; }
                            var candidate = m_walkable[current] && CanTraverseStatic(query, current, next, x, y, z, dx, dy, dz);
                            var currentBit = (m_edges[current] & NeighborBit(dx, dy, dz)) != 0U;
                            var reverseBit = (m_edges[next] & NeighborBit(-dx, -dy, -dz)) != 0U;
                            if (candidate != currentBit || candidate != reverseBit) { return false; }
                        }
                    }
                }
            }
            return true;
        }

        public int CellCount => m_walkable.Length;
        public string Name { get; }
        public NavigationDomainInput Tuning { get; }
        public int WalkableCellCount { get; }
        public long WorkspaceBytes => checked((long)CellCount * ((6L * sizeof(int)) + sizeof(long) + sizeof(uint) + sizeof(byte)) + SharedWorkspaceBytes);

        // Actual off-center locomotion needs a continuous proof: a cached grid edge certifies only the line
        // between its cell centers. Surface locomotion has a different support/clearance contract.
        public bool AdmitsLocomotion(in FixedVector3 from, in FixedVector3 to) {
            if (Tuning.Kind == NavigationKind.Surface || !TryCell(from, out _) || !TryCell(to, out _)) { return false; }
            if (Tuning.Kind == NavigationKind.Medium && (m_fields is null || !m_fields.IsSegmentInsideMedium(
                m_mediumField, from, to, Tuning.AgentRadius, m_capacity.MaxMediumSegmentSubdivisions))) { return false; }
            if (m_query.Overlap(FixedPosition.FromLocal(from), Tuning.AgentRadius) ||
                m_query.Overlap(FixedPosition.FromLocal(to), Tuning.AgentRadius)) { return false; }
            var delta = to - from;
            return delta == FixedVector3.Zero || !m_query.SphereCast(FixedPosition.FromLocal(from), delta,
                Tuning.AgentRadius, delta.Length, out _);
        }

        public FixedVector3 Position(int node) {
            Coordinates(node: node, x: out var x, y: out var y, z: out var z);
            var position = GridPosition(x, y, z);
            return Tuning.Kind == NavigationKind.Surface ? new FixedVector3(position.X, m_ground[node], position.Z) : position;
        }

        private FixedVector3 GridPosition(int x, int y, int z) {
            var local = new FixedVector3(Tuning.CellSize * FixedQ4816.FromInteger(x),
                Tuning.CellSize * FixedQ4816.FromInteger(y), Tuning.CellSize * FixedQ4816.FromInteger(z));
            return Tuning.Origin + (Tuning.YawRadians == FixedQ4816.Zero ? local : m_rotation.Rotate(local));
        }

        public bool TryCell(in FixedVector3 position, out int node) {
            var local = Tuning.YawRadians == FixedQ4816.Zero ? position : Tuning.Origin + m_inverseRotation.Rotate(position - Tuning.Origin);
            var x = RoundedCell(value: (Int128)local.X.Value - Tuning.Origin.X.Value, cellSize: Tuning.CellSize.Value);
            var z = RoundedCell(value: (Int128)local.Z.Value - Tuning.Origin.Z.Value, cellSize: Tuning.CellSize.Value);
            var y = (Tuning.Kind == NavigationKind.Surface ? 0 : RoundedCell(value: (Int128)local.Y.Value - Tuning.Origin.Y.Value, cellSize: Tuning.CellSize.Value));
            if (x < 0 || x >= Tuning.Width || y < 0 || y >= Tuning.Layers || z < 0 || z >= Tuning.Depth) {
                node = -1;
                return false;
            }
            node = Index(x: (int)x, y: (int)y, z: (int)z);
            return IsWalkable(node: node);
        }

        public NavigationStatus FindPath(int start, int goal, Span<int> path, out int pathLength, out int expanded) {
            pathLength = 0;
            expanded = 0;
            if (!IsWalkable(start) || !IsWalkable(goal)) {
                return NavigationStatus.OutsideDomain;
            }
            if (start == goal) {
                path[0] = start;
                pathLength = 1;
                return NavigationStatus.Arrived;
            }

            BeginSearch();
            Open(node: start, cost: 0, parent: -1, goal: goal);
            while (m_open.Count != 0) {
                var current = m_open.Pop(order: new GoalOrder(domain: this, goal: goal));
                m_closedStamp[current] = m_searchStamp;
                expanded++;
                if (current == goal) {
                    return Reconstruct(goal: goal, path: path, pathLength: out pathLength);
                }
                if (expanded >= Tuning.MaxExpandedNodes) {
                    return NavigationStatus.SearchLimit;
                }

                Coordinates(node: current, x: out var cx, y: out var cy, z: out var cz);
                var minY = (Tuning.Kind == NavigationKind.Surface ? 0 : -1);
                var maxY = (Tuning.Kind == NavigationKind.Surface ? 0 : 1);
                for (var dy = minY; dy <= maxY; dy++) {
                    for (var dz = -1; dz <= 1; dz++) {
                        for (var dx = -1; dx <= 1; dx++) {
                            var axes = ((dx == 0 ? 0 : 1) + (dy == 0 ? 0 : 1) + (dz == 0 ? 0 : 1));
                            if (axes == 0 || !AdmitsAxes(axes: axes)) {
                                continue;
                            }
                            var nx = cx + dx;
                            var ny = cy + dy;
                            var nz = cz + dz;
                            if ((uint)nx >= (uint)Tuning.Width || (uint)ny >= (uint)Tuning.Layers || (uint)nz >= (uint)Tuning.Depth) {
                                continue;
                            }
                            var next = Index(x: nx, y: ny, z: nz);
                            if (!CanTraverse(current: current, next: next, x: cx, y: cy, z: cz, dx: dx, dy: dy, dz: dz) || m_closedStamp[next] == m_searchStamp) {
                                continue;
                            }
                            var stepCost = (axes == 1 ? StraightCost : (axes == 2 ? DiagonalCost : SpaceDiagonalCost));
                            var nextCost = checked(m_cost[current] + stepCost);
                            if (m_openStamp[next] != m_searchStamp || nextCost < m_cost[next]) {
                                Open(node: next, cost: nextCost, parent: current, goal: goal);
                            }
                        }
                    }
                }
            }
            return NavigationStatus.Unreachable;
        }

        private void BeginSearch() {
            m_open.Clear();
            m_searchStamp++;
            if (m_searchStamp == int.MaxValue) {
                Array.Clear(array: m_openStamp);
                Array.Clear(array: m_closedStamp);
                m_searchStamp = 1;
            }
        }
        private bool AdmitsAxes(int axes) => Tuning.Kind == NavigationKind.Surface || Tuning.Connectivity switch {
            NavigationConnectivity.Axis => axes == 1,
            NavigationConnectivity.FacesAndEdges => axes <= 2,
            _ => true,
        };
        private bool CanTraverse(int current, int next, int x, int y, int z, int dx, int dy, int dz) {
            if ((m_edges[current] & NeighborBit(dx: dx, dy: dy, dz: dz)) == 0U || !IsWalkable(next)) {
                return false;
            }
            var axes = ((dx == 0 ? 0 : 1) + (dy == 0 ? 0 : 1) + (dz == 0 ? 0 : 1));
            if (Tuning.Kind == NavigationKind.Medium && axes > 1) {
                if (dx != 0 && !IsWalkable(Index(x: x + dx, y: y, z: z))) {
                    return false;
                }
                if (dy != 0 && !IsWalkable(Index(x: x, y: y + dy, z: z))) {
                    return false;
                }
                if (dz != 0 && !IsWalkable(Index(x: x, y: y, z: z + dz))) {
                    return false;
                }
            }
            if (Tuning.Kind == NavigationKind.Medium && (m_fields is null || !m_fields.IsSegmentInsideMedium(
                clearance: Tuning.AgentRadius,
                field: m_mediumField,
                from: Position(node: current),
                to: Position(node: next),
                maximumSubdivisions: m_capacity.MaxMediumSegmentSubdivisions
            ))) {
                return false;
            }
            return true;
        }
        public bool IsTraversableEdge(int current, int next) {
            if ((uint)current >= (uint)CellCount || (uint)next >= (uint)CellCount || current == next) {
                return false;
            }
            Coordinates(node: current, x: out var x, y: out var y, z: out var z);
            Coordinates(node: next, x: out var nx, y: out var ny, z: out var nz);
            var dx = nx - x;
            var dy = ny - y;
            var dz = nz - z;
            if (Math.Abs(dx) > 1 || Math.Abs(dy) > 1 || Math.Abs(dz) > 1) {
                return false;
            }
            return CanTraverse(current: current, next: next, x: x, y: y, z: z, dx: dx, dy: dy, dz: dz);
        }
        private void BuildEdges() {
            for (var current = 0; current < CellCount; current++) {
                if (!m_walkable[current]) {
                    continue;
                }
                Coordinates(node: current, x: out var x, y: out var y, z: out var z);
                var minY = (Tuning.Kind == NavigationKind.Surface ? 0 : -1);
                var maxY = (Tuning.Kind == NavigationKind.Surface ? 0 : 1);
                for (var dy = minY; dy <= maxY; dy++) {
                    for (var dz = -1; dz <= 1; dz++) {
                        for (var dx = -1; dx <= 1; dx++) {
                            var axes = ((dx == 0 ? 0 : 1) + (dy == 0 ? 0 : 1) + (dz == 0 ? 0 : 1));
                            if (axes == 0 || !AdmitsAxes(axes: axes)) {
                                continue;
                            }
                            var nx = x + dx;
                            var ny = y + dy;
                            var nz = z + dz;
                            if ((uint)nx >= (uint)Tuning.Width || (uint)ny >= (uint)Tuning.Layers || (uint)nz >= (uint)Tuning.Depth) {
                                continue;
                            }
                            var next = Index(x: nx, y: ny, z: nz);
                            // Each static edge is symmetric. Prove it once, then record each endpoint's local bit.
                            if (next <= current || !CanTraverseStatic(current: current, next: next, x: x, y: y, z: z, dx: dx, dy: dy, dz: dz)) {
                                continue;
                            }
                            m_edges[current] |= NeighborBit(dx: dx, dy: dy, dz: dz);
                            m_edges[next] |= NeighborBit(dx: -dx, dy: -dy, dz: -dz);
                        }
                    }
                }
            }
        }
        private bool CanTraverseStatic(int current, int next, int x, int y, int z, int dx, int dy, int dz) =>
            CanTraverseStatic(m_query, current, next, x, y, z, dx, dy, dz);

        private bool CanTraverseStatic(IWorldQuery query, int current, int next, int x, int y, int z, int dx, int dy, int dz) {
            if (!m_walkable[next]) {
                return false;
            }
            if (Tuning.Kind == NavigationKind.Surface) {
                var rise = FixedQ4816.Abs(value: (m_ground[next] - m_ground[current]));
                var maximumSlopeRise = ((dx != 0) && (dz != 0)
                    ? (Tuning.MaximumSlopeRise * SquareRootTwo)
                    : Tuning.MaximumSlopeRise
                );
                if (rise > Tuning.MaxStepHeight || rise > maximumSlopeRise) {
                    return false;
                }
            }
            var axes = ((dx == 0 ? 0 : 1) + (dy == 0 ? 0 : 1) + (dz == 0 ? 0 : 1));
            if (axes > 1) {
                if (dx != 0 && !m_walkable[Index(x: x + dx, y: y, z: z)]) {
                    return false;
                }
                if (dy != 0 && !m_walkable[Index(x: x, y: y + dy, z: z)]) {
                    return false;
                }
                if (dz != 0 && !m_walkable[Index(x: x, y: y, z: z + dz)]) {
                    return false;
                }
            }
            return HasClearTransition(query: query, current: current, next: next);
        }
        // A surface edge sweeps the agent's clearance spheres between the two route points from the step height up
        // to the head: the lowest sweep's underside sits maxStepHeight above the foot, because the step rule already
        // admits everything lower, and a sphere sweeping along the floor it stands on can only advance by its own
        // clearance to that floor per march step. A volume or medium edge sweeps once, at the route points.
        private bool HasClearTransition(int current, int next) => HasClearTransition(m_query, current, next);

        private bool HasClearTransition(IWorldQuery query, int current, int next) {
            var source = Position(node: current);
            var destination = Position(node: next);
            var delta = (destination - source);
            var distance = delta.Length;
            var sweeps = 1;
            var lift = FixedQ4816.Zero;
            var verticalCore = FixedQ4816.Zero;
            if (Tuning.Kind == NavigationKind.Surface) {
                verticalCore = FixedQ4816.Max(
                    x: FixedQ4816.Zero,
                    y: (Tuning.AgentHeight - (Tuning.AgentRadius * FixedQ4816.FromInteger(value: 2)) - (ClearanceEpsilon * FixedQ4816.FromInteger(value: 2)))
                );
                lift = FixedQ4816.Min(
                    x: verticalCore,
                    y: FixedQ4816.Max(
                        x: FixedQ4816.Zero,
                        y: Tuning.MaxStepHeight
                    )
                );
                var span = (verticalCore - lift);
                var diameter = (Tuning.AgentRadius * FixedQ4816.FromInteger(value: 2));
                sweeps = Math.Min(
                    val1: m_capacity.MaxSurfaceClearanceSweeps,
                    val2: checked((int)((span.Value + diameter.Value - 1L) / diameter.Value) + 1)
                );
            }
            for (var sweep = 0; sweep < sweeps; sweep++) {
                var offset = (sweeps == 1
                    ? verticalCore
                    : (lift + ((verticalCore - lift) * FixedQ4816.FromInteger(value: sweep) / FixedQ4816.FromInteger(value: sweeps - 1)))
                );
                var origin = source with { Y = (source.Y + offset) };
                if (m_query.SphereCast(
                    origin: FixedPosition.FromLocal(local: origin),
                    dir: delta,
                    radius: Tuning.AgentRadius,
                    maxDist: distance,
                    hit: out _
                )) {
                    return false;
                }
            }
            return true;
        }
        private static uint NeighborBit(int dx, int dy, int dz) {
            var ordinal = (((dy + 1) * 9) + ((dz + 1) * 3) + (dx + 1));
            if (ordinal > 13) {
                ordinal--;
            }
            return (1U << ordinal);
        }
        // f = g + h, then the smaller h, then the lower index: a total order, so the heap never breaks a tie itself.
        private int Compare(int left, int right, int goal) {
            var leftH = Heuristic(left, goal);
            var rightH = Heuristic(right, goal);
            var f = (m_cost[left] + leftH).CompareTo(m_cost[right] + rightH);
            return f != 0 ? f : (leftH != rightH ? leftH.CompareTo(rightH) : left.CompareTo(right));
        }
        private readonly struct GoalOrder(Domain domain, int goal) : INodeOrder {
            public int Compare(int left, int right) => domain.Compare(left: left, right: right, goal: goal);
        }
        private int Heuristic(int node, int goal) {
            Coordinates(node: node, x: out var nx, y: out var ny, z: out var nz);
            Coordinates(node: goal, x: out var gx, y: out var gy, z: out var gz);
            return checked(Math.Max(Math.Abs(nx - gx), Math.Max(Math.Abs(ny - gy), Math.Abs(nz - gz))) * StraightCost);
        }
        public bool IsWalkable(int node) {
            if ((uint)node >= (uint)m_walkable.Length || !m_walkable[node]) {
                return false;
            }
            if (Tuning.Kind != NavigationKind.Medium) {
                return true;
            }
            return m_fields is not null && m_fields.IsInsideMedium(field: m_mediumField, position: Position(node), clearance: Tuning.AgentRadius);
        }
        private int Index(int x, int y, int z) => ((((y * Tuning.Depth) + z) * Tuning.Width) + x);
        private void Coordinates(int node, out int x, out int y, out int z) {
            x = node % Tuning.Width;
            var yz = node / Tuning.Width;
            z = yz % Tuning.Depth;
            y = yz / Tuning.Depth;
        }
        private void Open(int node, int cost, int parent, int goal) {
            m_cost[node] = cost;
            m_parent[node] = parent;
            var order = new GoalOrder(domain: this, goal: goal);
            if (m_openStamp[node] != m_searchStamp) {
                m_openStamp[node] = m_searchStamp;
                m_open.Push(node: node, order: order);
            } else {
                m_open.Decrease(node: node, order: order);
            }
        }
        private NavigationStatus Reconstruct(int goal, Span<int> path, out int pathLength) {
            pathLength = 0;
            for (var node = goal; node >= 0; node = m_parent[node]) {
                if (pathLength >= path.Length || pathLength >= Tuning.MaxPathNodes) {
                    pathLength = 0;
                    return NavigationStatus.PathLimit;
                }
                path[pathLength++] = node;
            }
            path[..pathLength].Reverse();
            return NavigationStatus.Active;
        }
        private static Int128 RoundedCell(Int128 value, long cellSize) {
            var numerator = value;
            var denominator = cellSize;
            var quotient = numerator / denominator;
            var remainder = numerator % denominator;
            if (remainder < 0) {
                remainder += denominator;
                quotient--;
            }
            if ((remainder * 2L) >= denominator) {
                quotient++;
            }
            return quotient;
        }
    }
}
