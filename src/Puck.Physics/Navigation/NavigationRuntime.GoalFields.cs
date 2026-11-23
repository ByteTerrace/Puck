using Puck.Abstractions.Collections;

namespace Puck.Physics.Navigation;

/// <summary>A goal field: for every cell of a network from which a goal cell can be reached, the step a body standing
/// there takes on a cheapest route to the goal, across the network's domains and the junctions joining them. Many bodies
/// heading to one place read one field, each a step at a time, so a body holds no route of its own and a body pushed off
/// its line simply reads the step from where it now stands.</summary>
/// <remarks>A field is a pure function of the goal and the baked domains of its network (and the live medium a medium
/// domain reads), derived whole when first read (<see cref="NavigationRuntime.GoalField"/>). Which fields are resident
/// decides only work and memory: a dropped field is derived again with the same steps, so no field is simulation state,
/// and nothing checkpoints or hashes it. One byte per reached cell holds the step: a neighbour slot in the domain, a
/// junction to another domain, the goal itself, or no route.</remarks>
public sealed class NavigationGoalField {
    // A cell's step: 0..25 is the neighbour slot (NavigationRuntime.Domain.NeighborSlot), 26.. is the junction partner at
    // that rank in the cell's ascending junction list, and the two values below mark the goal and a cell with no route.
    internal const byte Goal = 0xFF;
    internal const byte NoRoute = 0xFE;
    internal const byte FirstJunction = 26;

    internal NavigationGoalField(int goalDomain, int goalCell, NavigationRuntime.Domain[] members, int[] memberOf, byte[]?[] steps, ulong[] mediumRevisions, int reachedCells) {
        GoalDomain = goalDomain;
        GoalCell = goalCell;
        Members = members;
        MemberOf = memberOf;
        Steps = steps;
        MediumRevisions = mediumRevisions;
        ReachedCells = reachedCells;
        Bytes = steps.Sum(selector: static chunk => ((long)(chunk?.Length ?? 0)));
    }

    /// <summary>Gets the bytes the field's steps hold.</summary>
    public long Bytes { get; }
    /// <summary>Gets the goal cell's domain index in the runtime that reads the field.</summary>
    public int GoalDomain { get; }
    /// <summary>Gets the goal cell.</summary>
    public int GoalCell { get; }
    /// <summary>Gets the cells from which the goal can be reached, the goal included.</summary>
    public int ReachedCells { get; }

    // The network's domains in runtime order, a step chunk for each (null where no cell reaches the goal), and the member
    // position of each runtime domain index (-1 outside the network).
    internal NavigationRuntime.Domain[] Members { get; }
    internal int[] MemberOf { get; }
    internal byte[]?[] Steps { get; }
    internal ulong[] MediumRevisions { get; }
    internal long LastRead { get; set; }
    // Whether the runtime reading the field has joined its network's junctions, which its junction steps index.
    internal bool NetworkJoined { get; set; } = true;

    // The same steps for a replacement runtime that keeps the field's domains in the same order at other indices; the
    // step chunks are never written after a derivation, so the two share them.
    internal NavigationGoalField Rebound(int goalDomain, int[] memberOf) => new(goalCell: GoalCell, goalDomain: goalDomain, mediumRevisions: MediumRevisions, memberOf: memberOf, members: Members, reachedCells: ReachedCells, steps: Steps) {
        LastRead = LastRead,
        NetworkJoined = false,
    };
}

public sealed partial class NavigationRuntime {
    private readonly Dictionary<int, NavigationGoalField> m_goalFields = [];
    private GoalFieldWorkspace? m_goalWorkspace;
    private long m_goalFieldBytes;
    private long m_goalFieldReads;

    /// <summary>Gets the number of resident goal fields.</summary>
    public int ResidentGoalFields => m_goalFields.Count;
    /// <summary>Gets the bytes the resident goal fields hold together, at most <see cref="NavigationCapacity.GoalFieldBytes"/>
    /// unless one field alone is larger.</summary>
    public long GoalFieldBytes => m_goalFieldBytes;
    /// <summary>Gets the goal fields derived since the last <see cref="BeginStep"/>.</summary>
    public int GoalFieldBuildsLast { get; private set; }
    /// <summary>Gets the cells goal-field derivations settled since the last <see cref="BeginStep"/>.</summary>
    public long GoalFieldCellsLast { get; private set; }
    /// <summary>Gets the goal-field steps read since the last <see cref="BeginStep"/>.</summary>
    public long GoalFieldStepsLast => Interlocked.Read(location: ref m_goalFieldStepsLast);
    // Counted with an interlock: GoalStep reads a resident field and may run on several threads at once.
    private long m_goalFieldStepsLast;
    /// <summary>Gets the goal fields dropped from a full cache over the runtime's life.</summary>
    public long GoalFieldEvictions { get; private set; }

