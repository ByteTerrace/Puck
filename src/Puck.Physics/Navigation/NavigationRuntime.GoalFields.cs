using Puck.Abstractions.Collections;

namespace Puck.Physics.Navigation;

/// <summary>A goal field: for every cell of a network from which a goal cell can be reached, the step a body standing
/// there takes on a cheapest route to the goal, across the network's domains and the junctions joining them. Many bodies
/// heading to one place read one field, each a step at a time, so a body holds no route of its own and a body pushed off
/// its line simply reads the step from where it now stands.</summary>
/// <remarks>A field is a pure function of the goal, the cost profile it prices areas by, the baked domains of its network
/// and their areas (and the live medium a medium domain reads), derived whole when first read (<see cref="NavigationRuntime.GoalField"/>). Which fields are resident
/// decides only work and memory: a dropped field is derived again with the same steps, so no field is simulation state,
/// and nothing checkpoints or hashes it. One byte per reached cell holds the step: a neighbour slot in the domain, a
/// junction to another domain, the goal itself, or no route.</remarks>
public sealed class NavigationGoalField {
    // A cell's step: 0..25 is the neighbour slot (NavigationRuntime.Domain.NeighborSlot), 26.. is the junction partner at
    // that rank in the cell's ascending junction list, and the two values below mark the goal and a cell with no route.
    internal const byte Goal = 0xFF;
    internal const byte NoRoute = 0xFE;
    internal const byte FirstJunction = 26;

    internal NavigationGoalField(int goalDomain, int goalCell, NavigationCostProfile profile, NavigationRuntime.Domain[] members, int[] memberOf, byte[]?[] steps, ulong[] mediumRevisions, int reachedCells) {
        GoalDomain = goalDomain;
        GoalCell = goalCell;
        Profile = profile;
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
    /// <summary>Gets the cost profile the field's steps are cheapest under: bodies that price areas differently read
    /// different fields toward one goal.</summary>
    public NavigationCostProfile Profile { get; }
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
    internal NavigationGoalField Rebound(int goalDomain, int[] memberOf) => new(goalCell: GoalCell, goalDomain: goalDomain, mediumRevisions: MediumRevisions, memberOf: memberOf, members: Members, profile: Profile, reachedCells: ReachedCells, steps: Steps) {
        LastRead = LastRead,
        NetworkJoined = false,
    };
}

public sealed partial class NavigationRuntime {
    // Keyed by the goal's runtime node and the profile, which compares by its costs.
    private readonly Dictionary<(int Node, NavigationCostProfile Profile), NavigationGoalField> m_goalFields = [];
    // The derivations requested ahead of their read (RequestGoalField), running off the thread that owns the runtime
    // until a read joins them (GoalField) or a step's start finds them done (BeginStep).
    private readonly Dictionary<(int Node, NavigationCostProfile Profile), GoalFieldJob> m_requestedGoalFields = [];
    private readonly List<(int Node, NavigationCostProfile Profile)> m_landedGoalFields = [];
    // Idle derivation scratch, taken by a derivation on whatever thread runs it and given back when it ends.
    private SharedPool<GoalFieldWorkspace>? m_goalWorkspaces;
    private long m_goalFieldBytes;
    private long m_goalFieldReads;

