using Puck.Maths;

namespace Puck.Physics.Navigation;

/// <summary>One waypoint of a route across a runtime's domains.</summary>
/// <param name="Domain">The domain's index in the runtime.</param>
/// <param name="Cell">The cell's index in that domain.</param>
/// <param name="Position">The cell's route position (<see cref="NavigationRuntime.Domain.Position"/>): a surface cell's
/// clearance-sphere center over its ground, a volume cell's center.</param>
public readonly record struct NavigationRouteNode(int Domain, int Cell, FixedVector3 Position);

/// <summary>Where a cross-domain route search may end: every cell it accepts is a goal, and its bounds give the search
/// a lower bound on the distance left.</summary>
public interface INavigationRouteGoal {
    /// <summary>Gets a box holding every accepted cell's route position; the search's heuristic is the horizontal
    /// Chebyshev distance to it.</summary>
    FixedAabb Bounds { get; }
    /// <summary>Returns whether a walkable cell ends the route.</summary>
    /// <param name="domain">The cell's domain.</param>
    /// <param name="cell">The cell.</param>
    /// <param name="foot">The point a body standing on the cell stands at (<see cref="NavigationRuntime.Domain.Foot"/>).</param>
    bool Accepts(int domain, int cell, in FixedVector3 foot);
}
/// <summary>A route goal of one cell.</summary>
/// <param name="Domain">The cell's domain.</param>
/// <param name="Cell">The cell.</param>
/// <param name="Position">The cell's route position.</param>
public sealed record NavigationCellGoal(int Domain, int Cell, FixedVector3 Position) : INavigationRouteGoal {
    /// <inheritdoc/>
    public FixedAabb Bounds => new(Maximum: Position, Minimum: Position);
    /// <inheritdoc/>
    public bool Accepts(int domain, int cell, in FixedVector3 foot) => ((domain == Domain) && (cell == Cell));
}
/// <summary>The work one cross-domain route search did.</summary>
/// <param name="Expanded">The nodes the search settled.</param>
/// <param name="Joins">The domain pairs whose junctions the search computed for the first time.</param>
public readonly record struct NavigationRouteWork(int Expanded, int Joins);

public sealed partial class NavigationRuntime {
    private const int MaxRouteSteps = 26;

    private SearchWorkspace? m_route;

    // The union of every domain's cells as one graph: a node is a domain's cell at its domain's offset, its edges the
    // domain's own baked edges plus a junction to each cell of another domain of its network standing at the same place.
    // A junction joins two walkable cells whose route positions share their X and Z exactly and, between two surfaces,
    // differ in height by no more than the lower step height of the two (two volume cells must coincide), so a route
    // crosses between domains only where both domains prove the same ground. Exact coincidence is what makes the hop free
    // of any unproven segment: aligned grids (origins and cell sizes exact in fixed point and multiples of each other)
    // share cell centers where they overlap. Route searches and goal fields share the junctions; each node's partners are
    // kept in ascending node order, so a goal field's junction rank names the same partner whichever search joined it.
    private readonly int[] m_offsets;
    private readonly bool[] m_joined;
    private readonly Dictionary<int, List<int>> m_junctions = [];

    // One A* search's working arrays over a node space: the cross-domain route search's (one per runtime) and a domain's
    // own path search's (rented per search, so searches on several threads each hold their own). A search's answer
    // depends only on the graph, never on what an earlier search left here: Begin opens a fresh stamp.
    private sealed class SearchWorkspace {
        public SearchWorkspace(int cells) {
            Cost = new long[cells];
            Heuristic = new long[cells];
            Parent = new int[cells];
            OpenStamp = new int[cells];
            ClosedStamp = new int[cells];
            Open = new NodeHeap(cells: cells);
        }

        public readonly long[] Cost;
        public readonly long[] Heuristic;
        public readonly int[] Parent;
        public readonly int[] OpenStamp;
        public readonly int[] ClosedStamp;
        public readonly NodeHeap Open;
        public int Stamp;