    /// <summary>Starts a simulation tick's navigation work: clears the per-tick goal-field counts.</summary>
    public void BeginStep() {
        GoalFieldBuildsLast = 0;
        GoalFieldCellsLast = 0;
        m_goalFieldStepsLast = 0;
    }

    /// <summary>Returns the goal field toward a walkable cell, deriving it when it is not resident or a live medium it
    /// reads has changed since it was derived. Bakes every domain of the goal's network and joins them.</summary>
    /// <param name="goalDomain">The goal cell's domain index.</param>
    /// <param name="goalCell">The goal cell; must be walkable.</param>
    /// <returns>The field.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="goalDomain"/> is not a domain index, or
    /// <paramref name="goalCell"/> is not a walkable cell of it.</exception>
    public NavigationGoalField GoalField(int goalDomain, int goalCell) {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value: ((uint)goalDomain), other: ((uint)m_domains.Length), paramName: nameof(goalDomain));
        if (!m_domains[goalDomain].IsWalkable(node: goalCell)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(goalCell), actualValue: goalCell, message: "a goal field needs a walkable goal cell.");
        }

        var key = (m_offsets[goalDomain] + goalCell);

        if (m_goalFields.TryGetValue(key: key, value: out var field) && IsCurrent(field: field)) {
            // A field kept from a prior runtime reads junctions this runtime has not joined yet.
            if (!field.NetworkJoined) {
                JoinNetwork(members: field.Members);
                field.NetworkJoined = true;
            }
            field.LastRead = ++m_goalFieldReads;
            return field;
        }
        if (field is not null) {
            Drop(key: key, field: field);
        }

        field = BuildGoalField(goalDomain: goalDomain, goalCell: goalCell);
        field.LastRead = ++m_goalFieldReads;
        m_goalFields[key] = field;
        m_goalFieldBytes += field.Bytes;
        while ((m_goalFieldBytes > m_capacity.GoalFieldBytes) && (m_goalFields.Count > 1)) {
            var oldest = -1;
            var oldestRead = long.MaxValue;

            foreach (var (candidateKey, candidate) in m_goalFields) {
                if ((candidate != field) && (candidate.LastRead < oldestRead)) {
                    oldest = candidateKey;
                    oldestRead = candidate.LastRead;
                }
            }
            Drop(key: oldest, field: m_goalFields[oldest]);
            GoalFieldEvictions++;
            m_work.Add(amount: 1, kind: NavigationSearchWork.FieldEvictions);
        }

        return field;
    }
    /// <summary>Reads the step a body standing on a cell takes toward a goal field's goal.</summary>
    /// <param name="field">The goal field, from <see cref="GoalField"/> on this runtime.</param>
    /// <param name="domain">The cell's domain index.</param>
    /// <param name="cell">The cell.</param>
    /// <param name="nextDomain">The next cell's domain: the same domain for a step, another for a junction at the same
    /// place; the cell's own domain when the cell is the goal.</param>
    /// <param name="nextCell">The next cell; the cell itself when it is the goal.</param>
    /// <returns><see cref="NavigationStatus.Active"/> with a next cell, <see cref="NavigationStatus.Arrived"/> on the goal,
    /// <see cref="NavigationStatus.OutsideDomain"/> when the domain is not in the field's network, or
    /// <see cref="NavigationStatus.Unreachable"/> when no route leads from the cell to the goal.</returns>
    public NavigationStatus GoalStep(NavigationGoalField field, int domain, int cell, out int nextDomain, out int nextCell) {
        ArgumentNullException.ThrowIfNull(argument: field);
        _ = Interlocked.Increment(location: ref m_goalFieldStepsLast);
        m_work.Add(amount: 1, kind: NavigationSearchWork.FieldSteps);
        nextDomain = domain;
        nextCell = cell;

        var member = ((((uint)domain) < ((uint)field.MemberOf.Length)) ? field.MemberOf[domain] : -1);

        if (member < 0) {
            return NavigationStatus.OutsideDomain;
        }
        if ((field.Steps[member] is not { } steps) || (((uint)cell) >= ((uint)steps.Length))) {
            return NavigationStatus.Unreachable;
        }

        var step = steps[cell];

        if (step == NavigationGoalField.Goal) {
            return NavigationStatus.Arrived;
        }
        if (step == NavigationGoalField.NoRoute) {
            return NavigationStatus.Unreachable;
        }
        if (step < NavigationGoalField.FirstJunction) {
            nextCell = m_domains[domain].NeighborAt(node: cell, slot: step);
            return NavigationStatus.Active;
        }

        var partner = m_junctions[(m_offsets[domain] + cell)][(step - NavigationGoalField.FirstJunction)];

        nextDomain = DomainOf(node: partner);
        nextCell = (partner - m_offsets[nextDomain]);
        return NavigationStatus.Active;
    }

    private bool IsCurrent(NavigationGoalField field) {
        for (var member = 0; (member < field.Members.Length); member++) {
            if (field.Members[member].MediumValueRevision != field.MediumRevisions[member]) {
                return false;
            }
        }
        return true;
    }
    private void Drop(int key, NavigationGoalField field) {
        _ = m_goalFields.Remove(key: key);
        m_goalFieldBytes -= field.Bytes;
    }
    // The domains a goal's field covers: the goal's domain and every domain that can join it, in runtime order. A medium
    // domain joins no other, so a medium goal's field covers its own domain alone.
    private Domain[] NetworkOf(int goalDomain) {
        var goal = m_domains[goalDomain];

        return m_domains.Where(predicate: domain => ((domain == goal) || Joins(left: domain, right: goal))).ToArray();
    }
    // A reverse Dijkstra over the network's cells and junctions from the goal, each step costed as the route search costs
    // it (its length in millimetres plus the clearance surcharge of the cell stepped onto, a junction free), then each
    // reached cell's step chosen from the settled costs alone: the first of its forward steps, in neighbour order and then
    // its junctions in ascending order, whose cost plus the cell it reaches equals the cell's own. The choice reads only
    // final costs, never the order the search settled ties in, so the field is a function of the graph.
    private NavigationGoalField BuildGoalField(int goalDomain, int goalCell) {
        var members = NetworkOf(goalDomain: goalDomain);

        JoinNetwork(members: members);

        var workspace = (m_goalWorkspace ??= new GoalFieldWorkspace(cells: checked((int)CellCount)));
        var memberOf = new int[m_domains.Length];
        Span<int> from = stackalloc int[MaxRouteSteps];
        Span<int> cost = stackalloc int[MaxRouteSteps];
        Span<byte> slots = stackalloc byte[MaxRouteSteps];

        Array.Fill(array: memberOf, value: -1);
        for (var member = 0; (member < members.Length); member++) {
            memberOf[Array.IndexOf(array: m_domains, value: members[member])] = member;
        }
        workspace.Begin();
        workspace.Relax(node: (m_offsets[goalDomain] + goalCell), cost: 0L);
        while (workspace.Open.Count != 0) {
            var node = workspace.Open.Pop(order: workspace.Order);
            var domainIndex = DomainOf(node: node);
            var domain = m_domains[domainIndex];
            var offset = m_offsets[domainIndex];
            var count = domain.RoutePredecessors(node: (node - offset), from: from, cost: cost);

            workspace.Settle(node: node);
            for (var index = 0; (index < count); index++) {
                workspace.Relax(node: (offset + from[index]), cost: (workspace.Cost[node] + cost[index]));
            }
            if (m_junctions.TryGetValue(key: node, value: out var partners)) {
                foreach (var partner in partners) {
                    if (memberOf[DomainOf(node: partner)] >= 0) {
                        workspace.Relax(node: partner, cost: workspace.Cost[node]);
                    }
                }
            }
        }

        var steps = new byte[members.Length][];
        var goalNode = (m_offsets[goalDomain] + goalCell);
        var settled = workspace.Settled.Length;

        for (var ordinal = 0; (ordinal < settled); ordinal++) {
            var node = workspace.Settled[ordinal];
            var domainIndex = DomainOf(node: node);
            var member = memberOf[domainIndex];
            var domain = m_domains[domainIndex];
            var offset = m_offsets[domainIndex];
            var chunk = steps[member];

            if (chunk is null) {
                chunk = new byte[domain.CellCount];
                Array.Fill(array: chunk, value: NavigationGoalField.NoRoute);
                steps[member] = chunk;
            }
            if (node == goalNode) {
                chunk[(node - offset)] = NavigationGoalField.Goal;
                continue;
            }

            var target = workspace.Cost[node];
            var step = NavigationGoalField.NoRoute;
            var count = domain.RouteSteps(node: (node - offset), next: from, cost: cost, slot: slots);

            for (var index = 0; (index < count); index++) {
                if (workspace.IsSettled(node: (offset + from[index])) && ((workspace.Cost[(offset + from[index])] + cost[index]) == target)) {
                    step = slots[index];
                    break;
                }
            }
            // A junction is free, so its two ends share a cost; only the end settled first can be the step, which keeps
            // two coincident cells from each pointing at the other.
            if ((step == NavigationGoalField.NoRoute) && m_junctions.TryGetValue(key: node, value: out var partners)) {
                for (var rank = 0; (rank < partners.Count); rank++) {
                    if ((memberOf[DomainOf(node: partners[rank])] >= 0) && workspace.SettledBefore(node: partners[rank], ordinal: ordinal) && (workspace.Cost[partners[rank]] == target)) {
                        step = checked((byte)(NavigationGoalField.FirstJunction + rank));
                        break;
                    }
                }
            }
            chunk[(node - offset)] = step;
        }

        var revisions = members.Select(selector: static domain => domain.MediumValueRevision).ToArray();

        GoalFieldBuildsLast++;
        GoalFieldCellsLast += settled;
        m_work.Add(amount: 1, kind: NavigationSearchWork.FieldBuilds);
        m_work.Add(amount: settled, kind: NavigationSearchWork.FieldCells);
        return new NavigationGoalField(goalCell: goalCell, goalDomain: goalDomain, mediumRevisions: revisions, memberOf: memberOf, members: members, reachedCells: settled, steps: steps);
    }
    // Keeps a prior runtime's goal fields whose network is the same domain objects in the same order here: equal baked
    // domains, so equal junctions in the same ascending order, and so every step means what it meant.
    private void AdoptGoalFields(NavigationRuntime previous) {
        foreach (var field in previous.m_goalFields.Values) {
            var goalDomain = Array.IndexOf(array: m_domains, value: previous.m_domains[field.GoalDomain]);

            if ((goalDomain < 0) || !NetworkOf(goalDomain: goalDomain).AsSpan().SequenceEqual(other: field.Members)) {
                continue;
            }

            var memberOf = new int[m_domains.Length];

            Array.Fill(array: memberOf, value: -1);
            for (var member = 0; (member < field.Members.Length); member++) {
                memberOf[Array.IndexOf(array: m_domains, value: field.Members[member])] = member;
            }
            m_goalFields[(m_offsets[goalDomain] + field.GoalCell)] = field.Rebound(goalDomain: goalDomain, memberOf: memberOf);
            m_goalFieldBytes += field.Bytes;
            m_goalFieldReads = Math.Max(val1: m_goalFieldReads, val2: field.LastRead);
        }
    }
    private void JoinNetwork(Domain[] members) {
        for (var left = 0; (left < members.Length); left++) {
            for (var right = (left + 1); (right < members.Length); right++) {
                Join(first: Array.IndexOf(array: m_domains, value: members[left]), second: Array.IndexOf(array: m_domains, value: members[right]));
            }
        }
    }

    // Scratch for one derivation at a time, over every cell of the runtime: costs in millimetres (wide enough for any
    // route across the largest world), the open heap ordered by cost then node, the cells discovered, the cells settled
    // in settle order and each settled cell's place in it. Clearing costs what the last derivation reached.
    private sealed class GoalFieldWorkspace(int cells) {
        public readonly long[] Cost = new long[cells];
        public readonly NodeHeap Open = new(cells: cells);
        private readonly MarkedIndexSet m_discovered = new();
        private readonly MarkedIndexSet m_settled = new();
        private readonly int[] m_ordinal = new int[cells];

        public KeyThenIndexOrder<long> Order => new(keys: Cost);
        public ReadOnlySpan<int> Settled => m_settled.Members;

        public void Begin() {
            Open.Clear();
            m_discovered.Clear();
            m_settled.Clear();
        }
        public bool IsSettled(int node) => m_settled.Contains(index: node);
        public bool SettledBefore(int node, int ordinal) => (m_settled.Contains(index: node) && (m_ordinal[node] < ordinal));
        public void Relax(int node, long cost) {
            if (m_settled.Contains(index: node)) {
                return;
            }
            if (m_discovered.Add(index: node)) {
                Cost[node] = cost;
                Open.Push(node: node, order: Order);
            } else if (cost < Cost[node]) {
                Cost[node] = cost;
                Open.Decrease(node: node, order: Order);
            }
        }
        public void Settle(int node) {
            m_ordinal[node] = m_settled.Count;
            _ = m_settled.Add(index: node);
        }
    }
}
