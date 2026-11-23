using Puck.Maths;

namespace Puck.Physics.Navigation;

/// <summary>Whether a navigation runtime is read-only, shared by the runtime and the domains it reads.</summary>
internal sealed class NavigationReadGate {
    /// <summary>Gets a gate for a domain no runtime has bound yet; nothing makes it read-only.</summary>
    public static NavigationReadGate Open => new();
    /// <summary>Gets or sets whether the runtime is read-only.</summary>
    public bool ReadOnly { get; set; }
}
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
}
/// <summary>A deterministic, bounded A*/shared-search kernel over a set of navigation domains, each proved against the
/// static contact geometry its bodies collide with and an optional live-medium field. Reads no document; every
/// domain's tuning arrives pre-compiled as a <see cref="NavigationDomainInput"/>.</summary>
/// <remarks>A domain bakes its occupancy and static edges once (<see cref="NavigationDomainBake"/>), on whichever thread
/// first needs it or on the thread pool ahead of that need (<see cref="BeginBakes"/>). The bake reads only the domain's
/// tuning and its geometry, which never changes, so its result is the same whichever thread ran it and whenever: a caller
/// that needs a bake still in flight waits for it, and no answer ever depends on when it finished.</remarks>
public sealed partial class NavigationRuntime {
    private readonly Domain[] m_domains;
    private readonly bool[] m_retained;
    private readonly NavigationReadGate m_gate = new();

    /// <summary>Marks the runtime read-only until the returned scope is disposed: reads from several threads at once
    /// may run, and every operation that writes a shared part of the runtime throws instead (a domain's bake
    /// adoption, a goal field's derivation or cache read, a cross-domain route search, a network join). A path
    /// search within one domain stays allowed, each on a workspace of its own.</summary>
    /// <returns>The scope that ends the read-only span.</returns>
    /// <exception cref="InvalidOperationException">The runtime is already read-only.</exception>
    public ReadOnlyScope EnterReadOnly() {
        if (m_gate.ReadOnly) {
            throw new InvalidOperationException(message: "The navigation runtime is already read-only.");
        }
        m_gate.ReadOnly = true;
        return new ReadOnlyScope(gate: m_gate);
    }
    /// <summary>The span <see cref="EnterReadOnly"/> opened; disposing it makes the runtime writable again.</summary>
    public readonly ref struct ReadOnlyScope {
        private readonly NavigationReadGate m_gate;

        internal ReadOnlyScope(NavigationReadGate gate) => m_gate = gate;
        /// <summary>Ends the read-only span.</summary>
        public void Dispose() => m_gate.ReadOnly = false;
    }
    // Throws while the runtime is read-only: the door every shared write passes.
    private void ThrowIfReadOnly(string operation) {
        if (m_gate.ReadOnly) {
            throw new InvalidOperationException(message: $"A read-only navigation runtime refused {operation}; it runs only on the thread that owns the runtime.");
        }
    }

