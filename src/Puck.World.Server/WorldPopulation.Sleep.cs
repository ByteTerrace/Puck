using Puck.Maths;

namespace Puck.World.Server;

public sealed partial class WorldPopulation {
    // Derived work sets. Slot order is the authority order, independent of insertion or wake order. They are
    // rebuilt by ordinary activation during restore. Only canonical frozen-component membership is checkpointed;
    // work lists, spatial bounds and adjacency objects are derived.
    private readonly BodyWorkSet m_awakeBodies;
    private readonly BodyWorkSet m_sleepingBodies;
    private readonly BodyWorkSet m_eventBodies;

    private readonly Dictionary<int, SortedSet<int>> m_rigidNeighbours = [];
    private readonly List<int> m_island = [];
    private readonly HashSet<int> m_islandVisited = [];

    private readonly int[] m_stepBodies;
    private readonly int[] m_eventBodyImage;
    private readonly int[] m_sleepQueries;

    private readonly SleepingIndex m_sleepIndex = new();
    private bool m_sleepIndexDirty = true;

    private bool m_wakingIsland;
    private ulong m_sleepFieldVersion;

    /// <summary>Number of rigid bodies parked outside the tick's integration and contact work sets.</summary>
    public int SleepingRigidBodies => m_sleepingBodies.Count;
    /// <summary>Number of simulated bodies visited by the integration pass this tick.</summary>
    public int SimulatedBodyVisits { get; private set; }

    private void BodyEntryChanged(int index) {
        WakeBodyIsland(index: index);
        m_eventBodies.Add(item: index);
        if (m_entries[index] is { Active: true, Body: not null }) { m_awakeBodies.Add(item: index); } else { m_awakeBodies.Remove(item: index); }
    }
    private void BodyWoke(int index) {
        WakeBodyIsland(index: index);
        m_eventBodies.Add(item: index);
        if (m_entries[index] is { Active: true, Body: not null }) { m_awakeBodies.Add(item: index); }
    }
    private void WakeBodyIsland(int index) {
        if (m_wakingIsland || !m_sleepingBodies.Contains(item: index)) { return; }
        m_wakingIsland = true;
        m_island.Clear();
        m_islandVisited.Clear();
        m_island.Add(item: index);
        m_islandVisited.Add(item: index);
        for (var cursor = 0; (cursor < m_island.Count); cursor++) {
            var member = m_island[cursor];

            if (m_rigidNeighbours.TryGetValue(key: member, value: out var neighbours)) {
                foreach (var neighbour in neighbours) {
                    if (m_islandVisited.Add(item: neighbour)) { m_island.Add(item: neighbour); }
                }
            }
        }
        m_island.Sort();
        foreach (var member in m_island) {
            if (m_sleepingBodies.Remove(item: member)) {
                m_sleepIndexDirty = true;
                if (m_entries[member] is { Active: true, Body: { } body }) {
                    body.WakeRigidIsland();
                    m_awakeBodies.Add(item: member);
                    m_eventBodies.Add(item: member);
                }
            }
        }
        // Every edge of the woken component will be derived again from actual contacts this tick.
        foreach (var member in m_island) { m_rigidNeighbours.Remove(key: member); }
        m_wakingIsland = false;
    }
    private void LinkRigidBodies(int left, int right) {
        if (!m_rigidNeighbours.TryGetValue(key: left, value: out var a)) { m_rigidNeighbours[left] = a = []; }
        if (!m_rigidNeighbours.TryGetValue(key: right, value: out var b)) { m_rigidNeighbours[right] = b = []; }
        a.Add(item: right);
        b.Add(item: left);
    }
    private ReadOnlySpan<int> PrepareBodyStep() {
        if (m_sleepFieldVersion != ContactFieldVersion) {
            while (m_sleepingBodies.Count > 0) { WakeBodyIsland(index: m_sleepingBodies.Min); }
            m_sleepFieldVersion = ContactFieldVersion;
        }
        m_awakeBodies.CopyTo(array: m_stepBodies);
        foreach (var index in m_awakeBodies) { m_eventBodies.Add(item: index); }
        foreach (var index in m_awakeBodies) { m_rigidNeighbours.Remove(key: index); }
        return m_stepBodies.AsSpan(start: 0, length: m_awakeBodies.Count);
    }
    // Freeze a complete resting contact component together. A supported stack cannot freeze above a body that
    // is still moving, and changing any member wakes the component before its support graph is discarded.
    private void ParkRigidIslands() {
        m_islandVisited.Clear();
        m_awakeBodies.CopyTo(array: m_stepBodies);
        var count = m_awakeBodies.Count;

        for (var ordinal = 0; (ordinal < count); ordinal++) {
            var root = m_stepBodies[ordinal];

            if (m_entries[root].Body is not { IsRigid: true }) { continue; }
            if (!m_islandVisited.Add(item: root)) { continue; }
            m_island.Clear();
            m_island.Add(item: root);
            var resting = true;

            for (var cursor = 0; (cursor < m_island.Count); cursor++) {
                var member = m_island[cursor];

                resting &= (m_entries[member] is { Active: true, Body: { IsRigid: true, Resting: true, CarriedBy: null } });
                if (m_rigidNeighbours.TryGetValue(key: member, value: out var neighbours)) {
                    foreach (var neighbour in neighbours) {
                        if (m_islandVisited.Add(item: neighbour)) { m_island.Add(item: neighbour); }
                    }
                }
            }
            if (!resting) { continue; }
            m_island.Sort();
            var rootPlusOne = (m_island[0] + 1);

            foreach (var member in m_island) {
                m_entries[member].Body!.ParkRigidIsland(contactFieldVersion: m_sleepFieldVersion, rootPlusOne: rootPlusOne);
                m_awakeBodies.Remove(item: member);
                m_sleepingBodies.Add(item: member);
            }
            m_sleepIndexDirty = true;
        }
    }
    // Only frozen component membership is continuation state. Rebuild the index and an equivalent star graph
    // from its canonical roots; adjacency within a frozen component matters only when the whole component wakes.
    private void RestoreSleepingIslands() {
        m_rigidNeighbours.Clear();
        m_sleepFieldVersion = ContactFieldVersion;
        var pendingFieldWake = false;

        foreach (var index in m_awakeBodies.ToArray()) {
            var body = m_entries[index].Body!;
            var residue = body.CaptureIntegrationResidue();
            var root = (residue.RigidSleepRootPlusOne - 1);

            if (root < 0) { continue; }
            if (root != index) { LinkRigidBodies(left: root, right: index); }
            body.ParkRigidIsland(rootPlusOne: (root + 1), contactFieldVersion: residue.LastContactFieldVersion);
            pendingFieldWake |= (residue.LastContactFieldVersion != ContactFieldVersion);
            m_awakeBodies.Remove(item: index);
            m_sleepingBodies.Add(item: index);
        }
        if (pendingFieldWake) { m_sleepFieldVersion = unchecked((ContactFieldVersion - 1UL)); }
        m_sleepIndexDirty = true;
    }

