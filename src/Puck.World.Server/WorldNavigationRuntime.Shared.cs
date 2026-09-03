using Puck.Maths;

namespace Puck.World.Server;

/// <summary>Canonical discovered-node state of a shared destination tree. Heap layout is derived, not persisted.</summary>
public readonly record struct WorldNavigationTreeNode(int Node, int Cost, int Next, bool Settled);

/// <summary>One resident destination tree and requests queued for the next navigation step. Age is the
/// unique recency rank among resident trees: zero is newest, not an elapsed-time counter.</summary>
public sealed record WorldNavigationTreeCheckpoint(int Goal, int Age, WorldNavigationTreeNode[] Nodes, int[] Pending);

/// <summary>A domain's shared search scheduler and resident destination trees, in stable slot order.</summary>
public sealed record WorldNavigationSharedCheckpoint(int Cursor, WorldNavigationTreeCheckpoint[] Trees);

internal sealed partial class WorldNavigationRuntime {
    public void BeginStep() {
        foreach (var domain in m_domains) { domain.AdvanceShared(); }
    }

    public WorldNavigationSharedCheckpoint[] CaptureShared() => m_domains.Select(domain => domain.CaptureShared()).ToArray();

    public void ValidateShared(WorldNavigationSharedCheckpoint[]? checkpoints) {
        if (checkpoints is null) {
            if (m_domains.Any(domain => domain.Sharing is not null)) {
                throw new InvalidOperationException("population checkpoint omits shared navigation state.");
            }
            return;
        }
        if (checkpoints.Length != Count) { throw new InvalidOperationException("shared navigation checkpoint domain count differs."); }
        for (var index = 0; index < Count; index++) { m_domains[index].ValidateShared(checkpoints[index]); }
    }

    public void RestoreShared(WorldNavigationSharedCheckpoint[]? checkpoints) {
        ValidateShared(checkpoints);
        if (checkpoints is null) { return; }
        for (var index = 0; index < Count; index++) { m_domains[index].RestoreShared(checkpoints[index]); }
    }

    public void AppendSharedHash(ref Fnv1aHash hash) {
        foreach (var domain in m_domains) { domain.AppendSharedHash(ref hash); }
    }

    internal sealed partial class Domain {
        private SharedTree[] m_sharedTrees = [];
        private int m_sharedCursor;
        private ulong m_sharedFieldRevision;
        public WorldNavigationSharing? Sharing { get; private set; }
        public int SharedExpandedLast { get; private set; }
        public int SharedPathsLast { get; private set; }
        public int SharedCapacityRefusalsLast { get; private set; }
        public int SharedResidentGoals => m_sharedTrees.Count(tree => tree.Goal >= 0);
        // Five cell-sized int arrays, one bool array, two bounded pending-cell lists and derived hash blocks.
        private long SharedWorkspaceBytes => (long)m_sharedTrees.Length *
            (CellCount * (5L * sizeof(int) + sizeof(byte)) + Math.Min(CellCount, WorldBodiesLimits.CapacityCeiling) * 2L * sizeof(int)
            + SharedTree.HashBlockCount(CellCount) * (sizeof(ulong) + sizeof(byte)));
        private ulong MediumRevision => m_mediumField >= 0 ? m_fields!.ValueRevision(m_mediumField) : 0;
        private bool SharedStale => Tuning.Kind == WorldNavigationKind.Medium && m_sharedFieldRevision != MediumRevision;

        private void InitializeSharing() {
            m_sharedTrees = new SharedTree[Sharing?.GoalCapacity ?? 0];
            for (var index = 0; index < m_sharedTrees.Length; index++) { m_sharedTrees[index] = new SharedTree(CellCount); }
            m_sharedFieldRevision = MediumRevision;
        }

        private void SynchronizeSharedGraph() {
            if (!SharedStale) { return; }
            foreach (var tree in m_sharedTrees) { tree.Reset(-1); }
            m_sharedCursor = 0;
            m_sharedFieldRevision = MediumRevision;
        }

