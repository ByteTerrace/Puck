using Puck.Maths;

namespace Puck.Physics.Navigation;

/// <summary>Canonical discovered-node state of a shared destination tree. Heap layout is derived, not persisted.</summary>
public readonly record struct NavigationTreeNode(int Node, int Cost, int Next, bool Settled);
/// <summary>One resident destination tree and requests queued for the next navigation step. Age is the
/// unique recency rank among resident trees: zero is newest, not an elapsed-time counter.</summary>
public sealed record NavigationTreeCheckpoint(int Goal, int Age, NavigationTreeNode[] Nodes, int[] Pending);
/// <summary>A domain's shared search scheduler and resident destination trees, in stable slot order.</summary>
public sealed record NavigationSharedCheckpoint(int Cursor, NavigationTreeCheckpoint[] Trees);
public sealed partial class NavigationRuntime {
    public void BeginStep() {
        foreach (var domain in m_domains) { domain.AdvanceShared(); }
    }
    public NavigationSharedCheckpoint[] CaptureShared() => m_domains.Select(selector: domain => domain.CaptureShared()).ToArray();
    public void ValidateShared(NavigationSharedCheckpoint[]? checkpoints) {
        if (checkpoints is null) {
            if (m_domains.Any(predicate: domain => (domain.Sharing is not null))) {
                throw new InvalidOperationException(message: "population checkpoint omits shared navigation state.");
            }
            return;
        }
        if (checkpoints.Length != Count) { throw new InvalidOperationException(message: "shared navigation checkpoint domain count differs."); }
        for (var index = 0; (index < Count); index++) { m_domains[index].ValidateShared(checkpoint: checkpoints[index]); }
    }
    public void RestoreShared(NavigationSharedCheckpoint[]? checkpoints) {
        ValidateShared(checkpoints: checkpoints);
        if (checkpoints is null) { return; }
        for (var index = 0; (index < Count); index++) { m_domains[index].RestoreShared(checkpoint: checkpoints[index]); }
    }
    public void AppendSharedHash(ref Fnv1aHash hash) {
        foreach (var domain in m_domains) { domain.AppendSharedHash(hash: ref hash); }
    }

    public sealed partial class Domain {
        private SharedTree[] m_sharedTrees = [];
        private int m_sharedCursor;
        private ulong m_sharedFieldRevision;

        public NavigationSharing? Sharing { get; private set; }
        public int SharedExpandedLast { get; private set; }
        public int SharedPathsLast { get; private set; }
        public int SharedCapacityRefusalsLast { get; private set; }
        public int SharedResidentGoals => m_sharedTrees.Count(predicate: tree => (tree.Goal >= 0));

        // Five cell-sized int arrays, one bool array, two bounded pending-cell lists and derived hash blocks.
        private long SharedWorkspaceBytes => (((long)m_sharedTrees.Length) *
            (((CellCount * ((5L * sizeof(int)) + sizeof(byte))) + ((Math.Min(val1: CellCount, val2: m_capacity.MaxConcurrentRequesters) * 2L) * sizeof(int)))
            + (SharedTree.HashBlockCount(cells: CellCount) * (sizeof(ulong) + sizeof(byte)))));
        private ulong MediumRevision => ((m_mediumField >= 0) ? m_fields!.ValueRevision(field: m_mediumField) : 0);
        private bool SharedStale => ((Tuning.Kind == NavigationKind.Medium) && (m_sharedFieldRevision != MediumRevision));

        private void InitializeSharing() {
            m_sharedTrees = new SharedTree[(Sharing?.GoalCapacity ?? 0)];
            for (var index = 0; (index < m_sharedTrees.Length); index++) { m_sharedTrees[index] = new SharedTree(cells: CellCount, maxConcurrentRequesters: m_capacity.MaxConcurrentRequesters); }
            m_sharedFieldRevision = MediumRevision;
        }
        private void SynchronizeSharedGraph() {
            if (!SharedStale) { return; }
            foreach (var tree in m_sharedTrees) { tree.Reset(goal: -1); }
            m_sharedCursor = 0;
            m_sharedFieldRevision = MediumRevision;
        }