        public long Bytes => checked(((long)Cost.Length) * ((2L * sizeof(long)) + (5L * sizeof(int))));

        // Empties the open set and moves to a fresh stamp, so no node reads as opened or closed by an earlier search.
        public void Begin() {
            Open.Clear();
            if (++Stamp == int.MaxValue) {
                Array.Clear(array: OpenStamp);
                Array.Clear(array: ClosedStamp);
                Stamp = 1;
            }
        }
    }

    // f = g + h, then the smaller h, then the lower node: a total order, so the heap never breaks a tie itself.
    private readonly struct SearchOrder(SearchWorkspace workspace) : IComparer<int> {
        public int Compare(int x, int y) {
            var f = (workspace.Cost[x] + workspace.Heuristic[x]).CompareTo(value: (workspace.Cost[y] + workspace.Heuristic[y]));

            return ((f != 0)
                ? f
                : ((workspace.Heuristic[x] != workspace.Heuristic[y])
                    ? workspace.Heuristic[x].CompareTo(value: workspace.Heuristic[y])
                    : x.CompareTo(value: y)));
        }
    }

    /// <summary>Gets the junctions between domains the route searches and goal fields have found so far, each counted
    /// once.</summary>
    public int JunctionCount { get; private set; }
    /// <summary>Gets the bytes of the cross-domain route search's workspace, allocated on the first route search; zero
    /// before it.</summary>
    public long RouteWorkspaceBytes => (m_route?.Bytes ?? 0L);