        public void AdvanceShared() {
            SharedExpandedLast = SharedPathsLast = SharedCapacityRefusalsLast = 0;
            if (Sharing is null) { return; }
            SynchronizeSharedGraph();
            foreach (var tree in m_sharedTrees) { tree.PinnedForStep = tree.PendingCount != 0; }
            var idleSlots = 0;
            while (SharedExpandedLast < Sharing.ExpandedNodesPerTick && idleSlots < m_sharedTrees.Length) {
                var tree = m_sharedTrees[m_sharedCursor];
                m_sharedCursor = (m_sharedCursor + 1) % m_sharedTrees.Length;
                if (tree.NeedsWork && tree.Expand(this)) {
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

        public WorldNavigationStatus RequestShared(int start, int goal, Span<int> path, out int length) {
            length = 0;
            SynchronizeSharedGraph();
            if (!IsWalkable(start) || !IsWalkable(goal)) { return WorldNavigationStatus.OutsideDomain; }
            if (start == goal) { path[0] = start; length = 1; return WorldNavigationStatus.Arrived; }
            SharedTree? selected = null;
            SharedTree? victim = null;
            foreach (var tree in m_sharedTrees) {
                if (tree.Goal == goal) { selected = tree; break; }
                if (!tree.PinnedForStep && tree.PendingCount == 0 && (victim is null || tree.Goal < 0 || (victim.Goal >= 0 && tree.Age > victim.Age))) { victim = tree; }
            }
            if (selected is null) {
                if (victim is null) { SharedCapacityRefusalsLast++; return WorldNavigationStatus.CapacityLimited; }
                selected = victim;
                var previousAge = selected.Goal >= 0 ? selected.Age : m_sharedTrees.Length;
                selected.Reset(goal);
                selected.Age = previousAge;
            }
            foreach (var tree in m_sharedTrees) {
                // Ages are recency ranks, not saturated request counters. Repeatedly touching the newest tree
                // must not collapse every other age into a tie and evict a more recently used destination.
                if (tree.Goal >= 0 && tree != selected && tree.Age < selected.Age) { tree.Age++; }
            }
            selected.Age = 0;
            var status = selected.ReadPath(start, path[..Math.Min(path.Length, Tuning.MaxPathNodes)], out length);
            if (length != 0) { SharedPathsLast++; }
            return status;
        }

        private readonly record struct SharedEdge(int Node, int Cost);

        private int Predecessors(int node, Span<SharedEdge> edges) {
            Coordinates(node, out var x, out var y, out var z);
            var count = 0;
            for (var dy = Tuning.Kind == WorldNavigationKind.Surface ? 0 : -1; dy <= (Tuning.Kind == WorldNavigationKind.Surface ? 0 : 1); dy++) {
                for (var dz = -1; dz <= 1; dz++) {
                    for (var dx = -1; dx <= 1; dx++) {
                        var axes = (dx == 0 ? 0 : 1) + (dy == 0 ? 0 : 1) + (dz == 0 ? 0 : 1);
                        var nx = x + dx; var ny = y + dy; var nz = z + dz;
                        if (axes == 0 || !AdmitsAxes(axes) || (uint)nx >= (uint)Tuning.Width ||
                            (uint)ny >= (uint)Tuning.Layers || (uint)nz >= (uint)Tuning.Depth) { continue; }
                        var next = Index(nx, ny, nz);
                        // Reverse search: prove predecessor -> settled node, not an assumed directed reverse edge.
                        if (IsTraversableEdge(next, node)) {
                            edges[count++] = new SharedEdge(next, axes == 1 ? StraightCost : axes == 2 ? DiagonalCost : SpaceDiagonalCost);
                        }
                    }
                }
            }
            return count;
        }

        public WorldNavigationSharedCheckpoint CaptureShared() => new(SharedStale ? 0 : m_sharedCursor,
            m_sharedTrees.Select(tree => SharedStale ? new WorldNavigationTreeCheckpoint(-1, 0, [], []) : tree.Capture()).ToArray());

        public void ValidateShared(WorldNavigationSharedCheckpoint checkpoint) {
            if (checkpoint is null || checkpoint.Trees is null || checkpoint.Trees.Length != m_sharedTrees.Length ||
                checkpoint.Cursor < 0 || checkpoint.Cursor >= Math.Max(1, m_sharedTrees.Length)) {
                throw new InvalidOperationException("shared navigation checkpoint scheduler shape differs.");
            }
            var goals = new HashSet<int>();
            var ages = new HashSet<int>();
            foreach (var tree in checkpoint.Trees) {
                if (tree is null || tree.Goal < -1 || tree.Goal >= CellCount || tree.Age < 0 || tree.Age >= m_sharedTrees.Length ||
                    tree.Nodes is null || tree.Nodes.Length > CellCount || tree.Pending is null ||
                    tree.Pending.Length > Math.Min(CellCount, WorldBodiesLimits.CapacityCeiling)) {
                    throw new InvalidOperationException("shared navigation checkpoint tree shape differs.");
                }
                if (tree.Goal < 0) {
                    if (tree.Age != 0 || tree.Nodes.Length != 0 || tree.Pending.Length != 0) {
                        throw new InvalidOperationException("shared navigation checkpoint empty slot carries work.");
                    }
                    continue;
                }
                if (!goals.Add(tree.Goal)) { throw new InvalidOperationException("shared navigation checkpoint repeats a goal."); }
                if (!ages.Add(tree.Age)) { throw new InvalidOperationException("shared navigation checkpoint repeats a recency rank."); }
                var nodes = new Dictionary<int, WorldNavigationTreeNode>(tree.Nodes.Length);
                var previous = -1;
                foreach (var node in tree.Nodes) {
                    if (node.Node <= previous || node.Node >= CellCount || node.Cost < 0 || node.Cost > CellCount * SpaceDiagonalCost) {
                        throw new InvalidOperationException("shared navigation checkpoint has invalid discovered nodes.");
                    }
                    nodes.Add(node.Node, node);
                    previous = node.Node;
                }
                if (!nodes.TryGetValue(tree.Goal, out var root) || root.Cost != 0 || root.Next != -1) {
                    throw new InvalidOperationException("shared navigation checkpoint lacks its zero-cost root.");
                }
                foreach (var node in tree.Nodes) {
                    if (node.Node == tree.Goal) { continue; }
                    if (!nodes.TryGetValue(node.Next, out var next) || !next.Settled || next.Cost >= node.Cost) {
                        throw new InvalidOperationException("shared navigation checkpoint successor must be settled at a lower cost.");
                    }
                    Coordinates(node.Node, out var x, out var y, out var z);
                    Coordinates(next.Node, out var nx, out var ny, out var nz);
                    var dx = nx - x; var dy = ny - y; var dz = nz - z;
                    var axes = (dx == 0 ? 0 : 1) + (dy == 0 ? 0 : 1) + (dz == 0 ? 0 : 1);
                    if (Math.Abs(dx) > 1 || Math.Abs(dy) > 1 || Math.Abs(dz) > 1 ||
                        (m_edges[node.Node] & NeighborBit(dx, dy, dz)) == 0 ||
                        node.Cost - next.Cost != (axes == 1 ? StraightCost : axes == 2 ? DiagonalCost : SpaceDiagonalCost)) {
                        throw new InvalidOperationException("shared navigation checkpoint successor is not a costed static edge.");
                    }
                }
                previous = -1;
                foreach (var pending in tree.Pending) {
                    if (pending <= previous || pending >= CellCount || (nodes.TryGetValue(pending, out var node) && node.Settled)) {
                        throw new InvalidOperationException("shared navigation checkpoint has invalid pending starts.");
                    }
                    previous = pending;
                }
            }
            if (ages.Any(age => age >= goals.Count)) {
                throw new InvalidOperationException("shared navigation checkpoint recency ranks must be contiguous from zero.");
            }
        }

        public void RestoreShared(WorldNavigationSharedCheckpoint checkpoint) {
            m_sharedCursor = checkpoint.Cursor;
            m_sharedFieldRevision = MediumRevision;
            for (var index = 0; index < m_sharedTrees.Length; index++) { m_sharedTrees[index].Restore(checkpoint.Trees[index]); }
            SharedExpandedLast = SharedPathsLast = SharedCapacityRefusalsLast = 0;
        }

        public void AppendSharedHash(ref Fnv1aHash hash) {
            // A changed medium invalidates every old tree before its next use. Canonicalize that dead state now,
            // so checkpointing after a field edit does not require retaining an obsolete copy of the old lattice.
            var stale = SharedStale;
            hash.Add(m_sharedTrees.Length);
            hash.Add(stale ? 0 : m_sharedCursor);
            foreach (var tree in m_sharedTrees) { tree.AppendHash(ref hash, stale); }
        }

        private sealed class SharedTree {
            // Representation granularity, not an author policy. Only changed blocks rehash; a settled tree's
            // digest is O(1) per authoritative hash, independent of the admitted graph capacity.
            private const int HashBlockSize = 64;
            private readonly int[] m_cost;
            private readonly int[] m_next;
            private readonly int[] m_heap;
            private readonly int[] m_position;
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
            private int m_heapCount;
            public int Goal { get; private set; } = -1;
            public int Age { get; set; }
            public int PendingCount { get; private set; }
            // Tick-local reservation, overwritten before body reads; not independent checkpoint/hash state.
            public bool PinnedForStep { get; set; }
            public bool NeedsWork => PendingCount != 0 && m_heapCount != 0;

            public SharedTree(int cells) {
                m_cost = new int[cells]; m_next = new int[cells]; m_heap = new int[cells];
                m_position = new int[cells]; m_stamp = new int[cells]; m_pending = new bool[cells];
                m_pendingList = new int[Math.Min(cells, WorldBodiesLimits.CapacityCeiling)];
                m_pendingOrdered = new int[m_pendingList.Length];
                m_blockHashes = new ulong[HashBlockCount(cells)];
                m_dirtyBlocks = new bool[m_blockHashes.Length];
            }

            public static int HashBlockCount(int cells) => (cells + HashBlockSize - 1) / HashBlockSize;

            public void Reset(int goal) {
                ClearPending();
                if (++m_generation == int.MaxValue) { Array.Clear(m_stamp); m_generation = 1; }
                Goal = goal; Age = 0; m_heapCount = 0; PinnedForStep = false;
                // Every undiscovered block has the same empty digest. Do not scan all cells just because a
                // new goal reused this slot; Open/Restore mark only blocks that acquire discovered nodes.
                Array.Fill(m_blockHashes, Fnv1aHash.Create().Value);
                Array.Clear(m_dirtyBlocks);
                m_nodesDirty = true;
                if (goal >= 0) { Open(goal, 0, -1); }
            }

            public void ClearPending() {
                for (var index = 0; index < m_pendingLength; index++) { m_pending[m_pendingList[index]] = false; }
                m_pendingLength = PendingCount = 0;
                m_pendingDirty = true;
            }

            private bool Settled(int node) => m_stamp[node] == m_generation && m_position[node] == -2;

            private void Queue(int node) {
                if (m_pending[node]) { return; }
                m_pending[node] = true;
                m_pendingList[m_pendingLength++] = node;
                PendingCount++;
                m_pendingDirty = true;
            }

            public WorldNavigationStatus ReadPath(int start, Span<int> path, out int length) {
                length = 0;
                if (!Settled(start)) {
                    if (m_heapCount == 0) { return WorldNavigationStatus.Unreachable; }
                    Queue(start);
                    return WorldNavigationStatus.Pending;
                }
                for (var node = start; node >= 0; node = m_next[node]) {
                    if (length == path.Length) { length = 0; return WorldNavigationStatus.PathLimit; }
                    path[length++] = node;
                }
                return WorldNavigationStatus.Active;
            }

            public bool Expand(Domain domain) {
                if (m_heapCount == 0) { return false; }
                var node = Pop();
                m_position[node] = -2;
                InvalidateNodeHash(node);
                if (m_pending[node]) { m_pending[node] = false; PendingCount--; m_pendingDirty = true; }
                Span<SharedEdge> edges = stackalloc SharedEdge[26];
                var count = domain.Predecessors(node, edges);
                for (var index = 0; index < count; index++) {
                    var edge = edges[index];
                    var cost = checked(m_cost[node] + edge.Cost);
                    if (!Settled(edge.Node) && (m_stamp[edge.Node] != m_generation || cost < m_cost[edge.Node])) {
                        Open(edge.Node, cost, node);
                    }
                }
                return true;
            }

            private int Compare(int left, int right) {
                var cost = m_cost[left].CompareTo(m_cost[right]);
                return cost != 0 ? cost : left.CompareTo(right);
            }

            private void Open(int node, int cost, int next) {
                m_cost[node] = cost; m_next[node] = next;
                InvalidateNodeHash(node);
                if (m_stamp[node] != m_generation) {
                    m_stamp[node] = m_generation; m_position[node] = m_heapCount; m_heap[m_heapCount++] = node;
                }
                var index = m_position[node];
                while (index > 0) {
                    var parent = (index - 1) / 2;
                    if (Compare(m_heap[parent], node) <= 0) { break; }
                    Swap(index, parent); index = parent;
                }
            }

            private int Pop() {
                var result = m_heap[0];
                var last = m_heap[--m_heapCount];
                if (m_heapCount == 0) { return result; }
                m_heap[0] = last; m_position[last] = 0;
                var index = 0;
                while (true) {
                    var left = index * 2 + 1;
                    if (left >= m_heapCount) { break; }
                    var right = left + 1;
                    var best = right < m_heapCount && Compare(m_heap[right], m_heap[left]) < 0 ? right : left;
                    if (Compare(m_heap[index], m_heap[best]) <= 0) { break; }
                    Swap(index, best); index = best;
                }
                return result;
            }

            private void Swap(int a, int b) {
                (m_heap[a], m_heap[b]) = (m_heap[b], m_heap[a]);
                m_position[m_heap[a]] = a; m_position[m_heap[b]] = b;
            }

            public WorldNavigationTreeCheckpoint Capture() {
                var nodes = new List<WorldNavigationTreeNode>();
                var pending = new List<int>();
                if (Goal >= 0) {
                    for (var node = 0; node < m_stamp.Length; node++) {
                        if (m_stamp[node] == m_generation) { nodes.Add(new(node, m_cost[node], m_next[node], Settled(node))); }
                        if (m_pending[node]) { pending.Add(node); }
                    }
                }
                return new(Goal, Age, nodes.ToArray(), pending.ToArray());
            }

            public void Restore(WorldNavigationTreeCheckpoint state) {
                Reset(-1);
                Goal = state.Goal; Age = state.Age;
                foreach (var node in state.Nodes) {
                    if (node.Settled) {
                        m_stamp[node.Node] = m_generation; m_position[node.Node] = -2;
                        m_cost[node.Node] = node.Cost; m_next[node.Node] = node.Next;
                        InvalidateNodeHash(node.Node);
                    } else { Open(node.Node, node.Cost, node.Next); }
                }
                foreach (var node in state.Pending) { Queue(node); }
            }

            public void AppendHash(ref Fnv1aHash hash, bool empty) {
                hash.Add(empty ? -1 : Goal); hash.Add(empty ? 0 : Age);
                if (empty || Goal < 0) { return; }
                if (m_nodesDirty) {
                    var nodesHash = Fnv1aHash.Create();
                    nodesHash.Add(m_stamp.Length);
                    for (var block = 0; block < m_blockHashes.Length; block++) {
                        if (m_dirtyBlocks[block]) {
                            var blockHash = Fnv1aHash.Create();
                            var end = Math.Min((block + 1) * HashBlockSize, m_stamp.Length);
                            for (var node = block * HashBlockSize; node < end; node++) {
                                if (m_stamp[node] != m_generation) { continue; }
                                blockHash.Add(node); blockHash.Add(m_cost[node]); blockHash.Add(m_next[node]);
                                blockHash.Add((byte)(Settled(node) ? 1 : 0));
                            }
                            m_blockHashes[block] = blockHash.Value;
                            m_dirtyBlocks[block] = false;
                        }
                        nodesHash.Add(m_blockHashes[block]);
                    }
                    m_nodesHash = nodesHash.Value;
                    m_nodesDirty = false;
                }
                if (m_pendingDirty) {
                    var count = 0;
                    for (var index = 0; index < m_pendingLength; index++) {
                        var node = m_pendingList[index];
                        if (m_pending[node]) { m_pendingOrdered[count++] = node; }
                    }
                    var pending = m_pendingOrdered.AsSpan(0, count);
                    pending.Sort();
                    var pendingHash = Fnv1aHash.Create();
                    pendingHash.Add(count);
                    foreach (var node in pending) { pendingHash.Add(node); }
                    m_pendingHash = pendingHash.Value;
                    m_pendingDirty = false;
                }
                hash.Add(m_nodesHash);
                hash.Add(m_pendingHash);
            }

            private void InvalidateNodeHash(int node) {
                m_dirtyBlocks[node / HashBlockSize] = true;
                m_nodesDirty = true;
            }
        }
    }
}