    /// <summary>Compiles a runtime over the given domains.</summary>
    /// <param name="domains">The compiled domain tunings, in stable authored order.</param>
    /// <param name="geometry">The static contact geometry bodies collide with; required when
    /// <paramref name="domains"/> is non-empty. It must never change and must answer from any thread, since bakes read it
    /// off the caller's thread. An <see cref="IRegionalContactField"/> gives each domain the part its footprint can
    /// reach, which identifies the domain's bake.</param>
    /// <param name="fields">The live-medium field a medium-kind domain reads through; optional when no domain is
    /// medium-kind.</param>
    /// <param name="capacity">Representation ceilings the host's own authoring vocabulary declares.</param>
    /// <param name="previous">The prior runtime, when a host is replacing its document. A domain whose tuning, capacity
    /// and geometry part are equal to a prior domain's keeps that domain's workspace, its bake (finished or in flight)
    /// included; a medium domain also requires the same field provider at the same revision. A prior goal field whose
    /// network is the same kept domains in the same order stays resident.</param>
    /// <param name="work">The ledger the runtime counts its searches into; <see langword="null"/> counts into
    /// <see cref="NavigationSearchWork.Process"/>.</param>
    /// <exception cref="InvalidOperationException"><paramref name="domains"/> is non-empty and
    /// <paramref name="geometry"/> is <see langword="null"/>.</exception>
    public NavigationRuntime(IReadOnlyList<NavigationDomainInput> domains, IContactField? geometry, INavigationMediumField? fields, NavigationCapacity capacity, NavigationRuntime? previous = null, NavigationSearchWork? work = null) {
        if (
            (domains.Count != 0) &&
            (geometry is null)
        ) {
            throw new InvalidOperationException(message: "navigation domains require the static contact geometry.");
        }

        m_domains = new Domain[domains.Count];
        m_retained = new bool[domains.Count];
        var retained = 0;
        var rebaked = 0;

        for (var index = 0; (index < domains.Count); index++) {
            var row = domains[index];
            var part = Domain.PartOf(geometry: geometry!, row: in row);
            Domain? reused = null;

            if (previous is not null) {
                for (var oldIndex = 0; (oldIndex < previous.m_domains.Length); oldIndex++) {
                    var candidate = previous.m_domains[oldIndex];

                    if (
                        (candidate.Name == row.Name) &&
                        candidate.TryRebind(
                        row: row,
                        geometry: part,
                        fields: fields,
                        capacity: capacity
                    )
                    ) {
                        reused = candidate;
                        break;
                    }
                }
            }
            m_domains[index] = (reused ?? new Domain(
                row: row,
                geometry: part,
                fields: fields,
                capacity: capacity
            ));
            if (reused is not null) { m_retained[index] = true; retained++; continue; }
            // Start a bounded replacement bake without holding the installing thread on its contact queries.
            if (previous is not null) {
                foreach (var candidate in previous.m_domains) {
                    if ((candidate.Name == row.Name) && m_domains[index].TryBeginRebakeFrom(previous: candidate)) {
                        rebaked++;
                        break;
                    }
                }
            }
        }
        // A prior domain nothing kept has no reader left: stop its bake rather than let it run on.
        if (previous is not null) {
            foreach (var old in previous.m_domains) {
                if (Array.IndexOf(array: m_domains, value: old) < 0) { old.AbandonBake(); }
            }
        }
        RetainedDomainCount = retained;
        RebuiltDomainCount = (domains.Count - retained);
        RebakedDomainCount = rebaked;
        m_capacity = capacity;
        m_work = (work ?? NavigationSearchWork.Process);
        m_offsets = new int[(m_domains.Length + 1)];
        for (var index = 0; (index < m_domains.Length); index++) {
            m_offsets[(index + 1)] = checked((m_offsets[index] + m_domains[index].CellCount));
        }
        m_joined = new bool[(m_domains.Length * m_domains.Length)];
        foreach (var domain in m_domains) {
            domain.Work = m_work;
            domain.Gate = m_gate;
        }
        if (previous is not null) {
            AdoptGoalFields(previous: previous);
        }
    }

    private readonly NavigationCapacity m_capacity;
    private readonly NavigationSearchWork m_work;

    public int Count => m_domains.Length;
    /// <summary>Gets the number of domain workspaces retained from the prior runtime installation.</summary>
    public int RetainedDomainCount { get; }
    /// <summary>Gets the number of domain workspaces compiled for this runtime installation.</summary>
    public int RebuiltDomainCount { get; }
    /// <summary>Gets how many rebuilt domains scheduled a partial bake from their predecessor's bake.</summary>
    public int RebakedDomainCount { get; }
    /// <summary>Gets the number of domains whose bake has finished. Reading it never waits for a bake.</summary>
    public int BakedDomainCount => m_domains.Count(predicate: static domain => domain.IsBaked);
    /// <summary>Gets whether every domain's bake has finished, so no read of this runtime waits for one. Reading it never
    /// waits for a bake.</summary>
    public bool IsBaked => m_domains.All(predicate: static domain => domain.IsBaked);
    /// <summary>Gets the work of every finished bake. Reading it never waits for a bake.</summary>
    public NavigationBakeWork BakeWork {
        get {
            var total = default(NavigationBakeWork);

            foreach (var domain in m_domains) {
                if (domain.IsBaked) { total += domain.BakeWork; }
            }
            return total;
        }
    }
    public long CellCount => m_domains.Sum(selector: static domain => ((long)domain.CellCount));
    public long WalkableCellCount => m_domains.Sum(selector: static domain => ((long)domain.WalkableCellCount));
    public long WorkspaceBytes => m_domains.Sum(selector: static domain => domain.WorkspaceBytes);