        public void AdvanceShared() {
            SharedExpandedLast = SharedPathsLast = SharedCapacityRefusalsLast = 0;
            if (Sharing is null) { return; }
            SynchronizeSharedGraph();
            foreach (var tree in m_sharedTrees) { tree.PinnedForStep = (tree.PendingCount != 0); }
            var idleSlots = 0;

            while ((SharedExpandedLast < Sharing.ExpandedNodesPerTick) && (idleSlots < m_sharedTrees.Length)) {
                var tree = m_sharedTrees[m_sharedCursor];

                m_sharedCursor = ((m_sharedCursor + 1) % m_sharedTrees.Length);
                if (tree.NeedsWork && tree.Expand(domain: this)) {
                    SharedExpandedLast++;
                    idleSlots = 0;
                } else { idleSlots++; }
            }
            // Requests are renewed by still-interested bodies below. A canceled requester can consume at most
            // this one already-queued step; it cannot keep an abandoned search running indefinitely.
            foreach (var tree in m_sharedTrees) {
                // Keep last step's requests pinned through this whole delivery phase, even if they just
                // finished. A lower-index body must not evict the answer before its requester gets a turn.
                tree.ClearPending();
            }
        }
        public NavigationStatus RequestShared(int start, int goal, Span<int> path, out int length) {
            length = 0;
            SynchronizeSharedGraph();
            if (!IsWalkable(node: start) || !IsWalkable(node: goal)) { return NavigationStatus.OutsideDomain; }
            if (start == goal) { path[0] = start; length = 1; return NavigationStatus.Arrived; }
            SharedTree? selected = null;
            SharedTree? victim = null;

            foreach (var tree in m_sharedTrees) {
                if (tree.Goal == goal) { selected = tree; break; }
                if (!tree.PinnedForStep && (tree.PendingCount == 0) && ((victim is null) || (tree.Goal < 0) || ((victim.Goal >= 0) && (tree.Age > victim.Age)))) { victim = tree; }
            }
            if (selected is null) {
                if (victim is null) { SharedCapacityRefusalsLast++; return NavigationStatus.CapacityLimited; }
                selected = victim;
                var previousAge = ((selected.Goal >= 0) ? selected.Age : m_sharedTrees.Length);

                selected.Reset(goal: goal);
                selected.Age = previousAge;
            }
            foreach (var tree in m_sharedTrees) {
                // Ages are recency ranks, not saturated request counters. Repeatedly touching the newest tree
                // must not collapse every other age into a tie and evict a more recently used destination.
                if ((tree.Goal >= 0) && (tree != selected) && (tree.Age < selected.Age)) { tree.Age++; }
            }
            selected.Age = 0;
            var status = selected.ReadPath(start, path[..Math.Min(val1: path.Length, val2: Tuning.MaxPathNodes)], out length);

            if (length != 0) { SharedPathsLast++; }
            return status;
        }

        private readonly record struct SharedEdge(int Node, int Cost);

        private int Predecessors(int node, Span<SharedEdge> edges) {
            Coordinates(node: node, x: out var x, y: out var y, z: out var z);
            var count = 0;

            for (var dy = ((Tuning.Kind == NavigationKind.Surface) ? 0 : -1); (dy <= ((Tuning.Kind == NavigationKind.Surface) ? 0 : 1)); dy++) {
                for (var dz = -1; (dz <= 1); dz++) {
                    for (var dx = -1; (dx <= 1); dx++) {
                        var axes = ((((dx == 0) ? 0 : 1) + ((dy == 0) ? 0 : 1)) + ((dz == 0) ? 0 : 1));
                        var nx = (x + dx); var ny = (y + dy); var nz = (z + dz);

                        if ((axes == 0) || !AdmitsAxes(axes: axes) || (((uint)nx) >= ((uint)Tuning.Width)) ||
                            (((uint)ny) >= ((uint)Tuning.Layers)) || (((uint)nz) >= ((uint)Tuning.Depth))) { continue; }
                        var next = Index(x: nx, y: ny, z: nz);
                        // Reverse search: prove predecessor -> settled node, not an assumed directed reverse edge.
                        if (IsTraversableEdge(current: next, next: node)) {
                            edges[count++] = new SharedEdge(Cost: ((axes == 1) ? StraightCost : ((axes == 2) ? DiagonalCost : SpaceDiagonalCost)), Node: next);
                        }
                    }
                }
            }
            return count;
        }

