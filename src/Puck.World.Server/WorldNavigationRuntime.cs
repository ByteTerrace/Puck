using Puck.Maths;

namespace Puck.World.Server;

/// <summary>Result of one bounded deterministic route search.</summary>
public enum WorldNavigationStatus : byte {
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

internal sealed partial class WorldNavigationRuntime {
    private readonly Domain[] m_domains;

    public WorldNavigationRuntime(WorldDefinition definition, IWorldQuery? query, WorldFieldLattice? fields) {
        var rows = definition.Navigation.Rows;

        if (rows.Count != 0 && query is null) {
            throw new InvalidOperationException(message: "navigation domains require the deterministic solid-field query provider.");
        }

        m_domains = new Domain[rows.Count];
        for (var index = 0; index < rows.Count; index++) {
            m_domains[index] = new Domain(row: rows[index], query: query!, fields: fields);
        }
    }

    public int Count => m_domains.Length;
    public long CellCount => m_domains.Sum(selector: static domain => (long)domain.CellCount);
    public long WalkableCellCount => m_domains.Sum(selector: static domain => (long)domain.WalkableCellCount);
    public long WorkspaceBytes => m_domains.Sum(selector: static domain => domain.WorkspaceBytes);
    public Domain this[int index] => m_domains[index];

    internal sealed partial class Domain {
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
        private readonly int[] m_heap;
        private int m_heapCount;
        private readonly int[] m_heapPosition;
        private readonly int[] m_openStamp;
        private readonly int[] m_parent;
        private int m_searchStamp;
        private readonly bool[] m_walkable;

        private readonly WorldFieldLattice? m_fields;
        private readonly int m_mediumField;
        private readonly IWorldQuery m_query;