    public Domain this[int index] => m_domains[index];

    /// <summary>Gets whether the domain at <paramref name="index"/> retained its compiled workspace from the prior install.</summary>
    public bool WasRetained(int index) => m_retained[index];
    /// <summary>Starts every unbaked domain's bake on the thread pool, so a later read finds it finished or in flight
    /// rather than baking on the reader's thread. A domain already baked or baking is left as it is.</summary>
    public void BeginBakes() {
        foreach (var domain in m_domains) { domain.BeginBake(); }
    }

    public sealed partial class Domain {
        private const int StraightCost = 1_000;
        private const int DiagonalCost = 1_414;
        private const int SpaceDiagonalCost = 1_732;

        private static readonly FixedQ4816 SquareRootTwo = FixedQ4816.FromDouble(value: 1.4142135623730951);

        // The path searches' workspaces: a search rents one and returns it, so searches on several threads at once each
        // hold their own, and a domain no search reaches allocates none.
        private readonly Puck.Abstractions.Collections.SharedPool<SearchWorkspace> m_searches;
        private long m_searchBytes;
        // The reader's gate: while it is read-only, a read that would adopt a bake throws instead.
        private NavigationReadGate m_gate = NavigationReadGate.Open;

        // The adopted bake's arrays: empty until EnsureBaked adopts one, never written afterwards.
        private bool[] m_walkable = [];
        private FixedQ4816[] m_ground = [];
        private uint[] m_edges = [];
        private NavigationDomainBake? m_bake;
        // A bake running on the thread pool. Only the owner's thread reads or replaces it; the bake itself reads only
        // the immutable tuning, agent volumes and geometry part.
        private Task<NavigationDomainBake>? m_pending;
        private CancellationTokenSource? m_cancellation;
        // While a delta is running, a newer replacement can start from the same immutable bake and obsolete the delta.
        private (NavigationDomainBake Bake, IContactField Geometry)? m_rebakeBasis;
        private INavigationMediumField? m_fields;

        private readonly int m_mediumField;

        private IContactField m_geometry;
        private NavigationCapacity m_capacity;

        private readonly FixedQuaternion m_rotation;
        private readonly FixedQuaternion m_inverseRotation;

        public Domain(NavigationDomainInput row, IContactField geometry, INavigationMediumField? fields, NavigationCapacity capacity) {
            Name = row.Name;
            Tuning = row;
            var up = new FixedVector3(
                X: FixedQ4816.Zero,
                Y: FixedQ4816.One,
                Z: FixedQ4816.Zero
            );

            m_rotation = FixedQuaternion.FromAxisAngle(
                up,
                row.YawRadians
            );
            m_inverseRotation = FixedQuaternion.FromAxisAngle(
                up,
                -row.YawRadians
            );
            m_fields = fields;
            m_geometry = geometry;
            m_capacity = capacity;
            m_agent = AgentVolumes(tuning: in row, lift: out m_lift);
            m_groundProbe = [FixedBodyColliderVolume.Sphere(center: new FixedVector3(X: FixedQ4816.Zero, Y: -GroundProbeRadius, Z: FixedQ4816.Zero), radius: GroundProbeRadius)];
            m_mediumField = (((Tuning.Kind == NavigationKind.Medium) && (fields is not null) && fields.TryFieldIndex(
                name: Tuning.Medium!,
                field: out var mediumField
            ))
                ? mediumField
                : -1
            );
            CellCount = checked(((Tuning.Width * Tuning.Depth) * Tuning.Layers));
            m_mediumRevision = MediumValueRevision;
            m_searches = new(create: NewSearch);
        }

        // The live medium's revision when the domain was compiled; a domain whose medium has moved on is not retained.
        private readonly ulong m_mediumRevision;
        private NavigationSearchWork m_work = NavigationSearchWork.Process;