        public NavigationSharedCheckpoint CaptureShared() => new(Cursor: (SharedStale ? 0 : m_sharedCursor),
            Trees: m_sharedTrees.Select(selector: tree => (SharedStale ? new NavigationTreeCheckpoint(Age: 0, Goal: -1, Nodes: [], Pending: []) : tree.Capture())).ToArray());
        public void ValidateShared(NavigationSharedCheckpoint checkpoint) {
            EnsureBaked();
            if ((checkpoint is null) || (checkpoint.Trees is null) || (checkpoint.Trees.Length != m_sharedTrees.Length) ||
                (checkpoint.Cursor < 0) || (checkpoint.Cursor >= Math.Max(val1: 1, val2: m_sharedTrees.Length))) {
                throw new InvalidOperationException(message: "shared navigation checkpoint scheduler shape differs.");
            }
            var goals = new HashSet<int>();
            var ages = new HashSet<int>();

            foreach (var tree in checkpoint.Trees) {
                if ((tree is null) || (tree.Goal < -1) || (tree.Goal >= CellCount) || (tree.Age < 0) || (tree.Age >= m_sharedTrees.Length) ||
                    (tree.Nodes is null) || (tree.Nodes.Length > CellCount) || (tree.Pending is null) ||
                    (tree.Pending.Length > Math.Min(val1: CellCount, val2: m_capacity.MaxConcurrentRequesters))) {
                    throw new InvalidOperationException(message: "shared navigation checkpoint tree shape differs.");
                }
                if (tree.Goal < 0) {
                    if ((tree.Age != 0) || (tree.Nodes.Length != 0) || (tree.Pending.Length != 0)) {
                        throw new InvalidOperationException(message: "shared navigation checkpoint empty slot carries work.");
                    }
                    continue;
                }
                if (!goals.Add(item: tree.Goal)) { throw new InvalidOperationException(message: "shared navigation checkpoint repeats a goal."); }
                if (!ages.Add(item: tree.Age)) { throw new InvalidOperationException(message: "shared navigation checkpoint repeats a recency rank."); }
                var nodes = new Dictionary<int, NavigationTreeNode>(capacity: tree.Nodes.Length);
                var previous = -1;

                foreach (var node in tree.Nodes) {
                    if ((node.Node <= previous) || (node.Node >= CellCount) || (node.Cost < 0) || (node.Cost > (CellCount * SpaceDiagonalCost))) {
                        throw new InvalidOperationException(message: "shared navigation checkpoint has invalid discovered nodes.");
                    }
                    nodes.Add(key: node.Node, value: node);
                    previous = node.Node;
                }
                if (!nodes.TryGetValue(key: tree.Goal, value: out var root) || (root.Cost != 0) || (root.Next != -1)) {
                    throw new InvalidOperationException(message: "shared navigation checkpoint lacks its zero-cost root.");
                }
                foreach (var node in tree.Nodes) {
                    if (node.Node == tree.Goal) { continue; }
                    if (!nodes.TryGetValue(key: node.Next, value: out var next) || !next.Settled || (next.Cost >= node.Cost)) {
                        throw new InvalidOperationException(message: "shared navigation checkpoint successor must be settled at a lower cost.");
                    }
                    Coordinates(node: node.Node, x: out var x, y: out var y, z: out var z);
                    Coordinates(node: next.Node, x: out var nx, y: out var ny, z: out var nz);
                    var dx = (nx - x); var dy = (ny - y); var dz = (nz - z);
                    var axes = ((((dx == 0) ? 0 : 1) + ((dy == 0) ? 0 : 1)) + ((dz == 0) ? 0 : 1));

                    if ((Math.Abs(value: dx) > 1) || (Math.Abs(value: dy) > 1) || (Math.Abs(value: dz) > 1) ||
                        ((m_edges[node.Node] & NeighborBit(dx: dx, dy: dy, dz: dz)) == 0) ||
                        ((node.Cost - next.Cost) != ((axes == 1) ? StraightCost : ((axes == 2) ? DiagonalCost : SpaceDiagonalCost)))) {
                        throw new InvalidOperationException(message: "shared navigation checkpoint successor is not a costed static edge.");
                    }
                }
                previous = -1;
                foreach (var pending in tree.Pending) {
                    if ((pending <= previous) || (pending >= CellCount) || (nodes.TryGetValue(key: pending, value: out var node) && node.Settled)) {
                        throw new InvalidOperationException(message: "shared navigation checkpoint has invalid pending starts.");
                    }
                    previous = pending;
                }
            }
            if (ages.Any(predicate: age => (age >= goals.Count))) {
                throw new InvalidOperationException(message: "shared navigation checkpoint recency ranks must be contiguous from zero.");
            }
        }
        public void RestoreShared(NavigationSharedCheckpoint checkpoint) {
            m_sharedCursor = checkpoint.Cursor;
            m_sharedFieldRevision = MediumRevision;
            for (var index = 0; (index < m_sharedTrees.Length); index++) { m_sharedTrees[index].Restore(state: checkpoint.Trees[index]); }
            SharedExpandedLast = SharedPathsLast = SharedCapacityRefusalsLast = 0;
        }
        public void AppendSharedHash(ref Fnv1aHash hash) {
            // A changed medium invalidates every old tree before its next use. Canonicalize that dead state now,
            // so checkpointing after a field edit does not require retaining an obsolete copy of the old lattice.
            var stale = SharedStale;

            hash.Add(value: m_sharedTrees.Length);
            hash.Add(value: (stale ? 0 : m_sharedCursor));
            foreach (var tree in m_sharedTrees) { tree.AppendHash(empty: stale, hash: ref hash); }
        }