    /// <summary>Gets the number of resident goal fields.</summary>
    public int ResidentGoalFields => m_goalFields.Count;
    /// <summary>Gets the bytes the resident goal fields hold together, at most <see cref="NavigationCapacity.GoalFieldBytes"/>
    /// unless one field alone is larger.</summary>
    public long GoalFieldBytes => m_goalFieldBytes;
    /// <summary>Gets the goal fields derived and made resident since the last <see cref="BeginStep"/>.</summary>
    public int GoalFieldBuildsLast { get; private set; }
    /// <summary>Gets the cells those derivations settled.</summary>
    public long GoalFieldCellsLast { get; private set; }
    /// <summary>Gets the goal-field derivations requested ahead of their read since the last <see cref="BeginStep"/>.</summary>
    public int GoalFieldRequestsLast { get; private set; }
    /// <summary>Gets the goal-field reads since the last <see cref="BeginStep"/> that waited for a derivation: one still
    /// running when its read came, or none requested ahead, which the read then derived itself.</summary>
    public int GoalFieldWaitsLast { get; private set; }
    /// <summary>Gets the goal-field steps read since the last <see cref="BeginStep"/>.</summary>
    public long GoalFieldStepsLast => Interlocked.Read(location: ref m_goalFieldStepsLast);
    // Counted with an interlock: GoalStep reads a resident field and may run on several threads at once.
    private long m_goalFieldStepsLast;
    /// <summary>Gets the goal fields dropped from a full cache over the runtime's life.</summary>
    public long GoalFieldEvictions { get; private set; }

    /// <summary>Starts a simulation tick's navigation work: clears the per-tick goal-field counts and makes resident every
    /// derivation requested ahead that has finished. What is resident decides only work: a read of a field not resident
    /// derives it or joins its derivation (<see cref="GoalField"/>), so no step's outcome depends on when a derivation
    /// finished.</summary>
    public void BeginStep() {
        GoalFieldBuildsLast = 0;
        GoalFieldCellsLast = 0;
        GoalFieldRequestsLast = 0;
        GoalFieldWaitsLast = 0;
        m_goalFieldStepsLast = 0;
        if (m_requestedGoalFields.Count == 0) {
            return;
        }
        foreach (var (key, job) in m_requestedGoalFields) {
            if (job.Task!.IsCompleted) { m_landedGoalFields.Add(item: key); }
        }
        foreach (var key in m_landedGoalFields) {
            _ = m_requestedGoalFields.Remove(key: key, value: out var job);
            Install(field: job!.Task!.GetAwaiter().GetResult());
        }
        m_landedGoalFields.Clear();
    }