        /// <summary>Gets the live medium's current revision for a medium domain, zero for any other: what a goal field
        /// over the domain compares to tell whether the water it was derived over has changed.</summary>
        internal ulong MediumValueRevision => ((m_mediumField >= 0) ? m_fields!.ValueRevision(field: m_mediumField) : 0UL);
        // The ledger the runtime that reads the domain counts into.
        internal NavigationSearchWork Work { set => m_work = value; }
        // The gate of the runtime that reads the domain.
        internal NavigationReadGate Gate { set => m_gate = value; }

        /// <summary>Returns the part of a geometry one domain's queries can reach: the
        /// <see cref="IRegionalContactField.Within"/> part of its footprint's region, or the whole geometry.</summary>
        internal static IContactField PartOf(IContactField geometry, in NavigationDomainInput row) =>
            ((geometry is IRegionalContactField regional)
                ? regional.Within(region: Region(tuning: in row, rotation: FixedQuaternion.FromAxisAngle(FixedVector3.UnitY, row.YawRadians)))
                : geometry);

        /// <summary>Starts this domain's bake on the thread pool unless it is baked or baking.</summary>
        public void BeginBake() {
            if ((m_bake is not null) || (m_pending is not null)) {
                return;
            }
            var geometry = m_geometry;
            StartBake(bake: cancellation => Bake(cancellation: cancellation, geometry: geometry));
        }

        private void StartBake(Func<CancellationToken, NavigationDomainBake> bake) {
            var cancellation = new CancellationTokenSource();

            m_cancellation = cancellation;
            m_pending = Task.Run(cancellationToken: cancellation.Token, function: () => bake(cancellation.Token));
        }
        // Stops a bake in flight for a domain no runtime reads any more; its fault is observed here, never left for the
        // unobserved-task handler.
        internal void AbandonBake() {
            if ((m_pending is not { } pending) || (m_cancellation is not { } cancellation)) {
                return;
            }
            cancellation.Cancel();
            _ = pending.ContinueWith(
                cancellationToken: CancellationToken.None,
                continuationAction: static (task, state) => {
                    _ = task.Exception;
                    ((CancellationTokenSource)state!).Dispose();
                },
                continuationOptions: TaskContinuationOptions.None,
                scheduler: TaskScheduler.Default,
                state: cancellation
            );
            m_pending = null;
            m_cancellation = null;
            m_rebakeBasis = null;
        }
        /// <summary>Adopts this domain's occupancy sweep and static edge bake, once: a bake in flight is waited for, and
        /// with none in flight the bake runs on the calling thread.</summary>
        /// <remarks>Every kernel read that depends on the bake calls this itself. A host calls it only when it restores
        /// state computed from a bake the kernel cannot see, such as a stored route.</remarks>
        /// <exception cref="InvalidOperationException">The domain is not baked and its runtime is read-only
        /// (<see cref="EnterReadOnly"/>): a bake is adopted only on the thread that owns the runtime.</exception>
        public void EnsureBaked() {
            if (m_bake is not null) {
                return;
            }
            ThrowIfReadOnly();
            Adopt(bake: ((m_pending is { } pending)
                ? pending.GetAwaiter().GetResult()
                : Bake(cancellation: CancellationToken.None, geometry: m_geometry)));
        }
        // Adopt is the one door a bake enters the domain through, so a read-only runtime refuses every adoption, whichever
        // read asked; EnsureBaked also asks before it waits for or runs a bake.
        private void ThrowIfReadOnly() {
            if (m_gate.ReadOnly) {
                throw new InvalidOperationException(message: $"Navigation domain '{Name}' was read unbaked while its runtime is read-only; bake it before the reads begin.");
            }
        }
        private void Adopt(NavigationDomainBake bake) {
            ThrowIfReadOnly();
            m_bake = bake;
            m_pending = null;
            m_rebakeBasis = null;
            m_cancellation?.Dispose();
            m_cancellation = null;
            m_walkable = bake.Walkable;
            m_ground = bake.Ground;
            m_edges = bake.Edges;
        }