    internal ReadOnlySpan<int> EventBodies() {
        m_eventBodies.CopyTo(array: m_eventBodyImage);
        return m_eventBodyImage.AsSpan(start: 0, length: m_eventBodies.Count);
    }
    internal void CompleteBodyEvents() {
        // Keep awake entries between ticks. Only the final dirty delivery of a newly frozen/deactivated slot leaves
        // the set; bodies that stay awake need no per-tick membership changes.
        foreach (var index in EventBodies()) {
            if (!m_awakeBodies.Contains(item: index)) { m_eventBodies.Remove(item: index); }
        }
    }
    internal int NearbySleepingBodies(WorldBody body, int[] destination) {
        if (m_sleepIndexDirty) {
            m_sleepIndex.Rebuild(population: this, sleeping: m_sleepingBodies);
            m_sleepIndexDirty = false;
        }
        return m_sleepIndex.Query(body: body, destination: destination);
    }

    // A retained broadphase over frozen bounds. Only island membership changes rebuild it. Queries visit cached
    // bounds in neighbouring Y/Z cells, using binary X ranges; no sleeping body's object is read on an idle tick.
    private sealed class SleepingIndex {
        private readonly Dictionary<(long Y, long Z), List<(WorldSweepBody Bounds, FixedVector3 Position)>> m_cells = [];

        private long m_width = 2;
        private long m_maximumRadius;