        public Domain(WorldNavigationDomain row, IWorldQuery query, WorldFieldLattice? fields) {
            Name = row.Name;
            Tuning = FixedWorldNavigationDomain.Compile(domain: row);
            m_fields = fields;
            m_query = query;
            m_mediumField = ((Tuning.Kind == WorldNavigationKind.Medium) && (fields is not null) && fields.TryFieldIndex(name: Tuning.Medium!, field: out var mediumField)
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
            m_heapPosition = new int[count];
            m_heap = new int[count];

            for (var node = 0; node < count; node++) {
                Coordinates(node: node, x: out var x, y: out var y, z: out var z);
                var probe = new FixedVector3(
                    X: (Tuning.Origin.X + (Tuning.CellSize * FixedQ4816.FromInteger(value: x))),
                    Y: (Tuning.Origin.Y + (Tuning.CellSize * FixedQ4816.FromInteger(value: y))),
                    Z: (Tuning.Origin.Z + (Tuning.CellSize * FixedQ4816.FromInteger(value: z)))
                );

                if (Tuning.Kind != WorldNavigationKind.Surface) {
                    if (!query.Overlap(center: FixedPosition.FromLocal(local: probe), radius: Tuning.AgentRadius)) {
                        m_ground[node] = probe.Y;
                        m_walkable[node] = true;
                        WalkableCellCount++;
                    }
                    continue;
                }

                if (!query.TryGroundHeight(
                    position: FixedPosition.FromLocal(local: probe),
                    probeUp: Tuning.ProbeUp,
                    probeDown: Tuning.ProbeDown,
                    groundY: out var ground
                )) {
                    continue;
                }

                var foot = new FixedVector3(X: probe.X, Y: (ground + Tuning.AgentRadius + ClearanceEpsilon), Z: probe.Z);
                var headY = (ground + Tuning.AgentHeight - Tuning.AgentRadius - ClearanceEpsilon);
                var clear = !query.Overlap(center: FixedPosition.FromLocal(local: foot), radius: Tuning.AgentRadius);

                if (clear && headY > foot.Y) {
                    var head = new FixedVector3(X: probe.X, Y: headY, Z: probe.Z);
                    var core = (head - foot);
                    clear = !query.Overlap(center: FixedPosition.FromLocal(local: head), radius: Tuning.AgentRadius)
                        && !query.SphereCast(
                            origin: FixedPosition.FromLocal(local: foot),
                            dir: core,
                            radius: Tuning.AgentRadius,
                            maxDist: core.Length,
                            hit: out _
                        );
                }
                if (clear) {
                    // The route point is the lower clearance-sphere center, not the ground surface. Steering a body
                    // toward the surface would make the route itself collide even though its bake was clear.
                    m_ground[node] = foot.Y;
                    m_walkable[node] = true;
                    WalkableCellCount++;
                }
            }

            BuildEdges();
            Sharing = row.Shared;
            InitializeSharing();
        }

        public int CellCount => m_walkable.Length;
        public string Name { get; }
        public FixedWorldNavigationDomain Tuning { get; }
        public int WalkableCellCount { get; }
        public long WorkspaceBytes => checked((long)CellCount * ((6L * sizeof(int)) + sizeof(long) + sizeof(uint) + sizeof(byte)) + SharedWorkspaceBytes);

        // Actual off-center locomotion needs a continuous proof: a cached grid edge certifies only the line
        // between its cell centers. Surface locomotion has a different support/clearance contract.
        public bool AdmitsLocomotion(in FixedVector3 from, in FixedVector3 to) {
            if (Tuning.Kind == WorldNavigationKind.Surface || !TryCell(from, out _) || !TryCell(to, out _)) { return false; }
            if (Tuning.Kind == WorldNavigationKind.Medium && (m_fields is null || !m_fields.IsSegmentInsideMedium(
                m_mediumField, from, to, Tuning.AgentRadius, WorldNavigationCapacity.MaxMediumSegmentSubdivisions))) { return false; }
            if (m_query.Overlap(FixedPosition.FromLocal(from), Tuning.AgentRadius) ||
                m_query.Overlap(FixedPosition.FromLocal(to), Tuning.AgentRadius)) { return false; }
            var delta = to - from;
            return delta == FixedVector3.Zero || !m_query.SphereCast(FixedPosition.FromLocal(from), delta,
                Tuning.AgentRadius, delta.Length, out _);
        }

        public FixedVector3 Position(int node) {
            Coordinates(node: node, x: out var x, y: out var y, z: out var z);
            return new FixedVector3(
                X: (Tuning.Origin.X + (Tuning.CellSize * FixedQ4816.FromInteger(value: x))),
                Y: (Tuning.Kind == WorldNavigationKind.Surface ? m_ground[node] : (Tuning.Origin.Y + (Tuning.CellSize * FixedQ4816.FromInteger(value: y)))),
                Z: (Tuning.Origin.Z + (Tuning.CellSize * FixedQ4816.FromInteger(value: z)))
            );
        }

        public bool TryCell(in FixedVector3 position, out int node) {
            var x = RoundedCell(value: (Int128)position.X.Value - Tuning.Origin.X.Value, cellSize: Tuning.CellSize.Value);
            var z = RoundedCell(value: (Int128)position.Z.Value - Tuning.Origin.Z.Value, cellSize: Tuning.CellSize.Value);
            var y = (Tuning.Kind == WorldNavigationKind.Surface ? 0 : RoundedCell(value: (Int128)position.Y.Value - Tuning.Origin.Y.Value, cellSize: Tuning.CellSize.Value));
            if (x < 0 || x >= Tuning.Width || y < 0 || y >= Tuning.Layers || z < 0 || z >= Tuning.Depth) {
                node = -1;
                return false;
            }
            node = Index(x: (int)x, y: (int)y, z: (int)z);
            return IsWalkable(node: node);
        }

        public WorldNavigationStatus FindPath(int start, int goal, Span<int> path, out int pathLength, out int expanded) {
            pathLength = 0;
            expanded = 0;
            if (!IsWalkable(start) || !IsWalkable(goal)) {
                return WorldNavigationStatus.OutsideDomain;
            }
            if (start == goal) {
                path[0] = start;
                pathLength = 1;
                return WorldNavigationStatus.Arrived;
            }

            BeginSearch();
            Open(node: start, cost: 0, parent: -1, goal: goal);
            while (m_heapCount != 0) {
                var current = Pop(goal: goal);
                m_closedStamp[current] = m_searchStamp;
                expanded++;
                if (current == goal) {
                    return Reconstruct(goal: goal, path: path, pathLength: out pathLength);
                }
                if (expanded >= Tuning.MaxExpandedNodes) {
                    return WorldNavigationStatus.SearchLimit;
                }

                Coordinates(node: current, x: out var cx, y: out var cy, z: out var cz);
                var minY = (Tuning.Kind == WorldNavigationKind.Surface ? 0 : -1);
                var maxY = (Tuning.Kind == WorldNavigationKind.Surface ? 0 : 1);
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
            return WorldNavigationStatus.Unreachable;
        }

        private void BeginSearch() {
            m_heapCount = 0;
            m_searchStamp++;
            if (m_searchStamp == int.MaxValue) {
                Array.Clear(array: m_openStamp);
                Array.Clear(array: m_closedStamp);
                m_searchStamp = 1;
            }
        }
        private bool AdmitsAxes(int axes) => Tuning.Kind == WorldNavigationKind.Surface || Tuning.Connectivity switch {
            WorldNavigationConnectivity.Axis => axes == 1,
            WorldNavigationConnectivity.FacesAndEdges => axes <= 2,
            _ => true,
        };
        private bool CanTraverse(int current, int next, int x, int y, int z, int dx, int dy, int dz) {
            if ((m_edges[current] & NeighborBit(dx: dx, dy: dy, dz: dz)) == 0U || !IsWalkable(next)) {
                return false;
            }
            var axes = ((dx == 0 ? 0 : 1) + (dy == 0 ? 0 : 1) + (dz == 0 ? 0 : 1));
            if (Tuning.Kind == WorldNavigationKind.Medium && axes > 1) {
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
            if (Tuning.Kind == WorldNavigationKind.Medium && (m_fields is null || !m_fields.IsSegmentInsideMedium(
                clearance: Tuning.AgentRadius,
                field: m_mediumField,
                from: Position(node: current),
                to: Position(node: next),
                maximumSubdivisions: WorldNavigationCapacity.MaxMediumSegmentSubdivisions
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
                var minY = (Tuning.Kind == WorldNavigationKind.Surface ? 0 : -1);
                var maxY = (Tuning.Kind == WorldNavigationKind.Surface ? 0 : 1);
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
        private bool CanTraverseStatic(int current, int next, int x, int y, int z, int dx, int dy, int dz) {
            if (!m_walkable[next]) {
                return false;
            }
            if (Tuning.Kind == WorldNavigationKind.Surface) {
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
            return HasClearTransition(current: current, next: next);
        }
        private bool HasClearTransition(int current, int next) {
            var source = Position(node: current);
            var destination = Position(node: next);
            var delta = (destination - source);
            var distance = delta.Length;
            var sweeps = 1;
            var verticalCore = FixedQ4816.Zero;
            if (Tuning.Kind == WorldNavigationKind.Surface) {
                verticalCore = FixedQ4816.Max(
                    x: FixedQ4816.Zero,
                    y: (Tuning.AgentHeight - (Tuning.AgentRadius * FixedQ4816.FromInteger(value: 2)) - (ClearanceEpsilon * FixedQ4816.FromInteger(value: 2)))
                );
                var diameter = (Tuning.AgentRadius * FixedQ4816.FromInteger(value: 2));
                sweeps = Math.Min(
                    val1: WorldNavigationCapacity.MaxSurfaceClearanceSweeps,
                    val2: checked((int)((verticalCore.Value + diameter.Value - 1L) / diameter.Value) + 1)
                );
            }
            for (var sweep = 0; sweep < sweeps; sweep++) {
                var offset = (sweeps == 1
                    ? FixedQ4816.Zero
                    : (verticalCore * FixedQ4816.FromInteger(value: sweep) / FixedQ4816.FromInteger(value: sweeps - 1))
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
        private int Compare(int left, int right, int goal) {
            var leftH = Heuristic(left, goal);
            var rightH = Heuristic(right, goal);
            var f = (m_cost[left] + leftH).CompareTo(m_cost[right] + rightH);
            return f != 0 ? f : (leftH != rightH ? leftH.CompareTo(rightH) : left.CompareTo(right));
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
            if (Tuning.Kind != WorldNavigationKind.Medium) {
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
            if (m_openStamp[node] != m_searchStamp) {
                m_openStamp[node] = m_searchStamp;
                m_heapPosition[node] = m_heapCount;
                m_heap[m_heapCount++] = node;
            }
            SiftUp(index: m_heapPosition[node], goal: goal);
        }
        private int Pop(int goal) {
            var result = m_heap[0];
            var last = m_heap[--m_heapCount];
            if (m_heapCount != 0) {
                m_heap[0] = last;
                m_heapPosition[last] = 0;
                SiftDown(index: 0, goal: goal);
            }
            m_heapPosition[result] = -1;
            return result;
        }
        private WorldNavigationStatus Reconstruct(int goal, Span<int> path, out int pathLength) {
            pathLength = 0;
            for (var node = goal; node >= 0; node = m_parent[node]) {
                if (pathLength >= path.Length || pathLength >= Tuning.MaxPathNodes) {
                    pathLength = 0;
                    return WorldNavigationStatus.PathLimit;
                }
                path[pathLength++] = node;
            }
            path[..pathLength].Reverse();
            return WorldNavigationStatus.Active;
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
        private void SiftUp(int index, int goal) {
            while (index > 0) {
                var parent = (index - 1) >> 1;
                if (Compare(m_heap[index], m_heap[parent], goal) >= 0) {
                    break;
                }
                Swap(index, parent);
                index = parent;
            }
        }
        private void SiftDown(int index, int goal) {
            while (true) {
                var left = (index * 2) + 1;
                if (left >= m_heapCount) {
                    break;
                }
                var right = left + 1;
                var best = (right < m_heapCount && Compare(m_heap[right], m_heap[left], goal) < 0) ? right : left;
                if (Compare(m_heap[best], m_heap[index], goal) >= 0) {
                    break;
                }
                Swap(index, best);
                index = best;
            }
        }
        private void Swap(int left, int right) {
            (m_heap[left], m_heap[right]) = (m_heap[right], m_heap[left]);
            m_heapPosition[m_heap[left]] = left;
            m_heapPosition[m_heap[right]] = right;
        }
    }
}

internal sealed class BodyNavigationState {
    public int DomainIndex = -1;
    public int ExpandedLast;
    public int GoalCell = -1;
    public int PathLength;
    public int Waypoint;
    public int[] Path { get; private set; } = [];
    public WorldNavigationStatus Status;

    public Span<int> WritablePath() {
        if (Path.Length == 0) {
            Path = new int[WorldNavigationCapacity.MaxPathNodes];
        }
        return Path;
    }

    public void Clear(WorldNavigationStatus status = WorldNavigationStatus.None) {
        DomainIndex = -1;
        ExpandedLast = 0;
        GoalCell = -1;
        PathLength = 0;
        Waypoint = 0;
        Status = status;
    }
}