        // A retained workspace is safe only when its bake means the same thing under the replacement: equal tuning and
        // an equal geometry part, which answers every query the bake asked as the old part did. A diagnostic that starts
        // a bake must not change which workspaces authority retains. Medium revisions are part of the proof because
        // IsWalkable/edge admission consults the live field beside the geometry.
        internal bool TryRebind(NavigationDomainInput row, IContactField geometry, INavigationMediumField? fields, NavigationCapacity capacity) {
            if (
                (row != Tuning) ||
                (capacity != m_capacity) ||
                ((Tuning.Kind == NavigationKind.Medium) && !SameMediumRevision(fields: fields)) ||
                !Equals(objA: m_geometry, objB: geometry)
            ) {
                return false;
            }
            m_geometry = geometry;
            m_fields = fields;
            m_capacity = capacity;
            return true;
        }

        private bool SameMediumRevision(INavigationMediumField? fields) {
            if (
                (Tuning.Kind != NavigationKind.Medium) ||
                (m_fields is null) ||
                (fields is null) ||
                (m_mediumField < 0) ||
                !fields.TryFieldIndex(
                Tuning.Medium!,
                out var field
            ) ||
                (field != m_mediumField)
            ) {
                return false;
            }
            // Revision numbers belong to one provider; matching numbers from independent lattices prove nothing.
            return (
                ReferenceEquals(
                objA: m_fields,
                objB: fields
            ) &&
                (m_mediumRevision == fields.ValueRevision(field: field))
            );
        }

        public int CellCount { get; }
        /// <summary>Gets whether this domain's bake has finished, adopting a bake that finished on the thread pool. A bake
        /// that failed has finished too: the next read that needs it rethrows its failure. Reading it never waits for a
        /// bake.</summary>
        public bool IsBaked {
            get {
                if ((m_bake is null) && (m_pending is { IsCompletedSuccessfully: true } pending)) {
                    Adopt(bake: pending.Result);
                }
                return ((m_bake is not null) || (m_pending is { IsCompleted: true }));
            }
        }
        /// <summary>Gets the work this domain's bake did. Bakes the domain.</summary>
        public NavigationBakeWork BakeWork {
            get {
                EnsureBaked();
                return m_bake!.Work;
            }
        }
        public string Name { get; }
        public NavigationDomainInput Tuning { get; }
        public int WalkableCellCount {
            get {
                EnsureBaked();
                return (int)m_bake!.Work.Walkable;
            }
        }
        /// <summary>Gets the bytes the domain holds per cell for its bake (ground, edges, walkability) and the bytes of
        /// every path-search workspace it has allocated, one per search that ran at once.</summary>
        public long WorkspaceBytes => checked((((long)CellCount) * ((sizeof(long) + sizeof(uint)) + sizeof(byte))) + Interlocked.Read(location: ref m_searchBytes));

        // Actual off-center locomotion needs a continuous proof: a cached grid edge certifies only the line
        // between its cell centers. Surface locomotion has a different support/clearance contract.
        public bool AdmitsLocomotion(in FixedVector3 from, in FixedVector3 to) {
            if (
                (Tuning.Kind == NavigationKind.Surface) ||
                !TryCell(
                node: out _,
                position: from
            ) ||
                !TryCell(
                node: out _,
                position: to
            )
            ) { return false; }
            if (
                (Tuning.Kind == NavigationKind.Medium) &&
                ((m_fields is null) || !m_fields.IsSegmentInsideMedium(
                m_mediumField,
                from,
                to,
                Tuning.AgentRadius,
                m_capacity.MaxMediumSegmentSubdivisions
            ))
            ) { return false; }
            return (
                FixedContactProbe.IsClear(field: m_geometry, start: from, end: from, orientation: FixedQuaternion.Identity, up: FixedVector3.UnitY, volumes: m_agent) &&
                FixedContactProbe.IsClear(field: m_geometry, start: to, end: to, orientation: FixedQuaternion.Identity, up: FixedVector3.UnitY, volumes: m_agent) &&
                ((from == to) || FixedContactProbe.IsClear(field: m_geometry, start: from, end: to, orientation: FixedQuaternion.Identity, up: FixedVector3.UnitY, volumes: m_agent))
            );
        }
        public FixedVector3 Position(int node) {
            EnsureBaked();
            Coordinates(
                node: node,
                x: out var x,
                y: out var y,
                z: out var z
            );
            var position = GridPosition(
                x: x,
                y: y,
                z: z
            );

            return ((Tuning.Kind == NavigationKind.Surface)
                ? new FixedVector3(
                    X: position.X,
                    Y: m_ground[node],
                    Z: position.Z
                )
                : position
            );
        }