        internal void Rebuild(WorldPopulation population, BodyWorkSet sleeping) {
            m_cells.Clear();
            m_maximumRadius = 0;
            foreach (var index in sleeping) { m_maximumRadius = Math.Max(val1: m_maximumRadius, val2: population.m_entries[index].Body!.BroadphaseRadius.Value); }
            m_width = Math.Max(val1: 2, val2: m_maximumRadius.AddSaturating(right: m_maximumRadius));
            foreach (var index in sleeping) {
                var body = population.m_entries[index].Body!;
                var position = body.FixedPosition;
                var radius = body.BroadphaseRadius;
                var key = (position.Y.Value.FloorDivide(divisor: m_width), position.Z.Value.FloorDivide(divisor: m_width));

                if (!m_cells.TryGetValue(key: key, value: out var cell)) { m_cells[key] = cell = []; }
                cell.Add(item: (new WorldSweepBody(Index: index, MinimumX: (position.X - radius), MaximumX: (position.X + radius), Radius: radius), position));
            }
            foreach (var cell in m_cells.Values) { cell.Sort(comparison: static (a, b) => a.Bounds.CompareTo(other: b.Bounds)); }
        }
        internal int Query(WorldBody body, int[] destination) {
            if (m_cells.Count == 0) { return 0; }
            var position = body.FixedPosition;
            var radius = body.BroadphaseRadius;
            var reach = radius.Value.AddSaturating(right: m_maximumRadius);
            var firstY = position.Y.Value.SubtractSaturating(right: reach).FloorDivide(divisor: m_width);
            var lastY = position.Y.Value.AddSaturating(right: reach).FloorDivide(divisor: m_width);
            var firstZ = position.Z.Value.SubtractSaturating(right: reach).FloorDivide(divisor: m_width);
            var lastZ = position.Z.Value.AddSaturating(right: reach).FloorDivide(divisor: m_width);
            var minimum = (position.X.Value.SubtractSaturating(right: radius.Value).SubtractSaturating(right: m_width));
            var maximum = position.X.Value.AddSaturating(right: radius.Value);
            var count = 0;
            // A large live body must not enumerate an unbounded empty lattice. The occupied-cell fallback has
            // the same exact candidates and subsequent slot/X sorting removes dictionary enumeration order.
            var range = (((((Int128)lastY) - firstY) + 1) * ((((Int128)lastZ) - firstZ) + 1));

            if (range > m_cells.Count) {
                foreach (var pair in m_cells) {
                    if ((pair.Key.Y >= firstY) && (pair.Key.Y <= lastY) && (pair.Key.Z >= firstZ) && (pair.Key.Z <= lastZ)) { Visit(cell: pair.Value); }
                }
            } else {
                for (var y = firstY; (y <= lastY); y++) {
                    for (var z = firstZ; (z <= lastZ); z++) {
                        if (m_cells.TryGetValue(key: (y, z), value: out var cell)) { Visit(cell: cell); }
                    }
                }
            }
            return count;

            void Visit(List<(WorldSweepBody Bounds, FixedVector3 Position)> cell) {
                var low = 0;
                var high = cell.Count;

                while (low < high) {
                    var middle = (low + ((high - low) / 2));

                    if (cell[middle].Bounds.MinimumX.Value < minimum) { low = (middle + 1); } else { high = middle; }
                }
                for (var ordinal = low; ((ordinal < cell.Count) && (cell[ordinal].Bounds.MinimumX.Value <= maximum)); ordinal++) {
                    var candidate = cell[ordinal];
                    var sum = (radius + candidate.Bounds.Radius);

                    if ((candidate.Bounds.MaximumX.Value >= position.X.Value.SubtractSaturating(right: radius.Value)) &&
                        (FixedQ4816.Abs(value: (candidate.Position.Y - position.Y)) <= sum) && (FixedQ4816.Abs(value: (candidate.Position.Z - position.Z)) <= sum)) {
                        destination[count++] = candidate.Bounds.Index;
                    }
                }
            }
        }
    }
    // Membership mutates only on admission, wake and sleep. Pay for ordered insertion there, rather than a tree
    // enumerator's traversal stack on every tick. Iteration borrows the array; callers that can wake bodies copy it.
    private sealed class BodyWorkSet(int capacity) {
        private readonly int[] m_indices = new int[capacity];
        private readonly bool[] m_present = new bool[capacity];

        internal int Count { get; private set; }
        internal int Min => m_indices[0];

        internal bool Contains(int item) => m_present[item];
        internal bool Add(int item) {
            if (m_present[item]) { return false; }
            var position = ~Array.BinarySearch(array: m_indices, index: 0, length: Count, value: item);

            Array.Copy(sourceArray: m_indices, sourceIndex: position, destinationArray: m_indices, destinationIndex: (position + 1), length: (Count - position));
            m_indices[position] = item;
            m_present[item] = true;
            Count++;
            return true;
        }
        internal bool Remove(int item) {
            if (!m_present[item]) { return false; }
            var position = Array.BinarySearch(array: m_indices, index: 0, length: Count, value: item);

            Count--;
            Array.Copy(sourceArray: m_indices, sourceIndex: (position + 1), destinationArray: m_indices, destinationIndex: position, length: (Count - position));
            m_present[item] = false;
            return true;
        }
        internal void CopyTo(int[] array) => m_indices.AsSpan(start: 0, length: Count).CopyTo(destination: array);
        internal int[] ToArray() => m_indices.AsSpan(start: 0, length: Count).ToArray();

        public ReadOnlySpan<int>.Enumerator GetEnumerator() => ((ReadOnlySpan<int>)m_indices.AsSpan(start: 0, length: Count)).GetEnumerator();
    }
}