        private sealed class SharedTree {
            // Representation granularity, not an author policy. Only changed blocks rehash; a settled tree's
            // digest is O(1) per authoritative hash, independent of the admitted graph capacity.
            private const int HashBlockSize = 64;

            private readonly int[] m_cost;
            private readonly int[] m_next;
            private readonly NodeHeap m_open;
            private readonly int[] m_stamp;
            private readonly bool[] m_pending;
            private readonly int[] m_pendingList;
            private readonly int[] m_pendingOrdered;
            private readonly ulong[] m_blockHashes;
            private readonly bool[] m_dirtyBlocks;

            private bool m_nodesDirty = true;
            private ulong m_nodesHash;
            private bool m_pendingDirty = true;
            private ulong m_pendingHash;
            private int m_pendingLength;
            private int m_generation;

            public int Age { get; set; }
            public int Goal { get; private set; } = -1;
            public int PendingCount { get; private set; }
            // Tick-local reservation, overwritten before body reads; not independent checkpoint/hash state.
            public bool PinnedForStep { get; set; }
            public bool NeedsWork => ((PendingCount != 0) && (m_open.Count != 0));

            public SharedTree(int cells, int maxConcurrentRequesters) {
                m_cost = new int[cells]; m_next = new int[cells]; m_open = new NodeHeap(cells: cells);
                m_stamp = new int[cells]; m_pending = new bool[cells];
                m_pendingList = new int[Math.Min(val1: cells, val2: maxConcurrentRequesters)];
                m_pendingOrdered = new int[m_pendingList.Length];
                m_blockHashes = new ulong[HashBlockCount(cells: cells)];
                m_dirtyBlocks = new bool[m_blockHashes.Length];
            }

            public static int HashBlockCount(int cells) => cells.CeilingDivide(divisor: HashBlockSize);
            public void Reset(int goal) {
                ClearPending();
                if (++m_generation == int.MaxValue) { Array.Clear(array: m_stamp); m_generation = 1; }
                Goal = goal; Age = 0; m_open.Clear(); PinnedForStep = false;
                // Every undiscovered block has the same empty digest. Do not scan all cells just because a
                // new goal reused this slot; Open/Restore mark only blocks that acquire discovered nodes.
                Array.Fill(array: m_blockHashes, value: Fnv1aHash.Create().Value);
                Array.Clear(array: m_dirtyBlocks);
                m_nodesDirty = true;
                if (goal >= 0) { Open(cost: 0, next: -1, node: goal); }
            }
            public void ClearPending() {
                for (var index = 0; (index < m_pendingLength); index++) { m_pending[m_pendingList[index]] = false; }
                m_pendingLength = PendingCount = 0;
                m_pendingDirty = true;
            }

            // Discovered this generation and no longer queued: popped by Expand, or restored as settled.
            private bool Settled(int node) => ((m_stamp[node] == m_generation) && !m_open.Contains(node: node));
            private void Queue(int node) {
                if (m_pending[node]) { return; }
                m_pending[node] = true;
                m_pendingList[m_pendingLength++] = node;
                PendingCount++;
                m_pendingDirty = true;
            }

            public NavigationStatus ReadPath(int start, Span<int> path, out int length) {
                length = 0;
                if (!Settled(node: start)) {
                    if (m_open.Count == 0) { return NavigationStatus.Unreachable; }
                    Queue(node: start);
                    return NavigationStatus.Pending;
                }
                for (var node = start; (node >= 0); node = m_next[node]) {
                    if (length == path.Length) { length = 0; return NavigationStatus.PathLimit; }
                    path[length++] = node;
                }
                return NavigationStatus.Active;
            }
            public bool Expand(Domain domain) {
                if (m_open.Count == 0) { return false; }
                var node = m_open.Pop(order: new CostOrder(cost: m_cost));

                InvalidateNodeHash(node: node);
                if (m_pending[node]) { m_pending[node] = false; PendingCount--; m_pendingDirty = true; }
                Span<SharedEdge> edges = stackalloc SharedEdge[26];
                var count = domain.Predecessors(edges: edges, node: node);

                for (var index = 0; (index < count); index++) {
                    var edge = edges[index];
                    var cost = checked((m_cost[node] + edge.Cost));

                    if (!Settled(node: edge.Node) && ((m_stamp[edge.Node] != m_generation) || (cost < m_cost[edge.Node]))) {
                        Open(edge.Node, cost, node);
                    }
                }
                return true;
            }