        private FixedVector3 GridPosition(int x, int y, int z) {
            var local = new FixedVector3(
                X: (Tuning.CellSize * FixedQ4816.FromInteger(value: x)),
                Y: (Tuning.CellSize * FixedQ4816.FromInteger(value: y)),
                Z: (Tuning.CellSize * FixedQ4816.FromInteger(value: z))
            );

            return (Tuning.Origin + ((Tuning.YawRadians == FixedQ4816.Zero)
                ? local
                : m_rotation.Rotate(vector: local)));
        }

        public bool TryCell(in FixedVector3 position, out int node) => (TryIndex(node: out node, position: in position) && IsWalkable(node: node));
        /// <summary>Returns whether a world position rounds to a cell inside this domain's footprint, walkable or not.
        /// Reading it never bakes.</summary>
        /// <param name="position">The world position; a surface domain reads only its X and Z.</param>
        public bool Covers(in FixedVector3 position) => TryIndex(node: out _, position: in position);
        // The cell a world position rounds to, without the walkability a bake decides.
        private bool TryIndex(in FixedVector3 position, out int node) {
            var local = ((Tuning.YawRadians == FixedQ4816.Zero)
                ? position
                : (Tuning.Origin + m_inverseRotation.Rotate(vector: (position - Tuning.Origin)))
            );
            var x = RoundedCell(
                value: (((Int128)local.X.Value) - Tuning.Origin.X.Value),
                cellSize: Tuning.CellSize.Value
            );
            var z = RoundedCell(
                value: (((Int128)local.Z.Value) - Tuning.Origin.Z.Value),
                cellSize: Tuning.CellSize.Value
            );
            var y = ((Tuning.Kind == NavigationKind.Surface)
                ? 0
                : RoundedCell(
                    value: (((Int128)local.Y.Value) - Tuning.Origin.Y.Value),
                    cellSize: Tuning.CellSize.Value
                )
            );

            if (
                (x < 0) ||
                (x >= Tuning.Width) ||
                (y < 0) ||
                (y >= Tuning.Layers) ||
                (z < 0) ||
                (z >= Tuning.Depth)
            ) {
                node = -1;
                return false;
            }
            node = Index(
                x: ((int)x),
                y: ((int)y),
                z: ((int)z)
            );
            return true;
        }
        /// <summary>Searches one route between two cells of this domain on its own: a bounded A* over the baked edges,
        /// within the domain's expansion and path budgets.</summary>
        /// <param name="start">The start cell.</param>
        /// <param name="goal">The goal cell.</param>
        /// <param name="path">Receives the route's cells from start to goal.</param>
        /// <param name="pathLength">The route's cell count, zero when no route is returned.</param>
        /// <param name="expanded">The nodes the search settled.</param>
        /// <returns><see cref="NavigationStatus.Active"/> with a route, <see cref="NavigationStatus.Arrived"/> when the
        /// start is the goal, or why no route was returned.</returns>
        public NavigationStatus FindPath(int start, int goal, Span<int> path, out int pathLength, out int expanded) {
            var status = SearchPath(expanded: out expanded, goal: goal, path: path, pathLength: out pathLength, start: start);

            m_work.Add(amount: 1, kind: NavigationSearchWork.Searches);
            m_work.Add(amount: expanded, kind: NavigationSearchWork.SearchExpansions);
            return status;
        }
        private NavigationStatus SearchPath(int start, int goal, Span<int> path, out int pathLength, out int expanded) {
            pathLength = 0;
            expanded = 0;
            if (
                !IsWalkable(node: start) ||
                !IsWalkable(node: goal)
            ) {
                return NavigationStatus.OutsideDomain;
            }
            if (start == goal) {
                path[0] = start;
                pathLength = 1;
                return NavigationStatus.Arrived;
            }

            var workspace = m_searches.Rent();

            try {
                var order = new SearchOrder(workspace: workspace);

                workspace.Begin();
                Open(cost: 0, goal: goal, node: start, order: order, parent: -1, workspace: workspace);
                while (workspace.Open.Count != 0) {
                    var current = workspace.Open.Pop(order: order);

                    workspace.ClosedStamp[current] = workspace.Stamp;
                    expanded++;
                    if (current == goal) {
                        return Reconstruct(goal: goal, path: path, pathLength: out pathLength, workspace: workspace);
                    }
                    if (expanded >= Tuning.MaxExpandedNodes) {
                        return NavigationStatus.SearchLimit;
                    }

                    foreach (var step in Neighbors(node: current)) {
                        var next = step.Node;

                        if (
                            !CanTraverse(
                            current: current,
                            dx: step.Dx,
                            dy: step.Dy,
                            dz: step.Dz,
                            next: next,
                            x: step.X,
                            y: step.Y,
                            z: step.Z
                        ) ||
                            (workspace.ClosedStamp[next] == workspace.Stamp)
                        ) {
                            continue;
                        }
                        var nextCost = checked((workspace.Cost[current] + step.Cost));

                        if (
                            (workspace.OpenStamp[next] != workspace.Stamp) ||
                            (nextCost < workspace.Cost[next])
                        ) {
                            Open(cost: nextCost, goal: goal, node: next, order: order, parent: current, workspace: workspace);
                        }
                    }
                }
                return NavigationStatus.Unreachable;
            } finally {
                m_searches.Return(item: workspace);
            }
        }
        // Builds a workspace for a search that found none idle, counting its bytes into WorkspaceBytes.
        private SearchWorkspace NewSearch() {
            var created = new SearchWorkspace(cells: CellCount);

            _ = Interlocked.Add(location1: ref m_searchBytes, value: created.Bytes);
            return created;
        }
        private bool AdmitsAxes(int axes) => ((Tuning.Kind == NavigationKind.Surface) || Tuning.Connectivity switch {
            NavigationConnectivity.Axis => (axes == 1),
            NavigationConnectivity.FacesAndEdges => (axes <= 2),
            _ => true,
        });
        private bool CanTraverse(int current, int next, int x, int y, int z, int dx, int dy, int dz) {
            if (
                ((m_edges[current] & NeighborBit(
                dx: dx,
                dy: dy,
                dz: dz
            )) == 0U) ||
                !IsWalkable(node: next)
            ) {
                return false;
            }
            var axes = StepAxes(
                dx: dx,
                dy: dy,
                dz: dz
            );

            if (
                (Tuning.Kind == NavigationKind.Medium) &&
                (axes > 1)
            ) {
                if (
                    (dx != 0) &&
                    !IsWalkable(node: Index(
                    x: (x + dx),
                    y: y,
                    z: z
                ))
                ) {
                    return false;
                }
                if (
                    (dy != 0) &&
                    !IsWalkable(node: Index(
                    x: x,
                    y: (y + dy),
                    z: z
                ))
                ) {
                    return false;
                }
                if (
                    (dz != 0) &&
                    !IsWalkable(node: Index(
                    x: x,
                    y: y,
                    z: (z + dz)
                ))
                ) {
                    return false;
                }
            }
            if (
                (Tuning.Kind == NavigationKind.Medium) &&
                ((m_fields is null) || !m_fields.IsSegmentInsideMedium(
                clearance: Tuning.AgentRadius,
                field: m_mediumField,
                from: Position(node: current),
                to: Position(node: next),
                maximumSubdivisions: m_capacity.MaxMediumSegmentSubdivisions
            ))
            ) {
                return false;
            }
            return true;
        }