    /// <summary>Starts deriving the goal field toward a walkable cell under a cost profile off the calling thread, unless
    /// it is resident and current or already requested, so the read that needs it (<see cref="GoalField"/>) finds it done
    /// or joins it. The request bakes every domain of the goal's network and joins them on the calling thread; the
    /// derivation reads only what the request froze.</summary>
    /// <param name="goalDomain">The goal cell's domain index.</param>
    /// <param name="goalCell">The goal cell; must be walkable.</param>
    /// <param name="profile">What the field's routes pay for each area type and which they never enter;
    /// <see langword="null"/> is <see cref="NavigationCostProfile.Neutral"/>. Equal profiles share one field.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="goalDomain"/> is not a domain index, or
    /// <paramref name="goalCell"/> is not a walkable cell of it.</exception>
    /// <exception cref="InvalidOperationException">The runtime is read-only (<see cref="EnterReadOnly"/>).</exception>
    public void RequestGoalField(int goalDomain, int goalCell, NavigationCostProfile? profile = null) {
        var key = GoalKey(goalDomain: goalDomain, goalCell: goalCell, profile: ref profile);

        if (m_requestedGoalFields.ContainsKey(key: key) || (m_goalFields.TryGetValue(key: key, value: out var resident) && IsCurrent(field: resident))) {
            return;
        }

        var job = Freeze(goalDomain: goalDomain, goalCell: goalCell, profile: key.Profile);

        job.Task = Task.Run(function: () => Derive(job: job));
        m_requestedGoalFields[key] = job;
        GoalFieldRequestsLast++;
        m_work.Add(amount: 1, kind: NavigationSearchWork.FieldRequests);
    }
    /// <summary>Returns the goal field toward a walkable cell under a cost profile: the resident one while every live medium
    /// it read stands, else the derivation requested ahead (<see cref="RequestGoalField"/>), joined, else one derived on the
    /// calling thread. Bakes every domain of the goal's network and joins them.</summary>
    /// <param name="goalDomain">The goal cell's domain index.</param>
    /// <param name="goalCell">The goal cell; must be walkable.</param>
    /// <param name="profile">What the field's routes pay for each area type and which they never enter;
    /// <see langword="null"/> is <see cref="NavigationCostProfile.Neutral"/>. Equal profiles share one field.</param>
    /// <returns>The field.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="goalDomain"/> is not a domain index, or
    /// <paramref name="goalCell"/> is not a walkable cell of it.</exception>
    /// <exception cref="InvalidOperationException">The runtime is read-only (<see cref="EnterReadOnly"/>).</exception>
    public NavigationGoalField GoalField(int goalDomain, int goalCell, NavigationCostProfile? profile = null) {
        var key = GoalKey(goalDomain: goalDomain, goalCell: goalCell, profile: ref profile);

        if (TryResident(key: key, field: out var field)) {
            return field;
        }
        if (m_requestedGoalFields.Remove(key: key, value: out var requested)) {
            if (!requested.Task!.IsCompleted) {
                GoalFieldWaitsLast++;
                m_work.Add(amount: 1, kind: NavigationSearchWork.FieldWaits);
            }
            field = requested.Task.GetAwaiter().GetResult();
            // A medium that moved while the derivation ran leaves it describing water that is gone.
            if (!IsCurrent(field: field)) { field = null; }
        }
        if (field is null) {
            GoalFieldWaitsLast++;
            m_work.Add(amount: 1, kind: NavigationSearchWork.FieldWaits);
            field = Derive(job: Freeze(goalDomain: goalDomain, goalCell: goalCell, profile: key.Profile));
        }
        Install(field: field);

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

    // Validates a goal and returns its cache key, the profile defaulted to Neutral.
    private (int Node, NavigationCostProfile Profile) GoalKey(int goalDomain, int goalCell, ref NavigationCostProfile? profile) {
        ThrowIfReadOnly(operation: "a goal field read");
        profile ??= NavigationCostProfile.Neutral;
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value: ((uint)goalDomain), other: ((uint)m_domains.Length), paramName: nameof(goalDomain));
        if (!m_domains[goalDomain].IsWalkable(node: goalCell)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(goalCell), actualValue: goalCell, message: "a goal field needs a walkable goal cell.");
        }