    /// <summary>Finds the walkable cell a world position stands on: among the domains whose footprint and height band
    /// hold it and whose rounded cell is walkable, the one whose route height is nearest the position's, ties to the
    /// lower domain. Bakes each domain it asks.</summary>
    /// <param name="position">The world position, a body's foot or any point over ground.</param>
    /// <param name="domain">The domain's index, or -1.</param>
    /// <param name="cell">The cell's index in it, or -1.</param>
    /// <param name="network">The index of a domain whose network the search keeps to, or -1 for every domain.</param>
    /// <returns><see langword="true"/> when some domain holds a walkable cell there.</returns>
    public bool TryLocate(in FixedVector3 position, out int domain, out int cell, int network = -1) {
        domain = -1;
        cell = -1;
        var best = FixedQ4816.MaxValue;

        for (var index = 0; (index < m_domains.Length); index++) {
            var candidate = m_domains[index];

            if (
                ((network >= 0) && (index != network) && !Joins(left: candidate, right: m_domains[network])) ||
                !candidate.CoversRouteHeight(position: in position) ||
                !candidate.TryCell(node: out var node, position: in position)
            ) {
                continue;
            }

            var gap = FixedQ4816.Abs(value: (candidate.Position(node: node).Y - position.Y));

            if (gap < best) {
                best = gap;
                domain = index;
                cell = node;
            }
        }

        return (domain >= 0);
    }
    /// <summary>Finds a route from a walkable cell to the nearest cell a goal accepts, across the start domain's network:
    /// one bounded A* over its domains' baked edges and the junctions where they share a cell, its cost each step's length
    /// in millimetres and its heuristic the horizontal Chebyshev distance to the goal's bounds, which never exceeds the
    /// cost left. It bakes the domains it reaches and joins a pair of domains the first time it settles a cell inside the
    /// second one's footprint and height band.</summary>
    /// <param name="fromDomain">The start cell's domain.</param>
    /// <param name="fromCell">The start cell.</param>
    /// <param name="goal">The cells the route may end at.</param>
    /// <param name="route">Receives the route from start to goal, each junction hop as two consecutive nodes at one
    /// place; cleared first, and left empty when no route is returned.</param>
    /// <param name="maxExpanded">The most nodes the search settles before it answers <see cref="NavigationStatus.SearchLimit"/>.</param>
    /// <param name="maxNodes">The most nodes a returned route holds before the search answers <see cref="NavigationStatus.PathLimit"/>.</param>
    /// <param name="work">The nodes settled and the domain pairs joined.</param>
    /// <param name="profile">What the route pays for each area type and which it never enters; <see langword="null"/> is
    /// <see cref="NavigationCostProfile.Neutral"/>. The search's heuristic is scaled by its
    /// <see cref="NavigationCostProfile.Minimum"/>, so it stays a lower bound.</param>
    /// <returns><see cref="NavigationStatus.Active"/> with a route, <see cref="NavigationStatus.Arrived"/> when the goal
    /// accepts the start, <see cref="NavigationStatus.OutsideDomain"/> when the start is not a walkable cell,
    /// <see cref="NavigationStatus.Unreachable"/>, <see cref="NavigationStatus.SearchLimit"/> or <see cref="NavigationStatus.PathLimit"/>.</returns>
    /// <exception cref="InvalidOperationException">The runtime is read-only (<see cref="EnterReadOnly"/>).</exception>
    public NavigationStatus FindRoute(int fromDomain, int fromCell, INavigationRouteGoal goal, List<NavigationRouteNode> route, int maxExpanded, int maxNodes, out NavigationRouteWork work, NavigationCostProfile? profile = null) {
        ThrowIfReadOnly(operation: "a cross-domain route search");
        ArgumentNullException.ThrowIfNull(argument: route);
        var status = SearchRoute(cost: out _, fromCell: fromCell, fromDomain: fromDomain, goal: goal, maxCost: long.MaxValue, maxExpanded: maxExpanded, maxNodes: maxNodes, profile: (profile ?? NavigationCostProfile.Neutral), route: route, work: out work);

        m_work.Add(amount: 1, kind: NavigationSearchWork.Routes);
        m_work.Add(amount: work.Expanded, kind: NavigationSearchWork.RouteExpansions);
        return status;
    }
    /// <summary>Measures the cheapest route from a walkable cell to the nearest cell a goal accepts, as far as a cost:
    /// the same search as <see cref="FindRoute"/>, which stops once every route it has left costs more than
    /// <paramref name="maxCost"/>, so the work it does is bounded by the cells within that cost of the start and the
    /// goal, wherever the goal lies.</summary>
    /// <param name="fromDomain">The start cell's domain.</param>
    /// <param name="fromCell">The start cell.</param>
    /// <param name="goal">The cells the route may end at.</param>
    /// <param name="maxCost">The most the route may cost, in millimetres of the search's cost: each step's length,
    /// plus the clearance surcharge near walls, so a route's cost is never less than its length.</param>
    /// <param name="cost">The route's cost, in the same millimetres, when this returns
    /// <see cref="NavigationStatus.Active"/> or <see cref="NavigationStatus.Arrived"/>; otherwise zero.</param>
    /// <param name="work">The nodes settled and the domain pairs joined.</param>
    /// <returns><see cref="NavigationStatus.Active"/> when a route costs no more than <paramref name="maxCost"/>,
    /// <see cref="NavigationStatus.Arrived"/> when the goal accepts the start, <see cref="NavigationStatus.SearchLimit"/>
    /// when every route costs more, <see cref="NavigationStatus.Unreachable"/> when no route reaches the goal and
    /// <see cref="NavigationStatus.OutsideDomain"/> when the start is not a walkable cell.</returns>
    /// <exception cref="InvalidOperationException">The runtime is read-only (<see cref="EnterReadOnly"/>).</exception>
    public NavigationStatus MeasureRoute(int fromDomain, int fromCell, INavigationRouteGoal goal, long maxCost, out long cost, out NavigationRouteWork work) {
        ThrowIfReadOnly(operation: "a cross-domain route search");
        var status = SearchRoute(cost: out cost, fromCell: fromCell, fromDomain: fromDomain, goal: goal, maxCost: maxCost, maxExpanded: int.MaxValue, maxNodes: int.MaxValue, profile: NavigationCostProfile.Neutral, route: null, work: out work);

        m_work.Add(amount: 1, kind: NavigationSearchWork.Routes);
        m_work.Add(amount: work.Expanded, kind: NavigationSearchWork.RouteExpansions);
        return status;
    }
    // The one A* over the network: a route is reconstructed into `route` when one is given, and the search answers
    // SearchLimit once the cheapest open node's estimate passes `maxCost`, which no route found later could undercut.
    private NavigationStatus SearchRoute(int fromDomain, int fromCell, INavigationRouteGoal goal, NavigationCostProfile profile, List<NavigationRouteNode>? route, int maxExpanded, int maxNodes, long maxCost, out long cost, out NavigationRouteWork work) {
        ArgumentNullException.ThrowIfNull(argument: goal);
        route?.Clear();
        work = default;
        cost = 0L;
        if ((((uint)fromDomain) >= ((uint)m_domains.Length)) || !m_domains[fromDomain].IsWalkable(node: fromCell)) {
            return NavigationStatus.OutsideDomain;
        }

        var workspace = (m_route ??= new SearchWorkspace(cells: checked((int)CellCount)));
        var start = (m_offsets[fromDomain] + fromCell);
        var bounds = goal.Bounds;
        var order = new SearchOrder(workspace: workspace);
        var joins = 0;
        var expanded = 0;
        Span<int> next = stackalloc int[MaxRouteSteps];
        Span<long> stepCost = stackalloc long[MaxRouteSteps];
        Span<byte> slots = stackalloc byte[MaxRouteSteps];

        workspace.Begin();
        OpenRoute(bounds: in bounds, cost: 0L, node: start, order: order, parent: -1, profile: profile, workspace: workspace);
        while (workspace.Open.Count != 0) {
            var current = workspace.Open.Pop(order: order);
            var domainIndex = DomainOf(node: current);
            var domain = m_domains[domainIndex];
            var cell = (current - m_offsets[domainIndex]);

            if ((workspace.Cost[current] + workspace.Heuristic[current]) > maxCost) {
                work = new NavigationRouteWork(Expanded: expanded, Joins: joins);
                return NavigationStatus.SearchLimit;
            }
            workspace.ClosedStamp[current] = workspace.Stamp;
            expanded++;
            if (goal.Accepts(cell: cell, domain: domainIndex, foot: domain.Foot(node: cell))) {
                work = new NavigationRouteWork(Expanded: expanded, Joins: joins);
                cost = workspace.Cost[current];
                if (route is null) {
                    return ((current == start) ? NavigationStatus.Arrived : NavigationStatus.Active);
                }
                return ((current == start)
                    ? ReconstructRoute(goal: current, maxNodes: maxNodes, route: route, workspace: workspace) switch {
                        NavigationStatus.Active => NavigationStatus.Arrived,
                        var other => other,
                    }
                    : ReconstructRoute(goal: current, maxNodes: maxNodes, route: route, workspace: workspace));
            }
            if (expanded >= maxExpanded) {
                work = new NavigationRouteWork(Expanded: expanded, Joins: joins);
                return NavigationStatus.SearchLimit;
            }

            var count = domain.RouteSteps(cost: stepCost, next: next, node: cell, profile: profile, slot: slots);

            for (var step = 0; (step < count); step++) {
                RelaxRoute(bounds: in bounds, cost: checked((workspace.Cost[current] + stepCost[step])), node: (m_offsets[domainIndex] + next[step]), order: order, parent: current, profile: profile, workspace: workspace);
            }

            var position = domain.Foot(node: cell);

            for (var other = 0; (other < m_domains.Length); other++) {
                if ((other != domainIndex) && !m_joined[((domainIndex * m_domains.Length) + other)] && m_domains[other].CoversRouteHeight(position: in position) && Join(first: domainIndex, second: other)) {
                    joins++;
                }
            }
            if (m_junctions.TryGetValue(key: current, value: out var junctions)) {
                foreach (var junction in junctions) {
                    var partner = DomainOf(node: junction);

                    if (m_domains[partner].Admits(node: (junction - m_offsets[partner]), profile: profile)) {
                        RelaxRoute(bounds: in bounds, cost: workspace.Cost[current], node: junction, order: order, parent: current, profile: profile, workspace: workspace);
                    }
                }
            }
        }

        work = new NavigationRouteWork(Expanded: expanded, Joins: joins);
        return NavigationStatus.Unreachable;
    }