        public bool IsTraversableEdge(int current, int next) {
            EnsureBaked();
            if (
                (((uint)current) >= ((uint)CellCount)) ||
                (((uint)next) >= ((uint)CellCount)) ||
                (current == next)
            ) {
                return false;
            }
            Coordinates(
                node: current,
                x: out var x,
                y: out var y,
                z: out var z
            );
            Coordinates(
                node: next,
                x: out var nx,
                y: out var ny,
                z: out var nz
            );
            var dx = (nx - x);
            var dy = (ny - y);
            var dz = (nz - z);

            if (
                (Math.Abs(value: dx) > 1) ||
                (Math.Abs(value: dy) > 1) ||
                (Math.Abs(value: dz) > 1)
            ) {
                return false;
            }
            return CanTraverse(
                current: current,
                dx: dx,
                dy: dy,
                dz: dz,
                next: next,
                x: x,
                y: y,
                z: z
            );
        }

        private static uint NeighborBit(int dx, int dy, int dz) => (1U << NeighborSlot(dx: dx, dy: dy, dz: dz));
        // A step's place among the 26 around a cell, layer then depth then column, skipping the cell itself (13): the bit
        // of a baked edge mask and a goal field's step.
        private static byte NeighborSlot(int dx, int dy, int dz) {
            var ordinal = ((((dy + 1) * 9) + ((dz + 1) * 3)) + (dx + 1));

            return (byte)((ordinal > 13) ? (ordinal - 1) : ordinal);
        }
        private static (int Dx, int Dy, int Dz) SlotOffset(byte slot) {
            var ordinal = ((slot >= 13) ? (slot + 1) : slot);

            return (((ordinal % 3) - 1), ((ordinal / 9) - 1), (((ordinal / 3) % 3) - 1));
        }
        private int Heuristic(int node, int goal) {
            Coordinates(
                node: node,
                x: out var nx,
                y: out var ny,
                z: out var nz
            );
            Coordinates(
                node: goal,
                x: out var gx,
                y: out var gy,
                z: out var gz
            );
            return checked((Math.Max(
                val1: Math.Abs(value: (nx - gx)),
                val2: Math.Max(
                    val1: Math.Abs(value: (ny - gy)),
                    val2: Math.Abs(value: (nz - gz))
                )
            ) * StraightCost));
        }