            // Reverse Dijkstra from the goal: cost, then the lower index — a total order, so the heap never breaks
            // a tie itself.
            private readonly struct CostOrder(int[] cost) : INodeOrder {
                public int Compare(int left, int right) {
                    var byCost = cost[left].CompareTo(value: cost[right]);

                    return ((byCost != 0) ? byCost : left.CompareTo(value: right));
                }
            }

            private void Open(int node, int cost, int next) {
                m_cost[node] = cost; m_next[node] = next;
                InvalidateNodeHash(node: node);
                var order = new CostOrder(cost: m_cost);

                if (m_stamp[node] != m_generation) {
                    m_stamp[node] = m_generation; m_open.Push(node: node, order: order);
                } else { m_open.Decrease(node: node, order: order); }
            }

            public NavigationTreeCheckpoint Capture() {
                var nodes = new List<NavigationTreeNode>();
                var pending = new List<int>();

                if (Goal >= 0) {
                    for (var node = 0; (node < m_stamp.Length); node++) {
                        if (m_stamp[node] == m_generation) { nodes.Add(item: new(node, m_cost[node], m_next[node], Settled(node: node))); }
                        if (m_pending[node]) { pending.Add(item: node); }
                    }
                }
                return new(Goal, Age, nodes.ToArray(), pending.ToArray());
            }
            public void Restore(NavigationTreeCheckpoint state) {
                Reset(goal: -1);
                Goal = state.Goal; Age = state.Age;
                foreach (var node in state.Nodes) {
                    if (node.Settled) {
                        m_stamp[node.Node] = m_generation; m_open.Forget(node: node.Node);
                        m_cost[node.Node] = node.Cost; m_next[node.Node] = node.Next;
                        InvalidateNodeHash(node: node.Node);
                    } else { Open(node.Node, node.Cost, node.Next); }
                }
                foreach (var node in state.Pending) { Queue(node: node); }
            }
            public void AppendHash(ref Fnv1aHash hash, bool empty) {
                hash.Add(value: (empty ? -1 : Goal)); hash.Add(value: (empty ? 0 : Age));
                if (empty || (Goal < 0)) { return; }
                if (m_nodesDirty) {
                    var nodesHash = Fnv1aHash.Create();

                    nodesHash.Add(value: m_stamp.Length);
                    for (var block = 0; (block < m_blockHashes.Length); block++) {
                        if (m_dirtyBlocks[block]) {
                            var blockHash = Fnv1aHash.Create();
                            var end = Math.Min(val1: ((block + 1) * HashBlockSize), val2: m_stamp.Length);

                            for (var node = (block * HashBlockSize); (node < end); node++) {
                                if (m_stamp[node] != m_generation) { continue; }
                                blockHash.Add(value: node); blockHash.Add(value: m_cost[node]); blockHash.Add(value: m_next[node]);
                                blockHash.Add(value: ((byte)(Settled(node: node) ? 1 : 0)));
                            }
                            m_blockHashes[block] = blockHash.Value;
                            m_dirtyBlocks[block] = false;
                        }
                        nodesHash.Add(value: m_blockHashes[block]);
                    }
                    m_nodesHash = nodesHash.Value;
                    m_nodesDirty = false;
                }
                if (m_pendingDirty) {
                    var count = 0;

                    for (var index = 0; (index < m_pendingLength); index++) {
                        var node = m_pendingList[index];

                        if (m_pending[node]) { m_pendingOrdered[count++] = node; }
                    }
                    var pending = m_pendingOrdered.AsSpan(length: count, start: 0);

                    pending.Sort();
                    var pendingHash = Fnv1aHash.Create();

                    pendingHash.Add(value: count);
                    foreach (var node in pending) { pendingHash.Add(value: node); }
                    m_pendingHash = pendingHash.Value;
                    m_pendingDirty = false;
                }
                hash.Add(value: m_nodesHash);
                hash.Add(value: m_pendingHash);
            }

            private void InvalidateNodeHash(int node) {
                m_dirtyBlocks[(node / HashBlockSize)] = true;
                m_nodesDirty = true;
            }
        }
    }
}