    private int DomainOf(int node) => DomainOf(offsets: m_offsets, domains: m_domains.Length, node: node);
    // The domain a runtime node lies in, by the first node of each of the first `domains` domains.
    private static int DomainOf(int[] offsets, int domains, int node) {
        var index = Array.BinarySearch(array: offsets, index: 0, length: domains, value: node);

        if (index < 0) {
            index = (~index - 1);
        }
        // Equal offsets name empty domains before a non-empty one; the node belongs to the last of them.
        while (((index + 1) < domains) && (offsets[(index + 1)] <= node)) {
            index++;
        }

        return index;
    }
    // The horizontal Chebyshev distance from a position to a box, zero inside it, in whole millimetres.
    private static int DistanceTo(in FixedAabb bounds, in FixedVector3 position) => Math.Max(
        val1: Millimetres(value: FixedQ4816.Max(x: FixedQ4816.Zero, y: FixedQ4816.Max(x: (bounds.Minimum.X - position.X), y: (position.X - bounds.Maximum.X)))),
        val2: Millimetres(value: FixedQ4816.Max(x: FixedQ4816.Zero, y: FixedQ4816.Max(x: (bounds.Minimum.Z - position.Z), y: (position.Z - bounds.Maximum.Z))))
    );
    private void OpenRoute(int node, long cost, int parent, in FixedAabb bounds, SearchOrder order, NavigationCostProfile profile, SearchWorkspace workspace) {
        var domainIndex = DomainOf(node: node);

        workspace.Cost[node] = cost;
        workspace.Parent[node] = parent;
        workspace.Heuristic[node] = profile.ScaleHeuristic(distance: DistanceTo(bounds: in bounds, position: m_domains[domainIndex].Position(node: (node - m_offsets[domainIndex]))));
        workspace.OpenStamp[node] = workspace.Stamp;
        workspace.Open.Push(node: node, order: order);
    }
    private void RelaxRoute(int node, long cost, int parent, in FixedAabb bounds, SearchOrder order, NavigationCostProfile profile, SearchWorkspace workspace) {
        if (workspace.ClosedStamp[node] == workspace.Stamp) {
            return;
        }
        if (workspace.OpenStamp[node] != workspace.Stamp) {
            OpenRoute(bounds: in bounds, cost: cost, node: node, order: order, parent: parent, profile: profile, workspace: workspace);
        } else if (cost < workspace.Cost[node]) {
            workspace.Cost[node] = cost;
            workspace.Parent[node] = parent;
            workspace.Open.Decrease(node: node, order: order);
        }
    }
    private NavigationStatus ReconstructRoute(int goal, int maxNodes, List<NavigationRouteNode> route, SearchWorkspace workspace) {
        for (var node = goal; (node >= 0); node = workspace.Parent[node]) {
            if (route.Count >= maxNodes) {
                route.Clear();
                return NavigationStatus.PathLimit;
            }

            var domainIndex = DomainOf(node: node);
            var cell = (node - m_offsets[domainIndex]);

            route.Add(item: new NavigationRouteNode(Cell: cell, Domain: domainIndex, Position: m_domains[domainIndex].Position(node: cell)));
        }

        route.Reverse();
        return NavigationStatus.Active;
    }
    // Whether two domains can join at all: one network, and neither a medium.
    private static bool Joins(Domain left, Domain right) =>
        ((left.Tuning.Kind != NavigationKind.Medium) && (right.Tuning.Kind != NavigationKind.Medium) && string.Equals(a: left.Tuning.Network, b: right.Tuning.Network, comparisonType: StringComparison.Ordinal));
    // Records every junction between two domains, in both directions, the first time a search or a goal field needs
    // them; returns whether this call joined the pair.
    private bool Join(int first, int second) {
        // The cached result is a property of the pair, independent of the search that first reached it.
        if (first > second) {
            (first, second) = (second, first);
        }
        if (m_joined[((first * m_domains.Length) + second)]) {
            return false;
        }
        m_joined[((first * m_domains.Length) + second)] = true;
        m_joined[((second * m_domains.Length) + first)] = true;

        var left = m_domains[first];
        var right = m_domains[second];

        if (!Joins(left: left, right: right)) {
            return true;
        }

        var surfaces = ((left.Tuning.Kind == NavigationKind.Surface) && (right.Tuning.Kind == NavigationKind.Surface));
        var rise = FixedQ4816.Min(x: left.Tuning.MaxStepHeight, y: right.Tuning.MaxStepHeight);

        for (var cell = 0; (cell < left.CellCount); cell++) {
            if (!left.IsWalkable(node: cell)) {
                continue;
            }

            var position = (surfaces ? left.Foot(node: cell) : left.Position(node: cell));

            if (!right.TryCell(node: out var other, position: in position)) {
                continue;
            }

            var shared = (surfaces ? right.Foot(node: other) : right.Position(node: other));

            if (
                (shared.X != position.X) ||
                (shared.Z != position.Z) ||
                (surfaces
                    ? (FixedQ4816.Abs(value: (shared.Y - position.Y)) > rise)
                    : (shared.Y != position.Y))
            ) {
                continue;
            }

            Link(from: (m_offsets[first] + cell), to: (m_offsets[second] + other));
            Link(from: (m_offsets[second] + other), to: (m_offsets[first] + cell));
            JunctionCount++;
        }

        return true;
    }
    // Keeps each node's partners in ascending node order.
    private void Link(int from, int to) {
        if (!m_junctions.TryGetValue(key: from, value: out var list)) {
            list = [];
            m_junctions[from] = list;
        }

        var at = list.BinarySearch(item: to);

        list.Insert(index: ((at < 0) ? ~at : at), item: to);
    }
    // A non-negative fixed-point length in whole millimetres, rounded down.
    private static int Millimetres(FixedQ4816 value) => checked((int)((value.Value * 1000L) >> 16));