        public bool IsWalkable(int node) {
            EnsureBaked();
            if (
                (((uint)node) >= ((uint)m_walkable.Length)) ||
                !m_walkable[node]
            ) {
                return false;
            }
            if (Tuning.Kind != NavigationKind.Medium) {
                return true;
            }
            return (
                (m_fields is not null) &&
                m_fields.IsInsideMedium(
                field: m_mediumField,
                position: Position(node: node),
                clearance: Tuning.AgentRadius
            )
            );
        }

        private int Index(int x, int y, int z) => ((((y * Tuning.Depth) + z) * Tuning.Width) + x);
        private void Coordinates(int node, out int x, out int y, out int z) {
            x = (node % Tuning.Width);
            var yz = (node / Tuning.Width);

            z = (yz % Tuning.Depth);
            y = (yz / Tuning.Depth);
        }
        // The heuristic is stored as the node opens, so the search orders by f = g + h, then the smaller h, then the lower
        // index (SearchOrder): a total order, so the heap never breaks a tie itself.
        private void Open(int node, int cost, int parent, int goal, SearchOrder order, SearchWorkspace workspace) {
            workspace.Cost[node] = cost;
            workspace.Parent[node] = parent;
            workspace.Heuristic[node] = Heuristic(goal: goal, node: node);
            if (workspace.OpenStamp[node] != workspace.Stamp) {
                workspace.OpenStamp[node] = workspace.Stamp;
                workspace.Open.Push(node: node, order: order);
            } else {
                workspace.Open.Decrease(node: node, order: order);
            }
        }
        private NavigationStatus Reconstruct(int goal, Span<int> path, out int pathLength, SearchWorkspace workspace) {
            pathLength = 0;
            for (var node = goal; (node >= 0); node = workspace.Parent[node]) {
                if (
                    (pathLength >= path.Length) ||
                    (pathLength >= Tuning.MaxPathNodes)
                ) {
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
            var quotient = (numerator / denominator);
            var remainder = (numerator % denominator);

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