        return ((m_offsets[goalDomain] + goalCell), profile);
    }
    // The resident field for a key while every live medium it read stands; a stale one is dropped.
    private bool TryResident((int Node, NavigationCostProfile Profile) key, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out NavigationGoalField? field) {
        if (m_goalFields.TryGetValue(key: key, value: out field) && IsCurrent(field: field)) {
            // A field kept from a prior runtime reads junctions this runtime has not joined yet.
            if (!field.NetworkJoined) {
                JoinNetwork(members: field.Members);
                field.NetworkJoined = true;
            }
            field.LastRead = ++m_goalFieldReads;
            return true;
        }
        if (field is not null) {
            Drop(key: (key.Node, key.Profile), field: field);
            field = null;
        }

        return false;
    }
    // Makes a derived field resident, then drops the least recently read fields while the cache is past its bytes.
    private void Install(NavigationGoalField field) {
        var key = ((m_offsets[field.GoalDomain] + field.GoalCell), field.Profile);

        if (m_goalFields.Remove(key: key, value: out var replaced)) {
            m_goalFieldBytes -= replaced.Bytes;
        }
        field.LastRead = ++m_goalFieldReads;
        m_goalFields[key] = field;
        m_goalFieldBytes += field.Bytes;
        GoalFieldBuildsLast++;
        GoalFieldCellsLast += field.ReachedCells;
        m_work.Add(amount: 1, kind: NavigationSearchWork.FieldBuilds);
        m_work.Add(amount: field.ReachedCells, kind: NavigationSearchWork.FieldCells);
        while ((m_goalFieldBytes > m_capacity.GoalFieldBytes) && (m_goalFields.Count > 1)) {
            var oldest = default((int, NavigationCostProfile));
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
    }

    private bool IsCurrent(NavigationGoalField field) {
        for (var member = 0; (member < field.Members.Length); member++) {
            if (field.Members[member].MediumValueRevision != field.MediumRevisions[member]) {
                return false;
            }
        }
        return true;
    }
    private void Drop((int, NavigationCostProfile) key, NavigationGoalField field) {
        _ = m_goalFields.Remove(key: key);
        m_goalFieldBytes -= field.Bytes;
    }
    // The domains a goal's field covers: the goal's domain and every domain that can join it, in runtime order. A medium
    // domain joins no other, so a medium goal's field covers its own domain alone.
    private Domain[] NetworkOf(int goalDomain) {
        var goal = m_domains[goalDomain];

        return m_domains.Where(predicate: domain => ((domain == goal) || Joins(left: domain, right: goal))).ToArray();
    }

    // What one derivation reads, frozen on the thread that owns the runtime: the network's route graphs and area codes, the
    // junctions among its cells, and the live media's revisions. Nothing a later step changes reaches a derivation.
    private sealed class GoalFieldJob {
        public required int GoalDomain { get; init; }
        public required int GoalCell { get; init; }
        public required int GoalNode { get; init; }
        public required NavigationCostProfile Profile { get; init; }
        public required Domain[] Members { get; init; }
        public required int[] MemberOf { get; init; }
        public required RouteGraph[] Graphs { get; init; }
        public required byte[]?[] Areas { get; init; }
        // Each junction cell of the network and its partners in the network, ascending, in the order the runtime keeps.
        public required Dictionary<int, int[]> Junctions { get; init; }
        public required ulong[] MediumRevisions { get; init; }
        public Task<NavigationGoalField>? Task { get; set; }
    }

    // The junctions among each network's cells as a derivation reads them, by the network's first domain, with the
    // members and the junction table's revision they were taken at. Invariant: a snapshot is never written after it is
    // made, so every derivation of a network shares one, on whichever thread it runs; it is a function of the junction
    // table, which only Link writes (m_junctionRevision), and of the network's members, and FrozenJunctions, the one
    // door a job takes it by, makes another when either has moved.
    private readonly Dictionary<int, (Domain[] Members, long Revision, Dictionary<int, int[]> Junctions)> m_frozenJunctions = [];

    private Dictionary<int, int[]> FrozenJunctions(Domain[] members, int[] memberOf) {
        var network = Array.IndexOf(array: m_domains, value: members[0]);

        if (m_frozenJunctions.TryGetValue(key: network, value: out var frozen) && (frozen.Revision == m_junctionRevision) && (frozen.Members.Length == members.Length)) {
            var same = true;

            for (var member = 0; (same && (member < members.Length)); member++) { same = ReferenceEquals(objA: frozen.Members[member], objB: members[member]); }
            if (same) { return frozen.Junctions; }
        }

        var junctions = new Dictionary<int, int[]>();

        foreach (var (node, partners) in m_junctions) {
            if (memberOf[DomainOf(node: node)] < 0) { continue; }

            var count = 0;

            foreach (var partner in partners) { count += ((memberOf[DomainOf(node: partner)] >= 0) ? 1 : 0); }
            if (count == 0) { continue; }

            var kept = new int[partners.Count];

            // The rank a step names is the partner's rank in the runtime's whole list, so the list is kept whole and a
            // partner outside the network is marked by its own domain (MemberOf) when the derivation reads it.
            partners.CopyTo(array: kept);
            junctions[node] = kept;
        }
        m_frozenJunctions[network] = (members, m_junctionRevision, junctions);
        return junctions;
    }
    // Freezes one derivation's inputs: bakes and joins the goal's network, and takes each member's route graph.
    private GoalFieldJob Freeze(int goalDomain, int goalCell, NavigationCostProfile profile) {
        var members = NetworkOf(goalDomain: goalDomain);

        JoinNetwork(members: members);

        var memberOf = new int[m_domains.Length];
        var graphs = new RouteGraph[members.Length];
        var areas = new byte[]?[members.Length];

        Array.Fill(array: memberOf, value: -1);
        for (var member = 0; (member < members.Length); member++) {
            memberOf[Array.IndexOf(array: m_domains, value: members[member])] = member;
            graphs[member] = members[member].GoalGraph();
            areas[member] = (profile.IsNeutral ? null : members[member].GoalAreas());
        }

        return new GoalFieldJob {
            Areas = areas,
            GoalCell = goalCell,
            GoalDomain = goalDomain,
            GoalNode = (m_offsets[goalDomain] + goalCell),
            Graphs = graphs,
            Junctions = FrozenJunctions(memberOf: memberOf, members: members),
            MediumRevisions = members.Select(selector: static domain => domain.MediumValueRevision).ToArray(),
            MemberOf = memberOf,
            Members = members,
            Profile = profile,
        };
    }

    // A reverse Dijkstra over the network's cells and junctions from the goal, each step costed as the route search costs
    // it (its length in millimetres plus the clearance surcharge of the cell stepped onto, times the profile's multiplier
    // for that cell's area; a junction free; no step onto an area the profile excludes), then each reached cell's step
    // chosen from the settled costs alone: the first of its forward steps, in neighbour order and then its junctions in
    // ascending order, whose cost plus the cell it reaches equals the cell's own. The choice reads final costs and, between
    // a junction's coincident ends, which settled first, so the field is a function of the frozen graph. It reads only its
    // job and its own workspace, so it runs on any thread.
    private NavigationGoalField Derive(GoalFieldJob job) {
        var pool = Volatile.Read(location: ref m_goalWorkspaces) ?? CreateGoalWorkspaces();
        var workspace = pool.Rent();

        try {
            return Derive(job: job, workspace: workspace);
        } finally {
            pool.Return(item: workspace);
        }
    }
    private SharedPool<GoalFieldWorkspace> CreateGoalWorkspaces() {
        var cells = checked((int)CellCount);

        _ = Interlocked.CompareExchange(location1: ref m_goalWorkspaces, value: new SharedPool<GoalFieldWorkspace>(create: () => new GoalFieldWorkspace(cells: cells)), comparand: null);
        return m_goalWorkspaces!;
    }
    private NavigationGoalField Derive(GoalFieldJob job, GoalFieldWorkspace workspace) {
        var offsets = m_offsets;
        var domains = m_domains.Length;
        var memberOf = job.MemberOf;
        var profile = job.Profile;
        Span<long> axisCost = stackalloc long[4];

        workspace.Begin();
        workspace.Relax(node: job.GoalNode, cost: 0L);
        while (workspace.Open.Count != 0) {
            var node = workspace.Open.Pop(order: workspace.Order);
            var domainIndex = DomainOf(offsets: offsets, domains: domains, node: node);
            var member = memberOf[domainIndex];
            var graph = job.Graphs[member];
            var areas = job.Areas[member];
            var offset = offsets[domainIndex];
            var cell = (node - offset);
            var area = ((areas is null) ? (byte)0 : areas[cell]);

            workspace.Settle(node: node);
            // A cell whose area the profile excludes is stepped onto by no route, and a junction hop onto it is no step
            // either.
            if (profile.Scale(area: area, cost: 0L) < 0L) {
                continue;
            }
            graph.StepCosts(cell: cell, area: area, profile: profile, costs: axisCost);

            var cost = workspace.Cost[node];

            for (var bits = graph.Reverse[cell]; (bits != 0U); bits &= (bits - 1U)) {
                var slot = System.Numerics.BitOperations.TrailingZeroCount(value: bits);

                workspace.Relax(node: (node + graph.SlotDelta[slot]), cost: (cost + axisCost[RouteGraph.SlotAxes[slot]]));
            }
            if (job.Junctions.TryGetValue(key: node, value: out var partners)) {
                foreach (var partner in partners) {
                    if (memberOf[DomainOf(offsets: offsets, domains: domains, node: partner)] >= 0) {
                        workspace.Relax(node: partner, cost: cost);
                    }
                }
            }
        }

        var steps = new byte[job.Members.Length][];
        var settled = workspace.Settled.Length;

        for (var ordinal = 0; (ordinal < settled); ordinal++) {
            var node = workspace.Settled[ordinal];
            var domainIndex = DomainOf(offsets: offsets, domains: domains, node: node);
            var member = memberOf[domainIndex];
            var graph = job.Graphs[member];
            var areas = job.Areas[member];
            var offset = offsets[domainIndex];
            var cell = (node - offset);
            var chunk = steps[member];

            if (chunk is null) {
                chunk = new byte[graph.Forward.Length];
                Array.Fill(array: chunk, value: NavigationGoalField.NoRoute);
                steps[member] = chunk;
            }
            if (node == job.GoalNode) {
                chunk[cell] = NavigationGoalField.Goal;
                continue;
            }

            var target = workspace.Cost[node];
            var step = NavigationGoalField.NoRoute;

            for (var bits = graph.Forward[cell]; (bits != 0U); bits &= (bits - 1U)) {
                var slot = System.Numerics.BitOperations.TrailingZeroCount(value: bits);
                var next = (cell + graph.SlotDelta[slot]);
                var stepCost = graph.StepCost(next: next, axes: RouteGraph.SlotAxes[slot], area: ((areas is null) ? (byte)0 : areas[next]), profile: profile);

                if ((stepCost >= 0L) && workspace.IsSettled(node: (offset + next)) && ((workspace.Cost[(offset + next)] + stepCost) == target)) {
                    step = ((byte)slot);
                    break;
                }
            }
            // A junction is free, so its two ends share a cost; only the end settled first can be the step, which keeps
            // two coincident cells from each pointing at the other.
            if ((step == NavigationGoalField.NoRoute) && job.Junctions.TryGetValue(key: node, value: out var partners)) {
                for (var rank = 0; (rank < partners.Length); rank++) {
                    var partner = partners[rank];
                    var partnerDomain = DomainOf(offsets: offsets, domains: domains, node: partner);
                    var partnerMember = memberOf[partnerDomain];

                    if ((partnerMember >= 0) && workspace.SettledBefore(node: partner, ordinal: ordinal) && (workspace.Cost[partner] == target) &&
                        (job.Areas[partnerMember] is not { } partnerAreas || (profile.Scale(area: partnerAreas[(partner - offsets[partnerDomain])], cost: 0L) >= 0L))) {
                        step = checked((byte)(NavigationGoalField.FirstJunction + rank));
                        break;
                    }
                }
            }
            chunk[cell] = step;
        }

        return new NavigationGoalField(goalCell: job.GoalCell, goalDomain: job.GoalDomain, mediumRevisions: job.MediumRevisions, memberOf: memberOf, members: job.Members, profile: profile, reachedCells: settled, steps: steps);
    }
    // Keeps a prior runtime's goal fields whose network is the same domain objects in the same order here, under the same
    // area set: equal baked domains, so equal junctions in the same ascending order, equal area costs, and so every step
    // means what it meant.
    private void AdoptGoalFields(NavigationRuntime previous) {
        if (!ReferenceEquals(objA: m_areas, objB: previous.m_areas)) {
            return;
        }
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
            m_goalFields[((m_offsets[goalDomain] + field.GoalCell), field.Profile)] = field.Rebound(goalDomain: goalDomain, memberOf: memberOf);
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