    public sealed partial class Domain {
        /// <summary>Gets a cell's grid center: its X and Z and, for a surface cell, the domain's origin height. Reading
        /// it never bakes.</summary>
        /// <param name="node">The cell.</param>
        public FixedVector3 CellCenter(int node) {
            Coordinates(node: node, x: out var x, y: out var y, z: out var z);
            return GridPosition(x: x, y: y, z: z);
        }
        /// <summary>Gets the point a body standing on a cell stands at: a surface cell's ground under its route position,
        /// a volume cell's center. Bakes the domain.</summary>
        /// <param name="node">The cell.</param>
        public FixedVector3 Foot(int node) {
            var position = Position(node: node);

            return ((Tuning.Kind == NavigationKind.Surface)
                ? (position with { Y = (position.Y - Tuning.AgentRadius) })
                : position);
        }
        /// <summary>Returns whether a world position lies in this domain's footprint and, for a surface domain, inside
        /// the height band its ground probe searches (from <see cref="NavigationDomainInput.ProbeDown"/> under its
        /// origin to a route height over <see cref="NavigationDomainInput.ProbeUp"/> above it). Reading it never bakes.</summary>
        /// <param name="position">The world position.</param>
        public bool CoversRouteHeight(in FixedVector3 position) {
            if (!Covers(position: in position)) {
                return false;
            }
            if (Tuning.Kind != NavigationKind.Surface) {
                return true;
            }

            var height = (position.Y - Tuning.Origin.Y);

            return ((height >= -Tuning.ProbeDown) && (height <= (Tuning.ProbeUp + Tuning.AgentRadius)));
        }
        // Writes the cells one step from a cell that a route may take, with each step's cost in millimetres: its length,
        // and on a surface one cell's length more for each cell short of RouteClearanceCells that the cell stepped onto
        // stands from the edge of the walkable ground (RouteClearance), so a route keeps off a wall where the ground
        // allows and passes a doorway through its middle, rather than hugging the corners the shortest line touches;
        // then times the profile's multiplier for the area of the cell stepped onto, a step onto an area the profile
        // excludes left out. Each step's neighbour slot (NeighborSlot) goes to slot.
        internal int RouteSteps(int node, NavigationCostProfile profile, Span<int> next, Span<long> cost, Span<byte> slot) {
            EnsureBaked();

            var straight = Millimetres(value: Tuning.CellSize);
            var clearance = RouteClearance();
            var areas = (profile.IsNeutral ? null : AreaCodes());
            var count = 0;

            foreach (var step in Neighbors(node: node)) {
                if (!CanTraverse(current: node, dx: step.Dx, dy: step.Dy, dz: step.Dz, next: step.Node, x: step.X, y: step.Y, z: step.Z)) {
                    continue;
                }

                var stepCost = RouteStepCost(axes: step.Axes, clearance: clearance, next: step.Node, straight: straight);

                if ((areas is not null) && ((stepCost = profile.Scale(area: areas[step.Node], cost: stepCost)) < 0L)) {
                    continue;
                }
                next[count] = step.Node;
                cost[count] = stepCost;
                slot[count] = NeighborSlot(dx: step.Dx, dy: step.Dy, dz: step.Dz);
                count++;
            }

            return count;
        }
        // Writes the cells one route step before a cell, the steps RouteSteps takes from each of them into it, with the
        // same costs: the reverse of the route graph a goal field searches from its goal. A cell whose area the profile
        // excludes has none.
        internal int RoutePredecessors(int node, NavigationCostProfile profile, Span<int> from, Span<long> cost) {
            EnsureBaked();

            var area = (profile.IsNeutral ? (byte)0 : AreaCodes()[node]);

            if (profile.Scale(area: area, cost: 0L) < 0L) {
                return 0;
            }

            var straight = Millimetres(value: Tuning.CellSize);
            var clearance = RouteClearance();
            var count = 0;

            foreach (var step in Neighbors(node: node)) {
                if (!CanTraverse(current: step.Node, dx: -step.Dx, dy: -step.Dy, dz: -step.Dz, next: node, x: (step.X + step.Dx), y: (step.Y + step.Dy), z: (step.Z + step.Dz))) {
                    continue;
                }

                from[count] = step.Node;
                cost[count] = profile.Scale(area: area, cost: RouteStepCost(axes: step.Axes, clearance: clearance, next: node, straight: straight));
                count++;
            }

            return count;
        }
        /// <summary>Returns whether a profile admits standing on a cell: whether a route may step onto it.</summary>
        /// <param name="node">The cell.</param>
        /// <param name="profile">The cost profile.</param>
        internal bool Admits(int node, NavigationCostProfile profile) => (profile.IsNeutral || (profile.Scale(area: AreaCodes()[node], cost: 0L) >= 0L));
        // A step's length in millimetres and, on a surface, the clearance surcharge of the cell it steps onto.
        private static long RouteStepCost(int axes, byte[] clearance, int next, int straight) =>
            checked((((long)straight * StepCost(axes: axes)) / StraightCost) + ((clearance.Length == 0) ? 0L : ((long)straight * (RouteClearanceCells - clearance[next]))));

        private NavigationAreaSet m_areaSet = NavigationAreaSet.None;
        // The derived codes and what they were derived from, published as one reference so a reader on another thread
        // sees either a whole table or none.
        private sealed record AreaCodeTable(NavigationAreaSet Set, bool[] Walkable, byte[] Codes);
        private AreaCodeTable? m_areaCodes;

        /// <summary>Bakes the domain and derives its cells' area types, so reads from a read-only runtime
        /// (<see cref="EnterReadOnly"/>) find both ready.</summary>
        /// <exception cref="InvalidOperationException">The domain's runtime is read-only and either is not ready.</exception>
        public void EnsureAreas() => _ = AreaCodes();

        // The area set of the runtime that reads the domain.
        internal NavigationAreaSet Areas { set => m_areaSet = value; }

        /// <summary>Gets the area type of a cell: the type of the last area volume holding its route point, else the
        /// domain's own type (<see cref="NavigationDomainInput.Area"/>); -1 for a cell no type marks. Bakes the domain.</summary>
        /// <param name="node">The cell.</param>
        public int AreaOf(int node) => (AreaCodes()[node] - 1);

        // Each cell's area code (0 for unmarked, the type plus one otherwise), tested at the point a body standing on the
        // cell stands at (Foot). Derived once per bake and area set, never while the runtime is read-only.
        private byte[] AreaCodes() {
            EnsureBaked();
            if ((m_areaCodes is { } table) && ReferenceEquals(objA: table.Set, objB: m_areaSet) && ReferenceEquals(objA: table.Walkable, objB: m_walkable)) {
                return table.Codes;
            }
            ThrowIfReadOnly();

            var set = m_areaSet;
            var codes = new byte[CellCount];
            var region = Region(tuning: Tuning, rotation: m_rotation);
            var volumes = set.Volumes.Where(predicate: volume => volume.Shape.Bounds.Intersects(other: region)).ToArray();

            Array.Fill(array: codes, value: (byte)(Tuning.Area + 1));
            if (volumes.Length != 0) {
                for (var cell = 0; (cell < codes.Length); cell++) {
                    if (!m_walkable[cell]) {
                        continue;
                    }

                    var foot = Foot(node: cell);

                    foreach (var volume in volumes) {
                        if (volume.Shape.Bounds.Contains(point: foot) && volume.Shape.Contains(point: foot)) {
                            codes[cell] = (byte)(volume.Type + 1);
                        }
                    }
                }
            }

            m_areaCodes = new AreaCodeTable(Codes: codes, Set: set, Walkable: m_walkable);
            return codes;
        }
        // The cell one step from a cell along a neighbour slot.
        internal int NeighborAt(int node, byte slot) {
            var (dx, dy, dz) = SlotOffset(slot: slot);

            Coordinates(node: node, x: out var x, y: out var y, z: out var z);
            return Index(x: (x + dx), y: (y + dy), z: (z + dz));
        }

        // The clearance, in cells, past which a route step costs no more than its length.
        private const int RouteClearanceCells = 3;

        private byte[] m_clearance = [];
        private bool[]? m_clearanceOf;

        // The cells, counted as king moves, from each walkable surface cell to the nearest cell no route stands on,
        // capped at RouteClearanceCells (a cell beside the edge of the walkable ground is one); empty for a volume or
        // medium domain. Derived from the baked walkability once per bake.
        private byte[] RouteClearance() {
            if (Tuning.Kind != NavigationKind.Surface) {
                return [];
            }
            if (ReferenceEquals(objA: m_clearanceOf, objB: m_walkable)) {
                return m_clearance;
            }

            var walkable = m_walkable;
            var clearance = new byte[walkable.Length];

            for (var cell = 0; (cell < clearance.Length); cell++) {
                clearance[cell] = (byte)(walkable[cell] ? RouteClearanceCells : 0);
            }
            for (var ring = 1; (ring < RouteClearanceCells); ring++) {
                for (var z = 0; (z < Tuning.Depth); z++) {
                    for (var x = 0; (x < Tuning.Width); x++) {
                        var cell = Index(x: x, y: 0, z: z);

                        if (clearance[cell] != RouteClearanceCells) {
                            continue;
                        }

                        for (var dz = -1; ((dz <= 1) && (clearance[cell] == RouteClearanceCells)); dz++) {
                            for (var dx = -1; (dx <= 1); dx++) {
                                var nx = (x + dx);
                                var nz = (z + dz);

                                if ((nx >= 0) && (nx < Tuning.Width) && (nz >= 0) && (nz < Tuning.Depth) && (clearance[Index(x: nx, y: 0, z: nz)] == (ring - 1))) {
                                    clearance[cell] = (byte)ring;
                                    break;
                                }
                            }
                        }
                    }
                }
            }

            m_clearance = clearance;
            m_clearanceOf = walkable;
            return clearance;
        }
    }
}
